using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.Extensions.Logging;
using OneOS.Common;

namespace OneOS.Runtime.Kernel
{
    // The runtime's end of `oneos cp oneos:<path> <local>` (OneOS.Client.FileDownloader): one request per
    // connection, authenticated like a terminal. A file's content is streamed from a replica straight onto
    // the connection, after a response giving its exact length.
    public static class FileDownload
    {
        public static async Task HandleAsync(Runtime runtime, FileDownloadRequest request, TcpSocket socket, ILogger logger)
        {
            Task Reply(FileDownloadResponse response) =>
                socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(response with { MessageId = request.MessageId, SenderId = runtime.Config.ID }));
            var fs = runtime.FileSystemManager;
            var path = FileUpload.Normalize(request.Path);
            try
            {
                if (!runtime.SessionManager.AuthenticateUser(request.Username, request.Password))
                {
                    await Reply(new FileDownloadResponse { Error = "invalid credentials" });
                    return;
                }
                var node = fs.GetFileSystemNode(path);
                if (node is not (FileNode or DirectoryNode))
                {
                    await Reply(new FileDownloadResponse { Error = $"{path}: no such file or directory" });
                    return;
                }
                if (request.Kind == FileDownloadRequest.DownloadKind.List)
                {
                    var entries = new List<FileDownloadEntry> { new() { Path = "", IsDirectory = node is DirectoryNode } };
                    if (node is DirectoryNode dir) List(dir, "", entries);
                    await Reply(new FileDownloadResponse { Accepted = true, Entries = entries });
                    return;
                }
                if (node is not FileNode)
                {
                    await Reply(new FileDownloadResponse { Error = $"{path} is a directory" });
                    return;
                }
                var length = await fs.GetFileLengthAsync(path);
                var content = await fs.OpenReadStreamAsync(path);
                await Reply(new FileDownloadResponse { Accepted = true, Size = length });
                // One chunk at a time, in order, straight onto the connection.
                content.OnReceive(chunk => socket.SendRaw(chunk).GetAwaiter().GetResult());
                await content.StartListening();
            }
            catch (Exception ex)
            {
                logger.LogWarning("Download of {Path} failed: {Error}", path, ex.Message);
                try { await Reply(new FileDownloadResponse { Error = ex.Message }); } catch (Exception) { }
            }
            finally
            {
                await socket.StopAsync();
            }
        }

        // Directories before their contents, sorted.
        private static void List(DirectoryNode dir, string prefix, List<FileDownloadEntry> entries)
        {
            foreach (var (name, child) in new SortedDictionary<string, IFileSystemNode>(dir.Children, StringComparer.Ordinal))
            {
                if (child is not (FileNode or DirectoryNode)) continue;    // sockets, devices
                var path = prefix.Length == 0 ? name : prefix + "/" + name;
                entries.Add(new FileDownloadEntry { Path = path, IsDirectory = child is DirectoryNode });
                if (child is DirectoryNode sub) List(sub, path, entries);
            }
        }
    }
}
