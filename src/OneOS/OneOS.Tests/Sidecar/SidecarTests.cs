using System.Text;
using System.Text.Json;
using OneOS.Runtime;
using OneOS.Runtime.Language;
using OneOS.Runtime.Language.Models;
using OneOS.Runtime.Scheduling;
using OneOS.Runtime.Sidecar;
using OneOS.Tests.Language;
using OneOS.Tests.Scheduling;

namespace OneOS.Tests.Sidecar;

public class KeyRoutingTests
{
    [Theory]
    [InlineData("", 0xEF46DB3751D8E999UL)]
    [InlineData("a", 0xD24EC4F1A98C6E5BUL)]
    [InlineData("abc", 0x44BC2CF5AD770999UL)]
    [InlineData("Nobody inspects the spammish repetition", 0xFBCEA83C8A378BF1UL)]
    public void XxHash64TestVectors(string input, ulong expected) => Assert.Equal(expected, XxHash64.Hash(Encoding.UTF8.GetBytes(input)));

    [Theory]
    [InlineData(4, 128)]
    [InlineData(3, 128)]
    [InlineData(7, 64)]
    public void OwnedRangesMatchTheRoutingRule(int n, int g)
    {
        var ranges = Enumerable.Range(0, n).Select(i => KeyRouter.OwnedGroups(i, n, g)).ToList();
        Assert.Equal(0, ranges[0].From);
        Assert.Equal(g, ranges[^1].To);
        for (int i = 0; i + 1 < n; i++) Assert.Equal(ranges[i].To, ranges[i + 1].From);
        for (int k = 0; k < g; k++)
        {
            int owner = KeyRouter.Instance(k, n, g);
            Assert.InRange(k, ranges[owner].From, ranges[owner].To - 1);
        }
    }

    [Fact]
    public void CanonicalEncodingIsInjective()
    {
        JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        var ab = KeyRouter.Canonical(new[] { J("\"a\""), J("\"b\"") });
        var a_b = KeyRouter.Canonical(new[] { J("\"ab\"") });
        var num = KeyRouter.Canonical(new[] { J("1") });
        var str = KeyRouter.Canonical(new[] { J("\"1\"") });
        Assert.NotEqual(ab, a_b);
        Assert.NotEqual(num, str);
        Assert.Equal(KeyRouter.KeyGroup(new[] { J("\"cam-7\"") }, 128), KeyRouter.KeyGroup(new[] { J("\"cam-7\"") }, 128));
    }
}

public class OrderingTests
{
    private static readonly List<(string Kind, string Detail)> Reports = new();
    private static Buffered<string> M(int stream, long ts, string id, string? per = null, long size = 10) => new(stream, ts, per, size, id);

    private static OrderedMerge<string> Merge(long lateness = 0, TimeSpan? idle = null, long maxBuffer = 1 << 20, OnLateMode onLate = OnLateMode.Drop, bool per = false) =>
        new(lateness, idle, maxBuffer, onLate, per, (k, d) => Reports.Add((k, d)));

    private static List<string> Ids(IEnumerable<(MergeOutcome, Buffered<string>)> xs) => xs.Select(x => x.Item2.Item).ToList();

    [Fact]
    public void MergesTwoStreamsInClockOrder()
    {
        var m = Merge();
        // Streams are inactive until their first message, so a lone stream releases immediately.
        Assert.Equal(new[] { "a10" }, Ids(m.Offer(M(0, 10, "a10"), 0)));
        Assert.Empty(m.Offer(M(1, 12, "b12"), 0));                  // W = min(10, 12)
        Assert.Equal(new[] { "b12" }, Ids(m.Offer(M(0, 15, "a15"), 0)));   // W = min(15, 12)... then 12
        Assert.Equal(new[] { "a15" }, Ids(m.Offer(M(1, 20, "b20"), 0)));   // W = 15
    }

    [Fact]
    public void LatenessHoldsMessagesBack()
    {
        var m = Merge(lateness: 50);
        Assert.Empty(m.Offer(M(0, 100, "x100"), 0));               // W = 50
        Assert.Empty(m.Offer(M(0, 120, "x120"), 0));               // W = 70
        Assert.Equal(new[] { "x100" }, Ids(m.Offer(M(0, 160, "x160"), 0)));   // W = 110
    }

