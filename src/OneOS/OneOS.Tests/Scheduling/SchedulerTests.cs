using MessagePack;
using OneOS.Runtime;
using OneOS.Runtime.Language;
using OneOS.Runtime.Language.Models;
using OneOS.Runtime.Scheduling;
using OneOS.Tests.Language;

namespace OneOS.Tests.Scheduling;

internal sealed class FixedCluster : IClusterProvider
{
    public ClusterSnapshot Snapshot { get; set; } = ClusterSnapshot.Empty;
    public ClusterSnapshot TakeSnapshot() => Snapshot;
}

internal static class Sched
{
    public static HostRuntimeInfo Host(string name, string? label = null, string? zone = null, long cpu = 4000, long memMb = 8192,
        bool alive = true, string[]? exes = null, int? maxAgents = null) =>
        new(name, name, zone, new List<string>(), label, alive, new ResourceVector(cpu, memMb << 20), ResourceVector.Zero,
            exes?.ToDictionary(e => e, _ => ""), new List<string>(), maxAgents, 1);

    public static ClusterSnapshot Cluster(params HostRuntimeInfo[] hosts) => new(1, hosts, new List<NetworkQuality>());

    public static SchedulerOptions Options => new() { NewGraphInstanceId = () => "g1", SolverTimeLimit = TimeSpan.FromSeconds(10) };

    public static CompiledGraph Graph(string src, string? name = null)
    {
        var p = TestUtil.Compile(src);
        TestUtil.AssertNoErrors(p);
        return name == null ? p.Graphs.Single() : p.Graph(name)!;
    }

    public static CompiledGraph Example(string file, string graph) => TestUtil.CompileExample(file).Graph(graph)!;

    public static GraphInstanceInfo Plan(CompiledGraph g, ClusterSnapshot cluster, object?[]? args = null,
        SchedulerOptions? options = null, IProfileStore? profiles = null, CancellationToken ct = default) =>
        new GraphScheduler(new FixedCluster { Snapshot = cluster }, options ?? Options, profiles).PlanGraph(g, args ?? Array.Empty<object?>(), ct);

    public static SchedulingException Fails(CompiledGraph g, ClusterSnapshot cluster, object?[]? args = null, SchedulerOptions? options = null, IProfileStore? profiles = null) =>
        Assert.Throws<SchedulingException>(() => Plan(g, cluster, args, options, profiles));

    public static string HostOf(GraphInstanceInfo i, string agentId) => i.Agent(agentId).HostId!;
    public static IEnumerable<string> Codes(GraphInstanceInfo i) => i.Plan.Warnings.Select(w => w.Code);

    public const string M = "type m { v: string }\n";
}

public class SchedulerTests
{
    private static readonly ClusterSnapshot ThreeLevels = Sched.Cluster(
        Sched.Host("public-1", "public", "a"), Sched.Host("public-2", "public", "b"),
        Sched.Host("internal-1", "internal", "a"), Sched.Host("internal-2", "internal", "b"),
        Sched.Host("secret-1", "secret", "a"), Sched.Host("secret-2", "secret", "b"));

    private static GraphInstanceInfo PlanCamera() =>
        Sched.Plan(Sched.Example("camera.osh", "foo"), ThreeLevels, new object?[] { "cam-a", "cam-b" });

    // --- Main example (S§14 scenario 1, without lanes) ---

    [Fact]
    public void MainExamplePlacementRespectsLabels()
    {
        var i = PlanCamera();
        Assert.Equal("OPTIMAL", i.Plan.SolverStatus);
        foreach (var a in i.AgentsOf("det").Concat(i.Agents.Where(a => a.LaneLabel == "secret"))) Assert.StartsWith("secret-", a.HostId);
        foreach (var a in i.Agents.Where(a => a.LaneLabel == "internal")) Assert.DoesNotContain("public", a.HostId);
        foreach (var a in i.AgentsOf("sink")) Assert.True(a.HostId!.StartsWith("internal-") || a.HostId.StartsWith("secret-"), a.HostId);
        Assert.Equal("secret", i.Agent("g1/det/0").PlacementLabel);
        Assert.Equal("internal", i.Agent("g1/sink/0").PlacementLabel);
    }

