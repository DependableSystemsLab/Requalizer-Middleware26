using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OneOS.WebTerminal
{
    // A page's WebSocket (/ws), carrying JSON text messages. The first message says what the socket is for,
    // e.g. { "connection": "UserShell" }; the handler registered under that name then owns the socket.
    public sealed class ClientSocket
    {
        private const int MaxMessageBytes = 1 << 20;

        private readonly WebSocket _ws;
        private readonly SemaphoreSlim _sendLock = new(1, 1);   // a WebSocket allows one send at a time

        public ClientSocket(WebSocket ws, WebSession session, string id)
        {
            _ws = ws;
            Session = session;
            Id = id;
        }

        public WebSession Session { get; }
        public string Id { get; }
        public WebSocketState State => _ws.State;

        // The next text message as JSON, or null once the page has closed the socket.
        public async Task<JsonElement?> ReceiveJsonAsync(CancellationToken ct)
        {
            var text = await ReceiveTextAsync(ct);
            return text == null ? null : JsonDocument.Parse(text).RootElement.Clone();
        }

        public async Task<string?> ReceiveTextAsync(CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes) throw new InvalidDataException($"message larger than {MaxMessageBytes} bytes");
                if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }

        public Task SendJsonAsync<T>(T value, CancellationToken ct = default) =>
            SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, ct);

        public Task SendBinaryAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default) =>
            SendAsync(bytes, WebSocketMessageType.Binary, ct);

        private async Task SendAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType type, CancellationToken ct)
        {
            await _sendLock.WaitAsync(ct);
            try { await _ws.SendAsync(bytes, type, true, ct); }
            finally { _sendLock.Release(); }
        }

        public async Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken ct = default)
        {
            if (_ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
            await _sendLock.WaitAsync(ct);
            try { await _ws.CloseAsync(status, description, ct); }
            catch (WebSocketException) { }
            finally { _sendLock.Release(); }
        }
    }

    // Runs one handler per connection kind. A handler returns when its socket is done (closed by the page or by it).
    public delegate Task ConnectionHandler(ClientSocket socket, JsonElement request, CancellationToken ct);

    public sealed class WebSocketBroker
    {
        private readonly Dictionary<string, ConnectionHandler> _handlers = new();
        private readonly ILogger _logger;

        public WebSocketBroker(ILogger<WebSocketBroker> logger) { _logger = logger; }

        public void Register(string connection, ConnectionHandler handler) => _handlers[connection] = handler;

        public async Task AcceptAsync(HttpContext ctx, WebSession session)
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var socket = new ClientSocket(ws, session, ctx.TraceIdentifier);
            var ct = ctx.RequestAborted;
            string? connection = null;
            try
            {
                var request = await socket.ReceiveJsonAsync(ct);
                if (request == null) return;
                connection = request.Value.TryGetProperty("connection", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (connection == null || !_handlers.TryGetValue(connection, out var handler))
                {
                    _logger.LogWarning("WebSocket {Id} ({User}, {Protocol}) asked for unsupported connection '{Connection}'",
                        socket.Id, session.Username, ctx.Request.Protocol, connection);
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, $"unsupported connection '{connection}'", ct);
                    return;
                }

                _logger.LogInformation("WebSocket {Id} ({User}, {Protocol}) opened: {Connection}", socket.Id, session.Username, ctx.Request.Protocol, connection);
                await handler(socket, request.Value, ct);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                // the page went away
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebSocket {Id} ({Connection}) failed", socket.Id, connection);
                await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "server error", CancellationToken.None);
            }
            finally
            {
                if (connection != null) _logger.LogInformation("WebSocket {Id} closed: {Connection}", socket.Id, connection);
            }
        }
    }
}
