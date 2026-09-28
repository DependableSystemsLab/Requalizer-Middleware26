using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using OneOS.Runtime.Language.Models;
using OneOS.Runtime.Scheduling;

namespace OneOS.Runtime.Graphs;

// A human-readable report of a deployment plan: where every agent runs and why, and how every pipe
// is routed and checked. Used by `oneos sim` and the shell's `graph plan` / `graph spawn`.
public static class GraphReport
{
    [Flags]
    public enum Sections
    {
        None = 0, Cluster = 1, Summary = 2, Nodes = 4, Placement = 8, Pipes = 16, Routing = 32, Flows = 64, Labels = 128,
        Hosts = Placement,      // older name for the Placement section
        All = Cluster | Summary | Nodes | Placement | Pipes | Routing | Flows | Labels,
    }

    public static string Format(GraphInstanceInfo info, CompiledGraph? graph = null, ClusterSnapshot? cluster = null, Sections sections = Sections.All)
    {
        var sb = new StringBuilder();
        string Short(string agentId) => agentId.StartsWith(info.GraphInstanceId + "/", StringComparison.Ordinal) ? agentId[(info.GraphInstanceId.Length + 1)..] : agentId;
        var hostOf = info.Agents.ToDictionary(a => a.AgentId, a => a.HostId ?? "?");

        if (sections.HasFlag(Sections.Cluster) && cluster != null) Cluster(sb, info, cluster);
        if (sections.HasFlag(Sections.Summary)) Summary(sb, info);
        if (sections.HasFlag(Sections.Nodes)) Nodes(sb, info, graph);
        if (sections.HasFlag(Sections.Placement)) Hosts(sb, info, cluster, Short);
        if (sections.HasFlag(Sections.Pipes)) Pipes(sb, info, graph, Short, hostOf);
        if (sections.HasFlag(Sections.Routing)) Routing(sb, info, Short);
        if (sections.HasFlag(Sections.Flows)) Flows(sb, info);
        if (sections.HasFlag(Sections.Labels) && graph != null) Labels(sb, info, graph);
        return sb.ToString();
    }

    // --- Sections ---

    private static void Cluster(StringBuilder sb, GraphInstanceInfo info, ClusterSnapshot cluster)
    {
        Heading(sb, $"Cluster ({cluster.Hosts.Count} hosts)");
        var rows = cluster.Hosts.Select(h =>
        {
            var mine = info.Agents.Where(a => a.HostId == h.HostId).ToList();
            var cpu = mine.Sum(a => a.Demand.CpuMillis);
            var mem = mine.Sum(a => a.Demand.MemoryBytes);
            return new[]
            {
                h.HostId, h.Label ?? "⊥", h.Zone ?? "-", h.Alive ? "yes" : "NO",
                $"{cpu + h.Committed.CpuMillis}/{h.Capacity.CpuMillis}m ({Pct(cpu + h.Committed.CpuMillis, h.Capacity.CpuMillis)})",
                $"{(mem + h.Committed.MemoryBytes) >> 20}/{h.Capacity.MemoryBytes >> 20} MiB",
                mine.Count == 0 ? "-" : $"{mine.Count(a => a.Role == AgentRole.Primary)}" + (mine.Any(a => a.Role == AgentRole.Standby) ? $" (+{mine.Count(a => a.Role == AgentRole.Standby)} standby)" : ""),
                h.Executables == null ? "any" : string.Join(",", h.Executables.Keys),
            };
        });
        Table(sb, new[] { "HOST", "LABEL", "ZONE", "ALIVE", "CPU (after plan)", "MEMORY", "AGENTS", "EXECUTABLES" }, rows);
    }

    private static void Summary(StringBuilder sb, GraphInstanceInfo info)
    {
        var p = info.Plan;
        Heading(sb, $"Graph instance {info.GraphInstanceId}: {info.GraphName}({string.Join(", ", info.Args.Select(Render))})");
        sb.Append($"  state {info.State}, plan v{info.Version}, solver {p.SolverStatus}, objective {p.ObjectiveValue} (bound {p.BestBound})\n");
        sb.Append($"  {info.Agents.Count(a => a.Role == AgentRole.Primary)} agents");
        if (info.Agents.Any(a => a.Role == AgentRole.Standby)) sb.Append($" + {info.Agents.Count(a => a.Role == AgentRole.Standby)} standbys");
        sb.Append($" on {info.Agents.Select(a => a.HostId).Distinct().Count()} hosts, {info.Pipes.Count} pipes ({info.Pipes.Count(x => x.NativeFormat)} local), {info.RoutingTables.Count} routing tables\n");
        sb.Append("  objective terms: ").Append(string.Join(", ", p.ObjectiveTerms.Select(kv => $"{kv.Key}={kv.Value}"))).Append('\n');
        if (p.RelaxedConstraints.Count > 0) sb.Append("  relaxed: ").Append(string.Join(", ", p.RelaxedConstraints)).Append('\n');
        foreach (var w in p.Warnings) sb.Append("  ").Append(w.ToString().Replace("\n", "\n  ")).Append('\n');
    }

