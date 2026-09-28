using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OneOS.Runtime.Language.Ast;
using OneOS.Runtime.Language.Models;

namespace OneOS.Runtime.Language;

public sealed partial class AppCompiler
{
    private static readonly HashSet<string> OrderingPolicies = new() { "lateness", "idle_timeout", "on_late", "gap_timeout", "max_buffer" };

    private sealed record TargetRef(ResolvedTarget T, NodeCtx? Node, PortCtx? Port, EdgeCtx? Edge, FlowDecl? Flow);

    // --- Targets (L§7.1) ---

    private TargetRef? ResolveTarget(GraphCtx g, Target t)
    {
        int? instance = t.IndexKind == TargetIndexKind.At ? t.Index : null;
        if (g.NodeByName.TryGetValue(t.Name, out var n))
        {
            if (t.IndexKind == TargetIndexKind.At)
            {
                if (n.Kind == PartitionKind.Unpartitioned)
                {
                    g.Diags.Error("E0604", $"indexed target '{t}' on unpartitioned node '{n.Name}'", t.Span);
                    return null;
                }
            }
            if (t.Port == null)
            {
                var kind = t.IndexKind switch { TargetIndexKind.None => TargetKind.Node, TargetIndexKind.All => TargetKind.NodeAllInstances, _ => TargetKind.NodeInstance };
                return new TargetRef(new ResolvedTarget(kind, n.Name, instance, null), n, null, null, null);
            }
            if (PortOf(n, t.Port) is not { } p)
            {
                g.Diags.Error("E0105", $"node '{n.Name}' has no port '{t.Port}'", t.Span);
                return null;
            }
            var pk = t.IndexKind switch { TargetIndexKind.None => TargetKind.Port, TargetIndexKind.All => TargetKind.PortAllInstances, _ => TargetKind.PortInstance };
            return new TargetRef(new ResolvedTarget(pk, n.Name, instance, p.Name), n, p, null, null);
        }

        bool isEdge = g.Decl.Edges.Any(e => e.Name == t.Name);
        bool isFlow = g.Flows.ContainsKey(t.Name);
        if (isEdge || isFlow)
        {
            if (t.IndexKind != TargetIndexKind.None || t.Port != null)
            {
                g.Diags.Error("E0603", $"'{t}': {(isEdge ? "edges" : "flows")} cannot be indexed or have ports", t.Span);
                return null;
            }
            if (isEdge)
                return g.NamedEdges.TryGetValue(t.Name, out var e)
                    ? new TargetRef(new ResolvedTarget(TargetKind.Edge, t.Name, null, null), null, null, e, null)
                    : null;   // the edge itself failed to resolve; already reported
            return new TargetRef(new ResolvedTarget(TargetKind.Flow, t.Name, null, null), null, null, null, g.Flows[t.Name]);
        }

        g.Diags.Error("E0105", $"unknown policy target '{t.Name}'", t.Span);
        return null;
    }

    // --- Generic policies (L§7.2) ---

