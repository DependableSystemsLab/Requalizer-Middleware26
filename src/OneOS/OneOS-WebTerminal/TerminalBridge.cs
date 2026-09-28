using System.Text;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OneOS.Common;
using OneOS.Runtime;

namespace OneOS.WebTerminal
{
    // An embeddable terminal client over the raw-byte REPL protocol (cluster-api.md §4). OneOS.Client.Terminal is
    // console-bound (it reads/writes the console and calls Environment.Exit), so this is a session object instead:
    // it connects, signs in (following TerminalRedirectResponse), then streams raw UTF-8 both ways. Kept inside the
    // WebTerminal so src/OneOS is untouched; the shape mirrors Terminal.SignInAsync.
    //
    // Output is buffered from the moment of connect (capped scrollback) and fanned out to any number of attached
    // consumers: AttachOutput replays the buffer and then streams new output, so a terminal tab opened after login
    // still sees the welcome banner and prompt. Capture and fan-out share one lock so no output is missed between
    // replay and subscription.
    public sealed class TerminalSession : IAsyncDisposable
    {
        private static readonly byte[] Interrupt = { 0x03 };   // Ctrl+C / ETX
        private const int OutputBufferCap = 16 * 1024;         // characters of scrollback replayed on attach

        private string _address;
        private readonly string _senderId;
        private readonly ILogger _logger;
        private SecureClientSideSocket? _socket;

        private readonly object _outputLock = new();
        private readonly StringBuilder _buffer = new();
        private readonly Dictionary<Guid, Action<string>> _subscribers = new();

        // senderId keys the cluster session; reusing one reattaches to its shell (§4.2).
        public TerminalSession(string address, string senderId, ILogger? logger = null)
        {
            _address = address;
            _senderId = senderId;
            _logger = logger ?? NullLogger.Instance;
        }

        // The connection ended (the shell's own `exit`, or the runtime closing it).
        public event Action<Exception?>? Ended;

        // Connects and signs in, following redirects to the runtime hosting the session's shell. Returns the refusal
        // reason (§4.1) on failure, or null on success. `command` runs a batch command line instead of an interactive
        // session (§4.3).
        public async Task<string?> ConnectAsync(string username, string password, string? command, CancellationToken ct = default)
        {
            if (_socket != null) throw new InvalidOperationException("already connected");

            for (int redirects = 0; redirects < 5; redirects++)
            {
                var (host, port) = ParseAddress(_address);
                var socket = new SecureClientSideSocket(host, port, host, "", null, NullLogger<SecureClientSideSocket>.Instance, $"WebTerminal session to {_address}");
                await socket.ConnectAsync(5, ct).ConfigureAwait(false);

                await socket.Send(MessagePackSerializer.Serialize<RuntimeMessage>(new TerminalConnectRequest
                {
                    MessageId = Guid.NewGuid(), SenderId = _senderId, Username = username, Password = password, Command = command,
                })).ConfigureAwait(false);

                var response = MessagePackSerializer.Deserialize<RuntimeMessage>(socket.Receive());
                if (response is TerminalRedirectResponse redirect)
                {
                    _address = redirect.RedirectAddress;
                    await socket.StopAsync().ConfigureAwait(false);
                    continue;
                }
                if (response is not TerminalConnectResponse res)
                {
                    await socket.StopAsync().ConfigureAwait(false);
                    return $"unexpected response {response.GetType().Name}";
                }
                if (!res.Accepted)
                {
                    await socket.StopAsync().ConfigureAwait(false);
                    return res.Reason;
                }

                _socket = socket;
                socket.OnEnded += ex => { _socket = null; Ended?.Invoke(ex); };
                _ = socket.ListenRaw(OnRawOutput);   // buffering starts now, before any consumer attaches
                return null;
            }
            return "too many redirects";
        }

        // Streams the shell's raw UTF-8 output to `onOutput`: the buffered scrollback first, then new output, until
        // the returned handle is disposed.
        public IDisposable AttachOutput(Action<string> onOutput)
        {
            var id = Guid.NewGuid();
            lock (_outputLock)
            {
                if (_buffer.Length > 0) onOutput(_buffer.ToString());
                _subscribers[id] = onOutput;
            }
            return new Detacher(this, id);
        }

        private void OnRawOutput(byte[] payload)
        {
            var text = Encoding.UTF8.GetString(payload);
            lock (_outputLock)
            {
                _buffer.Append(text);
                if (_buffer.Length > OutputBufferCap) _buffer.Remove(0, _buffer.Length - OutputBufferCap);
                foreach (var subscriber in _subscribers.Values)
                {
                    try { subscriber(text); }
                    catch (Exception ex) { _logger.LogDebug(ex, "terminal output subscriber threw"); }
                }
            }
        }

        private void Detach(Guid id) { lock (_outputLock) _subscribers.Remove(id); }

        // Sends one command line (§4.1: the line's UTF-8, no newline, one write per line).
        public Task SendLineAsync(string line) => SendRawAsync(Encoding.UTF8.GetBytes(line));

        // Ctrl+C: interrupts the foreground job. Sent alone (§4.1).
        public Task InterruptAsync() => SendRawAsync(Interrupt);

        private Task SendRawAsync(byte[] bytes) =>
            (_socket ?? throw new InvalidOperationException("not connected")).SendRaw(bytes);

        public async ValueTask DisposeAsync()
        {
            var socket = _socket;
            _socket = null;
            if (socket != null) await socket.StopAsync().ConfigureAwait(false);
        }

        private sealed class Detacher : IDisposable
        {
            private readonly TerminalSession _session;
            private readonly Guid _id;
            public Detacher(TerminalSession session, Guid id) { _session = session; _id = id; }
            public void Dispose() => _session.Detach(_id);
        }

        private static (string Host, int Port) ParseAddress(string address)
        {
            var parts = address.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var port)) throw new ArgumentException($"invalid address '{address}' (host:port)");
            return (parts[0], port);
        }
    }
}
