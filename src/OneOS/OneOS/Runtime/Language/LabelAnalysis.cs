using System;
using System.Collections.Generic;
using System.Linq;
using OneOS.Runtime.Language.Ast;
using OneOS.Runtime.Language.Models;

namespace OneOS.Runtime.Language;

// Static label analysis (L§8.6, L§8.7) with possible-label sets (S§0.1).
//
// Two analyses run side by side. The interval analysis follows L§8.6 exactly and drives every
// diagnostic except E0734/W0734 (strict-mode ⊥ outputs, which need the sets). The set analysis
// computes PL(port), the labels a message on each port can carry, for the scheduler.
//
// It operates on the IR, so SpawnGraph can re-run it with bound graph parameters (L§9.1).
//
// The scheduler's DIFT phase re-runs it with an augmentation (S§4.4): external-source labels
// joined into a node's outputs and automatic external-sink ceilings.
// Scheduler additions for one node (S§4.4). `External`: the possible labels of external-source data
// joined onto every output (contains ⊥ when none may reach it). `ExternalSinkCeiling`: C_ext.
public sealed record NodeAugmentation(IReadOnlySet<int> External, int? ExternalSinkCeiling);

public sealed class LabelAnalysis
{
    private readonly CompiledGraph _g;
    private readonly LabelLattice L;
    private readonly IReadOnlyList<object?>? _args;
    private readonly IReadOnlyDictionary<string, NodeAugmentation>? _aug;
    private readonly List<Diagnostic> _diags = new();

    // Unchecked flows (L§8.7).
    private readonly Dictionary<string, List<GraphFlow>> _memberOf = new();     // edge → unchecked flows containing it
    private readonly Dictionary<string, List<GraphFlow>> _exclusiveIn = new();  // node → unchecked flows it is exclusive to
    private readonly Dictionary<string, List<Diagnostic>> _masked = new();      // flow → masked diagnostics

    // Interval results.
    private readonly Dictionary<string, (int Lo, int Up)> _port = new();
    private readonly Dictionary<string, (int Lo, int Up)> _inPort = new();
    private readonly Dictionary<string, (int DelLo, int DelUp, bool Deliverable)> _edge = new();
    private readonly Dictionary<string, (int InLo, int InUp, int Held, int BaseLo, int BaseUp)> _node = new();
    private readonly Dictionary<string, GraphEdge?> _upWitness = new(), _loWitness = new();
    // Output ports and nodes whose forbidden-top bound was already reported upstream.
    private readonly HashSet<string> _topReported = new();

    // Set results.
    private readonly Dictionary<string, HashSet<int>> _pl = new();
    private readonly Dictionary<string, HashSet<int>> _inputSet = new(), _baseSet = new();

    private LabelAnalysis(CompiledGraph graph, IReadOnlyList<object?>? args, IReadOnlyDictionary<string, NodeAugmentation>? aug)
    {
        _g = graph;
        L = graph.Lattice;
        _args = args;
        _aug = aug;
    }

    public static (LabelAnalysisInfo Info, IReadOnlyList<Diagnostic> Diagnostics) Run(CompiledGraph graph, IReadOnlyList<object?>? args,
        IReadOnlyDictionary<string, NodeAugmentation>? augmentation = null)
    {
        var a = new LabelAnalysis(graph, args, augmentation);
        var info = a.Analyze();
        return (info, a._diags);
    }

