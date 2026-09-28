using System;
using System.Collections.Generic;
using System.Linq;
using OneOS.Runtime.Language.Models;

namespace OneOS.Runtime.Sidecar;

// A message waiting in a port buffer. Ts is the clock value (ordered ports) or sequence number
// (sequenced ports); Per is the `per p` value, if any.
public sealed record Buffered<T>(int Stream, long Ts, string? Per, long Size, T Item);

public enum MergeOutcome { Released, Late, Routed }

// Checkpointed port state (L§9.3: ordered-merge state and sequence counters migrate to the standby).
public sealed record MergeStreamState(int Stream, long MaxTs, bool Active);
public sealed record OrderedMergeState<T>(IReadOnlyList<MergeStreamState> Streams, IReadOnlyList<Buffered<T>> Items,
    long Watermark, long LastReleased, IReadOnlyDictionary<string, long> LastReleasedPer);
public sealed record SequencedPortState<T>(long Next, IReadOnlyList<Buffered<T>> Items);

// The ordered merge of one receiving instance's ordered input port (L§6.6.3).
// Time is passed in explicitly (wall-clock ticks), so the merge is deterministic and testable.
public sealed class OrderedMerge<T>
{
    private sealed class Stream { public long MaxTs = long.MinValue; public long LastArrival; public bool Active; }

    private readonly long _lateness;                 // Dr, in clock units
    private readonly long? _idleTimeoutTicks;
    private readonly long _maxBufferBytes;
    private readonly OnLateMode _onLate;
    private readonly bool _perKeyed;
    private readonly Action<string, string> _report;

    private readonly Dictionary<int, Stream> _streams = new();
    private readonly SortedSet<(long Ts, int Stream, long Seq)> _order = new();
    private readonly Dictionary<(long, int, long), Buffered<T>> _items = new();
    private long _arrivals, _bufferBytes;
    private long _watermark = long.MinValue;
    private long _lastReleased = long.MinValue;
    private readonly Dictionary<string, long> _lastReleasedPer = new();

    public OrderedMerge(long latenessClockUnits, TimeSpan? idleTimeout, long maxBufferBytes, OnLateMode onLate, bool perKeyed, Action<string, string> report)
    {
        _lateness = latenessClockUnits;
        _idleTimeoutTicks = idleTimeout?.Ticks;
        _maxBufferBytes = maxBufferBytes;
        _onLate = onLate;
        _perKeyed = perKeyed;
        _report = report;
    }

    public long Watermark => _watermark;
    public int Buffered => _order.Count;

    // Offers a message from a stream (an incoming edge and sender instance). Returns what can now be
    // handed on, in order: released messages, and late ones with how they were handled.
    public List<(MergeOutcome Outcome, Buffered<T> Message)> Offer(Buffered<T> m, long nowTicks)
    {
        var result = new List<(MergeOutcome, Buffered<T>)>();
        if (!_streams.TryGetValue(m.Stream, out var s)) _streams[m.Stream] = s = new Stream();
        s.Active = true;
        s.LastArrival = nowTicks;
        s.MaxTs = Math.Max(s.MaxTs, m.Ts);

        long last = _perKeyed && m.Per != null ? _lastReleasedPer.GetValueOrDefault(m.Per, long.MinValue) : _lastReleased;
        if (m.Ts < last)
        {
            switch (_onLate)
            {
                case OnLateMode.Pass: result.Add((MergeOutcome.Late, m)); break;
                case OnLateMode.Route: result.Add((MergeOutcome.Routed, m)); break;
                default: _report("late_message_dropped", $"stream {m.Stream}: ts {m.Ts} < {last}"); break;
            }
        }
        else
        {
            var key = (m.Ts, m.Stream, _arrivals++);
            _order.Add(key);
            _items[key] = m;
            _bufferBytes += m.Size;
        }
        Release(result);
        return result;
    }

    // Advances time: streams quiet for the idle timeout stop holding back the watermark.
    public List<(MergeOutcome Outcome, Buffered<T> Message)> Tick(long nowTicks)
    {
        if (_idleTimeoutTicks is long idle)
            foreach (var s in _streams.Values.Where(s => s.Active && nowTicks - s.LastArrival >= idle)) s.Active = false;
        var result = new List<(MergeOutcome, Buffered<T>)>();
        Release(result);
        return result;
    }

    public OrderedMergeState<T> Export() => new(
        _streams.Select(kv => new MergeStreamState(kv.Key, kv.Value.MaxTs, kv.Value.Active)).ToList(),
        _order.Select(k => _items[k]).ToList(), _watermark, _lastReleased, new Dictionary<string, long>(_lastReleasedPer));

