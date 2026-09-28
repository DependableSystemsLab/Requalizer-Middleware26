using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OneOS.Runtime.Language.Ast;

namespace OneOS.Runtime.Language.Models;

// The compiler IR (L§11), extended with the data SpawnGraph needs (S§2.1). Everything here is
// plain data referenced by name, so it serializes to JSON; the only embedded AST is expression
// trees (process arguments and labeller bodies), which the runtime evaluates.

public enum PartitionKind { Unpartitioned, Keyless, Keyed }
public enum PortDirection { In, Out }
public enum OrderKind { Ordered, Sequenced }
public enum OnLateMode { Drop, Pass, Route }
public enum LatencyMode { Correlated, EventTime }
public enum LabelSpecKind { Constant, Labeller }
public enum TypeInfoKind { Primitive, Json, Format, KeyDomain, ClockDomain, Record, List, Label, Replica }
public enum TargetKind { Node, NodeAllInstances, NodeInstance, Port, PortAllInstances, PortInstance, Edge, Flow }

public sealed record CompiledProgram(
    LabelLattice Lattice,
    IReadOnlyDictionary<string, RecordTypeInfo> Types,
    IReadOnlyList<FormatEntry> Formats,
    IReadOnlyList<CompiledGraph> Graphs,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    [JsonIgnore] public bool HasErrors => Diagnostics.Any(d => d.IsError);
    public CompiledGraph? Graph(string name) => Graphs.FirstOrDefault(g => g.Name == name);
}

// --- Types ---

public sealed record TypeInfo(TypeInfoKind Kind, string Name, TypeInfo? Element = null, string? Repr = null, string? ClockUnit = null)
{
    public override string ToString() => Kind == TypeInfoKind.List ? $"list<{Element}>" : Name;
}
public sealed record FieldInfo(string Name, TypeInfo Type);
public sealed record RecordTypeInfo(string Name, IReadOnlyList<FieldInfo> Fields);

// --- Graph ---

public sealed record GraphParameterInfo(string Name, string Type);

