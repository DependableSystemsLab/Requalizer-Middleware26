using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneOS.Runtime.Language;
using OneOS.Runtime.Language.Models;

namespace OneOS.Runtime.Scheduling;

// --- Inputs: the static interface analyzer (A1) and the external DIFT policy (A2), S§2.3 ---

public sealed record SourceArtifact(string Path, string ContentHash);

public enum ExternalLabelKind { Static, OutputEvaluable, Dynamic, Unknown }

// A label from the external DIFT policy.
//   Static: `Label`.
//   OutputEvaluable (sources): a trusted labeller over the node's output; `Range` lists its possible results.
//   Dynamic (sinks): computed from the data at the sink; `LowerBound` when known.
//   Unknown: the policy says nothing.
public sealed record ExternalLabel(ExternalLabelKind Kind, string? Label = null, IReadOnlyList<string>? Range = null, string? LowerBound = null, string? Text = null)
{
    public static ExternalLabel Static(string label) => new(ExternalLabelKind.Static, Label: label);
    public static ExternalLabel OutputEvaluable(string text, params string[] range) => new(ExternalLabelKind.OutputEvaluable, Range: range, Text: text);
    public static ExternalLabel Dynamic(string? lowerBound = null, string? text = null) => new(ExternalLabelKind.Dynamic, LowerBound: lowerBound, Text: text);
    public static readonly ExternalLabel Unknown = new(ExternalLabelKind.Unknown);

    public override string ToString() => Kind switch
    {
        ExternalLabelKind.Static => $"Static({Label})",
        ExternalLabelKind.OutputEvaluable => $"OutputEvaluable({Text ?? string.Join("|", Range ?? Array.Empty<string>())})",
        ExternalLabelKind.Dynamic => LowerBound != null ? $"Dynamic(≥ {LowerBound})" : "Dynamic",
        _ => "Unknown",
    };
}

public sealed record ExternalInterface(string Location, string? Description = null);

// What the analyzer found in a node's code: external inputs and outputs with code locations, and
// for each internal output port the internal inputs and external sources that can reach it.
public sealed record InterfaceReport(
    IReadOnlyList<ExternalInterface> Sources,
    IReadOnlyList<ExternalInterface> Sinks,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? InternalDataflows = null);

public interface IInterfaceAnalyzer
{
    // Resolves the code a node runs from its bound argument vector (e.g. the script path).
    SourceArtifact Resolve(GraphNode node, IReadOnlyList<string> argv);
    // Null when the analyzer can't analyze the artifact (S§2.3, analyzer failure).
    InterfaceReport? Analyze(SourceArtifact artifact);
}

public interface IExternalDiftPolicy
{
    ExternalLabel SourceLabel(string graph, string node, string location);
    ExternalLabel SinkLabel(string graph, string node, string location);
}

public sealed record ExternalSourceInfo(string Location, ExternalLabel Label);
public sealed record ExternalSinkInfo(string Location, ExternalLabel Label);

// A1 and A2 combined for one node (S§2.3).
public sealed record NodeInterfaceAnalysis(
    SourceArtifact? Artifact,
    bool Analyzed,
    IReadOnlyList<ExternalSourceInfo> ExternalSources,
    IReadOnlyList<ExternalSinkInfo> ExternalSinks,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? InternalDataflows);

// IDM configuration for an Enabled agent (A3).
public sealed record IdmConfig(string LanguageRuntime, IReadOnlyList<ExternalSourceInfo> Sources, IReadOnlyList<ExternalSinkInfo> Sinks);

// The default when no analyzer is configured: every node is assumed to have no external interfaces,
// i.e. to be I-to-I. The DIFT report says so, since the assumption is not verified.
public sealed class AssumeInternalAnalyzer : IInterfaceAnalyzer, IExternalDiftPolicy
{
    public static readonly AssumeInternalAnalyzer Instance = new();
    public SourceArtifact Resolve(GraphNode node, IReadOnlyList<string> argv) => DiftDefaults.ResolveArtifact(argv);
    public InterfaceReport? Analyze(SourceArtifact artifact) => new(Array.Empty<ExternalInterface>(), Array.Empty<ExternalInterface>());
    public ExternalLabel SourceLabel(string graph, string node, string location) => ExternalLabel.Unknown;
    public ExternalLabel SinkLabel(string graph, string node, string location) => ExternalLabel.Unknown;
}

