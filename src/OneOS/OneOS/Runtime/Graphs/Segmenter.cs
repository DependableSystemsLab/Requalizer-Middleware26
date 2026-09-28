using System;
using System.Collections.Generic;
using OneOS.Runtime.Language;

namespace OneOS.Runtime.Graphs;

// Something a segmenter had to drop (reported as `framing_error`). `Offset` is the stream offset of the
// first dropped byte. Kinds:
//   oversize   a message longer than the segmenter's maximum; it is skipped and segmenting resumes after it
//   truncated  the stream ended inside a message
//   unframed   bytes outside any message (markers framing: before a start marker)
public sealed record FramingError(string Kind, string Message, long Offset, long DroppedBytes);

// Cuts a process's output byte stream into messages with its port's framing (L§5.2). Chunks may split
// messages, and even delimiters and markers, anywhere; Push returns the messages completed so far and
// Complete, at end of stream, whatever the framing makes of the rest. A message is never buffered past
// MaxSegmentBytes: longer ones are dropped and reported through Error. Not thread-safe.
//
//   lines, ndjson    the terminating "\n" (and a "\r" before it) is not part of the message; ndjson skips
//                    blank lines; an unterminated last line still counts at end of stream
//   delimiter(d)     the message includes d
//   markers(s, e)    from s through the matching e; markers nest (s … s … e … e is one message), so an
//                    embedded object such as a JPEG's EXIF thumbnail stays inside its container. A lone
//                    s or e byte sequence inside the data (not part of a nested pair) breaks the framing.
//   length_prefix    the prefix is not part of the message
//   fixed(n)         n bytes each
//   none             no segmenting: each chunk passes through as it came
public abstract class Segmenter
{
    // Room for the Envelope around the message within a message pipe frame (Socket.MaxFrameSize).
    public const int DefaultMaxSegmentBytes = 16 * 1024 * 1024 - 64 * 1024;

    protected readonly ByteBuffer Buffer = new();

