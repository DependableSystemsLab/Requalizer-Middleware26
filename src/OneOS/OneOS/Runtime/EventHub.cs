using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.Extensions.Logging;
using OneOS.Common;

namespace OneOS.Runtime
{
    // Browser clients' subscriptions (tier 0): the connected subscribers and their topics, and what each topic
    // publishes (see BrowserConnectRequest). Nothing is kept in the Registry: a subscription lasts as long as its
    // connection.
    //   "registry"  every runtime has the whole Registry, so this runtime publishes it: a snapshot, then each
    //               applied action. The snapshot is taken under the Registry's state lock, and actions are
    //               published under it too, so a subscriber gets every action after its snapshot exactly once.
    //   "metrics"   resource samples exist on each runtime: while any subscriber here wants them, every
    //               MetricsInterval this runtime samples itself and asks each peer for its sample
    //               (ResourceMetricsRequest), and publishes one ResourceMetricsEvent per runtime (Unreachable when
    //               a peer doesn't answer in time).
    // Each subscriber has a bounded queue drained by its own writer; one that falls that far behind is
    // disconnected (it may reconnect and gets a fresh snapshot) rather than holding events for the others.
    public sealed class EventHub : IAsyncDisposable
    {
        public const string RegistryTopic = "registry";
        public const string MetricsTopic = "metrics";
        public static readonly IReadOnlyList<string> Topics = new[] { RegistryTopic, MetricsTopic };

        public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(2);
        public TimeSpan MetricsTimeout { get; set; } = TimeSpan.FromSeconds(1.5);
        public int QueueCapacity { get; set; } = 10_000;

        private readonly Runtime _runtime;
        private readonly ILogger _logger;
        private readonly Monitoring.ResourceSampler _sampler;
        private readonly object _lock = new();
        private readonly List<Subscriber> _subscribers = new();
        private readonly CancellationTokenSource _stop = new();
        private Task? _metricsLoop;

        public EventHub(Runtime runtime, ILogger logger)
        {
            _runtime = runtime;
            _logger = logger;
            _sampler = new Monitoring.ResourceSampler(() => runtime.ExecutionManager.ProcessAgents
                .Where(a => a.ProcessId is int).Select(a => (a.URI, a.ProcessId!.Value)));
        }

        public int SubscriberCount { get { lock (_lock) return _subscribers.Count; } }

        // Once the Registry is running (Synchronizer started).
        public void Start()
        {
            _runtime.Synchronizer.OnRegistryApplied += PublishRegistryAction;
            _runtime.Synchronizer.OnRegistryReplaced += PublishRegistrySnapshot;
            _metricsLoop = Task.Run(() => MetricsLoopAsync(_stop.Token));
        }

        // A browser client's connection, after its first message: authenticates, answers, then serves it until it
        // disconnects. The socket was handed over by the runtime's framed reader.
        public async Task AcceptAsync(BrowserConnectRequest request, TcpSocket socket)
        {
            if (!_runtime.SessionManager.AuthenticateUser(request.Username, request.Password))
            {
                await Reply(socket, new BrowserConnectResponse { MessageId = request.MessageId, SenderId = _runtime.Config.ID, Reason = "invalid credentials" });
                _ = socket.StopAsync();
                return;
            }
            var subscriber = new Subscriber(request.Username, socket, QueueCapacity);
            lock (_lock) _subscribers.Add(subscriber);
            _logger.LogInformation("Browser client {User} connected from {Endpoint}", request.Username, socket.RemoteEndPoint);
            await Reply(socket, new BrowserConnectResponse
            {
                MessageId = request.MessageId, SenderId = _runtime.Config.ID, Accepted = true, RuntimeId = _runtime.Config.ID, Topics = Topics.ToList(),
            });
            socket.OnEnded += _ => Remove(subscriber, "disconnected");
            _ = WriterAsync(subscriber);
            _ = socket.Listen(frame => OnFrame(subscriber, frame));
        }

        private static Task Reply(TcpSocket socket, RuntimeMessage message) => socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(message));

        private void OnFrame(Subscriber subscriber, byte[] frame)
        {
            RuntimeMessage message;
            try { message = MessagePackSerializer.Deserialize<RuntimeMessage>(frame); }
            catch (Exception ex) { _logger.LogDebug("Browser client {User}: unreadable frame ({Error})", subscriber.User, ex.Message); return; }
            switch (message)
            {
                case SubscribeRequest subscribe:
                    foreach (var topic in subscribe.Topics.Distinct()) Subscribe(subscriber, topic);
                    break;
                case UnsubscribeRequest unsubscribe:
                    lock (_lock) foreach (var topic in unsubscribe.Topics) subscriber.Topics.Remove(topic);
                    break;
                default:
                    _logger.LogDebug("Browser client {User}: ignoring {Type}", subscriber.User, message.GetType().Name);
                    break;
            }
        }

        private void Subscribe(Subscriber subscriber, string topic)
        {
            switch (topic)
            {
                case RegistryTopic:
                    // Snapshot and subscription together, under the Registry's state lock: every later action is
                    // published to this subscriber after its snapshot, none before.
                    _runtime.Synchronizer.WithRegistryState((index, registry) =>
                    {
                        lock (_lock)
                        {
                            if (!subscriber.Topics.Add(RegistryTopic)) return 0;
                            Enqueue(subscriber, Snapshot(index, registry));
                        }
                        return 0;
                    });
                    break;
                case MetricsTopic:
                    lock (_lock) subscriber.Topics.Add(MetricsTopic);
                    break;
                default:
                    _logger.LogDebug("Browser client {User}: no topic '{Topic}'", subscriber.User, topic);
                    break;
            }
        }