// Declared interfaces and labels per node name: for tests, the simulator, and clusters without a
// code analyzer. Nodes it doesn't mention have no external interfaces.
public sealed class InMemoryDift : IInterfaceAnalyzer, IExternalDiftPolicy
{
    private readonly Dictionary<string, (List<(string Loc, ExternalLabel Label)> Sources, List<(string Loc, ExternalLabel Label)> Sinks)> _nodes = new();
    private readonly HashSet<string> _unanalyzable = new();

    public InMemoryDift Source(string node, string location, ExternalLabel label) { Entry(node).Sources.Add((location, label)); return this; }
    public InMemoryDift Sink(string node, string location, ExternalLabel label) { Entry(node).Sinks.Add((location, label)); return this; }
    public InMemoryDift Unanalyzable(string node) { _unanalyzable.Add(node); return this; }

    private (List<(string, ExternalLabel)> Sources, List<(string, ExternalLabel)> Sinks) Entry(string node)
    {
        if (!_nodes.TryGetValue(node, out var e)) _nodes[node] = e = (new(), new());
        return e;
    }

    // The artifact is the node name, so that declarations are looked up per node.
    public SourceArtifact Resolve(GraphNode node, IReadOnlyList<string> argv) => new(node.Name, "node:" + node.Name);

    public InterfaceReport? Analyze(SourceArtifact artifact)
    {
        if (_unanalyzable.Contains(artifact.Path)) return null;
        if (!_nodes.TryGetValue(artifact.Path, out var e)) return new(Array.Empty<ExternalInterface>(), Array.Empty<ExternalInterface>());
        return new(e.Sources.Select(s => new ExternalInterface(s.Item1)).ToList(), e.Sinks.Select(s => new ExternalInterface(s.Item1)).ToList());
    }

    public ExternalLabel SourceLabel(string graph, string node, string location) =>
        _nodes.TryGetValue(node, out var e) ? e.Sources.FirstOrDefault(s => s.Item1 == location).Item2 ?? ExternalLabel.Unknown : ExternalLabel.Unknown;

    public ExternalLabel SinkLabel(string graph, string node, string location) =>
        _nodes.TryGetValue(node, out var e) ? e.Sinks.FirstOrDefault(s => s.Item1 == location).Item2 ?? ExternalLabel.Unknown : ExternalLabel.Unknown;

    // { "nodeName": { "sources": [ { "location": "db", "label": "secret" } | { "location": "x", "range": ["a","b"] } ],
    //                 "sinks":   [ { "location": "out", "label": "public" } | { "location": "web", "dynamic": true, "lowerBound": "internal" } ],
    //                 "unanalyzable": true } }
    public static InMemoryDift FromJson(string json)
    {
        var d = new InMemoryDift();
        using var doc = JsonDocument.Parse(json);
        foreach (var node in doc.RootElement.EnumerateObject())
        {
            if (node.Value.TryGetProperty("unanalyzable", out var u) && u.GetBoolean()) d.Unanalyzable(node.Name);
            foreach (var (kind, add) in new (string, Action<string, ExternalLabel>)[] { ("sources", (l, x) => d.Source(node.Name, l, x)), ("sinks", (l, x) => d.Sink(node.Name, l, x)) })
            {
                if (!node.Value.TryGetProperty(kind, out var list)) continue;
                foreach (var i in list.EnumerateArray())
                {
                    string loc = i.GetProperty("location").GetString()!;
                    string? Str(string k) => i.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    ExternalLabel label =
                        i.TryGetProperty("range", out var r) ? ExternalLabel.OutputEvaluable(Str("text") ?? loc, r.EnumerateArray().Select(x => x.GetString()!).ToArray())
                        : i.TryGetProperty("dynamic", out var dy) && dy.GetBoolean() ? ExternalLabel.Dynamic(Str("lowerBound"), Str("text"))
                        : Str("label") is string l ? ExternalLabel.Static(l)
                        : ExternalLabel.Unknown;
                    add(loc, label);
                }
            }
        }
        return d;
    }
}