    protected Segmenter(int maxSegmentBytes)
    {
        if (maxSegmentBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxSegmentBytes));
        MaxSegmentBytes = maxSegmentBytes;
    }

    public int MaxSegmentBytes { get; }
    public event Action<FramingError>? Error;

    public static Segmenter For(Framing framing, int maxSegmentBytes = DefaultMaxSegmentBytes) => framing.Kind switch
    {
        FramingKind.None => new PassThroughSegmenter(maxSegmentBytes),
        FramingKind.Lines => new DelimiterSegmenter(new[] { (byte)'\n' }, keepDelimiter: false, stripCr: true, skipBlank: false, emitTrailing: true, maxSegmentBytes),
        FramingKind.Ndjson => new DelimiterSegmenter(new[] { (byte)'\n' }, keepDelimiter: false, stripCr: true, skipBlank: true, emitTrailing: true, maxSegmentBytes),
        FramingKind.Delimiter => new DelimiterSegmenter(Framing.Bytes(framing.Delimiter!), keepDelimiter: true, stripCr: false, skipBlank: false, emitTrailing: false, maxSegmentBytes),
        FramingKind.Markers => new MarkerSegmenter(Framing.Bytes(framing.Start!), Framing.Bytes(framing.End!), maxSegmentBytes),
        FramingKind.LengthPrefix => new LengthPrefixSegmenter(framing.Width, framing.BigEndian, maxSegmentBytes),
        FramingKind.Fixed => framing.Size <= maxSegmentBytes ? new FixedSegmenter(framing.Size, maxSegmentBytes)
            : throw new ArgumentException($"fixed({framing.Size}) messages exceed the {maxSegmentBytes}-byte maximum"),
        _ => throw new ArgumentException($"no segmenter for framing {framing}"),
    };

    public List<byte[]> Push(ReadOnlySpan<byte> chunk)
    {
        var segments = new List<byte[]>();
        if (chunk.Length == 0) return segments;
        Buffer.Append(chunk);
        Segment(segments);
        return segments;
    }

    public List<byte[]> Complete()
    {
        var segments = new List<byte[]>();
        Finish(segments);
        Buffer.Clear();
        return segments;
    }

    protected abstract void Segment(List<byte[]> segments);
    protected abstract void Finish(List<byte[]> segments);

    protected void Report(string kind, string message, long offset, long dropped) =>
        Error?.Invoke(new FramingError(kind, message, offset, dropped));

    // The same whichever chunk boundaries the message arrived with.
    protected void ReportOversize(long offset, long dropped) =>
        Report("oversize", $"message exceeds the {MaxSegmentBytes}-byte maximum; {dropped} bytes dropped", offset, dropped);

    // A growable window over the unconsumed part of the stream, tracking its offset in the stream.
    protected sealed class ByteBuffer
    {
        private byte[] _data = new byte[4096];
        private int _start;
        public int Count { get; private set; }
        public long Offset { get; private set; }            // stream offset of the first buffered byte

        public ReadOnlySpan<byte> Span => _data.AsSpan(_start, Count);

        public void Append(ReadOnlySpan<byte> chunk)
        {
            if (_start + Count + chunk.Length > _data.Length)
            {
                if (Count + chunk.Length <= _data.Length / 2)
                    System.Buffer.BlockCopy(_data, _start, _data, 0, Count);
                else
                {
                    var bigger = new byte[Math.Max(_data.Length * 2, Count + chunk.Length)];
                    System.Buffer.BlockCopy(_data, _start, bigger, 0, Count);
                    _data = bigger;
                }
                _start = 0;
            }
            chunk.CopyTo(_data.AsSpan(_start + Count));
            Count += chunk.Length;
        }

        public byte[] Take(int n)
        {
            var bytes = _data.AsSpan(_start, n).ToArray();
            Skip(n);
            return bytes;
        }

        public void Skip(int n)
        {
            _start += n;
            Count -= n;
            Offset += n;
            if (Count == 0) _start = 0;
        }

        public void Clear() => Skip(Count);

        // The first index of `pattern` at or after `from`, or -1.
        public int IndexOf(ReadOnlySpan<byte> pattern, int from)
        {
            if (from >= Count) return -1;
            int i = Span[from..].IndexOf(pattern);
            return i < 0 ? -1 : from + i;
        }
    }
}

public sealed class PassThroughSegmenter : Segmenter
{
    public PassThroughSegmenter(int maxSegmentBytes) : base(maxSegmentBytes) { }
    protected override void Segment(List<byte[]> segments) => segments.Add(Buffer.Take(Buffer.Count));
    protected override void Finish(List<byte[]> segments) { }
}

// lines, ndjson and delimiter(d).
public sealed class DelimiterSegmenter : Segmenter
{
    private readonly byte[] _delimiter;
    private readonly bool _keep, _stripCr, _skipBlank, _emitTrailing;
    private int _scanFrom;                 // no delimiter starts before this index of the buffer
    private long _discardFrom = -1;        // skipping an oversize message that began at this offset
    private long _discarded;

    public DelimiterSegmenter(byte[] delimiter, bool keepDelimiter, bool stripCr, bool skipBlank, bool emitTrailing, int maxSegmentBytes)
        : base(maxSegmentBytes)
    {
        if (delimiter.Length == 0) throw new ArgumentException("empty delimiter");
        (_delimiter, _keep, _stripCr, _skipBlank, _emitTrailing) = (delimiter, keepDelimiter, stripCr, skipBlank, emitTrailing);
    }

    protected override void Segment(List<byte[]> segments)
    {
        while (true)
        {
            int i = Buffer.IndexOf(_delimiter, _scanFrom);
            if (i < 0)
            {
                // Keep a possible partial delimiter at the end; everything before it belongs to the message.
                int settled = Math.Max(0, Buffer.Count - (_delimiter.Length - 1));
                if (_discardFrom >= 0) { _discarded += settled; Buffer.Skip(settled); _scanFrom = 0; }
                else if (settled > MaxSegmentBytes) { _discardFrom = Buffer.Offset; _discarded = settled; Buffer.Skip(settled); _scanFrom = 0; }
                else _scanFrom = settled;
                return;
            }
            int end = i + _delimiter.Length;
            if (_discardFrom >= 0)
            {
                _discarded += end;
                Buffer.Skip(end);
                Oversize();
            }
            else if (i > MaxSegmentBytes)
            {
                long at = Buffer.Offset;
                Buffer.Skip(end);
                ReportOversize(at, end);
            }
            else Emit(segments, Buffer.Take(_keep ? end : i), end - (_keep ? end : i));
            _scanFrom = 0;
        }
    }

