using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.Extensions.Logging;
using OneOS.Common;
using OneOS.Runtime;

namespace OneOS.Client
{
    public class Terminal : IAsyncDisposable
    {
        private string _currentAddress;
        private ClientSideSocket? _socket;
        private string URI;
        
        public Terminal(string initialAddress)
        {
            _currentAddress = initialAddress;
            URI = $"terminal/{Guid.NewGuid().ToString("N")}";
        }

        public async Task RunAsync(CancellationToken ct = default)
        {
            if (!await SignInAsync(null, null, null, Console.Out, ct).ConfigureAwait(false)) return;

            // Interactive loop
            _ = Task.Run(() =>
            {
                try
                {
                    _socket!.ListenRaw(payload =>
                    {
                        var text = Encoding.UTF8.GetString(payload);
                        Console.Write(text);
                    });
                }
                catch (Exception)
                {
                    // Ignore disconnects
                }
            });

            using var reg = ct.Register(() =>
            {
                _ = CleanupSocketAsync();
                Environment.Exit(0);
            });

            bool ctrlCPressed = false;
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                ctrlCPressed = true;
                if (_socket != null)
                {
                    var sigint = new byte[] { 3 }; // ASCII ETX / Ctrl+C
                    _ = _socket.SendRaw(sigint);
                }
            };

            while (!ct.IsCancellationRequested)
            {
                var line = Console.ReadLine();
                if (line == null) 
                {
                    if (ctrlCPressed)
                    {
                        ctrlCPressed = false;
                        continue;
                    }
                    break; // EOF
                }
                
                ctrlCPressed = false;
                var payload = Encoding.UTF8.GetBytes(line);
                await _socket!.SendRaw(payload).ConfigureAwait(false);

                if (line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            await CleanupSocketAsync().ConfigureAwait(false);
            Environment.Exit(0);
        }

        // Connects and signs in, following redirects to the runtime hosting the session's shell. Interactive (no
        // command): asks for credentials, again after a refusal. Batch: a refusal ends it. Status goes to `status`.
        private async Task<bool> SignInAsync(string? username, string? password, string? command, System.IO.TextWriter status, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (command == null) status.WriteLine($"Connecting to {_currentAddress}...");

                if (!await TryConnectAsync(status, ct).ConfigureAwait(false))
                {
                    status.WriteLine("Connection failed.");
                    return false;
                }

                if (username == null)
                {
                    status.Write("Username: ");
                    username = Console.ReadLine() ?? "";
                }
                if (password == null)
                {
                    status.Write("Password: ");
                    password = ReadSecret(status);
                }

                var connectReq = new TerminalConnectRequest
                {
                    MessageId = Guid.NewGuid(),
                    SenderId = URI,
                    Username = username,
                    Password = password,
                    Command = command
                };

                var payload = MessagePackSerializer.Serialize<RuntimeMessage>(connectReq);
                await _socket!.Send(payload).ConfigureAwait(false);

                // Wait for response
                var responseFrame = _socket!.Receive();
                var responseMsg = MessagePackSerializer.Deserialize<RuntimeMessage>(responseFrame);

                if (responseMsg is TerminalRedirectResponse redirect)
                {
                    if (command == null) status.WriteLine($"Redirected to runtime at {redirect.RedirectAddress}");
                    _currentAddress = redirect.RedirectAddress;
                    await CleanupSocketAsync().ConfigureAwait(false);
                    continue; // Loop to reconnect with SAME credentials
                }
                else if (responseMsg is TerminalConnectResponse res)
                {
                    if (res.Accepted)
                    {
                        if (command == null) status.WriteLine($"Connected to OneOS Runtime at {_currentAddress}!");
                        return true;
                    }
                    else
                    {
                        status.WriteLine($"Login failed: {res.Reason}");
                        await CleanupSocketAsync().ConfigureAwait(false);
                        if (command != null) return false;
                        username = null;
                        password = null;
                        continue; // Reprompt
                    }
                }
                else
                {
                    status.WriteLine($"Unexpected response type: {responseMsg.GetType().Name}");
                    return false;
                }
            }
            return false;
        }

        // `oneos connect -c <command>`: runs a command line in a new session and prints its output. Nothing is read
        // from stdin; Ctrl+C interrupts the command (a second one gives up waiting). Returns once the runtime
        // closes the connection after the last foreground agent has ended: 0, or 1 if the session couldn't start.
        public async Task<int> RunCommandAsync(string command, string? username, string? password, CancellationToken ct = default)
        {
            if (!await SignInAsync(username, password, command, Console.Error, ct).ConfigureAwait(false)) return 1;

            var output = Console.OpenStandardOutput();
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _socket!.OnEnded += _ => ended.TrySetResult();
            _ = _socket.ListenRaw(payload => { output.Write(payload); output.Flush(); });

            int interrupts = 0;
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                if (Interlocked.Increment(ref interrupts) > 1) { ended.TrySetResult(); return; }
                e.Cancel = true;
                _ = _socket?.SendRaw(new byte[] { 3 });
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                await ended.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally
            {
                Console.CancelKeyPress -= onCancel;
                await CleanupSocketAsync().ConfigureAwait(false);
            }
            return interrupts > 0 ? 130 : 0;
        }

        private async Task<bool> TryConnectAsync(System.IO.TextWriter status, CancellationToken ct)
        {
            var parts = _currentAddress.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
            {
                status.WriteLine($"Invalid address format: {_currentAddress}");
                return false;
            }

            var host = parts[0];

            try
            {
                using var factory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
                _socket = new SecureClientSideSocket(host, port, host, "", null, factory.CreateLogger<SecureClientSideSocket>(), $"Terminal client to {_currentAddress}");
                await _socket.ConnectAsync(5, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                status.WriteLine($"Error connecting to {_currentAddress}: {ex.Message}");
                return false;
            }
        }

        private async Task CleanupSocketAsync()
        {
            if (_socket != null)
            {
                await _socket.StopAsync().ConfigureAwait(false);
                _socket = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await CleanupSocketAsync().ConfigureAwait(false);
        }

        private string ReadSecret(System.IO.TextWriter status)
        {
            if (Console.IsInputRedirected)
            {
                return Console.ReadLine() ?? string.Empty;
            }

            var sb = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    status.WriteLine();
                    break;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Remove(sb.Length - 1, 1);
                    }
                }
                else if (key.KeyChar != '\u0000')
                {
                    sb.Append(key.KeyChar);
                }
            }
            return sb.ToString();
        }
    }
}