    private void BindGenericPolicy(GraphCtx g, GenericPolicy stmt)
    {
        var name = stmt.Name;
        var known = new[] { "always", "min_rate", "max_latency", "partitions", "key_groups", "pin", "lateness", "idle_timeout", "on_late", "gap_timeout", "max_buffer", "unchecked" };
        if (name == "label")
        {
            g.Diags.Error("E0602", "label(...) takes exactly one label source", stmt.Span);
            return;
        }
        if (!known.Contains(name))
        {
            g.Diags.Error("E0601", $"unknown policy '{name}'", stmt.Span);
            return;
        }
        if (stmt.By != null && name != "max_latency")
            g.Diags.Error("E0602", $"only max_latency accepts a 'by' clause, not '{name}'", stmt.Span);

        bool ArgCount(int min, int max)
        {
            if (stmt.Args.Count >= min && stmt.Args.Count <= max) return true;
            g.Diags.Error("E0602", $"'{name}' takes {(min == max ? $"{min}" : $"{min} to {max}")} argument{(max == 1 ? "" : "s")}, not {stmt.Args.Count}", stmt.Span);
            return false;
        }
        bool NoRange(PolicyArg a)
        {
            if (a.RangeEnd == null) return true;
            g.Diags.Error("E0602", $"'{name}' does not accept a range", a.Span);
            return false;
        }
        long? Duration(PolicyArg a)
        {
            if (!NoRange(a)) return null;
            if (a.Value is DurationLiteral d) return d.Nanoseconds;
            if (a.Value is not ErrorExpr) g.Diags.Error("E0602", $"'{name}' expects a duration such as 200ms", a.Span);
            return null;
        }
        double? Number(PolicyArg a, string what)
        {
            if (!NoRange(a)) return null;
            if (a.Value is IntLiteral i) return i.Value;
            if (a.Value is FloatLiteral f) return f.Value;
            if (a.Value is not ErrorExpr) g.Diags.Error("E0602", $"'{name}' expects a number for {what}", a.Span);
            return null;
        }
        double? Percentile(int index)
        {
            if (stmt.Args.Count <= index) return 99;
            var v = Number(stmt.Args[index], "the percentile");
            if (v is null) return null;
            if (v > 0 && v < 100) return v;
            g.Diags.Error("E0602", $"percentile {v} is outside the open range (0, 100)", stmt.Args[index].Span);
            return null;
        }

        var targets = stmt.Targets.Select(t => (Ast: t, Ref: ResolveTarget(g, t))).ToList();
        bool Allowed(TargetRef r, SourceSpan span, params TargetKind[] kinds)
        {
            if (kinds.Contains(r.T.Kind)) return true;
            g.Diags.Error("E0603", $"'{name}' cannot target '{r.T}' ({Describe(r.T.Kind)})", span);
            return false;
        }

        var argText = stmt.Args.Select(a => RenderArg(a)).ToList();
        var resolved = targets.Where(t => t.Ref != null).Select(t => t.Ref!.T).ToList();
        g.Policies.Add(new NormalizedPolicy(name, argText, stmt.By, resolved, stmt.Span));

        switch (name)
        {
            case "always":
                if (!ArgCount(0, 0)) return;
                foreach (var (ast, r) in targets)
                {
                    if (r == null || !Allowed(r, ast.Span, TargetKind.Node, TargetKind.NodeInstance)) continue;
                    if (r.T.Kind == TargetKind.NodeInstance && r.Node!.Kind == PartitionKind.Keyless)
                    {
                        g.Diags.Error("E0609", $"'always: {ast}' on keyless node '{r.Node.Name}'; keyless instances are interchangeable, use 'always: {r.Node.Name}'", ast.Span);
                        continue;
                    }
                    if (!CheckDuplicate(g, name, r.T, ast.Span)) continue;
                    g.Always.Add(new AlwaysPolicy(r.Node!.Name, r.T.Instance));
                }
                break;

            case "partitions":
            {
                if (!ArgCount(1, 1)) return;
                var a = stmt.Args[0];
                int lo, hi;
                if (a.Value is IntLiteral l && (a.RangeEnd == null || a.RangeEnd is IntLiteral))
                {
                    lo = (int)Math.Min(l.Value, int.MaxValue);
                    hi = a.RangeEnd is IntLiteral h ? (int)Math.Min(h.Value, int.MaxValue) : lo;
                }
                else { g.Diags.Error("E0602", "partitions expects an integer or an integer range a..b", a.Span); return; }
                if (lo < 1 || lo > hi) { g.Diags.Error("E0602", $"invalid partition count {RenderArg(a)}; need 1 ≤ a ≤ b", a.Span); return; }
                foreach (var (ast, r) in targets)
                {
                    if (r == null || !Allowed(r, ast.Span, TargetKind.Node)) continue;
                    var n = r.Node!;
                    if (n.Kind == PartitionKind.Unpartitioned) { g.Diags.Error("E0603", $"partitions applies only to partitioned nodes; '{n.Name}' is unpartitioned (declare it '{n.Name}[]' or '{n.Name}[key]')", ast.Span); continue; }
                    if (n.Kind == PartitionKind.Keyed && lo < hi) { g.Diags.Error("E0610", $"partition range {lo}..{hi} on keyed node '{n.Name}'; keyed nodes have a fixed instance count", ast.Span); continue; }
                    if (!CheckDuplicate(g, name, r.T, ast.Span)) continue;
                    n.MinInstances = lo; n.MaxInstances = hi;
                }
                break;
            }

            case "key_groups":
            {
                if (!ArgCount(1, 1) || !NoRange(stmt.Args[0])) return;
                if (stmt.Args[0].Value is not IntLiteral { Value: >= 1 and <= int.MaxValue } k) { g.Diags.Error("E0602", "key_groups expects an integer ≥ 1", stmt.Args[0].Span); return; }
                foreach (var (ast, r) in targets)
                {
                    if (r == null || !Allowed(r, ast.Span, TargetKind.Node)) continue;
                    if (r.Node!.Kind != PartitionKind.Keyed) { g.Diags.Error("E0603", $"key_groups applies only to keyed nodes; '{r.Node.Name}' is not keyed", ast.Span); continue; }
                    if (!CheckDuplicate(g, name, r.T, ast.Span)) continue;
                    r.Node.KeyGroups = (int)k.Value;
                }
                break;
            }

            case "pin":
            {
                if (!ArgCount(1, 1) || !NoRange(stmt.Args[0])) return;
                if (stmt.Args[0].Value is not StringLiteral { Value.Length: > 0 } pat) { g.Diags.Error("E0602", "pin expects a host-name glob string", stmt.Args[0].Span); return; }
                foreach (var (ast, r) in targets)
                {
                    if (r == null || !Allowed(r, ast.Span, TargetKind.Node, TargetKind.NodeInstance)) continue;
                    if (!CheckDuplicate(g, name, r.T, ast.Span)) continue;
                    g.Pins.Add(new PinPolicy(r.Node!.Name, r.T.Instance, pat.Value));
                }
                break;
            }

            case "min_rate":
            {
                if (!ArgCount(1, 2)) return;
                var rate = Number(stmt.Args[0], "the rate");
                var pct = Percentile(1);
                if (rate is null || pct is null) return;
                if (rate <= 0) { g.Diags.Error("E0602", "min_rate must be positive", stmt.Args[0].Span); return; }
                foreach (var (ast, r) in targets)
                {
                    if (r == null || !Allowed(r, ast.Span, TargetKind.Edge, TargetKind.Port, TargetKind.PortAllInstances, TargetKind.PortInstance)) continue;
                    if (!CheckDuplicate(g, name, r.T, ast.Span)) continue;
                    g.MinRates.Add(new RatePolicy(r.T, rate.Value, pct.Value));
                }
                break;
            }

            case "max_latency":
            {
                if (!ArgCount(1, 2)) return;
                var d = Duration(stmt.Args[0]);
                var pct = Percentile(1);
                if (d is null || pct is null) return;
                foreach (var (ast, r) in targets)
                {
                    if (r == null || !Allowed(r, ast.Span, TargetKind.Flow, TargetKind.Edge)) continue;
                    if (!CheckDuplicate(g, name, r.T, ast.Span)) continue;
                    g.MaxLatencies.Add((new LatencyPolicy(r.T, d.Value, pct.Value, stmt.By), stmt.Span));
                }
                break;
            }

            case "unchecked":
            {
                if (!ArgCount(1, 1) || !NoRange(stmt.Args[0])) return;
                if (stmt.Args[0].Value is not StringLiteral { Value.Length: > 0 } reason)
                {
                    g.Diags.Error("E0602", "unchecked requires a non-empty reason string", stmt.Args[0].Span);
                    return;
                }
                foreach (var (ast, r) in targets)
                {
                    if (r == null || !Allowed(r, ast.Span, TargetKind.Flow)) continue;
                    if (!CheckDuplicate(g, name, r.T, ast.Span)) continue;
                    g.Unchecked[r.T.Name] = (reason.Value, stmt.Span);
                }
                break;
            }

            default:
                BindOrderingPolicy(g, stmt, targets, ArgCount, Duration, NoRange);
                break;
        }
    }

