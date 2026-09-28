// Adapted from RadOS: Socket — length-prefixed framing over byte streams.
// Modernized for .NET 8: async Task.Run, Memory<byte>, ILogger, frame-size guards.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers;
using Microsoft.Extensions.Logging;

namespace OneOS.Common;

/// <summary>
/// A framed socket abstraction over a <see cref="Stream"/>.
/// Uses a 4-byte little-endian length header followed by payload bytes.
/// </summary>
public sealed class Socket
{
    /// <summary>Read buffer size in bytes.</summary>
    public const int BufferSize = 131_072;

    /// <summary>Maximum allowed frame size (16 MB). Protects against corrupt/malicious headers.</summary>
    public const int MaxFrameSize = 16 * 1024 * 1024;

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    // Hand-over between listeners (framed → raw): bytes the framed loop read past the frame at which it
    // handed the stream over, delivered first by the next listener; and the loop being handed over, which
    // the next listener waits for so that two reads are never pending on the stream at once.
    private byte[]? _unconsumed;
    private volatile bool _handingOver;

    /// <summary>The underlying stream for this socket.</summary>
    public Stream? Stream { get; set; }

    public string UsageContext { get; set; } = "Unknown";

    /// <summary>
    /// Raised when the listen loop ends (normally or due to error).
    /// The argument is the exception that caused termination, or <c>null</c> for graceful stop.
    /// </summary>
    public event Action<Exception?>? OnEnded;

    /// <summary>
    /// Initializes a new <see cref="Socket"/> without a stream.
    /// The <see cref="Stream"/> property must be set before calling Listen/Send.
    /// </summary>
    public Socket(ILogger logger, string usageContext = "Unknown")
    {
        _logger = logger;
        UsageContext = usageContext;
    }

    /// <summary>
    /// Initializes a new <see cref="Socket"/> with the given stream.
    /// </summary>
    public Socket(Stream stream, ILogger logger, string usageContext = "Unknown")
    {
        Stream = stream;
        _logger = logger;
        UsageContext = usageContext;
    }

