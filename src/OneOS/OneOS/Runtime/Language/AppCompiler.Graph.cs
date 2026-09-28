using System;
using System.Collections.Generic;
using System.Linq;
using OneOS.Runtime.Language.Ast;
using OneOS.Runtime.Language.Models;

namespace OneOS.Runtime.Language;

public sealed partial class AppCompiler
{
    // --- Per-graph working state ---

    private sealed class GraphCtx
    {
        public required GraphDecl Decl;
        public DiagnosticBag Diags = new();
        public Dictionary<string, PrimType> Params = new();
        public List<NodeCtx> Nodes = new();
        public Dictionary<string, NodeCtx> NodeByName = new();
        public List<EdgeCtx> Edges = new();
        public Dictionary<string, EdgeCtx> NamedEdges = new();
        public Dictionary<string, FlowDecl> Flows = new();
        public List<FlowCtx> FlowResults = new();
        public List<NormalizedPolicy> Policies = new();
        public HashSet<string> SeenPolicyTargets = new();
        public List<AlwaysPolicy> Always = new();
        public List<PinPolicy> Pins = new();
        public List<RatePolicy> MinRates = new();
        public List<(LatencyPolicy Policy, SourceSpan Span)> MaxLatencies = new();
        public Dictionary<string, (string Reason, SourceSpan Span)> Unchecked = new();
        public List<(PortCtx Port, string RoutePort, SourceSpan Span)> RouteTargets = new();
        public Labels Labels = new();
        public HashSet<string> CycleNodes = new();
        public List<NodeCtx> TopoOrder = new();
        public CompiledGraph? Graph;
    }

    private sealed class NodeCtx
    {
        public required NodeDecl Decl;
        public required int Index;
        public string Name => Decl.Name;
        public bool OneToOne;
        public PartitionKind Kind;
        public List<KeyDomainType> Keys = new();
        public int KeyGroups = DefaultKeyGroups;
        public int MinInstances = 1, MaxInstances = 1;
        public List<PortCtx> Inputs = new(), Outputs = new();
        public Dictionary<string, PortCtx> Ports = new();
    }

    private sealed class PortCtx
    {
        public required NodeCtx Node;
        public required PortDecl Decl;
        public required PortDirection Direction;
        public DslType Type = ErrorType.Instance;
        public string Name => Decl.Name;
        public string Ref => $"{Node.Name}.{Decl.Name}";
        public bool IsOrdered => Decl.Order is OrderedSpec;
        public bool IsSequenced => Decl.Order is SequencedSpec;
        // The process's error stream (L§6.1): the implicit `stderr` output, or a declared output of that name.
        public bool IsStderr => Direction == PortDirection.Out && Name == StderrPort;
        public bool Implicit;
        public IReadOnlyList<string>? ClockField;
        public ClockDomainType? Clock;
        public long WithinNanos;
        // Merge configuration: index -1 is the port-level default, i ≥ 0 an `n[i].p` override.
        public Dictionary<int, OrderingPolicy> Ordering = new();
        public SequenceInfo? Sequence;
        public bool SequenceOrigin, SequencePropagating;
    }

    private sealed class OrderingPolicy
    {
        public long? Lateness, IdleTimeout, GapTimeout, MaxBuffer;
        public OnLateMode? OnLate;
        public string? RoutePort;
        public bool LatenessFromPolicy;

        public OrderingPolicy MergedOver(OrderingPolicy d) => new()
        {
            Lateness = Lateness ?? d.Lateness, LatenessFromPolicy = Lateness != null || d.LatenessFromPolicy,
            IdleTimeout = IdleTimeout ?? d.IdleTimeout, GapTimeout = GapTimeout ?? d.GapTimeout, MaxBuffer = MaxBuffer ?? d.MaxBuffer,
            OnLate = OnLate ?? d.OnLate, RoutePort = OnLate != null ? RoutePort : d.RoutePort,
        };
    }

    private sealed class EdgeCtx
    {
        public required string Name;
        public required bool Named;
        public required int Index;
        public bool Implicit;
        public string? ImplicitOf;
        public EdgeOp Op;
        public required PortCtx Source, Destination;
        public IReadOnlyList<IReadOnlyList<string>>? ByPaths;
        public IReadOnlyList<IReadOnlyList<string>>? KeyPaths;
        public bool ForwardEligible;
        public required SourceSpan Span;
    }