    private void BindOrderingPolicy(GraphCtx g, GenericPolicy stmt, List<(Target Ast, TargetRef? Ref)> targets,
        Func<int, int, bool> argCount, Func<PolicyArg, long?> duration, Func<PolicyArg, bool> noRange)
    {
        var name = stmt.Name;
        if (!argCount(1, 1)) return;
        var arg = stmt.Args[0];
        long? value = null;
        OnLateMode? mode = null;
        (string Node, string Port, SourceSpan Span)? route = null;
        switch (name)
        {
            case "lateness" or "idle_timeout" or "gap_timeout":
                value = duration(arg);
                if (value == null) return;
                break;
            case "max_buffer":
                if (!noRange(arg)) return;
                if (arg.Value is not SizeLiteral { Value: > 0 } size) { g.Diags.Error("E0602", "max_buffer expects a positive size such as 64MB", arg.Span); return; }
                value = size.Bytes;
                break;
            case "on_late":
                if (!noRange(arg)) return;
                switch (arg.Value)
                {
                    case IdentExpr { Name: "drop" }: mode = OnLateMode.Drop; break;
                    case IdentExpr { Name: "pass" }: mode = OnLateMode.Pass; break;
                    case CallExpr { Callee: IdentExpr { Name: "route" }, Args: [MemberExpr { Target: IdentExpr rn } rm] }:
                        mode = OnLateMode.Route;
                        route = (rn.Name, rm.Name, rm.Span);
                        break;
                    default:
                        g.Diags.Error("E0602", "on_late expects drop, pass or route(n.p)", arg.Span);
                        return;
                }
                break;
        }

        foreach (var (ast, r) in targets)
        {
            if (r == null) continue;
            if (r.T.Kind is not (TargetKind.Port or TargetKind.PortAllInstances or TargetKind.PortInstance))
            {
                g.Diags.Error("E0603", $"'{name}' targets input ports, not '{r.T}' ({Describe(r.T.Kind)})", ast.Span);
                continue;
            }
            var p = r.Port!;
            bool fits = p.Direction == PortDirection.In && name switch
            {
                "gap_timeout" => p.IsSequenced,
                "max_buffer" => p.IsOrdered || p.IsSequenced,
                _ => p.IsOrdered,
            };
            if (!fits)
            {
                var want = name switch { "gap_timeout" => "sequenced input ports", "max_buffer" => "ordered or sequenced input ports", _ => "ordered input ports" };
                var have = p.Direction == PortDirection.Out ? "an output port" : p.IsSequenced ? "a sequenced input port" : p.IsOrdered ? "an ordered input port" : "an unordered input port";
                g.Diags.Error("E0606", $"'{name}' applies to {want}; '{p.Ref}' is {have}", ast.Span);
                continue;
            }
            if (route != null)
            {
                var (rnode, rport, rspan) = route.Value;
                PortCtx? rp = null;
                if (rnode != p.Node.Name) g.Diags.Error("E0607", $"on_late(route({rnode}.{rport})) must route to another input port of '{p.Node.Name}'", rspan);
                else if (!p.Node.Ports.TryGetValue(rport, out rp) || rp.Direction != PortDirection.In) g.Diags.Error("E0607", $"'{rnode}.{rport}' is not an input port of '{p.Node.Name}'", rspan);
                else if (rp == p) g.Diags.Error("E0607", $"on_late cannot route '{p.Ref}' to itself", rspan);
                else if (TypeOps.Assignable(p.Type, rp.Type, _formats) == Assignability.No)
                    g.Diags.Error("E0607", $"route target '{rp.Ref}' has type '{rp.Type.Display}', which '{p.Type.Display}' is not assignable to", rspan);
                if (rp == null || rp == p || rnode != p.Node.Name || rp.Direction != PortDirection.In) continue;
                if (!g.RouteTargets.Any(x => x.Port == p && x.RoutePort == rport)) g.RouteTargets.Add((p, rport, stmt.Span));
            }
            if (!CheckDuplicate(g, name, r.T, ast.Span)) continue;

            int key = r.T.Kind == TargetKind.PortInstance ? r.T.Instance!.Value : -1;
            if (!p.Ordering.TryGetValue(key, out var o)) p.Ordering[key] = o = new OrderingPolicy();
            switch (name)
            {
                case "lateness": o.Lateness = value; o.LatenessFromPolicy = true; break;
                case "idle_timeout": o.IdleTimeout = value; break;
                case "gap_timeout": o.GapTimeout = value; break;
                case "max_buffer": o.MaxBuffer = value; break;
                case "on_late": o.OnLate = mode; o.RoutePort = route?.Port; break;
            }
        }
    }

