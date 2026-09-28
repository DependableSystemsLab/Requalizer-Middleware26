using System.Collections.Concurrent;
using OneOS.Runtime;
using OneOS.Runtime.Graphs;
using OneOS.Runtime.Language;
using OneOS.Runtime.Language.Models;
using OneOS.Runtime.Scheduling;
using OneOS.Runtime.Sidecar;
using OneOS.Tests.Language;
using OneOS.Tests.Scheduling;

namespace OneOS.Tests.Graphs;

// End to end on one host: real Python processes speaking the JSON-lines port protocol, sidecars,
// and the in-memory pipe transport.
public class ExecutorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oneos-exec-" + Guid.NewGuid().ToString("N")[..8]);

    public ExecutorTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "import sys, json\n" + body);
        return path;
    }

    private static async Task<List<string>> WaitForLines(string path, Func<List<string>, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            if (done(lines)) return lines;
            await Task.Delay(100);
        }
        return File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
    }

    [Fact]
    public async Task PipelineWithLanesKeysAndLabels()
    {
        // Typed streams: record ports are newline-delimited JSON on stdin and stdout, no port protocol.
        var src = Script("src.py", """
            for i in range(1, 9):
                print(json.dumps({"id": "abcd"[i % 4], "v": i}), flush=True)
            sys.stdin.read()
            """);
        var dbl = Script("dbl.py", """
            for line in sys.stdin:
                m = json.loads(line)
                print(json.dumps({"id": m["id"], "v": 2 * m["v"]}), flush=True)
            """);
        var agg = Script("agg.py", """
            sums = {}
            for line in sys.stdin:
                m = json.loads(line)
                sums[m["id"]] = sums.get(m["id"], 0) + m["v"]
                print(json.dumps({"id": m["id"], "v": sums[m["id"]]}), flush=True)
            """);
        var sink = Script("sink.py", """
            out = open(sys.argv[1], "a")
            for line in sys.stdin:
                m = json.loads(line)
                out.write(f"{m['id']} {m['v']}\n"); out.flush()
            """);
        var result = Path.Combine(_dir, "result.txt");

        var program = $$"""
            labels { public < secret; }
            key id: string;
            type num { id: id, v: i64 }
            graph calc(out: string) {
              topology {
                node src () => (out: num)                     = process('python3', '{{src}}');
                @one_to_one
                node dbl[] (in: num) => (out: num)            = process('python3', '{{dbl}}');
                node agg[id] (in: num) => (out: num)          = process('python3', '{{agg}}');
                node sink (in: num)                           = process('python3', '{{sink}}', out);
                edge src --> dbl;
                edge dbl --> agg;
                edge agg --> sink;
              }
              policy {
                partitions(1): dbl;
                partitions(2): agg;
                label(n: num => n.id == 'a' ? secret : public): src.out;
                label(public): agg;
              }
            }
            """;
        var compiled = TestUtil.Compile(program);
        TestUtil.AssertNoErrors(compiled);
        var (bound, _) = GraphBinder.Bind(compiled.Graph("calc")!, new object?[] { result });
        var plan = Sched.Plan(bound!, Sched.Cluster(Sched.Host("h", "secret")));
        Assert.Equal(new[] { "public", "secret" }, plan.Plan.Audit.Dift.Single(d => d.Node == "dbl").Lanes);

        var events = new ConcurrentQueue<SidecarEvent>();
        var log = new ConcurrentQueue<string>();
        var metrics = new OneOS.Runtime.Monitoring.RuntimeMetrics();
        var exec = new GraphExecutor(bound!, plan, "h", "secret", new InMemoryPipeHost(), new OsProcessFactory(_dir),
            events.Enqueue, (lvl, msg) => log.Enqueue($"{lvl}: {msg}"), metrics: metrics);
        await exec.StartAsync();
        Assert.All(exec.Runners, r => Assert.Equal(AgentState.Running, r.State));

        var lines = await WaitForLines(result, l => l.Count >= 6, TimeSpan.FromSeconds(20));
        await exec.StopAsync(TimeSpan.FromSeconds(2));

        var finals = lines.Select(l => l.Split(' ')).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Max(p => int.Parse(p[1])));
        Assert.True(finals.Count == 3, string.Join("\n", lines.Concat(log)));
        Assert.Equal(12, finals["b"]);   // 2·1 + 2·5
        Assert.Equal(16, finals["c"]);   // 2·2 + 2·6
        Assert.Equal(20, finals["d"]);   // 2·3 + 2·7
        Assert.False(finals.ContainsKey("a"));   // secret, stopped at agg's `public` instance ceiling
        Assert.Contains(events, e => e.Kind == "label_violation" && e.Detail.Contains("instance ceiling"));
        Assert.All(exec.Runners, r => Assert.Equal(AgentState.Stopped, r.State));

        // Monitoring: every pipe that carried messages was measured at both ends, with labels and latency;
        // messages reaching the terminal node `sink` report their end-to-end latency from `src`.
        var windows = metrics.DrainPipes(TimeSpan.FromSeconds(1));
        var srcOut = windows.Where(w => w.End == OneOS.Runtime.Monitoring.PipeEnd.Out && w.Meta.From == "g1/src/0").ToList();
        Assert.Equal(8, srcOut.Sum(w => w.Messages));
        Assert.Equal(new[] { "public", "secret" }, srcOut.SelectMany(w => w.Labels!.Keys).Distinct().OrderBy(x => x));
        Assert.All(windows.Where(w => w.End == OneOS.Runtime.Monitoring.PipeEnd.In), w => Assert.NotNull(w.LatencyUs));
        var e2e = Assert.Single(metrics.DrainEndToEnd());
        Assert.Equal(("src", "sink"), (e2e.From, e2e.To));
        Assert.True(e2e.LatencyUs.N >= 6);
    }

    [Fact]
    public async Task ProtocolErrorsAreReportedNotFatal()
    {
        var chatty = Script("chatty.py", """
            print("hello, not json", flush=True)
            print(json.dumps({"port": "nope", "data": 1}), flush=True)
            print(json.dumps({"port": "out", "data": {"v": "ok"}}), flush=True)
            sys.stdin.read()
            """);
        var sink = Script("sink.py", """
            out = open(sys.argv[1], "a")
            for line in sys.stdin:
                out.write(json.loads(line)["data"]["v"] + "\n"); out.flush()
            """);
        var result = Path.Combine(_dir, "r.txt");
        var compiled = TestUtil.Compile($$"""
            type m { v: string }
            graph g(out: string) { topology {
              @json_lines node s () => (out: m) = process('python3', '{{chatty}}');
              @json_lines node k (in: m) = process('python3', '{{sink}}', out);
              edge s --> k; } }
            """);
        var (bound, _) = GraphBinder.Bind(compiled.Graph("g")!, new object?[] { result });
        var plan = Sched.Plan(bound!, Sched.Cluster(Sched.Host("h")));
        var log = new ConcurrentQueue<string>();
        var exec = new GraphExecutor(bound!, plan, "h", null, new InMemoryPipeHost(), new OsProcessFactory(_dir), _ => { }, (l, m) => log.Enqueue(m));
        await exec.StartAsync();
        var lines = await WaitForLines(result, l => l.Count >= 1, TimeSpan.FromSeconds(15));
        await exec.StopAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "ok" }, lines);
        Assert.Contains(log, m => m.Contains("is not a {\"port\", \"data\"} message"));
        Assert.Contains(log, m => m.Contains("no output port 'nope'"));
    }

    [Fact]
    public void PortDescriptorsFollowTheDefaultsThenDeclarationOrder()
    {
        AgentPortInfo P(string name, PortDirection d) => new(name, d, "m", null, null, null, null, 0, null, null, null, null,
            Array.Empty<string>(), false, false, Framing.Ndjson);
        var map = PortDescriptors.Assign(new[] { P("x", PortDirection.In), P("in", PortDirection.In), P("a", PortDirection.Out), P("stderr", PortDirection.Out), P("b", PortDirection.Out) });
        Assert.Equal(new Dictionary<string, int> { ["in"] = 0, ["a"] = 1, ["stderr"] = 2, ["x"] = 3, ["b"] = 4 }, map);
        var single = PortDescriptors.Assign(new[] { P("frames", PortDirection.In), P("result", PortDirection.Out) });
        Assert.Equal(new Dictionary<string, int> { ["frames"] = 0, ["result"] = 1 }, single);
    }

    // Typed streams with several ports: extra ports are descriptors 3, 4, … (ONEOS_PORT_<name>), opened
    // through the descriptor shim; a `stderr` output port is fd 2.
    [Fact]
    public async Task MultiPortProcessesUseTheirOwnDescriptors()
    {
        var src = Script("src.py", """
            for i in range(1, 7):
                print(json.dumps({"n": i}), flush=True)
            sys.stdin.read()
            """);
        var split = Script("split.py", """
            import os
            odds = os.fdopen(int(os.environ["ONEOS_PORT_odds"]), "w")
            for line in sys.stdin:
                n = json.loads(line)["n"]
                if n % 2 == 0: print(json.dumps({"n": n}), flush=True)
                else: odds.write(f"odd {n}\n"); odds.flush()
                sys.stderr.write(f"saw {n}\n"); sys.stderr.flush()
            """);
        var join = Script("join.py", """
            import os, threading
            out = open(sys.argv[1], "a"); lock = threading.Lock()
            def pump(name, stream, parse):
                for line in stream:
                    with lock: out.write(f"{name} {parse(line)}\n"); out.flush()
            fds = {p: int(os.environ["ONEOS_PORT_" + p]) for p in ("a", "b", "c")}
            ts = [threading.Thread(target=pump, args=("a", sys.stdin, lambda l: json.loads(l)["n"])),
                  threading.Thread(target=pump, args=("b", os.fdopen(fds["b"]), str.strip)),
                  threading.Thread(target=pump, args=("c", os.fdopen(fds["c"]), str.strip))]
            with lock: out.write(f"fds {fds['a']} {fds['b']} {fds['c']}\n"); out.flush()
            for t in ts: t.start()
            for t in ts: t.join()
            """);
        var result = Path.Combine(_dir, "joined.txt");
        var compiled = TestUtil.Compile($$"""
            type num { n: i64 }
            graph g(out: string) { topology {
              node src () => (out: num) = process('python3', '{{src}}');
              node split (in: num) => (evens: num, odds: lines, stderr: lines) = process('python3', '{{split}}');
              node join (a: num, b: lines, c: lines) = process('python3', '{{join}}', out);
              edge src --> split;
              edge split.evens --> join.a;
              edge split.odds --> join.b;
              edge split.stderr --> join.c; } }
            """);
        TestUtil.AssertNoErrors(compiled);
        var (bound, _) = GraphBinder.Bind(compiled.Graph("g")!, new object?[] { result });
        var plan = Sched.Plan(bound!, Sched.Cluster(Sched.Host("h")));
        var log = new ConcurrentQueue<string>();
        var exec = new GraphExecutor(bound!, plan, "h", null, new InMemoryPipeHost(), new OsProcessFactory(_dir), _ => { }, (l, m) => log.Enqueue(m));
        await exec.StartAsync();
        var lines = await WaitForLines(result, l => l.Count >= 13, TimeSpan.FromSeconds(20));
        await exec.StopAsync(TimeSpan.FromSeconds(2));
        Assert.True(lines.Count >= 13, string.Join("\n", lines) + "\n--\n" + string.Join("\n", log));
        Assert.Equal("fds 0 3 4", lines[0]);
        Assert.Equal(new[] { "a 2", "a 4", "a 6" }, lines.Where(l => l.StartsWith("a ")));
        Assert.Equal(new[] { "b odd 1", "b odd 3", "b odd 5" }, lines.Where(l => l.StartsWith("b ")));
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"c saw {i}"), lines.Where(l => l.StartsWith("c ")));
        Assert.Empty(Directory.GetDirectories(_dir, "oneos-fd-*"));   // FIFOs are removed once open
    }

    // stderr (plan 6.5a): the workers' stderr fans in to one collector; an unconnected stderr stays a log.
    [Fact]
    public async Task StderrFansInFromPartitionsAndOtherwiseLogs()
    {
        var src = Script("src.py", """
            sys.stderr.write("src starting\n"); sys.stderr.flush()
            for i in range(6):
                print(json.dumps({"v": i}), flush=True)
            sys.stdin.read()
            """);
        var work = Script("work.py", """
            import os
            me = os.environ["ONEOS_INSTANCE_INDEX"]
            for line in sys.stdin:
                v = json.loads(line)["v"]
                sys.stderr.write(f"w{me} got {v}\n"); sys.stderr.flush()
                print(line, end="", flush=True)
            """);
        var drain = Script("drain.py", "for line in sys.stdin: pass\n");
        var collect = Script("collect.py", """
            out = open(sys.argv[1], "a")
            for line in sys.stdin:
                out.write(line); out.flush()
            """);
        var result = Path.Combine(_dir, "stderr.txt");
        var compiled = TestUtil.Compile($$"""
            type m { v: i64 }
            graph g(out: string) { topology {
              node src () => (o: m) = process('python3', '{{src}}');
              node w[] (i: m) => (o: m) = process('python3', '{{work}}');
              node k (i: m) = process('python3', '{{drain}}');
              node errors (i: lines) = process('python3', '{{collect}}', out);
              edge src --> w; edge w --> k; edge w.stderr --> errors; }
              policy { partitions(2): w; } }
            """);
        TestUtil.AssertNoErrors(compiled);
        var (bound, _) = GraphBinder.Bind(compiled.Graph("g")!, new object?[] { result });
        var plan = Sched.Plan(bound!, Sched.Cluster(Sched.Host("h")));
        var log = new ConcurrentQueue<string>();
        var exec = new GraphExecutor(bound!, plan, "h", null, new InMemoryPipeHost(), new OsProcessFactory(_dir), _ => { }, (l, m) => log.Enqueue(m));
        await exec.StartAsync();
        var lines = await WaitForLines(result, l => l.Count >= 6, TimeSpan.FromSeconds(20));
        await exec.StopAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(Enumerable.Range(0, 6).Select(i => $"got {i}"), lines.Select(l => l[(l.IndexOf(' ') + 1)..]).OrderBy(x => x));
        Assert.Equal(2, lines.Select(l => l.Split(' ')[0]).Distinct().Count());     // both workers' stderr arrived
        Assert.Contains(log, m => m == "g1/src/0 stderr: src starting");
    }

    // Binary frames written in pieces are cut by the jpeg markers, load-balanced over w[], and reach the sink intact.
    [Fact]
    public async Task RawJpegFramesAreSegmentedAndLoadBalanced()
    {
        var cam = Script("cam.py", """
            import os, time
            for i in range(6):
                frame = bytes([0xFF, 0xD8, i, 0x00, 0x0A, 0xFF, 0xD9])
                os.write(1, frame[:3]); time.sleep(0.02); os.write(1, frame[3:])
            sys.stdin.read()
            """);
        var pass_ = Script("pass.py", """
            import os
            while True:
                b = os.read(0, 65536)
                if not b: break
                os.write(1, b)
            """);
        var sink = Script("sink.py", """
            import os
            out = open(sys.argv[1], "a"); data = b""
            while True:
                b = os.read(0, 65536)
                if not b: break
                data += b
                out.write(data.hex() + "\n"); out.flush()
            """);
        var result = Path.Combine(_dir, "frames.txt");
        var compiled = TestUtil.Compile($$"""
            graph g(out: string) { topology {
              node cam () => (out: jpeg) = process('python3', '{{cam}}');
              node w[] (in: jpeg) => (out: jpeg) = process('python3', '{{pass_}}');
              node k (in: jpeg) = process('python3', '{{sink}}', out);
              edge cam --> w; edge w --> k; }
              policy { partitions(2): w; } }
            """);
        TestUtil.AssertNoErrors(compiled);
        var (bound, _) = GraphBinder.Bind(compiled.Graph("g")!, new object?[] { result });
        var plan = Sched.Plan(bound!, Sched.Cluster(Sched.Host("h")));
        var metrics = new OneOS.Runtime.Monitoring.RuntimeMetrics();
        var exec = new GraphExecutor(bound!, plan, "h", null, new InMemoryPipeHost(), new OsProcessFactory(_dir), _ => { }, (l, m) => { },
            metrics: metrics);
        await exec.StartAsync();
        var lines = await WaitForLines(result, l => l.Count > 0 && l[^1].Length == 6 * 7 * 2, TimeSpan.FromSeconds(20));
        var windows = metrics.DrainPipes(TimeSpan.FromSeconds(1));
        await exec.StopAsync(TimeSpan.FromSeconds(2));
        var all = Convert.FromHexString(lines[^1]);
        var frames = Enumerable.Range(0, 6).Select(i => Convert.ToHexString(all[(i * 7)..(i * 7 + 7)])).ToList();
        Assert.All(frames, f => Assert.Matches("^FFD8.{2}000AFFD9$", f));
        Assert.Equal(Enumerable.Range(0, 6).Select(i => i.ToString("X2")).OrderBy(x => x), frames.Select(f => f[4..6]).OrderBy(x => x));
        // Load balancing used both workers: each received whole frames as separate messages.
        var intoWorkers = windows.Where(w => w.End == OneOS.Runtime.Monitoring.PipeEnd.In && w.Meta.To!.Contains("/w/")).ToList();
        Assert.Equal(6, intoWorkers.Sum(w => w.Messages));
        Assert.Equal(2, intoWorkers.Count(w => w.Messages > 0));
    }

    // The JSON-lines adapter on format ports: `data` is base64 for binary formats, a JSON string for `lines`.
    [Fact]
    public async Task FormatPortsInTheJsonLinesProtocol()
    {
        var cam = Script("cam.py", """
            import base64
            for i in range(3):
                img = bytes([0xFF, 0xD8, i, 0x0A, 0xFF, 0xD9])
                print(json.dumps({"port": "out", "data": base64.b64encode(img).decode()}), flush=True)
            print(json.dumps({"port": "out", "data": {"not": "base64"}}), flush=True)
            sys.stdin.read()
            """);
        var size = Script("size.py", """
            import base64
            for line in sys.stdin:
                img = base64.b64decode(json.loads(line)["data"])
                print(json.dumps({"port": "out", "data": f"{len(img)} bytes, id {img[2]}"}), flush=True)
            """);
        var sink = Script("sink.py", """
            out = open(sys.argv[1], "a")
            for line in sys.stdin:
                out.write(json.loads(line)["data"] + "\n"); out.flush()
            """);
        var result = Path.Combine(_dir, "r.txt");
        var compiled = TestUtil.Compile($$"""
            graph g(out: string) { topology {
              @json_lines node c () => (out: jpeg) = process('python3', '{{cam}}');
              @json_lines node z (in: jpeg) => (out: lines) = process('python3', '{{size}}');
              @json_lines node k (in: lines) = process('python3', '{{sink}}', out);
              edge c --> z; edge z --> k; } }
            """);
        TestUtil.AssertNoErrors(compiled);
        var (bound, _) = GraphBinder.Bind(compiled.Graph("g")!, new object?[] { result });
        var plan = Sched.Plan(bound!, Sched.Cluster(Sched.Host("h")));
        var log = new ConcurrentQueue<string>();
        var exec = new GraphExecutor(bound!, plan, "h", null, new InMemoryPipeHost(), new OsProcessFactory(_dir), _ => { }, (l, m) => log.Enqueue(m));
        await exec.StartAsync();
        var lines = await WaitForLines(result, l => l.Count >= 3, TimeSpan.FromSeconds(15));
        await exec.StopAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "6 bytes, id 0", "6 bytes, id 1", "6 bytes, id 2" }, lines);
        Assert.Contains(log, m => m.Contains("must be a base64 string"));
    }

    [Fact]
    public async Task MessagePipesDeliverWholeUnitsAndRawPipesBytes()
    {
        var host = new InMemoryPipeHost();
        var got = new ConcurrentQueue<(string Pipe, byte[] Unit)>();
        OneOS.Common.Pipe? messageIn = null, rawIn = null;
        host.ExpectInbound("m", PipeKind.Message, true, p => { messageIn = p; p.OnReceive(u => got.Enqueue(("m", u))); _ = p.StartListening(); });
        host.ExpectInbound("r", PipeKind.Raw, true, p => { rawIn = p; p.OnReceive(u => got.Enqueue(("r", u))); _ = p.StartListening(); });
        var messageOut = await host.OpenOutboundAsync("m", PipeKind.Message, "h", true, default);
        var rawOut = await host.OpenOutboundAsync("r", PipeKind.Raw, "h", true, default);
        Assert.IsAssignableFrom<OneOS.Common.MessagePipe>(messageIn);
        Assert.IsAssignableFrom<OneOS.Common.MessagePipe>(messageOut);
        Assert.IsAssignableFrom<OneOS.Common.RawPipe>(rawIn);
        Assert.IsAssignableFrom<OneOS.Common.RawPipe>(rawOut);

        var env = new Envelope { SenderAgentUri = "g/a/0", Target = "g/b/0", Channel = "in", Payload = "{\"x\":1}"u8.ToArray(), Label = "secret", Sequence = 7 };
        await messageOut.Send(MessagePack.MessagePackSerializer.Serialize(env));
        await rawOut.Send("abc"u8.ToArray());
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (got.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);

        var back = MessagePack.MessagePackSerializer.Deserialize<Envelope>(got.Single(g => g.Pipe == "m").Unit);
        Assert.Equal(("g/a/0", "in", "secret", 7L), (back.SenderAgentUri, back.Channel, back.Label, back.Sequence!.Value));
        Assert.Equal("abc"u8.ToArray(), got.Single(g => g.Pipe == "r").Unit);
    }

    [Fact]
    public void CheckpointsTravelAsChunkedEnvelopesAndReassemble()
    {
        var data = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();
        var cp = new AgentCheckpoint("g/agg/0", 3, DateTimeOffset.UtcNow, new ProcessSnapshot(data),
            new SidecarState("public", null, new Dictionary<string, long>(), new Dictionary<string, long>(),
                new Dictionary<string, OrderedMergeState<PortMessage>>(), new Dictionary<string, SequencedPortState<PortMessage>>(),
                new List<AnsweredMessage>(), new Dictionary<string, IReadOnlyList<Envelope>>(), null, null));
        var chunks = CheckpointStream.Split(cp, "g/agg/0s", chunkSize: 4096).ToList();
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.Equal(("#checkpoint", 3L, "g/agg/0s"), (c.Channel, c.Sequence!.Value, c.Target)));

        var assembler = new CheckpointAssembler();
        // A run cut short (the primary stopped mid-send) is dropped when the next one starts.
        Assert.Null(assembler.Push(Wire(chunks[0])));
        var next = CheckpointStream.Split(cp with { Sequence = 4 }, "g/agg/0s", chunkSize: 4096).ToList();
        AgentCheckpoint? done = null;
        foreach (var c in next) done = assembler.Push(Wire(c)) ?? done;
        Assert.Equal(4, done!.Sequence);
        Assert.Equal(data, done.Process!.Data);

        static Envelope Wire(Envelope e) => MessagePack.MessagePackSerializer.Deserialize<Envelope>(MessagePack.MessagePackSerializer.Serialize(e));
    }
}