internal static class DiftDefaults
{
    // The artifact of a process: its first argument if it names a file (a script), else the command.
    public static SourceArtifact ResolveArtifact(IReadOnlyList<string> argv)
    {
        var path = argv.Count > 1 ? argv[1] : argv[0];
        string hash;
        try
        {
            hash = File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "path:" + path;
        }
        catch (Exception) { hash = "path:" + path; }
        return new SourceArtifact(path, hash);
    }
}

// --- Phase 1 results (S§4) ---

public sealed record DiftReportEntry(
    string Node,
    ComponentClass Class,
    DiftMode Mode,
    string Rule,
    string? ExternalSinkCeiling,
    IReadOnlyList<string> Lanes,
    IReadOnlyList<string> TrustedLabellers,
    IReadOnlyList<SpawnDiagnostic> Findings,
    IReadOnlyDictionary<string, int> ElidedChecksPerPipe);

internal sealed class NodeDift
{
    public required NodeInterfaceAnalysis Interfaces;
    public ComponentClass Class;
    public DiftMode Mode;
    public string Rule = "";
    public int? SourceJoin;                      // SrcStatic, for Disabled nodes with external sources
    public List<string> TrustedLabellers = new();
    public List<ExternalSourceInfo> OutputEvaluable = new();
    public int? ExternalSinkCeiling;             // C_ext
    public HashSet<int> External = new();        // possible external-label joins (S§4.3 `ext`)
    public bool InternalMonitorDisabled;         // SPW09
    public bool Unanalyzed;                      // analyzer failed; AllowUnanalyzedNodes
    public List<int> Lanes = new();              // empty: not laned
    public List<SpawnDiagnostic> Findings = new();
    public IdmConfig? Idm;
    public string LanguageRuntime = "";

    public NodeAugmentation Augmentation => new(External, ExternalSinkCeiling);
}

// Phase 1 (S§4): classification, DIFT modes, automatic ceilings, augmented label analysis, lanes.
internal static class DiftPhase
{
    // Analysis results are cached per analyzer, by artifact content hash (S§2.3).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IInterfaceAnalyzer, ConcurrentDictionary<string, InterfaceReport?>> Cache = new();

