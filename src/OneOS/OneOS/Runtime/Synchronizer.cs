using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using MessagePack;
using OneOS.Common;

namespace OneOS.Runtime
{
    public enum SynchronizerState
    {
        Detached,
        Follower,
        Candidate,
        Leader
    }

    public enum SynchronizerTrigger
    {
        Start,
        ElectionTimeout,
        WinElection,
        DiscoverLeader,
        Stop
    }

    /// <summary>
    /// Synchronizer manages the Raft cluster node and the state machine.
    /// </summary>
    public class Synchronizer : IDisposable
    {
        private readonly Configuration _config;
        private RegistryPersistentState? _stateMachine;
        private RaftCluster? _raftCluster;

        public StateMachine<SynchronizerState, SynchronizerTrigger> Fsm { get; }

        // Since when no leader has been known (the start, or when the last known leader was lost).
        private DateTime _leaderLostAt = DateTime.UtcNow;
        private volatile bool _hadLeader;
        public DateTime LeaderLostAt { get { _ = LeaderAddress; return _leaderLostAt; } }

        public string? LeaderAddress
        {
            get
            {
                var leader = _raftCluster?.Leader;
                if (leader == null)
                {
                    if (_hadLeader) { _hadLeader = false; _leaderLostAt = DateTime.UtcNow; }
                    return null;
                }
                _hadLeader = true;
                
                if (!leader.IsRemote)
                    return $"{_config.Host}:{_config.Port}";

                if (leader.EndPoint is System.Net.IPEndPoint ipEp)
                {
                    return $"{ipEp.Address}:{ipEp.Port - 100}";
                }
                
                return null;
            }
        }

#pragma warning disable CS0067 // Event is never used
        public event Action<string>? OnFollowerJoined;
        public event Action<string>? OnFollowerDropped;
#pragma warning restore CS0067
        public event Action<RegistryAction>? OnRegistryUpdated;
        public event Action? OnRegistryHydrated;
        
        public Registry Registry { get; private set; } = new Registry();
        private RaftMembershipStorage? _membership;
        private IReadOnlySet<System.Net.EndPoint> Members =>
            ((DotNext.Net.Cluster.Consensus.Raft.Membership.IClusterConfigurationStorage<System.Net.EndPoint>)_membership!).ActiveConfiguration;

        // DotNext's own Raft logging (elections, unavailable members, replication failures), Information and up.
        private readonly Microsoft.Extensions.Logging.ILoggerFactory? _raftLoggerFactory;

        public Synchronizer(Configuration config, Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (loggerFactory != null) _raftLoggerFactory = new MinimumLevelLoggerFactory(loggerFactory, Microsoft.Extensions.Logging.LogLevel.Information);
            
            Fsm = new StateMachine<SynchronizerState, SynchronizerTrigger>(SynchronizerState.Detached);
            ConfigureFsm();
        }

        private void ConfigureFsm()
        {
            Fsm.AddTransition(SynchronizerState.Detached, SynchronizerTrigger.Start, SynchronizerState.Follower);
            
            Fsm.AddTransition(SynchronizerState.Follower, SynchronizerTrigger.ElectionTimeout, SynchronizerState.Candidate);
            Fsm.AddTransition(SynchronizerState.Candidate, SynchronizerTrigger.ElectionTimeout, SynchronizerState.Candidate);
            Fsm.AddTransition(SynchronizerState.Candidate, SynchronizerTrigger.WinElection, SynchronizerState.Leader);
            Fsm.AddTransition(SynchronizerState.Candidate, SynchronizerTrigger.DiscoverLeader, SynchronizerState.Follower);
            Fsm.AddTransition(SynchronizerState.Leader, SynchronizerTrigger.DiscoverLeader, SynchronizerState.Follower);
            
            // Allow direct transition from Follower to Leader (if DotNext skips Candidate eventing)
            Fsm.AddTransition(SynchronizerState.Follower, SynchronizerTrigger.WinElection, SynchronizerState.Leader);
            
            Fsm.AddTransition(SynchronizerState.Follower, SynchronizerTrigger.Stop, SynchronizerState.Detached);
            Fsm.AddTransition(SynchronizerState.Candidate, SynchronizerTrigger.Stop, SynchronizerState.Detached);
            Fsm.AddTransition(SynchronizerState.Leader, SynchronizerTrigger.Stop, SynchronizerState.Detached);
        }

