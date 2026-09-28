using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MessagePack;
using OneOS.Common;
using System.Collections.Concurrent;
using OneOS.Runtime.Kernel;

namespace OneOS.Runtime
{
    public class Runtime : IClusterProvider
    {
        private readonly Configuration _config;
        private readonly ILogger<Runtime> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ConcurrentDictionary<string, PeerRuntime> _peers = new();
        private readonly Synchronizer _synchronizer;    // tier 0 component
        public ConnectionManager ConnectionManager { get; }  // tier 0 component
        private readonly ExecutionManager _executionManager;    // tier 0 component
        private readonly SessionManager _sessionManager;    // tier 1 component
        private readonly FileSystemManager _fileSystemManager;  // tier 1 component
        private readonly ProcessManager _processManager;    // tier 1 component

        private readonly ConcurrentDictionary<int, Router> _routers = new();

        public event Action<RegistryAction>? OnRegistryUpdated;

        public Router GetRouter(int linkIndex)
        {
            return _routers.GetOrAdd(linkIndex, idx => new Router(this, _loggerFactory, idx));
        }

        public Router KernelRouter => GetRouter(0);

        internal bool IsLeader { get => _synchronizer.Fsm.CurrentState == SynchronizerState.Leader; }
        
        public Registry Registry => _synchronizer.Registry;
        public Configuration Config => _config;
        public ExecutionManager ExecutionManager => _executionManager;

        // Always-on measurements of this runtime's pipes and graph latencies (tier-0, local).
        public Monitoring.RuntimeMetrics Metrics { get; } = new Monitoring.RuntimeMetrics();

        // Set before StartAsync to write profiles to disk (`oneos start --profile`).
        public Monitoring.ProfileOptions? Profile { get; set; }
        private Monitoring.Profiler? _profiler;
        public string? ProfileDirectory => _profiler?.RunDirectory;
        public SessionManager SessionManager => _sessionManager;
        public FileSystemManager FileSystemManager => _fileSystemManager;
        public ProcessManager ProcessManager => _processManager;
        private readonly GraphManager _graphManager;    // tier 1 component
        private readonly Kernel.NetworkManager _networkManager;    // tier 1 component
        private readonly SocketProxy _socketProxy;    // tier 0 component
        private readonly EventHub _eventHub;          // tier 0 component: browser clients' subscriptions
        private readonly Kernel.IOManager _ioManager;    // tier 1 component
        public GraphManager GraphManager => _graphManager;
        // Cluster-wide sockets (plan step 8): claims (tier-1) and this runtime's proxies (tier-0).
        public Kernel.NetworkManager NetworkManager => _networkManager;
        public SocketProxy SocketProxy => _socketProxy;
        public EventHub EventHub => _eventHub;
        internal Synchronizer Synchronizer => _synchronizer;
        // I/O devices (IODriver): this runtime's drivers and access to devices anywhere.
        public Kernel.IOManager IOManager => _ioManager;

        public Runtime(Configuration config, ILoggerFactory loggerFactory)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _logger = _loggerFactory.CreateLogger<Runtime>();

            _synchronizer = new Synchronizer(_config, _loggerFactory);
            _synchronizer.Fsm.StateChanged += OnSynchronizerStateChanged;
            _synchronizer.OnFollowerJoined += peerId => _logger.LogInformation("Operational layer notified: Node {NodeId} joined the cluster.", peerId);
            _synchronizer.OnFollowerDropped += droneId => _logger.LogWarning("Operational layer notified: Node {NodeId} dropped from the cluster.", droneId);
            _executionManager = new ExecutionManager(this, loggerFactory);
            _sessionManager = new SessionManager(this, loggerFactory.CreateLogger<SessionManager>());
            _fileSystemManager = new FileSystemManager(this, loggerFactory.CreateLogger<FileSystemManager>());
            _processManager = new ProcessManager(this, loggerFactory.CreateLogger<ProcessManager>());
            _graphManager = new GraphManager(this, loggerFactory.CreateLogger<GraphManager>());
            _networkManager = new Kernel.NetworkManager(this, loggerFactory.CreateLogger<Kernel.NetworkManager>());
            _socketProxy = new SocketProxy(this, loggerFactory.CreateLogger<SocketProxy>());
            _eventHub = new EventHub(this, loggerFactory.CreateLogger<EventHub>());
            _ioManager = new Kernel.IOManager(this, loggerFactory.CreateLogger<Kernel.IOManager>());

            _synchronizer.OnRegistryUpdated += action => 
            {
                void HandleAction(RegistryAction a)
                {
                    _logger.LogInformation("Registry updated by action: {ActionType}", a.GetType().Name);
                    try
                    {
                        if (a is SetAgentAction setAgent)
                        {
                            _ = _executionManager.HandleAgentUpdateAsync(setAgent);
                        }
                        else if (a is DeleteAgentAction deleteAgent)
                        {
                            _ = _executionManager.HandleAgentDeleteAsync(deleteAgent);
                        }
                        else if (a is TransactionAction tx)
                        {
                            foreach (var childAction in tx.Actions)
                            {
                                HandleAction(childAction);
                            }
                        }
                        _executionManager.HandleGraphAction(a);
                        OnRegistryUpdated?.Invoke(a);    
                    }
                    catch (Exception ex)
                    {
                        _logger.LogCritical($"Error while handling Registry Update!\n{ex}");
                    }
                }

                HandleAction(action);
            };

            _synchronizer.OnRegistryHydrated += () =>
            {
                try
                {
                    _logger.LogInformation("Registry hydrated. Synchronizing ExecutionManager.");
                    _ = _executionManager.SynchronizeWithRegistry();
                }
                catch (Exception ex)
                {
                    _logger.LogCritical($"Error while synchronizing with Registry!\n{ex}");
                }
            };

