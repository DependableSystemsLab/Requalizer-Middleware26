using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using OneOS.Common;
using OneOS.Runtime;

namespace OneOS.Client
{
    // `oneos cp [-r] oneos:<remote> <local>`: copies files from the cluster's file system to local disk through
    // a runtime (OneOS.Runtime.Kernel.FileDownload). The remote tree is listed first; then each file comes over
    // its own authenticated connection, as exactly the length the runtime announces. Like cp: a destination that
    // is an existing local directory receives the copy under the source's name; a directory needs -r. A file is
    // written to <name>.oneos-part and renamed into place when complete.
    public sealed class FileDownloader
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _username, _password;
        private readonly Action<string> _log;

        public FileDownloader(string address, string username, string password, Action<string>? log = null)
        {
            var parts = address.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out _port)) throw new ArgumentException($"invalid address '{address}' (host:port)");
            _host = parts[0];
            _username = username;
            _password = password;
            _log = log ?? (_ => { });
        }

        public int Parallelism { get; init; } = 4;

        public async Task<FileUploader.Result> CopyAsync(string remotePath, string localPath, bool recursive, CancellationToken ct = default)
        {
            var remote = "/" + remotePath.Trim('/');
            var listing = await RequestAsync(new FileDownloadRequest { Kind = FileDownloadRequest.DownloadKind.List, Path = remote }, null, ct);
            if (!listing.Accepted) throw new InvalidOperationException(listing.Error);
            bool isDirectory = listing.Entries.First(e => e.Path == "").IsDirectory;
            if (isDirectory && !recursive) throw new InvalidOperationException($"-r not specified; omitting directory 'oneos:{remote}'");

            var name = remote == "/" ? "root" : remote[(remote.LastIndexOf('/') + 1)..];
            var target = Directory.Exists(localPath) ? Path.Combine(localPath, name) : Path.GetFullPath(localPath);
            if (isDirectory && File.Exists(target)) throw new InvalidOperationException($"cannot overwrite non-directory '{target}' with directory 'oneos:{remote}'");
            if (!isDirectory && Directory.Exists(target)) throw new InvalidOperationException($"'{target}' is a directory");

            var errors = new List<string>();
            if (!isDirectory)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var size = await DownloadFileAsync(remote, target, errors, ct);
                return new FileUploader.Result(size >= 0 ? 1 : 0, 0, Math.Max(0, size), errors);
            }

            var directories = listing.Entries.Where(e => e.IsDirectory).ToList();
            foreach (var d in directories) Directory.CreateDirectory(Local(target, d.Path));
            var files = listing.Entries.Where(e => !e.IsDirectory).ToList();
            long bytes = 0;
            int downloaded = 0;
            using var slots = new SemaphoreSlim(Parallelism);
            await Task.WhenAll(files.Select(async f =>
            {
                await slots.WaitAsync(ct);
                try
                {
                    var size = await DownloadFileAsync(remote.TrimEnd('/') + "/" + f.Path, Local(target, f.Path), errors, ct);
                    if (size >= 0) { Interlocked.Add(ref bytes, size); Interlocked.Increment(ref downloaded); }
                }
                finally { slots.Release(); }
            }));
            return new FileUploader.Result(downloaded, directories.Count, bytes, errors);
        }

        private static string Local(string target, string relative) =>
            relative.Length == 0 ? target : Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));

        // Downloads one file; its size, or -1 (the error is recorded).
        private async Task<long> DownloadFileAsync(string remote, string local, List<string> errors, CancellationToken ct)
        {
            try
            {
                var response = await RequestAsync(new FileDownloadRequest { Kind = FileDownloadRequest.DownloadKind.File, Path = remote }, local, ct);
                if (!response.Accepted) throw new InvalidOperationException(response.Error);
                _log($"oneos:{remote} -> {local} ({response.Size} bytes)");
                return response.Size;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (errors) errors.Add($"oneos:{remote}: {ex.Message}");
                return -1;
            }
        }

        // One request on its own connection. For a file, exactly the announced length follows raw.
        private async Task<FileDownloadResponse> RequestAsync(FileDownloadRequest request, string? file, CancellationToken ct)
        {
            request.MessageId = Guid.NewGuid();
            request.SenderId = "cp/" + Environment.MachineName;
            request.Username = _username;
            request.Password = _password;
            var socket = new SecureClientSideSocket(_host, _port, _host, "", null,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SecureClientSideSocket>.Instance, $"download from {_host}:{_port}");
            try
            {
                await socket.ConnectAsync(3, ct);
                await socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(request));
                var response = MessagePackSerializer.Deserialize<RuntimeMessage>(socket.Receive()) as FileDownloadResponse
                    ?? throw new InvalidOperationException("unexpected response from the runtime");
                if (file == null || !response.Accepted) return response;

                var partial = file + ".oneos-part";
                long received = 0;
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (response.Size > 0)
                    {
                        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        socket.OnEnded += _ => done.TrySetResult();
                        _ = socket.ListenRaw(chunk =>
                        {
                            var take = (int)Math.Min(chunk.Length, response.Size - received);
                            if (take <= 0) return;
                            output.Write(chunk, 0, take);
                            received += take;
                            if (received >= response.Size) done.TrySetResult();
                        });
                        await done.Task.WaitAsync(ct);
                    }
                }
                if (received < response.Size)
                {
                    File.Delete(partial);
                    throw new IOException($"the connection ended after {received} of {response.Size} bytes");
                }
                File.Move(partial, file, overwrite: true);
                return response;
            }
            finally { await socket.StopAsync(); }
        }
    }
}
