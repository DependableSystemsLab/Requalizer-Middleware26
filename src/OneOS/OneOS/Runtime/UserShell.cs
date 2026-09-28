using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Common;

namespace OneOS.Runtime
{
    public class UserShell : Agent
    {
        public string Username { get; }
        public string Session { get; }
        private readonly IVirtualRuntime _virtualRuntime;
        private readonly Channel<byte[]> _inputChannel;
        private Pipe? _outputPipe;
        private string _cwd;
        private string? _foregroundAgentUri;
        // The foreground job: every agent of the pipeline, upstream first (its last agent is _foregroundAgentUri,
        // whose end ends the job). Ctrl+C and a hang-up stop all of them, as a shell's SIGINT reaches the whole
        // foreground process group; stopping only the last left the others running into a closed pipe.
        private IReadOnlyList<string> _foregroundJob = Array.Empty<string>();
        // A batch session (`oneos connect -c`): no prompts, terminal input limited to Ctrl+C, closed at the end.
        private bool _batch, _aborted;
        private TaskCompletionSource _foregroundEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _lastOutputTicks;
        // Agents deleted from the Registry since the current command line started: a foreground agent can end, and
        // be deleted, before this shell has made it the foreground; a job's agents that already ended aren't stopped.
        private readonly HashSet<string> _deletedAgents = new();
        public Dictionary<string, string> Environment { get; } = new Dictionary<string, string>();