        // The index of the last committed entry (on the leader, that includes every action it has replicated).
        public long CommitIndex => _stateMachine?.LastCommittedEntryIndex ?? 0;

        // Waits (at most 10 s) until the local Registry has applied everything up to `index`: a follower's
        // read-your-writes after its update was committed through the leader.
        public async Task WaitForAppliedAsync(long index, CancellationToken cancellationToken = default)
        {
            if (_stateMachine == null || index <= 0) return;
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(TimeSpan.FromSeconds(10));
            try { await _stateMachine.WaitForAppliedAsync(index, bounded.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"[WARNING] Registry entry {index}, committed by the leader, was not applied locally within 10 s.");
            }
        }

        // Ordered Registry observation for the EventHub (see RegistryPersistentState.OnApplied).
        public event Action<long, RegistryAction>? OnRegistryApplied;
        public event Action<long>? OnRegistryReplaced;
        public T WithRegistryState<T>(Func<long, Registry, T> read) =>
            _stateMachine != null ? _stateMachine.WithState(read) : read(0, Registry);

        public async Task<bool> ReplicateActionAsync(RegistryAction action, CancellationToken cancellationToken = default)
        {
            if (Fsm.CurrentState != SynchronizerState.Leader) return false;

            if (_raftCluster == null) return false;

            var payload = MessagePackSerializer.Serialize(action);
            var entry = new ActionLogEntry(payload, _raftCluster.Term);
            // Returns once a majority has committed the entry (false: retry; e.g. leadership changed).
            try
            {
                if (!await _raftCluster.ReplicateAsync(entry, cancellationToken).ConfigureAwait(false)) return false;
                // Committed means at most the current commit index; wait until the local Registry has applied
                // that far, so the caller reads its own write (callers read the Registry right after this).
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(TimeSpan.FromSeconds(10));
                try { await _stateMachine!.WaitForAppliedAsync(_stateMachine.LastCommittedEntryIndex, bounded.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("[WARNING] A committed Registry action was not applied locally within 10 s.");
                }
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;   // no longer the leader
            }
        }

        public readonly struct ActionLogEntry : IRaftLogEntry
        {
            private readonly byte[] _payload;
            
            public ActionLogEntry(byte[] payload, long term)
            {
                _payload = payload;
                Term = term;
                Timestamp = DateTimeOffset.UtcNow;
            }

            public long Term { get; }
            public DateTimeOffset Timestamp { get; }
            public int? CommandId => null;
            public bool IsSnapshot => false;
            
            public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token) where TWriter : IAsyncBinaryWriter
            {
                return writer.WriteAsync(new ReadOnlyMemory<byte>(_payload), null, token);
            }
                
            public long? Length => _payload.Length;
            
            public bool IsReusable => true;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"Initializing Raft Synchronizer for node '{_config.ID}'...");

            var logPath = Path.Combine(_config.MountPath, _config.LogPath);
            if (!Directory.Exists(logPath))
                Directory.CreateDirectory(logPath);

            // The Raft log (latest snapshot + later entries) is the only persistent state: initializing the
            // state machine restores the Registry from it.
            Registry = new Registry();

            var raftLogPath = Path.Combine(logPath, "raft");
            if (!Directory.Exists(raftLogPath))
            {
                Directory.CreateDirectory(raftLogPath);
            }

            try
            {
                _stateMachine = new RegistryPersistentState(raftLogPath, Registry);
                await _stateMachine.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                // The node then starts empty and catches up from the leader (entries, or its snapshot).
                Console.WriteLine($"[WARNING] Corrupted Raft log detected at '{raftLogPath}'. Wiping and starting fresh...");
                Directory.Delete(raftLogPath, true);
                Directory.CreateDirectory(raftLogPath);
                _stateMachine?.Dispose();
                Registry.ReplaceWith(new Registry());
                _stateMachine = new RegistryPersistentState(raftLogPath, Registry);
                await _stateMachine.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }

