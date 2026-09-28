using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OneOS.Runtime;
using OneOS.Runtime.Driver;

namespace OneOS.Tests.Driver;

// The JavaScript environment's IPC (plan step 7): oneos.js Runtime.js in a real node process, talking to a
// NodeIpcChannel over inherited descriptors (DescriptorShim). Runtime.js needs no npm packages.
public class NodeIpcTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oneos-nodeipc-" + Guid.NewGuid().ToString("N")[..8]);
    public NodeIpcTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void TheLibraryIsEmbeddedAndInstallable()
    {
        Assert.Contains("Runtime.js", JavaScriptEnvironmentInstaller.LibraryFiles);
        Assert.Contains("instrument.js", JavaScriptEnvironmentInstaller.LibraryFiles);
        JavaScriptEnvironmentInstaller.WriteLibrary(_dir);
        Assert.True(File.Exists(Path.Combine(JavaScriptEnvironmentInstaller.ModuleDirectory(_dir), "Code.js")));
        Assert.False(JavaScriptEnvironmentInstaller.DependenciesInstalled(_dir));
    }

    [Fact]
    public async Task RuntimeJsTalksToTheRuntimeOverInheritedDescriptors()
    {
        JavaScriptEnvironmentInstaller.WriteLibrary(_dir);
        var runtimeJs = Path.Combine(JavaScriptEnvironmentInstaller.ModuleDirectory(_dir), "Runtime.js");
        var program = Path.Combine(_dir, "program.js");
        File.WriteAllText(program, $$"""
            const Runtime = require({{JsonSerializer.Serialize(runtimeJs)}});
            const root = {
                meta: { filename: 'program', uri: 'test/agent', cwd: '/home' },
                pauseTimers() { process.stderr.write('paused\n'); }, resumeTimers() {},
                snapshot() { return { counter: 42 }; }
            };
            Runtime.connect(root).then(() => {
                const fs = require('fs');
                const results = {};
                results.sync = fs.readFileSync('greeting.txt', 'utf8');                     // sync channel, relative to cwd
                fs.readFile('/home/greeting.txt', 'utf8', (err, text) => {
                    results.async = text;
                    fs.readFile('/nope.txt', 'utf8', (err2) => {
                        results.missing = err2 && err2.code;
                        const chunks = [];
                        fs.createReadStream('/data.bin')
                            .on('data', c => chunks.push(c))
                            .on('end', () => {
                                results.streamed = Buffer.concat(chunks).length;
                                const out = fs.createWriteStream('/out.txt');
                                out.write('hello ');
                                out.end('world', () => {
                                    process.stdout.json.write(results);
                                    process.stdout.json.write({ text: 'braces } { in strings' });
                                });
                            });
                    });
                });
            });
            """);

        var files = new ConcurrentDictionary<string, byte[]>
        {
            ["/home/greeting.txt"] = Encoding.UTF8.GetBytes("hi there"),
            ["/data.bin"] = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray(),
        };
        var written = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeIpcChannel? ipc = null;
        Task<object?> Handle(string method, JsonElement[] args)
        {
            string path = args[0].GetString()!;
            if (!path.StartsWith('/')) path = "/home/" + path;
            switch (method)
            {
                case "ReadTextFile":
                    return Task.FromResult<object?>(files.TryGetValue(path, out var b) ? Encoding.UTF8.GetString(b) : throw new FileNotFoundException(path));
                case "CreateReadStream":
                {
                    var id = ipc!.NewStreamId();
                    var data = files[path];
                    // Data can overtake the RPC's return on its way to the caller: Runtime.js buffers it.
                    _ = Task.Run(async () =>
                    {
                        for (int i = 0; i < data.Length; i += 65536) await ipc.SendDataAsync(id, data.AsMemory(i, Math.Min(65536, data.Length - i)));
                        await ipc.EndStreamAsync(id);
                    });
                    return Task.FromResult<object?>(id);
                }
                case "CreateWriteStream":
                {
                    var id = ipc!.NewStreamId();
                    var channel = Channel.CreateUnbounded<byte[]>();
                    ipc.AcceptStream(id, channel.Writer);
                    _ = Task.Run(async () =>
                    {
                        var all = new List<byte>();
                        await foreach (var chunk in channel.Reader.ReadAllAsync()) all.AddRange(chunk);
                        written.TrySetResult(Encoding.UTF8.GetString(all.ToArray()));
                    });
                    return Task.FromResult<object?>(id);
                }
                default: throw new NotSupportedException(method);
            }
        }

        var descriptors = new[] { new ExtraDescriptor(3, true), new ExtraDescriptor(4, false), new ExtraDescriptor(5, true), new ExtraDescriptor(6, false) };
        await using var shim = new DescriptorShim(descriptors, _dir);
        var psi = new ProcessStartInfo("node") { WorkingDirectory = _dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        psi.ArgumentList.Add(program);
        psi.Environment["ONEOS_IPC_FDS"] = "3,4,5,6";
        shim.Prepare(psi);
        using var process = Process.Start(psi)!;
        shim.Open();
        ipc = new NodeIpcChannel((await shim.Stream(4))!, (await shim.Stream(3))!, Handle);
        ipc.Start();
        _ = ipc.ServeSyncAsync((await shim.Stream(6))!, (await shim.Stream(5))!);

        // The runtime calls the program too.
        Assert.Equal(42, (await ipc.RequestAsync("checkpoint")).GetProperty("counter").GetInt32());
        await ipc.RequestAsync("pause");

        Assert.Equal("hello world", await written.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        var stdout = process.StandardOutput.ReadToEndAsync();
        Assert.True(process.WaitForExit(20_000), "the program should exit once nothing is pending");   // the channel doesn't hold it
        var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);                                       // newline-terminated JSON
        var results = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("hi there", results.GetProperty("sync").GetString());
        Assert.Equal("hi there", results.GetProperty("async").GetString());
        Assert.Equal("ENOENT", results.GetProperty("missing").GetString());
        Assert.Equal(200_000, results.GetProperty("streamed").GetInt32());
        Assert.Equal("braces } { in strings", JsonDocument.Parse(lines[1]).RootElement.GetProperty("text").GetString());
        Assert.Contains("paused", await process.StandardError.ReadToEndAsync());
    }
}