    public static void Configure(PlanContext ctx)
    {
        var g = ctx.Graph;
        var L = g.Lattice;
        var o = ctx.Options;
        var analyzer = o.Analyzer ?? AssumeInternalAnalyzer.Instance;
        var policy = o.DiftPolicy ?? (analyzer as IExternalDiftPolicy) ?? AssumeInternalAnalyzer.Instance;
        var errors = new List<SpawnDiagnostic>();
        var exclusive = g.Flows.Where(f => f.UncheckedReason != null).SelectMany(f => f.ExclusiveNodes.Select(n => (n, f.Name))).GroupBy(x => x.n).ToDictionary(x => x.Key, x => x.First().Name);

        int? defaultSource = o.UnlabeledExternalSource is string ul && L.Contains(ul) ? L.IndexOf(ul) : null;
        if (o.UnlabeledExternalSource != null && defaultSource == null)
            errors.Add(new SpawnDiagnostic("SP011", Severity.Error, $"UnlabeledExternalSource '{o.UnlabeledExternalSource}' is not a label of graph '{g.Name}'", Array.Empty<string>()));

        // Interfaces (A1 + A2) per node.
        foreach (var n in g.Nodes)
        {
            var argv = n.Process.Argv!;
            var artifact = analyzer.Resolve(n, argv);
            var report = Cache.GetOrCreateValue(analyzer).GetOrAdd(artifact.ContentHash, _ => analyzer.Analyze(artifact));
            var d = new NodeDift { Interfaces = null!, LanguageRuntime = Path.GetFileName(argv[0]) };
            if (report == null)
            {
                if (!o.AllowUnanalyzedNodes)
                {
                    errors.Add(new SpawnDiagnostic("SP012", Severity.Error, $"the interface analyzer failed on '{n.Name}' ({artifact.Path})", Array.Empty<string>()));
                    continue;
                }
                d.Unanalyzed = true;
                d.Interfaces = new NodeInterfaceAnalysis(artifact, false,
                    new[] { new ExternalSourceInfo("<unanalyzed>", ExternalLabel.Unknown) }, new[] { new ExternalSinkInfo("<unanalyzed>", ExternalLabel.Unknown) }, null);
                d.Findings.Add(Warn("SPW08", $"'{n.Name}' could not be analyzed; treated as X-to-X with DIFT enabled"));
            }
            else
            {
                // An Unknown source takes UnlabeledExternalSource as its static label, if configured (S§2.3).
                ExternalLabel SourceLabel(string loc) => policy.SourceLabel(g.Name, n.Name, loc) is { Kind: ExternalLabelKind.Unknown or ExternalLabelKind.Dynamic } && defaultSource is int ds
                    ? ExternalLabel.Static(L.Name(ds)) : policy.SourceLabel(g.Name, n.Name, loc);
                // Cluster-wide sockets the program listens on are external sources too (plan step 8.5).
                var sources = report.Sources.Select(s => s.Location)
                    .Concat((o.ProgramSockets?.Invoke(n, argv) ?? Array.Empty<string>()).Where(s => !report.Sources.Any(x => x.Location == s))).ToList();
                d.Interfaces = new NodeInterfaceAnalysis(artifact, true,
                    sources.Select(s => new ExternalSourceInfo(s, SourceLabel(s))).ToList(),
                    // An Unknown sink is treated as Static(⊥): only ⊥ data may leave through it (S§2.3).
                    report.Sinks.Select(s => new ExternalSinkInfo(s.Location, policy.SinkLabel(g.Name, n.Name, s.Location) is { Kind: ExternalLabelKind.Unknown }
                        ? ExternalLabel.Static(L.Bottom) : policy.SinkLabel(g.Name, n.Name, s.Location))).ToList(),
                    report.InternalDataflows);
            }

            foreach (var s in d.Interfaces.ExternalSources.Where(s => s.Label.Kind is ExternalLabelKind.Unknown or ExternalLabelKind.Dynamic))
                if (defaultSource == null && o.UnlabeledExternalSource == null)
                    errors.Add(new SpawnDiagnostic("SP011", Severity.Error, $"external source '{s.Location}' of '{n.Name}' has no label in the external DIFT policy, and no UnlabeledExternalSource is configured", Array.Empty<string>()));
            foreach (var label in d.Interfaces.ExternalSources.SelectMany(s => Labels(s.Label)).Concat(d.Interfaces.ExternalSinks.SelectMany(s => Labels(s.Label))))
                if (!L.Contains(label))
                    errors.Add(new SpawnDiagnostic("SP011", Severity.Error, $"'{n.Name}': the external DIFT policy names '{label}', which is not a label of graph '{g.Name}'", Array.Empty<string>()));

            bool hasSources = d.Interfaces.ExternalSources.Count > 0, hasSinks = d.Interfaces.ExternalSinks.Count > 0;
            d.Class = (hasSources, hasSinks) switch
            {
                (false, false) => ComponentClass.IToI,
                (false, true) => ComponentClass.IToX,
                (true, false) => ComponentClass.XToI,
                _ => ComponentClass.XToX,
            };
            ctx.Dift[n.Name] = d;
        }
        if (errors.Count > 0) throw new SchedulingException(errors.Concat(ctx.Warnings).ToList());

        // Modes, in topological order: an X-to-X decision needs MaxIn, which depends on upstream decisions.
        foreach (var n in TopologicalOrder(g))
        {
            var d = ctx.Dift[n.Name];
            var sources = d.Interfaces.ExternalSources;
            var sinks = d.Interfaces.ExternalSinks;
            var statics = sources.Where(s => s.Label.Kind == ExternalLabelKind.Static).Select(s => L.IndexOf(s.Label.Label!)).ToList();
            var evaluable = sources.Where(s => s.Label.Kind == ExternalLabelKind.OutputEvaluable).ToList();
            int srcStatic = L.JoinAll(statics);
            bool sourcesKnown = sources.All(s => s.Label.Kind is ExternalLabelKind.Static or ExternalLabelKind.OutputEvaluable);
            var sinkBounds = sinks.Select(s => s.Label.Kind switch
            {
                ExternalLabelKind.Static => (int?)L.IndexOf(s.Label.Label!),
                ExternalLabelKind.Dynamic when s.Label.LowerBound != null => L.IndexOf(s.Label.LowerBound),
                _ => null,
            }).ToList();
            bool sinksBounded = sinkBounds.All(b => b != null);
            int evaluableMax = L.JoinAll(evaluable.SelectMany(s => s.Label.Range!).Select(L.IndexOf));
            bool declaredDynamic = g.Labels.Dynamic.ContainsKey(n.Name);

            if (d.Unanalyzed)
            {
                d.Mode = DiftMode.Enabled;
                d.Rule = "not analyzable: treated as X-to-X with DIFT enabled (AllowUnanalyzedNodes)";
            }
            else if (exclusive.TryGetValue(n.Name, out var flow))
            {
                d.Mode = DiftMode.Disabled;
                d.Rule = $"exclusive node of unchecked flow '{flow}': always Disabled, no automatic ceiling";
            }
            else if (sources.Any(s => s.Location.StartsWith(JavaScriptSockets.LocationPrefix, StringComparison.Ordinal)))
            {
                // User decision (plan step 8, S2): a node listening on a cluster-wide socket has external input
                // and always runs with the IDM, whatever its sources' labels.
                d.Mode = DiftMode.Enabled;
                d.Rule = "listens on a cluster-wide network socket: external input, IDM enabled";
            }
            else switch (d.Class)
            {
                case ComponentClass.IToI:
                    d.Mode = DiftMode.Disabled;
                    d.Rule = d.Interfaces.Analyzed && o.Analyzer == null
                        ? "I-to-I (assumed: no interface analyzer is configured)"
                        : "I-to-I: labels follow the language rules";
                    break;
                case ComponentClass.IToX:
                    if (sinksBounded)
                    {
                        d.Mode = DiftMode.Disabled;
                        d.ExternalSinkCeiling = L.MeetAll(sinkBounds.Select(b => b!.Value));
                        d.Rule = $"I-to-X with static or lower-bounded sinks: C_ext = {L.Name(d.ExternalSinkCeiling.Value)}";
                    }
                    else
                    {
                        d.Mode = DiftMode.Enabled;
                        d.Rule = "I-to-X with a sink whose label has no known lower bound";
                    }
                    break;
                case ComponentClass.XToI:
                    d.Mode = sourcesKnown ? DiftMode.Disabled : DiftMode.Enabled;
                    d.Rule = sourcesKnown ? "X-to-I with static or output-evaluable sources: source labels joined onto outputs"
                                          : "X-to-I with an external source whose label is only known at runtime";
                    break;
                case ComponentClass.XToX:
                {
                    // MaxIn: the join of the augmented input set, given the decisions upstream.
                    var (partial, _) = LabelAnalysis.Run(g, g.Args, ctx.Dift.ToDictionary(kv => kv.Key, kv => kv.Value.Augmentation));
                    int maxIn = L.JoinAll(partial.Nodes[n.Name].InputSet.Select(L.IndexOf));
                    // Stricter than the spec's MaxIn ⊔ SrcStatic: output-evaluable data can reach the sinks too (S0).
                    int reach = L.Join(L.Join(maxIn, srcStatic), evaluableMax);
                    bool proven = sourcesKnown && sinksBounded && sinkBounds.All(b => L.Leq(reach, b!.Value));
                    d.Mode = proven ? DiftMode.Disabled : DiftMode.Enabled;
                    d.Rule = proven ? $"X-to-X: every sink write is provably compliant ({L.Name(reach)} ⊑ every sink bound)"
                                    : !sourcesKnown ? "X-to-X with an external source whose label is only known at runtime"
                                    : !sinksBounded ? "X-to-X with a sink whose label has no known lower bound"
                                    : $"X-to-X: data up to {L.Name(reach)} may reach a sink with a lower label";
                    break;
                }
            }

            // External-source labels joined onto outputs (S§4.3).
            if (o.DiftOverride is { } forced && d.Mode != forced)
            {
                d.Mode = forced;
                d.Rule = $"forced {forced} (DiftOverride; evaluation)";
            }

            if (d.Mode == DiftMode.Disabled && sources.Count > 0)
            {
                d.SourceJoin = srcStatic;
                var ext = new HashSet<int> { srcStatic };
                foreach (var s in evaluable)
                {
                    ext = ext.SelectMany(x => s.Label.Range!.Select(r => L.Join(x, L.IndexOf(r)))).ToHashSet();
                    d.TrustedLabellers.Add($"{n.Name}: {s.Location}: {s.Label.Text ?? string.Join(" | ", s.Label.Range!)}");
                    d.OutputEvaluable.Add(s);
                }
                d.External = ext;
            }
            else if (d.Mode == DiftMode.Enabled && !declaredDynamic && sources.Count > 0)
            {
                // D_ext, reported by the IDM, is bounded by the declared source labels.
                int bound = L.Join(L.Join(srcStatic, evaluableMax), sources.Any(s => s.Label.Kind is ExternalLabelKind.Unknown or ExternalLabelKind.Dynamic) ? defaultSource ?? L.TopIndex : L.BottomIndex);
                d.External = L.Interval(L.BottomIndex, bound).ToHashSet();
            }

            if (d.Mode == DiftMode.Enabled)
                d.Idm = new IdmConfig(d.LanguageRuntime, sources, sinks);

            // User clarification of S§4.2: only the internal monitor is turned off. The dynamic label D_r
            // still applies to the node's outputs (base = D_r), so declassification through it is kept.
            if (declaredDynamic && d.Class == ComponentClass.IToI && o.DisableInternalMonitorWhenInternal && !exclusive.ContainsKey(n.Name))
            {
                d.InternalMonitorDisabled = true;
                d.Findings.Add(Warn("SPW09", $"the internal monitor of '{n.Name}' is turned off (the node has no external sources); its outputs still carry the dynamic label D_r"));
            }
        }

        // Augmented analysis (S§4.4). New errors fail the spawn (SP010); new warnings are SPW07.
        var augmentation = ctx.Dift.ToDictionary(kv => kv.Key, kv => kv.Value.Augmentation);
        var (baseline, baseDiags) = LabelAnalysis.Run(g, g.Args);
        var (augmented, augDiags) = LabelAnalysis.Run(g, g.Args, augmentation);
        var known = baseDiags.Select(x => (x.Code, x.Message)).ToHashSet();
        var fresh = augDiags.Where(x => x.Code.Length > 0 && !known.Contains((x.Code, x.Message))).ToList();
        var sp010 = fresh.Where(x => x.IsError).Select(x => new SpawnDiagnostic("SP010", Severity.Error,
            $"due to external interfaces: {x.Message}", new[] { $"({x.Code})" }.Concat(x.Notes).ToList())).ToList();
        if (sp010.Count > 0) throw new SchedulingException(sp010.Concat(ctx.Warnings).ToList());
        foreach (var x in fresh.Where(x => !x.IsError))
            ctx.Warn("SPW07", $"due to external interfaces: {x.Message}", new[] { $"({x.Code})" }.Concat(x.Notes).ToArray());
        ctx.Analysis = augmented;

        ChooseLanes(ctx, exclusive.Keys.ToHashSet());
        foreach (var d in ctx.Dift.Values) ctx.Warnings.AddRange(d.Findings);
    }

