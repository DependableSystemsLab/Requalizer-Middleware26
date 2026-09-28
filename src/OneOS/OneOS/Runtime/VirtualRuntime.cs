using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OneOS.Common;

namespace OneOS.Runtime
{
    public class VirtualRuntime : IVirtualRuntime
    {
        private readonly Runtime _runtime;
        private string _sessionKey;
        public Agent Client { get; }

        public string HostRuntimeID => _runtime.Config.ID;

        public event Action<RegistryAction>? OnRegistryUpdated
        {
            add => _runtime.OnRegistryUpdated += value;
            remove => _runtime.OnRegistryUpdated -= value;
        }

        public string Session => _sessionKey;
        public string User => _runtime.Registry.Sessions[_sessionKey].User;
        public string ShellUri => _runtime.Registry.Sessions[_sessionKey].ShellUri;

        public VirtualRuntime(Runtime runtime, string sessionKey, Agent client)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _sessionKey = sessionKey;
            Client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public Task<string?> SignInUser(string username, string password, string clientUri) => 
            _runtime.SessionManager.SignInUser(username, password, clientUri);
            
        public Task<bool> SignOutUser(string sessionToken) => 
            _runtime.SessionManager.SignOutUser(sessionToken);

        public IReadOnlyDictionary<string, SessionInfo> GetSessions() => 
            _runtime.Registry.Sessions;

        public bool FileSystemNodeExists(string absolutePath) => 
            _runtime.FileSystemManager.FileSystemNodeExists(absolutePath);
            
        public bool IsDirectory(string absolutePath) => 
            _runtime.FileSystemManager.IsDirectory(absolutePath);
            
        public bool IsFile(string absolutePath) => 
            _runtime.FileSystemManager.IsFile(absolutePath);
            
        public IFileSystemNode? GetFileSystemNode(string absolutePath) => 
            _runtime.FileSystemManager.GetFileSystemNode(absolutePath);
            
        public DirectoryNode ReadDirectory(string absolutePath) => 
            _runtime.FileSystemManager.ReadDirectory(absolutePath);
            
        public FileNode ReadFileInfo(string absolutePath) => 
            _runtime.FileSystemManager.ReadFileInfo(absolutePath);
            
        public Task<FileNode> CreateFile(string absolutePath, bool throwErrorIfExists = true) => 
            _runtime.FileSystemManager.CreateFileAsync(absolutePath, throwErrorIfExists);
            
        public Task<DirectoryNode> CreateDirectory(string absolutePath, bool throwErrorIfExists = true) => 
            _runtime.FileSystemManager.CreateDirectoryAsync(absolutePath, throwErrorIfExists);
            
        public Task<IFileSystemNode> RemoveFileSystemNode(string absolutePath) => 
            _runtime.FileSystemManager.RemoveFileSystemNodeAsync(absolutePath);
            
        public Task<IFileSystemNode> MoveFileSystemNode(string srcAbsolutePath, string dstAbsolutePath) => 
            _runtime.FileSystemManager.MoveFileSystemNodeAsync(srcAbsolutePath, dstAbsolutePath);
            
        public Task<IFileSystemNode> CopyFileSystemNode(string srcAbsolutePath, string dstAbsolutePath) => 
            _runtime.FileSystemManager.CopyFileSystemNodeAsync(srcAbsolutePath, dstAbsolutePath);
            
        public Task<string> DownloadFile(string webURL, string directory, string? saveName = null) => 
            _runtime.FileSystemManager.DownloadFile(webURL, directory, saveName);
            
        public Task CreateFileReadStreamAsync(string filePath, string directory, Pipe outputPipe) => 
            _runtime.FileSystemManager.CreateFileReadStreamAsync(filePath, directory, outputPipe);

        public Task CreateFileWriteStreamAsync(string filePath, string directory, Pipe inputPipe) => 
            _runtime.FileSystemManager.CreateFileWriteStreamAsync(filePath, directory, inputPipe);

        public Task<byte[]> ReadFileAsync(string filePath, string directory) => 
            _runtime.FileSystemManager.ReadFileAsync(filePath, directory);

        public Task WriteFileAsync(string filePath, string directory, byte[] data) => 
            _runtime.FileSystemManager.WriteFileAsync(filePath, directory, data);

        public IReadOnlyDictionary<string, AgentInfo> GetAgents() => 
            _runtime.Registry.Agents;

        public Task<Agent?> WaitForAgentAsync(string uri, TimeSpan timeout) => 
            _runtime.ExecutionManager.WaitForAgentAsync(uri, timeout);
            
        public Task PauseAgentAsync(string uri) => 
            _runtime.ExecutionManager.PauseAgentAsync(uri);
            
        public Task StopAgentAsync(string uri) => 
            _runtime.ExecutionManager.StopAgentAsync(uri);

        public Task<AgentInfo> SpawnProcessAgentAsync(string sessionKey, string language, IReadOnlyList<string> arguments, Dictionary<string, string>? environment = null, bool isForeground = true) =>
            _runtime.ProcessManager.SpawnProcessAgentAsync(sessionKey, language, arguments, environment, isForeground);

        public Task<List<AgentInfo>> SpawnPipelineAsync(string sessionKey, List<(string language, IReadOnlyList<string> arguments)> pipeline, Dictionary<string, string>? environment = null, bool isForeground = true) =>
            _runtime.ProcessManager.SpawnPipelineAsync(sessionKey, pipeline, environment, isForeground);

        public Task KillAgentAsync(string uri) =>
            _runtime.ProcessManager.KillAgentAsync(uri);

        public Task<Scheduling.GraphInstanceInfo> PlanGraph(Language.Models.CompiledGraph graph, IReadOnlyList<object?> args, System.Threading.CancellationToken ct = default) =>
            _runtime.GraphManager.PlanAsync(graph, args, ct);

        public async Task<Scheduling.GraphInstanceHandle> SpawnGraph(Language.Models.CompiledGraph graph, IReadOnlyList<object?> args, System.Threading.CancellationToken ct = default) =>
            new Scheduling.GraphInstanceHandle(await _runtime.GraphManager.SpawnAsync(graph, args, ct,
                _runtime.Registry.Sessions.TryGetValue(_sessionKey, out var session) ? session.User : null).ConfigureAwait(false));

        public Task StopGraph(string graphInstanceId, System.Threading.CancellationToken ct = default) =>
            _runtime.GraphManager.StopAsync(graphInstanceId, ct);

        public IReadOnlyDictionary<string, Scheduling.GraphInstanceInfo> GetGraphInstances() => _runtime.GraphManager.Instances;

        public ClusterSnapshot TakeSnapshot() => _runtime.TakeSnapshot();
    }
}