    private LabelAnalysisInfo Analyze()
    {
        foreach (var f in _g.Flows.Where(f => f.UncheckedReason != null))
        {
            _masked[f.Name] = new List<Diagnostic>();
            foreach (var e in f.Edges) Add(_memberOf, e, f);
            foreach (var n in f.ExclusiveNodes) Add(_exclusiveIn, n, f);
        }

        var order = TopologicalOrder();
        foreach (var n in order) AnalyzeNode(n);

        var placement = order.OrderBy(n => n.Index).Select(Placement).ToList();
        var declass = order.SelectMany(Declassifications).ToList();

        var ports = new Dictionary<string, PortLabelInfo>();
        foreach (var n in _g.Nodes)
        {
            foreach (var p in n.Outputs)
                if (_port.TryGetValue(p.Ref, out var b)) ports[p.Ref] = new PortLabelInfo(L.Name(b.Lo), L.Name(b.Up), Names(_pl[p.Ref]));
            foreach (var p in n.Inputs)
                if (_inPort.TryGetValue(p.Ref, out var b)) ports[p.Ref] = new PortLabelInfo(L.Name(b.Lo), L.Name(b.Up), Names(_pl.GetValueOrDefault(p.Ref) ?? new()));
        }
        var nodes = _node.ToDictionary(kv => kv.Key, kv => new NodeLabelInfo(
            L.Name(kv.Value.InLo), L.Name(kv.Value.InUp), L.Name(kv.Value.Held), Names(_inputSet[kv.Key]), Names(_baseSet[kv.Key])));
        var uncheckedReports = _g.Flows.Where(f => f.UncheckedReason != null).Select(f =>
        {
            var report = new UncheckedFlowReport(f.Name, f.UncheckedReason!, f.ExclusiveNodes, f.NonExclusiveNodes, _masked[f.Name]);
            var notes = new List<string>
            {
                $"exclusive nodes: {(f.ExclusiveNodes.Count > 0 ? string.Join(", ", f.ExclusiveNodes) : "none")}",
            };
            foreach (var m in report.Masked) notes.Add($"masked {m.Code}: {m.Message}" + string.Concat(m.Notes.Select(x => "\n    " + x)));
            _diags.Add(new Diagnostic("", Severity.Note, $"unchecked flow '{f.Name}': {f.UncheckedReason}", f.Span, notes));
            return report;
        }).ToList();

        return new LabelAnalysisInfo(ports, nodes, placement, declass, uncheckedReports);
    }

    private List<GraphNode> TopologicalOrder()
    {
        var indeg = _g.Nodes.ToDictionary(n => n.Name, _ => 0);
        foreach (var e in _g.Edges) indeg[e.DestinationNode]++;
        var ready = new SortedSet<int>(_g.Nodes.Where(n => indeg[n.Name] == 0).Select(n => n.Index));
        var order = new List<GraphNode>();
        while (ready.Count > 0)
        {
            var n = _g.Nodes[ready.Min];
            ready.Remove(ready.Min);
            order.Add(n);
            foreach (var e in _g.EdgesOutOf(n.Name))
                if (--indeg[e.DestinationNode] == 0) ready.Add(_g.Node(e.DestinationNode).Index);
        }
        return order;
    }

    // --- Step 2 and 3: bounds and checks, per node ---

