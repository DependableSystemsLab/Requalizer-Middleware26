using System.Text;
using OneOS.Runtime.Graphs;
using OneOS.Runtime.Language;

namespace OneOS.Tests.Graphs;

// Segmenters cut a process's output stream into messages by its port's framing (L§5.2, plan step 6.3).
public class SegmenterTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static string S(byte[] b) => Encoding.UTF8.GetString(b);

    // Feeds `stream` in every one of several chunkings and checks each gives the same messages and errors.
    private static (List<byte[]> Segments, List<FramingError> Errors) Run(Framing framing, byte[] stream, int max = Segmenter.DefaultMaxSegmentBytes)
    {
        (List<byte[]>, List<FramingError>) Once(IEnumerable<byte[]> chunks)
        {
            var seg = Segmenter.For(framing, max);
            var errors = new List<FramingError>();
            seg.Error += errors.Add;
            var segments = new List<byte[]>();
            foreach (var c in chunks) segments.AddRange(seg.Push(c));
            segments.AddRange(seg.Complete());
            return (segments, errors);
        }

        var whole = Once(new[] { stream });
        var chunkings = new List<IEnumerable<byte[]>> { stream.Chunk(1), stream.Chunk(2), stream.Chunk(3), stream.Chunk(7) };
        var random = new Random(42);
        for (int r = 0; r < 20; r++)
        {
            var chunks = new List<byte[]>();
            for (int i = 0; i < stream.Length;)
            {
                int n = Math.Min(stream.Length - i, random.Next(0, 12));
                chunks.Add(stream[i..(i + n)]);
                i += n;
            }
            chunkings.Add(chunks);
        }
        foreach (var chunks in chunkings)
        {
            var (segments, errors) = Once(chunks);
            Assert.Equal(whole.Item1.Select(Convert.ToHexString), segments.Select(Convert.ToHexString));
            Assert.Equal(whole.Item2, errors);
        }
        return whole;
    }

    [Fact]
    public void LinesStripTerminatorsKeepEmptyLinesAndTheUnterminatedLast()
    {
        var (segments, errors) = Run(Framing.Lines, B("alpha\nbeta\r\n\ngamma"));
        Assert.Equal(new[] { "alpha", "beta", "", "gamma" }, segments.Select(S));
        Assert.Empty(errors);
    }

    [Fact]
    public void NdjsonSkipsBlankLines()
    {
        var (segments, errors) = Run(Framing.Ndjson, B("{\"a\":1}\n\n  \r\n{\"b\":\"x\\ny\"}\r\n{\"c\":3}"));
        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":\"x\\ny\"}", "{\"c\":3}" }, segments.Select(S));
        Assert.Empty(errors);
    }

    [Fact]
    public void DelimiterIsKeptAndATruncatedTailIsReported()
    {
        var (segments, errors) = Run(Framing.Parse("delimiter(0d0a00)"), B("one\r\n\0two\r\n\0thr\r\n"));
        Assert.Equal(new[] { "one\r\n\0", "two\r\n\0" }, segments.Select(S));
        var e = Assert.Single(errors);
        Assert.Equal(("truncated", 12L, 5L), (e.Kind, e.Offset, e.DroppedBytes));
    }

    [Fact]
    public void JpegMarkersNestAndBytesBetweenImagesAreReported()
    {
        // An image with an embedded thumbnail (its own SOI … EOI), noise, a second image, a cut-off third.
        byte[] img1 = { 0xFF, 0xD8, 0x01, 0xFF, 0xE1, 0xFF, 0xD8, 0x02, 0xFF, 0xD9, 0x03, 0xFF, 0x00, 0xFF, 0xD9 };
        byte[] noise = { 0x55, 0xFF, 0x66 };
        byte[] img2 = { 0xFF, 0xD8, 0x04, 0xFF, 0xD9 };
        byte[] cut = { 0xFF, 0xD8, 0x05, 0x06 };
        var (segments, errors) = Run(Framing.Parse("markers(ffd8, ffd9)"), img1.Concat(noise).Concat(img2).Concat(cut).ToArray());
        Assert.Equal(new[] { Convert.ToHexString(img1), Convert.ToHexString(img2) }, segments.Select(Convert.ToHexString));
        Assert.Equal(new[] { ("unframed", (long)img1.Length, 3L), ("truncated", (long)(img1.Length + noise.Length + img2.Length), 4L) },
            errors.Select(e => (e.Kind, e.Offset, e.DroppedBytes)));
    }

    [Theory]
    [InlineData("length_prefix(1, le)")]
    [InlineData("length_prefix(2, be)")]
    [InlineData("length_prefix(4, le)")]
    [InlineData("length_prefix(8, be)")]
    public void LengthPrefixedMessagesIncludingEmptyOnes(string text)
    {
        var framing = Framing.Parse(text);
        var messages = new[] { "hello", "", "a longer message with \n newlines \0 and nulls" };
        var stream = messages.SelectMany(m => Prefix(framing, B(m).Length).Concat(B(m))).ToArray();
        var (segments, errors) = Run(framing, stream);
        Assert.Equal(messages, segments.Select(S));
        Assert.Empty(errors);
    }

    private static byte[] Prefix(Framing f, long length)
    {
        var p = new byte[f.Width];
        for (int i = 0; i < f.Width; i++) p[f.BigEndian ? f.Width - 1 - i : i] = (byte)(length >> (8 * i));
        return p;
    }

    [Fact]
    public void FixedSizeMessages()
    {
        var (segments, errors) = Run(Framing.Parse("fixed(3)"), B("abcdefgh"));
        Assert.Equal(new[] { "abc", "def" }, segments.Select(S));
        Assert.Equal(("truncated", 6L, 2L), (errors.Single().Kind, errors.Single().Offset, errors.Single().DroppedBytes));
    }

    [Fact]
    public void NoFramingPassesChunksThrough()
    {
        var seg = Segmenter.For(Framing.None);
        Assert.Equal(new[] { "ab", "c\n" }, seg.Push(B("ab")).Concat(seg.Push(B("c\n"))).Select(S));
        Assert.Empty(seg.Push(Array.Empty<byte>()));
        Assert.Empty(seg.Complete());
    }

    // A message over the maximum is dropped and reported once; the messages around it still arrive.

    [Fact]
    public void OversizeLineIsSkippedUpToTheNextNewline()
    {
        var (segments, errors) = Run(Framing.Lines, B("ok\n" + new string('x', 40) + "\nfine\n"), max: 16);
        Assert.Equal(new[] { "ok", "fine" }, segments.Select(S));
        var e = Assert.Single(errors);
        Assert.Equal(("oversize", 3L, 41L), (e.Kind, e.Offset, e.DroppedBytes));
    }

    [Fact]
    public void OversizeLineAtEndOfStream()
    {
        var (segments, errors) = Run(Framing.Lines, B("ok\n" + new string('x', 40)), max: 16);
        Assert.Equal(new[] { "ok" }, segments.Select(S));
        Assert.Equal(("oversize", 3L, 40L), (errors.Single().Kind, errors.Single().Offset, errors.Single().DroppedBytes));
    }

    [Fact]
    public void OversizeLengthPrefixedMessageIsSkippedAndTheStreamStaysAligned()
    {
        var framing = Framing.Parse("length_prefix(2, le)");
        var big = new byte[40];
        var stream = Prefix(framing, 2).Concat(B("hi")).Concat(Prefix(framing, big.Length)).Concat(big).Concat(Prefix(framing, 3)).Concat(B("end")).ToArray();
        var (segments, errors) = Run(framing, stream, max: 16);
        Assert.Equal(new[] { "hi", "end" }, segments.Select(S));
        Assert.Equal(("oversize", 4L, 42L), (errors.Single().Kind, errors.Single().Offset, errors.Single().DroppedBytes));
    }

    [Fact]
    public void OversizeMarkedMessageIsSkippedThroughItsEndMarker()
    {
        byte[] big = new byte[] { 0xFF, 0xD8 }.Concat(new byte[40]).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();
        byte[] small = { 0xFF, 0xD8, 0x07, 0xFF, 0xD9 };
        var (segments, errors) = Run(Framing.Parse("markers(ffd8, ffd9)"), big.Concat(small).ToArray(), max: 16);
        Assert.Equal(new[] { Convert.ToHexString(small) }, segments.Select(Convert.ToHexString));
        var e = Assert.Single(errors);
        Assert.Equal(("oversize", 0L, (long)big.Length), (e.Kind, e.Offset, e.DroppedBytes));
    }

    [Fact]
    public void FixedSizeOverTheMaximumIsRejectedUpFront() =>
        Assert.Throws<ArgumentException>(() => Segmenter.For(Framing.Parse("fixed(100)"), maxSegmentBytes: 16));

    [Fact]
    public void LargeStreamsStayLinearAndExact()
    {
        var lines = Enumerable.Range(0, 20_000).Select(i => $"line {i} " + new string('z', i % 50)).ToList();
        var seg = Segmenter.For(Framing.Lines);
        var stream = B(string.Join("\n", lines) + "\n");
        var got = new List<string>();
        foreach (var c in stream.Chunk(4096)) got.AddRange(seg.Push(c).Select(S));
        got.AddRange(seg.Complete().Select(S));
        Assert.Equal(lines, got);
    }
}