    // The same policy given twice at the same target specificity is E0602 (see plan.md decisions).
    // `n.p` and `n[*].p` both set the port-level default.
    private static bool CheckDuplicate(GraphCtx g, string policy, ResolvedTarget t, SourceSpan span)
    {
        var norm = t.Kind switch
        {
            TargetKind.PortAllInstances => $"{t.Name}.{t.Port}",
            TargetKind.NodeAllInstances => t.Name,
            _ => t.ToString(),
        };
        if (g.SeenPolicyTargets.Add($"{policy}@{norm}")) return true;
        g.Diags.Error("E0602", $"'{policy}' is already given for '{norm}'", span);
        return false;
    }

    private static string Describe(TargetKind k) => k switch
    {
        TargetKind.Node => "a node",
        TargetKind.NodeAllInstances => "every instance of a node",
        TargetKind.NodeInstance => "a node instance",
        TargetKind.Port => "a port",
        TargetKind.PortAllInstances => "a port on every instance",
        TargetKind.PortInstance => "a port on one instance",
        TargetKind.Edge => "an edge",
        _ => "a flow",
    };

    private static string RenderArg(PolicyArg a) => RenderExpr(a.Value) + (a.RangeEnd != null ? ".." + RenderExpr(a.RangeEnd) : "");

    public static string RenderExpr(Expression e) => e switch
    {
        StringLiteral s => $"'{s.Value}'",
        IntLiteral i => i.Value.ToString(CultureInfo.InvariantCulture),
        FloatLiteral f => f.Value.ToString(CultureInfo.InvariantCulture),
        BoolLiteral b => b.Value ? "true" : "false",
        DurationLiteral d => d.ToString(),
        SizeLiteral z => z.ToString(),
        IdentExpr id => id.Name,
        MemberExpr m => $"{RenderExpr(m.Target)}.{m.Name}",
        CallExpr c => $"{RenderExpr(c.Callee)}({string.Join(", ", c.Args.Select(RenderExpr))})",
        UnaryExpr u => $"{u.Op}{RenderExpr(u.Operand)}",
        BinaryExpr b => $"{RenderExpr(b.Left)} {b.Op} {RenderExpr(b.Right)}",
        TernaryExpr t => $"{RenderExpr(t.Condition)} ? {RenderExpr(t.Then)} : {RenderExpr(t.Else)}",
        ErrorExpr x => x.Text,
        _ => "?",
    };

    // --- Label attachments (L§8.4) ---