    private void AnalyzeNode(GraphNode n)
    {
        bool exclusive = _exclusiveIn.ContainsKey(n.Name);
        var aug = _aug?.GetValueOrDefault(n.Name);
        var (pMax, pMin) = InstanceCeilingBounds(n);
        // An automatic external-sink ceiling (C_ext) is an extra instance ceiling; unchecked-exclusive nodes don't get one.
        if (aug?.ExternalSinkCeiling is int cExt && !exclusive)
        {
            pMax = L.Meet(pMax, cExt);
            pMin = L.Meet(pMin, cExt);
        }

        var delivered = new List<GraphEdge>();
        var inputSet = new HashSet<int>();
        foreach (var e in _g.EdgesInto(n.Name).OrderBy(e => e.Index))
        {
            var src = _port[e.Source];
            bool member = _memberOf.ContainsKey(e.Name);
            var (cMax, cMin) = PortCeilingBounds(e.Destination);
            var sink = ((Func<Diagnostic, bool>)(d => Report(d, member ? _memberOf[e.Name] : null)));
            bool deliverable = true;

            // An unsegmentable stream carries one label, so no check can drop only part of it (L§5.2).
            bool raw = Unsegmentable(e);
            if (raw && !member && _g.Labels.Dynamic.ContainsKey(e.SourceNode))
                sink(Diag("E0735", Severity.Error, $"edge '{e.Name}' carries an unsegmentable stream from '{e.SourceNode}', whose labels are supplied at runtime ('dynamic'); a stream has one label", e,
                    new[] { "use a segmentable format for the port, or a constant label on the sender" }));

            if (!L.Leq(src.Lo, cMax))
            {
                deliverable = member;
                sink(Diag("E0730", Severity.Error, $"edge '{e.Name}' can never pass the ceiling of {e.Destination}: {L.Name(src.Lo)} ⋢ {L.Name(cMax)}", e,
                    Witness(e, lo: true, $" [ceiling {CeilingText(e.Destination)}]")));
            }
            else if (!L.Leq(src.Up, cMin) && raw)
                sink(Diag("E0735", Severity.Error, $"edge '{e.Name}' carries an unsegmentable stream that would need a per-message check at {e.Destination}: {L.Name(src.Up)} ⋢ {L.Name(cMin)}", e,
                    Witness(e, lo: false, $" [ceiling {CeilingText(e.Destination)}]")));
            else if (!L.Leq(src.Up, cMin))
                sink(Diag("W0730", Severity.Warning, $"some messages may be dropped at {e.Destination}: {L.Name(src.Up)} ⋢ {L.Name(cMin)}", e,
                    Witness(e, lo: false, $" [ceiling {CeilingText(e.Destination)}]")));

            if (!L.Leq(src.Lo, pMax))
            {
                deliverable = member;
                sink(Diag("E0731", Severity.Error, $"edge '{e.Name}' can never pass the instance ceiling of '{n.Name}': {L.Name(src.Lo)} ⋢ {L.Name(pMax)}", e,
                    Witness(e, lo: true, $" [instance ceiling {L.Name(pMax)}]")));
            }
            else if (!L.Leq(src.Up, pMin) && raw)
                sink(Diag("E0735", Severity.Error, $"edge '{e.Name}' carries an unsegmentable stream that would need a per-message check at the instance ceiling of '{n.Name}': {L.Name(src.Up)} ⋢ {L.Name(pMin)}", e,
                    Witness(e, lo: false, $" [instance ceiling {L.Name(pMin)}]")));
            else if (!L.Leq(src.Up, pMin))
                sink(Diag("W0731", Severity.Warning, $"some messages may be dropped at the instance ceiling of '{n.Name}': {L.Name(src.Up)} ⋢ {L.Name(pMin)}", e,
                    Witness(e, lo: false, $" [instance ceiling {L.Name(pMin)}]")));

            int delUp = member ? src.Up : L.Meet(L.Meet(src.Up, cMax), pMax);
            _edge[e.Name] = (src.Lo, delUp, deliverable);
            var dset = member ? _pl[e.Source] : _pl[e.Source].Where(x => L.Leq(x, L.Meet(cMax, pMax))).ToHashSet();
            if (!_pl.TryGetValue(e.Destination, out var inPl)) _pl[e.Destination] = inPl = new HashSet<int>();
            inPl.UnionWith(dset);
            inputSet.UnionWith(dset);
            if (deliverable) delivered.Add(e);
        }

        int inLo = delivered.Count == 0 ? L.BottomIndex : L.MeetAll(delivered.Select(e => _edge[e.Name].DelLo));
        int inUp = delivered.Count == 0 ? L.BottomIndex : L.JoinAll(delivered.Select(e => _edge[e.Name].DelUp));
        _upWitness[n.Name] = delivered.FirstOrDefault(e => _edge[e.Name].DelUp == inUp) ?? delivered.FirstOrDefault();
        _loWitness[n.Name] = delivered.FirstOrDefault(e => _edge[e.Name].DelLo == inLo) ?? delivered.FirstOrDefault();
        foreach (var p in n.Inputs)
        {
            var into = delivered.Where(e => e.DestinationPort == p.Name).ToList();
            _inPort[p.Ref] = into.Count == 0 ? (L.BottomIndex, L.BottomIndex)
                : (L.MeetAll(into.Select(e => _edge[e.Name].DelLo)), L.JoinAll(into.Select(e => _edge[e.Name].DelUp)));
        }

        var dyn = _g.Labels.Dynamic.GetValueOrDefault(n.Name);
        var host = _g.Labels.Host.GetValueOrDefault(n.Name);
        int held = dyn != null ? L.Join(inUp, L.IndexOf(dyn.Hi)) : inUp;

        int baseLo = inLo, baseUp = inUp;
        if (dyn != null)
        {
            int lo = L.IndexOf(dyn.Lo), hi = L.IndexOf(dyn.Hi);
            (baseLo, baseUp) = exclusive ? (hi, hi) : (lo, hi);
            var sinkN = (Func<Diagnostic, bool>)(d => Report(d, exclusive ? _exclusiveIn[n.Name] : null));
            if (!L.Leq(lo, pMax))
                sinkN(Diag("E0731", Severity.Error, $"dynamic range {dyn.Lo}..{dyn.Hi} of '{n.Name}' can never pass its instance ceiling: {dyn.Lo} ⋢ {L.Name(pMax)}", n.Span));
            else if (!L.Leq(hi, pMin))
                sinkN(Diag("W0731", Severity.Warning, $"dynamic labels of '{n.Name}' may exceed its instance ceiling: {dyn.Hi} ⋢ {L.Name(pMin)}", n.Span));
        }
        if (host != null)
        {
            baseLo = L.Join(baseLo, L.IndexOf(host.Lo));
            baseUp = L.Join(baseUp, L.IndexOf(host.Hi));
        }
        if (aug is { External.Count: > 0 })
        {
            baseLo = L.Join(baseLo, L.MeetAll(aug.External));
            baseUp = L.Join(baseUp, L.JoinAll(aug.External));
            held = L.Join(held, L.JoinAll(aug.External));
        }
        _node[n.Name] = (inLo, inUp, held, baseLo, baseUp);

        // Base set (S§0.1).
        _inputSet[n.Name] = inputSet;
        var baseSet = BaseSet(_g, n, inputSet, aug, exclusive);
        _baseSet[n.Name] = baseSet;

        // Output ports.
        var baseInterval = L.Interval(baseLo, baseUp).ToList();
        void Output(GraphPort p, List<int> interval, HashSet<int> set)
        {
            var labeller = _g.Labels.DataLabellers.GetValueOrDefault(p.Ref);
            HashSet<int> range = labeller == null ? interval.ToHashSet() : interval.SelectMany(x => Range(labeller, x)).ToHashSet();
            if (range.Count == 0) range.Add(interval[0]);
            _port[p.Ref] = (L.MeetAll(range), L.JoinAll(range));

            _pl[p.Ref] = OutputSetFromBase(_g, _args, p, set);
            if (L.Strict && (n.IsSource || inputSet.Count > 0)) CheckStrictBottom(p, labeller, set);

            if (labeller != null) CheckLabellerBranches(p, labeller, interval);
        }
        foreach (var p in n.DataOutputs) Output(p, baseInterval, baseSet);

        // Deviation from L§8.6 (user decision, see plan.md): a source whose output port has a label
        // attachment holds the data it produces, so its held label (and placement) includes it.
        if (n.IsSource)
        {
            foreach (var p in n.DataOutputs.Where(p => _g.Labels.DataLabellers.ContainsKey(p.Ref)))
                held = L.JoinTable[held][_port[p.Ref].Up] is var j && j >= 0 ? j : L.TopIndex;
            _node[n.Name] = (inLo, inUp, held, baseLo, baseUp);
        }

        // stderr (L§6.1) can leak whatever the process holds and answers no particular input: it carries the
        // taint (not the per-message labels of a @one_to_one node), and a source's held label.
        foreach (var p in n.Outputs.Where(p => p.IsStderr))
        {
            if (n.IsSource) Output(p, new List<int> { held }, new HashSet<int> { held });
            else if (n.OneToOne) Output(p, baseInterval, BaseSet(_g, n, inputSet, aug, exclusive, perMessage: false));
            else Output(p, baseInterval, baseSet);
        }

        CheckCompartments(n, delivered, exclusive);
    }

