using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace OneOS.WebTerminal
{
    // A signed-in browser. The cluster has no tokens (every connection sends the password), so the session keeps the
    // credentials for as long as it lives (cluster-api.md §8). It is identified by a random token in a cookie.
    public sealed class WebSession
    {
        public required string Token { get; init; }
        public required string Username { get; init; }
        public required string Password { get; init; }
        public DateTime Created { get; init; } = DateTime.UtcNow;
        public DateTime Expires { get; init; }
    }

    // The WebTerminal's sessions, in memory (a server restart signs everyone out).
    public sealed class SessionStore : IDisposable
    {
        public const string CookieName = "oneos-session";
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

        private readonly ConcurrentDictionary<string, WebSession> _sessions = new();
        private readonly ILogger _logger;
        private readonly Timer _sweeper;

        // Raised (fire-and-forget) when a session is removed by logout or expiry, so its cluster session can be torn
        // down. Set once at startup.
        public Func<string, Task>? SessionRemoved { get; set; }

        public SessionStore(ILogger<SessionStore> logger)
        {
            _logger = logger;
            _sweeper = new Timer(_ => RemoveExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public WebSession Create(string username, string password)
        {
            var session = new WebSession
            {
                Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                Username = username,
                Password = password,
                Expires = DateTime.UtcNow + MaxAge,
            };
            _sessions[session.Token] = session;
            _logger.LogInformation("{User} signed in ({Count} active sessions)", username, _sessions.Count);
            return session;
        }

        // The request's session, or null when it has none (no cookie, unknown token, or expired).
        public WebSession? Find(HttpContext ctx)
        {
            if (!ctx.Request.Cookies.TryGetValue(CookieName, out var token) || token == null) return null;
            if (!_sessions.TryGetValue(token, out var session)) return null;
            if (session.Expires < DateTime.UtcNow) { Drop(token); return null; }
            return session;
        }

        public void Remove(WebSession session)
        {
            if (_sessions.TryRemove(session.Token, out _))
            {
                _logger.LogInformation("{User} signed out ({Count} active sessions)", session.Username, _sessions.Count);
                Notify(session.Token);
            }
        }

        private void Drop(string token)
        {
            if (_sessions.TryRemove(token, out _)) Notify(token);
        }

        private void Notify(string token)
        {
            var handler = SessionRemoved;
            if (handler != null) _ = handler(token);
        }

        public static void SetCookie(HttpContext ctx, WebSession session) =>
            ctx.Response.Cookies.Append(CookieName, session.Token, new CookieOptions
            {
                HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = MaxAge,
            });

        public static void ClearCookie(HttpContext ctx) => ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });

        private void RemoveExpired()
        {
            var now = DateTime.UtcNow;
            int removed = 0;
            foreach (var (token, session) in _sessions)
                if (session.Expires < now && _sessions.TryRemove(token, out _)) { removed++; Notify(token); }
            if (removed > 0) _logger.LogInformation("{Removed} sessions expired ({Count} active sessions)", removed, _sessions.Count);
        }

        public void Dispose() => _sweeper.Dispose();
    }
}