    private void BindLabelPolicy(GraphCtx g, LabelPolicy stmt)
    {
        if (!_lattice.Declared)
        {
            g.Diags.Error("E0704", "label statement without a labels block", stmt.Span);
            return;
        }

        LabelSpec? spec = null;
        List<(string Name, DslType Type)>? parameters = null;
        switch (stmt.Source)
        {
            case NamedLabelSource nl:
                if (_lattice.Contains(nl.Name))
                {
                    if (_lattice.Synthetic[_lattice.IndexOf(nl.Name)])
                        g.Diags.Error("E0105", $"'{nl.Name}' is a synthetic label and cannot be named in a program", nl.Span);
                    spec = new LabelSpec(LabelSpecKind.Constant, nl.Name, null, null, null, null);
                }
                else if (_labellers.TryGetValue(nl.Name, out var lab))
                {
                    parameters = lab.Params;
                    spec = new LabelSpec(LabelSpecKind.Labeller, null, nl.Name,
                        lab.Params.Select(p => new LabellerParamInfo(p.Name, p.Type.Display)).ToList(), lab.Decl.Body, lab.Decl.SourceText);
                }
                else g.Diags.Error("E0105", $"'{nl.Name}' is neither a label nor a labeller", nl.Span);
                break;

            case LambdaLabelSource lam:
            {
                var scope = new ExprScope { Lattice = _lattice, Labellers = LabellerNames, IsLabeller = true };
                foreach (var (pn, pt) in g.Params) scope.GraphParameters[pn] = pt;
                parameters = new List<(string, DslType)>();
                foreach (var p in lam.Parameters)
                {
                    var t = ResolveType(p.Type, allowBuiltins: true);
                    parameters.Add((p.Name, t));
                    if (!scope.Parameters.TryAdd(p.Name, t)) g.Diags.Error("E0104", $"duplicate lambda parameter '{p.Name}'", p.Span);
                    WarnIfShadowsLabel(p);
                }
                var bodyType = ExprChecker.TypeOf(lam.Body, scope, g.Diags);
                if (bodyType is not (LabelValueType or ErrorType))
                    g.Diags.Error("E0710", $"label lambda has type '{bodyType.Display}', not 'label'", lam.Body.Span);
                spec = new LabelSpec(LabelSpecKind.Labeller, null, null,
                    parameters.Select(p => new LabellerParamInfo(p.Name, p.Type.Display)).ToList(), lam.Body, lam.SourceText);
                break;
            }

            case DynamicLabelSource or HostLabelSource:
            {
                var (lo, hi, isDynamic) = stmt.Source is DynamicLabelSource d ? (d.Lo, d.Hi, true) : (((HostLabelSource)stmt.Source).Lo, ((HostLabelSource)stmt.Source).Hi, false);
                bool ok = true;
                foreach (var l in new[] { lo, hi })
                    if (!_lattice.Contains(l)) { g.Diags.Error("E0105", $"unknown label '{l}'", stmt.Source.Span); ok = false; }
                    else if (_lattice.Synthetic[_lattice.IndexOf(l)]) { g.Diags.Error("E0105", $"'{l}' is a synthetic label and cannot be named in a program", stmt.Source.Span); ok = false; }
                if (ok && !_lattice.Leq(lo, hi))
                {
                    g.Diags.Error("E0721", $"{(isDynamic ? "dynamic" : "host")} range {lo}..{hi}: {lo} ⋢ {hi}", stmt.Source.Span);
                    ok = false;
                }
                foreach (var t in stmt.Targets)
                {
                    var r = ResolveTarget(g, t);
                    if (r == null) continue;
                    if (r.T.Kind != TargetKind.Node)
                    {
                        g.Diags.Error(isDynamic ? "E0720" : "E0722", $"'{(isDynamic ? "dynamic" : "host")}' applies to a bare node, not '{r.T}'", t.Span);
                        continue;
                    }
                    var map = isDynamic ? g.Labels.Dynamic : g.Labels.Host;
                    if (map.ContainsKey(r.Node!.Name)) { g.Diags.Error("E0724", $"node '{r.Node.Name}' already has a {(isDynamic ? "dynamic" : "host")} attachment", t.Span); continue; }
                    if (ok) map[r.Node.Name] = new LabelRange(lo, hi);
                }
                return;
            }
        }

        foreach (var t in stmt.Targets)
        {
            var r = ResolveTarget(g, t);
            if (r == null || spec == null) continue;
            switch (r.T.Kind)
            {
                case TargetKind.Port when r.Port!.Direction == PortDirection.Out:
                    if (parameters != null && !CheckLabellerSignature(g, parameters, t, r.Port.Type, "output port", allowLabelParam: true)) continue;
                    if (!g.Labels.Data.TryAdd(r.Port.Ref, spec))
                        g.Diags.Error("E0723", $"output port '{r.Port.Ref}' already has a data labeller", t.Span);
                    break;
                case TargetKind.Port or TargetKind.PortAllInstances when r.Port!.Direction == PortDirection.In:
                    if (parameters != null && !CheckLabellerSignature(g, parameters, t, r.Port.Type, "input port", allowLabelParam: false)) continue;
                    if (!g.Labels.PortCeilings.TryAdd(r.Port.Ref, spec))
                        g.Diags.Error("E0724", $"input port '{r.Port.Ref}' already has a ceiling", t.Span);
                    break;
                case TargetKind.Node or TargetKind.NodeAllInstances:
                    if (parameters != null && !CheckLabellerSignature(g, parameters, t, ReplicaType.Instance, "instance", allowLabelParam: false)) continue;
                    if (!g.Labels.InstanceCeilings.TryAdd(r.Node!.Name, spec))
                        g.Diags.Error("E0724", $"instances of '{r.Node.Name}' already have a ceiling", t.Span);
                    break;
                case TargetKind.NodeInstance:
                    if (parameters != null && !CheckLabellerSignature(g, parameters, t, ReplicaType.Instance, "instance", allowLabelParam: false)) continue;
                    if (!g.Labels.InstanceOverrides.TryGetValue(r.Node!.Name, out var ov)) g.Labels.InstanceOverrides[r.Node.Name] = ov = new();
                    if (!ov.TryAdd(r.T.Instance!.Value, spec))
                        g.Diags.Error("E0724", $"instance '{r.T}' already has a ceiling", t.Span);
                    break;
                default:
                    g.Diags.Error("E0603", $"a label cannot be attached to '{r.T}' ({Describe(r.T.Kind)}); targets are output ports, input ports and instances", t.Span);
                    break;
            }
        }
    }