    [Fact]
    public void MainExampleAgentsStandbysAndKeyGroups()
    {
        var i = PlanCamera();
        var det = i.AgentsOf("det").Where(a => a.Role == AgentRole.Primary).OrderBy(a => a.InstanceIndex).ToList();
        Assert.Equal(new[] { 0, 32, 64, 96 }, det.Select(a => a.OwnedKeyGroups!.From));
        Assert.Equal(new[] { 32, 64, 96, 128 }, det.Select(a => a.OwnedKeyGroups!.To));

        // Stateful `always` nodes get standbys on other hosts (H5); keyless ones get two instances on two hosts (H7).
        foreach (var p in i.Agents.Where(a => a.Role == AgentRole.Primary && a.NodeName is "det" or "sink"))
        {
            var s = i.Agent(p.StandbyId!);
            Assert.Equal($"{p.AgentId}/standby", s.AgentId);
            Assert.NotEqual(p.HostId, s.HostId);
        }
        // anon is laned (S§14 scenario 1): per lane, two instances (always) on two hosts.
        var anon = i.AgentsOf("anon").ToList();
        Assert.Equal(new[] { "public", "internal", "secret" }, anon.Select(a => a.LaneLabel).Distinct());
        foreach (var lane in anon.GroupBy(a => a.LaneLabel))
        {
            Assert.Equal(new[] { $"g1/anon@{lane.Key}/0", $"g1/anon@{lane.Key}/1" }, lane.Select(a => a.AgentId));
            Assert.Equal(2, lane.Select(a => a.HostId).Distinct().Count());
        }
        Assert.All(anon, a => Assert.Null(a.StandbyId));
        Assert.Null(i.Agent("g1/det/0").LaneLabel);                      // keyed: not laned
        Assert.DoesNotContain(i.AgentsOf("s1"), a => a.Role == AgentRole.Standby);
    }

    [Fact]
    public void MainExamplePipes()
    {
        var i = PlanCamera();
        Assert.Equal(4, i.Pipes.Count(p => p.EdgeName == "e1"));
        Assert.All(i.Pipes.Where(p => p.EdgeName == "e1"), p => Assert.Equal(RoutingMode.Keyed, p.Routing));
        Assert.Equal(new[] { 0, 32, 64, 96 }, i.Pipes.Where(p => p.EdgeName == "e1").Select(p => p.DestinationKeyGroups!.From).OrderBy(x => x));
        Assert.Equal(24, i.Pipes.Count(p => p.EdgeName == "e3" && p.Routing == RoutingMode.LoadBalanced));
        Assert.Equal(6, i.Pipes.Count(p => p.EdgeName == "e4" && p.Routing == RoutingMode.Direct));
        Assert.Equal(6, i.Pipes.Count(p => p.Implicit && p.DestinationPort == "late_in"));
        Assert.Equal(44, i.Pipes.Count);
        // Traffic only moves upward: det → anon@λ carries only labels at or below λ.
        Assert.All(i.Pipes.Where(p => p.DestinationAgentId.Contains("anon@public")), p => Assert.Equal(new[] { "public" }, p.Allowed));
        Assert.All(i.Pipes.Where(p => p.DestinationAgentId.Contains("anon@internal")), p => Assert.Equal(new[] { "public", "internal" }, p.Allowed));
        Assert.DoesNotContain(i.Pipes, p => p.SourceAgentId.EndsWith("/standby") || p.DestinationAgentId.EndsWith("/standby"));

        // Stream ids within each receiving port: edge declaration order, then sender index.
        var det0 = i.Pipes.Where(p => p.DestinationAgentId == "g1/det/0").OrderBy(p => p.StreamId).ToList();
        Assert.Equal(new[] { "e1", "e2" }, det0.Select(p => p.EdgeName));
        Assert.Equal(new[] { 0, 1 }, det0.Select(p => p.StreamId));
        Assert.Equal(new[] { 0, 1, 2, 3 }, i.Pipes.Where(p => p.DestinationAgentId == "g1/anon@secret/0").Select(p => p.StreamId).OrderBy(x => x));
    }

    [Fact]
    public void MainExampleCheckElisionAndNativeChannels()
    {
        var i = PlanCamera();
        // S§14 scenario 1: anon@public → sink.in has its port-ceiling check elided (public ⊑ internal)...
        var fromPublic = i.Pipes.First(p => p.EdgeName == "e4" && p.SourceAgentId.Contains("anon@public"));
        Assert.Equal(new[] { "public" }, fromPublic.Allowed);
        Assert.True(fromPublic.Elided.HasFlag(ElidedChecks.PortCeiling));
        Assert.True(fromPublic.Elided.HasFlag(ElidedChecks.Host));
        // ...but anon@secret → sink.in does not: it may carry secret.
        var fromSecret = i.Pipes.First(p => p.EdgeName == "e4" && p.SourceAgentId.Contains("anon@secret"));
        Assert.Equal(new[] { "public", "internal", "secret" }, fromSecret.Allowed);
        Assert.False(fromSecret.Elided.HasFlag(ElidedChecks.PortCeiling));
        Assert.True(fromSecret.Elided.HasFlag(ElidedChecks.InstanceCeiling));
        Assert.True(fromSecret.Elided.HasFlag(ElidedChecks.Compartment));

        // e1 (≤ internal) into det on a secret host: every check is proven.
        Assert.All(i.Pipes.Where(p => p.EdgeName == "e1"), p =>
            Assert.Equal(ElidedChecks.PortCeiling | ElidedChecks.InstanceCeiling | ElidedChecks.Host | ElidedChecks.Compartment, p.Elided));
        Assert.All(i.Pipes, p => Assert.Equal(p.SourceHostId == p.DestinationHostId, p.NativeFormat));
    }

