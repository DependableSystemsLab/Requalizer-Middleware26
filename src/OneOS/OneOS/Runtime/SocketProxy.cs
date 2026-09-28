using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.Extensions.Logging;
using OneOS.Common;

namespace OneOS.Runtime
{
    // Cluster-wide sockets, tier-0 (plan step 8): this runtime listens on every claimed port
    // (Registry.Sockets, not IsIndependent) on its proxy addresses, and relays each connection to the owner's
    // socket on its hosting runtime's loopback address: directly when the owner is here, otherwise through a
    // tunnel (SocketConnectRequest) to that runtime, one per client connection.
    //
    // Proxies bind specific addresses, never the wildcard: on Linux a wildcard listener can't share a port
    // with the owner's listener on the loopback address (and vice versa), while a specific one can.
    public sealed class SocketProxy : IAsyncDisposable
    {
        private const int BufferSize = 65536;
        // After the client stops sending, how long the server's side may still answer through a tunnel (TLS
        // carries no half-close, so the tunnel closes as a whole).
        private static readonly TimeSpan TunnelDrain = TimeSpan.FromSeconds(10);

        private readonly Runtime _runtime;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<(string Address, int Port), (TcpListener Listener, CancellationTokenSource Cts)> _listeners = new();
        private readonly ConcurrentDictionary<(string Address, int Port), DateTime> _bindFailures = new();
        private readonly SemaphoreSlim _reconciling = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private Timer? _timer;

        public SocketProxy(Runtime runtime, ILogger<SocketProxy> logger)
        {
            _runtime = runtime;
            _logger = logger;
        }

        // The proxy endpoints currently listening, for tests and diagnostics.
        public IReadOnlyCollection<(string Address, int Port)> Listening => _listeners.Keys.ToList();

        public void Start()
        {
            _runtime.OnRegistryUpdated += OnRegistryUpdated;
            // Also periodically: bind failures (a port another process holds) are retried, and new
            // interface addresses picked up.
            _timer = new Timer(_ => _ = ReconcileAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        }

        private void OnRegistryUpdated(RegistryAction action)
        {
            if (action is ClaimSocketAction or ReleaseSocketAction or DeleteAgentAction or TransactionAction) _ = ReconcileAsync();
        }

        public IReadOnlyList<string> ProxyAddresses =>
            _runtime.Config.ProxyAddresses is { Count: > 0 } configured ? configured
            : NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString()).Append(IPAddress.Loopback.ToString()).Distinct().ToList();

        public async Task ReconcileAsync()
        {
            if (!await _reconciling.WaitAsync(0)) return;   // one at a time; the timer comes round again
            try
            {
                var ports = _runtime.Registry.Sockets.Values.Where(s => !s.IsIndependent).Select(s => s.Port).ToHashSet();
                var wanted = ProxyAddresses.SelectMany(a => ports.Select(p => (a, p))).ToHashSet();
                foreach (var key in _listeners.Keys.Where(k => !wanted.Contains(k)).ToList())
                    if (_listeners.TryRemove(key, out var gone))
                    {
                        gone.Cts.Cancel();
                        gone.Listener.Stop();
                        _logger.LogInformation("Socket proxy {Address}:{Port} closed", key.Address, key.Port);
                    }
                foreach (var key in wanted.Where(k => !_listeners.ContainsKey(k)))
                {
                    try
                    {
                        var listener = new TcpListener(IPAddress.Parse(key.a), key.p);
                        listener.Start();
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        _listeners[key] = (listener, cts);
                        _bindFailures.TryRemove(key, out _);
                        _ = AcceptAsync(listener, key.p, cts.Token);
                        _logger.LogInformation("Socket proxy listening on {Address}:{Port}", key.a, key.p);
                    }
                    catch (SocketException ex)
                    {
                        // Another process holds it (on a shared machine, possibly another runtime's proxy).
                        if (_bindFailures.TryAdd(key, DateTime.UtcNow))
                            _logger.LogWarning("Socket proxy can't listen on {Address}:{Port} ({Error}); retrying", key.a, key.p, ex.SocketErrorCode);
                    }
                }
            }
            finally { _reconciling.Release(); }
        }

