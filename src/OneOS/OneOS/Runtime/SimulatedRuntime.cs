using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OneOS.Common;

namespace OneOS.Runtime
{
    public class SimulatedRuntime : IVirtualRuntime, IClusterProvider
    {
        public Agent Client { get; } = null!;
        public string HostRuntimeID { get; } = "sim-host";
        public string Session { get; } = "sim-session";
        public string User { get; } = "sim-user";
        public string ShellUri { get; } = "sim-shell";

        public event Action<RegistryAction>? OnRegistryUpdated { add {} remove {} }

        private Dictionary<string, AgentInfo> _agents = new();

        public IReadOnlyDictionary<string, AgentInfo> GetAgents() => _agents;

        public Task<AgentInfo> SpawnProcessAgentAsync(string sessionKey, string language, IReadOnlyList<string> arguments, Dictionary<string, string>? environment = null, bool isForeground = true)
        {
            Console.WriteLine($"[SimulatedRuntime] Spawning {language} agent: {arguments}");
            var info = new AgentInfo 
            { 
                URI = $"agent-{Guid.NewGuid().ToString()[..8]}", 
                Language = Enum.TryParse<AgentInfo.LanguageInfo>(language, true, out var l) ? l : AgentInfo.LanguageInfo.JavaScript, 
                Arguments = arguments.ToList(), 
                Session = sessionKey 
            };
            _agents[info.URI] = info;
            return Task.FromResult(info);
        }

        public Task<List<AgentInfo>> SpawnPipelineAsync(string sessionKey, List<(string language, IReadOnlyList<string> arguments)> pipeline, Dictionary<string, string>? environment = null, bool isForeground = true)
        {
            var list = new List<AgentInfo>();
            foreach (var step in pipeline)
            {
                list.Add(SpawnProcessAgentAsync(sessionKey, step.language, step.arguments, environment, isForeground).Result);
            }
            return Task.FromResult(list);
        }

        public Task KillAgentAsync(string uri)
        {
            Console.WriteLine($"[SimulatedRuntime] Killing agent: {uri}");
            _agents.Remove(uri);
            return Task.CompletedTask;
        }

        // Dummy implementations for the rest
        public Task<string?> SignInUser(string username, string password, string clientUri) => Task.FromResult<string?>("sim-session");
        public Task<bool> SignOutUser(string sessionToken) => Task.FromResult(true);
        public IReadOnlyDictionary<string, SessionInfo> GetSessions() => new Dictionary<string, SessionInfo>();

        public bool FileSystemNodeExists(string absolutePath) => true;
        public bool IsDirectory(string absolutePath) => false;
        public bool IsFile(string absolutePath) => true;
        public IFileSystemNode? GetFileSystemNode(string absolutePath) => null;
        public DirectoryNode ReadDirectory(string absolutePath) => new DirectoryNode();
        public FileNode ReadFileInfo(string absolutePath) => new FileNode { Size = 0 };
        public Task<FileNode> CreateFile(string absolutePath, bool throwErrorIfExists = true) => Task.FromResult(new FileNode { Size = 0 });
        public Task<DirectoryNode> CreateDirectory(string absolutePath, bool throwErrorIfExists = true) => Task.FromResult(new DirectoryNode());
        public Task<IFileSystemNode> RemoveFileSystemNode(string absolutePath) => Task.FromResult<IFileSystemNode>(new FileNode { Size = 0 });
        public Task<IFileSystemNode> MoveFileSystemNode(string srcAbsolutePath, string dstAbsolutePath) => Task.FromResult<IFileSystemNode>(new FileNode { Size = 0 });
        public Task<IFileSystemNode> CopyFileSystemNode(string srcAbsolutePath, string dstAbsolutePath) => Task.FromResult<IFileSystemNode>(new FileNode { Size = 0 });
        public Task<string> DownloadFile(string webURL, string directory, string? saveName = null) => Task.FromResult("sim-file");
        public Task CreateFileReadStreamAsync(string filePath, string directory, Pipe outputPipe) => Task.CompletedTask;
        public Task CreateFileWriteStreamAsync(string filePath, string directory, Pipe inputPipe) => Task.CompletedTask;
        public Task<byte[]> ReadFileAsync(string filePath, string directory) => Task.FromResult(Array.Empty<byte>());
        public Task WriteFileAsync(string filePath, string directory, byte[] data) => Task.CompletedTask;

        public Task<Agent?> WaitForAgentAsync(string uri, TimeSpan timeout) => Task.FromResult<Agent?>(null);
        public Task PauseAgentAsync(string uri) => Task.CompletedTask;
        public Task StopAgentAsync(string uri) => Task.CompletedTask;

        private ClusterSnapshot _snapshot = ClusterSnapshot.Empty;
        private readonly Dictionary<string, Scheduling.GraphInstanceInfo> _graphs = new();

        public Scheduling.SchedulerOptions SchedulerOptions { get; set; } = new();
        public Scheduling.IProfileStore Profiles { get; set; } = new Scheduling.InMemoryProfileStore();

        public void InjectSnapshot(ClusterSnapshot snapshot) => _snapshot = snapshot;

        public ClusterSnapshot TakeSnapshot() => _snapshot;

        public IReadOnlyDictionary<string, Scheduling.GraphInstanceInfo> GetGraphInstances() => _graphs;

        public Task StopGraph(string graphInstanceId, System.Threading.CancellationToken ct = default)
        {
            _graphs.Remove(graphInstanceId);
            return Task.CompletedTask;
        }

        // The simulated cluster is injected (InjectSnapshot), so there is no cluster-info exchange.
        public Task<Scheduling.GraphInstanceInfo> PlanGraph(OneOS.Runtime.Language.Models.CompiledGraph graph, IReadOnlyList<object?> args, System.Threading.CancellationToken ct = default) =>
            Task.FromResult(new Scheduling.GraphScheduler(this, SchedulerOptions, Profiles).PlanGraph(graph, args, ct));

        // Simulated phase 6: record the instance and mark every primary as running. Standbys aren't started.
        public Task<Scheduling.GraphInstanceHandle> SpawnGraph(OneOS.Runtime.Language.Models.CompiledGraph graph, IReadOnlyList<object?> args, System.Threading.CancellationToken ct = default)
        {
            var plan = new Scheduling.GraphScheduler(this, SchedulerOptions, Profiles).PlanGraph(graph, args, ct);
            var running = plan with
            {
                Agents = plan.Agents.Select(a => a with { State = a.Role == Scheduling.AgentRole.Primary ? Scheduling.AgentState.Running : Scheduling.AgentState.Pending }).ToList(),
                State = Scheduling.GraphInstanceState.Running,
            };
            _graphs[running.GraphInstanceId] = running;
            return Task.FromResult(new Scheduling.GraphInstanceHandle(running));
        }
    }
}