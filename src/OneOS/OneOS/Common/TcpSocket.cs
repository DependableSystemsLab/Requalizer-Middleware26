using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OneOS.Common;

/// <summary>
/// Base class for TCP-backed sockets. Uses composition over a <see cref="Socket"/>
/// for length-prefixed framing and a <see cref="TcpClient"/> for the transport.
/// </summary>
public class TcpSocket
{
    protected readonly Socket _socket;

    public TcpClient? TcpClient { get; protected set; }

    /// <summary>The local endpoint of the underlying TCP connection.</summary>
    public EndPoint? LocalEndPoint => TcpClient?.Client.LocalEndPoint;

    /// <summary>The remote endpoint of the underlying TCP connection.</summary>
    public EndPoint? RemoteEndPoint => TcpClient?.Client.RemoteEndPoint;

    /// <summary>
    /// Raised when the listen loop ends (normally or due to error).
    /// </summary>
    public event Action<Exception?>? OnEnded
    {
        add => _socket.OnEnded += value;
        remove => _socket.OnEnded -= value;
    }

    public string UsageContext
    {
        get => _socket.UsageContext;
        set => _socket.UsageContext = value;
    }

    public TcpSocket(ILogger logger, string usageContext = "Unknown")
    {
        _socket = new Socket(logger, usageContext);
    }

    /// <summary>Sets the stream on the inner socket (used after deferred connection or SSL wrapping).</summary>
    protected void SetStream(Stream stream) => _socket.Stream = stream;

    public Task Listen(Action<byte[]> onMessage) => _socket.Listen(onMessage);

    public void HandOver() => _socket.HandOver();

    public Task ListenRaw(Action<byte[]> onMessage) => _socket.ListenRaw(onMessage);

    public void StopListen() => _socket.StopListen();

    public Task Send(byte[] payload) => _socket.Send(payload);

    public Task SendRaw(byte[] payload) => _socket.SendRaw(payload);

    public byte[] Receive() => _socket.Receive();
    public Task<byte[]> ReceiveAsync(CancellationToken ct = default) => _socket.ReceiveAsync(ct);

    public virtual string? RemoteCertificateHash => null;

    public override string ToString() =>
        $"{GetType().Name} {LocalEndPoint} -> {RemoteEndPoint}";

    /// <summary>
    /// Stops the socket and closes the underlying TCP connection.
    /// </summary>
    public virtual async Task StopAsync()
    {
        await _socket.StopAsync().ConfigureAwait(false);
        TcpClient?.Close();
    }

    /// <summary>
    /// Stops the listen loop without closing the connection.
    /// </summary>
    public Task StopListeningAsync() => _socket.StopListeningAsync();
}

/// <summary>
/// A socket accepted from an incoming TCP connection (server side).
/// </summary>
public class ServerSideSocket : TcpSocket
{
    public ServerSideSocket(TcpClient tcpClient, ILogger logger, string usageContext = "Unknown") : base(logger, usageContext)
    {
        TcpClient = tcpClient;
        SetStream(tcpClient.GetStream());
    }
}

/// <summary>
/// An outbound TCP connection to a remote peer (client side).
/// Supports connection with configurable retry logic.
/// </summary>
public class ClientSideSocket : TcpSocket
{
    protected readonly string _host;
    protected readonly int _port;
    protected readonly ILogger _logger;

