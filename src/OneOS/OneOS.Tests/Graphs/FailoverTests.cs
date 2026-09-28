using System.Collections.Concurrent;
using System.Text;
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

// Stateful failover (L§9.3, S§10.4): checkpoints to the standby's host, promotion, restore.
public class FailoverTests
{
    // A keyed stateful node with an ordered input (merge state migrates) and `always` (a standby).
    private const string Source = """
        labels { public < secret; }
        key k: string;
        clock t: u64 unit ms;
        type m { id: k, ts: t }
        graph g () {
          topology {
            @json_lines node s () => (o: m ordered within 1000ms) = process('fake', 's');
            @json_lines node agg[k] (i: m ordered) => (o: m)       = process('fake', 'agg');
            @json_lines node out (i: m)                            = process('fake', 'out');
            edge s --> agg;
            edge agg --> out;
          }
          policy {
            partitions(1): agg;
            always: agg;
            label(n: m => n.id == 'x' ? secret : public): s.o;
          }
        }
        """;

    private static Envelope Env(string label, JsonElement payload) =>
        new() { Label = label, Payload = Encoding.UTF8.GetBytes(payload.GetRawText()) };

    private static readonly ClusterSnapshot Hosts = Sched.Cluster(Sched.Host("h1", "secret"), Sched.Host("h2", "secret"), Sched.Host("h3", "secret"));

    private static (CompiledGraph Graph, GraphInstanceInfo Plan, GraphScheduler Scheduler) Plan()
    {
        var (bound, _) = GraphBinder.Bind(TestUtil.Compile(Source).Graph("g")!, Array.Empty<object?>());
        var scheduler = new GraphScheduler(new FixedCluster { Snapshot = Hosts }, Sched.Options);
        return (bound!, scheduler.PlanGraph(bound!, Array.Empty<object?>()), scheduler);
    }

    [Fact]
    public void PromotionMovesThePrimaryToItsStandbysHostAndPlacesANewStandby()
    {
        var (g, plan, scheduler) = Plan();
        var primary = plan.Agent("g1/agg/0");
        var standby = plan.Agent("g1/agg/0/standby");
        Assert.NotEqual(primary.HostId, standby.HostId);

        var next = scheduler.Replan(g, plan, new ReplanRequest(AvoidHosts: new HashSet<string> { primary.HostId! }, Trigger: "failover",
            Promote: new HashSet<string> { primary.AgentId }));
        Assert.Equal(standby.HostId, next.Agent(primary.AgentId).HostId);
        var newStandby = next.Agent(standby.AgentId).HostId;
        Assert.NotEqual(standby.HostId, newStandby);
        Assert.NotEqual(primary.HostId, newStandby);
        // The promoted primary is a new incarnation: its pipes are new.
        Assert.All(next.Pipes.Where(p => p.SourceAgentId == primary.AgentId || p.DestinationAgentId == primary.AgentId),
            p => Assert.StartsWith("g1/pipe/v2.", p.PipeId));
        Assert.Equal(next.Version, next.Agent(primary.AgentId).Version);
    }

    // With two eligible hosts, the new standby goes back to the failed primary's (still live) host rather
    // than sharing the promoted primary's.
    [Fact]
    public void NewStandbyPrefersSeparationOverAvoidingTheFailedHost()
    {
        var (bound, _) = GraphBinder.Bind(TestUtil.Compile(Source).Graph("g")!, Array.Empty<object?>());
        var scheduler = new GraphScheduler(new FixedCluster { Snapshot = Sched.Cluster(Sched.Host("h1", "secret"), Sched.Host("h2", "secret")) }, Sched.Options);
        var plan = scheduler.PlanGraph(bound!, Array.Empty<object?>());
        var primaryHost = plan.Agent("g1/agg/0").HostId!;
        var standbyHost = plan.Agent("g1/agg/0/standby").HostId!;
        var next = scheduler.Replan(bound!, plan, new ReplanRequest(AvoidHosts: new HashSet<string> { primaryHost }, Trigger: "failover",
            Promote: new HashSet<string> { "g1/agg/0" }));
        Assert.Equal(standbyHost, next.Agent("g1/agg/0").HostId);
        Assert.Equal(primaryHost, next.Agent("g1/agg/0/standby").HostId);
        Assert.DoesNotContain(next.Plan.Warnings, w => w.Code == "SPW02");
    }