    // Strict mode forbids emitting ⊥ (L§8.1), which with one compartment is the declared minimum.
    // E0734: every message on the port would be ⊥, so none is ever emitted; W0734: some may be.
    // Uses the label sets: the interval's lower bound is ⊥ whenever a port spans compartments.
    // Copies on unchecked member edges skip the emission check (L§8.7), so ports whose outgoing
    // edges are all unchecked members aren't reported.
    private void CheckStrictBottom(GraphPort p, LabelSpec? labeller, HashSet<int> baseSet)
    {
        var produced = labeller == null ? baseSet : baseSet.SelectMany(x => Range(labeller, x)).ToHashSet();
        produced.RemoveWhere(L.IsForbidden);
        if (!produced.Contains(L.BottomIndex)) return;
        var outgoing = _g.Edges.Where(e => e.Source == p.Ref).ToList();
        if (outgoing.Count > 0 && outgoing.All(e => _memberOf.ContainsKey(e.Name))) return;
        var bottom = L.Name(L.BottomIndex);
        var why = labeller == null ? "its node's inputs" : labeller.Kind == LabelSpecKind.Constant ? $"label({labeller.Label})" : $"its labeller {labeller}";
        if (produced.Count == 1)
            _diags.Add(Diag("E0734", Severity.Error,
                $"output port '{p.Ref}' can only carry {bottom}, which strict mode never emits: no message on it is delivered", p.Span,
                $"label from {why}", $"in a strict lattice with one compartment, the least declared label ({bottom}) is ⊥; declare a label below it"));
        else
            _diags.Add(Diag("W0734", Severity.Warning,
                $"output port '{p.Ref}' may carry {bottom}, which strict mode never emits: those messages are dropped", p.Span,
                $"label from {why}"));
    }

