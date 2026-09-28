using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OneOS.WebTerminal
{
    // Where the WebTerminal reaches the cluster (a runtime's address). No credentials: those come from each browser
    // user's login (cluster-api.md §8), so the server starts without any.
    public sealed class ClusterEndpoint
    {
        public ClusterEndpoint(string address) { Address = address; }
        public string Address { get; }
    }

    // The cluster-side state of one signed-in browser user, established at login and kept alive until logout or the
    // web session expires:
    //   - a persistent Terminal session (a UserShell): this is the session recorded in the Registry, and having it is
    //     what makes the user "authenticated" (per the design). Terminal tabs attach to this one shell.
    //   - a Browser (pub-sub) lease for the Resource Monitor and the /runtime and /fs views.
    public sealed class LiveSession : IAsyncDisposable
    {
        private readonly TerminalSession _terminal;
        private readonly ClusterViewManager.Lease _viewLease;
        private bool _ended;

        public LiveSession(TerminalSession terminal, ClusterViewManager.Lease viewLease)
        {
            _terminal = terminal;
            _viewLease = viewLease;
            terminal.Ended += _ => _ended = true;
        }

        public ClusterView View => _viewLease.View;
        public bool Ended => _ended;

        // Attaches a terminal tab: replays the buffered scrollback (welcome banner and prompt included), then streams
        // new output until disposed.
        public IDisposable AttachOutput(Action<string> onOutput) => _terminal.AttachOutput(onOutput);

        public Task SendLineAsync(string line) => _terminal.SendLineAsync(line);
        public Task InterruptAsync() => _terminal.InterruptAsync();

        public async ValueTask DisposeAsync()
        {
            // End the Registry session cleanly (`exit` ends the shell and its job, §4.2), then release the Browser lease.
            try { await _terminal.SendLineAsync("exit"); } catch { /* already gone */ }
            await _terminal.DisposeAsync().ConfigureAwait(false);
            await _viewLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Holds the LiveSession for each signed-in browser user, keyed by the web-session token (which is also the
    // terminal session's SenderId, so a reconnect reattaches to the same shell).
    public sealed class ClusterSessionManager
    {
        private readonly string _address;
        private readonly ClusterViewManager _views;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<ClusterSessionManager> _logger;
        private readonly ConcurrentDictionary<string, LiveSession> _sessions = new();

        public ClusterSessionManager(ClusterEndpoint endpoint, ClusterViewManager views, ILoggerFactory loggerFactory)
        {
            _address = endpoint.Address;
            _views = views;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<ClusterSessionManager>();
        }

        // Establishes the cluster session for a freshly created web session: a Terminal session (the Registry session)
        // and a Browser lease, both with the login credentials. Returns null on success, or the refusal reason to show
        // on the login page. On failure nothing is left behind.
        public async Task<string?> SignInAsync(WebSession session, CancellationToken ct)
        {
            var terminal = new TerminalSession(_address, "webterminal/" + session.Token, _loggerFactory.CreateLogger<TerminalSession>());
            var reason = await terminal.ConnectAsync(session.Username, session.Password, command: null, ct);
            if (reason != null)
            {
                await terminal.DisposeAsync().ConfigureAwait(false);
                return TranslateReason(reason);
            }

            ClusterViewManager.Lease viewLease;
            try
            {
                viewLease = await _views.AcquireAsync(session, ct);
            }
            catch (Exception ex)
            {
                await terminal.DisposeAsync().ConfigureAwait(false);
                _logger.LogWarning(ex, "Browser session for {User} could not be established", session.Username);
                return $"Could not establish the cluster session ({ex.Message})";
            }

            var live = new LiveSession(terminal, viewLease);
            _sessions[session.Token] = live;
            _logger.LogInformation("Cluster session established for {User} (token {Token})", session.Username, session.Token[..8]);
            return null;
        }

        public LiveSession? Get(string token) => _sessions.TryGetValue(token, out var s) ? s : null;

        public async Task RemoveAsync(string token)
        {
            if (_sessions.TryRemove(token, out var live))
                await live.DisposeAsync().ConfigureAwait(false);
        }

        private static string TranslateReason(string reason) => reason switch
        {
            "InvalidCredentials" => "Invalid username or password",
            "ShellSpawnTimeout" => "The cluster timed out starting your shell; try again",
            "ShellAgentNotRunning" or "ShellAgentNotFound" => "Your shell could not be started",
            "HostRuntimeNotFound" => "The runtime hosting your session is unavailable",
            _ when reason.Contains("connect", StringComparison.OrdinalIgnoreCase) => $"Could not reach the cluster ({reason})",
            _ => $"Sign-in failed: {reason}",
        };
    }
}
