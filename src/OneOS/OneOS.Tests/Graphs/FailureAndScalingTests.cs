using System.Collections.Concurrent;
using System.Text.Json;
using OneOS.Runtime;
using OneOS.Runtime.Graphs;
using OneOS.Runtime.Language;
using OneOS.Runtime.Language.Models;
using OneOS.Runtime.Scheduling;
using OneOS.Runtime.Sidecar;
using OneOS.Tests.Language;
using OneOS.Tests.Scheduling;

namespace OneOS.Tests.Graphs;

// S§10.4 (keyless failure handling) and S§10.5 (elastic scaling), option B: no stateful failover.
public class FailureAndScalingTests
{
    private static readonly ClusterSnapshot ThreeLevels = Sched.Cluster(
        Sched.Host("public-1", "public"), Sched.Host("public-2", "public"), Sched.Host("internal-1", "internal"),
        Sched.Host("internal-2", "internal"), Sched.Host("secret-1", "secret"), Sched.Host("secret-2", "secret"), Sched.Host("secret-3", "secret"));

    private static (CompiledGraph Graph, GraphInstanceInfo Plan, GraphScheduler Scheduler) Camera()
    {
        var (bound, _) = GraphBinder.Bind(TestUtil.CompileExample("camera.osh").Graph("foo")!, new object?[] { "a", "b" });
        var scheduler = new GraphScheduler(new FixedCluster { Snapshot = ThreeLevels }, Sched.Options);
        return (bound!, scheduler.PlanGraph(bound!, Array.Empty<object?>()), scheduler);
    }

    [Fact]
    public void ReplacementMovesOnlyTheFailedAgent()
    {
        var (g, plan, scheduler) = Camera();
        var failed = plan.Agent("g1/anon@secret/0");
        var next = scheduler.Replan(g, plan, new ReplanRequest(Replace: new HashSet<string> { failed.AgentId }, AvoidHosts: new HashSet<string> { failed.HostId! }));

        Assert.Equal(plan.Version + 1, next.Version);
        Assert.Equal(plan.Agents.Select(a => a.AgentId).OrderBy(x => x), next.Agents.Select(a => a.AgentId).OrderBy(x => x));
        var moved = next.Agent(failed.AgentId);
        Assert.NotEqual(failed.HostId, moved.HostId);
        Assert.StartsWith("secret-", moved.HostId);                          // same lane: still on a secret host
        foreach (var a in plan.Agents.Where(a => a.AgentId != failed.AgentId))
            Assert.Equal(a.HostId, next.Agent(a.AgentId).HostId);

        // Pipes not touching the moved agent keep their ids and stream ids; the moved agent's pipes are new.
        foreach (var p in next.Pipes)
        {
            bool touches = p.SourceAgentId == failed.AgentId || p.DestinationAgentId == failed.AgentId;
            var old = plan.Pipes.Single(o => o.EdgeName == p.EdgeName && o.SourceAgentId == p.SourceAgentId && o.DestinationAgentId == p.DestinationAgentId);
            if (touches) Assert.StartsWith("g1/pipe/v2.", p.PipeId);
            else Assert.Equal((old.PipeId, old.StreamId), (p.PipeId, p.StreamId));
        }
        foreach (var port in next.Pipes.GroupBy(p => (p.DestinationAgentId, p.DestinationPort)))
            Assert.Equal(port.Count(), port.Select(p => p.StreamId).Distinct().Count());
    }

    [Fact]
    public void MembershipResolveRoutesAroundAFailedLaneAndQueuesWhatCantMove()
    {
        var (_, plan, _) = Camera();
        var secretLane = plan.Agents.Where(a => a.LaneLabel == "secret").Select(a => a.AgentId).ToHashSet();
        var rerouted = GraphScheduler.RecomputeRoutingTables(plan, secretLane, TimeSpan.FromSeconds(5));
        foreach (var t in rerouted.RoutingTables.Where(t => t.EdgeName == "e3"))
        {
            Assert.DoesNotContain(t.ReceiverAgentIds, secretLane.Contains);
            Assert.Equal(new[] { "secret" }, t.Unroutable);                  // only the secret lane could take it
            Assert.Equal(1.0, t.Routes("internal").Sum(r => r.Probability), 6);
            Assert.Equal("membership", t.Trigger);
        }

        // S§14 scenario 9: with the med lane gone, med traffic moves up to the high lane and nothing queues.
        var dift = new InMemoryDift().Source("extract", "db", ExternalLabel.Static("low"));
        var levels = Sched.Cluster(Sched.Host("low", "low"), Sched.Host("med-1", "med"), Sched.Host("med-2", "med"), Sched.Host("high-1", "high"), Sched.Host("high-2", "high"));
        var etl = Sched.Plan(Sched.Example("etl.osh", "etl"), levels, options: Sched.Options with { Analyzer = dift });
        var med = etl.Agents.Where(a => a.NodeName == "transform" && a.LaneLabel == "med").Select(a => a.AgentId).ToHashSet();
        var t2 = GraphScheduler.RecomputeRoutingTables(etl, med, TimeSpan.FromSeconds(5)).RoutingTables.Single(t => t.EdgeName == "e1");
        Assert.Empty(t2.Unroutable);
        Assert.All(t2.Routes("med"), r => Assert.Contains("@high", r.Receiver));
    }