    // The base set of a node (S§0.1): what its taint (or per-message label) can be, given the labels it
    // receives, joined with host and external-source labels.
    public static HashSet<int> BaseSet(CompiledGraph g, GraphNode n, IEnumerable<int> inputs, NodeAugmentation? aug, bool uncheckedExclusive, bool perMessage = true)
    {
        var L = g.Lattice;
        var inputSet = inputs.ToHashSet();
        var dyn = g.Labels.Dynamic.GetValueOrDefault(n.Name);
        var host = g.Labels.Host.GetValueOrDefault(n.Name);
        HashSet<int> baseSet;
        if (dyn != null)
            baseSet = uncheckedExclusive ? new HashSet<int> { L.IndexOf(dyn.Hi) } : L.Interval(L.IndexOf(dyn.Lo), L.IndexOf(dyn.Hi)).ToHashSet();
        else if (inputSet.Count == 0) baseSet = new HashSet<int> { L.BottomIndex };
        else if (n.OneToOne && perMessage) baseSet = new HashSet<int>(inputSet);
        else baseSet = JoinClosure(L, inputSet);
        if (host != null)
        {
            var hs = L.Interval(L.IndexOf(host.Lo), L.IndexOf(host.Hi)).ToList();
            baseSet = baseSet.SelectMany(b => hs.Select(h => L.Join(b, h))).ToHashSet();
        }
        if (aug is { External.Count: > 0 })
            baseSet = baseSet.SelectMany(b => aug.External.Select(e => L.Join(b, e))).ToHashSet();
        baseSet.RemoveWhere(L.IsForbidden);
        return baseSet;
    }

    // PL of an output port given its node's base set: through the port's labeller, without the
    // forbidden top (never emitted) and, in strict mode, without ⊥ (never emitted).
    public static HashSet<int> OutputSetFromBase(CompiledGraph g, IReadOnlyList<object?>? args, GraphPort port, IEnumerable<int> baseSet)
    {
        var L = g.Lattice;
        var labeller = g.Labels.DataLabellers.GetValueOrDefault(port.Ref);
        var pl = labeller == null ? baseSet.ToHashSet() : baseSet.SelectMany(x => Range(g, args, labeller, x)).ToHashSet();
        pl.RemoveWhere(L.IsForbidden);
        if (L.Strict) pl.Remove(L.BottomIndex);
        return pl;
    }

    private HashSet<int> JoinClosure(HashSet<int> xs) => JoinClosure(L, xs);

