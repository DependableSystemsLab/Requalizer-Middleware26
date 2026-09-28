using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OneOS.Runtime
{
    // Extra descriptors (3, 4, …) for a System.Diagnostics.Process: the ports of a dataflow graph node,
    // the JavaScript environment's control channel, or both.
    //
    // Process can't hand a child extra descriptors, and inheriting ordinary pipes would leak them into every
    // child started at the same moment. So each extra descriptor is a named FIFO in a private (0700)
    // directory, and the command runs under a fixed /bin/sh shim that opens the FIFOs onto their descriptor
    // numbers and then execs the command (same pid). The command and its arguments are positional
    // parameters of the shim, never parsed by the shell. The FIFOs are removed once both ends are open.
    // Linux (and POSIX systems where O_RDWR on a FIFO doesn't block; see ProcessExited).
    public sealed class DescriptorShim : IAsyncDisposable
    {
        // $0 = oneos-fd-shim; then triples (r|w, fd, path) up to "--", then the command.
        public const string ShimScript = """
            while [ "$1" != -- ]; do
              case "$1" in
                r) eval "exec $2<\"\$3\"" ;;
                w) eval "exec $2>\"\$3\"" ;;
                *) echo "oneos-fd-shim: bad descriptor spec '$1'" >&2; exit 127 ;;
              esac
              shift 3
            done
            shift
            exec "$@"
            """;

        private readonly Action<string> _log;
        private readonly string? _dir;
        private readonly Dictionary<int, string> _fifos = new();
        private readonly Dictionary<int, Task<FileStream?>> _opens = new();
        private int _opened;

        public DescriptorShim(IReadOnlyList<ExtraDescriptor> descriptors, string scratchDirectory, Action<string>? log = null)
        {
            if (descriptors.Any(e => e.Fd < 3) || descriptors.Select(e => e.Fd).Distinct().Count() != descriptors.Count)
                throw new ArgumentException("extra descriptors must be distinct and at least 3");
            Descriptors = descriptors;
            _log = log ?? (_ => { });
            if (descriptors.Count == 0) return;
            _dir = Path.Combine(scratchDirectory, "oneos-fd-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(scratchDirectory);
            Directory.CreateDirectory(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var e in descriptors)
            {
                var path = Path.Combine(_dir, e.Fd.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (mkfifo(path, 0x180 /* 0600 */) != 0)
                    throw new IOException($"mkfifo {path} failed (errno {Marshal.GetLastPInvokeError()})");
                _fifos[e.Fd] = path;
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int mkfifo(string pathname, uint mode);

        public IReadOnlyList<ExtraDescriptor> Descriptors { get; }

        // Before the process starts: wraps the command in the shim when there are extra descriptors.
        public void Prepare(ProcessStartInfo psi)
        {
            if (Descriptors.Count == 0) return;
            var command = new List<string> { psi.FileName };
            command.AddRange(psi.ArgumentList);
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Clear();
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(ShimScript);
            psi.ArgumentList.Add("oneos-fd-shim");
            foreach (var e in Descriptors)
            {
                psi.ArgumentList.Add(e.ProcessReads ? "r" : "w");
                psi.ArgumentList.Add(e.Fd.ToString(System.Globalization.CultureInfo.InvariantCulture));
                psi.ArgumentList.Add(_fifos[e.Fd]);
            }
            psi.ArgumentList.Add("--");
            foreach (var c in command) psi.ArgumentList.Add(c);
        }

        // After the process started: opens our end of every FIFO, each in the background (opening a FIFO
        // blocks until the process opens the other end). Idempotent.
        public void Open()
        {
            if (Interlocked.Exchange(ref _opened, 1) == 1) return;
            foreach (var e in Descriptors)
            {
                var path = _fifos[e.Fd];
                _opens[e.Fd] = Task.Run(() =>
                {
                    try { return (FileStream?)new FileStream(path, FileMode.Open, e.ProcessReads ? FileAccess.Write : FileAccess.Read, FileShare.ReadWrite, 1); }
                    catch (Exception ex) { _log($"descriptor {e.Fd}: can't open {path}: {ex.Message}"); return null; }
                });
            }
            if (_dir != null) _ = Task.WhenAll(_opens.Values).ContinueWith(_ => RemoveDirectory(), TaskScheduler.Default);
        }

        // Our end of a descriptor: written when the process reads it, read when it writes it. Null when the
        // process never opened its end (it exited first).
        public Task<FileStream?> Stream(int fd) =>
            _opens.TryGetValue(fd, out var open) ? open : throw new ArgumentException($"descriptor {fd} is not open (call Open after the process starts)");

        // The process exited. FIFO ends it never opened would block our side forever: opening a FIFO
        // read-write doesn't block (Linux) and completes the pending open, after which our reader sees end of
        // stream and our writer fails harmlessly. An open of ours may not have started yet, so this repeats
        // until every open has completed.
        public Task ProcessExited()
        {
            return Task.Run(async () =>
            {
                for (int attempt = 0; attempt < 50 && _opens.Values.Any(o => !o.IsCompleted); attempt++)
                {
                    foreach (var (fd, open) in _opens)
                    {
                        if (open.IsCompleted) continue;
                        try { using var unblock = new FileStream(_fifos[fd], FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1); }
                        catch (Exception) { }
                    }
                    await Task.Delay(100).ConfigureAwait(false);
                }
            });
        }

        private void RemoveDirectory()
        {
            if (_dir == null) return;
            try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
        }

        public async ValueTask DisposeAsync()
        {
            await ProcessExited().ConfigureAwait(false);
            foreach (var open in _opens.Values)
            {
                try { if (await open.ConfigureAwait(false) is { } s) await s.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { }
            }
            RemoveDirectory();
        }
    }

    // Raw byte streams for a System.Diagnostics.Process (plan step 6.5): stdin, stdout, stderr, and the
    // extra descriptors of a dataflow graph node's ports, through a DescriptorShim (its own, or one the
    // owner shares with other descriptors, e.g. the JavaScript control channel).
    public sealed class RawProcessIo : IAsyncDisposable
    {
        private const int BufferSize = 65536;

        private readonly IReadOnlyList<ExtraDescriptor> _extra;
        private readonly Action<string> _log;
        private readonly DescriptorShim _shim;
        private readonly bool _ownsShim;
        private readonly Dictionary<int, Task> _writeTails = new();
        private readonly object _stdinLock = new();
        private Stream? _stdin;
        private bool _stdinClosed;
        private int _openOutputs;
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // `shim`: a shared shim that already carries `extra` (the owner prepares and opens it); without one,
        // this creates, prepares and opens its own.
        public RawProcessIo(IReadOnlyList<ExtraDescriptor> extra, string scratchDirectory, Action<string>? log = null, DescriptorShim? shim = null)
        {
            _extra = extra;
            _log = log ?? (_ => { });
            _ownsShim = shim == null;
            _shim = shim ?? new DescriptorShim(extra, scratchDirectory, log);
            if (extra.Any(e => !_shim.Descriptors.Contains(e))) throw new ArgumentException("the shared shim doesn't carry every port descriptor");
        }

        public event Action<int, ReadOnlyMemory<byte>>? Output;
        public event Action<int>? OutputEnded;

        // Completes when every output (stdout, stderr, extra outputs) has reached end of stream.
        public Task Drained => _drained.Task;

        // Before the process starts (own shim only; a shared shim is prepared by its owner).
        public void Prepare(ProcessStartInfo psi)
        {
            if (_ownsShim) _shim.Prepare(psi);
        }

        // After the process started: reads every output.
        public void Attach(Process process)
        {
            if (_ownsShim) _shim.Open();
            _stdin = process.StandardInput.BaseStream;
            var outputs = new List<(int Fd, Func<Task<Stream?>> Stream)>
            {
                (1, () => Task.FromResult<Stream?>(process.StandardOutput.BaseStream)),
                (2, () => Task.FromResult<Stream?>(process.StandardError.BaseStream)),
            };
            foreach (var e in _extra.Where(e => !e.ProcessReads))
                outputs.Add((e.Fd, async () => await _shim.Stream(e.Fd).ConfigureAwait(false)));
            _openOutputs = outputs.Count;
            // Each descriptor is read on its own thread (DedicatedThread): blocking reads of pipes and FIFOs.
            foreach (var (fd, stream) in outputs) _ = OneOS.Common.DedicatedThread.Run($"descriptor {fd} reader", () => Read(fd, stream));
        }

        private void Read(int fd, Func<Task<Stream?>> open)
        {
            try
            {
                var stream = open().GetAwaiter().GetResult();
                if (stream == null) return;
                var buffer = new byte[BufferSize];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    try { Output?.Invoke(fd, buffer.AsMemory(0, n)); }
                    catch (Exception ex) { _log($"descriptor {fd}: output handler failed: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            finally
            {
                try { OutputEnded?.Invoke(fd); }
                catch (Exception ex) { _log($"descriptor {fd}: end-of-output handler failed: {ex.Message}"); }
                if (Interlocked.Decrement(ref _openOutputs) == 0) _drained.TrySetResult();
            }
        }

        // Writes to stdin (0) or an extra input, in call order per descriptor. A process that closed the
        // descriptor or exited makes the write a no-op (logged).
        public Task WriteAsync(int fd, ReadOnlyMemory<byte> data)
        {
            if (fd == 0)
            {
                lock (_stdinLock)
                {
                    if (_stdinClosed || _stdin == null) return Task.CompletedTask;
                    try { _stdin.Write(data.Span); _stdin.Flush(); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { _log($"stdin: write failed: {ex.Message}"); }
                }
                return Task.CompletedTask;
            }
            var copy = data.ToArray();
            return Chain(fd, async stream => { await stream.WriteAsync(copy).ConfigureAwait(false); await stream.FlushAsync().ConfigureAwait(false); });
        }

        public Task CloseInputAsync(int fd)
        {
            if (fd == 0)
            {
                lock (_stdinLock)
                {
                    _stdinClosed = true;
                    try { _stdin?.Close(); } catch (Exception) { }
                }
                return Task.CompletedTask;
            }
            return Chain(fd, stream => { stream.Dispose(); return Task.CompletedTask; });
        }

        public Task CloseInputsAsync() =>
            Task.WhenAll(new[] { CloseInputAsync(0) }.Concat(_extra.Where(e => e.ProcessReads).Select(e => CloseInputAsync(e.Fd))));

        private Task Chain(int fd, Func<FileStream, Task> action)
        {
            if (!_extra.Any(e => e.Fd == fd && e.ProcessReads))
                throw new ArgumentException($"descriptor {fd} is not an input of this process");
            lock (_writeTails)
            {
                var previous = _writeTails.GetValueOrDefault(fd, Task.CompletedTask);
                var next = Run(previous);
                _writeTails[fd] = next;
                return next;
            }

            async Task Run(Task previous)
            {
                await previous.ConfigureAwait(false);
                var stream = await _shim.Stream(fd).ConfigureAwait(false);
                if (stream == null) return;
                try { await action(stream).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { _log($"descriptor {fd}: write failed: {ex.Message}"); }
            }
        }

        // The process exited (own shim only; see DescriptorShim.ProcessExited).
        public Task ProcessExited() => _ownsShim ? _shim.ProcessExited() : Task.CompletedTask;

        public ValueTask DisposeAsync() => _ownsShim ? _shim.DisposeAsync() : ValueTask.CompletedTask;
    }
}
