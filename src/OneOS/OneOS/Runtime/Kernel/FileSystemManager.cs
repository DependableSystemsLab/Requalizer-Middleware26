using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Common;

namespace OneOS.Runtime.Kernel
{
    public class FileSystemManager : Agent
    {
        private readonly Runtime _runtime;
        private readonly AgentRpc.Client _rpcClient;

        public FileSystemManager(Runtime runtime, ILogger<FileSystemManager> logger)
            : base($"{runtime.Config.URI}/FileSystemManager", logger)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _rpcClient = new AgentRpc.Client(this);
        }

        public bool FileSystemNodeExists(string absolutePath)
        {
            return _runtime.Registry.GetNode(absolutePath) != null;
        }

        public bool IsDirectory(string absolutePath)
        {
            return _runtime.Registry.GetNode(absolutePath) is DirectoryNode;
        }

        public bool IsFile(string absolutePath)
        {
            return _runtime.Registry.GetNode(absolutePath) is FileNode;
        }

        public IFileSystemNode? GetFileSystemNode(string absolutePath)
        {
            return _runtime.Registry.GetNode(absolutePath);
        }

        public bool TryGetFileSystemNode(string absolutePath, out IFileSystemNode? info)
        {
            info = _runtime.Registry.GetNode(absolutePath);
            return info != null;
        }

        public DirectoryNode ReadDirectory(string absolutePath)
        {
            if (_runtime.Registry.GetNode(absolutePath) is DirectoryNode dir)
            {
                return dir;
            }
            throw new InvalidOperationException($"{absolutePath} is not a directory");
        }

        public FileNode ReadFileInfo(string absolutePath)
        {
            if (_runtime.Registry.GetNode(absolutePath) is FileNode file)
            {
                return file;
            }
            throw new InvalidOperationException($"{absolutePath} is not a file");
        }

        public async Task<FileNode> CreateFileAsync(string absolutePath, bool throwErrorIfExists = true)
        {
            if (FileSystemNodeExists(absolutePath))
            {
                if (throwErrorIfExists)
                {
                    throw new InvalidOperationException($"{absolutePath} already exists");
                }
                else
                {
                    var existingNode = GetFileSystemNode(absolutePath);
                    if (existingNode is FileNode existingFile) return existingFile;
                    throw new InvalidOperationException($"{absolutePath} exists but is not a file");
                }
            }

            var file = new FileNode();
            
            // Replicating to local runtime
            file.Copies[_runtime.Config.ID] = Guid.NewGuid().ToString("N");
            
            // Replicating to random peers to reach a total of 3 replicas if possible
            var availablePeers = _runtime.Config.Peers.Keys.ToList();
            var random = new Random();
            
            // Fisher-Yates shuffle
            for (int i = availablePeers.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (availablePeers[i], availablePeers[j]) = (availablePeers[j], availablePeers[i]);
            }
            
            int additionalReplicasNeeded = 2; // We want 3 total (local + 2 peers)
            int peersToTake = Math.Min(additionalReplicasNeeded, availablePeers.Count);
            
            for (int i = 0; i < peersToTake; i++)
            {
                file.Copies[availablePeers[i]] = Guid.NewGuid().ToString("N");
            }

            var action = new SetFileSystemNodeAction { Path = absolutePath, Node = file };
            var success = await _runtime.UpdateRegistryAsync(action);
            if (!success)
            {
                throw new InvalidOperationException("Raft cluster rejected the file system update.");
            }
            return file;
        }

        public async Task<DirectoryNode> CreateDirectoryAsync(string absolutePath, bool throwErrorIfExists = true)
        {
            if (FileSystemNodeExists(absolutePath))
            {
                if (throwErrorIfExists)
                {
                    throw new InvalidOperationException($"{absolutePath} already exists");
                }
                else
                {
                    var existingNode = GetFileSystemNode(absolutePath);
                    if (existingNode is DirectoryNode existingDir) return existingDir;
                    throw new InvalidOperationException($"{absolutePath} exists but is not a directory");
                }
            }

            var dir = new DirectoryNode();
            var action = new SetFileSystemNodeAction { Path = absolutePath, Node = dir };
            var success = await _runtime.UpdateRegistryAsync(action);
            if (!success) throw new InvalidOperationException("Failed to commit directory to Raft cluster (if on a Follower, HTTP proxy may have failed).");
            return dir;
        }