    [Fact]
    public void SidecarStateSurvivesACheckpointRoundTrip()
    {
        var (g, plan, _) = Plan();
        var sidecar = new AgentSidecar(new SidecarConfig(g, plan, plan.Agent("g1/agg/0"), "secret"));
        var pipe = plan.Pipes.Single(p => p.DestinationAgentId == "g1/agg/0");
        JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        Assert.Single(sidecar.Receive(pipe.PipeId, Env("secret", J("""{ "id": "x", "ts": 1000 }"""))).Concat(
            sidecar.Receive(pipe.PipeId, Env("public", J("""{ "id": "y", "ts": 2500 }""")))));   // ts 1000 released, 2500 buffered

        var cp = new AgentCheckpoint("g1/agg/0", 1, DateTimeOffset.UtcNow, new ProcessSnapshot(new byte[] { 1, 2 }), sidecar.ExportState());
        var back = AgentCheckpoint.FromBytes(cp.ToBytes());
        Assert.Equal(new byte[] { 1, 2 }, back.Process!.Data);

        var restored = new AgentSidecar(new SidecarConfig(g, plan, plan.Agent("g1/agg/0"), "secret"));
        restored.ImportState(back.Sidecar);
        Assert.Equal("secret", restored.TaintName);
        var merge = restored.ExportState().Merges["i"];
        Assert.Equal(2500, Assert.Single(merge.Items).Ts);
        Assert.Equal(1000, merge.LastReleased);
        // A late message (below what was already released) is still late after the restore.
        Assert.Empty(restored.Receive(pipe.PipeId, Env("public", J("""{ "id": "y", "ts": 900 }"""))));
        // Advancing the watermark releases the restored buffer.
        var released = restored.Receive(pipe.PipeId, Env("public", J("""{ "id": "y", "ts": 4000 }""")));
        Assert.Equal(2500, AgentSidecar.Json(released.Single().Envelope).GetProperty("ts").GetInt64());
    }

    [Fact]
    public async Task StandbyHostTakesOverFromTheLatestCheckpoint()
    {
        var (g, plan, scheduler) = Plan();
        var primaryHost = plan.Agent("g1/agg/0").HostId!;
        var standbyHost = plan.Agent("g1/agg/0/standby").HostId!;
        var launcher = new FakeLauncher();
        launcher.Scripts["s"] = new[]
        {
            """{"port":"o","data":{"id":"x","ts":1000}}""",
            """{"port":"o","data":{"id":"y","ts":1500}}""",
            """{"port":"o","data":{"id":"x","ts":5000}}""",
        };
        var transport = new InMemoryPipeHost();
        var log = new ConcurrentQueue<string>();
        var taints = new ConcurrentDictionary<string, string>();
        var executors = Hosts.Hosts.ToDictionary(h => h.HostId, h =>
        {
            var e = new GraphExecutor(g, plan, h.HostId, h.Label, transport, launcher, _ => { }, (l, m) => log.Enqueue($"{l}: {m}"),
                checkpointInterval: null, taintFloor: a => taints.GetValueOrDefault(a));
            e.TaintRaised += (r, taint, _) => taints[r.AgentId] = taint;
            return e;
        });
        foreach (var e in executors.Values) await e.StartAsync();

        // ts 1000 and 1500 reach the primary's process; ts 5000 waits in the merge (lateness 1000 ms).
        FakeProcess primaryProcess;
        try { primaryProcess = await Until(() => launcher.ByAgent.GetValueOrDefault("g1/agg/0") is { Lines.Count: 2 } p ? p : null); }
        catch (TimeoutException) { throw new Exception(string.Join("\n", log) + "\nlines: " + string.Join("|", launcher.ByAgent.Select(kv => kv.Key + "=" + kv.Value.Lines.Count))); }
        // The taint is reported after the deliveries are written, so it can trail the lines by a moment.
        Assert.Equal("secret", await Until(() => taints.GetValueOrDefault("g1/agg/0")));
        var runner = executors[primaryHost].Runners.Single(r => r.AgentId == "g1/agg/0");
        var cp = await runner.CheckpointAsync();
        Assert.NotNull(cp);
        Assert.Equal("2", Encoding.UTF8.GetString(cp!.Process!.Data));
        await Until(() => executors[standbyHost].LatestCheckpoint("g1/agg/0"));

        // The primary's host fails; the controller promotes the standby.
        await executors[primaryHost].StopAsync(TimeSpan.FromSeconds(1));
        var next = scheduler.Replan(g, plan, new ReplanRequest(AvoidHosts: new HashSet<string> { primaryHost }, Trigger: "failover",
            Promote: new HashSet<string> { "g1/agg/0" }));
        Assert.Equal(standbyHost, next.Agent("g1/agg/0").HostId);
        foreach (var (host, e) in executors.Where(kv => kv.Key != primaryHost)) await e.ApplyPlanAsync(next);

        var takeover = executors[standbyHost].Runners.Single(r => r.AgentId == "g1/agg/0");
        var restoredProcess = launcher.ByAgent["g1/agg/0"];
        Assert.NotSame(primaryProcess, restoredProcess);
        Assert.Equal("2", restoredProcess.RestoredFrom);                            // process state
        Assert.Equal("secret", takeover.Sidecar.TaintName);                          // taint as of the failure
        Assert.Equal(5000, Assert.Single(takeover.Sidecar.ExportState().Merges["i"].Items).Ts);   // merge state
        Assert.Contains(log, m => m.Contains("took over from checkpoint 1"));
        Assert.Null(executors[standbyHost].LatestCheckpoint("g1/agg/0"));            // consumed

        foreach (var (host, e) in executors.Where(kv => kv.Key != primaryHost)) await e.StopAsync(TimeSpan.FromSeconds(1));
    }

