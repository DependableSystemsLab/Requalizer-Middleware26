using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OneOS.Common;

namespace OneOS.Runtime.Monitoring
{
    public enum PipeEnd { In, Out }

    // What a pipe end connects. Kind: "graph" (dataflow graph pipe, messages with labels) or "shell" (a
    // shell pipeline's raw byte pipe). Group: the graph instance or pipeline id. From/To: agent ids or URIs.
    // Mode: "message" (one unit per message) or "raw" (byte streams: shell pipelines and bypass edges, whose
    // units are read chunks, so message counts and rates are chunks).
    public sealed record PipeMeta(string Kind, string? Group, string? Edge, string? From, string? To, string Mode = "message");

    // Latency samples of one window: count, sum and max exactly; percentiles from a bounded reservoir.
    public sealed class LatencySamples
    {
        private const int Capacity = 4096;
        private readonly object _lock = new();
        private readonly double[] _reservoir = new double[Capacity];
        private long _count;
        private double _sum, _max;
        private readonly Random _random = new(7);

        public void Add(TimeSpan latency)
        {
            double us = Math.Max(0, latency.TotalMicroseconds);
            lock (_lock)
            {
                if (_count < Capacity) _reservoir[_count] = us;
                else
                {
                    long j = _random.NextInt64(_count + 1);
                    if (j < Capacity) _reservoir[j] = us;
                }
                _count++;
                _sum += us;
                if (us > _max) _max = us;
            }
        }

        public LatencySummary? Drain()
        {
            lock (_lock)
            {
                if (_count == 0) return null;
                var sample = _reservoir.Take((int)Math.Min(_count, Capacity)).OrderBy(x => x).ToArray();
                double P(double q) => sample[Math.Min(sample.Length - 1, (int)Math.Ceiling(q * sample.Length) - 1)];
                var s = new LatencySummary(_count, _sum / _count, P(0.5), P(0.95), P(0.99), _max);
                _count = 0; _sum = 0; _max = 0;
                return s;
            }
        }
    }

    // Microseconds.
    public sealed record LatencySummary(long N, double Mean, double P50, double P95, double P99, double Max);

    // One end of one pipe. Window counters are drained by the profiler; lifetime totals stay.
    public sealed class PipeMeter
    {
        private long _messages, _bytes, _totalMessages, _totalBytes;
        private ConcurrentDictionary<string, long> _labels = new();
        private readonly LatencySamples _latency = new();

        public PipeMeter(string pipeId, PipeEnd end, PipeMeta meta) { PipeId = pipeId; End = end; Meta = meta; }

        public string PipeId { get; }
        public PipeEnd End { get; }
        public PipeMeta Meta { get; }
        public long TotalMessages => Interlocked.Read(ref _totalMessages);
        public long TotalBytes => Interlocked.Read(ref _totalBytes);

        // One message (graph pipes) or one chunk (raw pipes).
        public void Record(long bytes, string? label = null, TimeSpan? latency = null)
        {
            Interlocked.Increment(ref _messages);
            Interlocked.Add(ref _bytes, bytes);
            Interlocked.Increment(ref _totalMessages);
            Interlocked.Add(ref _totalBytes, bytes);
            if (label != null) _labels.AddOrUpdate(label, 1, (_, n) => n + 1);
            if (latency is TimeSpan l) _latency.Add(l);
        }

        public PipeWindow? Drain(TimeSpan window)
        {
            long messages = Interlocked.Exchange(ref _messages, 0);
            long bytes = Interlocked.Exchange(ref _bytes, 0);
            var labels = Interlocked.Exchange(ref _labels, new ConcurrentDictionary<string, long>());
            var latency = _latency.Drain();
            if (messages == 0) return null;
            double seconds = Math.Max(1e-3, window.TotalSeconds);
            return new PipeWindow(PipeId, End, Meta, messages, messages / seconds, bytes, bytes / seconds, latency,
                labels.Count > 0 ? new SortedDictionary<string, long>(labels) : null);
        }
    }

    public sealed record PipeWindow(string PipeId, PipeEnd End, PipeMeta Meta, long Messages, double MessagesPerSec, long Bytes,
        double BytesPerSec, LatencySummary? LatencyUs, IReadOnlyDictionary<string, long>? Labels);

    public sealed record EndToEndWindow(string Graph, string From, string To, LatencySummary LatencyUs);

    // One graph message reaching a terminal node: origin and terminal node, the terminal agent, when the message's
    // lineage started (the source runtime's clock) and when it arrived (this runtime's clock), payload bytes.
    public sealed record MessageArrival(string Graph, string From, string To, string Agent, DateTimeOffset Origin, DateTimeOffset Arrived, long Bytes);

    // This runtime's always-on measurements (tier-0, local): pipe ends it sends or receives on, and the
    // end-to-end latency of graph messages arriving at terminal nodes it runs. Recording is cheap (atomic
    // counters, a bounded latency sample); `--profile` drains windows to disk (Profiler).
    public sealed class RuntimeMetrics
    {
        private readonly ConcurrentDictionary<(string, PipeEnd), PipeMeter> _pipes = new();
        private readonly ConcurrentDictionary<(string Graph, string From, string To), LatencySamples> _endToEnd = new();
        private readonly ConcurrentQueue<MessageArrival> _arrivals = new();
        private volatile bool _captureArrivals;

        public PipeMeter Pipe(string pipeId, PipeEnd end, PipeMeta meta) =>
            _pipes.GetOrAdd((pipeId, end), _ => new PipeMeter(pipeId, end, meta));

        public IEnumerable<PipeMeter> Pipes => _pipes.Values;

        public void RecordEndToEnd(string graph, string from, string to, TimeSpan latency) =>
            _endToEnd.GetOrAdd((graph, from, to), _ => new LatencySamples()).Add(latency);

        // Per-message arrivals are kept only while a profiler drains them, so they can't pile up otherwise.
        public bool CaptureArrivals { get => _captureArrivals; set => _captureArrivals = value; }

        public void RecordArrival(MessageArrival arrival)
        {
            if (_captureArrivals) _arrivals.Enqueue(arrival);
        }

        public List<MessageArrival> DrainArrivals()
        {
            var drained = new List<MessageArrival>();
            while (_arrivals.TryDequeue(out var a)) drained.Add(a);
            return drained;
        }

        // The pipe ends that carried traffic in the window just ended, reset for the next one.
        public List<PipeWindow> DrainPipes(TimeSpan window) =>
            _pipes.Values.Select(p => p.Drain(window)).Where(w => w != null).Cast<PipeWindow>().ToList();

        public List<EndToEndWindow> DrainEndToEnd() =>
            _endToEnd.Select(kv => kv.Value.Drain() is { } s ? new EndToEndWindow(kv.Key.Graph, kv.Key.From, kv.Key.To, s) : null)
                .Where(w => w != null).Cast<EndToEndWindow>().ToList();
    }

    // Counts what passes through a raw pipe end (shell pipelines): chunks and bytes.
    public sealed class MeteredPipe : Pipe
    {
        private readonly Pipe _inner;
        private readonly PipeMeter _meter;

        public MeteredPipe(Pipe inner, PipeMeter meter) { _inner = inner; _meter = meter; }

        public override Task Send(byte[] payload)
        {
            _meter.Record(payload.Length);
            return _inner.Send(payload);
        }

        public override void OnReceive(Action<byte[]> action) => _inner.OnReceive(payload =>
        {
            _meter.Record(payload.Length);
            action(payload);
        });

        public override Task StartListening() => _inner.StartListening();
        public override Task CloseAsync() => _inner.CloseAsync();
    }
}