    [Fact]
    public void PortCeilingElidedWhenProven()
    {
        var g = Sched.Graph("labels { public < internal; }\n" + Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge s --> k; } policy { label(public): s.o; label(internal): k.i; } }");
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("h", "internal")));
        Assert.True(i.Pipes.Single().Elided.HasFlag(ElidedChecks.PortCeiling));
    }

    [Fact]
    public void FlowsAndAuditReports()
    {
        var i = PlanCamera();
        var f = Assert.Single(i.Flows);
        Assert.Equal("g1/fastpath", f.FlowId);
        Assert.Equal(TimeSpan.FromMilliseconds(500), f.MaxLatency);
        Assert.Single(f.ExpectedPathLatencyMicros);
        Assert.True(f.ExpectedPathLatencyMicros[0] >= 50_000);   // includes det.in's 50ms lateness
        Assert.Single(i.Plan.Audit.Declassifications);
        Assert.Contains("g1/fastpath", i.Agent("g1/det/0").FlowIds);
        Assert.Contains(i.Plan.Warnings, w => w.Code == "SPW10" && w.Message.Contains("'sink'"));
        Assert.DoesNotContain(i.Plan.Warnings, w => w.Code == "SPW10" && w.Message.Contains("'det'"));
    }

    // --- Phase 0 ---

    [Fact]
    public void SP001CompileErrors()
    {
        var p = TestUtil.Compile(Sched.M + "graph g() { topology { node b (i: m) = process('b'); } }");
        var ex = Assert.Throws<SchedulingException>(() => Sched.Plan(p.Graphs.Single(), ThreeLevels));
        Assert.Equal("SP001", ex.Code);
    }

    [Fact]
    public void SP002ArgumentMismatch() =>
        Assert.Equal("SP002", Sched.Fails(Sched.Example("camera.osh", "foo"), ThreeLevels, new object?[] { "only-one" }).Code);

    [Fact]
    public void SP003LabelAnalysisWithBoundParameters()
    {
        var g = Sched.Graph("labels { public < secret; }\n" + Sched.M + "graph g(mode: string) { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge s --> k; }" +
            " policy { label(x: m => mode == 'strict' ? secret : public): s.o; label(public): k.i; } }");
        Assert.Equal("SP003", Sched.Fails(g, ThreeLevels, new object?[] { "strict" }).Code);
        Assert.NotNull(Sched.Plan(g, ThreeLevels, new object?[] { "lenient" }));
    }

    // --- Eligibility (H2) and early checks ---

    [Fact]
    public void SP004ListsWhyEachHostWasExcluded()
    {
        var g = Sched.Graph("labels { public < secret; }\n" + Sched.M + "graph g() { topology { node s () => (o: m) = process('python', 's.py'); node k (i: m) = process('node', 'k.js'); edge s --> k; } policy { label(secret): s.o; pin('h*'): k; } }");
        var cluster = Sched.Cluster(
            Sched.Host("h-public", "public"),
            Sched.Host("h-dead", "secret", alive: false),
            Sched.Host("x-secret", "secret"),
            Sched.Host("h-nonode", "secret", exes: new[] { "python" }),
            Sched.Host("h-odd", "classified"));
        var ex = Sched.Fails(g, cluster);
        Assert.Equal("SP004", ex.Code);
        var d = ex.Diagnostics.Single(x => x.Code == "SP004");
        Assert.Contains("for k", d.Message);
        Assert.Contains(d.Details, x => x.StartsWith("h-public: label public is below the placement label secret"));
        Assert.Contains(d.Details, x => x.StartsWith("h-dead: not alive"));
        Assert.Contains(d.Details, x => x.StartsWith("x-secret: name 'x-secret' does not match pin 'h*'"));
        Assert.Contains(d.Details, x => x.StartsWith("h-nonode: missing executable 'node'"));
        Assert.Contains(d.Details, x => x.Contains("'classified' is not a label of this graph; treated as ⊥"));
    }

    [Fact]
    public void LabelledNodesNeverFailOpen()
    {
        // The review's finding #1: a node holding `secret` must not land on a public host.
        var g = Sched.Graph("labels { public < secret; }\n" + Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge s --> k; } policy { label(secret): s.o; } }");
        Assert.Equal("SP004", Sched.Fails(g, Sched.Cluster(Sched.Host("p1", "public"), Sched.Host("p2", "public"))).Code);
        var ok = Sched.Plan(g, Sched.Cluster(Sched.Host("p1", "public"), Sched.Host("s1", "secret")));
        Assert.Equal("s1", Sched.HostOf(ok, "g1/k/0"));
    }

    [Fact]
    public void HigherHostLabelsAreEligible()
    {
        var g = Sched.Graph("labels { public < internal; internal < secret; }\n" + Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge s --> k; } policy { label(internal): s.o; } }");
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("public", "public"), Sched.Host("secret", "secret")));
        Assert.Equal("secret", Sched.HostOf(i, "g1/k/0"));
    }

    [Fact]
    public void SP006TotalDemandExceedsCapacity()
    {
        var g = Sched.Graph(Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k[] (i: m) = process('k'); edge s --> k; } policy { partitions(4): k; } }");
        var profiles = new InMemoryProfileStore().Set("g", "k", new NodeProfile(Demand: new ResourceVector(1500, 1L << 20)));
        var ex = Sched.Fails(g, Sched.Cluster(Sched.Host("a", cpu: 2000), Sched.Host("b", cpu: 2000)), profiles: profiles);
        Assert.Equal("SP006", ex.Code);
    }

    [Fact]
    public void SP007NamesTheConstraintClass()
    {
        // 3 × 600m fits in 2 × 1000m in total, but not per host (bin packing, H3).
        var g = Sched.Graph(Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k[] (i: m) = process('k'); edge s --> k; } policy { partitions(3): k; } }");
        var profiles = new InMemoryProfileStore()
            .Set("g", "k", new NodeProfile(Demand: new ResourceVector(600, 1L << 20)))
            .Set("g", "s", new NodeProfile(Demand: new ResourceVector(1, 1L << 20)));
        var ex = Sched.Fails(g, Sched.Cluster(Sched.Host("a", cpu: 1000), Sched.Host("b", cpu: 1000)), profiles: profiles);
        Assert.Equal("SP007", ex.Code);
        Assert.Contains(ex.Diagnostics[0].Details, d => d.StartsWith("H3 (capacity)"));
    }

    // --- Relaxation (S§8.2) ---

    [Fact]
    public void StandbySeparationIsRelaxedWhenOnlyOneHostQualifies()
    {
        var g = Sched.Graph("labels { public < secret; }\n" + Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge s --> k; } policy { label(secret): s.o; always: k; } }");
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("p", "public"), Sched.Host("s", "secret")));
        Assert.Contains("SPW02", Sched.Codes(i));
        Assert.Contains("H5: k", i.Plan.RelaxedConstraints);
        Assert.Equal("s", Sched.HostOf(i, "g1/k/0"));
        Assert.Equal("s", Sched.HostOf(i, "g1/k/0/standby"));
    }

    [Fact]
    public void LivenessSpreadIsRelaxedWithOneEligibleHost()
    {
        var g = Sched.Graph(Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k[] (i: m) = process('k'); edge s --> k; } policy { always: k; partitions(2): k; pin('only'): k; } }");
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("only"), Sched.Host("other")));
        Assert.Contains("SPW03", Sched.Codes(i));
        Assert.All(i.AgentsOf("k"), a => Assert.Equal("only", a.HostId));
    }

    [Fact]
    public void LivenessSpreadIsEnforced()
    {
        var g = Sched.Graph(Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k[] (i: m) = process('k'); edge s --> k; } policy { always: k; partitions(2..4): k; } }");
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("a"), Sched.Host("b")));
        Assert.Equal(2, i.AgentsOf("k").Select(a => a.HostId).Distinct().Count());
        Assert.DoesNotContain("SPW03", Sched.Codes(i));
    }

    // --- Counts (S§5.1, H6, S6) ---

    [Fact]
    public void ElasticGroupsActivateOnlyWhatTheyNeed()
    {
        var g = Sched.Graph(Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k[] (i: m) = process('k'); edge s --> k; } policy { partitions(1..4): k; } }");
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("a"), Sched.Host("b")));
        Assert.Equal(new[] { "g1/k/0" }, i.AgentsOf("k").Select(a => a.AgentId));
        Assert.Single(i.Pipes);
    }

    [Fact]
    public void RateMinimumRaisesTheCount()
    {
        var g = Sched.Graph(Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k[] (i: m) = process('k'); edge e: s --> k; } policy { partitions(1..8): k; min_rate(250): e; } }");
        var profiles = new InMemoryProfileStore().Set("g", "k", new NodeProfile(ThroughputPerInstance: 100));
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("a"), Sched.Host("b")), profiles: profiles);
        Assert.Equal(3, i.AgentsOf("k").Count());
        Assert.Equal(new[] { 0, 1, 2 }, i.AgentsOf("k").Select(a => a.InstanceIndex));
        Assert.Contains(i.Plan.Warnings, w => w.Code == "SPW01" && w.Message.Contains("raised from 1 to 3"));
        Assert.All(i.Pipes, p => Assert.Equal(84, p.EstimatedRate));   // 250 msg/s over 3 pipes, rounded up
        Assert.All(i.Pipes, p => Assert.Equal(85334, p.EstimatedBandwidth));   // × the default 1024-byte message
    }

    [Fact]
    public void LivenessMinimumIsClampedByPartitions()
    {
        var g = Sched.Graph(Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k[] (i: m) = process('k'); edge s --> k; } policy { always: k; } }");
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("a"), Sched.Host("b")));
        Assert.Contains(i.Plan.Warnings, w => w.Code == "SPW01" && w.Message.Contains("clamped to 1"));
        Assert.Single(i.AgentsOf("k"));
    }

    [Fact]
    public void InstanceStandbyOnlyForTheNamedInstance()
    {
        var g = Sched.Graph("key k: string; type r { id: k }\ngraph g() { topology { node s () => (o: r) = process('s'); node n[k] (i: r) = process('n'); edge s --> n; } policy { partitions(3): n; always: n[1]; } }");
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("a"), Sched.Host("b")));
        Assert.Equal(new[] { "g1/n/1/standby" }, i.Agents.Where(a => a.Role == AgentRole.Standby).Select(a => a.AgentId));
    }

    // --- Other hard constraints ---

    [Fact]
    public void PinGlobs()
    {
        Assert.True(GraphScheduler.GlobMatch("web-*-east", "web-1-east"));
        Assert.True(GraphScheduler.GlobMatch("h?", "h1"));
        Assert.False(GraphScheduler.GlobMatch("h?", "h10"));
        Assert.False(GraphScheduler.GlobMatch("web*", "xweb"));
        var g = Sched.Graph(Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k[] (i: m) = process('k'); edge s --> k; } policy { partitions(2): k; pin('web-*-east'): k; pin('db?'): k[1]; } }");
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("web-1-east"), Sched.Host("web-2-west"), Sched.Host("db1")));
        Assert.Equal("web-1-east", Sched.HostOf(i, "g1/k/0"));
        Assert.Equal("db1", Sched.HostOf(i, "g1/k/1"));
        Assert.Equal("web-*-east", i.Agent("g1/k/0").PinPattern);
    }

    [Fact]
    public void MaxAgentsPerHost()
    {
        var g = Sched.Graph(Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge s --> k; } }");
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("a", maxAgents: 1), Sched.Host("b", maxAgents: 1)));
        Assert.NotEqual(Sched.HostOf(i, "g1/s/0"), Sched.HostOf(i, "g1/k/0"));
    }

    [Fact]
    public void ReachabilityIsRequired()
    {
        var g = Sched.Graph(Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge e: s --> k; } policy { pin('a'): s; pin('b'): k; } }");
        var cluster = Sched.Cluster(Sched.Host("a"), Sched.Host("b")) with
        {
            Network = new List<NetworkQuality> { new("a", "b", new Dictionary<int, long>(), null, Reachable: false) },
        };
        var ex = Sched.Fails(g, cluster);
        Assert.Equal("SP007", ex.Code);
        Assert.Contains(ex.Diagnostics[0].Details, d => d.StartsWith("H8 (reachability): e"));
    }

    // --- Objective (S§7.4) ---

    [Fact]
    public void LatencyGoalsCoLocateAndReportMisses()
    {
        var src = Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); @one_to_one node t (i: m) => (o: m) = process('t'); node k (i: m) = process('k');" +
                  " edge a: s --> t; edge b: t --> k; flow f = { a, b }; } policy { max_latency(1ms): f; } }";
        var lat = new Dictionary<int, long> { [50] = 5_000, [99] = 20_000 };
        var cluster = Sched.Cluster(Sched.Host("x"), Sched.Host("y")) with
        {
            Network = new List<NetworkQuality> { new("x", "y", lat, null), new("y", "x", lat, null) },
        };
        var i = Sched.Plan(Sched.Graph(src), cluster);
        Assert.Single(i.Agents.Select(a => a.HostId).Distinct());
        Assert.DoesNotContain("SPW05", Sched.Codes(i));
        Assert.Equal(new long[] { 100 }, i.Flows.Single().ExpectedPathLatencyMicros);   // two local hops

        var pinned = Sched.Graph(src.Replace("max_latency(1ms): f;", "max_latency(1ms): f; pin('x'): s; pin('y'): k;"));
        var j = Sched.Plan(pinned, cluster);
        Assert.Contains("SPW05", Sched.Codes(j));
        Assert.True(j.Flows.Single().ExpectedPathLatencyMicros[0] >= 20_200);   // p99 cross-host + marshalling
    }

    [Fact]
    public void BandwidthShortfallIsReported()
    {
        var g = Sched.Graph(Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge e: s --> k; } policy { min_rate(10000): e; pin('x'): s; pin('y'): k; } }");
        var cluster = Sched.Cluster(Sched.Host("x"), Sched.Host("y")) with
        {
            Network = new List<NetworkQuality> { new("x", "y", new Dictionary<int, long>(), 1024 * 1024) },
        };
        var i = Sched.Plan(g, cluster);
        Assert.Contains("SPW06", Sched.Codes(i));
        Assert.True(i.Plan.ObjectiveTerms["S2_bandwidth_shortfall_kibps"] > 0);
    }

    [Fact]
    public void CrossHostTrafficIsAvoided()
    {
        // With the default weights, 1 MB/s of traffic doesn't outweigh load balance (W3 = 1/MB/s vs W4 = 10/‰)...
        var g = Sched.Graph(Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge e: s --> k; } policy { min_rate(1000): e; } }");
        Assert.False(Sched.Plan(g, Sched.Cluster(Sched.Host("x"), Sched.Host("y"))).Pipes.Single().NativeFormat);
        // ...but 10 GB/s does.
        var heavy = new InMemoryProfileStore().Set("g", "s", new NodeProfile(MessageSize: new Dictionary<string, long> { ["o"] = 10L << 20 }));
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("x"), Sched.Host("y")), profiles: heavy);
        Assert.True(i.Pipes.Single().NativeFormat);
    }

    [Fact]
    public void StandbysAvoidTheirPrimarysZone()
    {
        var g = Sched.Graph(Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge s --> k; } policy { always: k; } }");
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("a1", zone: "a"), Sched.Host("a2", zone: "a"), Sched.Host("b1", zone: "b")));
        var zone = (string h) => h[0];
        Assert.NotEqual(zone(Sched.HostOf(i, "g1/k/0")), zone(Sched.HostOf(i, "g1/k/0/standby")));
    }

    // --- Labels ---

    // tenants.osh with an unpartitioned store: a node that can't be laned stays compartment-bound.
    internal static string TenantsWithSharedStore() => File.ReadAllText(TestUtil.ExamplePath("tenants.osh"))
        .Replace("node store[tenant] (in: rec)", "node store (in: rec)").Replace("partitions(4): agg, store;", "partitions(4): agg;");

    [Fact]
    public void UnpartitionedForbiddenTopNodesAreCompartmentBoundAtRuntime()
    {
        var g = Sched.Graph(TenantsWithSharedStore());
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("a", "a_internal", cpu: 8000), Sched.Host("b", "b_internal", cpu: 8000)));
        var store = i.Agent("g1/store/0");
        Assert.True(store.CompartmentBoundAtRuntime);
        Assert.Equal("bottom", store.PlacementLabel);
        Assert.False(i.Agent("g1/a_src/0").CompartmentBoundAtRuntime);
        // Pipes into store can't prove compartment isolation.
        Assert.All(i.Pipes.Where(p => p.DestinationAgentId.Contains("/store/")), p => Assert.False(p.Elided.HasFlag(ElidedChecks.Compartment)));
    }

    // F3: a keyed node holding several compartments gets one lane (a full set of key-group owners) per
    // compartment, placed on that compartment's hosts; pipes only connect matching compartments.
    [Fact]
    public void KeyedNodesSpanningCompartmentsGetCompartmentLanes()
    {
        var g = Sched.Example("tenants.osh", "tenants");
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("a", "a_internal", cpu: 8000), Sched.Host("b", "b_internal", cpu: 8000)));
        foreach (var node in new[] { "agg", "store" })
            foreach (var (lane, host) in new[] { ("a_internal", "a"), ("b_internal", "b") })
            {
                var agents = i.AgentsOf(node).Where(a => a.LaneLabel == lane).OrderBy(a => a.AgentId).ToList();
                Assert.Equal(4, agents.Count);
                Assert.Equal(new[] { (0, 32), (32, 64), (64, 96), (96, 128) }, agents.Select(a => (a.OwnedKeyGroups!.From, a.OwnedKeyGroups.To)));
                Assert.All(agents, a =>
                {
                    Assert.Equal(host, a.HostId);
                    Assert.Equal(lane, a.PlacementLabel);
                    Assert.False(a.CompartmentBoundAtRuntime);
                    Assert.StartsWith($"g1/{node}@{lane}/", a.AgentId);
                });
            }
        Assert.All(i.Pipes.Where(p => p.EdgeName == "a1"), p => Assert.Contains("@a_internal/", p.DestinationAgentId));
        Assert.All(i.Pipes.Where(p => p.EdgeName == "b1"), p => Assert.Contains("@b_internal/", p.DestinationAgentId));
        Assert.DoesNotContain(i.Pipes, p => p.SourceAgentId.Contains("@a_internal/") && p.DestinationAgentId.Contains("@b_internal/"));
        Assert.All(i.Pipes, p => Assert.True(p.Elided.HasFlag(ElidedChecks.Compartment)));
        Assert.Equal(new[] { "a_internal", "b_internal" }, i.Plan.Audit.Dift.Single(d => d.Node == "agg").Lanes);

        // A compartment with no trusted host can't run its lane.
        Assert.Equal("SP004", Sched.Fails(g, Sched.Cluster(Sched.Host("a", "a_internal", cpu: 16000))).Code);
    }

    [Fact]
    public void LabelledSourcesArePlacedByTheirLabel()
    {
        var i = PlanCamera();
        Assert.Equal("secret", i.Agent("g1/s2/0").PlacementLabel);
        Assert.StartsWith("secret-", i.Agent("g1/s2/0").HostId);
        Assert.Equal("internal", i.Agent("g1/s1/0").PlacementLabel);

        // An unlabelled source stays unconstrained (⊥).
        var g = Sched.Graph("labels { public < secret; }\n" + Sched.M + "graph g() { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge s --> k; } }");
        Assert.Equal("public", Sched.Plan(g, Sched.Cluster(Sched.Host("p", "public"))).Agent("g1/s/0").PlacementLabel);
    }

    [Fact]
    public void CompartmentBoundNodesNeedAHostForSomeCompartment()
    {
        var src = TenantsWithSharedStore();
        var g = Sched.Graph(src);
        var cluster = Sched.Cluster(Sched.Host("a-pub", "a_public"), Sched.Host("a-int", "a_internal", cpu: 8000), Sched.Host("b-pub", "b_public"), Sched.Host("b-int", "b_internal", cpu: 8000));
        var i = Sched.Plan(g, cluster);
        Assert.Equal("a-int", Sched.HostOf(i, "g1/a_src/0"));
        Assert.Equal("b-int", Sched.HostOf(i, "g1/b_src/0"));
        var store = i.Agent("g1/store/0");
        Assert.Equal(new[] { "a_internal", "b_internal" }, store.PlacementAnyOf);
        Assert.EndsWith("-int", store.HostId);

        // With only public hosts for the tenant data, no instance can hold either compartment.
        var tooLow = Sched.Cluster(Sched.Host("a-int", "a_internal", cpu: 8000), Sched.Host("b-int", "b_internal", cpu: 8000), Sched.Host("a-pub", "a_public"));
        var pinned = Sched.Graph(src.Replace("partitions(4): agg;", "partitions(4): agg; pin('a-pub'): store;"));
        var ex = Sched.Fails(pinned, tooLow);
        Assert.Equal("SP004", ex.Code);
        Assert.Contains(ex.Diagnostics[0].Details, d => d.Contains("can't hold any compartment's data (a_internal or b_internal)"));
    }

    // F2: a source emitting several compartments holds data of each; it must run on a host that can hold
    // at least one compartment's data, and the plan fails when no host can.
    [Fact]
    public void SourceSpanningCompartmentsNeedsAHostForSomeCompartment()
    {
        var g = Sched.Graph("labels { a_lo < a_hi; b_lo < b_hi; }\nkey t: string;\ntype m { k: t }\n" +
            "graph g() { topology { node s () => (o: m) = process('s'); node n[t] (i: m) = process('n'); edge s --> n; }" +
            " policy { label((x: m) => x.k == 'a' ? a_hi : b_hi): s.o; } }");
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("a-lo", "a_lo"), Sched.Host("b-lo", "b_lo"), Sched.Host("a-hi", "a_hi"), Sched.Host("b-hi", "b_hi")));
        var s = i.Agent("g1/s/0");
        Assert.Equal(new[] { "a_hi", "b_hi" }, s.PlacementAnyOf);
        Assert.EndsWith("-hi", s.HostId);

        var ex = Sched.Fails(g, Sched.Cluster(Sched.Host("a-lo", "a_lo"), Sched.Host("b-lo", "b_lo")));
        Assert.Equal("SP004", ex.Code);
        Assert.Contains(ex.Diagnostics, d => d.Message == "no eligible host for s" && d.Details.Any(x => x.Contains("can't hold any compartment's data (a_hi or b_hi)")));
    }

    // F7: the forward-eligible hint (L§6.5) co-locates sender i with receiver i.
    [Fact]
    public void ForwardEligiblePairsAreCoLocated()
    {
        var g = Sched.Graph("key k: string;\ntype m { id: k }\n" +
            "graph g() { topology { node s () => (o: m) = process('s'); node a[k] (i: m) => (o: m) = process('a'); node b[k] (i: m) => (o: m) = process('b');" +
            " node c[k] (i: m) = process('c'); edge s --> a; edge a --> b; edge b --> c; } policy { partitions(3): a, b, c; } }");
        Assert.True(g.Edges.Where(e => e.SourceNode != "s").All(e => e.ForwardEligible));
        var i = Sched.Plan(g, Sched.Cluster(Sched.Host("h1", cpu: 2000), Sched.Host("h2", cpu: 2000), Sched.Host("h3", cpu: 2000)));
        for (int x = 0; x < 3; x++)
        {
            Assert.Equal(Sched.HostOf(i, $"g1/a/{x}"), Sched.HostOf(i, $"g1/b/{x}"));
            Assert.Equal(Sched.HostOf(i, $"g1/b/{x}"), Sched.HostOf(i, $"g1/c/{x}"));
        }
        Assert.Equal(0, i.Plan.ObjectiveTerms["S3_forward_pairs_split"]);

        // Without the preference, spreading alone splits some pairs.
        var off = Sched.Plan(g, Sched.Cluster(Sched.Host("h1", cpu: 2000), Sched.Host("h2", cpu: 2000), Sched.Host("h3", cpu: 2000)),
            options: Sched.Options with { Weights = new ObjectiveWeights(ForwardPairSplit: 0) });
        Assert.Contains(Enumerable.Range(0, 3), x => Sched.HostOf(off, $"g1/a/{x}") != Sched.HostOf(off, $"g1/b/{x}") || Sched.HostOf(off, $"g1/b/{x}") != Sched.HostOf(off, $"g1/c/{x}"));
    }

    [Fact]
    public void UncheckedFlowExclusiveNodesHaveNoPlacementLabel()
    {
        var i = Sched.Plan(Sched.Example("alerts.osh", "emergency"), Sched.Cluster(Sched.Host("p", "public"), Sched.Host("s", "secret")));
        Assert.Null(i.Agent("g1/pager/0").PlacementLabel);
        Assert.True(i.Agent("g1/pager/0").UncheckedExclusive);
        Assert.Equal("secret", i.Agent("g1/detector/0").PlacementLabel);
        Assert.True(i.Pipes.Single(p => p.EdgeName == "a2").Unchecked);
        Assert.False(i.Pipes.Single(p => p.EdgeName == "a3").Unchecked);
    }

    // --- Determinism, cancellation, records ---

    [Fact]
    public void PlansAreDeterministic()
    {
        var a = PlanCamera();
        var b = PlanCamera();
        Assert.Equal(a.Agents.Select(x => (x.AgentId, x.HostId)), b.Agents.Select(x => (x.AgentId, x.HostId)));
        Assert.Equal(a.Plan.ObjectiveValue, b.Plan.ObjectiveValue);
    }

    [Fact]
    public void CancellationBeforeSolving()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Sched.Plan(Sched.Example("camera.osh", "foo"), ThreeLevels, new object?[] { "a", "b" }, ct: cts.Token));
    }

    [Fact]
    public void SequencingMarksAndOrderingReachAgentPorts()
    {
        var i = Sched.Plan(Sched.Example("jpeg.osh", "jpeg_pipeline"), Sched.Cluster(Sched.Host("a"), Sched.Host("b")));
        Assert.True(i.Agent("g1/cam/0").Ports.Single().SequenceOrigin);
        Assert.True(i.Agent("g1/dec/0").Ports.Single(p => p.Direction == PortDirection.Out).SequencePropagating);
        Assert.Equal(TimeSpan.FromSeconds(2), i.Agent("g1/sink/0").Ports.Single().GapTimeout);

        var cam = PlanCamera().Agent("g1/sink/0").Ports.Single(p => p.Name == "in");
        Assert.Equal(TimeSpan.FromMilliseconds(200), cam.Lateness);
        Assert.Equal(OnLateMode.Route, cam.OnLate);
        Assert.Equal("internal", cam.Ceiling!.Label);
    }

    [Fact]
    public void GraphInstanceSerializesToJson()
    {
        var json = PlanCamera().ToJson();
        Assert.Contains("\"GraphInstanceId\": \"g1\"", json);
        Assert.Contains("\"Routing\": \"Keyed\"", json);
    }

    [Fact]
    public async Task SimulatedRuntimeSpawnsAndRecordsTheInstance()
    {
        var rt = new SimulatedRuntime { SchedulerOptions = Sched.Options };
        rt.InjectSnapshot(ThreeLevels);
        var interp = new AppInterpreter(rt);
        interp.AddSource(File.ReadAllText(TestUtil.ExamplePath("camera.osh")), "camera.osh");
        var outcome = await interp.SpawnAsync("spawn foo('cam-a', 'cam-b')");
        Assert.True(outcome.Succeeded, string.Join("\n", outcome.SchedulerDiagnostics));
        Assert.Equal(GraphInstanceState.Running, outcome.Handle!.State);
        Assert.Contains("g1", rt.GetGraphInstances().Keys);
        Assert.All(outcome.Handle.Info.Agents, a => Assert.Equal(a.Role == AgentRole.Primary ? AgentState.Running : AgentState.Pending, a.State));

        var failed = await interp.SpawnAsync("spawn foo('only-one')");
        Assert.False(failed.Succeeded);
        Assert.Contains(failed.Diagnostics, d => d.Code == "E0802");
    }
}

