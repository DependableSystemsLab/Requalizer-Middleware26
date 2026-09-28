using System;
using System.Collections.Generic;
using MessagePack;

namespace OneOS.Runtime
{
    // A host runtime's static description (scheduling-spec §2.2), as the runtime itself reports it in a
    // cluster-info exchange. Control-plane state: it never goes through the Registry.
    [MessagePackObject]
    public class HostInfo
    {
        [Key(0)] public string Id { get; set; } = string.Empty;
        [Key(1)] public string Name { get; set; } = string.Empty;
        [Key(2)] public string? Zone { get; set; }
        [Key(3)] public List<string> Tags { get; set; } = new List<string>();
        [Key(4)] public string? Label { get; set; }
        [Key(5)] public long CpuMillis { get; set; }
        [Key(6)] public long MemoryBytes { get; set; }
        // Launchable command → version; null when the runtime doesn't declare its executables.
        [Key(7)] public Dictionary<string, string>? Executables { get; set; }
        [Key(8)] public List<string> IdmSupport { get; set; } = new List<string>();
        [Key(9)] public int? MaxAgents { get; set; }
    }

    // The outcome of one cluster-info exchange: every host that answered the initiator, collected at
    // `CollectedAt` (the initiator's UTC clock). Each runtime keeps the latest one it has seen as its
    // "last known cluster state".
    [MessagePackObject]
    public class ClusterState
    {
        [Key(0)] public Guid RoundId { get; set; }
        [Key(1)] public string InitiatorId { get; set; } = string.Empty;
        [Key(2)] public DateTime CollectedAt { get; set; }
        [Key(3)] public List<HostInfo> Hosts { get; set; } = new List<HostInfo>();
        // Peers the initiator asked but that did not answer in time (or had no live link).
        [Key(4)] public List<string> Unreachable { get; set; } = new List<string>();
    }
}