    private static void Nodes(StringBuilder sb, GraphInstanceInfo info, CompiledGraph? graph)
    {
        Heading(sb, "Nodes");
        var dift = info.Plan.Audit.Dift.ToDictionary(d => d.Node);
        var names = graph?.Nodes.OrderBy(n => n.Index).Select(n => n.Name) ?? info.Agents.Select(a => a.NodeName).Distinct();
        var rows = new List<string[]>();
        foreach (var name in names)
        {
            var agents = info.Agents.Where(a => a.NodeName == name).ToList();
            var first = agents.FirstOrDefault(a => a.Role == AgentRole.Primary) ?? agents.FirstOrDefault();
            if (first == null) continue;
            var node = graph?.FindNode(name);
            string partitioning = first.Kind switch
            {
                PartitionKind.Keyed => $"keyed [{string.Join(", ", first.KeyDomains ?? Array.Empty<string>())}], G={first.KeyGroups}",
                PartitionKind.Keyless => "keyless []",
                _ => "unpartitioned",
            };
            int primaries = agents.Count(a => a.Role == AgentRole.Primary);
            int laneCount = agents.Select(a => a.LaneLabel).Distinct().Count(l => l != null);
            string instances = node != null && node.IsPartitioned
                ? $"{primaries} (partitions {Range(node.Instances)}{(laneCount > 1 ? $" per lane × {laneCount}" : "")})" : $"{primaries}";
            if (agents.Any(a => a.Role == AgentRole.Standby)) instances += $" + {agents.Count(a => a.Role == AgentRole.Standby)} standby";
            var d = dift.GetValueOrDefault(name);
            string diftText = d == null ? "-" : $"{Class(d.Class)}, {d.Mode.ToString().ToLowerInvariant()}" + (d.ExternalSinkCeiling != null ? $", C_ext {d.ExternalSinkCeiling}" : "");
            string lanes = d is { Lanes.Count: > 0 } ? string.Join(" | ", d.Lanes) : "-";
            string placement = first.CompartmentBoundAtRuntime ? AnyOf(first)
                : agents.Select(a => a.PlacementLabel).Distinct().Count() > 1 ? string.Join(" | ", agents.Where(a => a.Role == AgentRole.Primary).Select(a => a.PlacementLabel).Distinct().Select(l => l == null ? "none" : "⊒ " + l))
                : first.PlacementLabel == null ? "none" : "⊒ " + first.PlacementLabel;
            string attrs = string.Join(" ", new[] { first.OneToOne ? "@one_to_one" : null, first.PinPattern != null ? $"pin {first.PinPattern}" : null,
                first.UncheckedExclusive ? "unchecked-exclusive" : null }.Where(x => x != null));
            rows.Add(new[] { name, partitioning, instances, diftText, lanes, placement, attrs.Length == 0 ? "-" : attrs, string.Join(" ", first.Argv.Select(Quote)) });
        }
        Table(sb, new[] { "NODE", "PARTITIONING", "INSTANCES", "DIFT", "LANES", "PLACEMENT", "ATTRIBUTES", "PROCESS (argv)" }, rows);

        // Ports: types, ordering and label configuration.
        var portRows = new List<string[]>();
        foreach (var name in names)
        {
            var a = info.Agents.FirstOrDefault(x => x.NodeName == name && x.Role == AgentRole.Primary);
            if (a == null) continue;
            foreach (var p in a.Ports)
            {
                string order = p.Order == null ? "-" : p.Order.Kind == OrderKind.Sequenced ? "sequenced" + (p.GapTimeout is TimeSpan g ? $", gap {Ms(g)}" : "")
                    : $"ordered by {string.Join(".", p.Order.ClockField ?? Array.Empty<string>())}"
                      + (p.Order.Per is { Count: > 0 } per ? $" per {string.Join(".", per)}" : "")
                      + (p.Order.WithinNanos is long w && w > 0 ? $" within {w / 1_000_000}ms" : "")
                      + (p.Direction == PortDirection.In ? $", lateness {Ms(p.Lateness ?? TimeSpan.Zero)}" + (p.IdleTimeout is TimeSpan it ? $", idle {Ms(it)}" : "")
                        + (p.OnLate is OnLateMode ol and not OnLateMode.Drop ? $", on_late {ol.ToString().ToLowerInvariant()}{(p.OnLateRoutePort != null ? "→" + p.OnLateRoutePort : "")}" : "") : "")
                      + (p.Direction == PortDirection.In && p.MaxBufferBytes != 64L << 20 ? $", max_buffer {p.MaxBufferBytes >> 20}MB" : "");
                string seq = p.SequenceOrigin ? "seq origin" : p.SequencePropagating ? "seq propagate" : "";
                string label = p.Direction == PortDirection.Out ? (p.DataLabeller != null ? $"labeller {Spec(p.DataLabeller)}" : "-") : (p.Ceiling != null ? $"ceiling {Spec(p.Ceiling)}" : "-");
                portRows.Add(new[] { $"{name}.{p.Name}", p.Direction == PortDirection.In ? "in" : "out", p.PortType, order + (seq.Length > 0 ? $" ({seq})" : ""), label });
            }
        }
        if (portRows.Count > 0)
        {
            sb.Append('\n');
            Table(sb, new[] { "PORT", "DIR", "TYPE", "ORDERING", "LABEL" }, portRows);
        }
    }

