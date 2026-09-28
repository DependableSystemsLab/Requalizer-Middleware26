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
    // `oneos cp [-r] <local> <remote>`: copies local files into the cluster's file system through a runtime
    // (OneOS.Runtime.Kernel.FileUpload). Each file or directory is one authenticated connection; a file's
    // bytes go raw over it into the file system's write stream. Like cp: a destination that is an existing
    // directory receives the copy under the source's name; a directory needs -r. Missing parent directories
    // are created.
    public sealed class FileUploader
    {
        private const int ChunkSize = 256 * 1024;

        private readonly string _host;
        private readonly int _port;
        private readonly string _username, _password;
        private readonly Action<string> _log;

        public FileUploader(string address, string username, string password, Action<string>? log = null)
        {
            var parts = address.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out _port)) throw new ArgumentException($"invalid address '{address}' (host:port)");
            _host = parts[0];
            _username = username;
            _password = password;
            _log = log ?? (_ => { });
        }

        public int Parallelism { get; init; } = 4;

        public sealed record Result(int Files, int Directories, long Bytes, IReadOnlyList<string> Errors);

        public async Task<Result> CopyAsync(string localPath, string remotePath, bool recursive, CancellationToken ct = default)
        {
            var source = Path.GetFullPath(localPath);
            bool isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source)) throw new FileNotFoundException($"cannot stat '{localPath}': no such file or directory");
            if (isDirectory && !recursive) throw new InvalidOperationException($"-r not specified; omitting directory '{localPath}'");
            var remote = remotePath.StartsWith('/') ? remotePath : "/" + remotePath;

            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
            var resolved = await RequestAsync(new FileUploadRequest
                { Kind = FileUploadRequest.UploadKind.Resolve, Path = remote, SourceName = name, SourceIsDirectory = isDirectory }, null, ct);
            if (!resolved.Accepted) throw new InvalidOperationException(resolved.Error);
            var target = resolved.Path;

            var errors = new List<string>();
            if (!isDirectory)
            {
                var size = await UploadFileAsync(source, target, errors, ct);
                return new Result(size >= 0 ? 1 : 0, 0, Math.Max(0, size), errors);
            }

            // Directories first (parents before children), then the files, a few at a time.
            var directories = new[] { source }.Concat(Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)).OrderBy(d => d.Length).ToList();
            foreach (var dir in directories)
            {
                var path = Remote(target, source, dir);
                var response = await RequestAsync(new FileUploadRequest { Kind = FileUploadRequest.UploadKind.Directory, Path = path }, null, ct);
                if (response.Accepted) _log($"{dir}{Path.DirectorySeparatorChar} -> {path}/");
                else errors.Add($"{path}: {response.Error}");
            }
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
            long bytes = 0;
            int uploaded = 0;
            using var slots = new SemaphoreSlim(Parallelism);
            await Task.WhenAll(files.Select(async file =>
            {
                await slots.WaitAsync(ct);
                try
                {
                    var size = await UploadFileAsync(file, Remote(target, source, file), errors, ct);
                    if (size >= 0) { Interlocked.Add(ref bytes, size); Interlocked.Increment(ref uploaded); }
                }
                finally { slots.Release(); }
            }));
            return new Result(uploaded, directories.Count, bytes, errors);
        }

        private static string Remote(string target, string sourceRoot, string local)
        {
            var relative = Path.GetRelativePath(sourceRoot, local);
            return relative == "." ? target : target.TrimEnd('/') + "/" + relative.Replace(Path.DirectorySeparatorChar, '/');
        }

        // Uploads one file; its size, or -1 (the error is recorded).
        private async Task<long> UploadFileAsync(string local, string remote, List<string> errors, CancellationToken ct)
        {
            try
            {
                long size = new FileInfo(local).Length;
                var response = await RequestAsync(new FileUploadRequest { Kind = FileUploadRequest.UploadKind.File, Path = remote, Size = size }, local, ct);
                if (!response.Accepted) throw new InvalidOperationException(response.Error);
                _log($"{local} -> {remote} ({size} bytes)");
                return size;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (errors) errors.Add($"{remote}: {ex.Message}");
                return -1;
            }
        }

        // One request on its own connection. For a file, once the runtime is ready, its bytes follow raw and
        // the runtime's second response is the outcome.
        private async Task<FileUploadResponse> RequestAsync(FileUploadRequest request, string? file, CancellationToken ct)
        {
            request.MessageId = Guid.NewGuid();
            request.SenderId = "cp/" + Environment.MachineName;
            request.Username = _username;
            request.Password = _password;
            var socket = new SecureClientSideSocket(_host, _port, _host, "", null,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SecureClientSideSocket>.Instance, $"upload to {_host}:{_port}");
            try
            {
                await socket.ConnectAsync(3, ct);
                await socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(request));
                var response = Read(socket);
                if (file == null || !response.Accepted) return response;
                if (request.Size > 0)
                {
                    await using var stream = File.OpenRead(file);
                    // Exactly the size announced, even if the file grows meanwhile.
                    var buffer = new byte[ChunkSize];
                    long remaining = request.Size;
                    int n;
                    while (remaining > 0 && (n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct)) > 0)
                    {
                        await socket.SendRaw(buffer[..n]);
                        remaining -= n;
                    }
                }
                return Read(socket);
            }
            finally { await socket.StopAsync(); }
        }

        private static FileUploadResponse Read(TcpSocket socket) =>
            MessagePackSerializer.Deserialize<RuntimeMessage>(socket.Receive()) as FileUploadResponse
                ?? throw new InvalidOperationException("unexpected response from the runtime");
    }
}
