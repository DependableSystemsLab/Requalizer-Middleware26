using System.Collections.Generic;
using System.Linq;

namespace OneOS.Runtime
{
    // Resources are integers: CPU in millicores, memory in bytes (S§7).
    public sealed record ResourceVector(long CpuMillis, long MemoryBytes)
    {
        public static readonly ResourceVector Zero = new(0, 0);
        public ResourceVector Add(ResourceVector o) => new(CpuMillis + o.CpuMillis, MemoryBytes + o.MemoryBytes);
        public ResourceVector Subtract(ResourceVector o) => new(CpuMillis - o.CpuMillis, MemoryBytes - o.MemoryBytes);
        public ResourceVector Scale(double f) => new((long)System.Math.Ceiling(CpuMillis * f), MemoryBytes);
    }

    // One host runtime in the live inventory (S§2.2).
    public sealed record HostRuntimeInfo(
        string HostId,
        string Name,                                   // matched by `pin`
        string? Zone,
        IReadOnlyList<string> Tags,
        string? Label,                                 // null: ⊥ (L§8.2)
        bool Alive,
        ResourceVector Capacity,
        ResourceVector Committed,                      // reserved by other graph instances, including standbys
        IReadOnlyDictionary<string, string>? Executables,  // launchable command → version; null: unknown, anything goes
        IReadOnlyList<string> IdmSupport,              // language runtimes the IDM can be injected into (A3)
        int? MaxAgents,
        long Version)
    {
        public ResourceVector Free => Capacity.Subtract(Committed);
    }

    // Measured network quality for an ordered host pair, including a host to itself.
    public sealed record NetworkQuality(
        string From,
        string To,
        IReadOnlyDictionary<int, long> LatencyQuantilesMicros,   // percentile (50, 95, 99, ...) → µs
        long? BandwidthBytesPerSec,
        bool Reachable = true);

    // A consistent, versioned view of the cluster, taken once per spawn (S§2.2).
    public sealed record ClusterSnapshot(long Version, IReadOnlyList<HostRuntimeInfo> Hosts, IReadOnlyList<NetworkQuality> Network)
    {
        public static readonly ClusterSnapshot Empty = new(0, new List<HostRuntimeInfo>(), new List<NetworkQuality>());

        public NetworkQuality? Quality(string from, string to) => Network.FirstOrDefault(q => q.From == from && q.To == to);
    }
}
