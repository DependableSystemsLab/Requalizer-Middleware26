using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OneOS.Runtime.Monitoring
{
    // `oneos start --profile [dir] [--profile-interval s]`: where profiles go and how often to sample.
    public sealed record ProfileOptions(string Directory, TimeSpan Interval);

    // Writes this runtime's measurements to disk while profiling, one JSON record per line, every interval:
    //   resources.jsonl  CPU and memory of the runtime process and of each agent process it runs;
    //   pipes.jsonl      per active pipe end: messages and bytes (totals and per second), latency, labels;
    //   latency.jsonl    end-to-end latency of graph messages from their source node to a terminal node;
    //   messages.jsonl   each graph message reaching a terminal node here: origin and arrival time (Unix ms), bytes.
    // Files go to <Directory>/<runtime>-<start time>/, with run.json describing the run.
    public sealed class Profiler : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly string _runtimeId;
        private readonly ProfileOptions _options;
        private readonly RuntimeMetrics _metrics;
        private readonly ResourceSampler _sampler;
        private readonly Action<string> _log;
        private readonly object _lock = new();
        private StreamWriter? _resources, _pipes, _latency, _messages;
        private Timer? _timer;
        private DateTime _lastSample;

        public Profiler(string runtimeId, ProfileOptions options, RuntimeMetrics metrics, Func<IEnumerable<(string Agent, int Pid)>> agents,
            Action<string>? log = null)
        {
            _runtimeId = runtimeId;
            _options = options;
            _metrics = metrics;
            _sampler = new ResourceSampler(agents);
            _log = log ?? (_ => { });
            RunDirectory = Path.Combine(options.Directory, $"{runtimeId}-{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ssZ}");
        }

        public string RunDirectory { get; }

        public void Start()
        {
            Directory.CreateDirectory(RunDirectory);
            File.WriteAllText(Path.Combine(RunDirectory, "run.json"), JsonSerializer.Serialize(new
            {
                runtime = _runtimeId, started = DateTime.UtcNow, intervalSeconds = _options.Interval.TotalSeconds,
                processors = Environment.ProcessorCount, host = Environment.MachineName,
                cpuPct = "percent of one core (like top)", latencyUs = "microseconds; pipe latency is sender clock to receiver clock",
            }, Json));
            _resources = Open("resources.jsonl");
            _pipes = Open("pipes.jsonl");
            _latency = Open("latency.jsonl");
            _messages = Open("messages.jsonl");
            _lastSample = DateTime.UtcNow;
            // Discard what accumulated before profiling started, so the first window is a full interval.
            _metrics.DrainPipes(_options.Interval);
            _metrics.DrainEndToEnd();
            _metrics.DrainArrivals();
            _metrics.CaptureArrivals = true;
            _timer = new Timer(_ => Sample(), null, _options.Interval, _options.Interval);
            _log($"Profiling to {RunDirectory} every {_options.Interval.TotalSeconds:0.###} s");
        }

        private StreamWriter Open(string name) => new(Path.Combine(RunDirectory, name), append: true) { AutoFlush = false };

        public void Sample()
        {
            lock (_lock)
            {
                if (_resources == null) return;
                var now = DateTime.UtcNow;
                var window = now - _lastSample;
                _lastSample = now;
                try
                {
                    SampleResources(now);
                    foreach (var w in _metrics.DrainPipes(window))
                        Write(_pipes!, new
                        {
                            t = now, runtime = _runtimeId, pipe = w.PipeId, end = w.End == PipeEnd.In ? "in" : "out",
                            kind = w.Meta.Kind, mode = w.Meta.Mode, group = w.Meta.Group, edge = w.Meta.Edge, from = w.Meta.From, to = w.Meta.To,
                            messages = w.Messages, msgPerSec = Math.Round(w.MessagesPerSec, 2), bytes = w.Bytes, bytesPerSec = Math.Round(w.BytesPerSec, 1),
                            latencyUs = Summary(w.LatencyUs), labels = w.Labels,
                        });
                    foreach (var e in _metrics.DrainEndToEnd())
                        Write(_latency!, new { t = now, runtime = _runtimeId, graph = e.Graph, from = e.From, to = e.To, n = e.LatencyUs.N, latencyUs = Summary(e.LatencyUs) });
                    foreach (var a in _metrics.DrainArrivals())
                        Write(_messages!, new
                        {
                            runtime = _runtimeId, graph = a.Graph, from = a.From, to = a.To, agent = a.Agent,
                            originMs = UnixMs(a.Origin), arrivedMs = UnixMs(a.Arrived),
                            latencyUs = Math.Round((a.Arrived - a.Origin).TotalMicroseconds, 1), bytes = a.Bytes,
                        });
                    _resources.Flush(); _pipes!.Flush(); _latency!.Flush(); _messages!.Flush();
                }
                catch (Exception ex) { _log($"Profiling sample failed: {ex.Message}"); }
            }
        }

        private void SampleResources(DateTime now)
        {
            var (self, agents) = _sampler.Sample(now);
            Write(_resources!, new
            {
                t = now, runtime = _runtimeId, process = "runtime", pid = self.Pid, cpuPct = self.CpuPct,
                rssBytes = self.RssBytes, privateBytes = self.PrivateBytes, gcHeapBytes = self.GcHeapBytes,
                gc = self.GcCollections, threads = self.Threads,
            });
            foreach (var a in agents)
                Write(_resources!, new { t = now, runtime = _runtimeId, process = "agent", agent = a.Agent, pid = a.Pid, cpuPct = a.CpuPct, rssBytes = a.RssBytes, threads = a.Threads });
        }

        private static object? Summary(LatencySummary? s) => s == null ? null : new
        {
            n = s.N, mean = Math.Round(s.Mean, 1), p50 = Math.Round(s.P50, 1), p95 = Math.Round(s.P95, 1), p99 = Math.Round(s.P99, 1), max = Math.Round(s.Max, 1),
        };

        private static double UnixMs(DateTimeOffset t) => Math.Round((t - DateTimeOffset.UnixEpoch).TotalMilliseconds, 3);

        private static void Write(StreamWriter w, object record) => w.WriteLine(JsonSerializer.Serialize(record, Json));

        // A last sample, then the files are closed.
        public async ValueTask DisposeAsync()
        {
            if (_timer != null) await _timer.DisposeAsync();
            Sample();
            lock (_lock)
            {
                _metrics.CaptureArrivals = false;
                foreach (var w in new[] { _resources, _pipes, _latency, _messages }) w?.Dispose();
                _resources = _pipes = _latency = _messages = null;
            }
        }
    }
}
