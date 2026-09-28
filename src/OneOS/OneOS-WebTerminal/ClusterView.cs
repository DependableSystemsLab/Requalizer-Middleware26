using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OneOS.Client;
using OneOS.Runtime;

namespace OneOS.WebTerminal
{
    // The lists the client's Resource Monitor renders (field names match its sortable-table columns in app.js).
    public sealed record AgentDto(string uri, int pid, string runtime);
    public sealed record RuntimeDto(string uri, string status, int agents);
    public sealed record IoDto(string uri, string type, string driver, string runtime);
    public sealed record PipeDto(string key, string type, string? group, string? orderBy, string source, string sink);
    public sealed record SocketDto(int port, string owner, string hostRuntime);

    // One runtime's status, as seen through the metrics topic (a runtime that stops answering comes as Unreachable).
    public sealed record RuntimeStatus(string Id, bool Alive, DateTime LastSeen);

    // A per-user window onto the cluster, backed by one Browser connection (cluster-api.md §5.1: one Browser can
    // serve all of a user's pages). It keeps the Registry mirror the Browser maintains, tracks each runtime's
    // reachability from the metrics topic, and fans registry/metrics changes out to the user's Resource Monitor
    // sockets. Reconnects on Disconnected. Shared and reference-counted by ClusterViewManager.
    public sealed class ClusterView : IAsyncDisposable
    {
        private readonly string _address;
        private readonly string _username;
        private readonly string _password;
        private readonly ILogger _logger;

        private Browser? _browser;
        private readonly ConcurrentDictionary<string, RuntimeStatus> _runtimes = new();
        private volatile HashSet<string> _knownAgents = new();
        private readonly ConcurrentDictionary<Guid, Action<object>> _subscribers = new();
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private bool _disposed;

        public ClusterView(string address, string username, string password, ILogger logger)
        {
            _address = address;
            _username = username;
            _password = password;
            _logger = logger;
        }