        private async Task AcceptAsync(TcpListener listener, int port, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => HandleClientAsync(client, port));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        private async Task HandleClientAsync(TcpClient client, int port)
        {
            using (client)
            {
                if (!_runtime.Registry.Sockets.TryGetValue(port, out var socket)) return;
                try
                {
                    if (socket.HostRuntime == _runtime.Config.ID)
                    {
                        using var upstream = new TcpClient();
                        await upstream.ConnectAsync(IPAddress.Parse(_runtime.Config.LoopbackAddress), port);
                        await RelayAsync(client, upstream);
                    }
                    else
                    {
                        var tunnel = await _runtime.ConnectSocketTunnelAsync(socket.HostRuntime, port);
                        await RelayAsync(client.GetStream(), client.Client, tunnel);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Socket proxy {Port}: connection to {Owner} on {Host} failed: {Error}", port, socket.Owner, socket.HostRuntime, ex.Message);
                }
            }
        }

        // The hosting runtime's end of a tunnel: connect to the owner's socket, then relay.
        public async Task AcceptTunnelAsync(SocketConnectRequest request, TcpSocket tunnel)
        {
            TcpClient? upstream = null;
            bool ok = _runtime.Registry.Sockets.TryGetValue(request.Port, out var socket) && socket.HostRuntime == _runtime.Config.ID;
            if (ok)
            {
                try
                {
                    upstream = new TcpClient();
                    await upstream.ConnectAsync(IPAddress.Parse(_runtime.Config.LoopbackAddress), request.Port);
                }
                catch (SocketException)
                {
                    upstream.Dispose();
                    upstream = null;
                    ok = false;
                }
            }
            await tunnel.Send(MessagePackSerializer.Serialize<RuntimeMessage>(new RawPipeResponse { MessageId = request.MessageId, SenderId = _runtime.Config.ID, Accepted = ok }));
            if (upstream == null) { await tunnel.StopAsync(); return; }
            using (upstream) await RelayAsync(upstream.GetStream(), upstream.Client, tunnel);
        }

        // Two TCP connections on this host: each direction ends with a half-close of the other side.
        private static async Task RelayAsync(TcpClient a, TcpClient b)
        {
            await Task.WhenAll(Pump(a.GetStream(), b.Client), Pump(b.GetStream(), a.Client));

            static async Task Pump(NetworkStream from, System.Net.Sockets.Socket to)
            {
                var buffer = new byte[BufferSize];
                try
                {
                    int n;
                    while ((n = await from.ReadAsync(buffer)) > 0) await to.SendAsync(buffer.AsMemory(0, n), SocketFlags.None);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
                try { to.Shutdown(SocketShutdown.Send); } catch (Exception) { }
            }
        }

        // A TCP connection and a tunnel (TLS, which carries no half-close): when the tunnel ends, the TCP side
        // is half-closed; when the TCP side stops sending, the tunnel closes once the far side has finished or
        // TunnelDrain has passed.
        private static async Task RelayAsync(NetworkStream local, System.Net.Sockets.Socket localSocket, TcpSocket tunnel)
        {
            var tunnelEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tunnel.OnEnded += _ =>
            {
                try { localSocket.Shutdown(SocketShutdown.Send); } catch (Exception) { }
                tunnelEnded.TrySetResult();
            };
            var reading = tunnel.ListenRaw(chunk =>
            {
                try { local.Write(chunk, 0, chunk.Length); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { tunnelEnded.TrySetResult(); }
            });
            var buffer = new byte[BufferSize];
            try
            {
                int n;
                while ((n = await local.ReadAsync(buffer)) > 0) await tunnel.SendRaw(buffer[..n]);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
            await Task.WhenAny(tunnelEnded.Task, reading, Task.Delay(TunnelDrain));
            await tunnel.StopAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _runtime.OnRegistryUpdated -= OnRegistryUpdated;
            if (_timer != null) await _timer.DisposeAsync();
            _cts.Cancel();
            foreach (var (_, (listener, cts)) in _listeners) { cts.Cancel(); listener.Stop(); }
            _listeners.Clear();
        }
    }
}
