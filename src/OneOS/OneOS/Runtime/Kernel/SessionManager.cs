using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Common;
using System.Linq;

namespace OneOS.Runtime.Kernel
{
    public class SessionManager : Agent
    {
        private readonly Runtime _runtime;
        private static readonly SHA256 _sha256 = SHA256.Create();
        
        public SessionManager(Runtime runtime, ILogger<SessionManager> logger, Agent? parent = null)
            : base($"{runtime.Config.URI}/SessionManager", logger, parent)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        // Adapted from OneOS-V5 CheckPassword
        private static bool CheckPassword(string password, string saltedHash)
        {
            if (string.IsNullOrEmpty(saltedHash) || saltedHash.Length < 20) return false;
            
            var salt = saltedHash.Substring(0, 20);
            var payload = Encoding.UTF8.GetBytes(password + salt);
            var hash = Convert.ToBase64String(_sha256.ComputeHash(payload));
            return (salt + hash == saltedHash);
        }

        private string GenerateSessionToken()
        {
            return Guid.NewGuid().ToString("N");
        }

        internal async Task<SessionInfo> GetSessionAsync(string sessionKey)
        {
            int retries = 10;
            while (!_runtime.Registry.Sessions.ContainsKey(sessionKey) && retries > 0)
            {
                await Task.Delay(20);
                retries--;
            }

            if (!_runtime.Registry.Sessions.ContainsKey(sessionKey))
            {
                throw new Exception($"Could not find session {sessionKey} in the Registry");
            }

            return _runtime.Registry.Sessions[sessionKey];
        }

        private string GenerateShellUri(string username)
        {
            var key = Guid.NewGuid().ToString("N").Substring(0,8);
            return $"{username}.{_runtime.Config.Domain}/shell/{key}";
        }

        public bool AuthenticateUser(string username, string password)
        {
            if (_runtime.Registry.Users.TryGetValue(username, out var storedPasswordHash))
            {
                if (CheckPassword(password, storedPasswordHash))
                {
                    return true;
                }
            }
            return false;
        }

        public async Task<string?> SignInUser(string username, string password, string clientUri, CancellationToken ct = default)
        {
            if (!AuthenticateUser(username, password))
            {
                _logger.LogWarning("SignInUser failed for {Username}: invalid credentials.", username);
                throw new UnauthorizedAccessException($"Invalid credentials");
            }

            var sessionToken = GenerateSessionToken();
            var shellUri = GenerateShellUri(username);
            var info = new SessionInfo { User = username, ShellUri = shellUri, ClientUri = clientUri };
            var action = new SetSessionAction { SessionToken = sessionToken, Info = info };
            
            bool success = await _runtime.UpdateRegistryAsync(action, ct);
            if (!success)
            {
                _logger.LogError("Failed to update registry with new session for {Username}", username);
                throw new Exception($"Failed to update registry with new session for {username}");
            }

            _logger.LogInformation("User {Username} signed in with session {SessionToken}", username, sessionToken);
            return sessionToken;
        }

        public async Task<bool> SignOutUser(string sessionToken, CancellationToken ct = default)
        {
            var action = new DeleteSessionAction { SessionToken = sessionToken };
            bool success = await _runtime.UpdateRegistryAsync(action, ct);
            if (success)
            {
                _logger.LogInformation("Session {SessionToken} signed out.", sessionToken);
            }
            else
            {
                _logger.LogWarning("Failed to sign out session {SessionToken}.", sessionToken);
            }
            return success;
        }

        public async Task<string> CreateUserShell(string sessionKey, CancellationToken ct = default)
        {
            var session = await GetSessionAsync(sessionKey);
            int gpid;
            do
            {
                gpid = Random.Shared.Next(10000, 99999);
            } while (_runtime.Registry.Agents.Values.Any(a => a.GPID == gpid));
            
            var agentInfo = new AgentInfo
            {
                URI = session.ShellUri,
                GPID = gpid,
                Mode = AgentInfo.StartMode.New,
                Language = AgentInfo.LanguageInfo.CSharp,
                User = session.User,
                Session = sessionKey,
                BinaryPath = "UserShell", // Identifies the shell agent type
                Runtime = _runtime.Config.ID
            };

            var action = new SetAgentAction { Uri = session.ShellUri, Info = agentInfo };
            bool success = await _runtime.UpdateRegistryAsync(action, ct);
            
            if (success)
            {
                _logger.LogInformation("Created UserShell for {Username} at {URI}", session.User, session.ShellUri);
            }
            else
            {
                _logger.LogError("Failed to create UserShell for {Username} in the registry.", session.User);
            }
            
            return session.ShellUri;
        }

        protected override Task OnBeginAsync(CancellationToken ct)
        {
            _logger.LogInformation("SessionManager starting...");
            return Task.CompletedTask;
        }

        protected override Task OnPauseAsync(CancellationToken ct)
        {
            _logger.LogInformation("SessionManager pausing...");
            return Task.CompletedTask;
        }

        protected override Task OnEndAsync(CancellationToken ct)
        {
            _logger.LogInformation("SessionManager stopping...");
            return Task.CompletedTask;
        }

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in Inbox.Reader.ReadAllAsync(ct))
                {
                    _logger.LogInformation("SessionManager received message.");
                    // Process message...
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful cancellation
            }
        }
    }
}