    private static void Hosts(StringBuilder sb, GraphInstanceInfo info, ClusterSnapshot? cluster, Func<string, string> shortId)
    {
        Heading(sb, "Placement (agents by host)");
        foreach (var host in info.Agents.GroupBy(a => a.HostId ?? "?").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var h = cluster?.Hosts.FirstOrDefault(x => x.HostId == host.Key);
            sb.Append($"  {host.Key}" + (h != null ? $" [{h.Label ?? "⊥"}]" : "") + "\n");
            var rows = host.OrderBy(a => a.NodeName).ThenBy(a => a.AgentId, StringComparer.Ordinal).Select(a => new[]
            {
                "    " + shortId(a.AgentId),
                a.Role == AgentRole.Standby ? "standby" : "primary",
                a.State.ToString(),
                a.OwnedKeyGroups is { } k ? $"keys [{k.From},{k.To})" : a.LaneLabel != null ? $"lane {a.LaneLabel}" : "-",
                a.CompartmentBoundAtRuntime ? AnyOf(a) : a.PlacementLabel != null ? "⊒ " + a.PlacementLabel : "-",
                $"{a.Demand.CpuMillis}m/{a.Demand.MemoryBytes >> 20}MiB",
                a.Dift == DiftMode.Enabled ? "IDM" : "",
            });
            Table(sb, null, rows);
        }
    }

    private static void Pipes(StringBuilder sb, GraphInstanceInfo info, CompiledGraph? graph, Func<string, string> shortId, Dictionary<string, string> hostOf)
    {
        Heading(sb, "Pipes (by edge)");
        foreach (var edge in info.Pipes.GroupBy(p => p.EdgeName).OrderBy(g => g.Min(p => PipeIndex(p.PipeId))))
        {
            var p0 = edge.First();
            var e = graph?.Edge(edge.Key);
            string arrow = e?.Op == Language.Ast.EdgeOp.AllPartitions ? "-*>" : "-->";
            string from = e != null ? e.Source : $"?.{p0.SourcePort}";
            string to = e != null ? e.Destination : $"?.{p0.DestinationPort}";
            sb.Append($"  {edge.Key}: {from} {arrow} {to}   routing {p0.Routing}");
            if (p0.KeyPaths != null) sb.Append($" by ({string.Join(", ", p0.KeyPaths.Select(k => string.Join(".", k)))})");
            if (p0.ForwardEligible) sb.Append(", forward-eligible");
            if (p0.Implicit) sb.Append(", implicit route edge");
            if (p0.Unchecked) sb.Append(", UNCHECKED flow member");
            if (p0.Bypass) sb.Append(", BYPASS (raw byte stream, no sidecar)");
            if (p0.FlowIds.Count > 0) sb.Append($", flows {string.Join(", ", p0.FlowIds.Select(f => f[(f.LastIndexOf('/') + 1)..]))}");
            if (p0.EstimatedRate is long r) sb.Append($", ~{r} msg/s per pipe");
            sb.Append('\n');
            var rows = edge.OrderBy(p => p.SourceAgentId, StringComparer.Ordinal).ThenBy(p => p.DestinationAgentId, StringComparer.Ordinal).Select(p => new[]
            {
                "    " + shortId(p.SourceAgentId) + $" ({hostOf.GetValueOrDefault(p.SourceAgentId)})",
                "→ " + shortId(p.DestinationAgentId) + $" ({hostOf.GetValueOrDefault(p.DestinationAgentId)})",
                p.NativeFormat ? "local" : "remote",
                p.DestinationKeyGroups is { } k ? $"keys [{k.From},{k.To})" : "",
                "allowed {" + string.Join(",", p.Allowed) + "}",
                // Copies on unchecked member edges skip every delivery check (L§8.7), whatever was proven.
                p.Unchecked ? "checks skipped (unchecked)" : "checks " + Checks(p.Elided),
                $"stream {p.StreamId}",
                p.ExpectedLatencyMicros is long l ? $"~{l} µs" : "",
            });
            Table(sb, null, rows);
        }
    }

