using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Common;
using System.Security.Cryptography.X509Certificates;

namespace OneOS.Runtime;

/// <summary>
/// Manages TCP connections for the Runtime: accepts incoming connections
/// and establishes outbound connections to configured peers.
/// </summary>
public sealed class ConnectionManager
{
    private readonly Configuration _config;
    private readonly ILogger _logger;
    private readonly Func<TcpSocket, CancellationToken, Task>? _onConnected;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    /// <summary>
    /// Outbound connections initiated by this node. Keyed by <c>"host:port"</c>.
    /// </summary>
    public ConcurrentDictionary<string, TcpSocket> ActiveConnections { get; } = new();

    /// <summary>
    /// Inbound connections accepted from remote peers. Keyed by remote endpoint string.
    /// </summary>
    public ConcurrentDictionary<string, TcpSocket> PassiveConnections { get; } = new();

    /// <summary>
    /// Initializes a new <see cref="ConnectionManager"/>.
    /// </summary>
    /// <param name="config">The runtime configuration.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="onConnected">
    /// Async callback invoked when a new inbound connection is accepted.
    /// If <c>null</c>, accepted connections are tracked but no handler is called.
    /// </param>
    public ConnectionManager(
        Configuration config,
        ILogger logger,
        Func<TcpSocket, CancellationToken, Task>? onConnected = null)
    {
        _config = config;
        _logger = logger;
        _onConnected = onConnected;
    }

    /// <summary>
    /// Starts the TCP listener and begins accepting incoming connections.
    /// </summary>
    public Task Start(CancellationToken ct = default)
    {
        if (_acceptTask is not null)
            throw new InvalidOperationException("ConnectionManager already started.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _cts.Token;

        var endpoint = IPAddress.TryParse(_config.Host, out var address)
            ? new IPEndPoint(address, _config.Port)
            : new IPEndPoint(IPAddress.Any, _config.Port);

        _listener = new TcpListener(endpoint);
        _listener.Start(backlog: 100);

        _logger.LogInformation("ConnectionManager listening securely on {Endpoint}", endpoint);

        _acceptTask = Task.Run(async () =>
        {
            try
            {
                while (!linkedCt.IsCancellationRequested)
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync(linkedCt).ConfigureAwait(false);
                    var remoteEndpoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";

                    _logger.LogDebug("Accepted connection from {RemoteEndpoint}, initiating TLS handshake...", remoteEndpoint);

                    var socket = new SecureServerSideSocket(tcpClient, _logger, $"Passive connection from {remoteEndpoint}");
                    
                    try
                    {
                        var fullCertPath = System.IO.Path.Combine(_config.MountPath, _config.CertificatePath);
                        using var cert = X509CertificateLoader.LoadPkcs12FromFile(fullCertPath, "");
                        await socket.AuthenticateAsync(cert, linkedCt).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to authenticate passive connection from {RemoteEndpoint}", remoteEndpoint);
                        tcpClient.Dispose();
                        continue;
                    }

                    PassiveConnections[remoteEndpoint] = socket;
                    socket.OnEnded += ex =>
                    {
                        PassiveConnections.TryRemove(remoteEndpoint, out _);
                        if (ex is not null)
                            _logger.LogDebug(ex, "Passive connection {RemoteEndpoint} closed with exception", remoteEndpoint);
                    };

                    if (_onConnected is not null)
                    {
                        // Fire-and-forget the handler — don't block the accept loop.
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _onConnected(socket, linkedCt).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Connection handler threw for {RemoteEndpoint}", remoteEndpoint);
                            }
                        }, linkedCt);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown
            }
            catch (ObjectDisposedException)
            {
                // Listener was stopped
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Accept loop terminated unexpectedly");
            }
        }, linkedCt);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Connects to a peer at the specified <c>"host:port"</c> address with retry logic.
    /// </summary>
    /// <returns>The connected socket, or <c>null</c> if all connection attempts failed.</returns>
    public async Task<TcpSocket?> ConnectTo(string hostPort, string expectedCertHash, int retries = 5, CancellationToken ct = default)
    {
        var parts = hostPort.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
        {
            _logger.LogError("Invalid peer address format: '{HostPort}'. Expected 'host:port'", hostPort);
            return null;
        }

        var host = parts[0];

        try
        {
            var fullCertPath = System.IO.Path.Combine(_config.MountPath, _config.CertificatePath);
            using var localCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(fullCertPath, "");
            var socket = new SecureClientSideSocket(host, port, host, expectedCertHash, localCert, _logger, $"Active connection to {hostPort}");
            await socket.ConnectAsync(retries, ct).ConfigureAwait(false);

            ActiveConnections[hostPort] = socket;
            socket.OnEnded += ex =>
            {
                ActiveConnections.TryRemove(hostPort, out _);
                if (ex is not null)
                    _logger.LogDebug(ex, "Active connection to {HostPort} closed with exception", hostPort);
            };

            _logger.LogInformation("Connected securely to peer {HostPort}", hostPort);
            return socket;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to securely connect to peer {HostPort} after {Retries} retries", hostPort, retries);
            return null;
        }
    }

    /// <summary>
    /// Stops the listener and closes all active and passive connections.
    /// </summary>
    public async Task StopAsync()
    {
        _logger.LogInformation("ConnectionManager stopping...");

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _listener?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exception stopping TCP listener");
        }

        // Close all connections
        var stopTasks = new List<Task>();

        foreach (var (key, socket) in ActiveConnections)
        {
            stopTasks.Add(socket.StopAsync());
            ActiveConnections.TryRemove(key, out _);
        }

        foreach (var (key, socket) in PassiveConnections)
        {
            stopTasks.Add(socket.StopAsync());
            PassiveConnections.TryRemove(key, out _);
        }

        await Task.WhenAll(stopTasks).ConfigureAwait(false);

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Accept task ended with exception during stop");
            }
        }

        _cts?.Dispose();
        _cts = null;
        _acceptTask = null;

        _logger.LogInformation("ConnectionManager stopped");
    }
}
