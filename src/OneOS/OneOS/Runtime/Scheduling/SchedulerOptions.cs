using OneOS.Runtime.Language.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace OneOS.Runtime.Scheduling;

public enum LaneMode { Auto, Off, Always }

// Objective weights (S§7.4), in the spec's units.
public sealed record ObjectiveWeights(
    double FlowLatencyPerMs = 1000,          // W1, per ms of excess
    double BandwidthShortfallPerMBps = 100,  // W2, per MB/s of shortfall
    double CrossHostPerMBps = 1,             // W3, per MB/s
    double LoadPerMille = 10,                // W4, per per-mille of the busiest host
    double RelaxedViolation = 500,           // W5, per relaxed H5/H7 violation
    double SameZoneStandby = 50,             // W5, per standby in its primary's zone
    double Instance = 1,                     // W6, per active candidate
    double ForwardPairSplit = 50);           // W3, per forward-eligible sender/receiver pair on different hosts (L§6.5 hint)

// Scheduler configuration (S§2.5).
public sealed record SchedulerOptions
{
    public TimeSpan SolverTimeLimit { get; init; } = TimeSpan.FromSeconds(5);
    public int SolverWorkers { get; init; } = 8;
    public int RandomSeed { get; init; } = 1;
    public ObjectiveWeights Weights { get; init; } = new();
    public bool ReserveStandbyCapacity { get; init; } = true;
    public long LocalLatencyMicros { get; init; } = 50;
    public long MarshallingMicros { get; init; } = 200;
    public LaneMode LaneMode { get; init; } = LaneMode.Auto;
    public int MaxLanesPerNode { get; init; } = 8;
    public bool DisableInternalMonitorWhenInternal { get; init; } = true;
    public string? UnlabeledExternalSource { get; init; }
    public bool AllowUnanalyzedNodes { get; init; }
    // Bypass edges (plan 6.5b): qualifying edges carry raw byte streams without the sidecar. They keep byte
    // metrics only, and end-to-end latency isn't traced across them; turn off to measure it everywhere.
    // Unsegmentable streams are raw regardless.
    public bool BypassEdges { get; init; } = true;
    // Cluster-wide sockets a node's program listens on (plan step 8.5), as external-source locations
    // ("socket:<port>" or "socket:*"). Merged into the analyzer's report; such a node runs with the IDM enabled.
    public Func<GraphNode, IReadOnlyList<string>, IReadOnlyList<string>>? ProgramSockets { get; init; }

    // The static interface analyzer (A1) and external DIFT policy (A2). Without an analyzer, every
    // node is assumed to have no external interfaces. The policy defaults to the analyzer, if it is one.
    public IInterfaceAnalyzer? Analyzer { get; init; }
    public IExternalDiftPolicy? DiftPolicy { get; init; }
    public TimeSpan LbSolveBudget { get; init; } = TimeSpan.FromMilliseconds(100);
    public int MaxCommitRetries { get; init; } = 3;
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    // Evaluation switches: the comparison systems of the Requalizer paper (tools/DemoRunner). Defaults are
    // OneOS's own behaviour.
    //   DiftOverride           every node's DIFT mode forced: Enabled models layered DIFT (tracking everywhere,
    //                          whatever the dataflow allows), Disabled a platform with no tracking.
    //   LabelAwarePlacement    false: host labels don't constrain placement (a scheduler unaware of DIFT).
    //   EnforceDeliveryChecks  false: sidecars count failed delivery checks but deliver anyway (an unaware
    //                          platform doesn't guard internal interfaces; the violations stay measurable).
    public DiftMode? DiftOverride { get; init; }
    public bool LabelAwarePlacement { get; init; } = true;
    public bool EnforceDeliveryChecks { get; init; } = true;

    // Defaults for unknown profile estimates (S§2.4).
    public ResourceVector DefaultDemand { get; init; } = new(500, 256L << 20);
    public long DefaultMessageSize { get; init; } = 1024;

    // Allocates cluster-unique graph instance ids. Tests replace it for reproducible ids.
    public Func<string> NewGraphInstanceId { get; init; } = DefaultInstanceId;

    private static long _counter;
    private static string DefaultInstanceId() =>
        $"g{DateTime.UtcNow:yyMMddHHmmss}{Interlocked.Increment(ref _counter) % 1000:D3}{Random.Shared.Next(0x1000):x3}";
}

// Per-node estimates (S§2.4). Null means unknown; unknown estimates never make the model infeasible.
public sealed record NodeProfile(
    ResourceVector? Demand = null,
    long? ProcessingLatencyMicros = null,
    double? ThroughputPerInstance = null,
    IReadOnlyDictionary<string, long>? MessageSize = null,          // output port → bytes
    IReadOnlyDictionary<string, double>? OutputRate = null,         // output port → msg/s
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>? LabelFrequency = null, // port → label → fraction
    double? IdmCpuFactor = null,
    long? IdmLatencyMicros = null);

public interface IProfileStore
{
    // Keyed by (graph, node), falling back to the process command (S§2.4).
    NodeProfile? Get(string graph, string node, string command);
}

public sealed class InMemoryProfileStore : IProfileStore
{
    private readonly Dictionary<(string Graph, string Node), NodeProfile> _byNode = new();
    private readonly Dictionary<string, NodeProfile> _byCommand = new();

    public InMemoryProfileStore Set(string graph, string node, NodeProfile p) { _byNode[(graph, node)] = p; return this; }
    public InMemoryProfileStore SetForCommand(string command, NodeProfile p) { _byCommand[command] = p; return this; }

    public NodeProfile? Get(string graph, string node, string command) =>
        _byNode.TryGetValue((graph, node), out var p) ? p : _byCommand.TryGetValue(command, out var c) ? c : null;
}
