using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OneOS.Common;

namespace OneOS.Runtime
{
    public interface IVirtualRuntime
    {
        Agent Client { get; }

        string HostRuntimeID { get; }
        public string Session { get; }
        public string User { get; }
        public string ShellUri { get; }
        
        event Action<RegistryAction>? OnRegistryUpdated;

        // Session
        Task<string?> SignInUser(string username, string password, string clientUri);
        Task<bool> SignOutUser(string sessionToken);
        IReadOnlyDictionary<string, SessionInfo> GetSessions();

        // FileSystem
        bool FileSystemNodeExists(string absolutePath);
        bool IsDirectory(string absolutePath);
        bool IsFile(string absolutePath);
        IFileSystemNode? GetFileSystemNode(string absolutePath);
        DirectoryNode ReadDirectory(string absolutePath);
        FileNode ReadFileInfo(string absolutePath);
        Task<FileNode> CreateFile(string absolutePath, bool throwErrorIfExists = true);
        Task<DirectoryNode> CreateDirectory(string absolutePath, bool throwErrorIfExists = true);
        Task<IFileSystemNode> RemoveFileSystemNode(string absolutePath);
        Task<IFileSystemNode> MoveFileSystemNode(string srcAbsolutePath, string dstAbsolutePath);
        Task<IFileSystemNode> CopyFileSystemNode(string srcAbsolutePath, string dstAbsolutePath);
        Task<string> DownloadFile(string webURL, string directory, string? saveName = null);
        Task CreateFileReadStreamAsync(string filePath, string directory, Pipe outputPipe);
        Task CreateFileWriteStreamAsync(string filePath, string directory, Pipe inputPipe);
        Task<byte[]> ReadFileAsync(string filePath, string directory);
        Task WriteFileAsync(string filePath, string directory, byte[] data);

        // Execution
        IReadOnlyDictionary<string, AgentInfo> GetAgents();
        Task<Agent?> WaitForAgentAsync(string uri, TimeSpan timeout);
        Task PauseAgentAsync(string uri);
        Task StopAgentAsync(string uri);
        Task<AgentInfo> SpawnProcessAgentAsync(string username, string language, IReadOnlyList<string> arguments, Dictionary<string, string>? environment = null, bool isForeground = true);
        Task<List<AgentInfo>> SpawnPipelineAsync(string sessionKey, List<(string language, IReadOnlyList<string> arguments)> pipeline, Dictionary<string, string>? environment = null, bool isForeground = true);
        Task KillAgentAsync(string uri);
        
        // Dataflow graphs (scheduling-spec §1.3, §12). SpawnGraph binds `args` (unless the graph is
        // already bound), plans, and commits; PlanGraph stops before committing. Both throw
        // Scheduling.SchedulingException with an SP-code on failure.
        Task<Scheduling.GraphInstanceHandle> SpawnGraph(Language.Models.CompiledGraph graph, IReadOnlyList<object?> args, System.Threading.CancellationToken ct = default);
        Task<Scheduling.GraphInstanceInfo> PlanGraph(Language.Models.CompiledGraph graph, IReadOnlyList<object?> args, System.Threading.CancellationToken ct = default);
        Task StopGraph(string graphInstanceId, System.Threading.CancellationToken ct = default);
        IReadOnlyDictionary<string, Scheduling.GraphInstanceInfo> GetGraphInstances();
        ClusterSnapshot TakeSnapshot();
    }
}