    private sealed class FlowCtx
    {
        public required FlowDecl Decl;
        public List<EdgeCtx> Members = new();
        public List<string> Entry = new(), Exit = new(), Interior = new(), Exclusive = new(), NonExclusive = new();
        public List<List<EdgeCtx>> Paths = new();
        public LatencyMode Mode = LatencyMode.Correlated;
        public IReadOnlyList<string>? ClockField;
        public LatencyPolicy? Latency;
        public string? UncheckedReason;
    }

    private sealed class Labels
    {
        public Dictionary<string, LabelSpec> Data = new(), PortCeilings = new(), InstanceCeilings = new();
        public Dictionary<string, Dictionary<int, LabelSpec>> InstanceOverrides = new();
        public Dictionary<string, LabelRange> Dynamic = new(), Host = new();
    }

    // --- Phases 2–7 for one graph ---

    private GraphCtx CompileGraphStructure(GraphDecl decl)
    {
        var g = new GraphCtx { Decl = decl };
        CollectGraphScope(g);
        foreach (var n in g.Nodes) CheckNode(g, n);
        foreach (var e in decl.Edges) ResolveDeclaredEdge(g, e);
        foreach (var stmt in decl.Policies.OfType<GenericPolicy>()) BindGenericPolicy(g, stmt);
        foreach (var stmt in decl.Policies.OfType<LabelPolicy>()) BindLabelPolicy(g, stmt);
        CreateImplicitRouteEdges(g);
        CheckConnectivity(g);
        CheckRoutingAfterPolicies(g);
        CheckUnsegmentable(g);
        CheckOrdering(g);
        CheckFlows(g);
        CheckStrictSources(g);
        g.Graph = BuildIr(g);
        return g;
    }

    private void CollectGraphScope(GraphCtx g)
    {
        var scope = new Dictionary<string, (string Kind, SourceSpan Span)>();
        void Declare(string name, string kind, SourceSpan span)
        {
            if (scope.TryGetValue(name, out var prev))
            {
                g.Diags.Error("E0103", $"duplicate name '{name}' ({kind}) in graph '{g.Decl.Name}'; already declared as a {prev.Kind}", span);
                return;
            }
            scope[name] = (kind, span);
            if (_globals.ContainsKey(name))
                g.Diags.Warning("W0101", $"{kind} '{name}' shadows the global {_globals[name].Kind} of the same name", span);
        }

        foreach (var p in g.Decl.Parameters)
        {
            Declare(p.Name, "parameter", p.Span);
            var t = ResolveType(p.Type, allowBuiltins: false);
            if (t is PrimType pt) g.Params[p.Name] = pt;
            else if (t is not ErrorType) g.Diags.Error("E0204", $"graph parameter '{p.Name}' has type '{t.Display}'; graph parameters must be primitive", p.Span);
        }
        foreach (var n in g.Decl.Nodes)
        {
            Declare(n.Name, "node", n.Span);
            if (g.NodeByName.ContainsKey(n.Name)) continue;
            var ctx = new NodeCtx { Decl = n, Index = g.Nodes.Count };
            g.Nodes.Add(ctx);
            g.NodeByName[n.Name] = ctx;
        }
        foreach (var e in g.Decl.Edges.Where(e => e.Name != null)) Declare(e.Name!, "edge", e.Span);
        foreach (var f in g.Decl.Flows)
        {
            Declare(f.Name, "flow", f.Span);
            g.Flows.TryAdd(f.Name, f);
        }
    }