    // Lanes (S§4.5).
    private static void ChooseLanes(PlanContext ctx, HashSet<string> exclusive)
    {
        var g = ctx.Graph;
        var L = g.Lattice;
        var o = ctx.Options;
        var order = L.TopologicalOrder().Select((x, i) => (x, i)).ToDictionary(t => t.x, t => t.i);
        foreach (var n in g.Nodes)
        {
            var d = ctx.Dift[n.Name];
            if (o.LaneMode == LaneMode.Off || d.Mode != DiftMode.Disabled || exclusive.Contains(n.Name) || n.Inputs.Count == 0) continue;

            var input = n.Inputs.SelectMany(p => ctx.Analysis!.Ports[p.Ref].PossibleLabels).Distinct().Select(L.IndexOf).ToHashSet();
            bool qualifies = o.LaneMode == LaneMode.Always || input.Count > 1;
            if (!qualifies) continue;
            if (n.Partitioning == PartitionKind.Keyed)
            {
                // Keyed nodes keep their per-key state, so they aren't split by label. A keyed node holding
                // several compartments' data (held label = forbidden top) gets one lane per compartment
                // instead (user decision F3): each lane owns every key group for its compartment, so an
                // instance only ever receives one compartment's data.
                var held = L.IndexOf(ctx.Analysis!.Nodes[n.Name].Held);
                if (L.IsForbidden(held)) d.Lanes = CompartmentLanes(g, n, input, d);
                continue;
            }
            if (n.Partitioning != PartitionKind.Keyless)
            {
                d.Findings.Add(Warn("SPW10", $"'{n.Name}' would qualify for label-segregated lanes if it were declared keyless ('{n.Name}[]')"));
                continue;
            }

            // Drop labels the node's declared instance ceiling or C_ext would reject anyway.
            var lanes = input.ToList();
            var ceiling = g.Labels.InstanceCeilings.GetValueOrDefault(n.Name);
            if (ceiling != null)
            {
                int cmax = L.JoinAll(LabelAnalysis.Range(g, g.Args, ceiling, null));
                lanes = lanes.Where(x => L.Leq(x, cmax)).ToList();
            }
            if (d.ExternalSinkCeiling is int cExt) lanes = lanes.Where(x => L.Leq(x, cExt)).ToList();
            if (lanes.Count > o.MaxLanesPerNode)
            {
                lanes = L.Maximal(lanes).ToList();
                d.Findings.Add(Warn("SPW08", $"'{n.Name}' has more possible labels than MaxLanesPerNode ({o.MaxLanesPerNode}); using one lane per maximal label"));
            }
            d.Lanes = lanes.OrderBy(x => order[x]).ThenBy(x => L.Name(x), StringComparer.Ordinal).ToList();
        }
    }

