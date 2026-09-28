using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Runtime;

namespace OneOS_Core
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: oneos <command> [options]");
                Console.WriteLine("Commands:");
                Console.WriteLine("  start [--mount <path>]    Start the OneOS Runtime daemon");
                Console.WriteLine("        [--profile [<dir>]] [--profile-interval <seconds>]");
                Console.WriteLine("                            Write resource, pipe and latency profiles (JSONL) to <dir> (default <mount>/profile), every interval (default 1 s)");
                Console.WriteLine("        [--no-bypass]       Carry every graph edge through the sidecar (per-message metrics and end-to-end latency on all edges)");
                Console.WriteLine("  config [--mount <path>]   Start the interactive configuration session (including the certificate and peers)");
                Console.WriteLine("  peer list|add|remove|link Register peers and their certificates without the interactive session");
                Console.WriteLine("                            (e.g. `oneos peer link ~/.oneos/test0 ~/.oneos/test1 ~/.oneos/test2`; `oneos peer` for usage)");
                Console.WriteLine("  connect [address]        Start the terminal client (default: 127.0.0.1:5000)");
                Console.WriteLine("  connect [address] -c <command> [--user name]");
                Console.WriteLine("                            Run a command line (';'-separated) in a new session without an interactive terminal:");
                Console.WriteLine("                            prints its output and returns once its foreground agents have ended (password: ONEOS_PASSWORD or prompt)");
                Console.WriteLine("  cp [-r] <local> [oneos:]<remote> [--address host:port] [--user name]   (upload)");
                Console.WriteLine("  cp [-r] oneos:<remote> <local> [--address host:port] [--user name]     (download)");
                Console.WriteLine("                            Copy local files into the cluster's file system (password: ONEOS_PASSWORD or prompt)");
                Console.WriteLine("  eval <file_path>         Parse and execute a script file using NodeInterpreter");
                Console.WriteLine("  sim <file>... [--spawn \"G(args)\"] [--hosts f] [--dift f] [--sections a,b] [--ir] [--json]");
                Console.WriteLine("                            Compile DSL files and spawn a graph on a simulated cluster");
                Console.WriteLine("  test [unit|live|all] [--filter <expr>] [--verbose] [--repo <path>]");
                Console.WriteLine("                            Run the unit tests and/or the live cluster test (default: all) from the source tree");
                return;
            }

            string command = args[0].ToLower();

            if (command == "cp")
            {
                Environment.ExitCode = await RunCopyCommand(args.Skip(1).ToArray());
                return;
            }
            if (command == "connect")
            {
                Environment.ExitCode = await RunTerminalCommand(args.Skip(1).ToArray());
                return;
            }
            if (command == "peer")
            {
                Environment.ExitCode = await PeerSetup.RunAsync(args.Skip(1).ToArray());
                return;
            }
            if (command == "test")
            {
                Environment.ExitCode = await RunTestCommand(args.Skip(1).ToArray());
                return;
            }

            // Parse options
            string mountPath = Configuration.DefaultMountPath;
            bool profile = false;
            string? profileDir = null;
            double profileInterval = 1.0;
            bool noBypass = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--mount" && i + 1 < args.Length)
                {
                    mountPath = args[i + 1];
                    i++;
                }
                else if (args[i] == "--no-bypass") noBypass = true;
                else if (args[i] == "--profile")
                {
                    profile = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) profileDir = args[++i];
                }
                else if (args[i] == "--profile-interval" && i + 1 < args.Length
                    && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    profileInterval = seconds;
                    i++;
                }
            }

            if (command == "start")
            {
                await RunStartCommand(mountPath, profile
                    ? new OneOS.Runtime.Monitoring.ProfileOptions(profileDir ?? Path.Combine(mountPath, "profile"), TimeSpan.FromSeconds(profileInterval))
                    : null, noBypass);
            }
            else if (command == "config")
            {
                RunConfigCommand(mountPath);
            }
            else if (command == "eval")
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Error: Missing file path for eval command.");
                    return;
                }
                RunEvalCommand(args[1]);
            }
            else if (command == "sim")
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Error: Missing file path for sim command.");
                    return;
                }
                await RunSimCommand(args[1..]);
            }
            else
            {
                Console.WriteLine($"Unknown command: {command}");
            }
        }

        static async Task RunStartCommand(string mountPath, OneOS.Runtime.Monitoring.ProfileOptions? profile = null, bool noBypass = false)
        {
            Configuration? config = Configuration.Load(mountPath);

            if (config == null)
            {
                Console.WriteLine($"Error: Configuration not found at {mountPath}. Please run 'oneos config' first.");
                return;
            }

            var loggerFactory = LoggerFactory.Create(builder => 
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });

            var runtime = new Runtime(config, loggerFactory) { Profile = profile };
            if (noBypass) runtime.GraphManager.Options = runtime.GraphManager.Options with { BypassEdges = false };

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            
            using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGTERM, 
                ctx => 
                {
                    ctx.Cancel = true;
                    cts.Cancel();
                });

            await runtime.StartAsync(cts.Token);

            Console.WriteLine("OneOS Runtime is running. Press Ctrl+C to exit.");

            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (TaskCanceledException)
            {
                // Graceful shutdown requested
            }

            await runtime.StopAsync();
        }

        static void RunConfigCommand(string mountPath)
        {
            Console.WriteLine("Starting interactive configuration setup...");
            Console.WriteLine($"Mount path: {mountPath}");

            Configuration config = Configuration.Load(mountPath) ?? new Configuration();
            config.MountPath = mountPath;

            Console.Write($"Domain [{config.Domain}]: ");
            string? domain = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(domain)) config.Domain = domain;

            Console.Write($"ID [{config.ID}]: ");
            string? id = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(id)) config.ID = id;

            Console.Write($"Host [{config.Host}]: ");
            string? host = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(host)) config.Host = host;

            Console.Write($"Port [{config.Port}]: ");
            string? portStr = Console.ReadLine();
            if (int.TryParse(portStr, out int port)) config.Port = port;

            Console.Write($"Storage Path [{config.StoragePath}]: ");
            string? storagePath = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(storagePath)) config.StoragePath = storagePath;

            Console.Write($"Log Path [{config.LogPath}]: ");
            string? logPath = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(logPath)) config.LogPath = logPath;

            Console.Write($"Loopback address for user sockets [{config.LoopbackAddress}]: ");
            string? loopback = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(loopback)) config.LoopbackAddress = loopback.Trim();

            Console.Write($"Proxy addresses, comma-separated [{(config.ProxyAddresses is { } pa ? string.Join(",", pa) : "all public IPv4 addresses and 127.0.0.1")}]: ");
            string? proxies = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(proxies)) config.ProxyAddresses = proxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            // The runtime's certificate (created if missing) and its peers, with the certificate each must present.
            PeerSetup.Configure(config);

            config.Save(mountPath);
            Console.WriteLine($"Configuration saved to {Path.Combine(mountPath, "config.json")}");

            // The JavaScript environment for JavaScript agents (plan step 7.1).
            Console.WriteLine($"Installing the JavaScript environment in {config.TempPath} ...");
            var (ok, output) = OneOS.Runtime.Driver.JavaScriptEnvironmentInstaller.InstallAsync(config.TempPath).GetAwaiter().GetResult();
            Console.WriteLine(ok ? "JavaScript environment installed." : $"Error: installing the JavaScript environment failed:\n{output}");
        }

        // `test [unit|live|all] [--filter <expr>] [--verbose] [--repo <path>]`: runs the unit tests (src/OneOS.Tests,
        // `--filter` passed to `dotnet test`) and the live cluster test (tools/LiveClusterTest: three runtimes in one
        // process on copies of the configs, ports shifted by ONEOS_LIVE_PORT_OFFSET, default 20000). The source tree
        // is found above the current directory or this executable. The live test drives this very CLI (ONEOS_CLI), so
        // OneOS-Core isn't rebuilt underneath a running `oneos`. Its full output goes to a log file; only the
        // scenario lines are shown, unless --verbose.
        static async Task<int> RunTestCommand(string[] args)
        {
            string which = "all";
            string? filter = null, repo = null;
            bool verbose = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is "unit" or "live" or "all") which = args[i];
                else if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
                else if (args[i] == "--repo" && i + 1 < args.Length) repo = args[++i];
                else if (args[i] is "-v" or "--verbose") verbose = true;
                else
                {
                    Console.WriteLine("Usage: oneos test [unit|live|all] [--filter <expr>] [--verbose] [--repo <path>]");
                    return 2;
                }
            }
            if (filter != null && which == "live")
            {
                Console.WriteLine("oneos test: --filter applies to the unit tests only");
                return 2;
            }
            repo ??= FindSourceTree(Directory.GetCurrentDirectory()) ?? FindSourceTree(AppContext.BaseDirectory);
            if (repo == null || !File.Exists(Path.Combine(repo, "src/OneOS.Tests/OneOS.Tests.csproj")))
            {
                Console.WriteLine("oneos test: can't find the OneOS source tree (src/OneOS.Tests); run it from the repository or pass --repo <path>");
                return 2;
            }

            var results = new List<(string Name, bool Passed)>();
            if (which is "unit" or "all")
            {
                Console.WriteLine("== Unit tests (src/OneOS.Tests)");
                var arguments = new List<string> { "test", Path.Combine(repo, "src/OneOS.Tests") };
                if (filter != null) arguments.AddRange(new[] { "--filter", filter });
                results.Add(("unit tests", await RunDotnetAsync(repo, arguments, null, null) == 0));
            }
            if (which is "live" or "all")
            {
                var log = Path.Combine(Path.GetTempPath(), $"oneos-live-test-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                Console.WriteLine($"== Live cluster test (tools/LiveClusterTest; full log: {log})");
                var cli = typeof(Program).Assembly.Location;
                int code;
                await using (var writer = new StreamWriter(log) { AutoFlush = true })
                {
                    void Line(string line)
                    {
                        lock (writer) writer.WriteLine(line);
                        if (verbose || IsScenarioLine(line)) Console.WriteLine(line);
                    }
                    code = await RunDotnetAsync(repo, new[] { "run", "--project", Path.Combine(repo, "tools/LiveClusterTest") },
                        new Dictionary<string, string> { ["ONEOS_CLI"] = cli }, Line);
                }
                results.Add(("live cluster test", code == 0));
            }

            Console.WriteLine();
            foreach (var (name, passed) in results) Console.WriteLine($"{(passed ? "PASSED" : "FAILED")}  {name}");
            return results.All(r => r.Passed) ? 0 : 1;
        }

        static string? FindSourceTree(string start)
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "src/OneOS.Tests/OneOS.Tests.csproj"))) return dir.FullName;
            return null;
        }

        // The live test's own lines ("UPLOAD: …", "LIVE TEST PASSED", indented details), not the runtimes' logs.
        static bool IsScenarioLine(string line) =>
            line.StartsWith("  ") || System.Text.RegularExpressions.Regex.IsMatch(line, @"^[A-Z][A-Z0-9]{2,}( [A-Z0-9/-]+)*\b");

        // Runs `dotnet <arguments>` in the source tree: its output goes to onLine, or straight to this console.
        static async Task<int> RunDotnetAsync(string repo, IEnumerable<string> arguments, IDictionary<string, string>? environment, Action<string>? onLine)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet") { WorkingDirectory = repo, UseShellExecute = false };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            if (environment != null) foreach (var (k, v) in environment) psi.Environment[k] = v;
            psi.RedirectStandardOutput = psi.RedirectStandardError = onLine != null;
            using var process = System.Diagnostics.Process.Start(psi)!;
            if (onLine != null)
            {
                process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            await process.WaitForExitAsync();
            return process.ExitCode;
        }

        // `cp [-r] <source> <destination> [--address host:port] [--user name]`: copies between local disk and the
        // cluster's file system. A cluster path is marked "oneos:"; `oneos:<remote> <local>` downloads, and
        // `<local> [oneos:]<remote>` uploads (an unmarked destination is a cluster path).
        static async Task<int> RunCopyCommand(string[] args)
        {
            bool recursive = false;
            string address = "127.0.0.1:5000";
            string? user = null;
            var paths = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is "-r" or "-R" or "--recursive") recursive = true;
                else if (args[i] == "--address" && i + 1 < args.Length) address = args[++i];
                else if (args[i] == "--user" && i + 1 < args.Length) user = args[++i];
                else paths.Add(args[i]);
            }
            const string Cluster = "oneos:";
            if (paths.Count != 2 || (paths[0].StartsWith(Cluster) && paths[1].StartsWith(Cluster)))
            {
                Console.WriteLine("Usage: oneos cp [-r] <local> [oneos:]<remote> [--address host:port] [--user name]");
                Console.WriteLine("       oneos cp [-r] oneos:<remote> <local> [--address host:port] [--user name]");
                return 2;
            }
            bool download = paths[0].StartsWith(Cluster);
            if (user == null)
            {
                Console.Write("Username: ");
                user = Console.ReadLine() ?? "";
            }
            var password = Environment.GetEnvironmentVariable("ONEOS_PASSWORD");
            if (password == null)
            {
                Console.Write("Password: ");
                password = ReadPassword();
            }
            try
            {
                var result = download
                    ? await new OneOS.Client.FileDownloader(address, user, password, Console.WriteLine).CopyAsync(paths[0][Cluster.Length..], paths[1], recursive)
                    : await new OneOS.Client.FileUploader(address, user, password, Console.WriteLine)
                        .CopyAsync(paths[0], paths[1].StartsWith(Cluster) ? paths[1][Cluster.Length..] : paths[1], recursive);
                foreach (var e in result.Errors) Console.WriteLine($"error: {e}");
                Console.WriteLine($"{result.Files} file(s), {result.Directories} director{(result.Directories == 1 ? "y" : "ies")}, {result.Bytes} bytes copied"
                    + (result.Errors.Count > 0 ? $"; {result.Errors.Count} error(s)" : ""));
                return result.Errors.Count == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"oneos cp: {ex.Message}");
                return 1;
            }
        }

        static string ReadPassword()
        {
            if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
                if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
                else if (key.KeyChar != '\u0000') sb.Append(key.KeyChar);
            }
        }

        // `connect [address]`: an interactive terminal. `connect [address] -c <command> [--user name]`: runs the
        // command line in a new session, like `bash -c`, and exits once its foreground agents have ended.
        static async Task<int> RunTerminalCommand(string[] args)
        {
            string address = "127.0.0.1:5000";
            string? inline = null, user = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-c" && i + 1 < args.Length) inline = args[++i];
                else if (args[i] == "--user" && i + 1 < args.Length) user = args[++i];
                else if (!args[i].StartsWith("-")) address = args[i];
                else
                {
                    Console.Error.WriteLine("Usage: oneos connect [address] [-c <command> [--user name]]");
                    return 2;
                }
            }
            using var cts = new CancellationTokenSource();
            // Let Terminal.cs handle its own CancelKeyPress behavior so Ctrl+C goes to foreground agents
            await using var terminal = new OneOS.Client.Terminal(address);
            if (inline == null)
            {
                await terminal.RunAsync(cts.Token);
                return 0;
            }
            return await terminal.RunCommandAsync(inline, user, user == null ? null : Environment.GetEnvironmentVariable("ONEOS_PASSWORD"), cts.Token);
        }

        static void RunEvalCommand(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Error: File '{filePath}' not found.");
                return;
            }

            try
            {
                string content = File.ReadAllText(filePath);
                var interpreter = new OneOS.Runtime.Language.NodeInterpreter();
                interpreter.LoadNode(content);
                interpreter.Evaluate();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Evaluation error: {ex.Message}");
            }
        }

        // `sim a.osh b.osh [--spawn "foo('x', 'y')"] [--hosts hosts.json] [--dift interfaces.json]
        //      [--sections cluster,summary,nodes,placement,pipes,routing,flows,labels] [--ir] [--json]`
        // Compiles the files as one compilation unit, prints diagnostics, spawns a graph on a simulated
        // cluster and reports the plan. Without --spawn, a lone graph is spawned with placeholder
        // arguments. Without --hosts, the cluster has two hosts per declared label (or four unlabelled
        // hosts), in two zones.
        static async Task RunSimCommand(string[] args)
        {
            var files = new List<string>();
            string? spawn = null, hostsFile = null, diftFile = null;
            bool printIr = false, printJson = false;
            var sections = OneOS.Runtime.Graphs.GraphReport.Sections.All;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--spawn" && i + 1 < args.Length) spawn = args[++i];
                else if (args[i] == "--hosts" && i + 1 < args.Length) hostsFile = args[++i];
                else if (args[i] == "--dift" && i + 1 < args.Length) diftFile = args[++i];
                else if (args[i] == "--sections" && i + 1 < args.Length)
                {
                    sections = OneOS.Runtime.Graphs.GraphReport.Sections.None;
                    foreach (var x in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!Enum.TryParse<OneOS.Runtime.Graphs.GraphReport.Sections>(x, ignoreCase: true, out var s) || int.TryParse(x, out _))
                        {
                            Console.WriteLine($"Error: unknown report section '{x}'. Sections: cluster, summary, nodes, placement, pipes, routing, flows, labels, all.");
                            return;
                        }
                        sections |= s;
                    }
                }
                else if (args[i] == "--ir") printIr = true;
                else if (args[i] == "--json") printJson = true;
                else files.Add(args[i]);
            }

            var runtime = new OneOS.Runtime.SimulatedRuntime();
            if (diftFile != null)
                runtime.SchedulerOptions = runtime.SchedulerOptions with { Analyzer = OneOS.Runtime.Scheduling.InMemoryDift.FromJson(File.ReadAllText(diftFile)) };
            var interpreter = new OneOS.Runtime.Language.AppInterpreter(runtime);
            foreach (var f in files)
            {
                if (!File.Exists(f)) { Console.WriteLine($"Error: File '{f}' not found."); return; }
                interpreter.AddSource(File.ReadAllText(f), f);
            }

            var program = interpreter.Compile();
            foreach (var d in program.Diagnostics) Console.WriteLine(d + "\n");
            foreach (var g in program.Graphs)
                Console.WriteLine($"Compiled graph {g.Name}({string.Join(", ", g.Parameters.Select(p => $"{p.Name}: {p.Type}"))}): {g.Nodes.Count} nodes, {g.Edges.Count} edges, {g.Flows.Count} flows{(g.HasErrors ? " [errors]" : "")}");
            if (program.HasErrors) return;

            if (spawn == null)
            {
                if (program.Graphs.Count != 1)
                {
                    Console.WriteLine("Several graphs: pass --spawn \"Graph(args)\" to choose one.");
                    return;
                }
                var g = program.Graphs[0];
                spawn = $"{g.Name}({string.Join(", ", g.Parameters.Select(Placeholder))})";
                if (g.Parameters.Count > 0)
                    Console.WriteLine($"note: no --spawn given; using placeholder arguments: spawn {spawn}");
            }

            var snapshot = hostsFile != null ? LoadHosts(hostsFile) : DefaultSimCluster(program.Lattice);
            runtime.InjectSnapshot(snapshot);

            var outcome = await interpreter.SpawnAsync("spawn " + spawn);
            foreach (var d in outcome.Diagnostics.Where(d => d.IsError)) Console.WriteLine(d + "\n");
            if (outcome.Handle == null)
            {
                foreach (var d in outcome.SchedulerDiagnostics) Console.WriteLine(d + "\n");
                return;
            }

            Console.Write(OneOS.Runtime.Graphs.GraphReport.Format(outcome.Handle.Info, outcome.Graph, snapshot, sections));
            if (printIr) Console.WriteLine(outcome.Graph!.ToJson());
            if (printJson) Console.WriteLine(outcome.Handle.Info.ToJson());
        }

        // A placeholder spawn argument for a parameter: its name as a string, or a zero value.
        static string Placeholder(OneOS.Runtime.Language.Models.GraphParameterInfo p) => p.Type switch
        {
            "string" => $"'<{p.Name}>'",
            "bool" => "false",
            "f32" or "f64" => "0.0",
            _ => "0",
        };

        static OneOS.Runtime.ClusterSnapshot DefaultSimCluster(OneOS.Runtime.Language.LabelLattice lattice)
        {
            var labels = lattice.Declared ? lattice.Labels.Where((l, i) => !lattice.Synthetic[i]).Cast<string?>().ToList() : new List<string?> { null, null };
            var hosts = new List<OneOS.Runtime.HostRuntimeInfo>();
            foreach (var label in labels)
                for (int i = 1; i <= 2; i++)
                {
                    var name = $"{label ?? "host"}-{hosts.Count(h => h.Label == label) + 1}";
                    hosts.Add(new OneOS.Runtime.HostRuntimeInfo(name, name, i == 1 ? "zone-a" : "zone-b", new List<string>(), label, true,
                        new OneOS.Runtime.ResourceVector(4000, 8L << 30), OneOS.Runtime.ResourceVector.Zero, null, new List<string>(), null, 1));
                }
            return new OneOS.Runtime.ClusterSnapshot(1, hosts, new List<OneOS.Runtime.NetworkQuality>());
        }

        // hosts.json: [{ "name": "h1", "zone": "a", "label": "secret", "cpuMillis": 4000, "memoryMB": 8192,
        //                "executables": ["node", "python"], "idmSupport": ["node"], "alive": true }, ...]
        // idmSupport: the language runtimes whose programs can run with the IDM (DIFT Enabled) on that host.
        static OneOS.Runtime.ClusterSnapshot LoadHosts(string path)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var hosts = new List<OneOS.Runtime.HostRuntimeInfo>();
            foreach (var h in doc.RootElement.EnumerateArray())
            {
                string? Str(string k) => h.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                long Num(string k, long def) => h.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt64() : def;
                var name = Str("name") ?? $"host-{hosts.Count + 1}";
                var exes = h.TryGetProperty("executables", out var e) ? e.EnumerateArray().ToDictionary(x => x.GetString()!, _ => "") : null;
                bool alive = !h.TryGetProperty("alive", out var al) || al.GetBoolean();
                hosts.Add(new OneOS.Runtime.HostRuntimeInfo(name, name, Str("zone"), new List<string>(), Str("label"), alive,
                    new OneOS.Runtime.ResourceVector(Num("cpuMillis", 4000), Num("memoryMB", 8192) << 20), OneOS.Runtime.ResourceVector.Zero,
                    exes, h.TryGetProperty("idmSupport", out var idm) ? idm.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>(),
                    h.TryGetProperty("maxAgents", out var m) ? m.GetInt32() : null, 1));
            }
            return new OneOS.Runtime.ClusterSnapshot(1, hosts, new List<OneOS.Runtime.NetworkQuality>());
        }
    }
}