    // L§8.3: output ports take (m: T) or (m: T, x: label); input ports (m: T); instances (r: replica).
    // The first parameter's type must be T or a type T is assignable to (E0712).
    private bool CheckLabellerSignature(GraphCtx g, List<(string Name, DslType Type)> ps, Target t, DslType targetType, string what, bool allowLabelParam)
    {
        if (ps.Count == 0 || ps.Count > 2)
        {
            g.Diags.Error("E0712", $"labeller for {what} '{t}' must take {(allowLabelParam ? "one or two parameters" : "one parameter")}", t.Span);
            return false;
        }
        if (ps.Count == 2)
        {
            if (!allowLabelParam && ps[1].Type is LabelValueType)
            {
                g.Diags.Error("E0713", $"a 'label' second parameter is only allowed on output-port labellers, not on {what} '{t}'", t.Span);
                return false;
            }
            if (!allowLabelParam || ps[1].Type is not (LabelValueType or ErrorType))
            {
                g.Diags.Error("E0712", $"the second parameter of an output-port labeller must have type 'label' ('{t}')", t.Span);
                return false;
            }
        }
        var p0 = ps[0].Type;
        bool fits = p0 is ErrorType || targetType is ErrorType || (targetType is ReplicaType
            ? p0 is ReplicaType
            : p0 is not (ReplicaType or LabelValueType) && TypeOps.Assignable(targetType, p0, _formats) == Assignability.Yes);
        if (!fits)
        {
            g.Diags.Error("E0712", $"labeller parameter '{ps[0].Name}: {p0.Display}' does not accept {what} '{t}' of type '{targetType.Display}'", t.Span);
            return false;
        }
        return true;
    }

    // --- Ordering and sequencing (L§6.6) ---

    private void CheckOrdering(GraphCtx g)
    {
        foreach (var p in g.Nodes.SelectMany(n => n.Inputs))
        {
            if (!p.IsOrdered && !p.IsSequenced) continue;
            if (!p.Ordering.TryGetValue(-1, out var def)) p.Ordering[-1] = def = new OrderingPolicy();
            var incoming = g.Edges.Where(e => e.Destination == p).ToList();
            var declared = incoming.Where(e => !e.Implicit).ToList();

            if (p.IsOrdered)
            {
                long maxWithin = 0;
                foreach (var e in declared)
                {
                    if (!e.Source.IsOrdered)
                    {
                        g.Diags.Error("E0504", $"ordered input '{p.Ref}' is fed by unordered output '{e.Source.Ref}' (edge '{e.Name}')", e.Span);
                        continue;
                    }
                    maxWithin = Math.Max(maxWithin, e.Source.WithinNanos);
                    if (e.Source.Clock != null && p.Clock != null && e.Source.Clock.Name != p.Clock.Name)
                        g.Diags.Error("E0505", $"edge '{e.Name}': clock domain '{e.Source.Clock.Name}' of '{e.Source.Ref}' differs from '{p.Clock.Name}' of '{p.Ref}'", e.Span);
                }
                foreach (var (key, o) in p.Ordering)
                    if (o.Lateness is long lat && lat < maxWithin)
                        g.Diags.Warning("W0502", $"lateness {FormatNanos(lat)} on '{p.Ref}{(key >= 0 ? $" (instance {key})" : "")}' is below the largest sender promise ({FormatNanos(maxWithin)}); messages within their sender's promised disorder may be treated as late", p.Decl.Span);
                if (def.Lateness == null) def.Lateness = maxWithin;

                bool multiStream = incoming.Count > 1 || incoming.Any(e => e.Source.Node.MaxInstances > 1);
                if (multiStream && def.IdleTimeout == null)
                    g.Diags.Warning("W0503", $"ordered port '{p.Ref}' merges several streams but has no idle_timeout; a single quiet stream stalls the merge indefinitely", p.Decl.Span);
            }
            else
            {
                CheckSequenced(g, p, incoming);
            }

            foreach (var key in p.Ordering.Keys.Where(k => k >= 0).ToList())
                p.Ordering[key] = p.Ordering[key].MergedOver(def);
        }
    }

