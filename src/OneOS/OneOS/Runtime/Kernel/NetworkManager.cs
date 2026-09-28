using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OneOS.Runtime.Kernel
{
    // Why a claim failed; the JavaScript environment reports it as the Node.js error code.
    public sealed class SocketClaimException : Exception
    {
        public SocketClaimException(string code, string message) : base(message) { Code = code; }
        public string Code { get; }
    }

    // Cluster-wide sockets, tier-1 (plan step 8): a user component's listening port is claimed in the
    // Registry, so it is unique across the cluster; every runtime then proxies it (SocketProxy, tier-0).
    public sealed class NetworkManager
    {
        // listen(0): a free port from this range.
        public const int EphemeralFrom = 41000, EphemeralTo = 42000;

        private readonly Runtime _runtime;
        private readonly ILogger _logger;

        public NetworkManager(Runtime runtime, ILogger<NetworkManager> logger)
        {
            _runtime = runtime;
            _logger = logger;
        }

        // Ports the runtimes themselves use: each runtime's port and its Raft port (port + 100).
        public IReadOnlySet<int> ReservedPorts
        {
            get
            {
                var ports = new[] { _runtime.Config.Port }.Concat(_runtime.Config.Peers.Values.Select(p => p.Port)).ToList();
                return ports.Concat(ports.Select(p => p + 100)).ToHashSet();
            }
        }

        // Claims `port` (0: any free one) for `owner`, an agent on this runtime. Returns the port. Throws
        // SocketClaimException with EADDRINUSE when another agent holds it or a runtime uses it.
        public async Task<int> ClaimAsync(int port, string owner, bool independent = false, CancellationToken ct = default)
        {
            if (port < 0 || port > 65535) throw new SocketClaimException("ERR_SOCKET_BAD_PORT", $"port {port} is out of range");
            if (port == 0) port = FreePort();
            if (ReservedPorts.Contains(port))
                throw new SocketClaimException("EADDRINUSE", $"port {port} is used by the OneOS runtimes");
            if (_runtime.Registry.Sockets.TryGetValue(port, out var held) && held.Owner != owner)
                throw new SocketClaimException("EADDRINUSE", $"port {port} is already claimed by {held.Owner}");

            var info = new SocketInfo { Owner = owner, Port = port, HostRuntime = _runtime.Config.ID, IsIndependent = independent };
            if (!await UpdateReliablyAsync(new ClaimSocketAction { Info = info }, ct))
                throw new SocketClaimException("EAGAIN", $"the Registry could not be updated to claim port {port}");
            // The claim is decided when it is applied; this runtime's replica shows the outcome.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (_runtime.Registry.Sockets.TryGetValue(port, out var now))
                {
                    if (now.Owner == owner && now.HostRuntime == info.HostRuntime) { _logger.LogInformation("Socket {Port} claimed by {Owner}", port, owner); return port; }
                    if (now.Owner != owner) throw new SocketClaimException("EADDRINUSE", $"port {port} is already claimed by {now.Owner}");
                }
                await Task.Delay(20, ct);
            }
            throw new SocketClaimException("EAGAIN", $"the claim of port {port} was not applied in time");
        }

        public async Task ReleaseAsync(int port, string owner, CancellationToken ct = default)
        {
            if (_runtime.Registry.Sockets.TryGetValue(port, out var held) && held.Owner == owner && held.HostRuntime == _runtime.Config.ID)
                await UpdateReliablyAsync(new ReleaseSocketAction { Port = port, Owner = owner, HostRuntime = _runtime.Config.ID }, ct);
        }

        private int FreePort()
        {
            var taken = _runtime.Registry.Sockets.Keys.ToHashSet();
            var reserved = ReservedPorts;
            var free = Enumerable.Range(EphemeralFrom, EphemeralTo - EphemeralFrom).Where(p => !taken.Contains(p) && !reserved.Contains(p)).ToList();
            if (free.Count == 0) throw new SocketClaimException("EADDRINUSE", "no free port in the ephemeral range");
            return free[Random.Shared.Next(free.Count)];
        }

        // A follower may briefly know no leader (election): retry.
        private async Task<bool> UpdateReliablyAsync(RegistryAction action, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                try { if (await _runtime.UpdateRegistryAsync(action, ct)) return true; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger.LogDebug("Registry update {Action} failed: {Error}", action.GetType().Name, ex.Message); }
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, 100 * (attempt + 1))), ct);
            }
            return false;
        }
    }
}