    // Attributes, partitioning, ports, order specs and the process expression (L§6.1, §6.4, §6.6).
    private void CheckNode(GraphCtx g, NodeCtx n)
    {
        var d = n.Decl;
        foreach (var a in d.Attributes)
        {
            if (a.Name == "one_to_one") n.OneToOne = true;
            else if (a.Name == "json_lines") { }   // the process speaks the JSON-lines port protocol (L§6.1)
            else g.Diags.Error("E0302", $"unknown attribute '@{a.Name}'", a.Span);
        }

        if (d.Partition == null) n.Kind = PartitionKind.Unpartitioned;
        else if (d.Partition.Keys.Count == 0) n.Kind = PartitionKind.Keyless;
        else
        {
            n.Kind = PartitionKind.Keyed;
            var seen = new HashSet<string>();
            foreach (var k in d.Partition.Keys)
            {
                if (!seen.Add(k)) { g.Diags.Error("E0402", $"key domain '{k}' is repeated in the partition spec of '{n.Name}'", d.Partition.Span); continue; }
                if (_keys.TryGetValue(k, out var kd)) n.Keys.Add(kd);
                else g.Diags.Error("E0401", $"partition key '{k}' of '{n.Name}' is not a key domain", d.Partition.Span);
            }
        }

        foreach (var (ports, dir) in new[] { (d.Inputs, PortDirection.In), (d.Outputs, PortDirection.Out) })
        {
            foreach (var p in ports)
            {
                if (n.Ports.ContainsKey(p.Name)) { g.Diags.Error("E0104", $"duplicate port '{p.Name}' on node '{n.Name}'", p.Span); continue; }
                if (dir == PortDirection.In && p.Name == StderrPort)
                {
                    g.Diags.Error("E0104", $"'{StderrPort}' on node '{n.Name}' names the process's error stream, an output; it can't be an input port", p.Span);
                    continue;
                }
                var port = new PortCtx { Node = n, Decl = p, Direction = dir, Type = ResolveType(p.Type, allowBuiltins: false) };
                if (!TypeOps.IsPortType(port.Type))
                {
                    g.Diags.Error("E0202", $"port '{port.Ref}' has type '{port.Type.Display}'; a port type must be a record type or a format", p.Type.Span);
                    port.Type = ErrorType.Instance;
                }
                n.Ports[p.Name] = port;
                (dir == PortDirection.In ? n.Inputs : n.Outputs).Add(port);
                CheckOrderSpec(g, port);
            }
        }

        // stderr is not a data output: it answers nothing and doesn't make a node a non-sink.
        int dataOutputs = n.Outputs.Count(p => !p.IsStderr);
        if (n.OneToOne && (n.Inputs.Count != 1 || dataOutputs != 1))
            g.Diags.Error("E0308", $"@one_to_one node '{n.Name}' must have exactly one input and one output port (has {n.Inputs.Count} and {dataOutputs})", d.Span);
        if (n.Inputs.Count == 0 && dataOutputs == 0)
            g.Diags.Warning("W0301", $"node '{n.Name}' has no ports", d.Span);

        var scope = ProcessScope(g);
        foreach (var arg in new[] { d.Process.Command }.Concat(d.Process.Args))
        {
            var t = ExprChecker.TypeOf(arg, scope, g.Diags);
            if (t is not ErrorType && !(ExprChecker.Repr(t) == PrimType.String))
                g.Diags.Error("E0301", $"process argument has type '{t.Display}'; every argument must be a string", arg.Span);
        }
    }

    private ExprScope ProcessScope(GraphCtx g)
    {
        var scope = new ExprScope { Lattice = _lattice, Labellers = LabellerNames };
        foreach (var (name, t) in g.Params) scope.GraphParameters[name] = t;
        return scope;
    }