            _stateMachine.OnRegistryUpdated += action => OnRegistryUpdated?.Invoke(action);
            _stateMachine.OnApplied += (index, action) => OnRegistryApplied?.Invoke(index, action);
            _stateMachine.OnReplaced += index => OnRegistryReplaced?.Invoke(index);
            // A snapshot installed from the leader replaces the whole state: components re-synchronize.
            _stateMachine.OnSnapshotInstalled += () => { if (Fsm.CurrentState != SynchronizerState.Detached) OnRegistryHydrated?.Invoke(); };
            OnRegistryHydrated?.Invoke();

            // The membership persists with the log: only a seed node with no known members bootstraps the cluster
            // (a one-node cluster that then adds its peers); a restarted cluster elects among the members it knows.
            var membershipPath = Path.Combine(raftLogPath, "members");
            _membership = new RaftMembershipStorage(membershipPath);
            // The seed (the runtime that bootstraps a new cluster) is the one whose ID sorts first among itself and
            // its configured peers: deterministic across the cluster (test0 of test0..2; node1 of node1..n).
            bool seed = _config.Peers.Keys.All(peer => string.CompareOrdinal(_config.ID, peer) <= 0);

            var raftPort = _config.Port + 100;
            // Timing (Raft needs request round trips well inside the election timeout): the leader's heartbeat
            // round and a candidate's vote wait for every member up to RequestTimeout, so one member that is
            // alive but unresponsive (paused, swapping) must not hold them past the followers' election timeout.
            // Elections start after 1–2 s without a heartbeat (heartbeats every 300 ms), not 150–300 ms, so GC
            // pauses and a loaded machine don't depose a healthy leader.
            var raftConfig = new RaftCluster.TcpConfiguration(new System.Net.IPEndPoint(System.Net.IPAddress.Any, raftPort))
            {
                RequestTimeout = TimeSpan.FromMilliseconds(500),
                ConnectTimeout = TimeSpan.FromMilliseconds(500),
                LowerElectionTimeout = 1000,
                UpperElectionTimeout = 2000,
                HeartbeatThreshold = 0.3,
                ColdStart = seed && !RaftMembershipStorage.HasMembers(membershipPath),
                ConfigurationStorage = _membership,
                LoggerFactory = _raftLoggerFactory,
            };

            if (System.Net.IPAddress.TryParse(_config.Host, out var ip))
            {
                raftConfig.PublicEndPoint = new System.Net.IPEndPoint(ip, raftPort);
            }

            _raftCluster = new RaftCluster(raftConfig);
            _raftCluster.AuditTrail = _stateMachine!;
            _raftCluster.LeaderChanged += OnLeaderChanged;
            _raftCluster.MemberAdded += (cluster, e) =>
            {
                e.Member.MemberStatusChanged += OnMemberStatusChanged;
            };

            // Initialize the StateMachine logically
            Fsm.FireStrict(SynchronizerTrigger.Start);

            // Start DotNext Raft Cluster
            await _raftCluster.StartAsync(cancellationToken).ConfigureAwait(false);

            OnRegistryHydrated?.Invoke();