    [Fact]
    public void IdleStreamsStopHoldingTheWatermark()
    {
        var m = Merge(idle: TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { "a10" }, Ids(m.Offer(M(0, 10, "a10"), 0)));
        Assert.Empty(m.Offer(M(1, 50, "b50"), TimeSpan.FromSeconds(1.5).Ticks));   // stream 0 still holds W at 10
        // Stream 0 has been quiet for the idle timeout; only stream 1 holds W now.
        Assert.Equal(new[] { "b50" }, Ids(m.Tick(TimeSpan.FromSeconds(2).Ticks)));
    }

    [Theory]
    [InlineData(OnLateMode.Drop, 0)]
    [InlineData(OnLateMode.Pass, 1)]
    [InlineData(OnLateMode.Route, 1)]
    public void LateMessages(OnLateMode mode, int handedOn)
    {
        var m = Merge(onLate: mode);
        m.Offer(M(0, 10, "a10"), 0);
        m.Offer(M(0, 20, "a20"), 0);
        var late = m.Offer(M(1, 5, "late"), 0).Where(x => x.Item2.Item == "late").ToList();
        Assert.Equal(handedOn, late.Count);
        if (mode == OnLateMode.Route) Assert.Equal(MergeOutcome.Routed, late[0].Item1);
    }

    [Fact]
    public void PerKeyLateness()
    {
        var m = Merge(per: true);
        m.Offer(M(0, 10, "x10", per: "x"), 0);
        m.Offer(M(0, 20, "x20", per: "x"), 0);
        // y is behind x's clock, but lateness only compares messages with the same `per` value.
        Assert.Equal(new[] { "y15" }, Ids(m.Offer(M(0, 15, "y15", per: "y"), 0)));
        Assert.Empty(m.Offer(M(0, 12, "x12", per: "x"), 0));      // late for x: dropped
    }

    [Fact]
    public void BufferOverflowReleasesEarliestAndReports()
    {
        Reports.Clear();
        var m = Merge(lateness: 1000, maxBuffer: 25);
        m.Offer(M(0, 1, "a"), 0);
        m.Offer(M(0, 2, "b"), 0);
        Assert.Equal(new[] { "a" }, Ids(m.Offer(M(0, 3, "c"), 0)));
        Assert.Contains(Reports, r => r.Kind == "order_buffer_overflow");
        Assert.Equal(new[] { "b", "c" }, m.Flush().Select(x => x.Item));
    }

    [Fact]
    public void SequencedPortReleasesInOrderAndSkipsGaps()
    {
        Reports.Clear();
        var s = new SequencedPort<string>(TimeSpan.FromSeconds(2), 1 << 20, (k, d) => Reports.Add((k, d)));
        Assert.Empty(s.Offer(M(0, 1, "s1"), 0));
        Assert.Equal(new[] { "s0", "s1" }, s.Offer(M(0, 0, "s0"), 0).Select(x => x.Item));
        Assert.Empty(s.Offer(M(0, 3, "s3"), 0));                    // 2 is missing
        Assert.Empty(s.Tick(TimeSpan.FromSeconds(1).Ticks));
        Assert.Equal(new[] { "s3" }, s.Tick(TimeSpan.FromSeconds(3).Ticks).Select(x => x.Item));
        Assert.Contains(Reports, r => r.Kind == "sequence_gap_skipped");
        Assert.Equal(4, s.Next);
    }
}

public class AgentSidecarTests
{
    private static readonly ClusterSnapshot ThreeLevels = Sched.Cluster(
        Sched.Host("public-1", "public"), Sched.Host("public-2", "public"), Sched.Host("internal-1", "internal"),
        Sched.Host("internal-2", "internal"), Sched.Host("secret-1", "secret"), Sched.Host("secret-2", "secret"));

    private sealed class Deployment
    {
        public required CompiledGraph Graph;
        public required GraphInstanceInfo Instance;
        public ClusterSnapshot Cluster = ClusterSnapshot.Empty;
        public List<SidecarEvent> Events = new();
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;

        public AgentSidecar Sidecar(string agentId)
        {
            var a = Instance.Agent(agentId);
            var host = Cluster.Hosts.FirstOrDefault(h => h.HostId == a.HostId)?.Label;
            return new AgentSidecar(new SidecarConfig(Graph, Instance, a, host), Events.Add, () => Now);
        }
    }

