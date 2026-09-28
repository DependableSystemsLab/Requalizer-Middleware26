using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using OneOS.Common;

namespace OneOS.Runtime
{
    // Message pipes carry one agent-level frame (an Envelope) per unit, delimited transparently on the wire;
    // raw pipes carry a byte stream that OneOS neither delimits nor reframes.
    public enum PipeKind { Message, Raw }

    // The pipe surface a runtime offers its agents (tier-0). A pipe has an id and two ends: the receiver
    // expects its inbound end, and the sender opens the outbound end towards the receiver's runtime.
    // Co-located ends are joined by an in-process channel, others by a TCP pipe. Either side may come
    // first. ExecutionManager implements it; InMemoryPipeHost is for tests and simulation.
    public interface IPipeHost
    {
        void ExpectInbound(string pipeId, PipeKind kind, bool local, Action<Pipe> onReady);
        Task<Pipe> OpenOutboundAsync(string pipeId, PipeKind kind, string receiverRuntimeId, bool local, CancellationToken ct);
    }

    // All ends in one process, joined by channels.
    public sealed class InMemoryPipeHost : IPipeHost
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<Channel<byte[]>>> _pipes = new();

        private TaskCompletionSource<Channel<byte[]>> Slot(string id) =>
            _pipes.GetOrAdd(id, _ => new TaskCompletionSource<Channel<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously));

        public void ExpectInbound(string pipeId, PipeKind kind, bool local, Action<Pipe> onReady)
        {
            var channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
            Slot(pipeId).TrySetResult(channel);
            onReady(kind == PipeKind.Message ? new LocalMessageInputPipe(channel) : new LocalRawInputPipe(channel));
        }

        public async Task<Pipe> OpenOutboundAsync(string pipeId, PipeKind kind, string receiverRuntimeId, bool local, CancellationToken ct)
        {
            var channel = await Slot(pipeId).Task.WaitAsync(ct);
            return kind == PipeKind.Message ? new LocalMessageOutputPipe(channel) : new LocalRawOutputPipe(channel);
        }
    }
}