        public async Task<IFileSystemNode> RemoveFileSystemNodeAsync(string absolutePath)
        {
            var info = GetFileSystemNode(absolutePath);
            if (info == null)
            {
                throw new InvalidOperationException($"{absolutePath} does not exist");
            }

            var action = new DeleteFileSystemNodeAction { Path = absolutePath };
            var success = await _runtime.UpdateRegistryAsync(action);
            if (!success) throw new InvalidOperationException("Failed to commit removal to Raft cluster.");
            return info;
        }

        public async Task<IFileSystemNode> MoveFileSystemNodeAsync(string srcAbsolutePath, string dstAbsolutePath)
        {
            var srcNode = GetFileSystemNode(srcAbsolutePath);
            if (srcNode == null)
            {
                throw new InvalidOperationException($"{srcAbsolutePath} does not exist");
            }

            var dstNode = GetFileSystemNode(dstAbsolutePath);
            if (dstNode != null)
            {
                throw new InvalidOperationException($"{dstAbsolutePath} already exists");
            }

            var moveAction = new MoveFileSystemNodeAction { SrcPath = srcAbsolutePath, DstPath = dstAbsolutePath };
            var success = await _runtime.UpdateRegistryAsync(moveAction);
            if (!success) throw new InvalidOperationException("Failed to commit move to Raft cluster.");

            return srcNode;
        }
        
        public async Task<IFileSystemNode> CopyFileSystemNodeAsync(string srcAbsolutePath, string dstAbsolutePath)
        {
            var srcNode = GetFileSystemNode(srcAbsolutePath);
            if (srcNode == null)
            {
                throw new InvalidOperationException($"{srcAbsolutePath} does not exist");
            }

            var dstNode = GetFileSystemNode(dstAbsolutePath);
            if (dstNode != null)
            {
                throw new InvalidOperationException($"{dstAbsolutePath} already exists");
            }

            var setAction = new SetFileSystemNodeAction { Path = dstAbsolutePath, Node = srcNode };
            var success = await _runtime.UpdateRegistryAsync(setAction);
            if (!success) throw new InvalidOperationException("Failed to commit copy to Raft cluster.");

            return srcNode;
        }

        public async Task<string> DownloadFile(string webURL, string directory, string? saveName = null)
        {
            _logger.LogInformation("FileSystemManager: Starting download of {WebURL}", webURL);
            using var httpClient = new System.Net.Http.HttpClient();
            using var response = await httpClient.GetAsync(webURL, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();

            var fileName = saveName ?? System.IO.Path.GetFileName(webURL);
            var absPath = directory.TrimEnd('/') + "/" + fileName;

            var fileNode = await CreateFileAsync(absPath, throwErrorIfExists: false);

            var operations = new List<Task>();
            var writers = new List<RemoteRawOutputPipe>();

            foreach (var copy in fileNode.Copies)
            {
                var nodeId = copy.Key;
                var replicaId = copy.Value;
                var pipeId = Guid.NewGuid().ToString("N");
                
                var volumeUri = $"{nodeId}.{_runtime.Config.Domain}/volume";

                // Call RPC to prepare the pipe
                await _rpcClient.InvokeAsync<object>(volumeUri, "PrepareWriteStream", new object[] { replicaId, pipeId }, TimeSpan.FromSeconds(30));

                var outputPipe = await _runtime.EstablishRemoteRawOutputPipe(nodeId, pipeId);
                writers.Add(outputPipe);
            }

            var buffer = new byte[65536];
            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    var chunk = new byte[bytesRead];
                    Array.Copy(buffer, chunk, bytesRead);
                    var writeTasks = new List<Task>();
                    foreach (var writer in writers)
                    {
                        writeTasks.Add(writer.Send(chunk));
                    }
                    await Task.WhenAll(writeTasks);
                }
                else
                {
                    foreach (var writer in writers)
                    {
                        await writer.CloseAsync();
                    }
                    break;
                }
            }