    [Fact]
    public void ScalingResizesOneGroupAndKeepsTheRest()
    {
        var dift = new InMemoryDift().Source("extract", "db", ExternalLabel.Static("low"));
        var levels = Sched.Cluster(Sched.Host("low", "low"), Sched.Host("med", "med"), Sched.Host("high-1", "high"), Sched.Host("high-2", "high"));
        var options = Sched.Options with { Analyzer = dift };
        var (bound, _) = GraphBinder.Bind(TestUtil.CompileExample("etl.osh").Graph("etl")!, Array.Empty<object?>());
        var scheduler = new GraphScheduler(new FixedCluster { Snapshot = levels }, options);
        var plan = scheduler.PlanGraph(bound!, Array.Empty<object?>());
        Assert.Single(plan.Agents, a => a.NodeName == "transform" && a.LaneLabel == "high");

        var up = scheduler.Replan(bound!, plan, new ReplanRequest(GroupCounts: new Dictionary<string, int> { ["transform@high"] = 3 }, Trigger: "scaling"));
        Assert.Equal(new[] { "g1/transform@high/0", "g1/transform@high/1", "g1/transform@high/2" },
            up.Agents.Where(a => a.NodeName == "transform" && a.LaneLabel == "high").Select(a => a.AgentId));
        Assert.All(up.Agents.Where(a => a.LaneLabel == "high"), a => Assert.StartsWith("high-", a.HostId));
        foreach (var a in plan.Agents) Assert.Equal(a.HostId, up.Agent(a.AgentId).HostId);
        Assert.All(up.RoutingTables, t => Assert.Equal("scaling", t.Trigger));
        Assert.Equal(3, up.RoutingTables.First(t => t.EdgeName == "e1").ReceiverAgentIds.Count(r => r.Contains("transform@high")));

        var down = scheduler.Replan(bound!, up, new ReplanRequest(GroupCounts: new Dictionary<string, int> { ["transform@high"] = 1 }, Trigger: "scaling"));
        Assert.Single(down.Agents, a => a.NodeName == "transform" && a.LaneLabel == "high");
        Assert.Equal(up.Agent("g1/transform@high/0").HostId, down.Agent("g1/transform@high/0").HostId);
    }

    [Theory]
    [InlineData(2, 1, 8, 250.0, 100.0, null, 4)]      // 250 msg/s at 80% of 100/s per instance → 4
    [InlineData(4, 1, 8, 50.0, 100.0, null, 1)]       // scale in
    [InlineData(2, 2, 8, 10.0, 100.0, null, 2)]       // never below the minimum (e.g. `always`)
    [InlineData(2, 1, 3, 1000.0, 100.0, null, 3)]     // never above partitions
    [InlineData(2, 1, 8, 10.0, 100.0, 400.0, 5)]      // the min_rate goal counts even when traffic is low
    [InlineData(3, 1, 8, 1000.0, null, null, 3)]      // no throughput estimate: keep the size
    public void ScalingRule(int current, int min, int max, double measured, double? thr, double? goal, int expected) =>
        Assert.Equal(expected, ElasticScaling.Desired(current, min, max, measured, thr, goal));

    [Fact]
    public void PlanUpdatesAreConditional()
    {
        var r = new Registry();
        r.Apply(new CommitGraphAction { Record = new GraphInstanceRecord { Id = "g", Committed = new() { ["h1"] = new long[] { 500, 1 } }, ExpectedHostVersions = new() { ["h1"] = r.HostVersion("h1") } } });
        r.Apply(new SetGraphAgentStateAction { GraphId = "g", AgentId = "g/w/0", State = "Failed" });

        // Derived from an old plan version: rejected.
        var stale = new UpdateGraphPlanAction { GraphId = "g", BasePlanVersion = 0, PlanJson = "x" };
        r.Apply(stale);
        Assert.Contains("plan changed", r.GraphCommitRejections[stale.ConflictKey]);

        // Grows the reservation on h2, where another graph reserved resources since: rejected.
        var expect = r.HostVersion("h2");
        r.Apply(new CommitGraphAction { Record = new GraphInstanceRecord { Id = "other", Committed = new() { ["h2"] = new long[] { 100, 1 } }, ExpectedHostVersions = new() { ["h2"] = expect } } });
        var conflicting = new UpdateGraphPlanAction { GraphId = "g", BasePlanVersion = 1, PlanJson = "y", Committed = new() { ["h1"] = new long[] { 500, 1 }, ["h2"] = new long[] { 500, 1 } }, ExpectedHostVersions = new() { ["h2"] = expect } };
        r.Apply(conflicting);
        Assert.Contains("h2", r.GraphCommitRejections[conflicting.ConflictKey]);

        var ok = new UpdateGraphPlanAction
        {
            GraphId = "g", BasePlanVersion = 1, PlanJson = "z", Committed = new() { ["h2"] = new long[] { 500, 1 } },
            ExpectedHostVersions = new() { ["h2"] = r.HostVersion("h2") }, ResetAgents = new() { "g/w/0" }, RemovedAgents = new() { "g/w/1" },
        };
        long epoch = r.HostEpochs.GetValueOrDefault("h2");
        r.Apply(ok);
        Assert.Equal(2, r.Graphs["g"].PlanVersion);
        Assert.Equal("z", r.Graphs["g"].PlanJson);
        Assert.Equal("Pending", r.Graphs["g"].AgentStates["g/w/0"]);
        Assert.Equal(epoch + 1, r.HostEpochs["h2"]);                      // reservation grew on h2
    }