    // Order spec placement (L§3.5 notes) and clock field resolution (L§6.6.1).
    private void CheckOrderSpec(GraphCtx g, PortCtx p)
    {
        switch (p.Decl.Order)
        {
            case SequencedSpec s when p.Direction == PortDirection.Out:
                g.Diags.Error("E0514", $"'sequenced' on output port '{p.Ref}'", s.Span);
                break;
            case OrderedSpec o:
                if (p.Direction == PortDirection.In && o.Within != null)
                    g.Diags.Error("E0508", $"'within' on input port '{p.Ref}'; use the lateness policy instead", o.Span);
                if (p.Direction == PortDirection.Out && o.Per != null)
                    g.Diags.Error("E0509", $"'per' on output port '{p.Ref}'", o.Span);
                if (p.Type is ErrorType) break;

                if (o.By != null)
                {
                    var t = p.Type is RecordType ? TypeOps.ResolvePath(p.Type, o.By, out _) : null;
                    if (t is ClockDomainType c) { p.ClockField = o.By; p.Clock = c; }
                    else g.Diags.Error("E0501", $"'ordered by {string.Join(".", o.By)}' on '{p.Ref}': field is {(t == null ? "missing" : $"of type '{t.Display}'")}, not a clock domain", o.Span);
                }
                else if (p.Type is RecordType r)
                {
                    var clocks = r.Fields.Where(f => f.Type is ClockDomainType).ToList();
                    if (clocks.Count == 1) { p.ClockField = new[] { clocks[0].Name }; p.Clock = (ClockDomainType)clocks[0].Type; }
                    else if (clocks.Count == 0) g.Diags.Error("E0502", $"ordered port '{p.Ref}' has no clock field in type '{r.Name}'", o.Span);
                    else g.Diags.Error("E0503", $"ordered port '{p.Ref}' has several clock fields ({string.Join(", ", clocks.Select(c => c.Name))}); use 'ordered by'", o.Span);
                }
                else g.Diags.Error("E0502", $"ordered port '{p.Ref}' has format type '{p.Type.Display}', which has no clock field; use 'sequenced'", o.Span);

                if (o.Per != null && p.Direction == PortDirection.In && TypeOps.ResolvePath(p.Type, o.Per, out _) == null)
                    g.Diags.Error("E0507", $"'per {string.Join(".", o.Per)}' is not a field path of '{p.Type.Display}'", o.Span);
                if (o.Within != null) p.WithinNanos = o.Within.Nanoseconds;
                break;
        }
    }

    // --- Edges (L§6.2, §6.5) ---

    private void ResolveDeclaredEdge(GraphCtx g, EdgeDecl e)
    {
        var src = ResolveEndpoint(g, e.From, PortDirection.Out);
        var dst = ResolveEndpoint(g, e.To, PortDirection.In);
        if (src == null || dst == null) return;

        var name = e.Name ?? UniqueEdgeName(g, $"{src.Ref}__{dst.Ref}");
        var edge = new EdgeCtx { Name = name, Named = e.Name != null, Index = g.Edges.Count, Op = e.Op, Source = src, Destination = dst, ByPaths = e.By, Span = e.Span };
        g.Edges.Add(edge);
        if (e.Name != null) g.NamedEdges.TryAdd(e.Name, edge);
        CheckEdgeTypesAndRouting(g, edge);
    }

    public const string StderrPort = "stderr";

    // A port by name. Every process node has an implicit `stderr` output of format `lines` (L§6.1); it
    // becomes a port of the node when an edge or a policy refers to it. Unreferenced, stderr is a log.
    private PortCtx? PortOf(NodeCtx n, string name)
    {
        if (n.Ports.TryGetValue(name, out var port)) return port;
        if (name != StderrPort) return null;
        var decl = new PortDecl(StderrPort, new NamedTypeRef("lines") { Span = n.Decl.Span }, null) { Span = n.Decl.Span };
        port = new PortCtx { Node = n, Decl = decl, Direction = PortDirection.Out, Type = new FormatType("lines"), Implicit = true };
        n.Ports[name] = port;
        n.Outputs.Add(port);
        return port;
    }

    private static string UniqueEdgeName(GraphCtx g, string baseName)
    {
        var name = baseName;
        for (int i = 2; g.Edges.Any(x => x.Name == name); i++) name = $"{baseName}#{i}";
        return name;
    }

    private PortCtx? ResolveEndpoint(GraphCtx g, Endpoint ep, PortDirection want)
    {
        if (!g.NodeByName.TryGetValue(ep.Node, out var n))
        {
            g.Diags.Error("E0105", $"unknown node '{ep.Node}'", ep.Span);
            return null;
        }
        var side = want == PortDirection.Out ? "left" : "right";
        var candidates = want == PortDirection.Out ? n.Outputs.Where(p => !p.IsStderr).ToList() : n.Inputs;
        if (ep.Port == null)
        {
            if (candidates.Count == 1) return candidates[0];
            g.Diags.Error("E0304", $"'{ep.Node}' has {candidates.Count} {(want == PortDirection.Out ? "output" : "input")} ports; the {side} endpoint must name one", ep.Span);
            return null;
        }
        if (PortOf(n, ep.Port) is not { } port)
        {
            g.Diags.Error("E0105", $"node '{ep.Node}' has no port '{ep.Port}'", ep.Span);
            return null;
        }
        if (port.Direction != want)
        {
            g.Diags.Error("E0303", $"the {side} endpoint of an edge must be an {(want == PortDirection.Out ? "output" : "input")} port; '{port.Ref}' is an {(port.Direction == PortDirection.Out ? "output" : "input")}", ep.Span);
            return null;
        }
        return port;
    }

