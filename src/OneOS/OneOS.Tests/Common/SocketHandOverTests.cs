using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using OneOS.Common;

namespace OneOS.Tests.Common;

// A connection switching from framed messages to raw bytes (terminal connect, raw pipe request).
public class SocketHandOverTests
{
    // Like SslStream: a second read while one is pending throws NotSupportedException.
    private sealed class SingleReaderStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[] _current = Array.Empty<byte>();
        private int _offset;
        private int _reading;

        public void Push(byte[] chunk) => _chunks.Writer.TryWrite(chunk);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _reading, 1) == 1)
                throw new NotSupportedException("This method may not be called when another read operation is pending.");
            try
            {
                if (_offset >= _current.Length)
                {
                    _current = await _chunks.Reader.ReadAsync(ct);
                    _offset = 0;
                }
                int n = Math.Min(buffer.Length, _current.Length - _offset);
                _current.AsMemory(_offset, n).CopyTo(buffer);
                _offset += n;
                return n;
            }
            finally { Interlocked.Exchange(ref _reading, 0); }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Frame(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    [Fact]
    public async Task HandOverToRawLeavesNoPendingReadAndKeepsBytesReadPastTheFrame()
    {
        var stream = new SingleReaderStream();
        var socket = new Socket(stream, NullLogger.Instance, "test");
        var ended = false;
        socket.OnEnded += _ => ended = true;
        var raw = new StringBuilder();
        var rawEnded = new TaskCompletionSource();
        Exception? rawError = null;

        socket.Listen(frame =>
        {
            Assert.Equal("connect", Encoding.UTF8.GetString(frame));
            socket.HandOver();
            // The handler switches to raw later, after its own awaits (sign-in, shell creation...).
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                try { await socket.ListenRaw(chunk => { lock (raw) raw.Append(Encoding.UTF8.GetString(chunk)); }); }
                catch (Exception ex) { rawError = ex; }
                rawEnded.TrySetResult();
            });
        });

        // The handshake frame and the first raw bytes arrive in one read.
        stream.Push(Frame("connect").Concat(Encoding.UTF8.GetBytes("hello ")).ToArray());
        await Task.Delay(400);
        stream.Push(Encoding.UTF8.GetBytes("world"));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && raw.ToString() != "hello world") await Task.Delay(20);
        Assert.Null(rawError);
        Assert.Equal("hello world", raw.ToString());
        Assert.False(ended);                 // a hand-over is not the end of the connection
        Assert.False(rawEnded.Task.IsCompleted);
    }
}

public class CommandLineTests
{
    [Theory]
    [InlineData("python a.py", new[] { "python", "a.py" })]
    [InlineData("  node   x.js  --flag ", new[] { "node", "x.js", "--flag" })]
    [InlineData("python \"my script.py\" 'a b' c\\ d", new[] { "python", "my script.py", "a b", "c d" })]
    [InlineData("echo \"say \\\"hi\\\"\" ''", new[] { "echo", "say \"hi\"", "" })]
    public void SplitsWordsWithQuotesAndEscapes(string line, string[] expected) =>
        Assert.Equal(expected, OneOS.Common.CommandLine.Split(line));
}
