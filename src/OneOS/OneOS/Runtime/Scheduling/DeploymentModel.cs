using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OneOS.Runtime.Language;
using OneOS.Runtime.Language.Models;

namespace OneOS.Runtime.Scheduling;

// The deployment data model (S§11). Label references are label names in the graph's lattice.

public enum AgentRole { Primary, Standby }
public enum ComponentClass { IToI, IToX, XToI, XToX }
public enum DiftMode { Disabled, Enabled }
public enum AgentState { Pending, Starting, Running, Failed, Stopped }
public enum PipeState { Pending, Open, Closed }
public enum RoutingMode { Direct, LoadBalanced, Keyed, Broadcast }
public enum GraphInstanceState { Planned, Pending, Starting, Running, Degraded, Stopping, Failed, Stopped }

[Flags]
public enum ElidedChecks { None = 0, PortCeiling = 1, InstanceCeiling = 2, Host = 4, Compartment = 8 }

public sealed record KeyGroupRange(int From, int To);          // [From, To)

public sealed record AgentPortInfo(
    string Name, PortDirection Direction, string PortType, PortOrder? Order,
    TimeSpan? Lateness, TimeSpan? IdleTimeout, TimeSpan? GapTimeout, long MaxBufferBytes,
    OnLateMode? OnLate, string? OnLateRoutePort,
    LabelSpec? Ceiling,                         // input ports
    LabelSpec? DataLabeller,                    // output ports
    IReadOnlyList<string> PossibleLabels,       // PL (S§0.1); PL⁺ once DIFT augments it
    bool SequenceOrigin, bool SequencePropagating,
    Framing Framing);                           // how the port's stream is cut into messages (L§5.2)

public sealed record GraphAgentInfo(
    string AgentId, string GraphInstanceId, string NodeName, int InstanceIndex,
    AgentRole Role, string? PrimaryId, string? StandbyId,
    PartitionKind Kind, bool Stateless,

    // Process
    string Command, IReadOnlyList<string> Argv, bool OneToOne,

    // Partitioning and lanes
    IReadOnlyList<string>? KeyDomains, int KeyGroups, KeyGroupRange? OwnedKeyGroups,
    string? LaneLabel,

    // Ports
    IReadOnlyList<AgentPortInfo> Ports,

    // DIFT (S§4); filled in by phase 1
    ComponentClass Class,
    DiftMode Dift,
    string DiftRationale,                       // the S§4.2 rule that decided the mode
    IdmConfig? Idm,                             // sources and sinks with locations and labels, when Enabled
    string? SourceJoin,                         // SrcStatic, for Disabled X-to-I and X-to-X
    IReadOnlyList<ExternalSourceInfo> OutputEvaluable,  // trusted external-source labellers
    bool InternalMonitorDisabled,               // SPW09: internal monitor off; D_r still labels outputs

    // Labels (L§8.5)
    LabelSpec? InstanceCeiling,
    string? ExternalSinkCeiling,
    string? PlacementLabel,
    IReadOnlyList<string>? PlacementAnyOf,      // compartment-bound agents: L(host) ⊒ one of these
    bool CompartmentBoundAtRuntime,
    LabelRange? DynamicRange,
    LabelRange? HostSourceRange,
    bool UncheckedExclusive,

    // Placement
    string? PinPattern, ResourceVector Demand, string? HostId,

    // QoS
    IReadOnlyList<string> FlowIds,

    AgentState State, long Version);

public sealed record GraphPipeInfo(
    string PipeId, string GraphInstanceId, string EdgeName, bool Implicit,
    string SourceAgentId, string SourcePort, string DestinationAgentId, string DestinationPort,
    RoutingMode Routing,
    IReadOnlyList<IReadOnlyList<string>>? KeyPaths, KeyGroupRange? DestinationKeyGroups,
    int StreamId, bool ForwardEligible,
    IReadOnlyList<string> Allowed,              // S§5.2; the sidecar never sends other labels
    ElidedChecks Elided,                        // S§9.1
    bool NativeFormat,                          // co-located (A5)
    bool Unchecked, IReadOnlyList<string> FlowIds,
    long? EstimatedRate, long? EstimatedBandwidth,
    string? SourceHostId, string? DestinationHostId, long? ExpectedLatencyMicros,
    PipeState State, long Version,
    bool Bypass = false);                       // raw byte stream process to process, no sidecar (plan 6.5b)