    // Restores a checkpoint into an empty merge. Idle timers restart now.
    public void Import(OrderedMergeState<T> state, long nowTicks)
    {
        _streams.Clear(); _order.Clear(); _items.Clear(); _lastReleasedPer.Clear();
        _bufferBytes = 0;
        foreach (var s in state.Streams) _streams[s.Stream] = new Stream { MaxTs = s.MaxTs, Active = s.Active, LastArrival = nowTicks };
        foreach (var m in state.Items)
        {
            var key = (m.Ts, m.Stream, _arrivals++);
            _order.Add(key);
            _items[key] = m;
            _bufferBytes += m.Size;
        }
        _watermark = state.Watermark;
        _lastReleased = state.LastReleased;
        foreach (var (per, ts) in state.LastReleasedPer) _lastReleasedPer[per] = ts;
    }

    // At graph shutdown the buffer is flushed in order.
    public List<Buffered<T>> Flush()
    {
        var all = _order.Select(k => _items[k]).ToList();
        foreach (var m in all) MarkReleased(m);
        _order.Clear();
        _items.Clear();
        _bufferBytes = 0;
        return all;
    }

    private void Release(List<(MergeOutcome, Buffered<T>)> result)
    {
        // W = min over active streams of (maxTs − Dr); unchanged when no stream is active.
        var active = _streams.Values.Where(s => s.Active).ToList();
        if (active.Count > 0) _watermark = active.Min(s => s.MaxTs == long.MinValue ? long.MinValue : s.MaxTs - _lateness);

        while (_order.Count > 0 && _order.Min.Ts <= _watermark) Pop(result);

        if (_bufferBytes > _maxBufferBytes)
        {
            _report("order_buffer_overflow", $"buffer at {_bufferBytes} bytes exceeds {_maxBufferBytes}; releasing early");
            while (_order.Count > 0 && _bufferBytes > _maxBufferBytes) Pop(result);
        }
    }

    private void Pop(List<(MergeOutcome, Buffered<T>)> result)
    {
        var key = _order.Min;
        _order.Remove(key);
        var m = _items[key];
        _items.Remove(key);
        _bufferBytes -= m.Size;
        MarkReleased(m);
        result.Add((MergeOutcome.Released, m));
    }

    private void MarkReleased(Buffered<T> m)
    {
        _lastReleased = Math.Max(_lastReleased, m.Ts);
        if (m.Per != null) _lastReleasedPer[m.Per] = Math.Max(_lastReleasedPer.GetValueOrDefault(m.Per, long.MinValue), m.Ts);
    }
}

// A sequenced input port (L§6.6.4): releases strictly in sequence order, skipping a missing number
// after the gap timeout.
public sealed class SequencedPort<T>
{
    private readonly long? _gapTimeoutTicks;
    private readonly long _maxBufferBytes;
    private readonly Action<string, string> _report;
    private readonly SortedDictionary<long, Buffered<T>> _buffer = new();
    private long _next;
    private long? _waitingSince;
    private long _bytes;

    public SequencedPort(TimeSpan? gapTimeout, long maxBufferBytes, Action<string, string> report)
    {
        _gapTimeoutTicks = gapTimeout?.Ticks;
        _maxBufferBytes = maxBufferBytes;
        _report = report;
    }

    public long Next => _next;

    public SequencedPortState<T> Export() => new(_next, _buffer.Values.ToList());

    public void Import(SequencedPortState<T> state, long nowTicks)
    {
        _buffer.Clear();
        _bytes = 0;
        _next = state.Next;
        foreach (var m in state.Items) { _buffer[m.Ts] = m; _bytes += m.Size; }
        _waitingSince = _buffer.Count > 0 ? nowTicks : null;
    }

    public List<Buffered<T>> Offer(Buffered<T> m, long nowTicks)
    {
        if (m.Ts < _next) { _report("duplicate_sequence_dropped", $"sequence {m.Ts} already released"); return new(); }
        _buffer[m.Ts] = m;
        _bytes += m.Size;
        var released = Drain();
        if (_buffer.Count > 0) _waitingSince ??= nowTicks;
        if (_bytes > _maxBufferBytes)
        {
            _report("order_buffer_overflow", $"sequenced buffer at {_bytes} bytes exceeds {_maxBufferBytes}; skipping to {_buffer.Keys.First()}");
            Skip(released);
        }
        return released;
    }

    public List<Buffered<T>> Tick(long nowTicks)
    {
        var released = new List<Buffered<T>>();
        if (_gapTimeoutTicks is long gap && _waitingSince is long since && nowTicks - since >= gap && _buffer.Count > 0)
        {
            Skip(released);
            _waitingSince = _buffer.Count > 0 ? nowTicks : null;
        }
        return released;
    }

    // Skips the missing numbers before the earliest buffered one.
    private void Skip(List<Buffered<T>> released)
    {
        var first = _buffer.Keys.First();
        _report("sequence_gap_skipped", $"sequence {_next}..{first - 1} missing; skipped");
        _next = first;
        released.AddRange(Drain());
    }

    private List<Buffered<T>> Drain()
    {
        var released = new List<Buffered<T>>();
        while (_buffer.TryGetValue(_next, out var m))
        {
            _buffer.Remove(_next);
            _bytes -= m.Size;
            released.Add(m);
            _next++;
        }
        if (_buffer.Count == 0) _waitingSince = null;
        return released;
    }
}
