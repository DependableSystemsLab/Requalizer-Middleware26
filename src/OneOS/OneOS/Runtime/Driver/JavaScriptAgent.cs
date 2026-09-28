using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Common;
using OneOS.Runtime.Scheduling;

namespace OneOS.Runtime.Driver
{
    // A Node.js process in the OneOS JavaScript environment (oneos.js, JavaScriptEnvironment/, plan step 7).
    // Before it starts, its program is instrumented (instrument.js), so it runs through
    // require('oneos').bootstrap: `fs`, `net`, `process.env` and the stdio streams go through OneOS, and its
    // state can be checkpointed. It talks to this agent over inherited descriptors (NodeIpcChannel):
    // ONEOS_IPC_FDS = control in, control out, sync in, sync out. Interactive `node` (no program) runs as is.
    public class JavaScriptAgent : ProcessAgent
    {
        private readonly bool _isInteractiveMode;
        private static readonly byte[] NewLineBytes = Encoding.UTF8.GetBytes(Environment.NewLine);

        private int _ipcBase = -1;                       // control in, control out, sync in, sync out: _ipcBase .. +3
        private NodeIpcChannel? _ipc;
        private readonly TaskCompletionSource<NodeIpcChannel> _ipcReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _localScriptDirectory;           // for relative requires of a program outside the file system

        public JavaScriptAgent(Runtime runtime, string uri, AgentInfo agentInfo, ILogger logger, Agent? parent = null)
            : base(runtime, uri, agentInfo, logger, parent)
        {
            _logger.LogInformation("Spawned new JavaScript agent {URI} - {FileName} {Arguments}", uri, _processInfo.FileName, string.Join(" ", _processInfo.ArgumentList));

            if (_processInfo.ArgumentList.Count == 0)
            {
                _isInteractiveMode = true;
                _processInfo.ArgumentList.Add("--interactive");
            }
            else
            {
                _isInteractiveMode = false;
            }
        }

        // The control channel, once the process has opened it.
        public Task<NodeIpcChannel> Ipc => _ipcReady.Task;

        private string Cwd => _agentInfo.Environment.TryGetValue("CWD", out var c) && c.Length > 0 ? c : "/";

        // --- Instrumentation (before the process starts) ---

        // The snapshot format of CheckpointAsync: oneos.js's root.snapshot(), as JSON.
        public const string SnapshotFormat = "oneos-js";

        protected override bool RestoresWithDescriptors => true;