public sealed record CompiledGraph(
    string Name,
    IReadOnlyList<GraphParameterInfo> Parameters,
    IReadOnlyList<object?>? Args,                  // null until bound at spawn (L§9.1)
    LabelLattice Lattice,
    IReadOnlyDictionary<string, RecordTypeInfo> Types,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,                // declared edges, then implicit route edges
    IReadOnlyList<GraphFlow> Flows,
    GraphPolicies Policies,
    LabelAttachments Labels,
    LabelAnalysisInfo? Analysis,                   // null when structural errors prevented label analysis
    IReadOnlyList<Diagnostic> Diagnostics,
    SourceSpan Span)
{
    // Every named labeller the program declares, attached or not, by name. The IDM gets them all
    // (DiftPolicyModule): the program's code names them at its injection points.
    public IReadOnlyDictionary<string, LabelSpec> Labellers { get; init; } = new Dictionary<string, LabelSpec>();

    [JsonIgnore] public bool IsBound => Args != null;
    [JsonIgnore] public bool HasErrors => Diagnostics.Any(d => d.IsError);

    public GraphNode Node(string name) => Nodes.First(n => n.Name == name);
    public GraphNode? FindNode(string name) => Nodes.FirstOrDefault(n => n.Name == name);
    public GraphEdge? Edge(string name) => Edges.FirstOrDefault(e => e.Name == name);
    public GraphPort Port(string node, string port) => Node(node).Ports.First(p => p.Name == port);
    public GraphPort Port(string portRef) { var i = portRef.IndexOf('.'); return Port(portRef[..i], portRef[(i + 1)..]); }
    public IEnumerable<GraphEdge> EdgesInto(string node, string? port = null) =>
        Edges.Where(e => e.DestinationNode == node && (port == null || e.DestinationPort == port));
    public IEnumerable<GraphEdge> EdgesOutOf(string node, string? port = null) =>
        Edges.Where(e => e.SourceNode == node && (port == null || e.SourcePort == port));

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record ProcessSpec(
    Expression Command,
    IReadOnlyList<Expression> Args,
    IReadOnlyList<string>? Argv);                  // command + arguments, evaluated at spawn (L§6.1)

public sealed record InstanceRange(int Min, int Max)
{
    [JsonIgnore] public bool IsElastic => Min < Max;
}

public sealed record GraphNode(
    string Name,
    int Index,                                     // declaration order
    IReadOnlyList<string> Attributes,
    bool OneToOne,
    PartitionKind Partitioning,
    IReadOnlyList<string> KeyDomains,              // keyed nodes: the key tuple
    int KeyGroups,                                 // G (L§6.4); default 128
    InstanceRange Instances,                       // from `partitions`, default 1..1
    ProcessSpec Process,
    IReadOnlyList<GraphPort> Inputs,
    IReadOnlyList<GraphPort> Outputs,
    SourceSpan Span)
{
    [JsonIgnore] public bool Stateless => Partitioning == PartitionKind.Keyless;
    // @json_lines: all ports multiplexed on stdin/stdout as {"port", "data"} lines, instead of one stream per port.
    [JsonIgnore] public bool JsonLines => Attributes.Contains("json_lines");
    [JsonIgnore] public bool IsPartitioned => Partitioning != PartitionKind.Unpartitioned;
    [JsonIgnore] public bool IsSource => Inputs.Count == 0;
    [JsonIgnore] public bool IsSink => !DataOutputs.Any();
    // Outputs other than the process's error stream (`stderr`, L§6.1).
    [JsonIgnore] public IEnumerable<GraphPort> DataOutputs => Outputs.Where(p => !p.IsStderr);
    [JsonIgnore] public IEnumerable<GraphPort> Ports => Inputs.Concat(Outputs);
}

public sealed record GraphPort(
    string Node,
    string Name,
    PortDirection Direction,
    TypeInfo Type,
    PortOrder? Order,
    PortOrdering? Ordering,                        // effective merge configuration for ordered/sequenced inputs
    SequenceInfo? Sequence,
    SourceSpan Span,
    Framing Framing)                               // how a stream on this port is cut into messages (L§5.2)
{
    [JsonIgnore] public string Ref => $"{Node}.{Name}";
    [JsonIgnore] public bool IsStderr => Direction == PortDirection.Out && Name == "stderr";
}

// The declared order spec with its clock field resolved (L§6.6.1).
public sealed record PortOrder(
    OrderKind Kind,
    IReadOnlyList<string>? ClockField,
    string? ClockDomain,
    string? ClockUnit,
    IReadOnlyList<string>? Per,
    long? WithinNanos,
    long? WithinClockUnits);

// Merge configuration of an ordered or sequenced input port, with defaults applied (L§6.6.3, L§7.2).
public sealed record PortOrderingSettings(
    long LatenessNanos,
    bool LatenessFromPolicy,
    long? IdleTimeoutNanos,
    long? GapTimeoutNanos,
    long MaxBufferBytes,
    OnLateMode OnLate,
    string? OnLateRoutePort);

public sealed record PortOrdering(
    PortOrderingSettings Default,
    IReadOnlyDictionary<int, PortOrderingSettings> InstanceOverrides);   // from `n[i].p` targets

// Sequencing marks (L§6.6.4, S§5.3).
public sealed record SequenceInfo(
    bool Origin,                                   // this output port stamps sequence numbers
    bool Propagating,                              // this @one_to_one output inherits them
    string? OriginPort,                            // for a sequenced input: the origin port O
    IReadOnlyList<string>? PathNodes);             // for a sequenced input: nodes between O and here

public sealed record GraphEdge(
    string Name,
    int Index,
    bool Named,
    bool Implicit,                                 // implicit route edge from on_late(route(...)) (L§6.2)
    string? ImplicitOf,                            // for implicit edges: the declared edge it mirrors
    EdgeOp Op,
    string SourceNode, string SourcePort,
    string DestinationNode, string DestinationPort,
    IReadOnlyList<IReadOnlyList<string>>? KeyPaths, // resolved key extraction for keyed receivers (L§6.5)
    bool ForwardEligible,
    SourceSpan Span)
{
    [JsonIgnore] public string Source => $"{SourceNode}.{SourcePort}";
    [JsonIgnore] public string Destination => $"{DestinationNode}.{DestinationPort}";
}

public sealed record GraphFlow(
    string Name,
    IReadOnlyList<string> Edges,
    IReadOnlyList<string> EntryPorts,
    IReadOnlyList<string> ExitPorts,
    IReadOnlyList<string> InteriorNodes,
    IReadOnlyList<string> ExclusiveNodes,          // for unchecked flows (L§8.7)
    IReadOnlyList<string> NonExclusiveNodes,
    IReadOnlyList<IReadOnlyList<string>> EdgePaths,
    IReadOnlyList<IReadOnlyList<string>> NodePaths,
    LatencyMode Mode,
    IReadOnlyList<string>? ClockField,
    long? MaxLatencyNanos,
    double? LatencyPercentile,
    string? UncheckedReason,
    SourceSpan Span);

// --- Policies (L§7), normalized ---

public sealed record ResolvedTarget(TargetKind Kind, string Name, int? Instance, string? Port)
{
    public override string ToString() => Kind switch
    {
        TargetKind.NodeAllInstances => $"{Name}[*]",
        TargetKind.NodeInstance => $"{Name}[{Instance}]",
        TargetKind.Port => $"{Name}.{Port}",
        TargetKind.PortAllInstances => $"{Name}[*].{Port}",
        TargetKind.PortInstance => $"{Name}[{Instance}].{Port}",
        _ => Name,
    };
}

public sealed record NormalizedPolicy(string Name, IReadOnlyList<string> Args, IReadOnlyList<string>? By, IReadOnlyList<ResolvedTarget> Targets, SourceSpan Span);
public sealed record AlwaysPolicy(string Node, int? Instance);
public sealed record PinPolicy(string Node, int? Instance, string Pattern);
public sealed record RatePolicy(ResolvedTarget Target, double MessagesPerSecond, double Percentile);
public sealed record LatencyPolicy(ResolvedTarget Target, long MaxNanos, double Percentile, IReadOnlyList<string>? By);

public sealed record GraphPolicies(
    IReadOnlyList<NormalizedPolicy> All,
    IReadOnlyList<AlwaysPolicy> Always,
    IReadOnlyList<PinPolicy> Pins,
    IReadOnlyList<RatePolicy> MinRates,
    IReadOnlyList<LatencyPolicy> MaxLatencies);

// --- Labels (L§8.4) ---

public sealed record LabellerParamInfo(string Name, string Type);

// A constant label, or a labeller (named or inline) with its body.
public sealed record LabelSpec(
    LabelSpecKind Kind,
    string? Label,
    string? LabellerName,                          // null for inline lambdas
    IReadOnlyList<LabellerParamInfo>? Parameters,
    Expression? Body,
    string? SourceText)
{
    public override string ToString() => Kind == LabelSpecKind.Constant ? Label! : LabellerName ?? SourceText ?? "<lambda>";
}

public sealed record LabelRange(string Lo, string Hi);

public sealed record LabelAttachments(
    IReadOnlyDictionary<string, LabelSpec> DataLabellers,                      // output port "n.p"
    IReadOnlyDictionary<string, LabelSpec> PortCeilings,                       // input port "n.p"
    IReadOnlyDictionary<string, LabelSpec> InstanceCeilings,                   // node, from `n` or `n[*]`
    IReadOnlyDictionary<string, IReadOnlyDictionary<int, LabelSpec>> InstanceCeilingOverrides,  // node → i, from `n[i]`
    IReadOnlyDictionary<string, LabelRange> Dynamic,                           // node
    IReadOnlyDictionary<string, LabelRange> Host);                             // node

// --- Label analysis results (L§8.6, S§0.1) ---

public sealed record PortLabelInfo(string Lo, string Up, IReadOnlyList<string> PossibleLabels);

public sealed record NodeLabelInfo(
    string InLo, string InUp, string Held,
    IReadOnlyList<string> InputSet,                // union of delivered sets (S§0.1)
    IReadOnlyList<string> BaseSet);

// L(host) ⊒ MinHostLabel. MinHostLabel is null when no static constraint applies.
public sealed record PlacementConstraint(string Node, string? MinHostLabel, string Reason);

public sealed record DeclassificationSite(
    string Port,
    string BaseLo, string BaseUp,
    IReadOnlyList<string> LowerResults,
    string Labeller,
    IReadOnlyList<string> FieldsRead,
    string TrustedComponent,
    string TrustedProcess,
    bool DynamicMonitor);                          // potential declassification by the middleware monitor

public sealed record UncheckedFlowReport(
    string Flow, string Reason,
    IReadOnlyList<string> ExclusiveNodes, IReadOnlyList<string> NonExclusiveNodes,
    IReadOnlyList<Diagnostic> Masked);

public sealed record LabelAnalysisInfo(
    IReadOnlyDictionary<string, PortLabelInfo> Ports,
    IReadOnlyDictionary<string, NodeLabelInfo> Nodes,
    IReadOnlyList<PlacementConstraint> Placement,
    IReadOnlyList<DeclassificationSite> Declassifications,
    IReadOnlyList<UncheckedFlowReport> UncheckedFlows);