    private static Deployment Deploy(string example, string graph, object?[] args, ClusterSnapshot cluster) =>
        DeploySource(File.ReadAllText(TestUtil.ExamplePath(example)), graph, args, cluster);

    private static Deployment DeploySource(string source, string graph, object?[] args, ClusterSnapshot cluster, SchedulerOptions? options = null)
    {
        var (bound, diags) = GraphBinder.Bind(TestUtil.Compile(source).Graph(graph)!, args);
        Assert.NotNull(bound);
        return new Deployment { Graph = bound!, Instance = Sched.Plan(bound!, cluster, options: options), Cluster = cluster };
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    // A message as it arrives on a pipe: an Envelope with a label and a UTF-8 JSON payload.
    private static Envelope Env(string label, JsonElement payload) =>
        new() { Label = label, Payload = Encoding.UTF8.GetBytes(payload.GetRawText()) };

    private static Deployment Camera() => Deploy("camera.osh", "foo", new object?[] { "a", "b" }, ThreeLevels);

    [Fact]
    public void SourceLabellerAndKeyedRouting()
    {
        var d = Camera();
        var s1 = d.Sidecar("g1/s1/0");
        var employee = s1.Emit("video", J("""{ "cam": "cam-7", "ts": 1000, "group": "employee", "data": "" }"""));
        var visitor = s1.Emit("video", J("""{ "cam": "cam-7", "ts": 1001, "group": "visitor", "data": "" }"""));
        Assert.Equal("internal", Assert.Single(employee).Envelope.Label);
        Assert.Equal("public", Assert.Single(visitor).Envelope.Label);

        // Keyed routing: the same camera always reaches the det instance owning its key group.
        int group = KeyRouter.KeyGroup(new[] { J("\"cam-7\"") }, 128);
        var owner = d.Instance.AgentsOf("det").Single(a => a.Role == AgentRole.Primary && group >= a.OwnedKeyGroups!.From && group < a.OwnedKeyGroups.To);
        Assert.Equal(owner.AgentId, employee[0].DestinationAgentId);
        Assert.Equal(owner.AgentId, visitor[0].DestinationAgentId);
    }

    [Fact]
    public void DeliveryChecksAndTaint()
    {
        var d = Camera();
        var sink = d.Sidecar("g1/sink/0");
        var fromSecretLane = d.Instance.Pipes.First(p => p.DestinationAgentId == "g1/sink/0" && p.DestinationPort == "in" && p.SourceAgentId.Contains("anon@secret"));
        // sink.in has ceiling `internal`: a secret message is not delivered.
        Assert.Empty(sink.Receive(fromSecretLane.PipeId, Env("secret", J("""{ "cam": "c", "ts": 1, "redacted": false, "payload": {} }"""))));
        Assert.Contains(d.Events, e => e.Kind == "label_violation" && e.Detail.Contains("port ceiling"));
        Assert.Equal("public", sink.TaintName);

        // Internal messages pass; ordering holds them until the watermark (200ms lateness) passes them.
        Assert.Empty(sink.Receive(fromSecretLane.PipeId, Env("internal", J("""{ "cam": "c", "ts": 1000, "redacted": false, "payload": {} }"""))));
        var released = sink.Receive(fromSecretLane.PipeId, Env("public", J("""{ "cam": "c", "ts": 1300, "redacted": true, "payload": {} }""")));
        Assert.Equal("internal", Assert.Single(released).Envelope.Label);
        Assert.Equal("internal", sink.TaintName);
        Assert.Equal("public", Assert.Single(sink.Shutdown()).Envelope.Label);
    }

    [Fact]
    public void OneToOneLabellerDeclassifiesAndRoutesDirect()
    {
        var d = Camera();
        var anon = d.Sidecar("g1/anon@secret/0");
        var inbound = d.Instance.Pipes.First(p => p.DestinationAgentId == "g1/anon@secret/0");
        Assert.Single(anon.Receive(inbound.PipeId, Env("secret", J("""{ "cam": "c", "ts": 5, "redacted": false, "payload": {} }"""))));
        Assert.Single(anon.Receive(inbound.PipeId, Env("internal", J("""{ "cam": "c", "ts": 6, "redacted": false, "payload": {} }"""))));
        // anonymized(e, in) = e.redacted ? public : in, with `in` the answered input's label (@one_to_one).
        var first = anon.Emit("out", J("""{ "cam": "c", "ts": 5, "redacted": true, "payload": {} }"""));
        var second = anon.Emit("out", J("""{ "cam": "c", "ts": 6, "redacted": false, "payload": {} }"""));
        Assert.All(first, o => Assert.Equal("public", o.Envelope.Label));
        Assert.All(second, o => Assert.Equal("internal", o.Envelope.Label));
        Assert.Contains(first, o => o.DestinationAgentId == "g1/sink/0" && o.DestinationPort == "in");
        Assert.Contains(first, o => o.DestinationPort == "late_in");     // the implicit route edge is a real pipe too
        Assert.Equal("secret", anon.TaintName);
    }

    [Fact]
    public void LoadBalancedRoutingOnlyUsesCompliantLanes()
    {
        var d = Camera();
        var det = d.Sidecar("g1/det/0");
        var inbound = d.Instance.Pipes.First(p => p.DestinationAgentId == "g1/det/0" && p.EdgeName == "e2");
        // det.in is ordered with 50ms lateness: the first message is handed on once the watermark passes it.
        Assert.Empty(det.Receive(inbound.PipeId, Env("secret", J("""{ "cam": "c", "ts": 1, "group": "g", "data": "" }"""))));
        Assert.Single(det.Receive(inbound.PipeId, Env("secret", J("""{ "cam": "c", "ts": 100, "group": "g", "data": "" }"""))));
        Assert.Equal("secret", det.TaintName);
        for (int i = 0; i < 20; i++)
        {
            var out1 = det.Emit("out", J($$"""{ "cam": "c", "ts": {{i}}, "redacted": false, "payload": {} }"""));
            var o = Assert.Single(out1);
            Assert.Equal("secret", o.Envelope.Label);                  // det's taint is secret now
            Assert.Contains("anon@secret", o.DestinationAgentId);
        }
    }

    [Fact]
    public void NoCompliantRouteQueuesUntilATableSwapAllowsIt()
    {
        var d = Camera();
        var det = d.Sidecar("g1/det/0");
        det.RestoreTaint("secret");
        var table = d.Instance.RoutingTables.First(t => t.SourceAgentId == "g1/det/0");
        // Membership change: both secret-lane instances are gone.
        var survivors = table.ReceiverAgentIds.Where(r => !r.Contains("anon@secret")).ToList();
        var shrunk = LoadBalancer.Solve(new LoadBalanceProblem(table.SourceAgentId, table.SourcePort, table.EdgeName, table.Labels,
            table.Labels.ToDictionary(l => l, _ => 1.0 / 3), survivors,
            survivors.ToDictionary(r => r, r => (IReadOnlySet<string>)d.Instance.Pipes.First(p => p.SourceAgentId == "g1/det/0" && p.DestinationAgentId == r).Allowed.ToHashSet()),
            survivors.ToDictionary(r => r, _ => 1.0 / survivors.Count), 2), "membership", TimeSpan.FromSeconds(5));
        det.SwapRoutingTable(shrunk);
        Assert.Empty(det.Emit("out", J("""{ "cam": "c", "ts": 1, "redacted": false, "payload": {} }""")));
        Assert.Equal(1, det.QueuedCount);
        Assert.Contains(d.Events, e => e.Kind == "no_compliant_route");

        // The lane comes back: the queued message goes out.
        var sent = det.SwapRoutingTable(table);
        Assert.Contains("anon@secret", Assert.Single(sent).DestinationAgentId);
        Assert.Equal(0, det.QueuedCount);
    }

    [Fact]
    public void OrderPromisesAreCheckedButNeverDrop()
    {
        var d = Camera();
        var s2 = d.Sidecar("g1/s2/0");
        Assert.Single(s2.Emit("video", J("""{ "cam": "c", "ts": 1000, "group": "g", "data": "" }""")));
        Assert.Single(s2.Emit("video", J("""{ "cam": "c", "ts": 960, "group": "g", "data": "" }""")));   // within 50ms: fine
        Assert.DoesNotContain(d.Events, e => e.Kind == "order_promise_violated");
        Assert.Single(s2.Emit("video", J("""{ "cam": "c", "ts": 900, "group": "g", "data": "" }""")));   // beyond: reported, still sent
        Assert.Contains(d.Events, e => e.Kind == "order_promise_violated");
    }

    [Fact]
    public void CompartmentIsolationStopsMixing()
    {
        // An unpartitioned node fed from both compartments can't be laned; runtime isolation keeps it to one.
        var src = """
            labels { a_public < a_internal; b_public < b_internal; }
            key tenant: string;
            type rec { t: tenant, v: string }
            graph tenants () {
              topology {
                node mixed () => (out: rec) = process('node', 'mixed.js');
                node agg (in: rec) = process('node', 'aggregate.js');
                edge a1: mixed --> agg;
              }
              policy { label((r: rec) => r.t == 'x' ? a_internal : b_internal): mixed.out; }
            }
            """;
        var cluster = Sched.Cluster(Sched.Host("a", "a_internal"), Sched.Host("b", "b_internal"));
        var d = DeploySource(src, "tenants", Array.Empty<object?>(), cluster);
        var agg = d.Sidecar("g1/agg/0");
        var pipe = d.Instance.Pipes.Single(p => p.DestinationAgentId == "g1/agg/0");
        var hostLabel = d.Instance.Agent("g1/agg/0").HostId == "a" ? "a_internal" : "b_internal";
        var other = hostLabel == "a_internal" ? "b_internal" : "a_internal";
        Assert.Single(agg.Receive(pipe.PipeId, Env(hostLabel, J("""{ "t": "x", "v": "1" }"""))));
        Assert.Empty(agg.Receive(pipe.PipeId, Env(other, J("""{ "t": "y", "v": "2" }"""))));
        Assert.Contains(d.Events, e => e.Kind == "label_violation");
        Assert.Equal(hostLabel, agg.TaintName);
    }

    // F3: keyed routing into compartment lanes picks the key-group owner in the lane admitting the label.
    [Fact]
    public void KeyedRoutingPicksTheCompartmentLane()
    {
        var cluster = Sched.Cluster(Sched.Host("a", "a_internal", cpu: 8000), Sched.Host("b", "b_internal", cpu: 8000));
        var src = File.ReadAllText(TestUtil.ExamplePath("tenants.osh"))
            .Replace("edge a1: a_src --> agg;", "node mixed () => (out: rec) = process('node', 'mixed.js');\n    edge a1: a_src --> agg;\n    edge m1: mixed --> agg;")
            .Replace("label(a_internal): a_src.out;", "label(a_internal): a_src.out;\n    label((r: rec) => r.t == 'x' ? a_internal : b_internal): mixed.out;");
        var d = DeploySource(src, "tenants", Array.Empty<object?>(), cluster);
        var mixed = d.Sidecar("g1/mixed/0");
        var toA = Assert.Single(mixed.Emit("out", J("""{ "t": "x", "v": "1" }""")));
        var toB = Assert.Single(mixed.Emit("out", J("""{ "t": "y", "v": "2" }""")));
        Assert.StartsWith("g1/agg@a_internal/", toA.DestinationAgentId);
        Assert.StartsWith("g1/agg@b_internal/", toB.DestinationAgentId);
        var group = KeyRouter.KeyGroup(KeyRouter.ExtractKey(J("""{ "t": "x", "v": "1" }"""), new[] { new[] { "t" } }), 128);
        var owner = d.Instance.Agent(toA.DestinationAgentId).OwnedKeyGroups!;
        Assert.InRange(group, owner.From, owner.To - 1);
        Assert.Single(d.Sidecar(toA.DestinationAgentId).Receive(toA.PipeId, toA.Envelope));
    }

    // Profiling: a message's lineage starts at its source node and is passed on through @one_to_one nodes
    // (the answered input) and other nodes (their latest input); a terminal node reports the end-to-end latency.
    [Fact]
    public void EndToEndLatencyFollowsTheLineageToTheTerminalNode()
    {
        var src = """
            type m { k: string, v: i64 }
            graph g () {
              topology {
                node s () => (o: m) = process('python3', 's.py');
                @one_to_one
                node f (i: m) => (o: m) = process('python3', 'f.py');
                node agg (i: m) => (o: m) = process('python3', 'a.py');
                node k (i: m) = process('python3', 'k.py');
                edge s --> f; edge f --> agg; edge agg --> k;
              }
            }
            """;
        // With bypass edges these one-to-one edges would skip the sidecar, and lineage with them.
        var d = DeploySource(src, "g", Array.Empty<object?>(), Sched.Cluster(Sched.Host("h")), Sched.Options with { BypassEdges = false });
        var s = d.Sidecar("g1/s/0");
        var f = d.Sidecar("g1/f/0");
        var agg = d.Sidecar("g1/agg/0");
        var k = d.Sidecar("g1/k/0");
        var seen = new List<(string From, string To, TimeSpan Latency)>();
        k.EndToEnd += (from, to, latency) => seen.Add((from, to, latency));
        var arrived = new List<(string From, string To, DateTimeOffset Origin, DateTimeOffset At, long Bytes)>();
        k.Arrived += (from, to, origin, at, bytes) => arrived.Add((from, to, origin, at, bytes));

        d.Now = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var first = Assert.Single(s.Emit("o", J("""{ "k": "a", "v": 1 }""")));
        Assert.Equal("s", first.Envelope.OriginNode);
        d.Now = d.Now.AddMilliseconds(20);
        var second = Assert.Single(s.Emit("o", J("""{ "k": "a", "v": 2 }""")));

        d.Now = d.Now.AddMilliseconds(5);
        f.Receive(first.PipeId, first.Envelope);
        f.Receive(second.PipeId, second.Envelope);
        var fOut1 = Assert.Single(f.Emit("o", J("""{ "k": "a", "v": 1 }""")));   // answers the first input
        Assert.Equal(first.Envelope.OriginTimestamp, fOut1.Envelope.OriginTimestamp);

        agg.Receive(fOut1.PipeId, fOut1.Envelope);
        var fOut2 = Assert.Single(f.Emit("o", J("""{ "k": "a", "v": 2 }""")));
        agg.Receive(fOut2.PipeId, fOut2.Envelope);
        var aggOut = Assert.Single(agg.Emit("o", J("""{ "k": "a", "v": 3 }""")));   // latest input: the second
        Assert.Equal(second.Envelope.OriginTimestamp, aggOut.Envelope.OriginTimestamp);

        d.Now = d.Now.AddMilliseconds(100);
        k.Receive(aggOut.PipeId, aggOut.Envelope);
        var e2e = Assert.Single(seen);
        Assert.Equal(("s", "k"), (e2e.From, e2e.To));
        Assert.Equal(TimeSpan.FromMilliseconds(105), e2e.Latency);   // from the second emission at s
        var a = Assert.Single(arrived);
        Assert.Equal(("s", "k", second.Envelope.OriginTimestamp, d.Now, (long)aggOut.Envelope.Payload.Length), (a.From, a.To, a.Origin, a.At, a.Bytes));
    }

    [Fact]
    public void SequencingThroughParallelOneToOneNodes()
    {
        var cluster = Sched.Cluster(Sched.Host("h1"), Sched.Host("h2"));
        var d = Deploy("jpeg.osh", "jpeg_pipeline", Array.Empty<object?>(), cluster);
        var cam = d.Sidecar("g1/cam/0");
        var decoders = d.Instance.AgentsOf("dec").Select(a => d.Sidecar(a.AgentId)).ToList();
        var sink = d.Sidecar("g1/sink/0");

        // The origin stamps 0, 1, 2; frames spread over decoders; decoders answer in their own order.
        var frames = Enumerable.Range(0, 3).Select(i => Assert.Single(cam.Emit("out", J($"\"frame{i}\"")))).ToList();
        Assert.Equal(new long?[] { 0, 1, 2 }, frames.Select(f => f.Envelope.Sequence));
        var byDecoder = frames.GroupBy(f => f.DestinationAgentId).ToList();
        var toSink = new List<Outgoing>();
        foreach (var grp in byDecoder.AsEnumerable().Reverse())
        {
            var dec = decoders[d.Instance.AgentsOf("dec").ToList().FindIndex(a => a.AgentId == grp.Key)];
            foreach (var f in grp) dec.Receive(f.PipeId, f.Envelope);
            foreach (var f in grp) toSink.AddRange(dec.Emit("out", J($"\"decoded{f.Envelope.Sequence}\"")));
        }
        var delivered = toSink.OrderByDescending(o => o.Envelope.Sequence).SelectMany(o => sink.Receive(o.PipeId, o.Envelope)).ToList();
        Assert.Equal(new[] { "\"decoded0\"", "\"decoded1\"", "\"decoded2\"" }, delivered.Select(x => AgentSidecar.Json(x.Envelope).GetRawText()));
    }

    // --- Payloads as bytes (plan step 6.4) ---

    [Fact]
    public void BinaryJpegFramesKeepTheirBytesAndOrderThroughSequencedReassembly()
    {
        var cluster = Sched.Cluster(Sched.Host("h1"), Sched.Host("h2"));
        var d = Deploy("jpeg.osh", "jpeg_pipeline", Array.Empty<object?>(), cluster);
        var cam = d.Sidecar("g1/cam/0");
        var decoders = d.Instance.AgentsOf("dec").ToDictionary(a => a.AgentId, a => d.Sidecar(a.AgentId));
        var sink = d.Sidecar("g1/sink/0");

        // Not JSON: raw JPEG-like bytes, which nothing on these format ports ever parses.
        byte[] Frame(int i) => new byte[] { 0xFF, 0xD8, (byte)i, 0x00, 0xFF, 0xD9 };
        var frames = Enumerable.Range(0, 4).Select(i => Assert.Single(cam.Emit("out", Frame(i)))).ToList();
        var toSink = new List<Outgoing>();
        foreach (var f in frames.AsEnumerable().Reverse())
        {
            var dec = decoders[f.DestinationAgentId];
            var got = Assert.Single(dec.Receive(f.PipeId, f.Envelope));
            Assert.Equal(Frame((int)f.Envelope.Sequence!), got.Envelope.Payload);
            toSink.AddRange(dec.Emit("out", got.Envelope.Payload.Reverse().ToArray()));
        }
        var delivered = toSink.SelectMany(o => sink.Receive(o.PipeId, o.Envelope)).ToList();
        Assert.Equal(Enumerable.Range(0, 4).Select(i => Convert.ToHexString(Frame(i).Reverse().ToArray())), delivered.Select(x => Convert.ToHexString(x.Envelope.Payload)));
        Assert.DoesNotContain(d.Events, e => e.Kind == "schema_mismatch");
    }

    private const string BlobGraph = """
        labels { public < internal < secret; }
        graph blobs () {
          topology {
            node src () => (out: blob) = process('src');
            node w[] (in: blob) = process('w');
            edge src --> w;
          }
          policy { partitions(3): w; label(secret): src.out; }
        }
        """;

    [Fact]
    public void LengthPrefixedBlobsAreLoadBalancedWithAConstantLabel()
    {
        var formats = FormatRegistry.CreateDefault();
        formats.Add(new FormatEntry("blob", Framing.Parse("length_prefix(4, le)"), new[] { "bytes" }));
        var program = new AppCompiler(formats).Compile(new[] { new SourceFile("blobs.osh", BlobGraph) });
        TestUtil.AssertNoErrors(program);
        var (bound, _) = GraphBinder.Bind(program.Graph("blobs")!, Array.Empty<object?>());
        var d = new Deployment { Graph = bound!, Instance = Sched.Plan(bound!, ThreeLevels), Cluster = ThreeLevels };
        Assert.Equal(FramingKind.LengthPrefix, d.Instance.Agent("g1/src/0").Ports.Single().Framing.Kind);

        var src = d.Sidecar("g1/src/0");
        var workers = d.Instance.AgentsOf("w").Where(a => a.Role == AgentRole.Primary).ToDictionary(a => a.AgentId, a => d.Sidecar(a.AgentId));
        var seen = new HashSet<string>();
        for (int i = 0; i < 30; i++)
        {
            var blob = new byte[] { 0x00, 0xFF, (byte)i, 0x7B };   // not JSON (0x7B is '{')
            var o = Assert.Single(src.Emit("out", blob));
            Assert.Equal("secret", o.Envelope.Label);
            seen.Add(o.DestinationAgentId);
            var got = Assert.Single(workers[o.DestinationAgentId].Receive(o.PipeId, o.Envelope));
            Assert.Equal(blob, got.Envelope.Payload);
        }
        Assert.True(seen.Count > 1, "load balancing should use more than one worker");
        Assert.All(seen, w => Assert.Equal("secret", workers[w].TaintName));
        Assert.DoesNotContain(d.Events, e => e.Kind is "schema_mismatch" or "label_violation");
    }

    [Fact]
    public void PayloadsAreParsedOnlyWhereFieldsAreRead()
    {
        var d = Camera();
        // s1.video has a field-reading labeller and keyed routing: a non-JSON payload is dropped and reported.
        var s1 = d.Sidecar("g1/s1/0");
        Assert.Empty(s1.Emit("video", "not json"u8.ToArray()));
        Assert.Contains(d.Events, e => e.Kind == "schema_mismatch" && e.Detail.Contains("s1.video"));

        // det.in is ordered (reads the clock field): the same on the receiving side.
        var det = d.Sidecar("g1/det/0");
        var inbound = d.Instance.Pipes.First(p => p.DestinationAgentId == "g1/det/0" && p.EdgeName == "e2");
        Assert.Empty(det.Receive(inbound.PipeId, new Envelope { Label = "public", Payload = "{ broken"u8.ToArray() }));
        Assert.Contains(d.Events, e => e.Kind == "schema_mismatch" && e.Detail.Contains("e2"));
    }

    // stderr (plan 6.5a): a @one_to_one node's stderr carries its taint and leaves the answer queue alone;
    // a source's carries what its data ports may carry.
    [Fact]
    public void StderrCarriesTheTaintAndAnswersNothing()
    {
        var d = DeploySource("""
            labels { public < secret; }
            type m { v: string }
            graph g () {
              topology {
                node s1 () => (o: m) = process('a');
                node s2 () => (o: m) = process('b');
                @one_to_one node f (i: m) => (o: m) = process('f');
                node k (i: m) = process('k');
                node log (i: lines) = process('l');
                edge s1 --> f; edge s2 --> f; edge f --> k;
                edge s2.stderr --> log; edge f.stderr --> log;
              }
              policy { label(public): s1.o; label(secret): s2.o; }
            }
            """, "g", Array.Empty<object?>(), ThreeLevels);
        var s2 = d.Sidecar("g1/s2/0");
        Assert.Equal("secret", Assert.Single(s2.Emit("stderr", "starting"u8.ToArray())).Envelope.Label);

        var f = d.Sidecar("g1/f/0");
        var into = d.Instance.Pipes.Where(p => p.DestinationAgentId == "g1/f/0").ToList();
        Assert.Single(f.Receive(into.Single(p => p.SourceAgentId == "g1/s1/0").PipeId, Env("public", J("""{ "v": "1" }"""))));
        Assert.Single(f.Receive(into.Single(p => p.SourceAgentId == "g1/s2/0").PipeId, Env("secret", J("""{ "v": "2" }"""))));
        Assert.Equal("secret", Assert.Single(f.Emit("stderr", "warning"u8.ToArray())).Envelope.Label);
        Assert.Equal("public", Assert.Single(f.Emit("o", J("""{ "v": "1" }"""))).Envelope.Label);   // still answers input 1
        Assert.Equal("secret", Assert.Single(f.Emit("o", J("""{ "v": "2" }"""))).Envelope.Label);
    }

    [Fact]
    public void ConcreteEvaluatorHandlesLabellerConstructs()
    {
        var L = TestUtil.Compile("labels { public < internal; internal < secret; }").Lattice;
        var (ast, _) = new AppParser().Parse("labels { a; } type r { n: i32, s: string, tags: list<string> } labeller f (x: r, y: label) = x.n >= 3 && x.s.startsWith('v') || x.tags.contains('vip') ? join(y, internal) : meet(y, internal);");
        var body = ast.Items.OfType<OneOS.Runtime.Language.Ast.LabellerDecl>().Single().Body;
        int Eval(string json, string y) => ConcreteEval.EvalLabel(body, new Dictionary<string, object?> { ["x"] = J(json), ["y"] = new LabelValue(L.IndexOf(y)) }, L);
        Assert.Equal("secret", L.Name(Eval("""{ "n": 5, "s": "vip", "tags": [] }""", "secret")));
        Assert.Equal("internal", L.Name(Eval("""{ "n": 1, "s": "x", "tags": ["vip"] }""", "public")));
        Assert.Equal("internal", L.Name(Eval("""{ "n": 1, "s": "x", "tags": [] }""", "secret")));
        Assert.Throws<EvalException>(() => Eval("""{ "n": 1 }""", "public"));
    }
}
