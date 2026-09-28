using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using OneOS.Common;
using OneOS.Runtime;

namespace OneOS.Client
{
    // A publish-subscribe client of the cluster (for the WebTerminal; the REPL is OneOS.Client.Terminal). It
    // connects to any runtime, subscribes to topics, and receives their events (see the runtime's EventHub):
    //   "registry"  a snapshot of the Registry, then every action applied after it. The client keeps a mirror
    //               (Registry) up to date and raises RegistrySnapshot / RegistryUpdated.
    //   "metrics"   ResourceMetrics: each runtime's CPU and memory, and its agent processes', every couple of seconds.
    // Handlers run on the connection's reader, one event at a time, in order; they should return quickly.
    // Subscriptions end with the connection; reconnecting (a new Browser) starts from a fresh snapshot.
    public sealed class Browser : IAsyncDisposable
    {
        public const string RegistryTopic = EventHub.RegistryTopic;
        public const string MetricsTopic = EventHub.MetricsTopic;

        private readonly string _host;
        private readonly int _port;
        private SecureClientSideSocket? _socket;
        private readonly object _mirrorLock = new();
        private Registry? _registry;
        private long _registryIndex = -1;

        public Browser(string address)
        {
            var parts = address.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out _port)) throw new ArgumentException($"invalid address '{address}' (host:port)");
            _host = parts[0];
        }

        // The runtime this client is connected to, and the topics it publishes.
        public string? RuntimeId { get; private set; }
        public IReadOnlyList<string> Topics { get; private set; } = Array.Empty<string>();
        public bool Connected => _socket != null;

        // The mirrored Registry (null until the first snapshot) and the Raft log index it reflects. Read it inside
        // a handler, or under ReadRegistry from elsewhere (the reader updates it concurrently).
        public Registry? Registry => _registry;
        public long RegistryIndex => _registryIndex;
        public T ReadRegistry<T>(Func<Registry?, long, T> read) { lock (_mirrorLock) return read(_registry, _registryIndex); }

        public event Action<Registry, long>? RegistrySnapshot;
        public event Action<RegistryAction, long>? RegistryUpdated;
        public event Action<ResourceMetricsEvent>? ResourceMetrics;
        public event Action<Exception?>? Disconnected;

        // Connects and authenticates (throws when refused).
        public async Task ConnectAsync(string username, string password, CancellationToken ct = default)
        {
            if (_socket != null) throw new InvalidOperationException("already connected");
            var socket = new SecureClientSideSocket(_host, _port, _host, "", null, NullLogger<SecureClientSideSocket>.Instance, $"Browser client to {_host}:{_port}");
            await socket.ConnectAsync(3, ct).ConfigureAwait(false);
            await socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(new BrowserConnectRequest
            {
                MessageId = Guid.NewGuid(), SenderId = "browser/" + Environment.MachineName, Username = username, Password = password,
            })).ConfigureAwait(false);
            if (MessagePackSerializer.Deserialize<RuntimeMessage>(socket.Receive()) is not BrowserConnectResponse response)
            {
                await socket.StopAsync().ConfigureAwait(false);
                throw new InvalidOperationException("unexpected response from the runtime");
            }
            if (!response.Accepted)
            {
                await socket.StopAsync().ConfigureAwait(false);
                throw new UnauthorizedAccessException(response.Reason);
            }
            RuntimeId = response.RuntimeId;
            Topics = response.Topics;
            _socket = socket;
            socket.OnEnded += ex => { _socket = null; Disconnected?.Invoke(ex); };
            _ = socket.Listen(OnFrame);
        }

        public Task SubscribeAsync(params string[] topics) =>
            Send(new SubscribeRequest { MessageId = Guid.NewGuid(), Topics = topics.ToList() });

        public Task UnsubscribeAsync(params string[] topics) =>
            Send(new UnsubscribeRequest { MessageId = Guid.NewGuid(), Topics = topics.ToList() });

        private Task Send(RuntimeMessage message) =>
            (_socket ?? throw new InvalidOperationException("not connected")).Send(MessagePackSerializer.Serialize(message));

        private void OnFrame(byte[] frame)
        {
            switch (MessagePackSerializer.Deserialize<RuntimeMessage>(frame))
            {
                case RegistrySnapshotEvent snapshot:
                    var registry = MessagePackSerializer.Deserialize<Registry>(snapshot.Registry);
                    lock (_mirrorLock) { _registry = registry; _registryIndex = snapshot.Index; }
                    RegistrySnapshot?.Invoke(registry, snapshot.Index);
                    break;
                case RegistryUpdateEvent update:
                    lock (_mirrorLock)
                    {
                        if (_registry == null || update.Index <= _registryIndex) return;   // before or in the snapshot
                        _registry.Apply(update.Action);
                        _registryIndex = update.Index;
                    }
                    RegistryUpdated?.Invoke(update.Action, update.Index);
                    break;
                case ResourceMetricsEvent metrics:
                    ResourceMetrics?.Invoke(metrics);
                    break;
            }
        }

        public async ValueTask DisposeAsync()
        {
            var socket = _socket;
            _socket = null;
            if (socket != null) await socket.StopAsync().ConfigureAwait(false);
        }
    }
}