        public async Task EnsureConnectedAsync(CancellationToken ct)
        {
            if (_browser is { Connected: true }) return;
            await _connectLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_browser is { Connected: true } || _disposed) return;
                var browser = new Browser(_address);
                browser.RegistryUpdated += (_, _) => EmitRegistryDiffs();
                browser.RegistrySnapshot += (_, _) => { _knownAgents = new(); EmitRegistryDiffs(); };
                browser.ResourceMetrics += OnMetrics;
                browser.Disconnected += OnDisconnected;
                await browser.ConnectAsync(_username, _password, ct).ConfigureAwait(false);
                await browser.SubscribeAsync(Browser.RegistryTopic, Browser.MetricsTopic).ConfigureAwait(false);
                _browser = browser;
                _logger.LogInformation("ClusterView for {User} connected to {Runtime}", _username, browser.RuntimeId);
            }
            finally { _connectLock.Release(); }
        }

        private void OnDisconnected(Exception? ex)
        {
            _browser = null;
            _logger.LogWarning(ex, "ClusterView for {User} disconnected", _username);
        }

        // Reading the mirror snapshot-style for the REST endpoints. Returns empty lists until the first snapshot.
        public IReadOnlyList<AgentDto> Agents() => Read((r, _) => r == null ? new() :
            r.Agents.Values.Select(a => new AgentDto(a.URI, a.GPID, a.Runtime)).ToList());

        public IReadOnlyList<IoDto> IO() => Read((r, _) => r == null ? new() :
            r.IO.Select(kv => new IoDto(kv.Key, kv.Value.DeviceType, kv.Value.Driver, kv.Value.HostRuntime)).ToList());

        public IReadOnlyList<PipeDto> Pipes() => Read((r, _) => r == null ? new() :
            r.Pipes.Values.Select(p => new PipeDto(p.Id, p.Strategy.ToString(), p.Graph, p.OrderBy,
                string.Join(", ", p.Sources), string.Join(", ", p.Sinks))).ToList());

        public IReadOnlyList<SocketDto> Sockets() => Read((r, _) => r == null ? new() :
            r.Sockets.Values.Select(s => new SocketDto(s.Port, s.Owner, s.HostRuntime)).ToList());

        // The runtimes: those seen through metrics (with liveness), unioned with those the Registry references, so the
        // list is populated even before the first metrics tick.
        public IReadOnlyList<RuntimeDto> Runtimes()
        {
            var counts = Read((r, _) => r?.Agents.Values.GroupBy(a => a.Runtime).ToDictionary(g => g.Key, g => g.Count()) ?? new());
            var ids = new HashSet<string>(_runtimes.Keys);
            ids.UnionWith(counts.Keys);
            Read((r, _) => { if (r != null) foreach (var id in r.HostEpochs.Keys) ids.Add(id); return 0; });
            ids.Remove("");
            return ids.Select(id => new RuntimeDto(id,
                _runtimes.TryGetValue(id, out var s) ? (s.Alive ? "Alive" : "Dead") : "Alive",
                counts.GetValueOrDefault(id, 0))).OrderBy(r => r.uri).ToList();
        }

        // The file-system node at an absolute cluster path (null if missing or before the first snapshot).
        public IFileSystemNode? ReadNode(string absolutePath) => Read((r, _) =>
        {
            if (r == null) return null;
            IFileSystemNode node = r.FileSystem;
            foreach (var segment in absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (node is not DirectoryNode dir || !dir.Children.TryGetValue(segment, out var child)) return null;
                node = child;
            }
            return node;
        });

        private T Read<T>(Func<Registry?, long, T> read)
        {
            var browser = _browser;
            return browser != null ? browser.ReadRegistry(read) : read(null, -1);
        }

        // --- fan-out to Resource Monitor sockets ---

        public IDisposable Subscribe(Action<object> onEvent)
        {
            var id = Guid.NewGuid();
            _subscribers[id] = onEvent;
            return new Unsubscriber(this, id);
        }

        private void Broadcast(object message)
        {
            foreach (var subscriber in _subscribers.Values)
            {
                try { subscriber(message); }
                catch (Exception ex) { _logger.LogDebug(ex, "ClusterView subscriber threw"); }
            }
        }

        // Diffs the mirror's agent set against what we last broadcast, emitting agent-join / agent-leave (the message
        // shapes the client's resource-monitor handles). Runs on the Browser's reader thread, one event at a time.
        private void EmitRegistryDiffs()
        {
            var current = Read((r, _) => r?.Agents.Values.ToDictionary(a => a.URI, a => new AgentDto(a.URI, a.GPID, a.Runtime)) ?? new());
            var previous = _knownAgents;
            foreach (var (uri, dto) in current)
                if (!previous.Contains(uri)) Broadcast(new { type = "agent-join", data = dto });
            foreach (var uri in previous)
                if (!current.ContainsKey(uri)) Broadcast(new { type = "agent-leave", data = uri });
            _knownAgents = new HashSet<string>(current.Keys);
            Broadcast(new { type = "registryUpdated" });
        }

        private void OnMetrics(ResourceMetricsEvent metrics)
        {
            bool alive = !metrics.Unreachable;
            var prev = _runtimes.TryGetValue(metrics.Runtime, out var s) ? s.Alive : (bool?)null;
            _runtimes[metrics.Runtime] = new RuntimeStatus(metrics.Runtime, alive, metrics.Time);
            if (prev != alive) Broadcast(new { type = alive ? "runtime-join" : "runtime-leave", data = metrics.Runtime });
        }

        private void Unsubscribe(Guid id) => _subscribers.TryRemove(id, out _);

        public async ValueTask DisposeAsync()
        {
            _disposed = true;
            var browser = _browser;
            _browser = null;
            if (browser != null) await browser.DisposeAsync().ConfigureAwait(false);
        }

        private sealed class Unsubscriber : IDisposable
        {
            private readonly ClusterView _view;
            private readonly Guid _id;
            public Unsubscriber(ClusterView view, Guid id) { _view = view; _id = id; }
            public void Dispose() => _view.Unsubscribe(_id);
        }
    }

    // Holds one ClusterView per signed-in username, reference-counted so it is created on first use and disposed when
    // the last page using it goes away.
    public sealed class ClusterViewManager
    {
        private readonly string _address;
        private readonly ILogger<ClusterViewManager> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly object _lock = new();
        private readonly Dictionary<string, Entry> _views = new();

        public ClusterViewManager(ClusterEndpoint endpoint, ILoggerFactory loggerFactory)
        {
            _address = endpoint.Address;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<ClusterViewManager>();
        }

        // A connected, reference-counted lease on the user's ClusterView. Dispose it when the caller is done.
        public async Task<Lease> AcquireAsync(WebSession session, CancellationToken ct)
        {
            Entry entry;
            lock (_lock)
            {
                if (!_views.TryGetValue(session.Username, out entry!))
                {
                    var view = new ClusterView(_address, session.Username, session.Password, _loggerFactory.CreateLogger<ClusterView>());
                    entry = new Entry(view);
                    _views[session.Username] = entry;
                }
                entry.RefCount++;
            }
            try
            {
                await entry.View.EnsureConnectedAsync(ct).ConfigureAwait(false);
                return new Lease(this, session.Username, entry.View);
            }
            catch
            {
                await ReleaseAsync(session.Username).ConfigureAwait(false);
                throw;
            }
        }

        private async Task ReleaseAsync(string username)
        {
            ClusterView? toDispose = null;
            lock (_lock)
            {
                if (_views.TryGetValue(username, out var entry) && --entry.RefCount <= 0)
                {
                    _views.Remove(username);
                    toDispose = entry.View;
                }
            }
            if (toDispose != null)
            {
                await toDispose.DisposeAsync().ConfigureAwait(false);
                _logger.LogInformation("ClusterView for {User} closed (no more pages)", username);
            }
        }

        private sealed class Entry
        {
            public Entry(ClusterView view) { View = view; }
            public ClusterView View { get; }
            public int RefCount;
        }

        public sealed class Lease : IAsyncDisposable
        {
            private readonly ClusterViewManager _manager;
            private readonly string _username;
            public Lease(ClusterViewManager manager, string username, ClusterView view) { _manager = manager; _username = username; View = view; }
            public ClusterView View { get; }
            public ValueTask DisposeAsync() => new(_manager.ReleaseAsync(_username));
        }
    }
}