    private static void CheckSequenced(GraphCtx g, PortCtx p, List<EdgeCtx> incoming)
    {
        if (incoming.Count != 1)
        {
            g.Diags.Error("E0510", $"sequenced port '{p.Ref}' has {incoming.Count} incoming edges; it needs exactly one", p.Decl.Span);
            return;
        }
        var path = new List<NodeCtx>();
        var visited = new HashSet<NodeCtx>();
        var cur = incoming[0].Source;
        while (visited.Add(cur.Node))
        {
            var u = cur.Node;
            if (!u.OneToOne)
            {
                if (u.Kind != PartitionKind.Unpartitioned)
                {
                    // A partitioned node can't be the origin; if it processes input it should have been @one_to_one.
                    if (u.Inputs.Count > 0)
                        g.Diags.Error("E0511", $"node '{u.Name}' on the sequenced path into '{p.Ref}' is not @one_to_one", u.Decl.Span);
                    else
                        g.Diags.Error("E0513", $"the sequence origin '{cur.Ref}' for '{p.Ref}' belongs to partitioned node '{u.Name}'", u.Decl.Span);
                    return;
                }
                cur.SequenceOrigin = true;
                foreach (var n in path) n.Outputs.First(p => !p.IsStderr).SequencePropagating = true;
                p.Sequence = new SequenceInfo(false, false, cur.Ref, path.Select(n => n.Name).ToList());
                if (!path.Any(n => n.Kind != PartitionKind.Unpartitioned))
                    g.Diags.Warning("W0501", $"sequenced path into '{p.Ref}' has no partitioned node, so sequencing adds nothing", p.Decl.Span);
                return;
            }
            path.Insert(0, u);
            if (u.Inputs.Count != 1) return;   // E0308 already reported
            var ins = g.Edges.Where(e => e.Destination == u.Inputs[0]).ToList();
            if (ins.Count != 1)
            {
                g.Diags.Error("E0512", $"node '{u.Name}' on the sequenced path into '{p.Ref}' has {ins.Count} incoming edges on '{u.Inputs[0].Ref}'; it needs exactly one", u.Decl.Span);
                return;
            }
            cur = ins[0].Source;
        }
    }

    // --- Flows (L§6.7) ---

