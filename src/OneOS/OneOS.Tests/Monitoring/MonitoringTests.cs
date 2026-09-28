using System.Diagnostics;
using System.Text.Json;
using OneOS.Runtime.Monitoring;

namespace OneOS.Tests.Monitoring;

public class MonitoringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oneos-profile-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void PipeWindowsCountRatesLabelsAndLatencyThenReset()
    {
        var m = new RuntimeMetrics();
        var meta = new PipeMeta("graph", "g1", "e1", "g1/a/0", "g1/b/0");
        var pipe = m.Pipe("p1", PipeEnd.In, meta);
        for (int i = 1; i <= 100; i++)
            pipe.Record(50, i % 4 == 0 ? "secret" : "public", TimeSpan.FromMicroseconds(i * 10));
        Assert.Same(pipe, m.Pipe("p1", PipeEnd.In, meta));

        var w = Assert.Single(m.DrainPipes(TimeSpan.FromSeconds(2)));
        Assert.Equal((100L, 50.0, 5000L, 2500.0), (w.Messages, w.MessagesPerSec, w.Bytes, w.BytesPerSec));
        Assert.Equal(75, w.Labels!["public"]);
        Assert.Equal(25, w.Labels["secret"]);
        Assert.Equal((100L, 500.0, 950.0, 990.0, 1000.0), (w.LatencyUs!.N, w.LatencyUs.P50, w.LatencyUs.P95, w.LatencyUs.P99, w.LatencyUs.Max));
        Assert.Empty(m.DrainPipes(TimeSpan.FromSeconds(1)));           // an idle pipe end is not reported
        Assert.Equal(100, pipe.TotalMessages);                          // lifetime totals stay

        m.RecordEndToEnd("g1", "s", "k", TimeSpan.FromMilliseconds(3));
        var e = Assert.Single(m.DrainEndToEnd());
        Assert.Equal(("s", "k", 3000.0), (e.From, e.To, e.LatencyUs.P50));
        Assert.Empty(m.DrainEndToEnd());
    }

    [Fact]
    public async Task MeteredPipeCountsChunksAndBytesBothWays()
    {
        var m = new RuntimeMetrics();
        var channel = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var output = new MeteredPipe(new OneOS.Common.LocalRawOutputPipe(channel), m.Pipe("p", PipeEnd.Out, new PipeMeta("shell", "x", null, "a", "b")));
        var input = new MeteredPipe(new OneOS.Common.LocalRawInputPipe(channel), m.Pipe("p", PipeEnd.In, new PipeMeta("shell", "x", null, "a", "b")));
        var got = new List<byte[]>();
        input.OnReceive(got.Add);
        _ = input.StartListening();
        await output.Send(new byte[10]);
        await output.Send(new byte[5]);
        await output.CloseAsync();
        for (int i = 0; i < 50 && got.Count < 2; i++) await Task.Delay(10);
        var windows = m.DrainPipes(TimeSpan.FromSeconds(1)).ToDictionary(w => w.End);
        Assert.Equal((2L, 15L), (windows[PipeEnd.Out].Messages, windows[PipeEnd.Out].Bytes));
        Assert.Equal((2L, 15L), (windows[PipeEnd.In].Messages, windows[PipeEnd.In].Bytes));
        Assert.Null(windows[PipeEnd.In].Labels);
    }

    [Fact]
    public async Task ProfilerWritesResourcePipeAndLatencyRecords()
    {
        var m = new RuntimeMetrics();
        using var self = Process.GetCurrentProcess();
        var profiler = new Profiler("test0", new ProfileOptions(_dir, TimeSpan.FromHours(1)), m, () => new[] { ("agent-x", self.Id) });
        var origin = DateTimeOffset.UnixEpoch.AddSeconds(10);
        m.RecordArrival(new MessageArrival("g1", "s", "k", "g1/k/0", origin, origin.AddMilliseconds(1), 7));   // not profiling: not kept
        profiler.Start();
        m.Pipe("p1", PipeEnd.In, new PipeMeta("graph", "g1", "e1", "a", "b")).Record(100, "public", TimeSpan.FromMilliseconds(1));
        m.RecordEndToEnd("g1", "s", "k", TimeSpan.FromMilliseconds(4));
        m.RecordArrival(new MessageArrival("g1", "s", "k", "g1/k/0", origin, origin.AddMilliseconds(4.5), 42));
        profiler.Sample();
        await profiler.DisposeAsync();

        string[] Lines(string f) => File.ReadAllLines(Path.Combine(profiler.RunDirectory, f));
        Assert.True(File.Exists(Path.Combine(profiler.RunDirectory, "run.json")));
        var resources = Lines("resources.jsonl").Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Contains(resources, r => r.GetProperty("process").GetString() == "runtime" && r.GetProperty("rssBytes").GetInt64() > 0);
        Assert.Contains(resources, r => r.GetProperty("process").GetString() == "agent" && r.GetProperty("agent").GetString() == "agent-x");
        var pipe = JsonDocument.Parse(Assert.Single(Lines("pipes.jsonl"))).RootElement;
        Assert.Equal(("p1", "in", "graph", 100L), (pipe.GetProperty("pipe").GetString(), pipe.GetProperty("end").GetString(),
            pipe.GetProperty("kind").GetString(), pipe.GetProperty("bytes").GetInt64()));
        Assert.Equal(1, pipe.GetProperty("labels").GetProperty("public").GetInt64());
        var e2e = JsonDocument.Parse(Assert.Single(Lines("latency.jsonl"))).RootElement;
        Assert.Equal(4000, e2e.GetProperty("latencyUs").GetProperty("p50").GetDouble());
        var msg = JsonDocument.Parse(Assert.Single(Lines("messages.jsonl"))).RootElement;
        Assert.Equal(("s", "k", "g1/k/0", 10000.0, 10004.5, 4500.0, 42L), (msg.GetProperty("from").GetString(), msg.GetProperty("to").GetString(),
            msg.GetProperty("agent").GetString(), msg.GetProperty("originMs").GetDouble(), msg.GetProperty("arrivedMs").GetDouble(),
            msg.GetProperty("latencyUs").GetDouble(), msg.GetProperty("bytes").GetInt64()));
        m.RecordArrival(new MessageArrival("g1", "s", "k", "g1/k/0", origin, origin, 1));                     // profiler gone: not kept
        Assert.Empty(m.DrainArrivals());
    }
}
