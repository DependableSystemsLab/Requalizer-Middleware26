using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OneOS.Runtime.Driver
{
    // The runtime's end of the JavaScript environment's IPC (plan step 7; the process's end is
    // JavaScriptEnvironment/Runtime.js). Channels are inherited descriptors (FIFOs), not sockets. Frames:
    //     [u32 LE length][u8 kind][body]      length = 1 + body length
    //     'J'  a JSON message: { type: "call", transactionId, method, arguments }
    //                       or { type: "return", transactionId, hasError, result }
    //     'D'  stream data:    [u32 LE stream id][bytes]
    //     'E'  end of stream:  [u32 LE stream id][optional UTF-8 error message]
    // The control channel carries RPC both ways and every stream, multiplexed by id. The sync channel serves
    // one blocking call at a time (the process reads it with fs.readSync).
    public sealed class NodeIpcChannel
    {
        public const byte JsonFrame = (byte)'J', DataFrame = (byte)'D', EndFrame = (byte)'E';

        // Handles a call from the process. Throw FileNotFoundException for a missing file (the process sees
        // an ENOENT error), any other exception for a plain error.
        public delegate Task<object?> CallHandler(string method, JsonElement[] args);

        public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly Stream _fromProcess, _toProcess;
        private readonly CallHandler _handler;
        private readonly Action<string> _log;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly ConcurrentDictionary<uint, ChannelWriter<byte[]>> _incoming = new();
        private int _nextStream;
        private Task? _reading;
        private volatile bool _closed;                  // the process closed its end: nothing more to send

        public NodeIpcChannel(Stream fromProcess, Stream toProcess, CallHandler handler, Action<string>? log = null)
        {
            _fromProcess = fromProcess;
            _toProcess = toProcess;
            _handler = handler;
            _log = log ?? (_ => { });
        }

        // Ends when the process closes its end of the control channel.
        public Task Completion => _reading ?? Task.CompletedTask;

        // The channels are FIFOs: their read loops run on dedicated threads (DedicatedThread), not the pool.
        public void Start() => _reading = OneOS.Common.DedicatedThread.Run("oneos.js control channel", ReadLoop);

        // --- Frames ---

        public static byte[] Encode(byte kind, ReadOnlySpan<byte> body)
        {
            var frame = new byte[5 + body.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length + 1);
            frame[4] = kind;
            body.CopyTo(frame.AsSpan(5));
            return frame;
        }

        public static byte[] EncodeStream(byte kind, uint streamId, ReadOnlySpan<byte> payload)
        {
            var frame = new byte[9 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length + 5);
            frame[4] = kind;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), streamId);
            payload.CopyTo(frame.AsSpan(9));
            return frame;
        }

        public static byte[] EncodeJson(object message) => Encode(JsonFrame, JsonSerializer.SerializeToUtf8Bytes(message, Json));

        // One frame (kind byte first), or null at end of stream.
        public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
        {
            var header = new byte[4];
            if (!await ReadExactlyAsync(stream, header, ct)) return null;
            var frame = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
            if (frame.Length == 0 || !await ReadExactlyAsync(stream, frame, ct)) return null;
            return frame;
        }

        // Blocking versions of ReadFrameAsync, for the dedicated reader threads.
        public static byte[]? ReadFrame(Stream stream)
        {
            var header = new byte[4];
            if (!ReadExactly(stream, header)) return null;
            var frame = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
            if (frame.Length == 0 || !ReadExactly(stream, frame)) return null;
            return frame;
        }

        private static bool ReadExactly(Stream stream, byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) return false;
                read += n;
            }
            return true;
        }

        private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                if (n == 0) return false;
                read += n;
            }
            return true;
        }

        private async Task SendAsync(byte[] frame)
        {
            if (_closed) return;
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _toProcess.WriteAsync(frame).ConfigureAwait(false);
                await _toProcess.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { if (!_closed) _log($"control channel: write failed: {ex.Message}"); }
            finally { _writeLock.Release(); }
        }

        // --- Receiving ---

        private void ReadLoop()
        {
            try
            {
                while (ReadFrame(_fromProcess) is { } frame) Dispatch(frame);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            catch (Exception ex) { _log($"control channel: {ex.Message}"); }
            finally
            {
                _closed = true;
                foreach (var (_, tcs) in _pending) tcs.TrySetException(new IOException("the process closed its control channel"));
                foreach (var (_, writer) in _incoming) writer.TryComplete();
            }
        }

        private void Dispatch(byte[] frame)
        {
            switch (frame[0])
            {
                case JsonFrame:
                {
                    JsonElement message;
                    using (var doc = JsonDocument.Parse(frame.AsMemory(1))) message = doc.RootElement.Clone();
                    var type = message.GetProperty("type").GetString();
                    if (type == "call") _ = Task.Run(async () => await SendAsync(await HandleCallAsync(message).ConfigureAwait(false)).ConfigureAwait(false));
                    else if (type == "return" && _pending.TryRemove(message.GetProperty("transactionId").GetString()!, out var tcs))
                    {
                        if (message.TryGetProperty("hasError", out var e) && e.ValueKind == JsonValueKind.True)
                            tcs.TrySetException(new InvalidOperationException(message.GetProperty("result").ToString()));
                        else tcs.TrySetResult(message.TryGetProperty("result", out var r) ? r : default);
                    }
                    break;
                }
                case DataFrame:
                {
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(1));
                    if (_incoming.TryGetValue(id, out var writer)) writer.TryWrite(frame[5..]);
                    else _log($"control channel: data for unknown stream {id} dropped");
                    break;
                }
                case EndFrame:
                {
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(1));
                    if (_incoming.TryRemove(id, out var writer)) writer.TryComplete();
                    break;
                }
                default:
                    _log($"control channel: unknown frame kind {frame[0]}");
                    break;
            }
        }

        // A call from the process, answered with a return frame (shared by both channels).
        private async Task<byte[]> HandleCallAsync(JsonElement call)
        {
            var id = call.GetProperty("transactionId").GetString();
            var method = call.GetProperty("method").GetString() ?? "";
            var args = call.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
            try
            {
                var result = await _handler(method, args).ConfigureAwait(false);
                return EncodeJson(new { type = "return", transactionId = id, hasError = false, result });
            }
            catch (Exception ex)
            {
                var error = ex is FileNotFoundException or DirectoryNotFoundException ? "ENOENT" : ex.Message;
                if (error != "ENOENT") _log($"{method} failed: {ex.Message}");
                return EncodeJson(new { type = "return", transactionId = id, hasError = true, result = error });
            }
        }

        // The sync channel: one call, one return, until the process closes it (on a dedicated thread: a FIFO).
        public Task ServeSyncAsync(Stream fromProcess, Stream toProcess) => OneOS.Common.DedicatedThread.Run("oneos.js sync channel", () =>
        {
            try
            {
                while (ReadFrame(fromProcess) is { } frame)
                {
                    if (frame[0] != JsonFrame) { _log("sync channel: expected a JSON call"); continue; }
                    JsonElement call;
                    using (var doc = JsonDocument.Parse(frame.AsMemory(1))) call = doc.RootElement.Clone();
                    var reply = HandleCallAsync(call).GetAwaiter().GetResult();   // this thread is the channel's own
                    toProcess.Write(reply);
                    toProcess.Flush();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });

        // --- Calling the process ---

        public async Task<JsonElement> RequestAsync(string method, params object?[] args)
        {
            var id = Guid.NewGuid().ToString("N")[..10];
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            await SendAsync(EncodeJson(new { type = "call", transactionId = id, method, arguments = args })).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }

        // --- Streams ---

        public uint NewStreamId() => (uint)Interlocked.Increment(ref _nextStream);

        public Task SendDataAsync(uint streamId, ReadOnlyMemory<byte> data) => SendAsync(EncodeStream(DataFrame, streamId, data.Span));

        public Task EndStreamAsync(uint streamId, string? error = null) =>
            SendAsync(EncodeStream(EndFrame, streamId, error == null ? ReadOnlySpan<byte>.Empty : Encoding.UTF8.GetBytes(error)));

        // Data the process sends on a stream it writes, until it ends the stream.
        public void AcceptStream(uint streamId, ChannelWriter<byte[]> writer) => _incoming[streamId] = writer;
    }
}
