using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OneOS.Runtime
{
    // Opaque process state from a checkpoint (L§9.3). Its format belongs to the checkpoint implementation.
    public sealed record ProcessSnapshot(byte[] Data, string? Format = null);

    // A port on a descriptor of its own (plan step 6.5, Q6.1): the process reads it (an input port) or
    // writes it (an output port). Descriptors 0–2 are stdin, stdout and stderr; extra ones start at 3.
    public sealed record ExtraDescriptor(int Fd, bool ProcessReads);

    // A process as a port adapter sees it (dataflow graph agents), in one of two modes:
    //   line mode  line-framed stdin/stdout (the JSON-lines port protocol, `@json_lines` nodes);
    //   raw mode   byte streams, one per port: stdin, stdout, stderr and extra descriptors (typed streams).
    // Plus a graceful stop and checkpoints. ProcessAgent implements both; test fakes may implement only
    // line mode.
    public interface IProcessEndpoint
    {
        event Action<string>? StdoutLine;
        event Action<string>? StderrLine;
        event Action<int>? Exited;

        // Starts the process, or restores it from a checkpoint. Restoring throws NotSupportedException
        // (before anything starts) when the process can't be restored; the caller then starts it fresh.
        Task StartAsync(ProcessSnapshot? restoreFrom, CancellationToken ct);
        Task WriteLineAsync(string line);
        Task CloseInputAsync();
        // Closes stdin, waits up to `grace` for the process to exit, then kills it.
        Task StopAsync(TimeSpan grace);
        // A snapshot of the process state (L§9.3). Throws NotSupportedException when unavailable.
        Task<ProcessSnapshot> CheckpointAsync(CancellationToken ct);

        // --- Raw mode ---

        // Starts the process with raw byte streams and the given extra descriptors (instead of StartAsync).
        Task StartRawAsync(ProcessSnapshot? restoreFrom, IReadOnlyList<ExtraDescriptor> extra, CancellationToken ct) =>
            throw new NotSupportedException("this process endpoint has no raw mode");

        // Bytes the process wrote on a descriptor (1, 2 or an extra output); the memory is only valid during
        // the call. OutputEnded follows once per descriptor, at end of stream.
        event Action<int, ReadOnlyMemory<byte>>? Output { add { } remove { } }
        event Action<int>? OutputEnded { add { } remove { } }

        // Writes to descriptor 0 or an extra input, in call order per descriptor.
        Task WriteAsync(int fd, ReadOnlyMemory<byte> data) => throw new NotSupportedException("this process endpoint has no raw mode");
        Task CloseInputAsync(int fd) => throw new NotSupportedException("this process endpoint has no raw mode");
    }

    // Creates the process for a graph agent on this host (tier-0: ExecutionManager). The process is not
    // started. `argv` is the bound argument vector, command first; no shell is involved.
    public interface IProcessFactory
    {
        IProcessEndpoint CreateProcess(string agentId, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> environment);
    }
}
