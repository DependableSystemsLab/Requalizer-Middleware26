using System.Collections.Generic;
using System.Linq;
using OneOS.Runtime.Language.Models;
using OneOS.Runtime.Language;

namespace OneOS.Runtime.Scheduling;

// Working state shared by the planning phases. Drafts become GraphAgentInfo/GraphPipeInfo records at the end.

internal sealed class AgentDraft
{
    public required GraphNode Node;
    public required int Index;                  // instance index within the group, before renumbering
    public AgentRole Role = AgentRole.Primary;
    public AgentDraft? Primary, Standby;
    public required GroupDraft Group;
    public bool Candidate;                      // may be left inactive (index ≥ nmin of an elastic group)
    public KeyGroupRange? OwnedKeys;
    public string? Pin;
    public string? PlacementLabel;              // null: no label constraint
    public bool CompartmentBound;
    public IReadOnlyList<string>? PlacementAnyOf;   // compartment-bound nodes: L(h) ⊒ one of these
    public required ResourceVector Demand;
    public required string Command;
    public int? Lane;                            // lane label (S§4.5), for laned keyless nodes

    // Eligibility (H2): eligible host indices, and why every other host was excluded.
    public List<int> Eligible = new();
    public Dictionary<int, string> Excluded = new();

    // Solution.
    public bool Active;
    public int Host = -1;
    public int FinalIndex;
    public string Id = "";

    public string Describe => Role == AgentRole.Standby ? $"{Node.Name}[{Primary!.Index}] standby" : Node.IsPartitioned ? $"{Group.Name}[{Index}]" : Node.Name;
}

// A candidate group: the instances of one node (or, later, one lane). Counts and H6/H7 apply per group.
internal sealed class GroupDraft
{
    public required GraphNode Node;
    public List<AgentDraft> Agents = new();     // primaries, in index order
    public int Min, Max;
    public bool Always;
    public bool Keyless => Node.Partitioning == PartitionKind.Keyless;
    public bool Interchangeable = true;         // false when instance-specific policies exist (no symmetry breaking)
    public string? LaneName;
    public string Name => LaneName == null ? Node.Name : $"{Node.Name}@{LaneName}";
}

internal sealed class PipeDraft
{
    public required GraphEdge Edge;
    public required AgentDraft Source, Destination;
    public required RoutingMode Routing;
    public KeyGroupRange? DestinationKeys;
    public int StreamId;
    public required IReadOnlyList<string> Allowed;
    public bool Unchecked;
    public List<string> Flows = new();
    public double? Rate;                        // msg/s
    public long? Bandwidth;                     // bytes/s
    public double? LatencyPercentile;           // highest percentile among the latency goals it is on

    // Solution.
    public long? ExpectedLatencyMicros;
    public bool Native;
    public ElidedChecks Elided;
    public bool Bypass;                         // a raw byte stream between the processes, no sidecar (plan 6.5b)
}

// A latency goal over one node-level path (S§7.4, S1): a flow path, or a single edge.
internal sealed class LatencyPath
{
    public required string Owner;               // flow name, or "edge:<name>"
    public required List<GraphEdge> Edges;
    public required long BoundMicros;
    public required double Percentile;
    public long FixedMicros;                    // processing latency of interior nodes + lateness on the path
    public long ExpectedMicros;                 // from the solution
    public IReadOnlyList<string> NodePath => new[] { Edges[0].SourceNode }.Concat(Edges.Select(e => e.DestinationNode)).ToList();
}

internal sealed class PlanContext
{
    public required CompiledGraph Graph;
    public required string GraphInstanceId;
    public required ClusterSnapshot Snapshot;
    public required SchedulerOptions Options;
    public required IProfileStore Profiles;
    public List<AgentDraft> Agents = new();
    public List<GroupDraft> Groups = new();
    public List<PipeDraft> Pipes = new();
    public List<LatencyPath> LatencyPaths = new();
    public List<SpawnDiagnostic> Warnings = new();
    public List<string> Relaxed = new();
    public Dictionary<string, NodeDift> Dift = new();
    public LabelAnalysisInfo? Analysis;          // augmented by phase 1 (PL⁺, Held⁺); the graph's own until then
    public ReplanRequest? Replan;                // re-planning a running instance (S§10.4, S§10.5)
    public GraphInstanceInfo? Current;

    public int HostCount => Snapshot.Hosts.Count;
    public HostRuntimeInfo Host(int i) => Snapshot.Hosts[i];

    public NodeProfile Profile(GraphNode n) =>
        Profiles.Get(Graph.Name, n.Name, n.Process.Argv is { Count: > 0 } argv ? argv[0] : "") ?? new NodeProfile();

    public void Warn(string code, string message, params string[] details) =>
        Warnings.Add(new SpawnDiagnostic(code, OneOS.Runtime.Language.Severity.Warning, message, details));
}
