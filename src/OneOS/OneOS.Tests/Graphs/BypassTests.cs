using System.Collections.Concurrent;
using OneOS.Runtime;
using OneOS.Runtime.Graphs;
using OneOS.Runtime.Language;
using OneOS.Runtime.Monitoring;
using OneOS.Runtime.Scheduling;
using OneOS.Tests.Language;
using OneOS.Tests.Scheduling;

namespace OneOS.Tests.Graphs;

// Bypass edges (plan step 6.5b): edges that need nothing from the sidecar carry raw byte streams.
public class BypassTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oneos-bypass-" + Guid.NewGuid().ToString("N")[..8]);
    public BypassTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Head = "labels { public < secret; }\nclock t: u64 unit ms;\ntype m { v: string }\ntype r { ts: t }\n";

    private static Dictionary<string, bool> Bypass(string topology, string policy = "", SchedulerOptions? options = null, ClusterSnapshot? cluster = null)
    {
        var compiled = TestUtil.Compile(Head + $"graph g () {{ topology {{ {topology} }} policy {{ {policy} }} }}");
        TestUtil.AssertNoErrors(compiled);
        var (bound, _) = GraphBinder.Bind(compiled.Graph("g")!, Array.Empty<object?>());
        var plan = Sched.Plan(bound!, cluster ?? Sched.Cluster(Sched.Host("h1", "secret"), Sched.Host("h2", "secret")), options: options);
        return plan.Pipes.GroupBy(p => p.EdgeName).ToDictionary(g => g.Key, g => g.All(p => p.Bypass));
    }

    [Fact]
    public void OneToOneEdgesWithNothingForTheSidecarBypassIt()
    {
        var b = Bypass("node a () => (o: m) = process('a'); node b (i: m) => (o: m) = process('b'); node k[] (i: m) = process('k');"
            + " edge ab: a --> b; edge bk: b --> k;", "partitions(2): k; label(secret): a.o;");
        Assert.True(b["ab"]);        // one label, checks elided
        Assert.False(b["bk"]);       // load-balanced into k[]
    }

    [Theory]
    [InlineData("node a () => (o: r ordered) = process('a'); node b (i: r ordered) = process('b'); edge e: a --> b;", "")]   // ordering
    [InlineData("node a () => (o: m) = process('a'); node b (i: m) = process('b'); edge e: a --> b;", "always: b;")]        // standby
    [InlineData("node a () => (o: m) = process('a'); @json_lines node b (i: m) = process('b'); edge e: a --> b;", "")]       // port protocol
    [InlineData("node a () => (o: m) = process('a'); node c () => (o: m) = process('c'); node b (i: m) = process('b'); edge e: a --> b; edge c --> b;", "")]   // fan-in
    [InlineData("node a () => (o: m) = process('a'); node b (i: m) = process('b'); edge e: a --> b;", "label(n: m => n.v == 'x' ? secret : public): a.o;")]   // per-message labels
    [InlineData("node a () => (o: m) = process('a'); node b (i: m) = process('b'); edge e: a --> b;", "label(dynamic: public..secret): a;")]   // dynamic
    public void EdgesThatNeedTheSidecarKeepIt(string topology, string policy) => Assert.False(Bypass(topology, policy)["e"]);

    [Fact]
    public void BypassCanBeTurnedOffExceptForUnsegmentableStreams()
    {
        var b = Bypass("node a () => (o: m, raw: bytes) = process('a'); node b (i: m, raw: bytes) = process('b'); edge e: a.o --> b.i; edge r: a.raw --> b.raw;",
            "always: b;", Sched.Options with { BypassEdges = false });
        Assert.False(b["e"]);
        Assert.True(b["r"]);         // unsegmentable: raw even with a standby (W0902) and bypass off
    }

    [Fact]
    public void ReportShowsBypassEdges()
    {
        var compiled = TestUtil.Compile(Head + "graph g () { topology { node a () => (o: m) = process('a'); node b (i: m) = process('b'); edge e: a --> b; } }");
        var (bound, _) = GraphBinder.Bind(compiled.Graph("g")!, Array.Empty<object?>());
        var plan = Sched.Plan(bound!, Sched.Cluster(Sched.Host("h")));
        Assert.Contains("BYPASS", GraphReport.Format(plan, bound!, Sched.Cluster(Sched.Host("h"))));
    }

    // examples/dsl/textproc.osh: unmodified POSIX tools on `lines` ports (plan 6.6).
    [Fact]
    public void TextprocExampleLoadBalancesTheToolsAndBypassesTheLastEdge()
    {
        var (bound, _) = GraphBinder.Bind(TestUtil.CompileExample("textproc.osh").Graph("textproc")!, new object?[] { "words.py", "out.txt" });
        var plan = Sched.Plan(bound!, Sched.Cluster(Sched.Host("h1"), Sched.Host("h2")));
        Assert.Equal(2, plan.Agents.Count(a => a.NodeName == "up"));
        Assert.All(plan.Agents.SelectMany(a => a.Ports), p => Assert.Equal(FramingKind.Lines, p.Framing.Kind));
        var bypass = plan.Pipes.GroupBy(p => p.EdgeName).ToDictionary(g => g.Key, g => g.All(p => p.Bypass));
        Assert.Equal(new[] { false, false, true }, new[] { "src.o__up.i", "up.o__keep.i", "keep.o__sink.i" }.Select(e => bypass[e]));
    }

    // Records and a binary stream reach the sink unmodified over raw pipes, metered as bytes; the sink's
    // taint takes the streams' label as they connect.
    [Fact]
    public async Task BypassEdgesCarryBytesUnmodifiedAndMeterThem()
    {
        File.WriteAllText(Path.Combine(_dir, "src.py"), """
            import os, sys, json, time
            raw = int(os.environ["ONEOS_PORT_raw"])
            for i in range(5):
                line = json.dumps({"v": f"item {i}"}) + "\n"
                os.write(1, line[:4].encode()); time.sleep(0.01); os.write(1, line[4:].encode())
                os.write(raw, bytes([0, 255, i, 10, 13]))
            sys.stdin.read()
            """);
        File.WriteAllText(Path.Combine(_dir, "sink.py"), """
            import os, sys, threading
            lines = open(sys.argv[1], "a"); blob = open(sys.argv[2], "a"); data = b""
            def raw():
                global data
                fd = int(os.environ["ONEOS_PORT_raw"])
                while True:
                    b = os.read(fd, 65536)
                    if not b: break
                    data += b
                    blob.write(data.hex() + "\n"); blob.flush()
            threading.Thread(target=raw, daemon=True).start()
            for line in sys.stdin:
                lines.write(line); lines.flush()
            """);
        var linesFile = Path.Combine(_dir, "lines.txt");
        var blobFile = Path.Combine(_dir, "blob.txt");
        var compiled = TestUtil.Compile("""
            labels { public < secret; }
            type m { v: string }
            graph g(a: string, b: string) { topology {
              node src () => (o: m, raw: bytes) = process('python3', 'src.py');
              node sink (i: m, raw: bytes) = process('python3', 'sink.py', a, b);
              edge e: src.o --> sink.i;  edge r: src.raw --> sink.raw; }
              policy { label(secret): src.o; label(secret): src.raw; } }
            """);
        TestUtil.AssertNoErrors(compiled);
        var (bound, _) = GraphBinder.Bind(compiled.Graph("g")!, new object?[] { linesFile, blobFile });
        var plan = Sched.Plan(bound!, Sched.Cluster(Sched.Host("h", "secret")));
        Assert.All(plan.Pipes, p => Assert.True(p.Bypass));
        var metrics = new RuntimeMetrics();
        var log = new ConcurrentQueue<string>();
        var exec = new GraphExecutor(bound!, plan, "h", "secret", new InMemoryPipeHost(), new OsProcessFactory(_dir), _ => { }, (l, m) => log.Enqueue(m), metrics: metrics);
        await exec.StartAsync();
        Assert.Equal("secret", exec.Runners.Single(r => r.AgentId == "g1/sink/0").Sidecar.TaintName);   // before any data

        var deadline = DateTime.UtcNow.AddSeconds(20);
        string[] Read(string f) => File.Exists(f) ? File.ReadAllLines(f) : Array.Empty<string>();
        while (DateTime.UtcNow < deadline && (Read(linesFile).Length < 5 || Read(blobFile).LastOrDefault()?.Length != 50)) await Task.Delay(100);
        var windows = metrics.DrainPipes(TimeSpan.FromSeconds(1));
        await exec.StopAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(Enumerable.Range(0, 5).Select(i => $$"""{"v": "item {{i}}"}"""), Read(linesFile));
        Assert.Equal(string.Concat(Enumerable.Range(0, 5).Select(i => $"00ff{i:x2}0a0d")), Read(blobFile).Last());
        Assert.All(windows, w => Assert.Equal("raw", w.Meta.Mode));
        Assert.Equal(5 * 5, windows.Where(w => w.Meta.Edge == "r" && w.End == PipeEnd.In).Sum(w => w.Bytes));
        // Bytes, not messages: how the lines were chunked depends on how the OS coalesced the split writes.
        Assert.Equal(5 * """{"v": "item 0"}""".Length + 5, windows.Where(w => w.Meta.Edge == "e" && w.End == PipeEnd.Out).Sum(w => w.Bytes));
    }
}