    [Fact]
    public void SidecarResolvesRoutingOnLabelDrift()
    {
        var (g, plan, _) = Camera();
        var det = new AgentSidecar(new SidecarConfig(g, plan, plan.Agent("g1/det/0"), "secret"));
        det.RestoreTaint("secret");                                          // everything it emits is secret now
        for (int i = 0; i < 300; i++) det.Emit("out", JsonDocument.Parse($$"""{ "cam": "c", "ts": {{i}}, "redacted": false, "payload": {} }""").RootElement);
        var resolved = det.CheckDrift(0.2, 200, TimeSpan.FromSeconds(5));
        var t = Assert.Single(resolved);
        Assert.Equal("drift", t.Trigger);
        Assert.All(t.Routes("secret"), r => Assert.Contains("anon@secret", r.Receiver));
        Assert.Empty(det.CheckDrift(0.2, 200, TimeSpan.FromSeconds(5)));   // counts restart after a re-solve
    }
}

// A keyless worker dies mid-stream; the executor applies a re-plan that replaces it, and later
// messages flow through the replacement (single host, real processes).
public class ReplacementEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oneos-repl-" + Guid.NewGuid().ToString("N")[..8]);
    public ReplacementEndToEndTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task FailedKeylessWorkerIsReplaced()
    {
        string W(string name, string body) { var p = Path.Combine(_dir, name); File.WriteAllText(p, "import sys, json, time\n" + body); return p; }
        var src = W("src.py", "for i in range(40):\n    print(json.dumps({'v': i}), flush=True)\n    time.sleep(0.05)\nsys.stdin.read()\n");
        // The first worker incarnation crashes on v == 5; a replacement (marker file exists) keeps going.
        var marker = Path.Combine(_dir, "crashed");
        var work = W("work.py", $"import os\nfor line in sys.stdin:\n    m = json.loads(line)\n    if m['v'] == 5 and not os.path.exists('{marker}'):\n        open('{marker}', 'w').close(); sys.exit(1)\n    print(json.dumps(m), flush=True)\n");
        var sink = W("sink.py", "out = open(sys.argv[1], 'a')\nfor line in sys.stdin:\n    out.write(str(json.loads(line)['v']) + '\\n'); out.flush()\n");
        var result = Path.Combine(_dir, "out.txt");
        var compiled = TestUtil.Compile($$"""
            type m { v: i64 }
            graph g(out: string) { topology {
              node s () => (out: m) = process('python3', '{{src}}');
              node w[] (in: m) => (out: m) = process('python3', '{{work}}');
              node k (in: m) = process('python3', '{{sink}}', out);
              edge s --> w; edge w --> k; } }
            """);
        var (bound, _) = GraphBinder.Bind(compiled.Graph("g")!, new object?[] { result });
        var cluster = new FixedCluster { Snapshot = Sched.Cluster(Sched.Host("h")) };
        var scheduler = new GraphScheduler(cluster, Sched.Options);
        var plan = scheduler.PlanGraph(bound!, Array.Empty<object?>());

        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exec = new GraphExecutor(bound!, plan, "h", null, new InMemoryPipeHost(), new OsProcessFactory(_dir), _ => { }, (_, _) => { });
        exec.AgentStateChanged += r => { if (r.State == AgentState.Failed) failed.TrySetResult(r.AgentId); };
        await exec.StartAsync();

        var dead = await failed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("g1/w/0", dead);

        // What the controller does (S§10.4): route around it, then place a replacement.
        var rerouted = GraphScheduler.RecomputeRoutingTables(plan, new HashSet<string> { dead }, TimeSpan.FromSeconds(5));
        await exec.ApplyPlanAsync(rerouted);
        var replaced = scheduler.Replan(bound!, rerouted, new ReplanRequest(Replace: new HashSet<string> { dead }, AvoidHosts: new HashSet<string> { "h" }));
        await exec.ApplyPlanAsync(replaced);
        Assert.Equal(AgentState.Running, exec.Runners.Single(r => r.AgentId == dead).State);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        List<int> seen = new();
        while (DateTime.UtcNow < deadline)
        {
            seen = File.Exists(result) ? File.ReadAllLines(result).Select(int.Parse).ToList() : new();
            if (seen.Contains(39)) break;
            await Task.Delay(100);
        }
        await exec.StopAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(4, seen);           // before the crash
        Assert.Contains(39, seen);          // through the replacement
        Assert.DoesNotContain(5, seen);     // the message the worker died on is lost (no failover guarantees)
    }
}
