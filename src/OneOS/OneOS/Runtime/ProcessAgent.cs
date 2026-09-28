using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Common;

namespace OneOS.Runtime
{
    public class ProcessAgent : Agent, IProcessEndpoint
    {
        private const int BufferSize = 131072;

        protected readonly Runtime _runtime;
        protected readonly IVirtualRuntime _virtualRuntime;
        protected readonly AgentInfo _agentInfo;
        
        protected ProcessStartInfo _processInfo;
        protected Process _process;
        protected string _localAbsolutePath = string.Empty;

        public Pipe? InputPipe { get; private set; }
        public Pipe? OutputPipe { get; set; }

        // The OS process id, while the process has started and not been disposed.
        public int? ProcessId
        {
            get { try { return _process.Id; } catch (Exception) { return null; } }
        }

        public long BytesIn { get; private set; }
        public long BytesOut { get; private set; }
        public long MessagesIn { get; private set; }
        public long MessagesOut { get; private set; }

        public ProcessAgent(Runtime runtime, string uri, AgentInfo agentInfo, ILogger logger, Agent? parent = null) 
            : base(uri, logger, parent)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _virtualRuntime = new VirtualRuntime(_runtime, agentInfo.Session, this);
            _agentInfo = agentInfo ?? throw new ArgumentNullException(nameof(agentInfo));

            _processInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                WorkingDirectory = _runtime.Config.TempPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                FileName = _agentInfo.BinaryPath,
            };
            foreach (var arg in _agentInfo.Arguments) _processInfo.ArgumentList.Add(arg);

            _process = new Process
            {
                StartInfo = _processInfo,
                EnableRaisingEvents = true
            };

            _process.Exited += (sender, evt) =>
            {
                _ = _shim?.ProcessExited();
                _logger.LogInformation("ProcessAgent {URI} process {PID} exited.", URI, _process.Id);
                if (_lineMode || _rawMode) _ = OnLineModeExitAsync();
                else _ = StopAsync();
            };