// Host info is control-plane state, exchanged between runtimes (ClusterInfo* messages), not Registry state.
public class ClusterInfoTests
{
    [Fact]
    public void ClusterInfoMessagesRoundTripThroughMessagePack()
    {
        var info = new HostInfo { Id = "n1", Name = "n1", Label = "secret", CpuMillis = 4000, MemoryBytes = 8L << 30, Tags = new() { "gpu" },
            Executables = new() { ["node"] = "20" }, MaxAgents = 8 };
        var round = Guid.NewGuid();
        RuntimeMessage request = new ClusterInfoRequest { MessageId = Guid.NewGuid(), SenderId = "n0", RoundId = round };
        Assert.Equal(round, ((ClusterInfoRequest)MessagePackSerializer.Deserialize<RuntimeMessage>(MessagePackSerializer.Serialize(request))).RoundId);

        RuntimeMessage response = new ClusterInfoResponse { MessageId = request.MessageId, SenderId = "n1", Info = info };
        var r = (ClusterInfoResponse)MessagePackSerializer.Deserialize<RuntimeMessage>(MessagePackSerializer.Serialize(response));
        Assert.Equal((request.MessageId, "secret", 4000L, "20", 8), (r.MessageId, r.Info.Label, r.Info.CpuMillis, r.Info.Executables!["node"], r.Info.MaxAgents));

        var state = new ClusterState { RoundId = round, InitiatorId = "n0", CollectedAt = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc),
            Hosts = new() { info }, Unreachable = new() { "n2" } };
        RuntimeMessage update = new ClusterInfoUpdate { MessageId = Guid.NewGuid(), SenderId = "n0", State = state };
        var u = (ClusterInfoUpdate)MessagePackSerializer.Deserialize<RuntimeMessage>(MessagePackSerializer.Serialize(update));
        Assert.Equal(state.CollectedAt, u.State.CollectedAt);
        Assert.Equal("n1", Assert.Single(u.State.Hosts).Id);
        Assert.Equal(new[] { "n2" }, u.State.Unreachable);
    }
}