    protected override void Finish(List<byte[]> segments)
    {
        if (_discardFrom >= 0) { _discarded += Buffer.Count; Oversize(); return; }
        if (Buffer.Count == 0) return;
        if (_emitTrailing) Emit(segments, Buffer.Take(Buffer.Count), 0);
        else Report("truncated", $"stream ended inside a message ({Buffer.Count} bytes without a delimiter); dropped", Buffer.Offset, Buffer.Count);
    }

    private void Emit(List<byte[]> segments, byte[] message, int delimiterBytes)
    {
        if (delimiterBytes > 0) Buffer.Skip(delimiterBytes);
        if (_stripCr && message.Length > 0 && message[^1] == (byte)'\r') message = message[..^1];
        if (_skipBlank && message.AsSpan().IndexOfAnyExcept((byte)' ', (byte)'\t') < 0) return;
        segments.Add(message);
    }

    private void Oversize()
    {
        ReportOversize(_discardFrom, _discarded);
        _discardFrom = -1;
        _discarded = 0;
    }
}

// markers(start, end), nesting.
public sealed class MarkerSegmenter : Segmenter
{
    private readonly byte[] _start, _end;
    private readonly int _tail;            // bytes kept back that may begin a marker split across chunks
    private bool _inside;
    private int _depth;
    private int _scanFrom;
    private long _unframedFrom = -1, _unframed;
    private long _discardFrom = -1, _discarded;     // skipping an oversize message, still following its nesting

    public MarkerSegmenter(byte[] start, byte[] end, int maxSegmentBytes) : base(maxSegmentBytes)
    {
        if (start.Length == 0 || end.Length == 0) throw new ArgumentException("empty marker");
        (_start, _end) = (start, end);
        _tail = Math.Max(start.Length, end.Length) - 1;
    }

    protected override void Segment(List<byte[]> segments)
    {
        while (true)
        {
            if (!_inside)
            {
                int s = Buffer.IndexOf(_start, 0);
                int skip = s < 0 ? Math.Max(0, Buffer.Count - (_start.Length - 1)) : s;
                if (skip > 0)
                {
                    if (_unframedFrom < 0) _unframedFrom = Buffer.Offset;
                    _unframed += skip;
                    Buffer.Skip(skip);
                }
                if (s < 0) return;
                ReportUnframed();
                _inside = true;
                _depth = 1;
                _scanFrom = _start.Length;
            }

            int nextStart = Buffer.IndexOf(_start, _scanFrom), nextEnd = Buffer.IndexOf(_end, _scanFrom);
            if (nextEnd >= 0 && (nextStart < 0 || nextEnd <= nextStart))
            {
                _scanFrom = nextEnd + _end.Length;
                if (--_depth > 0) continue;
                int length = _scanFrom;
                long at = Buffer.Offset;
                if (_discardFrom >= 0)
                {
                    Buffer.Skip(length);
                    ReportOversize(_discardFrom, _discarded + length);
                    _discardFrom = -1;
                    _discarded = 0;
                }
                else if (length > MaxSegmentBytes)
                {
                    Buffer.Skip(length);
                    ReportOversize(at, length);
                }
                else segments.Add(Buffer.Take(length));
                _inside = false;
                _scanFrom = 0;
            }
            else if (nextStart >= 0)
            {
                _depth++;
                _scanFrom = nextStart + _start.Length;
            }
            else
            {
                // No marker yet: all but a possible partial marker at the end belongs to the message. An
                // oversize one is dropped as it arrives, following its nesting to its end.
                int settled = Math.Max(0, Buffer.Count - _tail);
                _scanFrom = Math.Max(_scanFrom, settled);
                if (_discardFrom < 0 && settled > MaxSegmentBytes) _discardFrom = Buffer.Offset;
                if (_discardFrom >= 0)
                {
                    _discarded += settled;
                    Buffer.Skip(settled);
                    _scanFrom -= settled;
                }
                return;
            }
        }
    }

