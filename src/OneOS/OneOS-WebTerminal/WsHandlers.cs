using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OneOS.WebTerminal
{
    // The /ws connection handlers. Registered with the broker in Program.cs.
    public sealed class WsHandlers
    {
        private readonly ClusterSessionManager _clusterSessions;
        private readonly ILogger<WsHandlers> _logger;

        public WsHandlers(ClusterSessionManager clusterSessions, ILogger<WsHandlers> logger)
        {
            _clusterSessions = clusterSessions;
            _logger = logger;
        }

        // { connection: "UserShell" } — a terminal tab. Attaches to the browser user's persistent cluster shell (the
        // one created at login): buffered output is replayed, then new output streams as { line } (the client splits
        // it on newlines) and each { line } the page sends is one command line. Closing the tab detaches but leaves
        // the shell running (it ends at logout); cluster-api.md §4.
        public async Task UserShellAsync(ClientSocket socket, JsonElement request, CancellationToken ct)
        {
            var live = _clusterSessions.Get(socket.Session.Token);
            if (live == null || live.Ended)
            {
                await socket.SendJsonAsync(new { line = "The cluster session is no longer available. Please sign in again.\n" }, ct);
                return;
            }

            // Stream output (replaying scrollback). Fire-and-forget onto the socket's own send lock.
            using var attach = live.AttachOutput(text => { _ = socket.SendJsonAsync(new { line = text }, ct); });

            while (!ct.IsCancellationRequested)
            {
                var message = await socket.ReceiveJsonAsync(ct);
                if (message == null) break;   // page closed the tab (shell stays alive)
                if (!message.Value.TryGetProperty("line", out var lineProp) || lineProp.ValueKind != JsonValueKind.String) continue;
                var line = lineProp.GetString()!;
                if (line == "\u0003") await live.InterruptAsync();
                else await live.SendLineAsync(line);
            }
        }

        // { connection: "ResourceMonitor" } — the Resource Monitor's live view. The page first fetches the lists over
        // REST, then keeps this socket open for deltas (agent-join/leave, runtime-join/leave, registryUpdated). It
        // uses the browser user's Browser session established at login.
        public async Task ResourceMonitorAsync(ClientSocket socket, JsonElement request, CancellationToken ct)
        {
            var live = _clusterSessions.Get(socket.Session.Token);
            if (live == null || live.Ended) return;
            using var subscription = live.View.Subscribe(message => { _ = socket.SendJsonAsync(message, ct); });

            // Nothing more is expected from the page; hold the socket open until it closes.
            while (!ct.IsCancellationRequested)
            {
                var message = await socket.ReceiveTextAsync(ct);
                if (message == null) break;
            }
        }
    }
}