    // One lane per compartment of the node's input labels: λ_c is the join of the compartment's labels
    // that pass the node's instance ceiling and C_ext. Labels outside every compartment (a shared ⊥)
    // fit under every lane.
    private static List<int> CompartmentLanes(CompiledGraph g, GraphNode n, HashSet<int> input, NodeDift d)
    {
        var L = g.Lattice;
        var labels = input.Where(x => !L.IsForbidden(x) && L.Compartments[x] >= 0);
        var ceiling = g.Labels.InstanceCeilings.GetValueOrDefault(n.Name);
        if (ceiling != null)
        {
            int cmax = L.JoinAll(LabelAnalysis.Range(g, g.Args, ceiling, null));
            labels = labels.Where(x => L.Leq(x, cmax));
        }
        if (d.ExternalSinkCeiling is int cExt) labels = labels.Where(x => L.Leq(x, cExt));
        var lanes = labels.GroupBy(x => L.Compartments[x]).OrderBy(c => c.Key).Select(c => L.JoinAll(c)).ToList();
        return lanes.Count > 1 ? lanes : new List<int>();
    }

    private static IEnumerable<string> Labels(ExternalLabel l) =>
        new[] { l.Label, l.LowerBound }.Where(x => x != null).Cast<string>().Concat(l.Range ?? Array.Empty<string>());

    private static SpawnDiagnostic Warn(string code, string message) => new(code, Severity.Warning, message, Array.Empty<string>());

    internal static List<GraphNode> TopologicalOrder(CompiledGraph g)
    {
        var indeg = g.Nodes.ToDictionary(n => n.Name, _ => 0);
        foreach (var e in g.Edges) indeg[e.DestinationNode]++;
        var ready = new SortedSet<int>(g.Nodes.Where(n => indeg[n.Name] == 0).Select(n => n.Index));
        var order = new List<GraphNode>();
        while (ready.Count > 0)
        {
            var n = g.Nodes[ready.Min];
            ready.Remove(ready.Min);
            order.Add(n);
            foreach (var e in g.EdgesOutOf(n.Name))
                if (--indeg[e.DestinationNode] == 0) ready.Add(g.Node(e.DestinationNode).Index);
        }
        return order;
    }
}