    protected override void Finish(List<byte[]> segments)
    {
        if (_inside && _discardFrom >= 0)
        {
            ReportOversize(_discardFrom, _discarded + Buffer.Count);
            (_discardFrom, _discarded, _inside) = (-1, 0, false);
            return;
        }
        if (_inside)
        {
            Report("truncated", $"stream ended inside a message ({Buffer.Count} bytes, {_depth} end marker(s) missing); dropped", Buffer.Offset, Buffer.Count);
            _inside = false;
            return;
        }
        if (Buffer.Count > 0)
        {
            if (_unframedFrom < 0) _unframedFrom = Buffer.Offset;
            _unframed += Buffer.Count;
        }
        ReportUnframed();
    }

    private void ReportUnframed()
    {
        if (_unframed == 0) return;
        Report("unframed", $"{_unframed} bytes outside any message (before a start marker); dropped", _unframedFrom, _unframed);
        _unframedFrom = -1;
        _unframed = 0;
    }
}

// length_prefix(width, le|be): an unsigned length, then that many bytes.
public sealed class LengthPrefixSegmenter : Segmenter
{
    private readonly int _width;
    private readonly bool _bigEndian;
    private ulong _skipping;               // bytes left of an oversize message being skipped

    public LengthPrefixSegmenter(int width, bool bigEndian, int maxSegmentBytes) : base(maxSegmentBytes)
    {
        if (width is not (1 or 2 or 4 or 8)) throw new ArgumentException("width must be 1, 2, 4 or 8");
        (_width, _bigEndian) = (width, bigEndian);
    }

    protected override void Segment(List<byte[]> segments)
    {
        while (true)
        {
            if (_skipping > 0)
            {
                int n = (int)Math.Min(_skipping, (ulong)Buffer.Count);
                Buffer.Skip(n);
                _skipping -= (ulong)n;
                if (_skipping > 0) return;
            }
            if (Buffer.Count < _width) return;
            var prefix = Buffer.Span[.._width];
            ulong length = 0;
            for (int i = 0; i < _width; i++) length |= (ulong)prefix[_bigEndian ? _width - 1 - i : i] << (8 * i);
            if (length > (ulong)MaxSegmentBytes)
            {
                ReportOversize(Buffer.Offset, (long)Math.Min(length + (ulong)_width, long.MaxValue));
                Buffer.Skip(_width);
                _skipping = length;
                continue;
            }
            if ((ulong)Buffer.Count < (ulong)_width + length) return;
            Buffer.Skip(_width);
            segments.Add(Buffer.Take((int)length));
        }
    }

    protected override void Finish(List<byte[]> segments)
    {
        if (_skipping > 0) { _skipping = 0; return; }   // already reported as oversize
        if (Buffer.Count > 0)
            Report("truncated", $"stream ended inside a message ({Buffer.Count} bytes); dropped", Buffer.Offset, Buffer.Count);
    }
}

// fixed(n).
public sealed class FixedSegmenter : Segmenter
{
    private readonly int _size;

    public FixedSegmenter(int size, int maxSegmentBytes) : base(maxSegmentBytes)
    {
        if (size <= 0 || size > maxSegmentBytes) throw new ArgumentOutOfRangeException(nameof(size));
        _size = size;
    }

    protected override void Segment(List<byte[]> segments)
    {
        while (Buffer.Count >= _size) segments.Add(Buffer.Take(_size));
    }

    protected override void Finish(List<byte[]> segments)
    {
        if (Buffer.Count > 0)
            Report("truncated", $"stream ended inside a message ({Buffer.Count} of {_size} bytes); dropped", Buffer.Offset, Buffer.Count);
    }
}