        public UserShell(Runtime runtime, string uri, AgentInfo info, ILogger logger, Agent? parent = null) 
            : base(uri, logger, parent)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));
            Username = info.User;
            Session = info.Session;
            _virtualRuntime = new VirtualRuntime(runtime, Session, this);
            
            _cwd = $"/home/{Username}";
            if (!_virtualRuntime.IsDirectory(_cwd))
            {
                _cwd = "/";
            }
            Environment["CWD"] = _cwd;

            _inputChannel = Channel.CreateUnbounded<byte[]>();

            _virtualRuntime.OnRegistryUpdated += action =>
            {
                if (action is DeleteAgentAction deleted) lock (_deletedAgents) _deletedAgents.Add(deleted.Uri);
                if (action is DeleteAgentAction deleteAction && _foregroundAgentUri == deleteAction.Uri)
                {
                    _logger.LogInformation("Foreground agent {Uri} was deleted from registry. Detaching...", _foregroundAgentUri);
                    ForegroundEnded();
                    if (!_batch) _ = SendOutputAsync($"\n{Username}@oneos:~$ ");
                }
            };
        }

        private string ResolvePath(string pathString)
        {
            if (pathString.StartsWith("/")) 
            {
                return NormalizePath(pathString);
            }
            return NormalizePath(_cwd + "/" + pathString);
        }

        private string NormalizePath(string path)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var stack = new Stack<string>();
            foreach (var part in parts)
            {
                if (part == ".") continue;
                if (part == "..") 
                {
                    if (stack.Count > 0) stack.Pop();
                }
                else 
                {
                    stack.Push(part);
                }
            }
            return "/" + string.Join("/", stack.Reverse());
        }

        // Attaches a terminal. With a command, the session is a batch: the command line runs without prompts or
        // input (the terminal's Ctrl+C still interrupts it), and the connection is closed, ending the session, once
        // its last foreground agent has ended. The terminal stays connected until then, so a batch never outlives
        // its terminal's wait: if the terminal goes away first, that's a hang-up, and the foreground is killed.
        internal void ConnectPipes(Pipe? input, Pipe? output, string? command = null)
        {
            if (output != null) _outputPipe = output;
            if (command != null)
            {
                _batch = true;
                if (input != null)
                {
                    input.OnReceive(payload => { if (Array.IndexOf(payload, (byte)3) >= 0) _inputChannel.Writer.TryWrite(new byte[] { 3 }); });
                    _ = HangUpWhenDisconnectedAsync(input.StartListening());
                }
                _ = Task.Run(() => RunBatchAsync(command));
                return;
            }
            if (input != null)
            {
                input.OnReceive(payload => _inputChannel.Writer.TryWrite(payload));
                _ = input.StartListening();
            }
            
            // Send initial prompt
            _ = SendOutputAsync("Welcome to OneOS Terminal!\n");
            _ = SendOutputAsync($"{Username}@oneos:~$ ");
        }

        private Task SendPromptAsync() => _batch ? Task.CompletedTask : SendOutputAsync($"{Username}@oneos:~$ ");

        private void ForegroundEnded()
        {
            _foregroundAgentUri = null;
            _foregroundJob = Array.Empty<string>();
            _foregroundEnded.TrySetResult();
        }

        private async Task RunBatchAsync(string command)
        {
            _logger.LogInformation("UserShell {Username} running a batch: {Command}", Username, command);
            try
            {
                foreach (var line in command.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (_aborted || _outputPipe == null) break;
                    _foregroundEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await ExecuteLineAsync(line, CancellationToken.None);
                    // The registry update that removes the foreground ends the wait; the check covers an agent that
                    // was deleted before it became the foreground. (Not "absent from the Registry": on a runtime
                    // that isn't the leader, the local Registry can apply the new agent after the spawn returns.)
                    while (_foregroundAgentUri is { } foreground && !_foregroundEnded.Task.IsCompleted)
                    {
                        bool deleted;
                        lock (_deletedAgents) deleted = _deletedAgents.Contains(foreground);
                        if (deleted) { ForegroundEnded(); break; }
                        await Task.WhenAny(_foregroundEnded.Task, Task.Delay(1000));
                    }
                }
                // The foreground's last output can still be on its way (it isn't ordered with the registry update).
                var deadline = System.Environment.TickCount64 + 3000;
                while (System.Environment.TickCount64 - Interlocked.Read(ref _lastOutputTicks) < 300 && System.Environment.TickCount64 < deadline)
                    await Task.Delay(100);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "UserShell {Username}: batch failed", Username);
                try { await SendOutputAsync($"oneos: {ex.Message}\n"); } catch (Exception) { }
            }
            finally
            {
                var output = _outputPipe;
                _outputPipe = null;
                if (output != null) { try { await output.CloseAsync(); } catch (Exception) { } }
                await StopAsync(CancellationToken.None);
            }
        }

        // The batch's terminal disconnected: kill the foreground and end the batch.
        private async Task HangUpWhenDisconnectedAsync(Task listening)
        {
            try { await listening; } catch (Exception) { }
            if (_outputPipe == null) return;   // the batch closed the connection itself
            _logger.LogInformation("UserShell {Username}: batch terminal disconnected", Username);
            _aborted = true;
            _outputPipe = null;
            if (_foregroundAgentUri != null) await KillForegroundJobAsync();
            ForegroundEnded();
        }

        // Stops every agent of the foreground job, upstream first, so none writes into a stopped one. An agent
        // that has already ended is skipped.
        private async Task KillForegroundJobAsync()
        {
            var job = _foregroundJob.Count > 0 ? _foregroundJob : (_foregroundAgentUri is { } only ? new[] { only } : Array.Empty<string>());
            foreach (var uri in job)
            {
                bool deleted;
                lock (_deletedAgents) deleted = _deletedAgents.Contains(uri);
                if (deleted || !_virtualRuntime.GetAgents().ContainsKey(uri)) continue;
                try { await _virtualRuntime.KillAgentAsync(uri); }
                catch (Exception ex) { _logger.LogDebug("Stopping {Uri} of the foreground job: {Error}", uri, ex.Message); }
            }
        }

        private async Task SendOutputAsync(string text)
        {
            if (_outputPipe != null)
            {
                await _outputPipe.Send(System.Text.Encoding.UTF8.GetBytes(text));
            }
        }

        protected override Task OnBeginAsync(CancellationToken ct)
        {
            _logger.LogInformation("UserShell for {Username} starting...", Username);
            _ = Task.Run(() => HandleInputChannelAsync(ct), ct);
            return Task.CompletedTask;
        }

        protected override Task OnPauseAsync(CancellationToken ct)
        {
            _logger.LogInformation("UserShell for {Username} pausing...", Username);
            return Task.CompletedTask;
        }

        protected override Task OnEndAsync(CancellationToken ct)
        {
            _logger.LogInformation("UserShell for {Username} stopping...", Username);
            return Task.CompletedTask;
        }

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in Inbox.Reader.ReadAllAsync(ct))
                {
                    if (msg is Envelope env && (env.Channel == "stdout" || env.Channel == "stderr"))
                    {
                        Interlocked.Exchange(ref _lastOutputTicks, System.Environment.TickCount64);
                        var text = System.Text.Encoding.UTF8.GetString(env.Payload);
                        await SendOutputAsync(text);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful cancellation
            }
        }

        private async Task HandleInputChannelAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var inputBuffer in _inputChannel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        if (inputBuffer.Length == 1 && inputBuffer[0] == 3) // Ctrl+C
                        {
                            _logger.LogInformation("Received kill signal (Ctrl+C) from terminal");
                            // A batch stops at Ctrl+C, like `bash -c`. Set before the kill: the kill's registry
                            // update ends the batch's wait for the foreground.
                            if (_batch) _aborted = true;

                            if (_foregroundAgentUri != null)
                            {
                                var killed = _foregroundJob.Count > 1 ? $"Foreground pipeline ({_foregroundJob.Count} agents)" : $"Foreground agent {_foregroundAgentUri}";
                                await KillForegroundJobAsync();
                                await SendOutputAsync($"\n{killed} killed via Ctrl+C.\n");
                            }
                            else
                            {
                                await SendOutputAsync("^C\n");
                            }
                            ForegroundEnded();
                            await SendPromptAsync();
                            continue;
                        }

                        if (_foregroundAgentUri != null)
                        {
                            var env = new Envelope
                            {
                                MessageId = Guid.NewGuid(),
                                SenderId = _virtualRuntime.HostRuntimeID,
                                SenderAgentUri = URI,
                                Strategy = RoutingStrategy.Direct,
                                Target = _foregroundAgentUri,
                                Channel = "stdin",
                                Payload = inputBuffer
                            };
                            
                            if (OnMessageOut != null)
                            {
                                await OnMessageOut(env);
                            }
                            continue;
                        }

                        var inputStr = System.Text.Encoding.UTF8.GetString(inputBuffer).Trim();
                        await ExecuteLineAsync(inputStr, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "UserShell encountered an error writing output. Socket may be closed.");
                        _outputPipe = null; // Detach pipe on failure to prevent repeated errors
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful cancellation
            }
        }

        // Runs one command line: a built-in, or a spawned (pipeline of) agent(s) that becomes the foreground.
        private async Task ExecuteLineAsync(string inputStr, CancellationToken ct)
        {
            lock (_deletedAgents) _deletedAgents.Clear();
            if (string.IsNullOrEmpty(inputStr))
            {
                await SendPromptAsync();
                return;
            }

            _logger.LogInformation("UserShell {Username} received command: {Command}", Username, inputStr);

            var pipelineStrParts = inputStr.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            
            if (pipelineStrParts.Length > 1)
            {
                var pipelineArgs = new List<(string cmd, IReadOnlyList<string> args)>();
                foreach (var part in pipelineStrParts)
                {
                    var words = OneOS.Common.CommandLine.Split(part);
                    pipelineArgs.Add((words[0].ToLowerInvariant(), words.Skip(1).ToList()));
                }
                
                var agents = await _virtualRuntime.SpawnPipelineAsync(Session, pipelineArgs, Environment, true);
                _foregroundJob = agents.Select(a => a.URI).ToList();
                _foregroundAgentUri = agents.Last().URI;
                _logger.LogInformation("Foreground agent set to {Uri}", _foregroundAgentUri);
            }
            else
            {
                var parts = inputStr.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var cmd = parts[0].ToLowerInvariant();
                var args = parts.Length > 1 ? parts[1] : "";

                switch (cmd)
                {
                    case "help":
                        await SendOutputAsync("Available commands:\n  help  - Show this message\n  whoami - Show current user\n  echo  - Echo text\n  ps    - List active agents\n  kill  - Kill an active agent by GPID or URI\n  pwd   - Print working directory\n  cd    - Change directory\n  ls    - List directory contents\n  mkdir - Make directory\n  rmdir - Remove directory\n  touch - Create empty file\n  rm    - Remove file\n  mv    - Move file/directory\n  cp    - Copy file/directory\n  node  - Start JavaScript Agent\n  python - Start Python Agent\n  graph - Check, plan, spawn, list and stop dataflow graphs\n  exit  - Terminate session\n");
                        break;
                    case "kill":
                        if (string.IsNullOrEmpty(args))
                        {
                            await SendOutputAsync("kill: missing operand\n");
                        }
                        else
                        {
                            try
                            {
                                string targetUri = args;
                                if (int.TryParse(args, out int gpid))
                                {
                                    var targetAgent = _virtualRuntime.GetAgents().Values.FirstOrDefault(a => a.GPID == gpid);
                                    if (targetAgent != null)
                                    {
                                        targetUri = targetAgent.URI;
                                    }
                                }

                                await _virtualRuntime.KillAgentAsync(targetUri);
                                await SendOutputAsync($"Agent {targetUri} killed.\n");
                            }
                            catch (Exception ex)
                            {
                                await SendOutputAsync($"kill failed: {ex.Message}\n");
                            }
                        }
                        break;
                    case "node":
                    case "python":
                        var agentInfo = await _virtualRuntime.SpawnProcessAgentAsync(Session, cmd, OneOS.Common.CommandLine.Split(args), Environment, true);
                        _foregroundJob = new[] { agentInfo.URI };
                        _foregroundAgentUri = agentInfo.URI;
                        _logger.LogInformation("Foreground agent set to {Uri}", _foregroundAgentUri);
                        break;
                case "graph":
                    await SendOutputAsync(await Graphs.GraphShellCommand.RunAsync(_virtualRuntime, args, ResolvePath, _cwd));
                    break;
                case "whoami":
                    await SendOutputAsync($"{Username}\n");
                    break;
                case "echo":
                    await SendOutputAsync($"{args}\n");
                    break;
                case "ps":
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine(string.Format("{0,-10} {1,-50} {2}", "GPID", "URI", "Runtime"));
                    foreach (var kvp in _virtualRuntime.GetAgents())
                    {
                        sb.AppendLine(string.Format("{0,-10} {1,-50} {2}", kvp.Value.GPID, kvp.Key, kvp.Value.Runtime));
                    }
                    await SendOutputAsync(sb.ToString());
                    break;
                case "pwd":
                    await SendOutputAsync($"{_cwd}\n");
                    break;
                case "cd":
                    var targetCd = string.IsNullOrEmpty(args) ? $"/home/{Username}" : ResolvePath(args);
                    if (_virtualRuntime.IsDirectory(targetCd))
                    {
                        _cwd = targetCd;
                        Environment["CWD"] = _cwd;
                    }
                    else
                    {
                        await SendOutputAsync($"{args}: No such directory\n");
                    }
                    break;
                case "ls":
                    var targetLs = string.IsNullOrEmpty(args) ? _cwd : ResolvePath(args);
                    var node = _virtualRuntime.GetFileSystemNode(targetLs);
                    if (node != null)
                    {
                        if (node is DirectoryNode dir)
                        {
                            var lsOutput = new System.Text.StringBuilder();
                            foreach (var child in dir.Children)
                            {
                                var type = child.Value is DirectoryNode ? "DIR " : "FILE";
                                lsOutput.AppendLine($"{type}\t{child.Key}");
                            }
                            await SendOutputAsync(lsOutput.ToString());
                        }
                        else
                        {
                            await SendOutputAsync($"{targetLs} is a file\n");
                        }
                    }
                    else
                    {
                        await SendOutputAsync($"{args}: No such file or directory\n");
                    }
                    break;
                case "mkdir":
                    if (string.IsNullOrEmpty(args)) await SendOutputAsync("mkdir: missing operand\n");
                    else
                    {
                        try { await _virtualRuntime.CreateDirectory(ResolvePath(args)); }
                        catch (Exception ex) { await SendOutputAsync($"mkdir: {ex.Message}\n"); }
                    }
                    break;
                case "cat":
                    if (string.IsNullOrEmpty(args)) await SendOutputAsync("cat: missing operand\n");
                    else
                    {
                        try 
                        { 
                            if (_outputPipe != null)
                            {
                                await _virtualRuntime.CreateFileReadStreamAsync(ResolvePath(args), _cwd, _outputPipe);
                            }
                            else
                            {
                                await SendOutputAsync("cat: output pipe is not connected\n");
                            }
                        }
                        catch (Exception ex) { await SendOutputAsync($"cat: {ex.Message}\n"); }
                    }
                    break;
                case "rmdir":
                case "rm":
                    if (string.IsNullOrEmpty(args)) await SendOutputAsync($"{cmd}: missing operand\n");
                    else
                    {
                        try { await _virtualRuntime.RemoveFileSystemNode(ResolvePath(args)); }
                        catch (Exception ex) { await SendOutputAsync($"{cmd}: {ex.Message}\n"); }
                    }
                    break;
                case "touch":
                    if (string.IsNullOrEmpty(args)) await SendOutputAsync("touch: missing operand\n");
                    else
                    {
                        try { await _virtualRuntime.CreateFile(ResolvePath(args)); }
                        catch (Exception ex) { await SendOutputAsync($"touch: {ex.Message}\n"); }
                    }
                    break;
                case "mv":
                    var mvArgs = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (mvArgs.Length < 2) await SendOutputAsync("mv: missing operand\n");
                    else
                    {
                        try { await _virtualRuntime.MoveFileSystemNode(ResolvePath(mvArgs[0]), ResolvePath(mvArgs[1])); }
                        catch (Exception ex) { await SendOutputAsync($"mv: {ex.Message}\n"); }
                    }
                    break;
                case "cp":
                    var cpArgs = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (cpArgs.Length < 2) await SendOutputAsync("cp: missing operand\n");
                    else
                    {
                        try { await _virtualRuntime.CopyFileSystemNode(ResolvePath(cpArgs[0]), ResolvePath(cpArgs[1])); }
                        catch (Exception ex) { await SendOutputAsync($"cp: {ex.Message}\n"); }
                    }
                    break;
                case "wget":
                    var wgetArgs = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (wgetArgs.Length < 1) await SendOutputAsync("wget: missing url\n");
                    else
                    {
                        try 
                        { 
                            var saveName = wgetArgs.Length > 1 ? wgetArgs[1] : null;
                            var downloadedPath = await _virtualRuntime.DownloadFile(wgetArgs[0], _cwd, saveName);
                            await SendOutputAsync($"Saved to {downloadedPath}\n");
                        }
                        catch (Exception ex) { await SendOutputAsync($"wget: {ex.Message}\n"); }
                    }
                    break;
                case "sessions":
                    var sb1 = new System.Text.StringBuilder();
                    sb1.AppendLine(string.Format("{0,-36} {1,-8} {2,-30} {3,-40}", "SessionKey", "User", "ShellUri", "ClientUri"));
                    foreach (var kvp in _virtualRuntime.GetSessions())
                    {
                        sb1.AppendLine(string.Format("{0,-36} {1,-8} {2,-30} {3,-40}", kvp.Key, kvp.Value.User, kvp.Value.ShellUri, kvp.Value.ClientUri));
                    }
                    await SendOutputAsync(sb1.ToString());
                    break;
                case "exit":
                    if (_batch) { _aborted = true; return; }   // ends the command line; the batch closes the session
                    await SendOutputAsync("Disconnected.\n");
                    _outputPipe = null; // Explicitly detach

                    await StopAsync(ct);
                    
                    return; // Skip prompt
                default:
                    await SendOutputAsync($"Command not found: {cmd}\n");
                    break;
            }
            }

            if (_outputPipe != null)
            {
                await SendPromptAsync();
            }
        }
    }
}
