using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using MessagePack;

namespace OneOS.Runtime
{
    // The Registry as a Raft state machine; the Raft log and its snapshots are the only persistent state.
    //  - A log entry is a MessagePack RegistryAction, applied when committed.
    //  - A snapshot is the whole Registry (MessagePack) as of the snapshot's index. Compaction (automatic
    //    during commits, and forced at stop) folds committed entries into it; on restart the node
    //    restores it and replays the rest of its log; a follower that fell behind the leader's
    //    compacted log installs the leader's snapshot.
    public class RegistryPersistentState : MemoryBasedStateMachine
    {
        private readonly Registry _registry;

        public event Action<RegistryAction>? OnRegistryUpdated;
        // The whole state was replaced by a snapshot (restore at startup, or installed from the leader).
        public event Action? OnSnapshotInstalled;

        // Ordered observation (EventHub): each action with its log index, and each replacement of the whole state,
        // raised while holding the state lock, so an observer that reads the state under the same lock
        // (WithState) sees every later change exactly once, in order. Handlers must be quick and must not block.
        public event Action<long, RegistryAction>? OnApplied;
        public event Action<long>? OnReplaced;
        private readonly object _stateLock = new();
        private long _stateIndex;   // the log index the Registry reflects (entries without an action don't count)

        // Reads the Registry, and the log index it reflects, with no change applied meanwhile.
        public T WithState<T>(Func<long, Registry, T> read)
        {
            lock (_stateLock) return read(_stateIndex, _registry);
        }

        public RegistryPersistentState(string path, Registry registry)
            : base(path, 100, new Options { InitialPartitionSize = 1024 * 1024, UseCaching = false, ReplayOnInitialize = true })
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        // The index of the last entry applied to the Registry; see WaitForAppliedAsync.
        private long _applied;
        private readonly object _appliedLock = new();
        private TaskCompletionSource _appliedChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Committed entries are applied asynchronously. Waits until everything up to `index` is applied,
        // so a writer can read its own write.
        public async Task WaitForAppliedAsync(long index, CancellationToken ct)
        {
            while (true)
            {
                Task changed;
                lock (_appliedLock)
                {
                    if (_applied >= index) return;
                    changed = _appliedChanged.Task;
                }
                await changed.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        private void MarkApplied(long index)
        {
            TaskCompletionSource signal;
            lock (_appliedLock)
            {
                if (index <= _applied) return;
                _applied = index;
                signal = _appliedChanged;
                _appliedChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            signal.TrySetResult();
        }

        protected override async ValueTask ApplyAsync(LogEntry entry)
        {
            try
            {
                var payload = await entry.ToByteArrayAsync();
                if (entry.IsSnapshot)
                {
                    var restored = Decode(payload);
                    lock (_stateLock)
                    {
                        _registry.ReplaceWith(restored);
                        _stateIndex = entry.Index;
                        OnReplaced?.Invoke(entry.Index);
                    }
                    OnSnapshotInstalled?.Invoke();
                }
                else if (payload.Length > 0)
                {
                    var action = MessagePackSerializer.Deserialize<RegistryAction>(payload);
                    lock (_stateLock)
                    {
                        _registry.Apply(action);
                        _stateIndex = entry.Index;
                        OnApplied?.Invoke(entry.Index, action);
                    }
                    OnRegistryUpdated?.Invoke(action);
                }
                else return;   // empty entries (e.g. a new leader's no-op) change nothing

                // A readable copy of the current state, for debugging.
                var registryPath = Path.Combine(Directory.GetParent(Location.FullName)!.FullName, "registry.json");
                await File.WriteAllTextAsync(registryPath, _registry.ToJsonString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to apply registry {(entry.IsSnapshot ? "snapshot" : "action")}: {ex.Message}");
            }
            finally
            {
                MarkApplied(entry.Index);
            }
        }

        // An empty snapshot (the initial state) decodes to a fresh Registry.
        private static Registry Decode(byte[] payload) =>
            payload.Length == 0 ? new Registry() : MessagePackSerializer.Deserialize<Registry>(payload);

        protected override SnapshotBuilder CreateSnapshotBuilder(in SnapshotBuilderContext context) => new RegistrySnapshotBuilder(context);

        // Builds the snapshot from the previous snapshot plus the entries being compacted, i.e. the exact
        // state at the snapshot's index. (The live Registry may already include later entries.)
        private sealed class RegistrySnapshotBuilder : IncrementalSnapshotBuilder
        {
            private Registry _state = new Registry();

            public RegistrySnapshotBuilder(in SnapshotBuilderContext context) : base(context) { }

            protected override async ValueTask ApplyAsync(LogEntry entry)
            {
                var payload = await entry.ToByteArrayAsync();
                if (entry.IsSnapshot) _state = Decode(payload);
                else if (payload.Length > 0) _state.Apply(MessagePackSerializer.Deserialize<RegistryAction>(payload));
            }

            public override ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token) =>
                writer.WriteAsync(MessagePackSerializer.Serialize(_state), null, token);
        }
    }
}