    /// <summary>
    /// Starts reading length-prefixed frames from the stream.
    /// Each complete frame is delivered to <paramref name="onMessage"/>.
    /// </summary>
    /// <returns>A task that completes when the listen loop exits.</returns>
    /// <exception cref="InvalidOperationException">Thrown if already listening.</exception>
    public Task Listen(Action<byte[]> onMessage)
    {
        if (_listenTask is { IsCompleted: false } && !_handingOver)
            throw new InvalidOperationException("Cannot listen on Socket — already listening.");

        if (Stream is null)
            throw new InvalidOperationException("Cannot listen — Stream is not set.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var stream = Stream;

        var previous = _listenTask;
        _listenTask = Task.Run(async () =>
        {
            Exception? exitException = null;
            bool handedOver = false;

            var buffer = new byte[BufferSize];
            var header = new byte[4];
            int headerRead = 0;
            byte[]? frame = null;
            int frameCursor = 0;

            try
            {
                var carried = await TakeOverAsync(previous).ConfigureAwait(false);
                while (!ct.IsCancellationRequested && !handedOver)
                {
                    int bytesRead;
                    if (carried is { Length: > 0 })
                    {
                        carried.CopyTo(buffer, 0);
                        bytesRead = carried.Length;
                        carried = null;
                    }
                    else bytesRead = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);

                    if (bytesRead == 0)
                        break; // Stream closed by remote

                    int cursor = 0;
                    while (cursor < bytesRead)
                    {
                        // Read header bytes until we have all 4
                        while (frame is null && cursor < bytesRead)
                        {
                            header[headerRead] = buffer[cursor];
                            headerRead++;
                            cursor++;

                            if (headerRead == 4)
                            {
                                int frameSize = BitConverter.ToInt32(header, 0);
                                if (frameSize <= 0 || frameSize > MaxFrameSize)
                                {
                                    _logger.LogError("Invalid frame size {FrameSize} — aborting listen loop", frameSize);
                                    return;
                                }
                                frame = new byte[frameSize];
                                frameCursor = 0;
                                headerRead = 0;
                            }
                        }

                        if (frame is null)
                            break; // Need more header bytes from next read

                        int frameBytesLeft = frame.Length - frameCursor;
                        int bytesLeft = bytesRead - cursor;

                        if (frameBytesLeft <= bytesLeft)
                        {
                            // Complete frame available in this read
                            Buffer.BlockCopy(buffer, cursor, frame, frameCursor, frameBytesLeft);
                            cursor += frameBytesLeft;
                            var complete = frame;
                            frame = null;
                            onMessage(complete);
                            if (_handingOver)
                            {
                                // Hand-over: keep what was read past this frame for the next listener.
                                _unconsumed = cursor < bytesRead ? buffer.AsSpan(cursor, bytesRead - cursor).ToArray() : null;
                                handedOver = true;
                                break;
                            }
                        }
                        else
                        {
                            // Partial frame — copy what we have and wait for more
                            Buffer.BlockCopy(buffer, cursor, frame, frameCursor, bytesLeft);
                            cursor += bytesLeft;
                            frameCursor += bytesLeft;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful cancellation
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Socket IO exception during listen [{UsageContext}]", UsageContext);
                exitException = new IOException($"Socket IO exception during listen [{UsageContext}]: {ex.Message}", ex);
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogDebug(ex, "Socket disposed during listen [{UsageContext}]", UsageContext);
                exitException = new ObjectDisposedException($"Socket disposed during listen [{UsageContext}]: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected exception in socket listen loop [{UsageContext}]", UsageContext);
                exitException = ex;
                throw;
            }
            finally
            {
                if (!handedOver) OnEnded?.Invoke(exitException);
            }
        }, ct);

        return _listenTask;
    }

    /// <summary>
    /// Called from a frame callback of <see cref="Listen"/> to hand the stream over to the next listener
    /// (e.g. <see cref="ListenRaw"/> after a handshake): the framed loop stops after the current frame
    /// without reading again, so no read is left pending, and any bytes it already read past that frame
    /// go to the next listener. <see cref="OnEnded"/> is not raised for a hand-over. Call it before
    /// starting whatever will listen next.
    /// </summary>
    public void HandOver() => _handingOver = true;

    // A new listener waits for a loop that handed the stream over to finish (it issues no further read),
    // then takes the bytes that loop read past its last frame.
    private async Task<byte[]?> TakeOverAsync(Task? previous)
    {
        if (previous is not null && _handingOver)
        {
            try { await previous.ConfigureAwait(false); }
            catch (Exception) { }
        }
        _handingOver = false;
        var carried = _unconsumed;
        _unconsumed = null;
        return carried;
    }

    /// <summary>
    /// Starts reading raw (unframed) bytes from the stream.
    /// Each read delivers the raw buffer contents to <paramref name="onMessage"/>.
    /// </summary>
    /// <returns>A task that completes when the listen loop exits.</returns>
    /// <exception cref="InvalidOperationException">Thrown if already listening.</exception>
    public Task ListenRaw(Action<byte[]> onMessage)
    {
        if (_listenTask is { IsCompleted: false } && !_handingOver)
            throw new InvalidOperationException("Cannot listen on Socket — already listening.");

        if (Stream is null)
            throw new InvalidOperationException("Cannot listen — Stream is not set.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var stream = Stream;

        var previous = _listenTask;
        _listenTask = Task.Run(async () =>
        {
            Exception? exitException = null;
            var buffer = new byte[BufferSize];

            try
            {
                if (await TakeOverAsync(previous).ConfigureAwait(false) is { Length: > 0 } carried)
                    onMessage(carried);
                while (!ct.IsCancellationRequested)
                {
                    int bytesRead = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);

                    if (bytesRead == 0)
                        break; // Stream closed by remote

                    onMessage(buffer.AsSpan(0, bytesRead).ToArray());
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful cancellation
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Socket IO exception during raw listen [{UsageContext}]", UsageContext);
                exitException = new IOException($"Socket IO exception during raw listen [{UsageContext}]: {ex.Message}", ex);
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogDebug(ex, "Socket disposed during raw listen [{UsageContext}]", UsageContext);
                exitException = new ObjectDisposedException($"Socket disposed during raw listen [{UsageContext}]: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected exception in socket raw listen loop [{UsageContext}]", UsageContext);
                exitException = ex;
                throw;
            }
            finally
            {
                OnEnded?.Invoke(exitException);
            }
        }, ct);

        return _listenTask;
    }

    /// <summary>
    /// Sends a length-prefixed frame. Thread-safe via internal write lock.
    /// </summary>
    public async Task Send(byte[] payload)
    {
        if (Stream is null)
            throw new InvalidOperationException("Cannot send — Stream is not set.");

        // Combine header + payload into one write to avoid interleaving
        // when multiple callers send concurrently.
        var block = new byte[payload.Length + 4];
        Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, block, 0, 4);
        Buffer.BlockCopy(payload, 0, block, 4, payload.Length);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await Stream.WriteAsync(block.AsMemory()).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Sends raw bytes without length-prefix framing. Thread-safe via internal write lock.
    /// </summary>
    public async Task SendRaw(byte[] payload)
    {
        if (Stream is null)
            throw new InvalidOperationException("Cannot send — Stream is not set.");

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await Stream.WriteAsync(payload.AsMemory()).ConfigureAwait(false);
            await Stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Asynchronously reads a single length-prefixed frame, without holding a thread while it waits. Inside a
    /// runtime, use this rather than <see cref="Receive"/>: a thread-pool thread blocked on a peer's answer, which
    /// may itself need a pool thread, starves the pool when many connections open at once.
    /// </summary>
    public async Task<byte[]> ReceiveAsync(CancellationToken ct = default)
    {
        if (Stream is null)
            throw new InvalidOperationException("Cannot receive — Stream is not set.");

        var header = new byte[4];
        await Stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int frameSize = BitConverter.ToInt32(header, 0);
        if (frameSize <= 0 || frameSize > MaxFrameSize)
            throw new InvalidOperationException($"Invalid frame size: {frameSize}");
        var frame = new byte[frameSize];
        await Stream.ReadExactlyAsync(frame, ct).ConfigureAwait(false);
        return frame;
    }

    /// <summary>
    /// Synchronously reads a single length-prefixed frame.
    /// Used during initial handshake sequences; prefer <see cref="Listen"/> for ongoing reads.
    /// </summary>
    public byte[] Receive()
    {
        if (Stream is null)
            throw new InvalidOperationException("Cannot receive — Stream is not set.");

        var header = new byte[4];
        int bytesRead = 0;
        while (bytesRead < 4)
        {
            int read = Stream.Read(header, bytesRead, 4 - bytesRead);
            if (read == 0)
                throw new IOException("Stream closed while reading frame header.");
            bytesRead += read;
        }

        int frameSize = BitConverter.ToInt32(header, 0);
        if (frameSize <= 0 || frameSize > MaxFrameSize)
            throw new InvalidOperationException($"Invalid frame size: {frameSize}");

        var frame = new byte[frameSize];
        bytesRead = 0;
        while (bytesRead < frame.Length)
        {
            int read = Stream.Read(frame, bytesRead, frame.Length - bytesRead);
            if (read == 0)
                throw new IOException("Stream closed while reading frame payload.");
            bytesRead += read;
        }

        return frame;
    }

    /// <summary>
    /// Reads raw bytes from the stream (single read, no framing).
    /// Used during initial handshake sequences.
    /// </summary>
    public async Task<byte[]> StreamRead()
    {
        if (Stream is null)
            throw new InvalidOperationException("Cannot read — Stream is not set.");

        var buffer = new byte[BufferSize];
        int bytesRead = await Stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
        return buffer.AsSpan(0, bytesRead).ToArray();
    }

    /// <summary>
    /// Writes raw bytes to the stream at a specific offset and count.
    /// Used during initial handshake sequences.
    /// </summary>
    public async Task StreamWrite(byte[] buffer, int startIndex, int count)
    {
        if (Stream is null)
            throw new InvalidOperationException("Cannot write — Stream is not set.");

        await Stream.WriteAsync(buffer.AsMemory(startIndex, count)).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the listen loop without closing the underlying stream.
    /// </summary>
    public void StopListen()
    {
        if (_cts is not null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
        _listenTask = null; // Clear so it can be listened to again
    }

    /// <summary>
    /// Stops the listen loop and closes the stream.
    /// </summary>
    public async Task StopAsync()
    {
        try
        {
            if (Stream is not null)
                await Stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exception flushing stream during stop");
        }

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        try
        {
            Stream?.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exception closing stream during stop");
        }

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Listen task ended with exception during stop");
            }
        }

        _cts?.Dispose();
        _cts = null;
        _listenTask = null;
    }

    /// <summary>
    /// Stops the listen loop without closing the underlying stream.
    /// </summary>
    public async Task StopListeningAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Listen task ended with exception during StopListeningAsync");
            }
        }

        _cts?.Dispose();
        _cts = null;
        _listenTask = null;
    }
}