    private void CheckEdgeTypesAndRouting(GraphCtx g, EdgeCtx e)
    {
        var what = e.Implicit ? $"implicit route edge {e.Source.Ref} -> {e.Destination.Ref}" : $"edge '{e.Name}'";
        switch (TypeOps.Assignable(e.Source.Type, e.Destination.Type, _formats))
        {
            case Assignability.No:
                g.Diags.Error("E0305", $"{what}: type '{e.Source.Type.Display}' is not assignable to '{e.Destination.Type.Display}'", e.Span);
                break;
            case Assignability.Unchecked:
                g.Diags.Warning("W0305", $"{what}: 'json' into record-typed port '{e.Destination.Ref}' is not checked", e.Span);
                break;
        }

        var recv = e.Destination.Node;
        if (e.Op == EdgeOp.AllPartitions)
        {
            if (recv.Kind == PartitionKind.Unpartitioned)
                g.Diags.Error("E0403", $"{what}: '-*>' into unpartitioned node '{recv.Name}'", e.Span);
            if (e.ByPaths != null)
                g.Diags.Error("E0404", $"{what}: 'by' is not allowed on a '-*>' edge", e.Span);
            return;
        }
        if (recv.Kind != PartitionKind.Keyed)
        {
            if (e.ByPaths != null) g.Diags.Error("E0409", $"{what}: 'by' on an edge into unkeyed node '{recv.Name}'", e.Span);
            return;
        }

        // Key extraction uses the receiving port's type (L§6.5).
        if (e.Destination.Type is ErrorType || recv.Keys.Count != recv.Decl.Partition!.Keys.Count) return;
        if (e.Destination.Type is not RecordType rt)
        {
            g.Diags.Error("E0411", $"{what}: keyed receiver '{e.Destination.Ref}' has format type '{e.Destination.Type.Display}', which cannot be key-routed", e.Span);
            return;
        }
        if (e.ByPaths == null)
        {
            var paths = new List<IReadOnlyList<string>>();
            foreach (var k in recv.Keys)
            {
                var fields = TypeOps.FieldsOfDomain(rt, k.Name).ToList();
                if (fields.Count == 0) g.Diags.Error("E0405", $"{what}: type '{rt.Name}' of '{e.Destination.Ref}' has no field of key domain '{k.Name}'", e.Span);
                else if (fields.Count > 1) g.Diags.Error("E0406", $"{what}: type '{rt.Name}' has several fields of key domain '{k.Name}' ({string.Join(", ", fields.Select(f => f.Name))}); add 'by'", e.Span);
                else paths.Add(new[] { fields[0].Name });
            }
            if (paths.Count == recv.Keys.Count) e.KeyPaths = paths;
            return;
        }
        if (e.ByPaths.Count != recv.Keys.Count)
        {
            g.Diags.Error("E0407", $"{what}: 'by' gives {e.ByPaths.Count} field paths but '{recv.Name}' is keyed by {recv.Keys.Count} key domains", e.Span);
            return;
        }
        bool ok = true;
        for (int i = 0; i < e.ByPaths.Count; i++)
        {
            var t = TypeOps.ResolvePath(rt, e.ByPaths[i], out _);
            if (t is KeyDomainType kd && kd.Name == recv.Keys[i].Name) continue;
            ok = false;
            g.Diags.Error("E0408", $"{what}: 'by' path '{string.Join(".", e.ByPaths[i])}' {(t == null ? "does not exist" : $"has type '{t.Display}'")}; expected key domain '{recv.Keys[i].Name}'", e.Span);
        }
        if (ok) e.KeyPaths = e.ByPaths;
    }