        // The program runs instrumented, or, restoring from a checkpoint, as the program restore.js makes of
        // the snapshot (the command is ready before the descriptor shim wraps it).
        protected override async Task ResolveToLocalProcess()
        {
            await base.ResolveToLocalProcess();
            if (_isInteractiveMode) return;
            var temp = _runtime.Config.TempPath;
            if (!JavaScriptEnvironmentInstaller.IsInstalled(temp))
                throw new InvalidOperationException($"the JavaScript environment isn't installed in {temp}; run `oneos config`");
            var args = _processInfo.ArgumentList;
            int i = Enumerable.Range(0, args.Count).FirstOrDefault(k => !args[k].StartsWith("-"), -1);
            if (i < 0) return;
            var script = Path.GetFullPath(args[i], _processInfo.WorkingDirectory);
            _localScriptDirectory = Path.GetDirectoryName(script);
            var id = new string(URI.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_').ToArray());
            var name = Path.GetFileNameWithoutExtension(script);

            if (RestoringFrom is { } snapshot)
            {
                if (snapshot.Format != SnapshotFormat)
                    throw new NotSupportedException($"a JavaScript agent restores '{SnapshotFormat}' snapshots, not '{snapshot.Format}'");
                var snapshotFile = Path.Combine(temp, $"{name}.snap.{id}.json");
                var restored = Path.Combine(temp, $"{name}.restored.{id}.js");
                await File.WriteAllBytesAsync(snapshotFile, snapshot.Data);
                var (ok, output) = await RunNodeAsync("restore.js", snapshotFile, restored);
                if (!ok || !File.Exists(restored)) throw new NotSupportedException($"restoring {script} from its checkpoint failed: {output}");
                args[i] = restored;
                _logger.LogInformation("JavaScriptAgent {URI}: restoring {Script} from a checkpoint", URI, script);
                return;
            }

            // Whether the program runs with the IDM (argv[6]): its plan says so (DIFT Enabled). Agents outside a
            // graph have no plan and run without it. With the IDM, its DIFT policy goes next to the instrumented
            // program, as a module under a random name that instrument.js requires (argv[7]).
            var idm = GraphAgent()?.Dift == Scheduling.DiftMode.Enabled;
            var instrumented = Path.Combine(temp, $"{name}.inst.{id}.js");
            var instrumentArgs = new List<string> { script, instrumented, URI, Cwd, idm ? "true" : "false" };
            if (idm)
            {
                var graph = Graphs.GraphSerialization.DeserializeGraph(_runtime.Registry.Graphs[_agentInfo.Graph!].GraphJson);
                var policy = Path.Combine(temp, $"{name}.dift.{Guid.NewGuid():N}.js");
                await File.WriteAllTextAsync(policy, DiftPolicyModule.Generate(graph, URI));
                instrumentArgs.Add(policy);
            }
            var (instrumentedOk, instrumentOutput) = await RunNodeAsync("instrument.js", instrumentArgs.ToArray());
            if (!instrumentedOk || !File.Exists(instrumented))
                throw new InvalidOperationException($"instrumenting {script} failed: {instrumentOutput}");
            args[i] = instrumented;
            _logger.LogDebug("JavaScriptAgent {URI}: instrumented {Script} as {Instrumented}", URI, script, instrumented);
        }

        // Runs one of oneos.js's tools (instrument.js, restore.js) to completion.
        private async Task<(bool Ok, string Output)> RunNodeAsync(string tool, params string[] args)
        {
            var temp = _runtime.Config.TempPath;
            var psi = new ProcessStartInfo("node")
            {
                WorkingDirectory = temp, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.ArgumentList.Add(Path.Combine(JavaScriptEnvironmentInstaller.ModuleDirectory(temp), tool));
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var node = Process.Start(psi)!;
            var stderr = node.StandardError.ReadToEndAsync();
            var stdout = node.StandardOutput.ReadToEndAsync();
            await node.WaitForExitAsync();
            return (node.ExitCode == 0, $"{(await stderr).Trim()} {(await stdout).Trim()}".Trim());
        }

        protected override Task RestoreProcessAsync(ProcessSnapshot snapshot, CancellationToken ct)
        {
            // The restored program is already the command (ResolveToLocalProcess).
            if (!_process.Start()) throw new InvalidOperationException($"Failed to start the restored process for {URI}");
            return Task.CompletedTask;
        }

        // A snapshot of the program (oneos.js root.snapshot()): its timers paused while it is taken, and the
        // port adapter holds its input meanwhile (L§9.3).
        public override async Task<ProcessSnapshot> CheckpointAsync(CancellationToken ct)
        {
            if (_isInteractiveMode) throw new NotSupportedException("an interactive node process can't be checkpointed");
            var ipc = await Ipc.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var timeout = TimeSpan.FromSeconds(30);
            await ipc.RequestAsync("pause").WaitAsync(timeout, ct);
            try
            {
                var snapshot = await ipc.RequestAsync("checkpoint").WaitAsync(timeout, ct);
                return new ProcessSnapshot(Encoding.UTF8.GetBytes(snapshot.GetRawText()), SnapshotFormat);
            }
            finally
            {
                try { await ipc.RequestAsync("resume").WaitAsync(timeout); }
                catch (Exception ex) { _logger.LogWarning(ex, "JavaScriptAgent {URI}: resume after a checkpoint failed", URI); }
            }
        }

        // --- The IPC channels ---

        protected override IReadOnlyList<ExtraDescriptor> ControlDescriptors(int firstFree)
        {
            if (_isInteractiveMode) return Array.Empty<ExtraDescriptor>();
            _ipcBase = firstFree;
            _processInfo.Environment["ONEOS_IPC_FDS"] = string.Join(",", Enumerable.Range(firstFree, 4));
            return new[]
            {
                new ExtraDescriptor(firstFree, ProcessReads: true),        // control: runtime -> process
                new ExtraDescriptor(firstFree + 1, ProcessReads: false),   // control: process -> runtime
                new ExtraDescriptor(firstFree + 2, ProcessReads: true),    // sync: runtime -> process
                new ExtraDescriptor(firstFree + 3, ProcessReads: false),   // sync: process -> runtime
            };
        }

        protected override void OnProcessStarted()
        {
            if (_ipcBase < 0 || Shim is not { } shim) return;
            _ = Task.Run(async () =>
            {
                var toProcess = await shim.Stream(_ipcBase);
                var fromProcess = await shim.Stream(_ipcBase + 1);
                var syncTo = await shim.Stream(_ipcBase + 2);
                var syncFrom = await shim.Stream(_ipcBase + 3);
                if (toProcess == null || fromProcess == null || syncTo == null || syncFrom == null)
                {
                    _ipcReady.TrySetException(new IOException("the process exited before opening its IPC channels"));
                    return;
                }
                var ipc = new NodeIpcChannel(fromProcess, toProcess, HandleCallAsync, m => _logger.LogWarning("JavaScriptAgent {URI}: {Message}", URI, m));
                ipc.Start();
                _ = ipc.ServeSyncAsync(syncFrom, syncTo);
                _ipc = ipc;
                _virtualRuntime.OnRegistryUpdated += OnRegistryUpdated;
                _ipcReady.TrySetResult(ipc);
            });
        }

        protected override Task OnEndAsync(CancellationToken ct)
        {
            _virtualRuntime.OnRegistryUpdated -= OnRegistryUpdated;
            // Its sockets go with it (a graph agent that restarts claims them again).
            foreach (var pipe in _devicePipes) _ = pipe.CloseAsync();
            foreach (var port in _claimedPorts.Keys.ToList())
                _ = _runtime.NetworkManager.ReleaseAsync(port, URI).ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            _claimedPorts.Clear();
            _ipcReady.TrySetException(new ObjectDisposedException(URI));
            return base.OnEndAsync(ct);
        }

        private void OnRegistryUpdated(RegistryAction action)
        {
            if (_ipc is { } ipc) _ = ipc.RequestAsync("emitRuntimeEvent", "registryUpdated", action.GetType().Name)
                .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        // --- Calls from the program (oneos.js Runtime.js) ---

        private async Task<object?> HandleCallAsync(string method, JsonElement[] args)
        {
            string Arg(int i) => args.Length > i ? args[i].ValueKind == JsonValueKind.String ? args[i].GetString()! : args[i].ToString() : "";
            switch (method)
            {
                case "ReadFile":
                    return Convert.ToBase64String(await ReadBytesAsync(Arg(0)));
                case "ReadTextFile":
                    return Encoding.UTF8.GetString(await ReadBytesAsync(Arg(0)));
                case "WriteFile":
                    await _virtualRuntime.WriteFileAsync(Resolve(Arg(0)), "/", Convert.FromBase64String(Arg(1)));
                    return true;
                case "WriteTextFile":
                    await _virtualRuntime.WriteFileAsync(Resolve(Arg(0)), "/", Encoding.UTF8.GetBytes(Arg(1)));
                    return true;
                case "AppendTextFile":
                {
                    var path = Resolve(Arg(0));
                    var existing = _virtualRuntime.IsFile(path) ? await _virtualRuntime.ReadFileAsync(path, "/") : Array.Empty<byte>();
                    await _virtualRuntime.WriteFileAsync(path, "/", existing.Concat(Encoding.UTF8.GetBytes(Arg(1))).ToArray());
                    return true;
                }
                case "GetFileStats":
                    return _virtualRuntime.GetFileSystemNode(Resolve(Arg(0))) switch
                    {
                        DirectoryNode => new { type = "directory" },
                        FileNode f => new { type = "file", value = new { size = f.Size } },
                        SocketInfo => new { type = "socket" },
                        _ => throw new FileNotFoundException(Arg(0)),
                    };
                case "ReadDirectory":
                    if (_virtualRuntime.GetFileSystemNode(Resolve(Arg(0))) is not DirectoryNode dir) throw new FileNotFoundException(Arg(0));
                    return new
                    {
                        value = dir.Children.ToDictionary(kv => kv.Key, kv => new { type = kv.Value switch { DirectoryNode => "directory", SocketInfo => "socket", _ => "file" } }),
                    };
                case "CreateReadStream":
                    return OpenReadStream(Arg(0), 0);
                case "RestoreReadStream":
                    return OpenReadStream(Arg(0), args.Length > 1 && args[1].TryGetInt64(out var skip) ? skip : 0);
                case "CreateWriteStream":
                    return OpenWriteStream(Arg(0));
                case "CreateFfmpegStream":
                case "CreateVideoInputStream":
                    return await OpenDeviceStreamAsync(Arg(0));
                case "CreateServer":
                    return await CreateServerAsync(args.Length > 0 && args[0].TryGetInt32(out var p) ? p : 0,
                        args.Length > 1 && args[1].ValueKind == JsonValueKind.True);
                case "ReleaseServer":
                    if (args.Length > 0 && args[0].TryGetInt32(out var released))
                    {
                        _claimedPorts.TryRemove(released, out _);
                        await _runtime.NetworkManager.ReleaseAsync(released, URI);
                    }
                    return true;
                case "LogIn":
                    return await _virtualRuntime.SignInUser(Arg(0), Arg(1), URI);
                case "GetRegistry":
                    return new
                    {
                        agents = _virtualRuntime.GetAgents().Values.Select(a => new { uri = a.URI, runtime = a.Runtime, user = a.User, binaryPath = a.BinaryPath, arguments = a.Arguments, graph = a.Graph }).ToList(),
                    };
                case "GetRuntimes":
                    return new[] { _runtime.Config.ID }.Concat(_runtime.Config.Peers.Keys).ToList();
                default:
                    throw new NotSupportedException($"'{method}' is not available in OneOS-V6");
            }
        }

        private readonly System.Collections.Concurrent.ConcurrentBag<Pipe> _devicePipes = new();

        // An I/O device anywhere in the cluster (IOManager), streamed to the program on the control channel.
        private async Task<uint> OpenDeviceStreamAsync(string device)
        {
            var ipc = _ipc!;
            var pipe = await _runtime.IOManager.OpenReadStreamAsync(device);
            _devicePipes.Add(pipe);
            var id = ipc.NewStreamId();
            // In order: the pipe delivers chunks one at a time, and each goes out before the next. Once the
            // program is gone, the stream is closed, and the driver lets go of it.
            pipe.OnReceive(chunk =>
            {
                if (ipc.Completion.IsCompleted) { _ = pipe.CloseAsync(); return; }
                ipc.SendDataAsync(id, chunk).GetAwaiter().GetResult();
            });
            _ = Task.Run(async () =>
            {
                try { await pipe.StartListening(); await ipc.EndStreamAsync(id); }
                catch (Exception ex) { await ipc.EndStreamAsync(id, ex.Message); }
            });
            return id;
        }

        // --- Cluster-wide sockets (plan step 8) ---

        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _claimedPorts = new();

        // listen(port): claims the port across the cluster, and tells the program where to listen: the OneOS
        // loopback address (every runtime proxies the port to it), or the public interface for an
        // independent socket. Errors reach the program as "<code>: <message>".
        private async Task<object> CreateServerAsync(int port, bool independent)
        {
            // A graph agent has external input only where its plan says so (the IDM is configured from it).
            if (GraphAgent() is { } agent && !JavaScriptSockets.Allows(agent.Idm?.Sources.Select(s => s.Location) ?? Array.Empty<string>(), port))
                throw new InvalidOperationException($"EACCES: graph node '{agent.NodeName}' was not planned with a network socket on port {port} (its program wasn't found to listen); it can't listen");
            int claimed;
            try { claimed = await _runtime.NetworkManager.ClaimAsync(port, URI, independent); }
            catch (Kernel.SocketClaimException ex) { throw new InvalidOperationException($"{ex.Code}: {ex.Message}"); }
            _claimedPorts[claimed] = true;
            return new { port = claimed, host = independent ? "0.0.0.0" : _runtime.Config.LoopbackAddress };
        }

        // This agent's plan entry, when it is a graph agent.
        private Scheduling.GraphAgentInfo? GraphAgent()
        {
            if (GraphInstance() is not { } instance) return null;
            var agentId = URI[(URI.IndexOf("/graphs/", StringComparison.Ordinal) + "/graphs/".Length)..];
            return instance.Agents.FirstOrDefault(a => a.AgentId == agentId);
        }

        private Scheduling.GraphInstanceInfo? GraphInstance()
        {
            if (string.IsNullOrEmpty(_agentInfo.Graph) || !_runtime.Registry.Graphs.ContainsKey(_agentInfo.Graph)) return null;
            return _runtime.GraphManager.Instances.TryGetValue(_agentInfo.Graph, out var instance) ? instance : null;
        }

        // A path as the program names it: relative to its virtual working directory.
        private string Resolve(string path)
        {
            var parts = new List<string>();
            foreach (var p in ((path.StartsWith('/') ? "" : Cwd.TrimEnd('/') + "/") + path).Split('/'))
            {
                if (p is "" or ".") continue;
                if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else parts.Add(p);
            }
            return "/" + string.Join("/", parts);
        }

        // The distributed file system first; a program that isn't in it (a local path) may read local files
        // by absolute path or next to itself (its relative requires).
        private string? LocalPath(string path)
        {
            if (Path.IsPathRooted(path) && File.Exists(path)) return path;
            if (_localScriptDirectory != null && !Path.IsPathRooted(path) && File.Exists(Path.Combine(_localScriptDirectory, path)))
                return Path.Combine(_localScriptDirectory, path);
            return null;
        }

        private async Task<byte[]> ReadBytesAsync(string path)
        {
            var resolved = Resolve(path);
            if (_virtualRuntime.IsFile(resolved)) return await _virtualRuntime.ReadFileAsync(resolved, "/");
            if (LocalPath(path) is { } local) return await File.ReadAllBytesAsync(local);
            throw new FileNotFoundException(path);
        }

        private uint OpenReadStream(string path, long skip)
        {
            var ipc = _ipc!;
            var resolved = Resolve(path);
            var local = _virtualRuntime.IsFile(resolved) ? null : LocalPath(path) ?? throw new FileNotFoundException(path);
            var id = ipc.NewStreamId();
            _ = Task.Run(async () =>
            {
                long toSkip = skip;
                async Task Send(byte[] chunk)
                {
                    if (toSkip >= chunk.Length) { toSkip -= chunk.Length; return; }
                    await ipc.SendDataAsync(id, chunk.AsMemory((int)toSkip));
                    toSkip = 0;
                }
                try
                {
                    if (local == null) await _virtualRuntime.CreateFileReadStreamAsync(resolved, "/", new ForwardingPipe(Send));
                    else
                    {
                        await using var file = File.OpenRead(local);
                        var buffer = new byte[65536];
                        int n;
                        while ((n = await file.ReadAsync(buffer)) > 0) await Send(buffer[..n]);
                    }
                    await ipc.EndStreamAsync(id);
                }
                catch (Exception ex) { await ipc.EndStreamAsync(id, ex.Message); }
            });
            return id;
        }

        private uint OpenWriteStream(string path)
        {
            var ipc = _ipc!;
            var resolved = Resolve(path);
            var id = ipc.NewStreamId();
            var channel = Channel.CreateUnbounded<byte[]>();
            ipc.AcceptStream(id, channel.Writer);
            _ = Task.Run(async () =>
            {
                try { await _virtualRuntime.CreateFileWriteStreamAsync(resolved, "/", new LocalRawInputPipe(channel)); }
                catch (Exception ex) { _logger.LogWarning(ex, "JavaScriptAgent {URI}: write stream to {Path} failed", URI, resolved); }
            });
            return id;
        }

        // The file system's read stream, forwarded to the program.
        private sealed class ForwardingPipe : Pipe
        {
            private readonly Func<byte[], Task> _send;
            public ForwardingPipe(Func<byte[], Task> send) { _send = send; }
            public override Task Send(byte[] payload) => _send(payload);
            public override Task CloseAsync() => Task.CompletedTask;
        }

        // --- Interactive mode ---

        protected override void HandleStdin(byte[] payload)
        {
            if (_isInteractiveMode)
            {
                lock (_process.StandardInput)
                {
                    try
                    {
                        if (!_process.HasExited)
                        {
                            _process.StandardInput.BaseStream.Write(payload, 0, payload.Length);
                            _process.StandardInput.BaseStream.Write(NewLineBytes, 0, NewLineBytes.Length);
                            _process.StandardInput.BaseStream.Flush();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to write to stdin of process {PID}.", _process.Id);
                    }
                }
            }
            else
            {
                base.HandleStdin(payload);
            }
        }
    }
}