    // Without process checkpoints, failover still restores middleware state and starts the process fresh.
    [Fact]
    public async Task CheckpointsWithoutProcessSupportCarryMiddlewareState()
    {
        var (g, plan, _) = Plan();
        var launcher = new FakeLauncher { CheckpointSupported = false };
        var host = plan.Agent("g1/agg/0").HostId!;
        var log = new ConcurrentQueue<string>();
        var e = new GraphExecutor(g, plan, host, "secret", new InMemoryPipeHost(), launcher, _ => { }, (l, m) => log.Enqueue(m));
        await e.StartAsync();
        var cp = await e.Runners.Single(r => r.AgentId == "g1/agg/0").CheckpointAsync();
        Assert.NotNull(cp);
        Assert.Null(cp!.Process);
        Assert.Contains(log, m => m.Contains("process checkpoints are not available"));
        await e.StopAsync(TimeSpan.FromSeconds(1));
    }

    private static async Task<T> Until<T>(Func<T?> probe) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is T value) return value;
            await Task.Delay(20);
        }
        throw new TimeoutException("condition not met");
    }

    private sealed class FakeProcess : IProcessEndpoint
    {
        public readonly List<string> Lines = new();
        public string? RestoredFrom;
        public bool CheckpointSupported = true;
        public string[] Script = Array.Empty<string>();
        public event Action<string>? StdoutLine;
        public event Action<string>? StderrLine { add { } remove { } }
        public event Action<int>? Exited { add { } remove { } }

        // The runner wires up the events before starting, so the script can run right away.
        public Task StartAsync(ProcessSnapshot? restoreFrom, CancellationToken ct)
        {
            if (restoreFrom != null) RestoredFrom = Encoding.UTF8.GetString(restoreFrom.Data);
            _ = Task.Run(() => { foreach (var line in Script) StdoutLine?.Invoke(line); });
            return Task.CompletedTask;
        }

        public Task WriteLineAsync(string line) { lock (Lines) Lines.Add(line); return Task.CompletedTask; }
        public Task CloseInputAsync() => Task.CompletedTask;
        public Task StopAsync(TimeSpan grace) => Task.CompletedTask;
        public Task<ProcessSnapshot> CheckpointAsync(CancellationToken ct)
        {
            if (!CheckpointSupported) throw new NotSupportedException("fake");
            lock (Lines) return Task.FromResult(new ProcessSnapshot(Encoding.UTF8.GetBytes(Lines.Count.ToString())));
        }
    }

    private sealed class FakeLauncher : IProcessFactory
    {
        public readonly ConcurrentDictionary<string, FakeProcess> ByAgent = new();
        public readonly Dictionary<string, string[]> Scripts = new();
        public bool CheckpointSupported = true;

        public IProcessEndpoint CreateProcess(string agentId, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> environment) =>
            ByAgent[agentId] = new FakeProcess { CheckpointSupported = CheckpointSupported, Script = Scripts.GetValueOrDefault(argv[1]) ?? Array.Empty<string>() };
    }
}