            ConnectionManager = new ConnectionManager(
                _config,
                loggerFactory.CreateLogger<ConnectionManager>(),
                HandleIncomingConnectionAsync
            );
        }

        private void OnSynchronizerStateChanged(object? sender, StateChangedEventArgs<SynchronizerState, SynchronizerTrigger> e)
        {
            _logger.LogInformation("Synchronizer State Changed: {From} -> {To} (Trigger: {Trigger})", e.PreviousState, e.CurrentState, e.Trigger);

            if (e.CurrentState == SynchronizerState.Leader && e.PreviousState != SynchronizerState.Leader)
            {
                _logger.LogInformation("This node has become the LEADER. Initiating leader-specific workflows...");
                // TODO: Start leader workflows
            }
            else if (e.PreviousState == SynchronizerState.Leader && e.CurrentState != SynchronizerState.Leader)
            {
                _logger.LogInformation("This node is no longer the LEADER. Halting leader-specific workflows...");
                // TODO: Stop leader workflows
            }
        }

        // This runtime's host description, as it reports it in cluster-info exchanges.
        public HostInfo DescribeHost() => new HostInfo
        {
            Id = Config.ID,
            Name = Config.ID,
            Zone = Config.Zone,
            Tags = new List<string>(Config.Tags),
            Label = Config.Label,
            CpuMillis = (long)Math.Max(1, Config.Cores.Length > 0 ? Config.Cores[0] : 1) * 1000,
            MemoryBytes = Config.Memory * 1024 * 1024,
            Executables = Config.VMs.Count == 0 ? null : Config.VMs.GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First().Bin),
            // Every node program runs instrumented through the JavaScript environment (oneos.js), the IDM's
            // injection point for JavaScript: a runtime with it installed supports the IDM for `node`.
            IdmSupport = Config.IdmSupport.Concat(Driver.JavaScriptEnvironmentInstaller.IsInstalled(Config.TempPath) ? new[] { "node" } : Array.Empty<string>()).Distinct().ToList(),
            MaxAgents = Config.MaxAgents,
        };

        // --- Cluster-info exchange (control plane; see ClusterInfoRequest) ---

        private volatile ClusterState? _clusterState;
        private readonly object _exchangeLock = new();
        private Task<ClusterState>? _exchange;

        // The latest cluster state this runtime has seen, from an exchange it initiated or took part in
        // (null before the first one). Its CollectedAt is the time the initiator gathered it.
        public ClusterState? LastKnownClusterState => _clusterState;

        // How long an initiator waits for each peer's answer; a peer that doesn't answer in time is left
        // out of the round (its hosts can't be scheduled on until the next exchange).
        public TimeSpan ClusterInfoTimeout { get; set; } = TimeSpan.FromSeconds(2);

        // Runs a cluster-info exchange with this runtime as the initiator and returns the new state.
        // Called on demand, just before scheduling; concurrent callers share the exchange in flight.
        public Task<ClusterState> RefreshClusterStateAsync(CancellationToken ct = default)
        {
            lock (_exchangeLock)
            {
                if (_exchange is { IsCompleted: false }) return _exchange;
                return _exchange = ExchangeClusterInfoAsync(ct);
            }
        }

        private async Task<ClusterState> ExchangeClusterInfoAsync(CancellationToken ct)
        {
            var round = Guid.NewGuid();
            var peers = Config.Peers.Keys.Where(id => id != Config.ID).OrderBy(id => id, StringComparer.Ordinal).ToList();

            // 1–2. Ask every peer for its host description.
            var answers = await Task.WhenAll(peers.Select(async id =>
            {
                if (!_peers.TryGetValue(id, out var peer) || !peer.IsHealthy) return (Id: id, Info: (HostInfo?)null);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(ClusterInfoTimeout);
                    var request = new ClusterInfoRequest { MessageId = Guid.NewGuid(), SenderId = Config.ID, RoundId = round };
                    var response = await peer.RequestAsync(request, 0, timeout.Token).ConfigureAwait(false);
                    return (Id: id, Info: (response as ClusterInfoResponse)?.Info);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.IO.IOException)
                {
                    _logger.LogDebug("Cluster info from {Peer} unavailable: {Error}", id, ex.Message);
                    return (Id: id, Info: (HostInfo?)null);
                }
            })).ConfigureAwait(false);

            // 3. Aggregate, adopt, and send the result to every peer.
            var state = new ClusterState
            {
                RoundId = round,
                InitiatorId = Config.ID,
                CollectedAt = DateTime.UtcNow,
                Hosts = new[] { DescribeHost() }.Concat(answers.Where(a => a.Info != null).Select(a => a.Info!)).ToList(),
                Unreachable = answers.Where(a => a.Info == null).Select(a => a.Id).ToList(),
            };
            AdoptClusterState(state);
            foreach (var id in peers.Where(id => !state.Unreachable.Contains(id)))
                if (_peers.TryGetValue(id, out var peer))
                    try { await peer.SendAsync(new ClusterInfoUpdate { MessageId = Guid.NewGuid(), SenderId = Config.ID, State = state }, 0, ct).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogDebug("Cluster info update to {Peer} failed: {Error}", id, ex.Message); }
            if (state.Unreachable.Count > 0)
                _logger.LogInformation("Cluster info exchange {Round}: no answer from {Peers}", round, string.Join(", ", state.Unreachable));
            return state;
        }

        // Every configured peer's resource sample (EventHub "metrics"): one answer per peer, Unreachable when it
        // isn't connected or doesn't answer within `timeout`.
        public async Task<List<ResourceMetricsEvent>> RequestPeerResourceMetricsAsync(TimeSpan timeout, CancellationToken ct)
        {
            var peers = Config.Peers.Keys.Where(id => id != Config.ID).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var answers = await Task.WhenAll(peers.Select(async id =>
            {
                var unreachable = new ResourceMetricsEvent { MessageId = Guid.NewGuid(), SenderId = Config.ID, Runtime = id, Time = DateTime.UtcNow, Unreachable = true };
                if (!_peers.TryGetValue(id, out var peer) || !peer.IsHealthy) return unreachable;
                try
                {
                    using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    bounded.CancelAfter(timeout);
                    var response = await peer.RequestAsync(new ResourceMetricsRequest { MessageId = Guid.NewGuid(), SenderId = Config.ID }, 0, bounded.Token).ConfigureAwait(false);
                    return response as ResourceMetricsEvent ?? unreachable;
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.IO.IOException)
                {
                    return unreachable;
                }
            })).ConfigureAwait(false);
            return answers.ToList();
        }

        // Keeps the most recently collected state (exchanges from different initiators may overlap).
        private void AdoptClusterState(ClusterState state)
        {
            lock (_exchangeLock)
                if (_clusterState == null || state.CollectedAt >= _clusterState.CollectedAt) _clusterState = state;
        }

        // A snapshot for the scheduler (scheduling-spec §2.2). Host descriptions come from the last known
        // cluster state (this runtime's own is always current); liveness is the peer link's health. A
        // configured peer with no known description is listed with no capacity, so nothing is placed on
        // it. Committed resources are the reservations of deployed graph instances (Registry). Network
        // measurements are not tracked yet.
        public ClusterSnapshot TakeSnapshot()
        {
            var entries = (_clusterState?.Hosts ?? new List<HostInfo>()).GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.Last());
            entries[Config.ID] = DescribeHost();
            var ids = entries.Keys.Concat(Config.Peers.Keys).Distinct().OrderBy(id => id, StringComparer.Ordinal);

            var hosts = new List<HostRuntimeInfo>();
            foreach (var id in ids)
            {
                bool alive = id == Config.ID || (_peers.TryGetValue(id, out var peer) && peer.IsHealthy);
                var committed = Registry.Graphs.Values.Where(g => g.State != "Failed")
                    .Select(g => g.Committed.TryGetValue(id, out var c) ? new ResourceVector(c[0], c[1]) : ResourceVector.Zero)
                    .Aggregate(ResourceVector.Zero, (acc, c) => acc.Add(c));
                if (entries.TryGetValue(id, out var h))
                    hosts.Add(new HostRuntimeInfo(h.Id, h.Name, h.Zone, h.Tags, h.Label, alive,
                        new ResourceVector(h.CpuMillis, h.MemoryBytes), committed,
                        h.Executables, h.IdmSupport, h.MaxAgents, Registry.HostVersion(id)));
                else
                    hosts.Add(new HostRuntimeInfo(id, id, null, new List<string>(), null, alive,
                        ResourceVector.Zero, ResourceVector.Zero, null, new List<string>(), null, Registry.HostVersion(id)));
            }
            return new ClusterSnapshot(hosts.Sum(h => h.Version), hosts, new List<NetworkQuality>());
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            _logger.LogInformation("Starting OneOS Runtime for node '{Id}' on {Host}:{Port}...", 
                _config.ID, _config.Host, _config.Port);

            // The JavaScript environment (oneos.js) JavaScript agents run in: the library is refreshed from
            // this build; its npm dependencies are installed at config time (`oneos config`).
            try
            {
                Driver.JavaScriptEnvironmentInstaller.WriteLibrary(_config.TempPath);
                if (!Driver.JavaScriptEnvironmentInstaller.DependenciesInstalled(_config.TempPath))
                    _logger.LogWarning("The JavaScript environment's npm dependencies are missing in {TempPath}; JavaScript agents can't start until `oneos config` installs them", _config.TempPath);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not write the JavaScript environment to {TempPath}", _config.TempPath); }
            
            // 1. Start the Connection Manager (binds TCP listener)
            await ConnectionManager.Start(ct).ConfigureAwait(false);

            // 2. Connect to configured peers
            await ConnectToPeersAsync(ct).ConfigureAwait(false);

            // 3. Start Kernel Agents
            KernelRouter.RegisterLocalAgent(_sessionManager);
            await _sessionManager.StartAsync(ct).ConfigureAwait(false);

            KernelRouter.RegisterLocalAgent(_fileSystemManager);
            await _fileSystemManager.StartAsync(ct).ConfigureAwait(false);

            KernelRouter.RegisterLocalAgent(_processManager);
            await _processManager.StartAsync(ct).ConfigureAwait(false);

            KernelRouter.RegisterLocalAgent(_graphManager);
            await _graphManager.StartAsync(ct).ConfigureAwait(false);

            if (Profile != null)
            {
                _profiler = new Monitoring.Profiler(Config.ID, Profile, Metrics,
                    () => _executionManager.ProcessAgents.Select(a => (a.URI, a.ProcessId)).Where(x => x.ProcessId is int).Select(x => (x.URI, x.ProcessId!.Value)),
                    msg => _logger.LogInformation("{Message}", msg));
                _profiler.Start();
            }

            var volumeDriver = new Driver.VolumeDriver(this, _loggerFactory);
            KernelRouter.RegisterLocalAgent(volumeDriver);
            await volumeDriver.StartAsync(ct).ConfigureAwait(false);

            // I/O devices this host exposes to the cluster (Configuration.IO).
            KernelRouter.RegisterLocalAgent(_ioManager);
            await _ioManager.StartAsync(ct).ConfigureAwait(false);
            foreach (var io in _config.IO)
            {
                Driver.IODriver? driver;
                try { driver = Driver.IODriver.Create(this, io, _loggerFactory); }
                catch (Exception ex) { _logger.LogError("I/O device {Name}: {Error}", io.Name, ex.Message); continue; }
                if (driver == null) { _logger.LogWarning("I/O device {Name}: unknown driver '{Driver}'", io.Name, io.Driver); continue; }
                KernelRouter.RegisterLocalAgent(driver);
                await driver.StartAsync(ct).ConfigureAwait(false);
                _ioManager.AddLocalDriver(driver);
            }

            // 4. Start the Raft Synchronizer
            await _synchronizer.StartAsync(ct).ConfigureAwait(false);
            await _executionManager.StartAsync(ct).ConfigureAwait(false);
            _socketProxy.Start();
            _eventHub.Start();
            _ = _ioManager.PublishLocalDevicesAsync(ct);

            _logger.LogInformation("Runtime [{Id}] is Ready", _config.ID);
        }

        public async Task StopAsync()
        {
            _logger.LogInformation("Stopping OneOS Runtime...");
            if (_profiler != null) await _profiler.DisposeAsync().ConfigureAwait(false);   // last sample while agents still run
            await _graphManager.StopAsync().ConfigureAwait(false);
            await _socketProxy.DisposeAsync().ConfigureAwait(false);
            await _eventHub.DisposeAsync().ConfigureAwait(false);
            foreach (var driver in _ioManager.LocalDrivers)
                try { await driver.StopAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogWarning(ex, "Stopping {Driver} failed", driver.URI); }
            await _executionManager.StopAsync().ConfigureAwait(false);
            await _sessionManager.StopAsync().ConfigureAwait(false);
            await ConnectionManager.StopAsync().ConfigureAwait(false);
            await _synchronizer.StopAsync().ConfigureAwait(false);
            _synchronizer.Dispose();
            _logger.LogInformation("Runtime stopped.\n\n\n");
        }

        private async Task ConnectToPeersAsync(CancellationToken ct)
        {
            if (_config.Peers == null || _config.Peers.Count == 0)
            {
                _logger.LogInformation("No peers configured for [{Id}]", _config.ID);
                return;
            }

            _logger.LogInformation("Connecting to {Count} configured peer(s)...", _config.Peers.Count);

            foreach (var kvp in _config.Peers)
            {
                var peerId = kvp.Key;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 1. Establish Control Plane Link (LinkIndex = 0)
                        await EstablishLinkAsync(peerId, 0, ct);
                        
                        // 2. Establish Data Plane Link (LinkIndex = 1)
                        await ProvisionUserspaceLink(peerId, 1, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to connect to peer {PeerId}", peerId);
                    }
                });
            }
        }

        private async Task EstablishLinkAsync(string peerId, int linkIndex, CancellationToken ct = default)
        {
            if (!_config.Peers.TryGetValue(peerId, out var peerInfo)) return;

            var peerRuntime = GetOrAddPeerRuntime(peerId);
            await peerRuntime.ConnectionSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (peerRuntime.ActiveSockets.ContainsKey(linkIndex))
                {
                    _logger.LogInformation("Active socket to {PeerId} on LinkIndex {LinkIndex} is already established.", peerId, linkIndex);
                    return;
                }

                _logger.LogDebug("Connecting to peer {PeerId} at {Address} for LinkIndex {LinkIndex}...", peerId, peerInfo.Address, linkIndex);
            
                var socket = await ConnectionManager.ConnectTo(peerInfo.Address, peerInfo.CertificateHash, retries: 3, ct: ct).ConfigureAwait(false);
                
                if (socket != null)
                {
                    socket.UsageContext = $"Active handshake to {peerId} (Link {linkIndex})";
                    _logger.LogInformation("Successfully connected to peer {PeerId} on LinkIndex {LinkIndex}", peerId, linkIndex);
                    
                    var req = new HandshakeRequest
                    {
                        MessageId = Guid.NewGuid(),
                        SenderId = _config.ID,
                        NodeId = _config.ID,
                        ClusterDomain = _config.Domain,
                        LinkIndex = linkIndex
                    };
                    
                    var tcs = new TaskCompletionSource<RuntimeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                    peerRuntime.RegisterPendingRequest(req.MessageId, tcs);
                    
                    var payload = MessagePackSerializer.Serialize<RuntimeMessage>(req);
                    await socket.Send(payload);
                    
                    peerRuntime.AttachActiveSocket(socket, linkIndex);
                    
                    socket.OnEnded += ex => 
                    {
                        if (peerRuntime.ActiveSockets.TryGetValue(linkIndex, out var active) && active == socket)
                        {
                            peerRuntime.DetachActiveSocket(linkIndex);
                        }
                    };

                    try 
                    {
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        linkedCts.CancelAfter(TimeSpan.FromSeconds(10));
                        await using (linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token)))
                        {
                            var res = await tcs.Task;
                            if (res is HandshakeResponse hr)
                            {
                                _logger.LogInformation("Successfully received HandshakeResponse from {PeerId} for LinkIndex {LinkIndex}. Accepted: {Accepted}", peerId, linkIndex, hr.Accepted);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to complete handshake with {PeerId} on LinkIndex {LinkIndex}", peerId, linkIndex);
                    }
                }
            }
            finally
            {
                peerRuntime.ConnectionSemaphore.Release();
            }
        }

        public Task ProvisionUserspaceLink(string peerId, int linkIndex, CancellationToken ct = default)
        {
            return EstablishLinkAsync(peerId, linkIndex, ct);
        }

        private async Task ReconnectPeerAsync(string peerId, int linkIndex)
        {
            _logger.LogInformation("Initiating reconnection to {PeerId} on LinkIndex {LinkIndex}...", peerId, linkIndex);
            await EstablishLinkAsync(peerId, linkIndex);
        }

        private PeerRuntime GetOrAddPeerRuntime(string peerId)
        {
            return _peers.GetOrAdd(peerId, id =>
            {
                var p = new PeerRuntime(id, _loggerFactory.CreateLogger<PeerRuntime>())
                {
                    RequestHandler = HandleRequestAsync
                };

                // Create remote agents for kernel components that bypass Registry
                var sessionAgentUri = $"{id}.{Config.Domain}/SessionManager";
                var sessionAgent = new RemoteAgent(sessionAgentUri, async msg =>
                {
                    if (msg is RuntimeMessage rMsg) await p.SendAsync(rMsg, 0);
                }, _loggerFactory.CreateLogger<RemoteAgent>());
                KernelRouter.RegisterRemoteAgent(sessionAgent);
                _ = sessionAgent.StartAsync();
                
                var fsAgentUri = $"{id}.{Config.Domain}/FileSystemManager";
                var fsAgent = new RemoteAgent(fsAgentUri, async msg =>
                {
                    if (msg is RuntimeMessage rMsg) await p.SendAsync(rMsg, 0);
                }, _loggerFactory.CreateLogger<RemoteAgent>());
                KernelRouter.RegisterRemoteAgent(fsAgent);
                _ = fsAgent.StartAsync();

                var psAgentUri = $"{id}.{Config.Domain}/ProcessManager";
                var psAgent = new RemoteAgent(psAgentUri, async msg =>
                {
                    if (msg is RuntimeMessage rMsg) await p.SendAsync(rMsg, 0);
                }, _loggerFactory.CreateLogger<RemoteAgent>());
                KernelRouter.RegisterRemoteAgent(psAgent);
                _ = psAgent.StartAsync();

                var volumeAgentUri = $"{id}.{Config.Domain}/volume";
                var volumeAgent = new RemoteAgent(volumeAgentUri, async msg =>
                {
                    if (msg is RuntimeMessage rMsg) await p.SendAsync(rMsg, 0);
                }, _loggerFactory.CreateLogger<RemoteAgent>());
                KernelRouter.RegisterRemoteAgent(volumeAgent);
                _ = volumeAgent.StartAsync(); // Need to start the RunLoopAsync!

                // I/O devices are reached through the peer's IOManager, which serves its local drivers.
                var ioAgent = new RemoteAgent($"{id}.{Config.Domain}/IOManager", async msg =>
                {
                    if (msg is RuntimeMessage rMsg) await p.SendAsync(rMsg, 0);
                }, _loggerFactory.CreateLogger<RemoteAgent>());
                KernelRouter.RegisterRemoteAgent(ioAgent);
                _ = ioAgent.StartAsync();

                return p;
            });
        }

        private Task HandleIncomingConnectionAsync(TcpSocket socket, CancellationToken ct)
        {
            PeerRuntime? peerRuntime = null;
            int connectionLinkIndex = 0;

            _ = socket.Listen(async frame =>
            {
                try
                {
                    var message = MessagePackSerializer.Deserialize<RuntimeMessage>(frame);

                    if (peerRuntime == null)
                    {
                        // Wait for the first message (handshake or terminal)
                        if (message is HandshakeRequest req)
                        {
                            if (_config.Peers.TryGetValue(req.SenderId, out var peerConfig))
                            {
                                var expectedHash = peerConfig.CertificateHash;
                                if (!string.IsNullOrEmpty(expectedHash) && 
                                    !string.Equals(socket.RemoteCertificateHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                                {
                                    _logger.LogWarning("Certificate hash mismatch for peer {SenderId}. Expected {Expected}, got {Actual}", req.SenderId, expectedHash, socket.RemoteCertificateHash);
                                    await socket.StopAsync();
                                    return;
                                }
                            }

                            connectionLinkIndex = req.LinkIndex;
                            peerRuntime = GetOrAddPeerRuntime(req.SenderId);
                            socket.UsageContext = $"Passive handshake from {req.SenderId} (Link {connectionLinkIndex})";
                            peerRuntime.AttachPassiveSocket(socket, connectionLinkIndex);
                            socket.OnEnded += ex => 
                            {
                                if (peerRuntime.PassiveSockets.TryGetValue(connectionLinkIndex, out var passive) && passive == socket)
                                {
                                    _logger.LogInformation("Passive connection from {SenderId} dropped for LinkIndex {LinkIndex}. Invalidating sockets.", req.SenderId, connectionLinkIndex);
                                    peerRuntime.DetachPassiveSocket(connectionLinkIndex);
                                    
                                    if (peerRuntime.ActiveSockets.TryGetValue(connectionLinkIndex, out var active) && active != null)
                                    {
                                        _ = active.StopAsync();
                                        peerRuntime.DetachActiveSocket(connectionLinkIndex);
                                    }
                                }
                            };
                            
                            peerRuntime.ActiveSockets.TryGetValue(connectionLinkIndex, out var activeSocket);

                            if (activeSocket == null)
                            {
                                _logger.LogInformation("Active socket for {SenderId} LinkIndex {LinkIndex} is missing. Initiating reconnection...", req.SenderId, connectionLinkIndex);
                                _ = ReconnectPeerAsync(req.SenderId, connectionLinkIndex);
                            }
                            
                            _logger.LogInformation("Received HandshakeRequest from {SenderId} for Domain {Domain} on LinkIndex {LinkIndex}", req.SenderId, req.ClusterDomain, connectionLinkIndex);
                            var acceptRes = new HandshakeResponse { MessageId = req.MessageId, SenderId = _config.ID, Accepted = true };
                            
                            // Send response via ActiveSocket to follow one-way topology rules.
                            _ = Task.Run(async () => 
                            {
                                int retries = 100; // 5 seconds max wait
                                while (retries > 0)
                                {
                                    if (peerRuntime.ActiveSockets.ContainsKey(connectionLinkIndex)) break;
                                    await Task.Delay(50);
                                    retries--;
                                }

                                if (peerRuntime.ActiveSockets.ContainsKey(connectionLinkIndex))
                                {
                                    await peerRuntime.SendAsync(acceptRes, connectionLinkIndex);
                                }
                                else
                                {
                                    _logger.LogWarning("Timeout waiting for ActiveSocket to {SenderId} on LinkIndex {LinkIndex}. Dropping HandshakeResponse.", req.SenderId, connectionLinkIndex);
                                }
                            });
                        }
                        else if (message is TerminalConnectRequest termReq)
                        {
                            socket.UsageContext = $"Terminal connection for user {termReq.Username}";
                            // The connection becomes a raw byte stream: stop the framed reader now, before it
                            // reads again, so no read is still pending when the shell's raw pipe starts.
                            socket.HandOver();
                            await HandleTerminalConnectRequestAsync(termReq, socket);
                        }
                        else if (message is BrowserConnectRequest browserReq)
                        {
                            socket.UsageContext = $"Browser client for user {browserReq.Username}";
                            socket.HandOver();   // the EventHub reads this connection's frames from now on
                            await _eventHub.AcceptAsync(browserReq, socket);
                        }
                        else if (message is FileDownloadRequest downloadReq)
                        {
                            socket.UsageContext = $"download {downloadReq.Kind} {downloadReq.Path} by {downloadReq.Username}";
                            socket.HandOver();   // a file's bytes go out raw after the first response
                            await Kernel.FileDownload.HandleAsync(this, downloadReq, socket, _logger);
                        }
                        else if (message is FileUploadRequest uploadReq)
                        {
                            socket.UsageContext = $"upload {uploadReq.Kind} {uploadReq.Path} by {uploadReq.Username}";
                            socket.HandOver();   // a file's bytes follow raw (see the terminal case)
                            await Kernel.FileUpload.HandleAsync(this, uploadReq, socket, _logger);
                        }
                        else if (message is SocketConnectRequest socketReq)
                        {
                            socket.UsageContext = $"socket {socketReq.Port} tunnel from {socketReq.SenderId}";
                            socket.HandOver();   // the tunnel takes the stream over (see the terminal case)
                            // Only peers of this cluster may open tunnels into its user components.
                            if (!_config.Peers.TryGetValue(socketReq.SenderId, out var tunnelPeer)
                                || (!string.IsNullOrEmpty(tunnelPeer.CertificateHash) && !string.Equals(socket.RemoteCertificateHash, tunnelPeer.CertificateHash, StringComparison.OrdinalIgnoreCase)))
                            {
                                _logger.LogWarning("Socket tunnel request from unknown or unverified peer {SenderId} refused", socketReq.SenderId);
                                await socket.StopAsync();
                                return;
                            }
                            await _socketProxy.AcceptTunnelAsync(socketReq, socket);
                        }
                        else if (message is RawPipeRequest pipeReq)
                        {
                            socket.UsageContext = $"{pipeReq.Kind} pipe {pipeReq.PipeId} from {pipeReq.SenderId}";
                            socket.HandOver();   // the pipe's end takes the stream over (see the terminal case)
                            if (_config.Peers.TryGetValue(pipeReq.SenderId, out var peerConfig))
                            {
                                var expectedHash = peerConfig.CertificateHash;
                                if (!string.IsNullOrEmpty(expectedHash) && 
                                    !string.Equals(socket.RemoteCertificateHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                                {
                                    _logger.LogWarning("Certificate hash mismatch for peer {SenderId}. Expected {Expected}, got {Actual}", pipeReq.SenderId, expectedHash, socket.RemoteCertificateHash);
                                    await socket.StopAsync();
                                    return;
                                }
                            }

                            _logger.LogInformation("Received RawPipeRequest {PipeId} from {SenderId}", pipeReq.PipeId, pipeReq.SenderId);
                            var acceptRes = new RawPipeResponse { MessageId = pipeReq.MessageId, SenderId = _config.ID, Accepted = true };
                            await socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(acceptRes));
                            _executionManager.FulfillPipe(pipeReq.PipeId, socket);
                        }
                        else
                        {
                            _logger.LogWarning("First message was not HandshakeRequest, TerminalConnectRequest, or RawPipeRequest (it was {MessageType}). Dropping connection from {RemoteEndpoint}", message.GetType().Name, socket.RemoteEndPoint);
                            _ = socket.StopAsync();
                        }
                    }
                    else
                    {
                        peerRuntime.HandleIncomingMessage(message, connectionLinkIndex);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process incoming message from {RemoteEndpoint}", socket.RemoteEndPoint);
                }
            });

            return Task.CompletedTask;
        }

        private async Task HandleTerminalConnectRequestAsync(TerminalConnectRequest termReq, TcpSocket socket)
        {
            _logger.LogInformation("Received TerminalConnectRequest from {Username} at {RemoteEndpoint}", termReq.Username, socket.RemoteEndPoint);
                            
            if (!_sessionManager.AuthenticateUser(termReq.Username, termReq.Password))
            {
                _logger.LogWarning("Terminal authentication failed for {Username}.", termReq.Username);
                var rejectRes = new TerminalConnectResponse { MessageId = termReq.MessageId, Accepted = false, Reason = "InvalidCredentials" };
                _ = socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(rejectRes));
                _ = socket.StopAsync();
                return;
            }

            _logger.LogInformation("Terminal authenticated successfully for {Username}.", termReq.Username);

            // Check for existing session
            string existingSessionKey = "";
            SessionInfo? existingSession = null;
            foreach (var session in Registry.Sessions)
            {
                if (session.Value.ClientUri == termReq.SenderId)
                {
                    existingSessionKey = session.Key;
                    existingSession = session.Value;
                    break;
                }
            }

            if (existingSession != null)
            {
                if (Registry.Agents.TryGetValue(existingSession.ShellUri, out var shellAgent))
                {
                    var hostRuntime = shellAgent.Runtime;
                    if (hostRuntime == _config.ID)
                    {
                        var localShell = await _executionManager.WaitForAgentAsync(existingSession.ShellUri, TimeSpan.FromSeconds(5));
                        if (localShell == null)
                        {
                            var rejectRes = new TerminalConnectResponse { MessageId = termReq.MessageId, Accepted = false, Reason = "ShellAgentNotRunning" };
                            _ = socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(rejectRes));
                            _ = socket.StopAsync();
                            return;
                        }

                        var acceptRes = new TerminalConnectResponse { MessageId = termReq.MessageId, Accepted = true };
                        await socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(acceptRes));
                        
                        // Wire up to shell layer
                        var inputPipe = _executionManager.CreateRemoteInput(socket);
                        var outputPipe = _executionManager.CreateRemoteOutput(socket);
                        if (localShell is UserShell shell)
                        {
                            shell.ConnectPipes(inputPipe, outputPipe, termReq.Command);
                        }
                        return;
                    }
                    else
                    {
                        if (_config.Peers.TryGetValue(hostRuntime, out var hostPeerInfo))
                        {
                            _logger.LogInformation("Redirecting terminal for {Username} to runtime {HostRuntime} at {Address}", termReq.Username, hostRuntime, hostPeerInfo.Address);
                            var redirectRes = new TerminalRedirectResponse { MessageId = termReq.MessageId, RedirectAddress = hostPeerInfo.Address };
                            _ = socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(redirectRes));
                        }
                        else
                        {
                            _logger.LogWarning("Host runtime {HostRuntime} for user {Username} not found in peers configuration.", hostRuntime, termReq.Username);
                            var rejectRes = new TerminalConnectResponse { MessageId = termReq.MessageId, Accepted = false, Reason = "HostRuntimeNotFound" };
                            _ = socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(rejectRes));
                        }
                        _ = socket.StopAsync();
                        return;
                    }
                }
                else
                {
                    // Session exists but shell agent doesn't, this is an inconsistent state, reject for now
                    var rejectRes = new TerminalConnectResponse { MessageId = termReq.MessageId, Accepted = false, Reason = "ShellAgentNotFound" };
                    _ = socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(rejectRes));
                    _ = socket.StopAsync();

                    await _sessionManager.SignOutUser(existingSessionKey);

                    return;
                }
            }
            else
            {
                // No existing session, sign in and create shell
                var sessionKey = await _sessionManager.SignInUser(termReq.Username, termReq.Password, termReq.SenderId);
                if (sessionKey == null)
                {
                    _logger.LogWarning("Session creation failed for {Username}.", termReq.Username);
                    var rejectRes = new TerminalConnectResponse { MessageId = termReq.MessageId, Accepted = false, Reason = "UnknownSignInFailure" };
                    _ = socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(rejectRes));
                    _ = socket.StopAsync();
                    return;
                }

                string shellUri = await _sessionManager.CreateUserShell(sessionKey);
                
                var localShell = await _executionManager.WaitForAgentAsync(shellUri, TimeSpan.FromSeconds(15));
                if (localShell == null)
                {
                    _logger.LogWarning("Timeout waiting for UserShell to spawn for {Username}.", termReq.Username);
                    var rejectRes = new TerminalConnectResponse { MessageId = termReq.MessageId, Accepted = false, Reason = "ShellSpawnTimeout" };
                    _ = socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(rejectRes));
                    _ = socket.StopAsync();
                    return;
                }

                var acceptRes = new TerminalConnectResponse { MessageId = termReq.MessageId, Accepted = true };
                await socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(acceptRes));

                // Wire up to shell layer
                var inputPipe = _executionManager.CreateRemoteInput(socket);
                var outputPipe = _executionManager.CreateRemoteOutput(socket);
                if (localShell is UserShell shell)
                {
                    shell.ConnectPipes(inputPipe, outputPipe, termReq.Command);
                }
            }
        }

        private async Task<RuntimeMessage?> HandleRequestAsync(RuntimeMessage request, int linkIndex)
        {
            switch (request)
            {
                case HandshakeRequest req:
                    _logger.LogWarning("Received unexpected HandshakeRequest from {SenderId} on established connection LinkIndex {LinkIndex}.", req.SenderId, linkIndex);
                    return new HandshakeResponse { MessageId = req.MessageId, SenderId = _config.ID, Accepted = true };
                    
                case ResourceMetricsRequest req:
                    var sample = _eventHub.SampleResources();
                    sample.MessageId = req.MessageId;
                    return sample;

                case ClusterInfoRequest req:
                    return new ClusterInfoResponse { MessageId = req.MessageId, SenderId = _config.ID, Info = DescribeHost() };

                case ClusterInfoUpdate update:
                    AdoptClusterState(update.State);
                    return null;

                case RegistryUpdateRequest req:
                    if (IsLeader)
                    {
                        bool ok = await _synchronizer.ReplicateActionAsync(req.Action);
                        return new RegistryUpdateResponse { MessageId = req.MessageId, Success = ok, CommitIndex = ok ? _synchronizer.CommitIndex : 0 };
                    }
                    else
                    {
                        return new RegistryUpdateResponse { MessageId = req.MessageId, Success = false, ErrorMessage = "Not the leader" };
                    }

                case Envelope env:
                    // Envelopes are fire-and-forget from the networking layer's perspective.
                    // We dispatch it to the Router without returning a response.
                    _ = GetRouter(linkIndex).RouteAsync(env, request.SenderId);
                    return null;

                default:
                    _logger.LogWarning("Received unknown request type from {SenderId} on LinkIndex {LinkIndex}", request.SenderId, linkIndex);
                    return null;
            }
        }

        public async Task ForwardEnvelopeAsync(string remoteRuntimeId, Envelope envelope, int linkIndex = 0, CancellationToken ct = default)
        {
            if (_peers.TryGetValue(remoteRuntimeId, out var peer))
            {
                await peer.SendAsync(envelope, linkIndex, ct);
            }
            else
            {
                _logger.LogWarning("Cannot forward envelope to {RuntimeId}, peer not found or disconnected.", remoteRuntimeId);
            }
        }

        // How long after losing its leader (or starting) a runtime's updates wait for one: an election takes 1–2 s
        // (see Synchronizer). Past that, as when running without a quorum, an update fails at once, as it did
        // before (shutdown cleanup on a lone runtime makes many; each waiting would take minutes).
        public TimeSpan LeaderWait { get; set; } = TimeSpan.FromSeconds(5);

        public async Task<bool> UpdateRegistryAsync(RegistryAction action, CancellationToken ct = default)
        {
            while (!IsLeader && _synchronizer.LeaderAddress == null && DateTime.UtcNow < _synchronizer.LeaderLostAt + LeaderWait)
                await Task.Delay(100, ct);
            if (IsLeader)
            {
                return await _synchronizer.ReplicateActionAsync(action, ct);
            }
            else
            {
                var leaderAddress = _synchronizer.LeaderAddress;
                if (leaderAddress == null)
                {
                    _logger.LogWarning("Cannot update registry: no leader known.");
                    return false;
                }
                
                // Find the peer that corresponds to the leader
                PeerRuntime? leaderPeer = null;
                foreach (var kvp in _peers)
                {
                    if (_config.Peers.TryGetValue(kvp.Key, out var info) && info.Address == leaderAddress)
                    {
                        leaderPeer = kvp.Value;
                        break;
                    }
                }

                if (leaderPeer == null)
                {
                    _logger.LogWarning("Cannot update registry: leader peer {Address} not found in connected peers.", leaderAddress);
                    return false;
                }

                var req = new RegistryUpdateRequest 
                { 
                    MessageId = Guid.NewGuid(), 
                    SenderId = _config.ID, 
                    Action = action 
                };
                
                try
                {
                    // Control plane updates always go over link 0
                    var res = await leaderPeer.RequestAsync(req, 0, ct);
                    if (res is RegistryUpdateResponse r)
                    {
                        if (r.Success) await _synchronizer.WaitForAppliedAsync(r.CommitIndex, ct);
                        return r.Success;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to forward RegistryUpdateRequest to leader.");
                }
                
                return false;
            }
        }

        // Target Runtime needs to set up their own server side pipe -- these methods do not set it up for them.
        // They only obtain the client-side end of the pipe.
        internal async Task<RemoteRawInputPipe> EstablishRemoteRawInputPipe(string targetRuntimeId, string pipeId) =>
            new RemoteRawInputPipe(await ConnectPipeAsync(targetRuntimeId, pipeId, "read", PipeKind.Raw));

        internal async Task<RemoteRawOutputPipe> EstablishRemoteRawOutputPipe(string targetRuntimeId, string pipeId) =>
            new RemoteRawOutputPipe(await ConnectPipeAsync(targetRuntimeId, pipeId, "write", PipeKind.Raw));

        internal async Task<RemoteMessageInputPipe> EstablishRemoteMessageInputPipe(string targetRuntimeId, string pipeId) =>
            new RemoteMessageInputPipe(await ConnectPipeAsync(targetRuntimeId, pipeId, "read", PipeKind.Message));

        internal async Task<RemoteMessageOutputPipe> EstablishRemoteMessageOutputPipe(string targetRuntimeId, string pipeId) =>
            new RemoteMessageOutputPipe(await ConnectPipeAsync(targetRuntimeId, pipeId, "write", PipeKind.Message));

        // A tunnel to the runtime hosting cluster-wide socket `port` (plan step 8): raw bytes both ways.
        internal Task<TcpSocket> ConnectSocketTunnelAsync(string targetRuntimeId, int port) =>
            ConnectWithRequestAsync(targetRuntimeId, new SocketConnectRequest { Port = port, SenderId = Config.ID, MessageId = Guid.NewGuid() }, $"socket {port} tunnel");

        // Connects to the target runtime and asks it to hand the connection to whoever expects `pipeId` there.
        private Task<TcpSocket> ConnectPipeAsync(string targetRuntimeId, string pipeId, string mode, PipeKind kind) =>
            ConnectWithRequestAsync(targetRuntimeId, new RawPipeRequest { PipeId = pipeId, Mode = mode, Kind = kind, SenderId = Config.ID, MessageId = Guid.NewGuid() }, $"{kind} pipe");

        // Opens a connection to the target runtime whose first message is `request`, and waits for its
        // RawPipeResponse; the connection then belongs to the caller.
        private async Task<TcpSocket> ConnectWithRequestAsync(string targetRuntimeId, RuntimeMessage request, string what)
        {
            var peerConfig = targetRuntimeId == Config.ID ? null : Config.Peers[targetRuntimeId];
            var host = peerConfig?.Host ?? "127.0.0.1";
            var port = peerConfig?.Port ?? Config.Port;

            var certHash = peerConfig?.CertificateHash ?? "";
            var socket = await ConnectionManager.ConnectTo($"{host}:{port}", certHash);
            if (socket == null) throw new Exception($"Failed to connect to {host}:{port}");

            await socket.Send(MessagePack.MessagePackSerializer.Serialize<RuntimeMessage>(request));

            // Asynchronously: many pipes open at once when a graph starts, and a pool thread blocked here per
            // connection (waiting on a peer that may need one to answer) starved the pool for seconds.
            var resBytes = await socket.ReceiveAsync();
            var res = MessagePack.MessagePackSerializer.Deserialize<RuntimeMessage>(resBytes);
            if (res is RawPipeResponse rpr && rpr.Accepted)
            {
                socket.StopListen();
                return socket;
            }
            await socket.StopAsync();
            throw new Exception($"{what} request rejected.");
        }
    }
}