    public ClientSideSocket(string host, int port, ILogger logger, string usageContext = "Unknown") : base(logger, usageContext)
    {
        _host = host;
        _port = port;
        _logger = logger;

        // Enforce IPv4 for Linux compatibility.
        TcpClient = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            TcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to set KeepAlive on ClientSideSocket");
        }
    }

    /// <summary>
    /// Attempts to connect with exponential backoff retries.
    /// </summary>
    public virtual async Task ConnectAsync(int retries = 5, CancellationToken ct = default)
    {
        Exception? lastException = null;
        int delayMs = 1000;

        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                await TcpClient!.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
                SetStream(TcpClient.GetStream());
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogDebug(
                    "Connection to {Host}:{Port} failed (attempt {Attempt}/{Retries}): {Message}",
                    _host, _port, attempt + 1, retries + 1, ex.Message);

                if (attempt < retries)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    delayMs = Math.Min(delayMs * 2, 10_000); // Cap at 10s

                    TcpClient?.Dispose();
                    TcpClient = new TcpClient(AddressFamily.InterNetwork);
                    try
                    {
                        TcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    }
                    catch (Exception e)
                    {
                        _logger.LogDebug(e, "Failed to set KeepAlive on retried ClientSideSocket");
                    }
                }
            }
        }

        throw new IOException(
            $"Failed to connect to {_host}:{_port} after {retries + 1} attempts.",
            lastException);
    }
}

/// <summary>
/// A secure server-side socket that wraps the TCP stream in TLS using SslStream.
/// </summary>
public class SecureServerSideSocket : TcpSocket
{
    private SslStream? _sslStream;

    public SecureServerSideSocket(TcpClient tcpClient, ILogger logger, string usageContext = "Unknown") : base(logger, usageContext)
    {
        try
        {
            tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to set KeepAlive on SecureServerSideSocket");
        }
        TcpClient = tcpClient;
    }

    public override string? RemoteCertificateHash => _sslStream?.RemoteCertificate?.GetCertHashString();

    /// <summary>
    /// Authenticates the server connection using the provided certificate.
    /// Must be called before Listen() or Send().
    /// </summary>
    public async Task AuthenticateAsync(X509Certificate certificate, CancellationToken ct = default)
    {
        _sslStream = new SslStream(TcpClient!.GetStream(), false, (sender, cert, chain, errors) => true);
        await _sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            ClientCertificateRequired = true,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
        }, ct).ConfigureAwait(false);
        
        SetStream(_sslStream);
    }
}

/// <summary>
/// A secure client-side socket that wraps the outbound TCP stream in TLS using SslStream.
/// </summary>
public class SecureClientSideSocket : ClientSideSocket
{
    private readonly string _targetHost;
    private readonly string _expectedCertHash;
    private readonly X509Certificate? _localCert;
    private SslStream? _sslStream;

    public SecureClientSideSocket(string host, int port, string targetHost, string expectedCertHash, X509Certificate? localCert, ILogger logger, string usageContext = "Unknown") 
        : base(host, port, logger, usageContext)
    {
        _targetHost = targetHost;
        _expectedCertHash = expectedCertHash ?? string.Empty;
        _localCert = localCert;
    }

    public override string? RemoteCertificateHash => _sslStream?.RemoteCertificate?.GetCertHashString();

    public override async Task ConnectAsync(int retries = 5, CancellationToken ct = default)
    {
        // Establish the underlying TCP connection first
        await base.ConnectAsync(retries, ct).ConfigureAwait(false);

        // Wrap the stream in TLS
        _sslStream = new SslStream(TcpClient!.GetStream(), false, 
            (sender, certificate, chain, sslPolicyErrors) => 
            {
                if (certificate == null) return false;

                // If a hash is explicitly expected, enforce it strictly.
                if (!string.IsNullOrEmpty(_expectedCertHash))
                {
                    string actualHash = certificate.GetCertHashString();
                    if (!string.Equals(actualHash, _expectedCertHash, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError("Certificate hash mismatch! Expected {Expected}, but got {Actual}", _expectedCertHash, actualHash);
                        return false;
                    }
                }
                else
                {
                    if (sslPolicyErrors != SslPolicyErrors.None)
                    {
                        _logger.LogWarning("Accepting certificate with SSL Policy Errors: {Errors}", sslPolicyErrors);
                    }
                }

                return true;
            });

        var options = new SslClientAuthenticationOptions
        {
            TargetHost = _targetHost,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
        };

        if (_localCert != null)
        {
            options.ClientCertificates = new X509CertificateCollection { _localCert };
        }

        await _sslStream.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);

        // Overwrite the stream with the secure stream
        SetStream(_sslStream);
    }
}
