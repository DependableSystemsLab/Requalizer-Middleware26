using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using OneOS.Runtime.Language;
using OneOS.Runtime.Language.Models;

namespace OneOS.Runtime.Scheduling;

// Turns a compiled graph into a deployment plan (scheduling-spec phases 0–5 and the §9.1
// finalize steps). Committing and starting the plan (phase 6) is the deployer's job.
//
// Not yet implemented (plan.md step 3): phase 1 (DIFT classification, lanes), load-balancing
// routing tables, the DIFT report. Every node is treated as I-to-I with DIFT disabled, which
// is exact when nodes have no external interfaces.
public sealed class GraphScheduler
{
    private readonly IClusterProvider _cluster;
    private readonly SchedulerOptions _options;
    private readonly IProfileStore _profiles;

    public GraphScheduler(IClusterProvider cluster, SchedulerOptions? options = null, IProfileStore? profiles = null)
    {
        _cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));
        _options = options ?? new SchedulerOptions();
        _profiles = profiles ?? new InMemoryProfileStore();
    }

    // PlanGraph (S§12): phases 0–5 plus the finalize steps, without committing.
    // Throws SchedulingException carrying the SP-code on failure.
    public GraphInstanceInfo PlanGraph(CompiledGraph graph, IReadOnlyList<object?> args, CancellationToken ct = default)
    {
        var ctx = Prepare(graph, args, ct);
        return Run(ctx, null, ct);
    }

    // Re-plans a running instance (S§10.4, S§10.5): every agent not being re-placed stays on its host,
    // `Replace` agents are placed again (avoiding `AvoidHosts` when possible), and `GroupCounts` resizes
    // elastic groups. The instance keeps its id; unchanged pipes keep their ids and stream ids.
    public GraphInstanceInfo Replan(CompiledGraph bound, GraphInstanceInfo current, ReplanRequest request, CancellationToken ct = default)
    {
        var snapshot = _cluster.TakeSnapshot();
        // The snapshot's reservations include this instance's own; the re-plan replaces them.
        var own = current.Agents.Where(a => a.HostId != null && (a.Role == AgentRole.Primary || _options.ReserveStandbyCapacity))
            .GroupBy(a => a.HostId!).ToDictionary(g => g.Key, g => g.Aggregate(ResourceVector.Zero, (acc, a) => acc.Add(a.Demand)));
        snapshot = snapshot with
        {
            Hosts = snapshot.Hosts.Select(h => own.TryGetValue(h.HostId, out var o)
                ? h with { Committed = new ResourceVector(Math.Max(0, h.Committed.CpuMillis - o.CpuMillis), Math.Max(0, h.Committed.MemoryBytes - o.MemoryBytes)) }
                : h).ToList(),
        };
        var ctx = new PlanContext
        {
            Graph = bound, GraphInstanceId = current.GraphInstanceId, Snapshot = snapshot, Options = _options, Profiles = _profiles,
            Analysis = bound.Analysis, Replan = request, Current = current,
        };
        var next = Run(ctx, request, ct);
        return PreserveIdentity(current, next, request.Trigger, request.Reincarnated);
    }

    private static GraphInstanceInfo Run(PlanContext ctx, ReplanRequest? request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DiftPhase.Configure(ctx);
        ct.ThrowIfCancellationRequested();
        Concretize(ctx);
        BindPolicies(ctx);
        ComputeEligibility(ctx);
        if (request != null) PinExisting(ctx, request);
        EarlyChecks(ctx);
        ct.ThrowIfCancellationRequested();
        var result = new PlacementSolver(ctx).Solve(ct);
        ct.ThrowIfCancellationRequested();
        Finalize(ctx);
        return BuildRecords(ctx, result);
    }

    // Keeps every agent that isn't being re-placed on its current host, and steers re-placed and new
    // agents away from `AvoidHosts` (unless that would leave them nowhere to go).
    private static void PinExisting(PlanContext ctx, ReplanRequest request)
    {
        var current = ctx.Current!;
        var replace = request.Reincarnated;
        var promote = request.Promote ?? new HashSet<string>();
        var avoid = request.AvoidHosts ?? new HashSet<string>();
        foreach (var a in ctx.Agents)
        {
            var id = $"{ctx.GraphInstanceId}/{a.Group.Name}/{a.Index}" + (a.Role == AgentRole.Standby ? "/standby" : "");
            // A promoted primary runs where its standby was, which holds its latest checkpoint.
            if (a.Role == AgentRole.Primary && promote.Contains(id)
                && current.Agents.FirstOrDefault(x => x.AgentId == id + "/standby")?.HostId is string standbyHost)
            {
                int sh = Enumerable.Range(0, ctx.HostCount).FirstOrDefault(i => ctx.Host(i).HostId == standbyHost, -1);
                if (sh >= 0 && a.Eligible.Contains(sh))
                {
                    a.Eligible = new List<int> { sh };
                    continue;
                }
            }
            var old = current.Agents.FirstOrDefault(x => x.AgentId == id);
            if (old != null && !replace.Contains(id))
            {
                int h = Enumerable.Range(0, ctx.HostCount).FirstOrDefault(i => ctx.Host(i).HostId == old.HostId, -1);
                if (h >= 0 && a.Eligible.Contains(h))
                {
                    a.Eligible = new List<int> { h };
                    continue;
                }
            }
            var preferred = a.Eligible.Where(h => !avoid.Contains(ctx.Host(h).HostId)).ToList();
            // A promoted primary's new standby: separation from the primary (H5) matters more than
            // avoiding the host where the old process died, which may still be alive.
            if (a.Role == AgentRole.Standby && a.Primary is { } p && p.Eligible.Count == 1
                && promote.Contains($"{ctx.GraphInstanceId}/{p.Group.Name}/{p.Index}"))
            {
                var apart = preferred.Where(h => h != p.Eligible[0]).ToList();
                if (apart.Count == 0) apart = a.Eligible.Where(h => h != p.Eligible[0]).ToList();
                if (apart.Count > 0) { a.Eligible = apart; continue; }
            }
            if (preferred.Count > 0) a.Eligible = preferred;
        }
    }

    // Unchanged pipes (same edge, endpoints and hosts) keep their ids and stream ids, so live
    // connections carry over; others get fresh ids. Unchanged agents keep their state.
    private static GraphInstanceInfo PreserveIdentity(GraphInstanceInfo current, GraphInstanceInfo next, string trigger, IReadOnlySet<string> replaced)
    {
        var version = current.Version + 1;
        var oldHost = current.Agents.ToDictionary(a => a.AgentId, a => a.HostId);
        var newHost = next.Agents.ToDictionary(a => a.AgentId, a => a.HostId);
        // A replaced agent is a new incarnation even on the same host: its pipes get fresh ids, so senders
        // open new connections instead of writing into the old process's.
        bool Unchanged(string agent) => !replaced.Contains(agent) && oldHost.TryGetValue(agent, out var h) && newHost.GetValueOrDefault(agent) == h;
        var oldPipes = current.Pipes.ToDictionary(p => (p.EdgeName, p.SourceAgentId, p.DestinationAgentId));
        var nextStream = current.Pipes.GroupBy(p => (p.DestinationAgentId, p.DestinationPort)).ToDictionary(g => g.Key, g => g.Max(p => p.StreamId) + 1);
        int fresh = 0;
        var pipes = next.Pipes.Select(p =>
        {
            if (oldPipes.TryGetValue((p.EdgeName, p.SourceAgentId, p.DestinationAgentId), out var o) && Unchanged(p.SourceAgentId) && Unchanged(p.DestinationAgentId))
                return p with { PipeId = o.PipeId, StreamId = o.StreamId, Version = o.Version };
            var key = (p.DestinationAgentId, p.DestinationPort);
            int stream = nextStream.GetValueOrDefault(key);
            nextStream[key] = stream + 1;
            return p with { PipeId = $"{current.GraphInstanceId}/pipe/v{version}.{fresh++}", StreamId = stream, Version = version };
        }).ToList();
        var agents = next.Agents.Select(a => Unchanged(a.AgentId) ? a with { State = current.Agent(a.AgentId).State, Version = current.Agent(a.AgentId).Version } : a with { Version = version }).ToList();
        var tables = next.RoutingTables.Select(t => t with { Trigger = trigger, Version = version }).ToList();
        return next with { Pipes = pipes, Agents = agents, RoutingTables = tables, State = current.State, CreatedAt = current.CreatedAt, Version = version };
    }

    // Membership change (S§10.4, first step): re-solve every load-balancing table without the given
    // receivers, before any replacement exists. Labels left without a compliant receiver queue.
    public static GraphInstanceInfo RecomputeRoutingTables(GraphInstanceInfo instance, IReadOnlySet<string> unavailable, TimeSpan budget)
    {
        var tables = new List<RoutingTableInfo>();
        foreach (var t in instance.RoutingTables)
        {
            if (unavailable.Contains(t.SourceAgentId)) { tables.Add(t); continue; }
            var receivers = t.ReceiverAgentIds.Where(r => !unavailable.Contains(r)).ToList();
            var pipes = instance.Pipes.Where(p => p.SourceAgentId == t.SourceAgentId && p.EdgeName == t.EdgeName).ToList();
            var mass = t.Labels.Select((l, i) => (l, m: t.Weights[i].Sum())).ToDictionary(x => x.l, x => x.m);
            double total = mass.Values.Sum();
            var freq = mass.ToDictionary(kv => kv.Key, kv => total > 0 ? kv.Value / total : 1.0 / Math.Max(1, mass.Count));
            var latency = pipes.Where(p => receivers.Contains(p.DestinationAgentId)).ToDictionary(p => p.DestinationAgentId, p => p.ExpectedLatencyMicros ?? 50);
            var problem = new LoadBalanceProblem(t.SourceAgentId, t.SourcePort, t.EdgeName, t.Labels, freq, receivers,
                receivers.ToDictionary(r => r, r => (IReadOnlySet<string>)pipes.First(p => p.DestinationAgentId == r).Allowed.ToHashSet()),
                LoadBalancer.SharesFromLatency(latency), t.Redundancy);
            tables.Add(LoadBalancer.Solve(problem, "membership", budget, t.Version + 1));
        }
        return instance with { RoutingTables = tables, Version = instance.Version + 1 };
    }

    // --- Phase 0: prepare (S§3) ---

    // Steps 1–3: reject graphs with compilation errors (SP001), bind the arguments (SP002), and re-run
    // label analysis with the parameters bound (SP003). An already bound graph is returned as is.
    public static CompiledGraph BindForSpawn(CompiledGraph graph, IReadOnlyList<object?> args)
    {
        if (graph.HasErrors)
            throw new SchedulingException("SP001", $"graph '{graph.Name}' has compilation errors",
                graph.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()).ToArray());
        if (graph.IsBound)
        {
            if (args.Count > 0 && !args.SequenceEqual(graph.Args!))
                throw new SchedulingException("SP002", $"{graph.Name}: the graph is already bound to different arguments");
            return graph;
        }
        var (b, diags) = GraphBinder.Bind(graph, args);
        if (b != null) return b;
        var code = diags.Any(d => d.Code == "E0804") ? "SP003" : "SP002";
        var msg = code == "SP003" ? "label analysis with bound parameters failed" : "spawn argument count or type mismatch";
        throw new SchedulingException(code, $"{graph.Name}: {msg}", diags.Where(d => d.IsError).Select(d => d.ToString()).ToArray());
    }

    private PlanContext Prepare(CompiledGraph graph, IReadOnlyList<object?> args, CancellationToken ct)
    {
        var bound = BindForSpawn(graph, args);
        if (bound.Analysis == null)
            throw new SchedulingException("SP003", $"{graph.Name}: no label analysis results");

        ct.ThrowIfCancellationRequested();
        return new PlanContext
        {
            Graph = bound,
            GraphInstanceId = _options.NewGraphInstanceId(),
            Snapshot = _cluster.TakeSnapshot(),
            Options = _options,
            Profiles = _profiles,
            Analysis = bound.Analysis,
        };
    }

    // --- Phase 2: concretize (S§5) ---

    private static void Concretize(PlanContext ctx)
    {
        var g = ctx.Graph;
        var L = g.Lattice;
        var analysis = ctx.Analysis!;
        var exclusive = g.Flows.Where(f => f.UncheckedReason != null).SelectMany(f => f.ExclusiveNodes).ToHashSet();

        foreach (var n in g.Nodes.OrderBy(n => n.Index))
        {
            var profile = ctx.Profile(n);
            var dift = ctx.Dift[n.Name];
            var always = g.Policies.Always.Where(a => a.Node == n.Name).ToList();
            bool nodeAlways = always.Any(a => a.Instance == null);

            var held = analysis.Nodes[n.Name];
            var constraint = analysis.Placement.First(c => c.Node == n.Name);
            string? placement = constraint.MinHostLabel;
            bool compartmentBound = false;
            IReadOnlyList<string>? anyOf = null;
            if (placement == null && !exclusive.Contains(n.Name) && L.IsForbidden(L.IndexOf(held.Held)))
            {
                placement = held.InLo;          // S§6: the forbidden-top case
                compartmentBound = true;
                anyOf = CompartmentAlternatives(ctx, n);
            }

            var nodePin = g.Policies.Pins.FirstOrDefault(p => p.Node == n.Name && p.Instance == null)?.Pattern;
            bool interchangeable = !g.Policies.Pins.Any(p => p.Node == n.Name && p.Instance != null)
                && !(g.Labels.InstanceCeilingOverrides.TryGetValue(n.Name, out var ov) && ov.Count > 0);

            var command = n.Process.Argv![0];
            var demand = profile.Demand ?? ctx.Options.DefaultDemand;
            if (dift.Mode == DiftMode.Enabled) demand = demand.Scale(profile.IdmCpuFactor ?? 1.5);   // H3: IDM overhead

            // A group per lane for laned nodes (S§4.5), else one per node. `partitions` bounds apply per group.
            var lanes = dift.Lanes.Count > 0 ? dift.Lanes.Cast<int?>().ToList() : new List<int?> { null };
            foreach (var lane in lanes)
            {
                var group = new GroupDraft { Node = n, Always = nodeAlways, Interchangeable = interchangeable, LaneName = lane is int l ? L.Name(l) : null };
                ctx.Groups.Add(group);

                // Instance counts (S§5.1): nmin = max(a, liveness minimum, rate minimum), clamped to b.
                int a = n.Instances.Min, b = n.Instances.Max;
                // A re-plan keeps (or sets, when scaling) each keyless group's current size.
                if (ctx.Current != null && n.Partitioning == PartitionKind.Keyless)
                {
                    int size = ctx.Replan?.GroupCounts?.GetValueOrDefault(group.Name)
                        ?? ctx.Current.Agents.Count(x => x.NodeName == n.Name && x.LaneLabel == group.LaneName && x.Role == AgentRole.Primary);
                    if (size > 0) { a = b = Math.Clamp(size, 1, n.Instances.Max); }
                }
                int nmin = a;
                if (n.Partitioning == PartitionKind.Keyless)
                {
                    if (group.Always) nmin = Math.Max(nmin, 2);
                    var rateMin = RateMinimum(ctx, n, profile, lane is int ln ? LaneShare(ctx, n, dift.Lanes, ln, profile) : 1);
                    if (rateMin > nmin) nmin = rateMin;
                    if (nmin > b)
                    {
                        ctx.Warn("SPW01", $"'{group.Name}' needs {nmin} instances but partitions allows at most {b}; clamped to {b}");
                        nmin = b;
                    }
                    else if (nmin > a)
                        ctx.Warn("SPW01", $"minimum instance count of '{group.Name}' raised from {a} to {nmin}"
                            + (group.Always && nmin == 2 ? " (always)" : " (min_rate)"));
                }
                else
                {
                    var rateMin = RateMinimum(ctx, n, profile, 1);
                    if (rateMin > b) ctx.Warn("SPW01", $"'{n.Name}' would need {rateMin} instances for its min_rate goal but has a fixed count of {b}");
                }
                group.Min = nmin;
                group.Max = b;

                // A lane-λ instance holds at most λ ⊔ ext, so it is placed by λ ⊔ SrcStatic (S§6).
                var agentPlacement = lane is int lam ? L.Name(L.Join(lam, dift.SourceJoin ?? L.BottomIndex)) : placement;
                for (int i = 0; i < b; i++)
                {
                    var agent = new AgentDraft
                    {
                        Node = n, Index = i, Group = group, Candidate = i >= nmin, Demand = demand, Command = command, Lane = lane,
                        Pin = g.Policies.Pins.FirstOrDefault(p => p.Node == n.Name && p.Instance == i)?.Pattern ?? nodePin,
                        PlacementLabel = agentPlacement, CompartmentBound = lane == null && compartmentBound, PlacementAnyOf = lane == null ? anyOf : null,
                    };
                    if (n.Partitioning == PartitionKind.Keyed)
                    {
                        // Consistent with L§6.4 routing (instance = floor(g·n/G)); see plan.md decisions.
                        var (from, to) = Sidecar.KeyRouter.OwnedGroups(i, b, n.KeyGroups);
                        agent.OwnedKeys = new KeyGroupRange(from, to);
                    }
                    group.Agents.Add(agent);
                    ctx.Agents.Add(agent);
                }

                // Standbys: stateful nodes with `always` (L§9.3); `always: n[i]` covers instance i only.
                // Keyless lanes get extra instances rather than standbys; a keyed node's compartment lanes
                // (F3) get a standby per instance, placed like its primary.
                if (n.Partitioning != PartitionKind.Keyless)
                    foreach (var p in group.Agents.Where(p => group.Always || always.Any(x => x.Instance == p.Index)))
                    {
                        var s = new AgentDraft
                        {
                            Node = n, Index = p.Index, Role = AgentRole.Standby, Primary = p, Group = group, Demand = demand, Command = command,
                            Pin = p.Pin, PlacementLabel = p.PlacementLabel, CompartmentBound = p.CompartmentBound, PlacementAnyOf = p.PlacementAnyOf,
                            OwnedKeys = p.OwnedKeys, Lane = p.Lane,
                        };
                        p.Standby = s;
                        ctx.Agents.Add(s);
                    }
            }
        }

        // Pipes (S§5.2): one per (edge, sender agent, receiver agent); standbys excluded. Into a laned
        // receiver, a pipe exists only if some label it may carry fits under the receiver's lane.
        var restricted = new Dictionary<(string Port, int Lane), IReadOnlyList<string>>();
        IReadOnlyList<string> SenderSet(AgentDraft s, GraphEdge e)
        {
            if (s.Lane is not int mu) return analysis.Ports[e.Source].PossibleLabels;
            if (restricted.TryGetValue((e.Source, mu), out var cached)) return cached;
            // A laned sender's output set comes from the input labels at or below its lane.
            var inputs = analysis.Nodes[s.Node.Name].InputSet.Select(L.IndexOf).Where(x => L.Leq(x, mu));
            var baseSet = LabelAnalysis.BaseSet(g, s.Node, inputs, ctx.Dift[s.Node.Name].Augmentation, false, perMessage: !g.Port(e.Source).IsStderr);
            var set = LabelAnalysis.OutputSetFromBase(g, g.Args, g.Port(e.Source), baseSet).OrderBy(x => x).Select(L.Name).ToList();
            return restricted[(e.Source, mu)] = set;
        }

        foreach (var e in g.Edges.OrderBy(e => e.Index))
        {
            var senders = ctx.Groups.Where(x => x.Node.Name == e.SourceNode).SelectMany(x => x.Agents).ToList();
            var receivers = ctx.Groups.Where(x => x.Node.Name == e.DestinationNode).SelectMany(x => x.Agents).ToList();
            var destNode = g.Node(e.DestinationNode);
            var flows = g.Flows.Where(f => f.Edges.Contains(e.Name)).Select(f => f.Name).ToList();
            bool isUnchecked = g.Flows.Any(f => f.UncheckedReason != null && f.Edges.Contains(e.Name));
            var routing = e.Op == Language.Ast.EdgeOp.AllPartitions ? RoutingMode.Broadcast : destNode.Partitioning switch
            {
                PartitionKind.Unpartitioned => RoutingMode.Direct,
                PartitionKind.Keyed => RoutingMode.Keyed,
                _ => RoutingMode.LoadBalanced,
            };
            foreach (var s in senders)
            {
                var sent = SenderSet(s, e);
                foreach (var r in receivers)
                {
                    // A -*> copy to a lane that can't admit it is dropped by the lane ceiling (S§5.2).
                    var allowed = r.Lane is int lam && routing != RoutingMode.Broadcast
                        ? sent.Where(x => L.Leq(L.IndexOf(x), lam)).ToList() : sent;
                    if (r.Lane != null && allowed.Count == 0) continue;
                    ctx.Pipes.Add(new PipeDraft
                    {
                        Edge = e, Source = s, Destination = r, Routing = routing, Allowed = allowed, Unchecked = isUnchecked, Flows = flows,
                        DestinationKeys = routing == RoutingMode.Keyed ? r.OwnedKeys : null,
                    });
                }
            }
        }

        // Stream ids: deterministic within each receiving port, by edge declaration index then sender index.
        foreach (var port in ctx.Pipes.GroupBy(p => (p.Destination, p.Edge.DestinationPort)))
        {
            int id = 0;
            foreach (var p in port.OrderBy(p => p.Edge.Index).ThenBy(p => p.Source.Group.Name, StringComparer.Ordinal).ThenBy(p => p.Source.Index)) p.StreamId = id++;
        }
    }

    // The share of a node's traffic whose lowest covering lane is `lane` (S§5.1), from the profile's
    // label frequencies on its input ports, or uniform over the possible input labels.
    private static double LaneShare(PlanContext ctx, GraphNode n, IReadOnlyList<int> lanes, int lane, NodeProfile profile)
    {
        var L = ctx.Graph.Lattice;
        var labels = n.Inputs.SelectMany(p => ctx.Analysis!.Ports[p.Ref].PossibleLabels).Distinct().ToList();
        var freq = n.Inputs.Select(p => profile.LabelFrequency?.GetValueOrDefault(p.Name)).FirstOrDefault(f => f != null);
        double total = 0, share = 0;
        foreach (var l in labels)
        {
            double f = freq?.GetValueOrDefault(l) ?? 1.0 / labels.Count;
            total += f;
            int x = L.IndexOf(l);
            var lowest = lanes.FirstOrDefault(c => L.Leq(x, c), -1);
            if (lowest == lane) share += f;
        }
        return total > 0 ? share / total : 0;
    }

    // Placement for a node whose held label is the forbidden top (L§8.6 step 4 leaves it to the
    // implementation): each instance ends up holding one compartment's data, so its host must be able
    // to hold everything the node may receive from at least one compartment. Without this, an instance
    // could land on a host where every delivery fails the host check.
    private static IReadOnlyList<string>? CompartmentAlternatives(PlanContext ctx, GraphNode n)
    {
        var L = ctx.Graph.Lattice;
        var info = ctx.Analysis!.Nodes[n.Name];
        var held = info.InputSet.Concat(info.BaseSet);
        // A labelled source holds what it emits (user decision, see plan.md), so its alternatives come from
        // its outputs: it must run on a host that can hold at least one compartment's data (F2).
        if (n.IsSource)
            held = held.Concat(n.Outputs.SelectMany(p => ctx.Analysis.Ports.TryGetValue(p.Ref, out var pl) ? pl.PossibleLabels : Array.Empty<string>()));
        var labels = held.Select(L.IndexOf).Where(x => !L.IsForbidden(x) && L.Compartments[x] >= 0);
        var perCompartment = labels.GroupBy(x => L.Compartments[x]).OrderBy(c => c.Key).Select(c => L.Name(L.JoinAll(c))).ToList();
        return perCompartment.Count > 0 ? perCompartment : null;
    }

    // Rate minimum (S§5.1): ceil(r / ThroughputPerInstance) for aggregate min_rate goals into the node.
    private static int RateMinimum(PlanContext ctx, GraphNode n, NodeProfile profile, double share)
    {
        if (profile.ThroughputPerInstance is not double thr || thr <= 0) return 0;
        double rate = 0;
        foreach (var r in ctx.Graph.Policies.MinRates)
        {
            bool intoNode = r.Target.Kind switch
            {
                TargetKind.Edge => ctx.Graph.Edge(r.Target.Name)?.DestinationNode == n.Name,
                TargetKind.Port => r.Target.Name == n.Name && n.Inputs.Any(p => p.Name == r.Target.Port),
                _ => false,
            };
            if (intoNode) rate = Math.Max(rate, r.MessagesPerSecond);
        }
        return rate > 0 ? (int)Math.Ceiling(rate * share / thr) : 0;
    }

    // --- Phase 3: bind policies (S§6) ---

    private static void BindPolicies(PlanContext ctx)
    {
        var g = ctx.Graph;

        // min_rate → per-pipe message rates; the target's pipe group shares the goal evenly.
        foreach (var r in g.Policies.MinRates)
        {
            IEnumerable<IGrouping<object, PipeDraft>> groups = r.Target.Kind switch
            {
                TargetKind.Edge => ctx.Pipes.Where(p => p.Edge.Name == r.Target.Name).GroupBy(_ => (object)"edge"),
                TargetKind.Port => PortPipes(ctx, r.Target.Name, r.Target.Port!).GroupBy(_ => (object)"port"),
                TargetKind.PortAllInstances => PortPipes(ctx, r.Target.Name, r.Target.Port!).GroupBy(p => (object)AgentOnPort(p, r.Target.Name)),
                TargetKind.PortInstance => PortPipes(ctx, r.Target.Name, r.Target.Port!).Where(p => AgentOnPort(p, r.Target.Name).Index == r.Target.Instance)
                    .GroupBy(_ => (object)"instance"),
                _ => Enumerable.Empty<IGrouping<object, PipeDraft>>(),
            };
            foreach (var grp in groups)
            {
                var pipes = grp.ToList();
                int nominal = Math.Max(1, pipes.Count(p => !p.Source.Candidate && !p.Destination.Candidate));
                foreach (var p in pipes) p.Rate = Math.Max(p.Rate ?? 0, r.MessagesPerSecond / nominal);
            }
        }

        // Profiled output rates for pipes without a min_rate goal: routed edges split the rate, broadcasts copy it.
        foreach (var p in ctx.Pipes.Where(p => p.Rate == null))
        {
            var profile = ctx.Profile(p.Source.Node);
            if (profile.OutputRate?.TryGetValue(p.Edge.SourcePort, out var rate) != true) continue;
            int receivers = Math.Max(1, p.Destination.Group.Min);
            p.Rate = p.Routing == RoutingMode.Broadcast ? rate : rate / receivers;
        }
        foreach (var p in ctx.Pipes.Where(p => p.Rate != null))
        {
            var profile = ctx.Profile(p.Source.Node);
            long size = profile.MessageSize?.TryGetValue(p.Edge.SourcePort, out var s) == true ? s : ctx.Options.DefaultMessageSize;
            p.Bandwidth = (long)Math.Ceiling(p.Rate!.Value * size);
        }

        // max_latency → latency paths (S1). Flows use their node-level paths; an edge goal is a one-edge path.
        foreach (var f in g.Flows.Where(f => f.MaxLatencyNanos != null))
            foreach (var path in f.EdgePaths)
                ctx.LatencyPaths.Add(MakePath(ctx, f.Name, path.Select(e => g.Edge(e)!).ToList(), f.MaxLatencyNanos!.Value, f.LatencyPercentile ?? 99));
        foreach (var l in g.Policies.MaxLatencies.Where(l => l.Target.Kind == TargetKind.Edge))
            ctx.LatencyPaths.Add(MakePath(ctx, "edge:" + l.Target.Name, new List<GraphEdge> { g.Edge(l.Target.Name)! }, l.MaxNanos, l.Percentile));

        // A pipe on several goals uses the highest percentile among them (S§7.4).
        foreach (var path in ctx.LatencyPaths)
            foreach (var p in ctx.Pipes.Where(p => path.Edges.Contains(p.Edge)))
                p.LatencyPercentile = Math.Max(p.LatencyPercentile ?? 0, path.Percentile);
    }

    private static IEnumerable<PipeDraft> PortPipes(PlanContext ctx, string node, string port) =>
        ctx.Pipes.Where(p => (p.Edge.DestinationNode == node && p.Edge.DestinationPort == port) || (p.Edge.SourceNode == node && p.Edge.SourcePort == port));

    private static AgentDraft AgentOnPort(PipeDraft p, string node) => p.Edge.DestinationNode == node ? p.Destination : p.Source;

    private static LatencyPath MakePath(PlanContext ctx, string owner, List<GraphEdge> edges, long boundNanos, double pct)
    {
        long fixedMicros = 0;
        // Processing latency of interior nodes: every node on the path except the first and last.
        foreach (var e in edges.Take(edges.Count - 1))
        {
            var profile = ctx.Profile(ctx.Graph.Node(e.DestinationNode));
            fixedMicros += profile.ProcessingLatencyMicros ?? 0;
            if (ctx.Dift[e.DestinationNode].Mode == DiftMode.Enabled) fixedMicros += profile.IdmLatencyMicros ?? 0;
        }
        // Lateness of the ordered input ports on the path, including the exit port.
        foreach (var e in edges)
        {
            var port = ctx.Graph.Port(e.Destination);
            if (port.Order?.Kind == OrderKind.Ordered && port.Ordering != null) fixedMicros += port.Ordering.Default.LatenessNanos / 1000;
        }
        return new LatencyPath { Owner = owner, Edges = edges, BoundMicros = boundNanos / 1000, Percentile = pct, FixedMicros = fixedMicros };
    }

    // --- H2: eligibility (S§7.3) ---

    private static void ComputeEligibility(PlanContext ctx)
    {
        var L = ctx.Graph.Lattice;
        for (int h = 0; h < ctx.HostCount; h++)
        {
            var host = ctx.Host(h);
            var hostLabel = HostLabel(ctx, host, out var labelNote);
            foreach (var a in ctx.Agents)
            {
                string? reason = null;
                if (!host.Alive) reason = "not alive";
                else if (host.MaxAgents == 0) reason = "MaxAgents is 0";
                else if (host.Executables != null && !host.Executables.ContainsKey(a.Command) && !host.Executables.ContainsKey(System.IO.Path.GetFileName(a.Command)))
                    reason = $"missing executable '{a.Command}'";
                else if (ctx.Dift[a.Node.Name].Mode == DiftMode.Enabled && !host.IdmSupport.Contains(ctx.Dift[a.Node.Name].LanguageRuntime))
                    reason = $"no IDM support for '{ctx.Dift[a.Node.Name].LanguageRuntime}' (the node runs with DIFT enabled)";
                else if (a.Pin != null && !GlobMatch(a.Pin, host.Name))
                    reason = $"name '{host.Name}' does not match pin '{a.Pin}'";
                else if (ctx.Options.LabelAwarePlacement && a.PlacementLabel != null && !L.Leq(L.IndexOf(a.PlacementLabel), hostLabel))
                    reason = $"label {L.Name(hostLabel)}{labelNote} is below the placement label {a.PlacementLabel}";
                else if (ctx.Options.LabelAwarePlacement && a.PlacementAnyOf != null && !a.PlacementAnyOf.Any(x => L.Leq(L.IndexOf(x), hostLabel)))
                    reason = $"label {L.Name(hostLabel)}{labelNote} can't hold any compartment's data ({string.Join(" or ", a.PlacementAnyOf)})";
                else if (a.Demand.CpuMillis > host.Free.CpuMillis || a.Demand.MemoryBytes > host.Free.MemoryBytes)
                    reason = $"not enough free capacity (needs {Format(a.Demand)}, has {Format(host.Free)})";

                if (reason == null) a.Eligible.Add(h);
                else a.Excluded[h] = reason;
            }
        }
    }

    // L(h): the host's label in the graph's lattice. A host without a label, or with a label this
    // graph's lattice doesn't know, counts as ⊥, which is the conservative choice.
    internal static int HostLabel(PlanContext ctx, HostRuntimeInfo host, out string note)
    {
        var L = ctx.Graph.Lattice;
        note = "";
        if (host.Label == null) return L.BottomIndex;
        if (L.Contains(host.Label) && !L.Synthetic[L.IndexOf(host.Label)]) return L.IndexOf(host.Label);
        note = $" ('{host.Label}' is not a label of this graph; treated as ⊥)";
        return L.BottomIndex;
    }

    // `pin` patterns are host-name globs with `*` and `?` (L§7.2).
    public static bool GlobMatch(string pattern, string name) =>
        Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$");

    private static string Format(ResourceVector r) => $"{r.CpuMillis}m CPU, {r.MemoryBytes >> 20} MiB";

    // --- Early infeasibility checks (S§8.2) ---

    private static void EarlyChecks(PlanContext ctx)
    {
        var errors = new List<SpawnDiagnostic>();
        foreach (var a in ctx.Agents.Where(a => a.Eligible.Count == 0 && !a.Candidate))
        {
            var details = Enumerable.Range(0, ctx.HostCount).Select(h => $"{ctx.Host(h).HostId}: {a.Excluded[h]}").ToList();
            if (ctx.HostCount == 0) details.Add("the cluster snapshot has no hosts");
            errors.Add(new SpawnDiagnostic("SP004", Severity.Error, $"no eligible host for {a.Describe}", details));
        }
        if (errors.Count > 0) throw new SchedulingException(errors.Concat(ctx.Warnings).ToList());

        var mandatory = ctx.Agents.Where(a => !a.Candidate && (a.Role == AgentRole.Primary || ctx.Options.ReserveStandbyCapacity)).ToList();
        var need = mandatory.Aggregate(ResourceVector.Zero, (acc, a) => acc.Add(a.Demand));
        var free = ctx.Snapshot.Hosts.Where(h => h.Alive).Aggregate(ResourceVector.Zero, (acc, h) => acc.Add(h.Free));
        if (need.CpuMillis > free.CpuMillis || need.MemoryBytes > free.MemoryBytes)
            throw new SchedulingException("SP006", "total demand exceeds total free capacity",
                $"demand: {Format(need)}", $"free: {Format(free)}");
    }

    // --- §9.1 finalize steps 1–3 ---

    private static void Finalize(PlanContext ctx)
    {
        // 1. Drop inactive candidates and their pipes; renumber each group's active agents.
        ctx.Agents.RemoveAll(a => !a.Active);
        ctx.Pipes.RemoveAll(p => !p.Source.Active || !p.Destination.Active);
        foreach (var grp in ctx.Groups)
        {
            int i = 0;
            foreach (var a in grp.Agents.Where(a => a.Active)) a.FinalIndex = i++;
            grp.Agents.RemoveAll(a => !a.Active);
        }
        foreach (var a in ctx.Agents)
        {
            if (a.Role == AgentRole.Standby) a.FinalIndex = a.Primary!.FinalIndex;
            a.Id = $"{ctx.GraphInstanceId}/{a.Group.Name}/{a.FinalIndex}" + (a.Role == AgentRole.Standby ? "/standby" : "");
        }

        // Stream ids stay dense after dropping candidates: per receiving port, by edge declaration index,
        // then sender group and instance (S§5.2).
        foreach (var port in ctx.Pipes.GroupBy(p => (p.Destination, p.Edge.DestinationPort)))
        {
            int id = 0;
            foreach (var p in port.OrderBy(p => p.Edge.Index).ThenBy(p => ctx.Groups.IndexOf(p.Source.Group)).ThenBy(p => p.Source.FinalIndex)) p.StreamId = id++;
        }

        // 2. Native channels for co-located pipes (A5).
        foreach (var p in ctx.Pipes) p.Native = p.Source.Host == p.Destination.Host;

        // 3. Check elision: skip a delivery check only when it is proven to pass for every allowed label.
        var g = ctx.Graph;
        var L = g.Lattice;
        foreach (var p in ctx.Pipes)
        {
            var allowed = p.Allowed.Select(L.IndexOf).ToList();
            var elided = ElidedChecks.None;

            var portCeiling = g.Labels.PortCeilings.GetValueOrDefault(p.Edge.Destination);
            if (portCeiling == null || Below(allowed, L.MeetAll(LabelAnalysis.Range(g, g.Args, portCeiling, null)), L))
                elided |= ElidedChecks.PortCeiling;

            var instanceCeiling = InstanceCeiling(g, p.Destination);
            var dift = ctx.Dift[p.Destination.Node.Name];
            bool uncheckedExclusive = g.Flows.Any(f => f.UncheckedReason != null && f.ExclusiveNodes.Contains(p.Destination.Node.Name));
            if ((instanceCeiling == null || Below(allowed, L.MeetAll(LabelAnalysis.Range(g, g.Args, instanceCeiling, null)), L))
                && (dift.ExternalSinkCeiling is not int cExt || uncheckedExclusive || Below(allowed, cExt, L))
                && (p.Destination.Lane is not int lane || Below(allowed, lane, L)))
                elided |= ElidedChecks.InstanceCeiling;

            if (Below(allowed, HostLabel(ctx, ctx.Host(p.Destination.Host), out _), L))
                elided |= ElidedChecks.Host;

            if (!L.ForbiddenTop || (p.Destination.PlacementLabel != null && !p.Destination.CompartmentBound
                && L.SameCompartment(allowed.Append(L.IndexOf(p.Destination.PlacementLabel)))))
                elided |= ElidedChecks.Compartment;

            p.Elided = elided;
        }

        // 4. Bypass edges (plan 6.5b).
        MarkBypass(ctx);
    }

    // An edge whose messages need nothing from the sidecar carries its sender's byte stream as it is, over
    // a raw pipe: nothing segments, labels, checks, orders or routes it (plan step 6.5b, Q6.3). Its stream
    // carries one label, joined into the receiver's taint when the stream connects. The edge qualifies when
    //   - both processes use typed streams (not @json_lines);
    //   - it is one-to-one: unpartitioned sender and receiver, the only edge from its port and into its port;
    //   - nothing orders it: no ordered/sequenced port, order promise or sequence numbers;
    //   - no label work is left: the sending port's messages all carry the same one label (no dynamic node,
    //     no per-message labeller, not strict mode), every delivery check is elided or the edge unchecked,
    //     and the pipe allows that label;
    //   - its receiver has no standby (checkpoints need the sidecar's barrier; user decision);
    //   - no latency flow measures it (flows need per-message entry times).
    // Unsegmentable edges (framing `none`) are always bypass edges: the compiler already ensured the shape
    // (E0412, E0413, E0515); a label check that placement couldn't make static is an error (SP013).
    private static void MarkBypass(PlanContext ctx)
    {
        var g = ctx.Graph;
        var L = g.Lattice;
        var errors = new List<SpawnDiagnostic>();
        foreach (var edge in ctx.Pipes.GroupBy(p => p.Edge))
        {
            var e = edge.Key;
            var src = g.Node(e.SourceNode);
            var dst = g.Node(e.DestinationNode);
            var srcPort = g.Port(e.Source);
            var dstPort = g.Port(e.Destination);
            var pipes = edge.ToList();
            var pl = ctx.Analysis!.Ports.TryGetValue(e.Source, out var info) ? info.PossibleLabels : new List<string>();

            bool LabelsStatic() => pipes.All(p => (p.Unchecked || p.Elided == (ElidedChecks.PortCeiling | ElidedChecks.InstanceCeiling | ElidedChecks.Host | ElidedChecks.Compartment))
                && pl.All(p.Allowed.Contains)
                && pl.Count > 0 && !L.IsForbidden(L.JoinAll(pl.Select(L.IndexOf))));
            bool typedStreams = !src.JsonLines && !dst.JsonLines;

            if (!srcPort.Framing.Segmentable)
            {
                if (!typedStreams) continue;   // a @json_lines side frames each message as a line
                if (!LabelsStatic())
                {
                    errors.Add(new SpawnDiagnostic("SP013", Severity.Error,
                        $"edge '{e.Name}' carries an unsegmentable stream, but its label check can't be made static with this placement",
                        pipes.Select(p => $"{p.Source.Describe} -> {p.Destination.Describe}: allowed {{{string.Join(", ", p.Allowed)}}}, possible {{{string.Join(", ", pl)}}}, elided {p.Elided}").ToArray()));
                    continue;
                }
                foreach (var p in pipes) p.Bypass = true;
                continue;
            }

            var labeller = g.Labels.DataLabellers.GetValueOrDefault(e.Source);
            bool bypass = ctx.Options.BypassEdges && typedStreams
                && pipes.Count == 1
                && src.Partitioning == PartitionKind.Unpartitioned && dst.Partitioning == PartitionKind.Unpartitioned
                && g.Edges.Count(x => x.Source == e.Source) == 1 && g.Edges.Count(x => x.Destination == e.Destination) == 1
                && srcPort.Order == null && dstPort.Order == null && srcPort.Sequence == null && dstPort.Sequence == null
                && !L.Strict && !g.Labels.Dynamic.ContainsKey(src.Name)
                && (labeller == null || labeller.Kind == LabelSpecKind.Constant)
                && pl.Count == 1 && LabelsStatic()
                && pipes[0].Destination.Standby == null
                && !g.Flows.Any(f => f.Edges.Contains(e.Name) && f.MaxLatencyNanos != null);
            if (bypass) pipes[0].Bypass = true;
        }
        if (errors.Count > 0) throw new SchedulingException(errors.Concat(ctx.Warnings).ToList());
    }

    private static bool Below(IEnumerable<int> xs, int bound, LabelLattice L) => xs.All(x => L.Leq(x, bound));

    // §9.1 step 4: an initial routing table per sender agent and load-balanced edge (S§10.2), with target
    // shares from the planned latencies and label frequencies from profiles (uniform when unknown).
    private static List<RoutingTableInfo> RoutingTables(PlanContext ctx)
    {
        var tables = new List<RoutingTableInfo>();
        var g = ctx.Graph;
        foreach (var grp in ctx.Pipes.Where(p => p.Routing == RoutingMode.LoadBalanced).GroupBy(p => (p.Source, p.Edge)).OrderBy(x => x.Key.Edge.Index).ThenBy(x => x.Key.Source.Id, StringComparer.Ordinal))
        {
            var (sender, edge) = grp.Key;
            var pipes = grp.OrderBy(p => p.Destination.Id, StringComparer.Ordinal).ToList();
            var labels = pipes.SelectMany(p => p.Allowed).Distinct().OrderBy(g.Lattice.IndexOf).ToList();
            var profile = ctx.Profile(sender.Node).LabelFrequency?.GetValueOrDefault(edge.SourcePort);
            double mass = labels.Sum(l => profile?.GetValueOrDefault(l) ?? 1.0);
            var freq = labels.ToDictionary(l => l, l => mass > 0 ? (profile?.GetValueOrDefault(l) ?? 1.0) / mass : 1.0 / labels.Count);
            var receivers = pipes.Select(p => p.Destination.Id).ToList();
            var shares = LoadBalancer.SharesFromLatency(pipes.ToDictionary(p => p.Destination.Id, p => p.ExpectedLatencyMicros ?? ctx.Options.LocalLatencyMicros));
            int c = g.Policies.Always.Any(a => a.Node == edge.DestinationNode && a.Instance == null) ? 2 : 1;
            var problem = new LoadBalanceProblem(sender.Id, edge.SourcePort, edge.Name, labels, freq, receivers,
                pipes.ToDictionary(p => p.Destination.Id, p => (IReadOnlySet<string>)p.Allowed.ToHashSet()), shares, c);
            tables.Add(LoadBalancer.Solve(problem, "spawn", ctx.Options.LbSolveBudget));
        }
        return tables;
    }

    private static LabelSpec? InstanceCeiling(CompiledGraph g, AgentDraft a) =>
        g.Labels.InstanceCeilingOverrides.TryGetValue(a.Node.Name, out var ov) && ov.TryGetValue(a.FinalIndex, out var spec)
            ? spec : g.Labels.InstanceCeilings.GetValueOrDefault(a.Node.Name);

    // --- Records (S§11) ---

    private static GraphInstanceInfo BuildRecords(PlanContext ctx, PlacementResult result)
    {
        var g = ctx.Graph;
        var gid = ctx.GraphInstanceId;
        var flowsOf = g.Flows.ToDictionary(f => f.Name, f => f.Edges.SelectMany(e => new[] { g.Edge(e)!.SourceNode, g.Edge(e)!.DestinationNode }).ToHashSet());

        var agents = ctx.Agents.OrderBy(a => a.Node.Index).ThenBy(a => ctx.Groups.IndexOf(a.Group)).ThenBy(a => a.FinalIndex).ThenBy(a => a.Role).Select(a =>
        {
            var n = a.Node;
            var d = ctx.Dift[n.Name];
            bool uncheckedExclusive = g.Flows.Any(f => f.UncheckedReason != null && f.ExclusiveNodes.Contains(n.Name));
            var ports = n.Ports.Select(p => PortInfo(ctx, p)).ToList();
            return new GraphAgentInfo(a.Id, gid, n.Name, a.FinalIndex, a.Role,
                a.Primary?.Id, a.Standby?.Id, n.Partitioning, n.Stateless,
                a.Command, n.Process.Argv!, n.OneToOne,
                n.Partitioning == PartitionKind.Keyed ? n.KeyDomains : null, n.KeyGroups, a.OwnedKeys, a.Lane is int ln ? g.Lattice.Name(ln) : null,
                ports,
                d.Class, d.Mode, d.Rule, d.Idm, d.SourceJoin is int sj ? g.Lattice.Name(sj) : null, d.OutputEvaluable, d.InternalMonitorDisabled,
                InstanceCeiling(g, a), d.ExternalSinkCeiling is int ce && !uncheckedExclusive ? g.Lattice.Name(ce) : null,
                a.PlacementLabel, a.PlacementAnyOf, a.CompartmentBound,
                g.Labels.Dynamic.GetValueOrDefault(n.Name), g.Labels.Host.GetValueOrDefault(n.Name),
                uncheckedExclusive,
                a.Pin, a.Demand, ctx.Host(a.Host).HostId,
                flowsOf.Where(kv => kv.Value.Contains(n.Name)).Select(kv => $"{gid}/{kv.Key}").ToList(),
                AgentState.Pending, 1);
        }).ToList();

        int pipeNo = 0;
        var pipes = ctx.Pipes.OrderBy(p => p.Edge.Index).ThenBy(p => p.Source.FinalIndex).ThenBy(p => p.Destination.FinalIndex).Select(p =>
            new GraphPipeInfo($"{gid}/pipe/{pipeNo++}", gid, p.Edge.Name, p.Edge.Implicit,
                p.Source.Id, p.Edge.SourcePort, p.Destination.Id, p.Edge.DestinationPort,
                p.Routing, p.Edge.KeyPaths, p.DestinationKeys, p.StreamId, p.Edge.ForwardEligible,
                p.Allowed, p.Elided, p.Native, p.Unchecked, p.Flows.Select(f => $"{gid}/{f}").ToList(),
                p.Rate is double r ? (long)Math.Ceiling(r) : null, p.Bandwidth,
                ctx.Host(p.Source.Host).HostId, ctx.Host(p.Destination.Host).HostId, p.ExpectedLatencyMicros,
                PipeState.Pending, 1, p.Bypass)).ToList();

        var flows = g.Flows.Select(f => new FlowInfo($"{gid}/{f.Name}", f.Name, f.Edges, f.NodePaths, f.Mode, f.ClockField,
            f.MaxLatencyNanos is long ns ? TimeSpan.FromTicks(ns / 100) : null, f.LatencyPercentile, f.UncheckedReason, f.ExclusiveNodes,
            ctx.LatencyPaths.Where(p => p.Owner == f.Name).Select(p => p.ExpectedMicros).ToList())).ToList();

        // DIFT report (S§9.1 step 5), with the number of elided delivery checks per incoming pipe.
        var dift = g.Nodes.Select(n =>
        {
            var d = ctx.Dift[n.Name];
            var elided = pipes.Where(p => p.DestinationAgentId.StartsWith($"{gid}/{n.Name}/", StringComparison.Ordinal) || p.DestinationAgentId.StartsWith($"{gid}/{n.Name}@", StringComparison.Ordinal))
                .ToDictionary(p => p.PipeId, p => System.Numerics.BitOperations.PopCount((uint)p.Elided));
            return new DiftReportEntry(n.Name, d.Class, d.Mode, d.Rule, d.ExternalSinkCeiling is int c ? g.Lattice.Name(c) : null,
                d.Lanes.Select(g.Lattice.Name).ToList(), d.TrustedLabellers,
                d.Findings.Concat(ctx.Warnings.Where(w => w.Code == "SPW07" && w.Message.Contains($"'{n.Name}'"))).ToList(), elided);
        }).ToList();

        var usedHosts = ctx.Agents.Select(a => a.Host).Distinct().ToList();
        var plan = new DeploymentPlan(ctx.Snapshot.Version, usedHosts.ToDictionary(h => ctx.Host(h).HostId, h => ctx.Host(h).Version), result.Status, result.ObjectiveValue, result.BestBound, result.Terms,
            ctx.Relaxed, ctx.Warnings,
            new AuditReports(ctx.Analysis!.Declassifications, ctx.Analysis.UncheckedFlows, dift)) { EnforceDeliveryChecks = ctx.Options.EnforceDeliveryChecks };

        return new GraphInstanceInfo(gid, g.Name, g.Args!, g.Lattice, flows, agents, pipes, RoutingTables(ctx), plan,
            GraphInstanceState.Planned, DateTimeOffset.UtcNow, 1);
    }

    private static AgentPortInfo PortInfo(PlanContext ctx, GraphPort p)
    {
        var g = ctx.Graph;
        var o = p.Ordering?.Default;
        TimeSpan? Ns(long? v) => v is long x ? TimeSpan.FromTicks(x / 100) : null;
        return new AgentPortInfo(p.Name, p.Direction, p.Type.ToString(), p.Order,
            p.Order?.Kind == OrderKind.Ordered ? Ns(o?.LatenessNanos) : null, Ns(o?.IdleTimeoutNanos), Ns(o?.GapTimeoutNanos),
            o?.MaxBufferBytes ?? AppCompiler.DefaultMaxBufferBytes, o?.OnLate, o?.OnLateRoutePort,
            g.Labels.PortCeilings.GetValueOrDefault(p.Ref), g.Labels.DataLabellers.GetValueOrDefault(p.Ref),
            ctx.Analysis!.Ports.TryGetValue(p.Ref, out var pl) ? pl.PossibleLabels : new List<string>(),
            p.Sequence?.Origin ?? false, p.Sequence?.Propagating ?? false, p.Framing);
    }
}