    private static HashSet<int> JoinClosure(LabelLattice L, HashSet<int> xs)
    {
        var closure = new HashSet<int>(xs);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var a in closure.ToList())
                foreach (var b in closure.ToList())
                    if (closure.Add(L.Join(a, b))) changed = true;
        }
        return closure;
    }

    // L§8.6 step 3, compartments. Each cause is reported once, at the first node in topological order.
    private void CheckCompartments(GraphNode n, List<GraphEdge> delivered, bool exclusive)
    {
        if (!L.ForbiddenTop) return;
        int top = L.TopIndex;
        var nb = _node[n.Name];
        var sink = (Func<Diagnostic, bool>)(d => Report(d, exclusive ? _exclusiveIn[n.Name] : null));
        bool inheritedLo = delivered.Any(e => _edge[e.Name].DelLo == top);
        bool inheritedUp = delivered.Any(e => _edge[e.Name].DelUp == top && _topReported.Contains(e.Source));

        // E0733: certain mixing.
        string? certain = null;
        List<GraphEdge> witnesses = new();
        if (!inheritedLo)
        {
            if (!n.IsPartitioned)
            {
                for (int i = 0; i < delivered.Count && certain == null; i++)
                    for (int j = i + 1; j < delivered.Count && certain == null; j++)
                    {
                        int a = _edge[delivered[i].Name].DelLo, b = _edge[delivered[j].Name].DelLo;
                        if (L.JoinTable[a][b] == top)
                        {
                            certain = $"certain cross-compartment mixing at {delivered[i].Destination}: {L.Name(a)} ⊔ {L.Name(b)} = top";
                            witnesses = new() { delivered[i], delivered[j] };
                        }
                    }
            }
            if (certain == null)
            {
                var p = n.Outputs.FirstOrDefault(p => _port[p.Ref].Lo == top);
                if (p != null)
                {
                    certain = $"certain cross-compartment mixing at {p.Ref}: every message would be labelled top";
                    witnesses = OnePerCompartment(delivered, lo: true);
                }
            }
        }
        if (certain != null)
        {
            sink(Diag("E0733", Severity.Error, certain, n.Span, witnesses.Select((e, i) => $"path {i + 1}: {WitnessPath(e, lo: true, "")}").ToArray()));
            foreach (var p in n.Outputs) _topReported.Add(p.Ref);
            return;
        }

        // W0733: possible mixing, relying on runtime isolation.
        string? where = nb.InUp == top ? $"the inputs of '{n.Name}'" : nb.Held == top ? $"the held label of '{n.Name}'"
            : n.Outputs.FirstOrDefault(p => _port[p.Ref].Up == top) is { } op ? op.Ref : null;
        if (where == null) return;
        // Runtime isolation checks messages one by one, which an unsegmentable stream in or out can't have,
        // so this is an error even when the mixing was already reported upstream.
        var rawIn = delivered.FirstOrDefault(Unsegmentable);
        var rawOut = n.Outputs.FirstOrDefault(p => !p.Framing.Segmentable);
        if (rawIn != null || rawOut != null)
        {
            sink(Diag("E0735", Severity.Error, $"possible cross-compartment mixing at {where}, with an unsegmentable stream "
                + (rawIn != null ? $"on edge '{rawIn.Name}'" : $"on '{rawOut!.Ref}'") + ", which runtime isolation can't check message by message", n.Span,
                OnePerCompartment(delivered, lo: false).Select((e, i) => $"path {i + 1}: {WitnessPath(e, lo: false, "")}").ToArray()));
            foreach (var p in n.Outputs.Where(p => _port[p.Ref].Up == top)) _topReported.Add(p.Ref);
            return;
        }
        if (inheritedUp) { foreach (var p in n.Outputs.Where(p => _port[p.Ref].Up == top)) _topReported.Add(p.Ref); return; }
        var paths = OnePerCompartment(delivered, lo: false);
        sink(Diag("W0733", Severity.Warning, $"possible cross-compartment mixing at {where}: labels may join to top; relies on runtime isolation", n.Span,
            paths.Select((e, i) => $"path {i + 1}: {WitnessPath(e, lo: false, "")}").ToArray()));
        foreach (var p in n.Outputs.Where(p => _port[p.Ref].Up == top)) _topReported.Add(p.Ref);
    }

    private List<GraphEdge> OnePerCompartment(List<GraphEdge> edges, bool lo)
    {
        var seen = new HashSet<int>();
        var result = new List<GraphEdge>();
        foreach (var e in edges)
        {
            var b = _edge[e.Name];
            int label = lo ? b.DelLo : b.DelUp;
            int c = L.Compartments[label];
            if (c >= 0 && seen.Add(c)) result.Add(e);
        }
        return result;
    }

    // --- Ceilings ---

    private (int Max, int Min) PortCeilingBounds(string portRef)
    {
        var spec = _g.Labels.PortCeilings.GetValueOrDefault(portRef);
        if (spec == null) return (L.TopIndex, L.TopIndex);
        var r = Range(spec, null);
        return (L.JoinAll(r), L.MeetAll(r));
    }

    private (int Max, int Min) InstanceCeilingBounds(GraphNode n)
    {
        var ranges = new List<IReadOnlySet<int>>();
        var def = _g.Labels.InstanceCeilings.GetValueOrDefault(n.Name);
        ranges.Add(def != null ? Range(def, null) : new HashSet<int> { L.TopIndex });
        if (_g.Labels.InstanceCeilingOverrides.TryGetValue(n.Name, out var ov))
            foreach (var spec in ov.Values) ranges.Add(Range(spec, null));
        var all = ranges.SelectMany(r => r).ToList();
        return (L.JoinAll(all), L.MeetAll(all));
    }

    private string CeilingText(string portRef)
    {
        var spec = _g.Labels.PortCeilings.GetValueOrDefault(portRef);
        if (spec == null) return L.Top;
        var r = Range(spec, null);
        return r.Count == 1 ? L.Name(r.First()) : $"{L.Name(L.MeetAll(r))}..{L.Name(L.JoinAll(r))}";
    }

    // --- Range: abstract evaluation of a label spec (L§8.6 step 2) ---

    private IReadOnlySet<int> Range(LabelSpec spec, int? inLabel) => Range(_g, _args, spec, inLabel);

    private AbstractEnv Env(LabelSpec spec, int? inLabel) => Env(_g, _args, spec, inLabel);

    // Range(spec, in = inLabel): the labels a label spec can produce (L§8.6 step 2), with the graph's
    // parameters bound to `args` (unknown when null). Message and replica parameters are unknown.
    public static IReadOnlySet<int> Range(CompiledGraph graph, IReadOnlyList<object?>? args, LabelSpec spec, int? inLabel)
    {
        var lattice = graph.Lattice;
        if (spec.Kind == LabelSpecKind.Constant) return new HashSet<int> { lattice.IndexOf(spec.Label!) };
        return AbstractEval.LabelsOf(AbstractEval.Eval(spec.Body!, Env(graph, args, spec, inLabel)), lattice);
    }

    private static AbstractEnv Env(CompiledGraph graph, IReadOnlyList<object?>? args, LabelSpec spec, int? inLabel)
    {
        var env = new AbstractEnv { Lattice = graph.Lattice };
        // Only inline lambdas see graph parameters; top-level labellers don't (L§4.4).
        if (spec.LabellerName == null)
            for (int i = 0; i < graph.Parameters.Count; i++)
                env.Values[graph.Parameters[i].Name] = args != null && i < args.Count && args[i] != null ? new KnownVal(args[i]!) : UnknownVal.Instance;
        var ps = spec.Parameters ?? Array.Empty<LabellerParamInfo>();
        if (ps.Count > 0) env.Values[ps[0].Name] = UnknownVal.Instance;
        if (ps.Count > 1) env.Values[ps[1].Name] = inLabel is int x ? new KnownVal(new LabelValue(x)) : UnknownVal.Instance;
        return env;
    }

    // W0710 (a branch that always equals the base label) and W0711 (a condition that doesn't fold at spawn).
    private void CheckLabellerBranches(GraphPort port, LabelSpec spec, List<int> baseInterval)
    {
        if (spec.Kind != LabelSpecKind.Labeller || spec.Body == null) return;
        var ps = spec.Parameters!;
        var leaves = new List<Expression>();
        var conditions = new List<Expression>();
        void Collect(Expression e)
        {
            if (e is TernaryExpr t) { conditions.Add(t.Condition); Collect(t.Then); Collect(t.Else); }
            else leaves.Add(e);
        }
        Collect(spec.Body);
        if (conditions.Count == 0) return;

        if (ps.Count == 2 && baseInterval.Count > 0)
        {
            foreach (var leaf in leaves)
            {
                if (leaf is IdentExpr id && id.Name == ps[1].Name) continue;
                if (baseInterval.All(x => Range(spec with { Body = leaf }, x) is var r && r.Count == 1 && r.Contains(x)))
                    _diags.Add(new Diagnostic("W0710", Severity.Warning,
                        $"labeller branch '{AppCompiler.RenderExpr(leaf)}' on {port.Ref} always equals the base label, so it has no effect", leaf.Span, Array.Empty<string>()));
            }
        }
        if (_args != null && baseInterval.Count > 0)
        {
            foreach (var c in conditions)
            {
                if (ps.Count > 0 && ExprChecker.Mentions(c, ps[0].Name)) continue;
                if (AbstractEval.Eval(c, Env(spec, baseInterval[0])) is not KnownVal)
                    _diags.Add(new Diagnostic("W0711", Severity.Warning,
                        $"condition '{AppCompiler.RenderExpr(c)}' on {port.Ref} does not constant-fold", c.Span, Array.Empty<string>()));
            }
        }
    }

    // --- Step 4: placement constraints ---

    private PlacementConstraint Placement(GraphNode n)
    {
        if (_exclusiveIn.ContainsKey(n.Name))
            return new PlacementConstraint(n.Name, null, $"exclusive node of unchecked flow '{_exclusiveIn[n.Name][0].Name}'");
        int held = _node[n.Name].Held;
        if (L.IsForbidden(held))
            return new PlacementConstraint(n.Name, null, "held label is the forbidden top; the compartment is fixed at runtime");
        return new PlacementConstraint(n.Name, L.Name(held), $"L(host) ⊒ Held({n.Name})");
    }

    // --- Step 5: declassification sites ---

    private IEnumerable<DeclassificationSite> Declassifications(GraphNode n)
    {
        var nb = _node[n.Name];
        var baseInterval = L.Interval(nb.BaseLo, nb.BaseUp).ToList();
        var process = RenderProcess(n);
        if (!n.IsSource)
        {
            foreach (var p in n.Outputs)
            {
                var spec = _g.Labels.DataLabellers.GetValueOrDefault(p.Ref);
                // A constant label is a labeller that ignores its arguments (L§8.4), so it is a site too.
                if (spec == null) continue;
                var lower = new SortedSet<int>();
                foreach (var x in baseInterval)
                    foreach (var r in Range(spec, x))
                        if (!L.Leq(x, r)) lower.Add(r);
                if (lower.Count == 0) continue;

                var fields = spec.Kind == LabelSpecKind.Labeller && spec.Parameters!.Count > 0
                    ? ExprChecker.FieldsRead(spec.Body!, spec.Parameters[0].Name) : Array.Empty<string>();
                var text = spec.Kind == LabelSpecKind.Constant ? $"label({spec.Label}) (constant)" : LabellerText(spec);
                var site = new DeclassificationSite(p.Ref, L.Name(nb.BaseLo), L.Name(nb.BaseUp), Names(lower), text, fields, n.Name, process, false);
                _diags.Add(new Diagnostic("", Severity.Note,
                    $"declassification at {p.Ref}: {Interval(nb.BaseLo, nb.BaseUp)} → {{{string.Join(", ", site.LowerResults)}}}", p.Span, new[]
                    {
                        $"labeller: {text}",
                        $"reads fields: {(fields.Count > 0 ? string.Join(", ", fields) : "none")}",
                        $"trusted component: {n.Name} ({process})",
                    }));
                yield return site;
            }
        }

        var dyn = _g.Labels.Dynamic.GetValueOrDefault(n.Name);
        if (dyn != null && !_exclusiveIn.ContainsKey(n.Name) && !L.Leq(nb.InUp, L.IndexOf(dyn.Lo)))
        {
            var site = new DeclassificationSite(n.Name, L.Name(nb.InLo), L.Name(nb.InUp), new[] { dyn.Lo }, $"dynamic: {dyn.Lo}..{dyn.Hi}",
                Array.Empty<string>(), "middleware monitor", process, true);
            _diags.Add(new Diagnostic("", Severity.Note,
                $"potential declassification at {n.Name}: inputs up to {L.Name(nb.InUp)}, dynamic label may be as low as {dyn.Lo}", n.Span,
                new[] { "trusted component: the middleware label monitor" }));
            yield return site;
        }
    }

    private static string LabellerText(LabelSpec spec)
    {
        var text = (spec.SourceText ?? "").Trim();
        if (text.StartsWith("labeller ", StringComparison.Ordinal)) text = text["labeller ".Length..].TrimStart();
        return text.TrimEnd(';').TrimEnd();
    }

    private static string RenderProcess(GraphNode n) =>
        "process " + string.Join(" ", (n.Process.Argv ?? new[] { n.Process.Command }.Concat(n.Process.Args).Select(AppCompiler.RenderExpr).ToList())
            .Select(a => a.StartsWith('\'') ? a : $"'{a}'"));

    // --- Witness paths (L§8.6) ---

    private string[] Witness(GraphEdge e, bool lo, string suffix) => new[] { "path: " + WitnessPath(e, lo, suffix) };

    private string WitnessPath(GraphEdge last, bool lo, string suffix)
    {
        var segments = new List<string>();
        var e = last;
        var seen = new HashSet<string>();
        while (e != null && seen.Add(e.Name))
        {
            var b = _port[e.Source];
            segments.Insert(0, $"{e.Source} {Interval(b.Lo, b.Up)} -{e.Name}-> {e.Destination}");
            e = (lo ? _loWitness : _upWitness).GetValueOrDefault(e.SourceNode);
        }
        return string.Join("; ", segments) + suffix;
    }

    private string Interval(int lo, int up) => lo == up ? $"[{L.Name(lo)}]" : $"[{L.Name(lo)}..{L.Name(up)}]";

    // --- Reporting ---

    // An edge whose sender port has framing `none` (L§5.2).
    private bool Unsegmentable(GraphEdge e) =>
        _g.Node(e.SourceNode).Outputs.FirstOrDefault(p => p.Name == e.SourcePort) is { Framing.Segmentable: false };

    private Diagnostic Diag(string code, Severity sev, string message, GraphEdge e, string[] notes) => new(code, sev, message, e.Span, notes);
    private Diagnostic Diag(string code, Severity sev, string message, SourceSpan span, params string[] notes) => new(code, sev, message, span, notes);

    // Diagnostics masked by an unchecked flow go to that flow's report instead (L§8.7).
    private bool Report(Diagnostic d, List<GraphFlow>? maskingFlows)
    {
        if (maskingFlows == null || maskingFlows.Count == 0) { _diags.Add(d); return true; }
        foreach (var f in maskingFlows) _masked[f.Name].Add(d);
        return false;
    }

    private IReadOnlyList<string> Names(IEnumerable<int> xs) => xs.OrderBy(x => x).Select(L.Name).ToList();

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<T>();
        list.Add(value);
    }
}
