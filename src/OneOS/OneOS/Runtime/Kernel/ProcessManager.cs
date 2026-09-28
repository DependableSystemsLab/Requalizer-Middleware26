using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Common;

namespace OneOS.Runtime.Kernel
{
    public class ProcessManager : Agent
    {
        private readonly Runtime _runtime;
        private readonly Scheduler _scheduler;

        public Scheduler Scheduler => _scheduler;

        public ProcessManager(Runtime runtime, ILogger<ProcessManager> logger, Agent? parent = null)
            : base($"{runtime.Config.URI}/ProcessManager", logger, parent)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _scheduler = new Scheduler(_runtime);
        }

        public async Task<AgentInfo> SpawnProcessAgentAsync(string sessionKey, string language, IReadOnlyList<string> arguments, Dictionary<string, string>? environment = null, bool isForeground = true)
        {
            SessionInfo session = _runtime.Registry.Sessions[sessionKey];

            var envDict = new Dictionary<string, string>();
            if (environment != null)
            {
                foreach (var kvp in environment)
                {
                    envDict[kvp.Key] = kvp.Value;
                }
            }

            int gpid;
            do
            {
                gpid = Random.Shared.Next(10000, 99999);
            } while (_runtime.Registry.Agents.Values.Any(a => a.GPID == gpid));

            var uri = $"{session.User}.{_runtime.Config.Domain}/agents/{Guid.NewGuid()}";
            var agentInfo = new AgentInfo
            {
                URI = uri,
                GPID = gpid,
                Mode = AgentInfo.StartMode.New,
                Language = language.ToLowerInvariant() == "node" ? AgentInfo.LanguageInfo.JavaScript : AgentInfo.LanguageInfo.Python,
                User = session.User,
                Session = sessionKey,
                BinaryPath = language,
                Arguments = arguments.ToList(),
                Environment = envDict,
                OutputToShell = isForeground
            };
            _scheduler.AssignRuntime(agentInfo);

            var action = new SetAgentAction { Uri = uri, Info = agentInfo };
            var ok = await _runtime.UpdateRegistryAsync(action);
            if (ok)
            {
                return agentInfo;
            }
            throw new Exception("Failed to update registry to spawn agent.");
        }

        public async Task<List<AgentInfo>> SpawnPipelineAsync(string sessionKey, List<(string language, IReadOnlyList<string> arguments)> pipeline, Dictionary<string, string>? environment = null, bool isForeground = true)
        {
            SessionInfo session = _runtime.Registry.Sessions[sessionKey];
            var envDict = new Dictionary<string, string>();
            if (environment != null)
            {
                foreach (var kvp in environment) envDict[kvp.Key] = kvp.Value;
            }

            var graphId = Guid.NewGuid().ToString("N");
            var agents = new List<AgentInfo>();
            var actions = new List<RegistryAction>();

            for (int i = 0; i < pipeline.Count; i++)
            {
                int gpid;
                do { gpid = Random.Shared.Next(10000, 99999); } while (_runtime.Registry.Agents.Values.Any(a => a.GPID == gpid));

                var uri = $"{session.User}.{_runtime.Config.Domain}/agents/{Guid.NewGuid()}";
                var agentInfo = new AgentInfo
                {
                    URI = uri,
                    GPID = gpid,
                    Mode = AgentInfo.StartMode.New,
                    Language = pipeline[i].language.ToLowerInvariant() == "node" ? AgentInfo.LanguageInfo.JavaScript : AgentInfo.LanguageInfo.Python,
                    User = session.User,
                    Session = sessionKey,
                    Graph = graphId,
                    BinaryPath = pipeline[i].language,
                    Arguments = pipeline[i].arguments.ToList(),
                    Environment = envDict,
                    OutputToShell = isForeground && (i == pipeline.Count - 1)
                };
                
                _scheduler.AssignRuntime(agentInfo);
                agents.Add(agentInfo);
            }

            for (int i = 0; i < agents.Count - 1; i++)
            {
                var pipeId = Guid.NewGuid().ToString("N");
                var pipeInfo = new PipeInfo
                {
                    Id = pipeId,
                    Graph = graphId,
                    Sources = new List<string> { agents[i].URI },
                    Sinks = new List<string> { agents[i + 1].URI },
                    Strategy = PipeInfo.PipeStrategy.Direct
                };

                actions.Add(new SetPipeAction { Info = pipeInfo });
            }

            foreach (var a in agents)
            {
                actions.Add(new SetAgentAction { Uri = a.URI, Info = a });
            }

            var transaction = new TransactionAction { Actions = actions };
            var ok = await _runtime.UpdateRegistryAsync(transaction);
            
            if (ok)
            {
                return agents;
            }
            throw new Exception("Failed to update registry to spawn pipeline.");
        }
        public async Task KillAgentAsync(string uri)
        {
            var action = new DeleteAgentAction { Uri = uri };
            var ok = await _runtime.UpdateRegistryAsync(action);
            if (!ok)
            {
                throw new Exception($"Failed to update registry to kill agent {uri}.");
            }
        }


        protected override Task OnBeginAsync(CancellationToken ct)
        {
            _logger.LogInformation("ProcessManager starting...");
            return Task.CompletedTask;
        }

        protected override Task OnPauseAsync(CancellationToken ct)
        {
            _logger.LogInformation("ProcessManager pausing...");
            return Task.CompletedTask;
        }

        protected override Task OnEndAsync(CancellationToken ct)
        {
            _logger.LogInformation("ProcessManager stopping...");
            return Task.CompletedTask;
        }

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in Inbox.Reader.ReadAllAsync(ct))
                {
                    _logger.LogInformation("ProcessManager received message.");
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
