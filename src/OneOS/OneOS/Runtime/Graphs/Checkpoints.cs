using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OneOS.Runtime.Scheduling;
using OneOS.Runtime.Sidecar;

namespace OneOS.Runtime.Graphs;

// A stateful primary's checkpoint for its standby (L§9.3): the process snapshot (when the process
// supports it) and the sidecar's middleware state, taken at the same point in the input stream.
// Primaries send one per interval over a checkpoint channel to the standby's host, which keeps the
// latest. Messages the primary handles after its last checkpoint are lost on failover (at-most-once).
public sealed record AgentCheckpoint(string AgentId, long Sequence, DateTimeOffset TakenAt, ProcessSnapshot? Process, SidecarState Sidecar)
{
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static AgentCheckpoint FromBytes(byte[] bytes) => JsonSerializer.Deserialize<AgentCheckpoint>(bytes)!;

    // The checkpoint channel from a primary to its standby, as a pipe the transports can carry. Its id
    // changes with either endpoint's host or incarnation, so a new pair never reuses an old connection.
    public static GraphPipeInfo? Channel(GraphInstanceInfo instance, GraphAgentInfo primary)
    {
        if (primary.StandbyId == null || instance.Agents.FirstOrDefaultById(primary.StandbyId) is not { } standby) return null;
        return new GraphPipeInfo($"{standby.AgentId}#checkpoint/{primary.HostId}.v{primary.Version}-{standby.HostId}.v{standby.Version}",
            instance.GraphInstanceId, "#checkpoint", true, primary.AgentId, "#checkpoint", standby.AgentId, "#checkpoint",
            RoutingMode.Direct, null, null, 0, false, Array.Empty<string>(), ElidedChecks.None, primary.HostId == standby.HostId,
            false, Array.Empty<string>(), null, null, primary.HostId, standby.HostId, null, PipeState.Pending, instance.Version);
    }
}

// The checkpoint channel is a message pipe, whose frames are bounded (Socket.MaxFrameSize), while a process
// snapshot may not be: a checkpoint travels as a run of Envelopes (Channel "#checkpoint", Sequence = the
// checkpoint's), each carrying one chunk. The pipe keeps them in order; the receiver reassembles the run.
[MessagePack.MessagePackObject]
public sealed record CheckpointChunk(
    [property: MessagePack.Key(0)] long Sequence,
    [property: MessagePack.Key(1)] int Index,
    [property: MessagePack.Key(2)] int Count,
    [property: MessagePack.Key(3)] byte[] Data);

public static class CheckpointStream
{
    public const int ChunkSize = 4 * 1024 * 1024;

    public static IEnumerable<Envelope> Split(AgentCheckpoint checkpoint, string target, int chunkSize = ChunkSize)
    {
        var bytes = checkpoint.ToBytes();
        int count = Math.Max(1, (bytes.Length + chunkSize - 1) / chunkSize);
        for (int i = 0; i < count; i++)
        {
            int offset = i * chunkSize;
            var chunk = new CheckpointChunk(checkpoint.Sequence, i, count, bytes.AsSpan(offset, Math.Min(chunkSize, bytes.Length - offset)).ToArray());
            yield return new Envelope
            {
                SenderAgentUri = checkpoint.AgentId, Target = target, Channel = "#checkpoint",
                Sequence = checkpoint.Sequence, Payload = MessagePack.MessagePackSerializer.Serialize(chunk),
            };
        }
    }
}

// Reassembles the checkpoints on one channel. A run that is cut short (the primary stopped mid-send) is
// dropped when the next one starts.
public sealed class CheckpointAssembler
{
    private readonly List<byte[]> _parts = new();
    private long _sequence = -1;

    public AgentCheckpoint? Push(Envelope envelope)
    {
        var chunk = MessagePack.MessagePackSerializer.Deserialize<CheckpointChunk>(envelope.Payload);
        if (chunk.Index == 0) { _parts.Clear(); _sequence = chunk.Sequence; }
        else if (chunk.Sequence != _sequence || chunk.Index != _parts.Count) { _parts.Clear(); _sequence = -1; return null; }
        _parts.Add(chunk.Data);
        if (_parts.Count < chunk.Count) return null;
        var bytes = new byte[_parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (var p in _parts) { p.CopyTo(bytes, offset); offset += p.Length; }
        _parts.Clear();
        _sequence = -1;
        return AgentCheckpoint.FromBytes(bytes);
    }
}

internal static class AgentListExtensions
{
    public static GraphAgentInfo? FirstOrDefaultById(this IEnumerable<GraphAgentInfo> agents, string id)
    {
        foreach (var a in agents) if (a.AgentId == id) return a;
        return null;
    }
}

// Registry names of graph agents: <owner>.<domain>/graphs/<agentId>, where the agent id starts with the
// graph instance id.
public static class GraphUris
{
    public static string Agent(string owner, string domain, string agentId) => $"{owner}.{domain}/graphs/{agentId}";
    public static string InstanceOf(string agentId) => agentId[..agentId.IndexOf('/')];
}
