using System.Diagnostics;
using OneOS.Runtime;

namespace OneOS.Tests.Graphs;

// Plain OS processes behind the IProcessFactory seam, for executor tests without a runtime (the
// runtime's factory is ExecutionManager, which runs graph agents as ProcessAgents).
internal sealed class OsProcessFactory : IProcessFactory
{
    private readonly string _workingDirectory;
    public OsProcessFactory(string workingDirectory) { _workingDirectory = workingDirectory; }

    public IProcessEndpoint CreateProcess(string agentId, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> environment)
    {
        var psi = new ProcessStartInfo
        {
            FileName = argv[0] == "python" ? "python3" : argv[0],
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = _workingDirectory,
        };
        if (psi.FileName == "python3") psi.ArgumentList.Add("-u");
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in environment) psi.Environment[k] = v;
        return new OsProcess(psi);
    }

    private sealed class OsProcess : IProcessEndpoint
    {
        private readonly Process _p;
        private RawProcessIo? _raw;
        public event Action<string>? StdoutLine;
        public event Action<string>? StderrLine;
        public event Action<int>? Exited;
        public event Action<int, ReadOnlyMemory<byte>>? Output;
        public event Action<int>? OutputEnded;

        public OsProcess(ProcessStartInfo psi)
        {
            _p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _p.OutputDataReceived += (_, e) => { if (e.Data != null) StdoutLine?.Invoke(e.Data); };
            _p.ErrorDataReceived += (_, e) => { if (e.Data != null) StderrLine?.Invoke(e.Data); };
            _p.Exited += async (_, _) =>
            {
                _p.WaitForExit();
                if (_raw != null)
                {
                    _ = _raw.ProcessExited();
                    await Task.WhenAny(_raw.Drained, Task.Delay(5000));
                }
                Exited?.Invoke(_p.ExitCode);
            };
        }

        // Raw mode through the runtime's RawProcessIo (descriptor shim and FIFOs), as ProcessAgent does.
        public Task StartRawAsync(ProcessSnapshot? restoreFrom, IReadOnlyList<ExtraDescriptor> extra, CancellationToken ct)
        {
            if (restoreFrom != null) throw new NotSupportedException("test processes can't restore");
            Directory.CreateDirectory(_p.StartInfo.WorkingDirectory);
            _raw = new RawProcessIo(extra, _p.StartInfo.WorkingDirectory);
            _raw.Output += (fd, data) => Output?.Invoke(fd, data);
            _raw.OutputEnded += fd => OutputEnded?.Invoke(fd);
            _raw.Prepare(_p.StartInfo);
            _p.Start();
            _raw.Attach(_p);
            return Task.CompletedTask;
        }

        public Task WriteAsync(int fd, ReadOnlyMemory<byte> data) => _raw!.WriteAsync(fd, data);
        public Task CloseInputAsync(int fd) => _raw!.CloseInputAsync(fd);

        public Task StartAsync(ProcessSnapshot? restoreFrom, CancellationToken ct)
        {
            if (restoreFrom != null) throw new NotSupportedException("test processes can't restore");
            Directory.CreateDirectory(_p.StartInfo.WorkingDirectory);
            _p.Start();
            _p.BeginOutputReadLine();
            _p.BeginErrorReadLine();
            return Task.CompletedTask;
        }

        public Task WriteLineAsync(string line)
        {
            lock (_p) { try { if (!_p.HasExited) { _p.StandardInput.WriteLine(line); _p.StandardInput.Flush(); } } catch (IOException) { } }
            return Task.CompletedTask;
        }

        public Task CloseInputAsync()
        {
            if (_raw != null) return _raw.CloseInputsAsync();
            lock (_p) { try { _p.StandardInput.Close(); } catch (IOException) { } }
            return Task.CompletedTask;
        }

        public async Task StopAsync(TimeSpan grace)
        {
            await CloseInputAsync();
            try { using var cts = new CancellationTokenSource(grace); await _p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { _p.Kill(true); } catch (InvalidOperationException) { } }
            catch (InvalidOperationException) { }
        }

        public Task<ProcessSnapshot> CheckpointAsync(CancellationToken ct) => throw new NotSupportedException("test processes can't checkpoint");
    }
}