public sealed record FlowInfo(
    string FlowId, string Name, IReadOnlyList<string> Edges,
    IReadOnlyList<IReadOnlyList<string>> NodePaths,
    LatencyMode Mode, IReadOnlyList<string>? ClockField,
    TimeSpan? MaxLatency, double? LatencyPercentile,
    string? UncheckedReason, IReadOnlyList<string> ExclusiveNodes,
    IReadOnlyList<long> ExpectedPathLatencyMicros);

public sealed record SpawnDiagnostic(string Code, Severity Severity, string Message, IReadOnlyList<string> Details)
{
    public override string ToString() =>
        $"{(Severity == Severity.Error ? "error" : Severity == Severity.Warning ? "warning" : "note")} {Code}: {Message}"
        + string.Concat(Details.Select(d => "\n  " + d));
}

public sealed record AuditReports(
    IReadOnlyList<DeclassificationSite> Declassifications,
    IReadOnlyList<UncheckedFlowReport> UncheckedFlows,
    IReadOnlyList<DiftReportEntry> Dift);       // DIFT report (S§9.1 step 5)

public sealed record DeploymentPlan(
    long SnapshotVersion,
    IReadOnlyDictionary<string, long> HostVersions,     // of the hosts the plan uses; the commit is conditional on them
    string SolverStatus, long ObjectiveValue, long BestBound,
    IReadOnlyDictionary<string, long> ObjectiveTerms,
    IReadOnlyList<string> RelaxedConstraints,
    IReadOnlyList<SpawnDiagnostic> Warnings,
    AuditReports Audit)
{
    // SchedulerOptions.EnforceDeliveryChecks, for the sidecars.
    public bool EnforceDeliveryChecks { get; init; } = true;
}

public sealed record GraphInstanceInfo(
    string GraphInstanceId, string GraphName, IReadOnlyList<object?> Args,
    LabelLattice Lattice, IReadOnlyList<FlowInfo> Flows,
    IReadOnlyList<GraphAgentInfo> Agents, IReadOnlyList<GraphPipeInfo> Pipes,
    IReadOnlyList<RoutingTableInfo> RoutingTables,
    DeploymentPlan Plan, GraphInstanceState State, DateTimeOffset CreatedAt, long Version)
{
    [JsonIgnore] public IEnumerable<string> AgentIds => Agents.Select(a => a.AgentId);
    [JsonIgnore] public IEnumerable<string> PipeIds => Pipes.Select(p => p.PipeId);

    public GraphAgentInfo Agent(string id) => Agents.First(a => a.AgentId == id);
    public IEnumerable<GraphAgentInfo> AgentsOf(string node) => Agents.Where(a => a.NodeName == node);

    public string ToJson() => JsonSerializer.Serialize(this, CompiledGraph.JsonOptions);
}

// A re-plan of a running instance: agents to place again, hosts to avoid for them, and new sizes for
// elastic groups ("node" or "node@lane").
// Promote: failed stateful primaries whose standby takes over (L§9.3, S§10.4): each keeps its agent id
// and moves to its standby's host, and a new standby is placed with the same rules as at spawn time.
public sealed record ReplanRequest(
    IReadOnlySet<string>? Replace = null,
    IReadOnlySet<string>? AvoidHosts = null,
    IReadOnlyDictionary<string, int>? GroupCounts = null,
    string Trigger = "membership",
    IReadOnlySet<string>? Promote = null)
{
    // Agents that become new incarnations: replaced ones, promoted primaries and their standbys.
    public IReadOnlySet<string> Reincarnated => (Replace ?? new HashSet<string>())
        .Concat((Promote ?? new HashSet<string>()).SelectMany(p => new[] { p, p + "/standby" })).ToHashSet();
}

// Returned by SpawnGraph: the instance id, its plan and current state.
public sealed class GraphInstanceHandle
{
    public GraphInstanceHandle(GraphInstanceInfo info) { Info = info; }
    public GraphInstanceInfo Info { get; internal set; }
    public string GraphInstanceId => Info.GraphInstanceId;
    public DeploymentPlan Plan => Info.Plan;
    public GraphInstanceState State => Info.State;
}

// A failed spawn or plan. The first diagnostic is the SP-code that failed it (S§13).
public sealed class SchedulingException : Exception
{
    public SchedulingException(IReadOnlyList<SpawnDiagnostic> diagnostics)
        : base(string.Join("\n", diagnostics.Where(d => d.Severity == Severity.Error)))
    {
        Diagnostics = diagnostics;
    }

    public SchedulingException(string code, string message, params string[] details)
        : this(new[] { new SpawnDiagnostic(code, Severity.Error, message, details) }) { }

    public IReadOnlyList<SpawnDiagnostic> Diagnostics { get; }
    public string Code => Diagnostics.First(d => d.Severity == Severity.Error).Code;
}