            // Bootstrap: a cold-starting seed, once it has elected itself (a one-node cluster; that takes an election
            // timeout, 1–2 s), adds the configured peers that aren't members yet, retrying until each is added or
            // it stops being the leader. (A fixed 2 s wait raced the election and could leave the peers out.)
            if (raftConfig.ColdStart) _ = Task.Run(async () =>
            {
                try
                {
                    var pending = _config.Peers.Values
                        .Select(peer => System.Net.IPAddress.TryParse(peer.Host, out var ip) ? (peer.ID, EndPoint: new System.Net.IPEndPoint(ip, peer.Port + 100)) : default)
                        .Where(p => p.EndPoint != null).ToList();
                    while (pending.Count > 0 && !cancellationToken.IsCancellationRequested)
                    {
                        if (_raftCluster.Leader?.IsRemote != false) { await Task.Delay(200, cancellationToken); continue; }   // not (yet) the leader
                        foreach (var (id, endPoint) in pending.ToList())
                        {
                            if (Members.Contains(endPoint)) { pending.Remove((id, endPoint)); continue; }
                            try
                            {
                                if (await _raftCluster.AddMemberAsync(endPoint, cancellationToken))
                                {
                                    Console.WriteLine($"Added {id} to Raft cluster at {endPoint}");
                                    pending.Remove((id, endPoint));
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                            catch (Exception ex) { Console.WriteLine($"Failed to add {id} to Raft cluster (will retry): {ex.Message}"); }
                        }
                        if (pending.Count > 0) await Task.Delay(1000, cancellationToken);
                    }
                }
                catch (OperationCanceledException) { }
            }, cancellationToken);
        }

        private void OnLeaderChanged(DotNext.Net.Cluster.ICluster cluster, DotNext.Net.Cluster.IClusterMember? leader)
        {
            if (leader != null)
            {
                if (leader.IsRemote)
                {
                    Console.WriteLine($"Raft Leader Changed: Node '{leader.EndPoint}' is the new leader.");
                    if (Fsm.CurrentState == SynchronizerState.Leader || Fsm.CurrentState == SynchronizerState.Candidate)
                    {
                        Fsm.FireStrict(SynchronizerTrigger.DiscoverLeader);
                    }
                }
                else
                {
                    Console.WriteLine($"Raft Leader Changed: This node is now the LEADER.");
                    if (Fsm.CurrentState == SynchronizerState.Candidate || Fsm.CurrentState == SynchronizerState.Follower)
                    {
                        Fsm.FireStrict(SynchronizerTrigger.WinElection);
                    }
                }
            }
        }

        private void OnMemberStatusChanged(DotNext.Net.Cluster.ClusterMemberStatusChangedEventArgs e)
        {
            if (e.NewStatus == DotNext.Net.Cluster.ClusterMemberStatus.Unavailable)
            {
                if (e.Member.EndPoint is System.Net.IPEndPoint ipEp)
                {
                    int port = ipEp.Port - 100;
                    foreach (var peer in _config.Peers.Values)
                    {
                        if (peer.Port == port) 
                        {
                            OnFollowerDropped?.Invoke(peer.ID);
                            break;
                        }
                    }
                }
            }
        }


        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Console.WriteLine("Stopping Raft Synchronizer...");
            
            if (Fsm.CurrentState != SynchronizerState.Detached)
            {
                Fsm.FireStrict(SynchronizerTrigger.Stop);
            }
            
            if (_raftCluster != null)
            {
                if (_stateMachine != null)
                {
                    try
                    {
                        await _stateMachine.ForceCompactionAsync(long.MaxValue, cancellationToken).ConfigureAwait(false);
                        Console.WriteLine("Successfully took memory snapshot for state machine.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARNING] Failed to force snapshot compaction: {ex.Message}");
                    }
                }
                
                await _raftCluster.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _raftCluster?.Dispose();
            _stateMachine?.Dispose();
            _membership?.Dispose();
        }

    // A logger factory whose loggers drop entries below a minimum level (DotNext logs every replication at Debug).
    internal sealed class MinimumLevelLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        private readonly Microsoft.Extensions.Logging.ILoggerFactory _inner;
        private readonly Microsoft.Extensions.Logging.LogLevel _minimum;

        public MinimumLevelLoggerFactory(Microsoft.Extensions.Logging.ILoggerFactory inner, Microsoft.Extensions.Logging.LogLevel minimum) { _inner = inner; _minimum = minimum; }

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Filtered(_inner.CreateLogger(categoryName), _minimum);
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) => _inner.AddProvider(provider);
        public void Dispose() { }

        private sealed class Filtered : Microsoft.Extensions.Logging.ILogger
        {
            private readonly Microsoft.Extensions.Logging.ILogger _inner;
            private readonly Microsoft.Extensions.Logging.LogLevel _minimum;
            public Filtered(Microsoft.Extensions.Logging.ILogger inner, Microsoft.Extensions.Logging.LogLevel minimum) { _inner = inner; _minimum = minimum; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level >= _minimum && _inner.IsEnabled(level);
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(level)) _inner.Log(level, id, state, exception, formatter);
            }
        }
    }
    }
}