        // Published Registry content never carries credentials (a user's stored value is a salted password hash).
        // Subscribers get the usernames with empty values, and user additions and removals (SetUserAction without
        // its password, DeleteUserAction). Only these published copies are redacted: the Registry and Raft
        // replication keep the full SetUserAction.
        private RegistrySnapshotEvent Snapshot(long index, Registry registry)
        {
            var copy = MessagePackSerializer.Deserialize<Registry>(MessagePackSerializer.Serialize(registry));
            foreach (var user in copy.Users.Keys.ToList()) copy.Users[user] = string.Empty;
            return new() { MessageId = Guid.NewGuid(), SenderId = _runtime.Config.ID, Index = index, Registry = MessagePackSerializer.Serialize(copy) };
        }

        private static RegistryAction Redact(RegistryAction action) => action switch
        {
            SetUserAction user => new SetUserAction { Username = user.Username, Password = string.Empty },
            TransactionAction tx when tx.Actions.Any(a => a is SetUserAction or TransactionAction) => new TransactionAction { Actions = tx.Actions.Select(Redact).ToList() },
            _ => action,
        };

        // Raised under the Registry's state lock (Synchronizer.OnRegistryApplied).
        private void PublishRegistryAction(long index, RegistryAction action)
        {
            lock (_lock)
            {
                if (_subscribers.Count == 0) return;
                var message = new RegistryUpdateEvent { MessageId = Guid.NewGuid(), SenderId = _runtime.Config.ID, Index = index, Action = Redact(action) };
                foreach (var s in _subscribers.Where(s => s.Topics.Contains(RegistryTopic)).ToList()) Enqueue(s, message);
            }
        }

        // The whole Registry was replaced (a snapshot from the leader): subscribers start over from it.
        private void PublishRegistrySnapshot(long index)
        {
            _runtime.Synchronizer.WithRegistryState((current, registry) =>
            {
                lock (_lock)
                {
                    var subscribers = _subscribers.Where(s => s.Topics.Contains(RegistryTopic)).ToList();
                    if (subscribers.Count == 0) return 0;
                    var message = Snapshot(current, registry);
                    foreach (var s in subscribers) Enqueue(s, message);
                }
                return 0;
            });
        }

        // Under _lock.
        private void Enqueue(Subscriber subscriber, RuntimeMessage message)
        {
            if (subscriber.Queue.Writer.TryWrite(message)) return;
            // Too far behind (or already closing): drop the subscriber rather than an event.
            _ = Task.Run(() => Remove(subscriber, $"fell {QueueCapacity} events behind"));
        }

        private async Task WriterAsync(Subscriber subscriber)
        {
            try
            {
                await foreach (var message in subscriber.Queue.Reader.ReadAllAsync())
                    await subscriber.Socket.Send(MessagePackSerializer.Serialize(message)).ConfigureAwait(false);
            }
            catch (Exception ex) { Remove(subscriber, $"send failed: {ex.Message}"); }
        }

        private void Remove(Subscriber subscriber, string reason)
        {
            lock (_lock)
            {
                if (!_subscribers.Remove(subscriber)) return;
            }
            subscriber.Queue.Writer.TryComplete();
            _logger.LogInformation("Browser client {User} removed: {Reason}", subscriber.User, reason);
            _ = subscriber.Socket.StopAsync();
        }

        // This runtime's resource sample (also a peer's answer to ResourceMetricsRequest).
        public ResourceMetricsEvent SampleResources()
        {
            var now = DateTime.UtcNow;
            var (process, agents) = _sampler.Sample(now);
            return new ResourceMetricsEvent { MessageId = Guid.NewGuid(), SenderId = _runtime.Config.ID, Runtime = _runtime.Config.ID, Time = now, Process = process, Agents = agents };
        }

        private async Task MetricsLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(MetricsInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                List<Subscriber> subscribers;
                lock (_lock) subscribers = _subscribers.Where(s => s.Topics.Contains(MetricsTopic)).ToList();
                if (subscribers.Count == 0) continue;
                try
                {
                    var samples = new List<ResourceMetricsEvent> { SampleResources() };
                    samples.AddRange(await _runtime.RequestPeerResourceMetricsAsync(MetricsTimeout, ct).ConfigureAwait(false));
                    lock (_lock)
                        foreach (var s in _subscribers.Where(s => s.Topics.Contains(MetricsTopic)).ToList())
                            foreach (var sample in samples) Enqueue(s, sample);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _logger.LogDebug("Metrics round failed: {Error}", ex.Message); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _runtime.Synchronizer.OnRegistryApplied -= PublishRegistryAction;
            _runtime.Synchronizer.OnRegistryReplaced -= PublishRegistrySnapshot;
            if (_metricsLoop != null) { try { await _metricsLoop.ConfigureAwait(false); } catch (Exception) { } }
            List<Subscriber> all;
            lock (_lock) all = _subscribers.ToList();
            foreach (var s in all) Remove(s, "runtime stopping");
        }

        private sealed class Subscriber
        {
            public Subscriber(string user, TcpSocket socket, int capacity)
            {
                User = user;
                Socket = socket;
                Queue = Channel.CreateBounded<RuntimeMessage>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            }

            public string User { get; }
            public TcpSocket Socket { get; }
            public Channel<RuntimeMessage> Queue { get; }
            public HashSet<string> Topics { get; } = new();
        }
    }
}