    private static void Routing(StringBuilder sb, GraphInstanceInfo info, Func<string, string> shortId)
    {
        if (info.RoutingTables.Count == 0) return;
        Heading(sb, "Load-balancing routing tables (routing probability per label, S§10.2)");
        foreach (var t in info.RoutingTables)
        {
            sb.Append($"  {shortId(t.SourceAgentId)}.{t.SourcePort} on {t.EdgeName}: redundancy {t.Redundancy}, deviation {t.Deviation:0.###}, {t.Trigger} v{t.Version}");
            if (t.Unroutable.Count > 0) sb.Append($", QUEUED (no compliant route): {string.Join(", ", t.Unroutable)}");
            sb.Append('\n');
            var header = new[] { "    label" }.Concat(t.ReceiverAgentIds.Select(shortId)).ToArray();
            var rows = t.Labels.Select((l, i) =>
            {
                double total = t.Weights[i].Sum();
                return new[] { "    " + l }.Concat(t.ReceiverAgentIds.Select((_, s) => !t.Active[i][s] ? "·" : (total > 0 ? t.Weights[i][s] / total : 0).ToString("0.00", CultureInfo.InvariantCulture))).ToArray();
            });
            Table(sb, header, rows);
        }
        sb.Append("  (· = not a route for that label: the receiver's lane or ceiling can't accept it)\n");
    }

    private static void Flows(StringBuilder sb, GraphInstanceInfo info)
    {
        if (info.Flows.Count == 0) return;
        Heading(sb, "Flows");
        foreach (var f in info.Flows)
        {
            sb.Append($"  {f.Name} = {{{string.Join(", ", f.Edges)}}}, {(f.Mode == LatencyMode.EventTime ? $"event time by {string.Join(".", f.ClockField ?? Array.Empty<string>())}" : "correlated")}");
            if (f.MaxLatency is TimeSpan m) sb.Append($", max_latency {Ms(m)} at p{f.LatencyPercentile}");
            if (f.MaxLatency == null) sb.Append(", no latency goal");
            if (f.UncheckedReason != null) sb.Append($", UNCHECKED ('{f.UncheckedReason}'), exclusive nodes: {string.Join(", ", f.ExclusiveNodes)}");
            sb.Append('\n');
            for (int i = 0; i < f.NodePaths.Count; i++)
                sb.Append($"    path {string.Join(" → ", f.NodePaths[i])}" + (i < f.ExpectedPathLatencyMicros.Count ? $": expected {f.ExpectedPathLatencyMicros[i]} µs" : "") + "\n");
        }
    }

