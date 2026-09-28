using Microsoft.AspNetCore.StaticFiles;
using OneOS.Client;
using OneOS.Runtime;

namespace OneOS.WebTerminal
{
    // The /fs endpoints the client's File System Viewer and Text Editor use. Directory listings come from the
    // Registry mirror (the tree is replicated state); file bytes go through the same transfer path as `oneos cp`
    // (cluster-api.md §7), staged through a temp file on the server.
    public static class FileSystemApi
    {
        private static readonly FileExtensionContentTypeProvider ContentTypes = new();

        // GET /fs/<path>: a directory's entries as [{ type, name }], or a file's bytes.
        public static async Task<IResult> GetAsync(string path, ClusterView view, string address, WebSession session, CancellationToken ct)
        {
            var abspath = Normalize(path);
            var node = view.ReadNode(abspath);
            switch (node)
            {
                case null:
                    return Results.NotFound($"{abspath} does not exist");
                case DirectoryNode dir:
                    var entries = dir.Children
                        .Select(kv => new { type = kv.Value is DirectoryNode ? "directory" : "file", name = kv.Key })
                        .Where(e => e.type == "directory" || e.name != null)
                        .OrderBy(e => e.type == "directory" ? 0 : 1).ThenBy(e => e.name)
                        .ToList();
                    return Results.Json(entries);
                case FileNode:
                    var (bytes, error) = await DownloadAsync(address, session, abspath, ct);
                    if (error != null) return Results.Problem(error, statusCode: StatusCodes.Status500InternalServerError);
                    return Results.Bytes(bytes!, ContentTypes.TryGetContentType(abspath, out var ctype) ? ctype : "application/octet-stream");
                default:
                    return Results.Problem($"{abspath} cannot be viewed over the web", statusCode: StatusCodes.Status400BadRequest);
            }
        }

        // POST /fs/<path> with { content }: overwrite a file's contents (the Text Editor's Save).
        public static async Task<IResult> PostAsync(string path, WriteBody body, ClusterView view, string address, WebSession session, CancellationToken ct)
        {
            var abspath = Normalize(path);
            var node = view.ReadNode(abspath);
            if (node is DirectoryNode) return Results.BadRequest("Cannot POST to a directory");

            var temp = Path.Combine(Path.GetTempPath(), "oneos-wt-" + Guid.NewGuid().ToString("N"));
            try
            {
                await File.WriteAllTextAsync(temp, body.content ?? "", ct);
                var uploader = new FileUploader(address, session.Username, session.Password);
                var result = await uploader.CopyAsync(temp, abspath, recursive: false, ct);
                return Results.Json(new { error = result.Errors.Count > 0 ? string.Join("; ", result.Errors) : (string?)null });
            }
            finally { try { File.Delete(temp); } catch { /* best effort */ } }
        }

        private static async Task<(byte[]? Bytes, string? Error)> DownloadAsync(string address, WebSession session, string abspath, CancellationToken ct)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "oneos-wt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var localPath = Path.Combine(tempDir, Path.GetFileName(abspath.TrimEnd('/')) is { Length: > 0 } name ? name : "file");
                var downloader = new FileDownloader(address, session.Username, session.Password);
                var result = await downloader.CopyAsync(abspath, localPath, recursive: false, ct);
                if (result.Errors.Count > 0) return (null, string.Join("; ", result.Errors));
                if (!File.Exists(localPath)) return (null, "the file could not be downloaded");
                return (await File.ReadAllBytesAsync(localPath, ct), null);
            }
            finally { try { Directory.Delete(tempDir, true); } catch { /* best effort */ } }
        }

        // A cluster path: leading slash, no "." / ".." segments (the client sends absolute cluster paths).
        private static string Normalize(string path)
        {
            var segments = ("/" + path).Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s != "." && s != "..").ToArray();
            return "/" + string.Join('/', segments);
        }

        public sealed record WriteBody(string? content);
    }
}