    // `on_late(route(n.r)): n.q` creates an implicit edge a.p → n.r for each incoming edge a.p → n.q (L§6.2).
    private void CreateImplicitRouteEdges(GraphCtx g)
    {
        foreach (var (port, routePort, span) in g.RouteTargets)
        {
            var route = port.Node.Ports[routePort];
            foreach (var e in g.Edges.Where(x => !x.Implicit && x.Destination == port).ToList())
            {
                if (g.Edges.Any(x => x.Implicit && x.Source == e.Source && x.Destination == route)) continue;
                var edge = new EdgeCtx
                {
                    Name = UniqueEdgeName(g, $"{e.Source.Ref}__{route.Ref}"), Named = false, Index = g.Edges.Count,
                    Implicit = true, ImplicitOf = e.Name, Op = e.Op, Source = e.Source, Destination = route, ByPaths = e.ByPaths, Span = span,
                };
                g.Edges.Add(edge);
                CheckEdgeTypesAndRouting(g, edge);
            }
        }
    }

    // E0307, W0302, E0306 (L§6.2).
    private void CheckConnectivity(GraphCtx g)
    {
        foreach (var n in g.Nodes)
        {
            foreach (var p in n.Inputs)
                if (!g.Edges.Any(e => e.Destination == p) && !g.RouteTargets.Any(r => r.Port.Node == n && r.RoutePort == p.Name))
                    g.Diags.Error("E0307", $"input port '{p.Ref}' has no incoming edge", p.Decl.Span);
            foreach (var p in n.Outputs.Where(p => !p.Implicit))
                if (!g.Edges.Any(e => e.Source == p))
                    g.Diags.Warning("W0302", $"output port '{p.Ref}' has no outgoing edges", p.Decl.Span);
        }

        // Kahn's algorithm over nodes; nodes left over are on (or downstream of) a cycle.
        var indeg = g.Nodes.ToDictionary(n => n, _ => 0);
        foreach (var e in g.Edges) indeg[e.Destination.Node]++;
        var queue = new Queue<NodeCtx>(g.Nodes.Where(n => indeg[n] == 0));
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            g.TopoOrder.Add(n);
            foreach (var e in g.Edges.Where(e => e.Source.Node == n))
                if (--indeg[e.Destination.Node] == 0) queue.Enqueue(e.Destination.Node);
        }
        if (g.TopoOrder.Count == g.Nodes.Count) return;

