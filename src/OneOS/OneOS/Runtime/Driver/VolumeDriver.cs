using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Common;
using OneOS.Runtime.Kernel;

namespace OneOS.Runtime.Driver
{
    public class VolumeDriver : Agent
    {
        private readonly Runtime _runtime;
        private readonly AgentRpc.Server _rpcServer;
        private readonly string _storageDir;
        // Replicas being written. Reads share a replica with its writer (they see what has been written so far),
        // but a second writer is refused: on Linux, .NET's FileShare only distinguishes exclusive (None) from
        // shared, so the file's lock can't keep writers apart once readers are allowed.
        private readonly ConcurrentDictionary<string, byte> _writing = new();

        public VolumeDriver(Runtime runtime, ILoggerFactory loggerFactory)
            : base($"{runtime.Config.URI}/volume", loggerFactory.CreateLogger<VolumeDriver>())
        {
            _runtime = runtime;
            _rpcServer = new AgentRpc.Server(this);
            _storageDir = Path.Combine(runtime.Config.MountPath, runtime.Config.StoragePath);
            
            if (!Directory.Exists(_storageDir))
            {
                Directory.CreateDirectory(_storageDir);
            }
        }

        [AgentRpc.Method("PrepareWriteStream")]
        public void PrepareWriteStream(string replicaId, string pipeId)
        {
            _logger.LogInformation("VolumeDriver: Expecting raw pipe {PipeId} for Replica {ReplicaId}", pipeId, replicaId);
            var filePath = Path.Combine(_storageDir, replicaId);
            
            _runtime.ExecutionManager.ExpectPipe(pipeId, socket => 
            {
                _logger.LogInformation("VolumeDriver: Raw pipe {PipeId} connected. Writing to {FilePath}", pipeId, filePath);
                var inputPipe = new RemoteRawInputPipe(socket);
                
                if (!_writing.TryAdd(filePath, 0))
                    throw new IOException($"Replica {replicaId} is already being written");
                FileStream fs;
                try { fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read); }
                catch { _writing.TryRemove(filePath, out _); throw; }
                
                inputPipe.OnReceive(chunk => 
                {
                    fs.Write(chunk, 0, chunk.Length);
                });
                
                _ = Task.Run(async () => 
                {
                    try
                    {
                        await inputPipe.StartListening();
                    }
                    finally
                    {
                        _logger.LogInformation("VolumeDriver: Raw pipe {PipeId} disconnected. Closing stream.", pipeId);
                        fs.Flush();
                        fs.Close();
                        fs.Dispose();
                        _writing.TryRemove(filePath, out _);
                    }
                });
            });
        }

        // How many bytes a replica holds (-1: none), so a writer can tell when its data has landed.
        [AgentRpc.Method("ReplicaLength")]
        public long ReplicaLength(string replicaId)
        {
            var info = new FileInfo(Path.Combine(_storageDir, replicaId));
            return info.Exists ? info.Length : -1;
        }

        [AgentRpc.Method("PrepareReadStream")]
        public void PrepareReadStream(string replicaId, string pipeId)
        {
            _logger.LogInformation("VolumeDriver: Expecting raw pipe {PipeId} to read Replica {ReplicaId}", pipeId, replicaId);
            var filePath = Path.Combine(_storageDir, replicaId);
            
            if (!File.Exists(filePath)) throw new FileNotFoundException($"Replica {replicaId} not found");

            _runtime.ExecutionManager.ExpectPipe(pipeId, socket => 
            {
                _logger.LogInformation("VolumeDriver: Raw pipe {PipeId} connected. Reading from {FilePath}", pipeId, filePath);
                var outputPipe = new RemoteRawOutputPipe(socket);
                
                _ = Task.Run(async () => 
                {
                    try
                    {
                        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var buffer = new byte[65536];
                        while (true)
                        {
                            int bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length);
                            if (bytesRead > 0)
                            {
                                var chunk = new byte[bytesRead];
                                Array.Copy(buffer, chunk, bytesRead);
                                await outputPipe.Send(chunk);
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "VolumeDriver: Error reading replica {ReplicaId}", replicaId);
                    }
                    finally
                    {
                        _logger.LogInformation("VolumeDriver: Finished reading replica {ReplicaId}. Closing pipe.", replicaId);
                        await outputPipe.CloseAsync();
                    }
                });
            });
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
                        if (req is AgentRpc.RequestMessage rpcReq)
                        {
                            await _rpcServer.ProcessRequestAsync(env, rpcReq);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