    private static void Labels(StringBuilder sb, GraphInstanceInfo info, CompiledGraph graph)
    {
        Heading(sb, $"Labels (lattice: {string.Join(", ", info.Lattice.Labels)}; ⊥={info.Lattice.Bottom}, ⊤={info.Lattice.Top}{(info.Lattice.ForbiddenTop ? " forbidden" : "")})");
        if (graph.Analysis == null) return;
        var rows = new List<string[]>();
        foreach (var n in graph.Nodes.OrderBy(n => n.Index))
        {
            // Laned nodes: each lane's agents carry their own part, so show the union.
            var primaries = info.Agents.Where(x => x.NodeName == n.Name && x.Role == AgentRole.Primary).ToList();
            foreach (var p in n.Ports)
            {
                if (!graph.Analysis.Ports.TryGetValue(p.Ref, out var pl)) continue;
                var planned = primaries.Count == 0 ? null
                    : primaries.SelectMany(x => x.Ports.FirstOrDefault(y => y.Name == p.Name)?.PossibleLabels ?? Array.Empty<string>()).Distinct().ToList();
                rows.Add(new[] { p.Ref, p.Direction == PortDirection.In ? "in" : "out", pl.Lo == pl.Up ? $"[{pl.Lo}]" : $"[{pl.Lo}..{pl.Up}]",
                    "{" + string.Join(",", planned ?? pl.PossibleLabels) + "}" });
            }
        }
        Table(sb, new[] { "PORT", "DIR", "INTERVAL (L§8.6)", "POSSIBLE LABELS (PL⁺)" }, rows);
        var held = graph.Analysis.Nodes.Select(kv => $"{kv.Key}={kv.Value.Held}");
        sb.Append($"  held labels: {string.Join(", ", held)}\n");
        foreach (var d in info.Plan.Audit.Declassifications)
            sb.Append($"  declassification at {d.Port}: [{d.BaseLo}..{d.BaseUp}] → {{{string.Join(", ", d.LowerResults)}}} by {d.Labeller}; trusted: {d.TrustedComponent}\n");
        foreach (var u in info.Plan.Audit.UncheckedFlows)
            sb.Append($"  unchecked flow {u.Flow}: {u.Masked.Count} masked diagnostics\n");
    }

    // --- Helpers ---

    private static void Heading(StringBuilder sb, string title) => sb.Append('\n').Append("── ").Append(title).Append(' ').Append(new string('─', Math.Max(3, 76 - title.Length))).Append('\n');

    private static void Table(StringBuilder sb, string[]? header, IEnumerable<string[]> rows)
    {
        var all = rows.ToList();
        if (header != null) all.Insert(0, header);
        if (all.Count == 0) return;
        int cols = all.Max(r => r.Length);
        var widths = Enumerable.Range(0, cols).Select(c => all.Max(r => c < r.Length ? r[c].Length : 0)).ToArray();
        // Columns that are empty in every data row are left out.
        var shown = Enumerable.Range(0, cols).Where(c => all.Skip(header != null ? 1 : 0).Any(r => c < r.Length && r[c].Trim().Length > 0)).ToList();
        foreach (var r in all)
        {
            var line = string.Join("  ", shown.Select(c => (c < r.Length ? r[c] : "").PadRight(widths[c]))).TrimEnd();
            sb.Append(header != null && !line.StartsWith("  ", StringComparison.Ordinal) ? "  " : "").Append(line).Append('\n');
        }
    }

    // A compartment-bound agent's host must hold one compartment's data; with no alternatives (e.g. a
    // source emitting several compartments) nothing constrains it.
    private static string AnyOf(GraphAgentInfo a) => a.PlacementAnyOf is { Count: > 0 } xs
        ? $"one of {string.Join("/", xs)}" : "none (spans compartments)";

    private static string Checks(ElidedChecks e)
    {
        var all = new[] { (ElidedChecks.PortCeiling, "port"), (ElidedChecks.InstanceCeiling, "instance"), (ElidedChecks.Host, "host"), (ElidedChecks.Compartment, "compartment") };
        var kept = all.Where(x => !e.HasFlag(x.Item1)).Select(x => x.Item2).ToList();
        return kept.Count == 0 ? "all elided" : $"on: {string.Join("+", kept)}";
    }

    private static int PipeIndex(string id) => int.TryParse(id[(id.LastIndexOf('/') + 1)..].Split('.').Last(), out var n) ? n : int.MaxValue;
    private static string Range(InstanceRange r) => r.Min == r.Max ? $"{r.Min}" : $"{r.Min}..{r.Max}";
    private static string Class(ComponentClass c) => c switch { ComponentClass.IToI => "I-to-I", ComponentClass.IToX => "I-to-X", ComponentClass.XToI => "X-to-I", _ => "X-to-X" };
    private static string Pct(long used, long cap) => cap <= 0 ? "-" : $"{100 * used / cap}%";
    private static string Ms(TimeSpan t) => t.TotalMilliseconds >= 1000 && t.TotalMilliseconds % 1000 == 0 ? $"{t.TotalSeconds:0}s" : $"{t.TotalMilliseconds:0.#}ms";
    private static string Spec(LabelSpec s) => s.Kind == LabelSpecKind.Constant ? s.Label! : s.LabellerName ?? "(" + s.SourceText + ")";
    private static string Quote(string a) => a.Contains(' ') || a.Length == 0 ? $"'{a}'" : a;
    private static string Render(object? v) => v switch { string s => $"'{s}'", bool b => b ? "true" : "false", null => "null", _ => Convert.ToString(v, CultureInfo.InvariantCulture)! };
}