            SetEnvironment();
        }

        protected virtual void SetEnvironment()
        {
            // The address user components bind their listening sockets to (plan step 8).
            _processInfo.Environment["ONEOS_LOOPBACK"] = _runtime.Config.LoopbackAddress;
            if (_agentInfo.Environment != null)
            {
                foreach (var item in _agentInfo.Environment)
                {
                    _processInfo.Environment["ONEOS_" + item.Key] = item.Value;
                }
            }
        }

        // A script operand that names a file in the distributed file system is fetched to the runtime's temp
        // directory first. The script is the first operand after interpreter flags (drivers add e.g. -u).
        protected virtual async Task ResolveToLocalProcess()
        {
            if (string.IsNullOrWhiteSpace(_agentInfo.BinaryPath)) return;

            var args = _processInfo.ArgumentList;
            int i = Enumerable.Range(0, args.Count).FirstOrDefault(k => !args[k].StartsWith("-"), -1);
            if (i < 0) return;

            string cwd = _agentInfo.Environment.TryGetValue("CWD", out var c) ? c : "/";
            var absPath = args[i].StartsWith("/") ? args[i] : cwd.TrimEnd('/') + "/" + args[i];
            if (!_virtualRuntime.IsFile(absPath)) return;   // a local path, or not a script

            _logger.LogDebug("Script {Path} found in VFS. Fetching to local TempPath...", absPath);
            System.IO.Directory.CreateDirectory(_runtime.Config.TempPath);
            var localPath = System.IO.Path.Combine(_runtime.Config.TempPath, System.IO.Path.GetFileName(absPath));
            await System.IO.File.WriteAllBytesAsync(localPath, await _virtualRuntime.ReadFileAsync(absPath, cwd));
            args[i] = localPath;
        }

        protected override async Task OnBeginAsync(CancellationToken ct)
        {
            _logger.LogInformation("ProcessAgent {URI} starting...", URI);

            // A start can fail before the process runs (e.g. a checkpoint it can't restore) and be retried:
            // every attempt begins from the original command, not one a previous attempt rewrote.
            if (_originalArguments == null)
            {
                _originalFileName = _processInfo.FileName;
                _originalArguments = _processInfo.ArgumentList.ToList();
            }
            else
            {
                _processInfo.FileName = _originalFileName!;
                _processInfo.ArgumentList.Clear();
                foreach (var a in _originalArguments) _processInfo.ArgumentList.Add(a);
                if (_shim != null) { _ = _shim.DisposeAsync().AsTask(); _shim = null; }
            }
            
            await ResolveToLocalProcess();
            // The working directory (the runtime's temp path) may not exist yet on a fresh runtime.
            System.IO.Directory.CreateDirectory(_processInfo.WorkingDirectory);

            // Extra descriptors: a graph node's ports (raw mode), then any a subclass asks for (e.g. the
            // JavaScript control channel), all through one descriptor shim.
            Action<string> log = m => _logger.LogWarning("ProcessAgent {URI}: {Message}", URI, m);
            var ports = _rawMode ? _rawExtra! : Array.Empty<ExtraDescriptor>();
            var control = ControlDescriptors(ports.Count == 0 ? 3 : ports.Max(e => e.Fd) + 1);
            if (ports.Count + control.Count > 0)
            {
                _shim = new DescriptorShim(ports.Concat(control).ToList(), _processInfo.WorkingDirectory, log);
                _shim.Prepare(_processInfo);
            }
            if (_rawMode)
            {
                _raw = new RawProcessIo(ports, _processInfo.WorkingDirectory, log, _shim);
                _raw.Output += (fd, data) => _output?.Invoke(fd, data);
                _raw.OutputEnded += fd => _outputEnded?.Invoke(fd);
                _raw.Prepare(_processInfo);
            }

            if (_restoreFrom is { } snapshot)
            {
                await RestoreProcessAsync(snapshot, ct);
                _logger.LogInformation("ProcessAgent {URI} restored process {PID} from a checkpoint.", URI, _process.Id);
            }
            else if (!_process.Start())
            {
                throw new InvalidOperationException($"Failed to start process for Agent {URI}");
            }
            else
            {
                _logger.LogInformation("ProcessAgent {URI} started process {PID}.", URI, _process.Id);
            }

            _shim?.Open();
            OnProcessStarted();

            if (_rawMode)
            {
                _raw!.Attach(_process);
                return;
            }

            if (_lineMode)
            {
                _ = Task.Run(() => ReadLinesAsync(_process.StandardOutput, line => _stdoutLine?.Invoke(line), _stdoutDrained));
                _ = Task.Run(() => ReadLinesAsync(_process.StandardError, line => _stderrLine?.Invoke(line), null));
                return;
            }
            
            _ = Task.Run(() => HandleStdout(ct), ct);
            _ = Task.Run(() => HandleStderr(ct), ct);
        }

        // --- IProcessEndpoint: line-framed stdio for a port adapter (dataflow graph agents) ---

        private bool _lineMode;
        private ProcessSnapshot? _restoreFrom;
        private readonly TaskCompletionSource _stdoutDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<string>? _stdoutLine, _stderrLine;
        private Action<int>? _exited;
        event Action<string>? IProcessEndpoint.StdoutLine { add => _stdoutLine += value; remove => _stdoutLine -= value; }
        event Action<string>? IProcessEndpoint.StderrLine { add => _stderrLine += value; remove => _stderrLine -= value; }
        event Action<int>? IProcessEndpoint.Exited { add => _exited += value; remove => _exited -= value; }

        async Task IProcessEndpoint.StartAsync(ProcessSnapshot? restoreFrom, CancellationToken ct)
        {
            _lineMode = true;
            _restoreFrom = restoreFrom;
            try { await StartAsync(ct).ConfigureAwait(false); }
            finally { _restoreFrom = null; }
        }

        Task IProcessEndpoint.WriteLineAsync(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            WriteToStdin(bytes);
            return Task.CompletedTask;
        }

        // --- Extra descriptors for subclasses ---

        private DescriptorShim? _shim;
        private string? _originalFileName;
        private List<string>? _originalArguments;

        // Whether a checkpoint can be restored into a process that has port descriptors (the restore
        // prepares the command before the descriptor shim wraps it).
        protected virtual bool RestoresWithDescriptors => false;

        // Descriptors a subclass wants beyond the port descriptors, numbered from `firstFree` (called once,
        // before the process starts; the subclass can still set environment variables then).
        protected virtual IReadOnlyList<ExtraDescriptor> ControlDescriptors(int firstFree) => Array.Empty<ExtraDescriptor>();

        // The process started: `Shim` streams for ControlDescriptors can be awaited now.
        protected virtual void OnProcessStarted() { }

        protected DescriptorShim? Shim => _shim;

        // A checkpoint this start restores from (endpoint starts only), for subclasses that restore by
        // preparing their command before it starts.
        protected ProcessSnapshot? RestoringFrom => _restoreFrom;

        // --- IProcessEndpoint raw mode: one byte stream per port (stdin, stdout, stderr, extra descriptors) ---

        private bool _rawMode;
        private IReadOnlyList<ExtraDescriptor>? _rawExtra;
        private RawProcessIo? _raw;
        private Action<int, ReadOnlyMemory<byte>>? _output;
        private Action<int>? _outputEnded;
        event Action<int, ReadOnlyMemory<byte>>? IProcessEndpoint.Output { add => _output += value; remove => _output -= value; }
        event Action<int>? IProcessEndpoint.OutputEnded { add => _outputEnded += value; remove => _outputEnded -= value; }

        async Task IProcessEndpoint.StartRawAsync(ProcessSnapshot? restoreFrom, IReadOnlyList<ExtraDescriptor> extra, CancellationToken ct)
        {
            // A restored process gets its descriptors from the checkpoint implementation, which doesn't know
            // about the descriptor shim yet.
            if (restoreFrom != null && extra.Count > 0 && !RestoresWithDescriptors)
                throw new NotSupportedException("restoring a process with extra port descriptors is not available");
            _rawMode = true;
            _rawExtra = extra;
            _restoreFrom = restoreFrom;
            try { await StartAsync(ct).ConfigureAwait(false); }
            finally { _restoreFrom = null; }
        }

        Task IProcessEndpoint.WriteAsync(int fd, ReadOnlyMemory<byte> data)
        {
            if (_raw == null) throw new InvalidOperationException("the process is not in raw mode");
            BytesIn += data.Length;
            MessagesIn++;
            return _raw.WriteAsync(fd, data);
        }

        Task IProcessEndpoint.CloseInputAsync(int fd) =>
            _raw?.CloseInputAsync(fd) ?? throw new InvalidOperationException("the process is not in raw mode");

        Task IProcessEndpoint.CloseInputAsync()
        {
            if (_raw != null) return _raw.CloseInputsAsync();
            lock (_process.StandardInput)
            {
                try { _process.StandardInput.Close(); } catch (Exception) { }
            }
            return Task.CompletedTask;
        }

        async Task IProcessEndpoint.StopAsync(TimeSpan grace)
        {
            _endpointStopping = true;
            await ((IProcessEndpoint)this).CloseInputAsync().ConfigureAwait(false);
            try
            {
                using var cts = new CancellationTokenSource(grace);
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }   // never started
            await StopAsync().ConfigureAwait(false);
        }

        // A snapshot of the process state for its standby (L§9.3). The process checkpoint implementation
        // plugs in here; without one, checkpoints carry middleware state only.
        public virtual Task<ProcessSnapshot> CheckpointAsync(CancellationToken ct) =>
            throw new NotSupportedException("process checkpointing is not available");

        // Starts `_process` from a checkpoint instead of from scratch. Must throw NotSupportedException
        // before starting anything when it can't restore.
        protected virtual Task RestoreProcessAsync(ProcessSnapshot snapshot, CancellationToken ct) =>
            throw new NotSupportedException("restoring a process from a checkpoint is not available");

        private static async Task ReadLinesAsync(System.IO.StreamReader reader, Action<string> onLine, TaskCompletionSource? drained)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) onLine(line);
            }
            catch (Exception) { }
            finally { drained?.TrySetResult(); }
        }

        // Line mode: the exit is reported after stdout has been drained, then the agent stops.
        private async Task OnLineModeExitAsync()
        {
            var drained = _stdoutDrained.Task;
            if (_raw != null)
            {
                _ = _raw.ProcessExited();
                drained = _raw.Drained;
            }
            await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            int code;
            try { code = _process.ExitCode; } catch (InvalidOperationException) { code = -1; }
            ReportExit(code);
            await StopAsync().ConfigureAwait(false);
        }

        private bool _endpointStopping;
        private int _exitReported;

        private void ReportExit(int code)
        {
            if (Interlocked.Exchange(ref _exitReported, 1) != 0) return;
            ExitCode = code;
            _exited?.Invoke(code);
        }

        public int? ExitCode { get; private set; }

        // The process's own exit and an explicit stop (a graceful endpoint stop, `kill`, shutdown) can race;
        // they share one stop, so the state machine's Stop transition fires once. The stop runs outside the lock:
        // it disposes the Process, which takes the Process's own lock, and the Process raises Exited (which
        // stops the agent) while holding that lock; running the stop under _stopLock deadlocked the two.
        private readonly object _stopLock = new();
        private Task? _stopTask;

        public override Task StopAsync(CancellationToken ct = default)
        {
            TaskCompletionSource stop;
            lock (_stopLock)
            {
                if (_stopTask != null) return _stopTask;
                stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopTask = stop.Task;
            }
            _ = RunStopAsync(stop, ct);
            return stop.Task;
        }

        private async Task RunStopAsync(TaskCompletionSource stop, CancellationToken ct)
        {
            try
            {
                await base.StopAsync(ct).ConfigureAwait(false);
                stop.TrySetResult();
            }
            catch (Exception ex) { stop.TrySetException(ex); }
        }

        protected override Task OnPauseAsync(CancellationToken ct)
        {
            _logger.LogInformation("ProcessAgent {URI} pausing...", URI);
            return Task.CompletedTask;
        }

        protected override Task OnEndAsync(CancellationToken ct)
        {
            _logger.LogInformation("ProcessAgent {URI} stopping...", URI);
            _ending = true;
            // Closing the input pipe ends the upstream connection cleanly (the writer sees it closed) instead of
            // leaving it to deliver into a disposed process.
            if (InputPipe is { } input) _ = CloseQuietlyAsync(input);
            
            try
            {
                // A process that never started (e.g. its executable doesn't exist) has nothing to kill.
                bool started;
                try { _ = _process.Id; started = true; } catch (InvalidOperationException) { started = false; }
                if (started && !_process.HasExited)
                {
                    _process.Kill(true);
                    // Line mode: a stop from outside (e.g. `kill`) must reach the port adapter as an exit; the
                    // process is disposed below, so its own Exited event may never fire.
                    if ((_lineMode || _rawMode) && !_endpointStopping) ReportExit(137);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception while killing the process of agent {URI}.", URI);
            }
            finally
            {
                _process.Dispose();
                if (_raw != null) _ = _raw.DisposeAsync().AsTask();
                if (_shim != null) _ = _shim.DisposeAsync().AsTask();
            }
            
            return Task.CompletedTask;
        }

        private async Task CloseQuietlyAsync(Pipe pipe)
        {
            try { await pipe.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug("Closing the input pipe of {URI}: {Error}", URI, ex.Message); }
        }

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in Inbox.Reader.ReadAllAsync(ct))
                {
                    if (msg is Envelope env)
                    {
                        if (env.Channel == "stdin" || env.Channel == "default")
                        {
                            HandleStdin(env.Payload);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProcessAgent {URI} RunLoop encountered an error.", URI);
            }
        }

        protected virtual void HandleStdin(byte[] payload)
        {
            WriteToStdin(payload);
        }

        public void ConnectInputPipe(Pipe inputPipe)
        {
            InputPipe = inputPipe;
            InputPipe.OnReceive(payload => WriteToStdin(payload));
            _ = InputPipe.StartListening();
        }

        // Input for a stopping or stopped agent (an upstream agent still writing, e.g. after Ctrl+C stopped this
        // end of a pipeline) is dropped: its Process is disposed, and the stop closes the input pipe.
        private volatile bool _ending;
        private long _droppedAfterStop;

        protected void WriteToStdin(byte[] payload)
        {
            if (_ending) { DroppedAfterStop(payload); return; }
            try
            {
                lock (_process.StandardInput)
                {
                    if (!_process.HasExited)
                    {
                        _process.StandardInput.BaseStream.Write(payload, 0, payload.Length);
                        _process.StandardInput.BaseStream.Flush();
                        BytesIn += payload.Length;
                        MessagesIn++;
                    }
                }
            }
            catch (Exception ex) when (_ending || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                DroppedAfterStop(payload);   // the process ended (or is ending) while this chunk arrived
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to write to stdin of agent {URI}: {Error}", URI, ex.Message);
            }
        }

        private void DroppedAfterStop(byte[] payload)
        {
            if (Interlocked.Add(ref _droppedAfterStop, payload.Length) == payload.Length)
                _logger.LogDebug("ProcessAgent {URI} has stopped; dropping input that still arrives", URI);
        }

        protected virtual async Task HandleStdout(CancellationToken ct)
        {
            try
            {
                byte[] buffer = new byte[BufferSize];
                while (!ct.IsCancellationRequested && !_process.HasExited)
                {
                    int bytesRead = await _process.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (bytesRead > 0)
                    {
                        var payload = buffer.Take(bytesRead).ToArray();
                        BytesOut += bytesRead;
                        MessagesOut++;

                        if (OutputPipe != null)
                        {
                            await OutputPipe.Send(payload);
                        }
                        else if (OnMessageOut != null)
                        {
                            var target = _agentInfo.OutputToShell 
                                ? _virtualRuntime.ShellUri
                                : _virtualRuntime.Client?.URI ?? string.Empty;
                            var env = new Envelope
                            {
                                MessageId = Guid.NewGuid(),
                                SenderId = _virtualRuntime.HostRuntimeID,
                                SenderAgentUri = URI,
                                Strategy = RoutingStrategy.Direct,
                                Target = target,
                                Channel = "stdout",
                                Payload = payload
                            };
                            
                            await OnMessageOut(env);
                        }

                        _logger.LogInformation(Encoding.UTF8.GetString(payload));
                    }
                    else
                    {
                        await Task.Delay(10, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read stdout from process {PID}.", _process.Id);
            }
        }

        protected virtual async Task HandleStderr(CancellationToken ct)
        {
            try
            {
                byte[] buffer = new byte[BufferSize];
                while (!ct.IsCancellationRequested && !_process.HasExited)
                {
                    int bytesRead = await _process.StandardError.BaseStream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (bytesRead > 0)
                    {
                        var payload = buffer.Take(bytesRead).ToArray();
                        
                        if (OnMessageErr != null)
                        {
                            var target = _agentInfo.OutputToShell 
                                ? _virtualRuntime.ShellUri 
                                : _virtualRuntime.Client?.URI ?? string.Empty;
                            var env = new Envelope
                            {
                                MessageId = Guid.NewGuid(),
                                SenderId = _virtualRuntime.HostRuntimeID,
                                SenderAgentUri = URI,
                                Strategy = RoutingStrategy.Direct,
                                Target = target,
                                Channel = "stderr",
                                Payload = payload
                            };
                            
                            await OnMessageErr(env);
                        }
                    }
                    else
                    {
                        await Task.Delay(10, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read stderr from process {PID}.", _process.Id);
            }
        }
    }
}