            _logger.LogInformation("FileSystemManager: Finished downloading {WebURL}", webURL);
            return absPath;
        }

        // The replica a reader uses: this runtime's, else the first.
        private (string NodeId, string ReplicaId) ReadReplica(FileNode file) =>
            file.Copies.TryGetValue(_runtime.Config.ID, out var local) ? (_runtime.Config.ID, local) : (file.Copies.First().Key, file.Copies.First().Value);

        // A file's length, from the replica a reader would use (the Registry's Size is 0 for files written
        // before sizes were recorded).
        public async Task<long> GetFileLengthAsync(string absolutePath)
        {
            if (GetFileSystemNode(absolutePath) is not FileNode file) throw new System.IO.FileNotFoundException(absolutePath);
            if (file.Copies.Count == 0) throw new InvalidOperationException("File has no replicas");
            var (nodeId, replicaId) = ReadReplica(file);
            return Convert.ToInt64(await _rpcClient.InvokeAsync<object>($"{nodeId}.{_runtime.Config.Domain}/volume", "ReplicaLength", new object[] { replicaId }, TimeSpan.FromSeconds(30)));
        }

        // A read stream of a file's content, not yet listening: set OnReceive, then StartListening (it returns
        // when the whole file has been delivered). Chunks arrive one at a time, in order.
        public async Task<Pipe> OpenReadStreamAsync(string absolutePath)
        {
            if (GetFileSystemNode(absolutePath) is not FileNode file) throw new System.IO.FileNotFoundException(absolutePath);
            if (file.Copies.Count == 0) throw new InvalidOperationException("File has no replicas");
            var (nodeId, replicaId) = ReadReplica(file);
            var pipeId = Guid.NewGuid().ToString("N");
            await _rpcClient.InvokeAsync<object>($"{nodeId}.{_runtime.Config.Domain}/volume", "PrepareReadStream", new object[] { replicaId, pipeId }, TimeSpan.FromSeconds(30));
            return await _runtime.EstablishRemoteRawInputPipe(nodeId, pipeId);
        }

        public async Task CreateFileReadStreamAsync(string filePath, string directory, Pipe outputPipe)
        {
            var absPath = filePath.StartsWith("/") ? filePath : directory.TrimEnd('/') + "/" + filePath;
            var node = GetFileSystemNode(absPath);
            if (node == null) throw new InvalidOperationException("File not found");
            if (node is not FileNode fileNode) throw new InvalidOperationException("Not a file");

            if (fileNode.Copies.Count == 0) throw new InvalidOperationException("File has no replicas");

            // Prefer local replica
            string targetNodeId;
            string targetReplicaId;
            if (fileNode.Copies.TryGetValue(_runtime.Config.ID, out var localReplicaId))
            {
                targetNodeId = _runtime.Config.ID;
                targetReplicaId = localReplicaId;
            }
            else
            {
                // Just pick the first one
                var first = fileNode.Copies.First();
                targetNodeId = first.Key;
                targetReplicaId = first.Value;
            }

            var volumeUri = $"{targetNodeId}.{_runtime.Config.Domain}/volume";
            var pipeId = Guid.NewGuid().ToString("N");

            // Call RPC to prepare the read pipe
            await _rpcClient.InvokeAsync<object>(volumeUri, "PrepareReadStream", new object[] { targetReplicaId, pipeId }, TimeSpan.FromSeconds(30));

            var inputPipe = await _runtime.EstablishRemoteRawInputPipe(targetNodeId, pipeId);
            inputPipe.PipeTo(outputPipe);
                
            try 
            {
                await inputPipe.StartListening(); 
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StreamFileAsync: streaming interrupted.");
            }
        }

        public async Task CreateFileWriteStreamAsync(string filePath, string directory, Pipe inputPipe)
        {
            var absPath = filePath.StartsWith("/") ? filePath : directory.TrimEnd('/') + "/" + filePath;
            var fileNode = await CreateFileAsync(absPath, throwErrorIfExists: false);

            var writers = new List<RemoteRawOutputPipe>();

            foreach (var copy in fileNode.Copies)
            {
                var nodeId = copy.Key;
                var replicaId = copy.Value;
                var pipeId = Guid.NewGuid().ToString("N");
                
                var volumeUri = $"{nodeId}.{_runtime.Config.Domain}/volume";

                // Call RPC to prepare the pipe
                await _rpcClient.InvokeAsync<object>(volumeUri, "PrepareWriteStream", new object[] { replicaId, pipeId }, TimeSpan.FromSeconds(30));

                var outputPipe = await _runtime.EstablishRemoteRawOutputPipe(nodeId, pipeId);
                writers.Add(outputPipe);
            }

            // Each replica's chunks are sent one after another (the socket's write lock isn't FIFO, so concurrent
            // sends could reorder them), and all of them are sent before its pipe closes (closing drops queued
            // sends). OnReceive is called for one chunk at a time.
            var sending = writers.Select(_ => Task.CompletedTask).ToArray();
            inputPipe.OnReceive(payload => 
            {
                for (int i = 0; i < writers.Count; i++)
                {
                    var writer = writers[i];
                    sending[i] = sending[i].ContinueWith(previous => previous.IsFaulted ? previous : writer.Send(payload), TaskScheduler.Default).Unwrap();
                }
            });
            
            try 
            {
                await inputPipe.StartListening(); 
            }
            finally
            {
                for (int i = 0; i < writers.Count; i++)
                {
                    try { await sending[i]; }
                    catch (Exception ex) { _logger.LogWarning("Writing a replica of {Path} failed: {Error}", absPath, ex.Message); }
                    await writers[i].CloseAsync();
                }
            }
        }

        // Waits until every replica of `file` holds `size` bytes: a write stream's end doesn't mean the replicas
        // have written everything they were sent. Returns false on timeout.
        public async Task<bool> WaitForReplicasAsync(FileNode file, long size, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            foreach (var (nodeId, replicaId) in file.Copies)
            {
                var volumeUri = $"{nodeId}.{_runtime.Config.Domain}/volume";
                while (true)
                {
                    long length = -1;
                    try { length = Convert.ToInt64(await _rpcClient.InvokeAsync<object>(volumeUri, "ReplicaLength", new object[] { replicaId }, TimeSpan.FromSeconds(10))); }
                    catch (Exception ex) { _logger.LogDebug("ReplicaLength on {Node} failed: {Error}", nodeId, ex.Message); }
                    if (length == size) break;
                    if (DateTime.UtcNow > deadline) return false;
                    await Task.Delay(50);
                }
            }
            return true;
        }

        public async Task<byte[]> ReadFileAsync(string filePath, string directory)
        {
            var absPath = filePath.StartsWith("/") ? filePath : directory.TrimEnd('/') + "/" + filePath;
            var node = GetFileSystemNode(absPath);
            if (node == null) throw new InvalidOperationException("File not found");
            if (node is not FileNode fileNode) throw new InvalidOperationException("Not a file");

            if (fileNode.Copies.Count == 0) throw new InvalidOperationException("File has no replicas");

            string targetNodeId;
            string targetReplicaId;
            if (fileNode.Copies.TryGetValue(_runtime.Config.ID, out var localReplicaId))
            {
                targetNodeId = _runtime.Config.ID;
                targetReplicaId = localReplicaId;
            }
            else
            {
                var first = fileNode.Copies.First();
                targetNodeId = first.Key;
                targetReplicaId = first.Value;
            }

            var volumeUri = $"{targetNodeId}.{_runtime.Config.Domain}/volume";
            var pipeId = Guid.NewGuid().ToString("N");

            _logger.LogInformation($"{URI} making an RPC call to {volumeUri}");

            await _rpcClient.InvokeAsync<object>(volumeUri, "PrepareReadStream", new object[] { targetReplicaId, pipeId }, TimeSpan.FromSeconds(30));

            var inputPipe = await _runtime.EstablishRemoteRawInputPipe(targetNodeId, pipeId);

            using var ms = new System.IO.MemoryStream();
            inputPipe.OnReceive(payload => ms.Write(payload, 0, payload.Length));
            
            await inputPipe.StartListening();
            
            return ms.ToArray();
        }

        public async Task WriteFileAsync(string filePath, string directory, byte[] data)
        {
            var absPath = filePath.StartsWith("/") ? filePath : directory.TrimEnd('/') + "/" + filePath;
            var fileNode = await CreateFileAsync(absPath, throwErrorIfExists: false);

            var writers = new List<RemoteRawOutputPipe>();

            foreach (var copy in fileNode.Copies)
            {
                var nodeId = copy.Key;
                var replicaId = copy.Value;
                var pipeId = Guid.NewGuid().ToString("N");
                
                var volumeUri = $"{nodeId}.{_runtime.Config.Domain}/volume";
                await _rpcClient.InvokeAsync<object>(volumeUri, "PrepareWriteStream", new object[] { replicaId, pipeId }, TimeSpan.FromSeconds(30));

                var outputPipe = await _runtime.EstablishRemoteRawOutputPipe(nodeId, pipeId);
                writers.Add(outputPipe);
            }

            var writeTasks = new List<Task>();
            foreach (var writer in writers)
            {
                writeTasks.Add(writer.Send(data));
            }
            await Task.WhenAll(writeTasks);

            foreach (var writer in writers)
            {
                await writer.CloseAsync();
            }
        }

        protected override Task OnBeginAsync(CancellationToken ct) => Task.CompletedTask;
        protected override Task OnPauseAsync(CancellationToken ct) => Task.CompletedTask;
        protected override Task OnEndAsync(CancellationToken ct) => Task.CompletedTask;

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in Inbox.Reader.ReadAllAsync(ct))
                {
                    if (msg is Envelope env && env.Channel == "rpc")
                    {
                        var req = MessagePack.MessagePackSerializer.Deserialize<AgentRpc.RpcMessage>(env.Payload);
                        if (req is AgentRpc.ResponseMessage rpcRes)
                        {
                            _rpcClient.ProcessResponse(rpcRes);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
