using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.Extensions.Logging;
using OneOS.Common;

namespace OneOS.Runtime.Kernel
{
    // The runtime's end of `oneos cp` (OneOS.Client.FileUploader): one request per connection, authenticated
    // like a terminal. For a file, the connection is handed over to the file system's write stream: the
    // client's raw bytes go straight into the file's replicas.
    public static class FileUpload
    {
        public static async Task HandleAsync(Runtime runtime, FileUploadRequest request, TcpSocket socket, ILogger logger)
        {
            Task Reply(bool accepted, string path, string error = "") =>
                socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(new FileUploadResponse
                    { MessageId = request.MessageId, SenderId = runtime.Config.ID, Accepted = accepted, Path = path, Error = error }));
            var fs = runtime.FileSystemManager;
            var path = Normalize(request.Path);
            try
            {
                if (!runtime.SessionManager.AuthenticateUser(request.Username, request.Password))
                {
                    await Reply(false, path, "invalid credentials");
                    return;
                }
                switch (request.Kind)
                {
                    case FileUploadRequest.UploadKind.Resolve:
                    {
                        // cp: into an existing directory, under the source's name; otherwise to the path itself.
                        var target = fs.IsDirectory(path) ? Normalize(path + "/" + request.SourceName) : path;
                        if (request.SourceIsDirectory && fs.GetFileSystemNode(target) is FileNode)
                            await Reply(false, target, $"{target} is a file, not a directory");
                        else if (!request.SourceIsDirectory && fs.IsDirectory(target))
                            await Reply(false, target, $"{target} is a directory");
                        else await Reply(true, target);
                        return;
                    }
                    case FileUploadRequest.UploadKind.Directory:
                        await fs.CreateDirectoryAsync(path, throwErrorIfExists: false);
                        await Reply(true, path);
                        return;
                    case FileUploadRequest.UploadKind.File:
                        await ReceiveFileAsync(runtime, request, path, socket, Reply, logger);
                        return;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Upload of {Path} failed: {Error}", path, ex.Message);
                try { await Reply(false, path, ex.Message); } catch (Exception) { }
            }
            finally
            {
                await socket.StopAsync();
            }
        }

        private static async Task ReceiveFileAsync(Runtime runtime, FileUploadRequest request, string path, TcpSocket socket,
            Func<bool, string, string, Task> reply, ILogger logger)
        {
            var fs = runtime.FileSystemManager;
            if (fs.IsDirectory(path)) { await reply(false, path, $"{path} is a directory"); return; }
            var node = await fs.CreateFileAsync(path, throwErrorIfExists: false);

            // The write stream reads the client's bytes from a channel the socket fills, up to Size.
            var channel = Channel.CreateUnbounded<byte[]>();
            var writing = fs.CreateFileWriteStreamAsync(path, "/", new LocalRawInputPipe(channel));
            long received = 0;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (request.Size == 0) { channel.Writer.TryComplete(); done.TrySetResult(); }
            else
            {
                socket.OnEnded += _ => done.TrySetResult();
                _ = socket.ListenRaw(chunk =>
                {
                    var take = (int)Math.Min(chunk.Length, request.Size - received);
                    if (take <= 0) return;
                    channel.Writer.TryWrite(take == chunk.Length ? chunk : chunk[..take]);
                    received += take;
                    if (received >= request.Size) { channel.Writer.TryComplete(); done.TrySetResult(); }
                });
            }
            await reply(true, path, "");                           // ready: the bytes follow
            await done.Task;
            channel.Writer.TryComplete();
            await writing;
            if (received < request.Size)
            {
                logger.LogWarning("Upload of {Path} ended after {Received} of {Size} bytes; removed", path, received, request.Size);
                try { await fs.RemoveFileSystemNodeAsync(path); } catch (Exception) { }
                return;
            }
            // Confirmed only once every replica has all of it.
            if (!await fs.WaitForReplicasAsync(node, request.Size, TimeSpan.FromSeconds(60)))
            {
                logger.LogWarning("Upload of {Path}: its replicas didn't receive all {Size} bytes; removed", path, request.Size);
                try { await fs.RemoveFileSystemNodeAsync(path); } catch (Exception) { }
                await reply(false, path, "the file's replicas didn't receive all of its bytes");
                return;
            }
            // The file's size, for stat and readers.
            await runtime.UpdateRegistryAsync(new SetFileSystemNodeAction
                { Path = path, Node = new FileNode { Copies = node.Copies, Checksum = node.Checksum, Size = request.Size } });
            logger.LogInformation("Uploaded {Path} ({Size} bytes)", path, request.Size);
            await reply(true, path, "");
        }

        // An absolute path without empty, "." or ".." segments.
        public static string Normalize(string path)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var p in path.Split('/'))
            {
                if (p is "" or ".") continue;
                if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else parts.Add(p);
            }
            return "/" + string.Join("/", parts);
        }
    }
}