    private void CheckFlows(GraphCtx g)
    {
        foreach (var f in g.Decl.Flows)
        {
            if (g.FlowResults.Any(x => x.Decl.Name == f.Name)) continue;
            var ctx = new FlowCtx { Decl = f };
            g.FlowResults.Add(ctx);

            foreach (var name in f.Edges.Distinct())
            {
                if (g.NamedEdges.TryGetValue(name, out var e)) ctx.Members.Add(e);
                else if (!g.Decl.Edges.Any(x => x.Name == name))
                    g.Diags.Error("E0620", $"flow '{f.Name}': '{name}' is not a named, declared edge", f.Span);
            }
            if (ctx.Members.Count == 0) continue;

            // Connectivity (E0621): union-find over the member edges' endpoint nodes.
            var parent = new Dictionary<NodeCtx, NodeCtx>();
            NodeCtx Find(NodeCtx x) { if (!parent.TryGetValue(x, out var p)) parent[x] = p = x; return p == x ? x : parent[x] = Find(p); }
            foreach (var e in ctx.Members) parent[Find(e.Source.Node)] = Find(e.Destination.Node);
            if (parent.Keys.Select(Find).Distinct().Count() > 1)
            {
                g.Diags.Error("E0621", $"flow '{f.Name}': its edges do not form a single connected piece", f.Span);
                continue;
            }

            var dests = ctx.Members.Select(e => e.Destination.Node).ToHashSet();
            var srcs = ctx.Members.Select(e => e.Source.Node).ToHashSet();
            ctx.Entry = ctx.Members.Where(e => !dests.Contains(e.Source.Node)).Select(e => e.Source.Ref).Distinct().ToList();
            ctx.Exit = ctx.Members.Where(e => !srcs.Contains(e.Destination.Node)).Select(e => e.Destination.Ref).Distinct().ToList();
            ctx.Interior = ctx.Members.Select(e => e.Destination.Node).Where(srcs.Contains).OrderBy(n => n.Index).Select(n => n.Name).Distinct().ToList();

            // Flow paths: from each entry edge along member edges to an exit (the graph is a DAG).
            if (g.CycleNodes.Count == 0)
            {
                void Walk(List<EdgeCtx> sofar)
                {
                    if (ctx.Paths.Count >= 10_000) return;
                    var next = ctx.Members.Where(e => e.Source.Node == sofar[^1].Destination.Node).ToList();
                    if (next.Count == 0) { ctx.Paths.Add(new List<EdgeCtx>(sofar)); return; }
                    foreach (var e in next) { sofar.Add(e); Walk(sofar); sofar.RemoveAt(sofar.Count - 1); }
                }
                foreach (var e in ctx.Members.Where(e => !dests.Contains(e.Source.Node))) Walk(new List<EdgeCtx> { e });
            }

            // Exclusive nodes: every incoming and outgoing edge (including implicit ones) is a member (L§8.7).
            var touching = ctx.Members.SelectMany(e => new[] { e.Source.Node, e.Destination.Node }).Distinct().OrderBy(n => n.Index);
            foreach (var n in touching)
            {
                bool exclusive = g.Edges.Where(e => e.Source.Node == n || e.Destination.Node == n).All(ctx.Members.Contains);
                (exclusive ? ctx.Exclusive : ctx.NonExclusive).Add(n.Name);
            }

            if (g.Unchecked.TryGetValue(f.Name, out var uc))
            {
                ctx.UncheckedReason = uc.Reason;
                foreach (var n in ctx.NonExclusive)
                    g.Diags.Warning("W0740", $"node '{n}' touches unchecked flow '{f.Name}' but is not exclusive; its node-level checks stay on", g.NodeByName[n].Decl.Span);
            }

            var latency = g.MaxLatencies.FirstOrDefault(m => m.Policy.Target.Kind == TargetKind.Flow && m.Policy.Target.Name == f.Name);
            if (latency.Policy == null) continue;
            ctx.Latency = latency.Policy;
            if (latency.Policy.By != null)
            {
                ctx.Mode = LatencyMode.EventTime;
                ctx.ClockField = latency.Policy.By;
                CheckEventTimeField(g, ctx.Members, latency.Policy.By, $"flow '{f.Name}'", latency.Span);
            }
            else
            {
                foreach (var n in ctx.Interior.Where(n => !g.NodeByName[n].OneToOne))
                    g.Diags.Error("E0622", $"max_latency on flow '{f.Name}' is not measurable: interior node '{n}' is not @one_to_one; add 'by <clock field>'", latency.Span);
            }

            // W0901: lateness along a flow path (ordered input ports, including the exit) ≥ the bound.
            foreach (var path in ctx.Paths)
            {
                long sum = path.Select(e => e.Destination).Where(p => p.IsOrdered && p.Ordering.ContainsKey(-1)).Sum(p => p.Ordering[-1].Lateness ?? 0);
                if (sum >= latency.Policy.MaxNanos)
                {
                    g.Diags.Warning("W0901", $"flow '{f.Name}': lateness along path {string.Join(" -> ", path.Select(e => e.Name))} totals {FormatNanos(sum)}, at least the max_latency bound {FormatNanos(latency.Policy.MaxNanos)}", latency.Span);
                    break;
                }
            }
        }

        foreach (var (policy, span) in g.MaxLatencies.Where(m => m.Policy.Target.Kind == TargetKind.Edge && m.Policy.By != null))
            if (g.NamedEdges.TryGetValue(policy.Target.Name, out var e))
                CheckEventTimeField(g, new[] { e }, policy.By!, $"edge '{e.Name}'", span);
    }

    // E0623: `by f` must be a field path of the same clock domain at both ends of every member edge.
    private static void CheckEventTimeField(GraphCtx g, IEnumerable<EdgeCtx> edges, IReadOnlyList<string> field, string what, SourceSpan span)
    {
        string? domain = null;
        var path = string.Join(".", field);
        foreach (var port in edges.SelectMany(e => new[] { e.Source, e.Destination }).Distinct())
        {
            if (port.Type is ErrorType) continue;
            var t = TypeOps.ResolvePath(port.Type, field, out _);
            if (t is not ClockDomainType c)
            {
                g.Diags.Error("E0623", $"{what}: '{path}' {(t == null ? "is missing from" : "is not a clock domain in")} the type '{port.Type.Display}' of '{port.Ref}'", span);
                continue;
            }
            domain ??= c.Name;
            if (c.Name != domain)
                g.Diags.Error("E0623", $"{what}: '{path}' has clock domain '{c.Name}' at '{port.Ref}' but '{domain}' elsewhere", span);
        }
    }

    public static string FormatNanos(long ns)
    {
        foreach (var (unit, size) in new[] { ("h", 3_600_000_000_000L), ("m", 60_000_000_000L), ("s", 1_000_000_000L), ("ms", 1_000_000L), ("us", 1_000L) })
            if (ns != 0 && ns % size == 0) return $"{ns / size}{unit}";
        return $"{ns}ns";
    }
}