        var remaining = g.Nodes.Where(n => !g.TopoOrder.Contains(n)).ToHashSet();
        // Strip nodes that are merely downstream of the cycle to name only the cycle itself.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var n in remaining.ToList())
                if (!g.Edges.Any(e => e.Source.Node == n && remaining.Contains(e.Destination.Node))) { remaining.Remove(n); changed = true; }
        }
        foreach (var n in remaining) g.CycleNodes.Add(n.Name);
        var edge = g.Edges.First(e => remaining.Contains(e.Source.Node) && remaining.Contains(e.Destination.Node));
        g.Diags.Error("E0306", $"the node graph has a cycle through {string.Join(", ", remaining.OrderBy(n => n.Index).Select(n => n.Name))}", edge.Span);
    }

    // Checks that need the policies: E0410, E0608, forward-eligibility (L§6.4, §6.5).
    private void CheckRoutingAfterPolicies(GraphCtx g)
    {
        foreach (var n in g.Nodes.Where(n => n.Kind == PartitionKind.Keyed))
        {
            if (!g.Edges.Any(e => !e.Implicit && e.Destination.Node == n && e.Op == EdgeOp.Routed))
                g.Diags.Error("E0410", $"keyed node '{n.Name}' has no incoming '-->' edge, so nothing is ever routed by key", n.Decl.Span);
            if (n.MaxInstances > n.KeyGroups)
                g.Diags.Error("E0608", $"'{n.Name}' has {n.MaxInstances} instances but only {n.KeyGroups} key groups", n.Decl.Span);
        }
        foreach (var e in g.Edges.Where(e => e.Op == EdgeOp.Routed))
        {
            var s = e.Source.Node; var r = e.Destination.Node;
            e.ForwardEligible = s.Kind == PartitionKind.Keyed && r.Kind == PartitionKind.Keyed
                && s.Keys.Select(k => k.Name).SequenceEqual(r.Keys.Select(k => k.Name))
                && s.KeyGroups == r.KeyGroups && s.MinInstances == r.MinInstances && s.MaxInstances == r.MaxInstances;
        }
    }

    // Edges from a port whose framing is `none` (plain `bytes`) carry the stream as it is: nothing can split,
    // interleave or reorder it, so it goes one-to-one from a single sender instance to a single receiver
    // instance, as the only stream into its port (L§5.2). Label checks are per stream: see E0735.
    private void CheckUnsegmentable(GraphCtx g)
    {
        foreach (var e in g.Edges)
        {
            if (e.Source.Type is ErrorType || _formats.FramingOf(e.Source.Type).Segmentable) continue;
            var what = e.Implicit ? $"implicit route edge {e.Source.Ref} -> {e.Destination.Ref}" : $"edge '{e.Name}'";
            var stream = $"'{e.Source.Ref}' is an unsegmentable stream (format '{e.Source.Type.Display}')";
            var recv = e.Destination.Node;
            if (recv.Kind != PartitionKind.Unpartitioned)
                g.Diags.Error("E0412", $"{what}: {stream}, so it can't be divided among or copied to the instances of partitioned node '{recv.Name}'", e.Span);
            if (e.Source.Node.Kind != PartitionKind.Unpartitioned)
                g.Diags.Error("E0413", $"{what}: {stream} from partitioned node '{e.Source.Node.Name}'; the streams of its instances would interleave in '{e.Destination.Ref}'", e.Span);
            var into = g.Edges.Count(x => x.Destination == e.Destination);
            if (into > 1)
                g.Diags.Error("E0413", $"{what}: {stream}, but '{e.Destination.Ref}' has {into} incoming edges; an unsegmentable stream must be the only one into its port", e.Span);
            if (e.Destination.IsSequenced)
                g.Diags.Error("E0515", $"{what}: {stream}, so sequenced port '{e.Destination.Ref}' can't number its messages", e.Span);
            if (recv.Kind != PartitionKind.Keyless && g.Always.Any(a => a.Node == recv.Name))
                g.Diags.Warning("W0902", $"{what}: {stream} into '{recv.Name}', which has a standby ('always'); bytes in flight on it aren't covered by checkpoints, so a failover may lose or repeat part of the stream", e.Span);
        }
    }

    // Strict mode: every source output port needs a label attachment (L§8.1, E0705).
    private void CheckStrictSources(GraphCtx g)
    {
        if (!_lattice.Strict) return;
        foreach (var n in g.Nodes.Where(n => n.Inputs.Count == 0))
            foreach (var p in n.Outputs)
                if (!g.Labels.Data.ContainsKey(p.Ref))
                    g.Diags.Error("E0705", $"source output port '{p.Ref}' has no label attachment (required in strict mode)", p.Decl.Span);
    }

    // --- IR (L§11) ---

    private CompiledGraph BuildIr(GraphCtx g)
    {
        var nodes = g.Nodes.Select(n => new GraphNode(
            n.Name, n.Index, n.Decl.Attributes.Select(a => a.Name).ToList(), n.OneToOne, n.Kind,
            n.Keys.Select(k => k.Name).ToList(), n.KeyGroups, new InstanceRange(n.MinInstances, n.MaxInstances),
            new ProcessSpec(n.Decl.Process.Command, n.Decl.Process.Args, null),
            n.Inputs.Select(PortIr).ToList(), n.Outputs.Select(PortIr).ToList(), n.Decl.Span)).ToList();

        var edges = g.Edges.Select(e => new GraphEdge(e.Name, e.Index, e.Named, e.Implicit, e.ImplicitOf, e.Op,
            e.Source.Node.Name, e.Source.Name, e.Destination.Node.Name, e.Destination.Name,
            e.KeyPaths, e.ForwardEligible, e.Span)).ToList();

        var flows = g.FlowResults.Select(f => new GraphFlow(f.Decl.Name, f.Members.Select(m => m.Name).ToList(),
            f.Entry, f.Exit, f.Interior, f.Exclusive, f.NonExclusive,
            f.Paths.Select(p => (IReadOnlyList<string>)p.Select(e => e.Name).ToList()).ToList(),
            f.Paths.Select(p => (IReadOnlyList<string>)new[] { p[0].Source.Node.Name }.Concat(p.Select(e => e.Destination.Node.Name)).ToList())
                .DistinctBy(p => string.Join("/", p)).ToList(),
            f.Mode, f.ClockField, f.Latency?.MaxNanos, f.Latency?.Percentile, f.UncheckedReason, f.Decl.Span)).ToList();

        var policies = new GraphPolicies(g.Policies, g.Always, g.Pins, g.MinRates, g.MaxLatencies.Select(m => m.Policy).ToList());
        var labels = new LabelAttachments(g.Labels.Data, g.Labels.PortCeilings, g.Labels.InstanceCeilings,
            g.Labels.InstanceOverrides.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<int, LabelSpec>)kv.Value),
            g.Labels.Dynamic, g.Labels.Host);

        var usedTypes = new Dictionary<string, RecordTypeInfo>();
        void AddType(DslType t)
        {
            while (t is ListType l) t = l.Element;
            if (t is not RecordType r || usedTypes.ContainsKey(r.Name)) return;
            usedTypes[r.Name] = ToRecordInfo(r);
            foreach (var f in r.Fields) AddType(f.Type);
        }
        foreach (var p in g.Nodes.SelectMany(n => n.Ports.Values)) AddType(p.Type);

        return new CompiledGraph(g.Decl.Name,
            g.Decl.Parameters.Select(p => new GraphParameterInfo(p.Name, g.Params.TryGetValue(p.Name, out var t) ? t.Name : "?")).ToList(),
            null, _lattice, usedTypes, nodes, edges, flows, policies, labels, null, g.Diags.Items, g.Decl.Span)
        {
            Labellers = _labellers.ToDictionary(kv => kv.Key, kv => new LabelSpec(LabelSpecKind.Labeller, null, kv.Key,
                kv.Value.Params.Select(p => new LabellerParamInfo(p.Name, p.Type.Display)).ToList(), kv.Value.Decl.Body, kv.Value.Decl.SourceText)),
        };
    }

    private GraphPort PortIr(PortCtx p)
    {
        PortOrder? order = p.Decl.Order switch
        {
            OrderedSpec o => new PortOrder(OrderKind.Ordered, p.ClockField, p.Clock?.Name, p.Clock?.Unit, o.Per,
                o.Within?.Nanoseconds, o.Within != null && p.Clock != null ? o.Within.Nanoseconds / UnitNanos(p.Clock.Unit) : null),
            SequencedSpec => new PortOrder(OrderKind.Sequenced, null, null, null, null, null, null),
            _ => null,
        };
        PortOrdering? ordering = null;
        if (p.Direction == PortDirection.In && p.Decl.Order != null && p.Ordering.TryGetValue(-1, out var def))
        {
            PortOrderingSettings Settings(OrderingPolicy o) => new(o.Lateness ?? 0, o.LatenessFromPolicy,
                o.IdleTimeout, o.GapTimeout, o.MaxBuffer ?? DefaultMaxBufferBytes, o.OnLate ?? OnLateMode.Drop, o.RoutePort);
            ordering = new PortOrdering(Settings(def),
                p.Ordering.Where(kv => kv.Key >= 0).ToDictionary(kv => kv.Key, kv => Settings(kv.Value)));
        }
        var seq = p.Sequence ?? (p.SequenceOrigin || p.SequencePropagating ? new SequenceInfo(p.SequenceOrigin, p.SequencePropagating, null, null) : null);
        return new GraphPort(p.Node.Name, p.Name, p.Direction, ToTypeInfo(p.Type), order, ordering, seq, p.Decl.Span, _formats.FramingOf(p.Type));
    }

    public static long UnitNanos(string unit) => unit switch
    {
        "ns" => 1L, "us" => 1_000L, "ms" => 1_000_000L, "s" => 1_000_000_000L, "m" => 60_000_000_000L, "h" => 3_600_000_000_000L, _ => 1L,
    };
}
