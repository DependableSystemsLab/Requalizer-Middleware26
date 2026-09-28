// Runs one of the Requalizer paper's demo applications end to end on an in-process OneOS cluster built from the
// demo's host inventory (the paper's 8-host cluster), profiles it, and writes the profiles, the deployment plan
// and a summary to an output folder.
//
//   dotnet run --project src/DemoRunner -- --app AAL|FD|SPG --mode baseline|ldift|requalizer --output DIR
//       [--duration SECONDS]   AAL: how long the camera and the wearable stream (default 30)
//       [--records N]          FD: transactions (default 2000); SPG: readings (default 8000)
//       [--port-base PORT]     first runtime port (default 31000; one port per host, plus Raft at +100)
//       [--profile-interval S] profile sampling interval in seconds (default 1)
//       [--log LEVEL]          runtime log level (default Warning)
//       [--keep]               keep the temporary cluster folder
//
// Modes (the paper's systems under test, §5.2):
//   requalizer  (or `aware`) OneOS as designed: label-segregated lanes, label-aware placement, DIFT only where needed,
//               delivery checks enforced.
//   ldift       layered DIFT: DIFT enabled in every component, the scheduler unaware of labels (no lanes,
//               placement ignores host labels), internal interfaces not guarded (failed checks are counted,
//               and the message is delivered anyway).
//   baseline    no DIFT anywhere, the same unaware scheduler and unguarded internal interfaces.
// All modes keep the source labellers, so every message carries a label and violations can be measured: a
// message received on a runtime whose host label is below the message's label.
//
// Output: DIR/run.json (parameters), DIR/plan.json (agents, hosts, DIFT modes, lanes, pipes), DIR/profiles/
// (resources.jsonl, pipes.jsonl, latency.jsonl, messages.jsonl per runtime; see OneOS.Runtime.Monitoring.Profiler),
// DIR/summary.json and DIR/summary.txt (end-to-end latency, sink throughput, violations, DIFT and lanes).
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OneOS.Runtime;
using OneOS.Runtime.Language;
using OneOS.Runtime.Scheduling;

var opts = new Dictionary<string, string>();
var flags = new HashSet<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--keep") flags.Add("keep");
    else if (args[i].StartsWith("--") && i + 1 < args.Length) opts[args[i][2..]] = args[++i];
    else { Usage($"unexpected argument '{args[i]}'"); return 2; }
}
void Usage(string? error = null)
{
    if (error != null) Console.Error.WriteLine($"demo: {error}");
    Console.Error.WriteLine("usage: DemoRunner --app AAL|FD|SPG --mode baseline|ldift|requalizer --output DIR [--duration S] [--records N] [--port-base P] [--profile-interval S] [--log LEVEL] [--keep]");
}
string app = opts.GetValueOrDefault("app", "").ToUpperInvariant(), mode = opts.GetValueOrDefault("mode", "").ToLowerInvariant();
if (mode == "aware") mode = "requalizer";   // cp-scheduler.py's name for it
if (app is not ("AAL" or "FD" or "SPG") || mode is not ("baseline" or "ldift" or "requalizer") || !opts.TryGetValue("output", out var outputArg))
{
    Usage("--app, --mode and --output are required");
    return 2;
}
int duration = int.Parse(opts.GetValueOrDefault("duration", "30"));
int records = int.Parse(opts.GetValueOrDefault("records", app == "FD" ? "2000" : "8000"));
int portBase = int.Parse(opts.GetValueOrDefault("port-base", "31000"));
double profileInterval = double.Parse(opts.GetValueOrDefault("profile-interval", "1"), System.Globalization.CultureInfo.InvariantCulture);
var logLevel = Enum.TryParse<LogLevel>(opts.GetValueOrDefault("log", "Warning"), true, out var lvl) ? lvl : LogLevel.Warning;
var output = Path.GetFullPath(outputArg);

// --- The application ---------------------------------------------------------------------------------------
var repo = FindRepository(AppContext.BaseDirectory) ?? FindRepository(Directory.GetCurrentDirectory())
    ?? throw new InvalidOperationException("can't find the Requalizer repository (src/examples/ and src/OneOS)");
var appDir = Path.Combine(repo, "src", "examples", app switch { "AAL" => "AssistedLiving", "FD" => "FraudDetection", _ => "SmartPowerGrid" });
var (graphFile, hostsFile, graphName) = app switch
{
    "AAL" => ("living_demo.osh", "living_demo.json", "AmbientAssistedLiving"),
    "FD" => ("fraud_demo.osh", "fraud_demo.json", "DSP_frauddetection"),
    _ => ("grid_demo.osh", "grid_demo.json", "DSP_smartgrid"),
};
var hosts = JsonDocument.Parse(File.ReadAllText(Path.Combine(appDir, "hosts", hostsFile))).RootElement.EnumerateArray()
    .Select(h => (Name: h.GetProperty("name").GetString()!, Label: h.GetProperty("label").GetString()!,
        Zone: h.TryGetProperty("zone", out var z) ? z.GetString() : null,
        CpuMillis: h.GetProperty("cpuMillis").GetInt64(), MemoryMB: h.GetProperty("memoryMB").GetInt64()))
    .ToList();

var hostLabel = hosts.ToDictionary(h => h.Name, h => h.Label);
GraphInstanceInfo? instance = null;
string gid = "";
DateTime started = DateTime.UtcNow, ended = DateTime.UtcNow;

var tmp = Path.Combine(Path.GetTempPath(), "oneos-demo-" + Guid.NewGuid().ToString("N")[..8]);
var clusterDir = Path.Combine(tmp, "cluster");
var work = Path.Combine(tmp, "work");
Directory.CreateDirectory(work);
Directory.CreateDirectory(output);
Log($"{app} / {mode}: {hosts.Count} hosts, cluster in {tmp}, output in {output}");

// The sinks' writer: writes each JSON record it receives to the output file and a progress count next to it.
File.WriteAllText(Path.Combine(work, "writer.js"), """
    const fs = require('fs');
    const out = fs.createWriteStream(process.argv[2]);
    let records = 0;
    process.stdin.json.on('data', r => { records++; out.write(JSON.stringify(r) + '\n'); });
    const progress = () => fs.writeFile(process.argv[2] + '.progress', JSON.stringify({ records: records }), 'utf8', () => {});
    setInterval(progress, 500);
    """);

// The graph program: script names resolved to the example's folder, the sinks' writer to the one above.
var source = File.ReadAllText(Path.Combine(appDir, graphFile))
    .Replace("'/home/root/writer.js'", $"'{work}/writer.js'");
source = Regex.Replace(source, @"process\('node', '([^'/][^']*)'", m => $"process('node', '{appDir}/{m.Groups[1].Value}'");
var compiled = AppCompiler.CompileSource(source);
if (compiled.HasErrors) { Console.Error.WriteLine(string.Join("\n", compiled.Diagnostics)); return 1; }
var graph = compiled.Graph(graphName) ?? throw new InvalidOperationException($"no graph {graphName} in {graphFile}");

// --- The cluster ---------------------------------------------------------------------------------------------
var certHashes = new Dictionary<string, string>();
for (int i = 0; i < hosts.Count; i++)
{
    var dir = Path.Combine(clusterDir, hosts[i].Name);
    Directory.CreateDirectory(dir);
    CertificateHelper.GenerateSelfSignedCertificate(Path.Combine(dir, "cert.pfx"), $"{hosts[i].Name}.demo");
    certHashes[hosts[i].Name] = CertificateHelper.GetCertHashFromFile(Path.Combine(dir, "cert.pfx"));
}
for (int i = 0; i < hosts.Count; i++)
{
    var h = hosts[i];
    var cfg = new Configuration
    {
        Domain = "demo", ID = h.Name, Host = "127.0.0.1", Port = portBase + i, Label = h.Label, Zone = h.Zone,
        Cores = new[] { (int)Math.Max(1, h.CpuMillis / 1000), 2000 }, Memory = (int)h.MemoryMB,
        // Each runtime of this one-machine cluster has its own loopback and proxy address (cluster-wide sockets).
        LoopbackAddress = $"127.124.126.{i + 1}", ProxyAddresses = new List<string> { $"127.0.2.{i + 1}" },
        Peers = hosts.Select((p, j) => (p, j)).Where(x => x.j != i).ToDictionary(x => x.p.Name,
            x => new PeerInfo { ID = x.p.Name, Host = "127.0.0.1", Port = portBase + x.j, CertificateHash = certHashes[x.p.Name] }),
    };
    cfg.Save(Path.Combine(clusterDir, h.Name));
}
// The JavaScript environment's npm dependencies, installed once into a cache (what `oneos config` installs).
var jsCache = Path.Combine(Path.GetTempPath(), "oneos-live-js-cache");
if (!OneOS.Runtime.Driver.JavaScriptEnvironmentInstaller.DependenciesInstalled(jsCache))
{
    Log("installing the JavaScript environment's dependencies (once) ...");
    var (ok, npm) = await OneOS.Runtime.Driver.JavaScriptEnvironmentInstaller.InstallDependenciesAsync(jsCache);
    if (!ok) { Console.Error.WriteLine("npm install failed:\n" + npm); return 1; }
}
foreach (var h in hosts) CopyDirectory(Path.Combine(jsCache, "node_modules"), Path.Combine(clusterDir, h.Name, "temp", "node_modules"));

using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; }).SetMinimumLevel(logLevel));
using var cts = new CancellationTokenSource();
var runtimes = new List<Runtime>();
var profiles = Path.Combine(tmp, "profiles");
foreach (var h in hosts)
{
    var rt = new Runtime(Configuration.Load(Path.Combine(clusterDir, h.Name))!, loggers)
        { Profile = new OneOS.Runtime.Monitoring.ProfileOptions(profiles, TimeSpan.FromSeconds(profileInterval)) };
    runtimes.Add(rt);
    _ = rt.StartAsync(cts.Token);
}
var rt0 = runtimes[0];
if (!await Until(() => runtimes.All(r => r.TakeSnapshot().Hosts.Count == hosts.Count && r.TakeSnapshot().Hosts.All(x => x.Alive)), 90, "peer links"))
    return await Finish(1);
// A replicated write proves a leader and a working log.
if (!await Write(new SetUserAction { Username = "demo", Password = "" })) { Log("no Raft leader"); return await Finish(1); }
Log("cluster ready");

// --- Input data ----------------------------------------------------------------------------------------------
var graphArgs = app switch
{
    "AAL" => new object?[] { $"fps=15;seconds={duration};identified=0.5;bytes=4096", $"hz=2;seconds={duration}", "doctor-model.bin" },
    "FD" => new object?[] { "/data/cc.dat", "model.txt", "/data/fd.out" },
    _ => new object?[] { "/data/grid.csv", "/data/grid" },
};
if (app == "FD")
    await rt0.FileSystemManager.WriteFileAsync("/data/cc.dat", "/", System.Text.Encoding.UTF8.GetBytes(string.Join("\n", File.ReadLines(Path.Combine(appDir, "credit-card.dat")).Take(records)) + "\n"));
if (app == "SPG")
    await rt0.FileSystemManager.WriteFileAsync("/data/grid.csv", "/", System.Text.Encoding.UTF8.GetBytes(string.Join("\n", File.ReadLines(Path.Combine(appDir, "smart-grid.csv")).Take(records)) + "\n"));

// --- Deploy --------------------------------------------------------------------------------------------------
var baseOptions = rt0.GraphManager.Options with { StartupTimeout = TimeSpan.FromSeconds(90) };
if (app == "AAL")
{
    var dift = OneOS.Runtime.Scheduling.InMemoryDift.FromJson(File.ReadAllText(Path.Combine(appDir, "living_demo.dift.json")));
    baseOptions = baseOptions with { Analyzer = dift, DiftPolicy = dift };
}
var modeOptions = mode switch
{
    "ldift" => baseOptions with { LaneMode = LaneMode.Off, LabelAwarePlacement = false, DiftOverride = DiftMode.Enabled, EnforceDeliveryChecks = false },
    "baseline" => baseOptions with { LaneMode = LaneMode.Off, LabelAwarePlacement = false, DiftOverride = DiftMode.Disabled, EnforceDeliveryChecks = false },
    _ => baseOptions,
};
// On every runtime, not just the spawner: the failure and scaling controller runs on the Raft leader and re-plans
// with that runtime's options (a failover under default options would turn the unaware modes into requalizer).
foreach (var r in runtimes) r.GraphManager.Options = modeOptions;
started = DateTime.UtcNow;
try { instance = await rt0.GraphManager.SpawnAsync(graph, graphArgs, cts.Token); }
catch (SchedulingException ex) { Log($"spawn failed: {ex.Message}"); return await Finish(1); }
gid = instance.GraphInstanceId;
Log($"spawned {gid}: {instance.Agents.Count(a => a.Role == AgentRole.Primary)} agents on {instance.Agents.Where(a => a.HostId != null).Select(a => a.HostId).Distinct().Count()} hosts");
WriteJson("plan.json", new
{
    graph = graphName, instance = gid, mode,
    agents = instance.Agents.Select(a => new
    {
        id = a.AgentId, node = a.NodeName, role = a.Role.ToString(), lane = a.LaneLabel, host = a.HostId,
        hostLabel = a.HostId != null ? hostLabel.GetValueOrDefault(a.HostId) : null,
        dift = a.Dift.ToString(), diftRule = a.DiftRationale, componentClass = a.Class.ToString(), placementLabel = a.PlacementLabel,
    }),
    pipes = instance.Pipes.Select(p => new
    {
        id = p.PipeId, edge = p.EdgeName, from = p.SourceAgentId, to = p.DestinationAgentId, routing = p.Routing.ToString(),
        allowed = p.Allowed, elided = p.Elided.ToString(), bypass = p.Bypass,
    }),
    relaxed = instance.Plan.RelaxedConstraints,
    warnings = instance.Plan.Warnings.Select(w => w.ToString()),
});

// --- Run -----------------------------------------------------------------------------------------------------
if (app == "AAL")
{
    // The camera and the wearable stream for `duration` seconds; then the pipeline drains.
    await Task.Delay(TimeSpan.FromSeconds(duration + 5));
}
else
{
    // Until the graph goes quiet: no message received on any of its pipes for 5 s (or 5 minutes in all). The
    // sinks' output can't tell: FD alerts on outliers only, so its sink may see nothing for long stretches.
    long Delivered() => runtimes.SelectMany(r => r.Metrics.Pipes)
        .Where(m => m.Meta.Group == gid && m.End == OneOS.Runtime.Monitoring.PipeEnd.In).Sum(m => m.TotalMessages);
    long last = -1; int quietFor = 0;
    for (int s = 0; s < 300 && quietFor < 5; s++)
    {
        await Task.Delay(1000);
        long now = Delivered();
        if (now > 0 && now == last) quietFor++; else quietFor = 0;
        last = now;
    }
    Log($"pipe traffic: {Math.Max(last, 0)} messages delivered; sink output: {Math.Max(await Progress(app == "FD" ? "/data/fd.out.progress" : "/data/grid.outlier.out.progress"), 0)} records");
}
ended = DateTime.UtcNow;
await Task.Delay(TimeSpan.FromSeconds(profileInterval * 2));   // a last profile window
await rt0.GraphManager.StopAsync(gid, cts.Token);
return await Finish(0);

// --- Helpers -------------------------------------------------------------------------------------------------
async Task<int> Finish(int code)
{
    foreach (var r in runtimes) { try { await r.StopAsync(); } catch (Exception) { } }   // flushes the profilers
    cts.Cancel();
    if (Directory.Exists(profiles))
    {
        var dest = Path.Combine(output, "profiles");
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        CopyDirectory(profiles, dest);
    }
    WriteJson("run.json", new { app, mode, duration, records, portBase, profileInterval, hosts, tmp, exitCode = code });
    if (code == 0 && instance != null) Summarize(instance);
    if (!flags.Contains("keep")) { try { Directory.Delete(tmp, true); } catch (Exception) { } }
    Log(code == 0 ? $"done: {output}" : $"failed ({code}); what exists is in {output}");
    return code;
}

void Summarize(GraphInstanceInfo instance)
{
    var L = instance.Lattice;
    var dir = Path.Combine(output, "profiles");
    IEnumerable<JsonElement> Records(string file) => Directory.GetDirectories(dir).SelectMany(d =>
        File.Exists(Path.Combine(d, file)) ? File.ReadLines(Path.Combine(d, file)).Select(l => JsonDocument.Parse(l).RootElement) : Enumerable.Empty<JsonElement>());
    var pipes = Records("pipes.jsonl").Where(r => r.TryGetProperty("group", out var g) && g.GetString() == gid).ToList();
    var latency = Records("latency.jsonl").Where(r => r.GetProperty("graph").GetString() == gid).ToList();
    var resources = Records("resources.jsonl").ToList();

    // Violations: labelled messages received (pipe "in" ends) on a runtime whose host label is below their label.
    long labelled = 0, violating = 0;
    var perEdge = new Dictionary<string, (long Messages, long Violations)>();
    foreach (var r in pipes.Where(r => r.GetProperty("end").GetString() == "in" && r.TryGetProperty("labels", out _)))
    {
        var runtime = r.GetProperty("runtime").GetString()!;
        var edge = r.TryGetProperty("edge", out var e) ? e.GetString() ?? "?" : "?";
        foreach (var lab in r.GetProperty("labels").EnumerateObject())
        {
            long n = lab.Value.GetInt64();
            bool bad = L.Contains(lab.Name) && L.Contains(hostLabel[runtime]) && !L.Leq(lab.Name, hostLabel[runtime]);
            labelled += n;
            if (bad) violating += n;
            var (m, v) = perEdge.GetValueOrDefault(edge);
            perEdge[edge] = (m + n, v + (bad ? n : 0));
        }
    }
    int bypassed = instance.Pipes.Count(p => p.Bypass);

    // End-to-end latency per source→sink, over the profile windows: n-weighted mean, n-weighted median of the
    // windows' p50s, and the worst window's p99 (the windows' reservoirs can't be merged into an exact p99).
    var e2e = latency.GroupBy(r => $"{r.GetProperty("from").GetString()} -> {r.GetProperty("to").GetString()}").Select(g =>
    {
        var w = g.Select(r => r.GetProperty("latencyUs")).ToList();
        long n = w.Sum(x => x.GetProperty("n").GetInt64());
        return new
        {
            path = g.Key, messages = n,
            meanMs = n == 0 ? 0 : Math.Round(w.Sum(x => x.GetProperty("mean").GetDouble() * x.GetProperty("n").GetInt64()) / n / 1000, 3),
            p50Ms = Math.Round(WeightedMedian(w.Select(x => (x.GetProperty("p50").GetDouble(), x.GetProperty("n").GetInt64()))) / 1000, 3),
            p99Ms = Math.Round(w.Max(x => x.GetProperty("p99").GetDouble()) / 1000, 3),
            maxMs = Math.Round(w.Max(x => x.GetProperty("max").GetDouble()) / 1000, 3),
            windows = w.Count,
        };
    }).ToList();

    // Throughput into the sinks (the nodes with no outgoing edge), summed per profile window.
    var sinkNodes = graph.Nodes.Where(n => !graph.Edges.Any(e => e.SourceNode == n.Name)).Select(n => n.Name).ToHashSet();
    string NodeOf(string agentId) => agentId.Split('/').Skip(1).FirstOrDefault()?.Split('@')[0] ?? agentId;
    var sinkWindows = pipes.Where(r => r.GetProperty("end").GetString() == "in" && sinkNodes.Contains(NodeOf(r.GetProperty("to").GetString() ?? "")))
        .GroupBy(r => r.GetProperty("t").GetString()!.Substring(0, 19))
        .Select(g => (Bytes: g.Sum(r => r.GetProperty("bytesPerSec").GetDouble()), Messages: g.Sum(r => r.GetProperty("msgPerSec").GetDouble())))
        .ToList();
    static double WeightedMedian(IEnumerable<(double Value, long N)> xs)
    {
        var sorted = xs.Where(x => x.N > 0).OrderBy(x => x.Value).ToList();
        long half = (sorted.Sum(x => x.N) + 1) / 2, seen = 0;
        foreach (var x in sorted) { seen += x.N; if (seen >= half) return x.Value; }
        return 0;
    }
    double P(IEnumerable<double> xs, double q) { var s = xs.OrderBy(x => x).ToList(); return s.Count == 0 ? 0 : s[(int)Math.Min(s.Count - 1, Math.Floor(q * s.Count))]; }

    // Resources: the demo's agents, and each runtime process.
    var agentResources = resources.Where(r => r.GetProperty("process").GetString() == "agent" && (r.GetProperty("agent").GetString() ?? "").Contains($"/{gid}/"))
        .GroupBy(r => r.GetProperty("agent").GetString()!).Select(g => new
        {
            agent = g.Key[(g.Key.IndexOf(gid, StringComparison.Ordinal) + gid.Length + 1)..],
            maxRssMB = Math.Round(g.Max(r => r.GetProperty("rssBytes").GetInt64()) / 1048576.0, 1),
            meanCpuPct = Math.Round(g.Where(r => r.TryGetProperty("cpuPct", out var c) && c.ValueKind == JsonValueKind.Number).Select(r => r.GetProperty("cpuPct").GetDouble()).DefaultIfEmpty(0).Average(), 1),
            samples = g.Count(),
        }).OrderBy(a => a.agent).ToList();

    var primaries = instance.Agents.Where(a => a.Role == AgentRole.Primary).ToList();
    var summary = new
    {
        app, mode, instance = gid, seconds = Math.Round((ended - started).TotalSeconds, 1),
        dift = new { enabled = primaries.Count(a => a.Dift == DiftMode.Enabled), disabled = primaries.Count(a => a.Dift == DiftMode.Disabled) },
        lanes = primaries.Where(a => a.LaneLabel != null).GroupBy(a => a.NodeName).ToDictionary(g => g.Key, g => g.GroupBy(a => a.LaneLabel!).ToDictionary(x => x.Key, x => x.Count())),
        standbys = instance.Agents.Count(a => a.Role == AgentRole.Standby),
        endToEnd = e2e,
        sinkThroughput = new
        {
            windows = sinkWindows.Count,
            meanMsgPerSec = Math.Round(sinkWindows.Select(w => w.Messages).DefaultIfEmpty(0).Average(), 1),
            meanBytesPerSec = Math.Round(sinkWindows.Select(w => w.Bytes).DefaultIfEmpty(0).Average(), 1),
            p1BytesPerSec = Math.Round(P(sinkWindows.Select(w => w.Bytes), 0.01), 1),
        },
        violations = new
        {
            labelledMessages = labelled, violating, percent = labelled == 0 ? 0 : Math.Round(100.0 * violating / labelled, 2),
            perEdge = perEdge.OrderBy(e => e.Key).ToDictionary(e => e.Key, e => new { messages = e.Value.Messages, violations = e.Value.Violations }),
            note = bypassed > 0 ? $"{bypassed} bypass pipe(s) carry raw bytes without labels and aren't counted" : null,
        },
        agents = agentResources,
    };
    WriteJson("summary.json", summary);
    var text = new System.Text.StringBuilder();
    text.AppendLine($"{app} / {mode}: {summary.seconds} s, {primaries.Count} agents ({summary.dift.enabled} with DIFT), {summary.standbys} standby(s)");
    foreach (var (node, lanes) in summary.lanes) text.AppendLine($"  lanes {node}: {string.Join(", ", lanes.Select(l => $"{l.Key}×{l.Value}"))}");
    foreach (var p in e2e) text.AppendLine($"  end-to-end {p.path}: {p.messages} msgs, mean {p.meanMs} ms, p50 {p.p50Ms} ms, p99 {p.p99Ms} ms (worst window), max {p.maxMs} ms");
    text.AppendLine($"  sink throughput: mean {summary.sinkThroughput.meanMsgPerSec} msg/s, {summary.sinkThroughput.meanBytesPerSec} B/s; p1 {summary.sinkThroughput.p1BytesPerSec} B/s over {summary.sinkThroughput.windows} windows");
    text.AppendLine($"  violations: {violating} of {labelled} labelled messages ({summary.violations.percent}%) reached a host below their label{(summary.violations.note != null ? "; " + summary.violations.note : "")}");
    File.WriteAllText(Path.Combine(output, "summary.txt"), text.ToString());
    Console.Write(text);
}

// The sink writer's record count. The writer rewrites the file every 500 ms, and the VFS refuses a read that
// overlaps a write ("being used by another process"), so try a few times.
async Task<int> Progress(string file)
{
    Exception? last = null;
    for (int attempt = 0; attempt < 10; attempt++)
    {
        try
        {
            var j = JsonDocument.Parse(await rt0.FileSystemManager.ReadFileAsync(file, "/")).RootElement;
            return j.GetProperty("records").GetInt32();
        }
        catch (Exception ex) { last = ex; await Task.Delay(150); }
    }
    Log($"can't read {file}: {last?.GetBaseException().Message}");
    return 0;
}

async Task<bool> Write(RegistryAction action)
{
    for (int i = 0; i < 60; i++)
    {
        try { if (await rt0.UpdateRegistryAsync(action)) return true; } catch (Exception) { }
        await Task.Delay(500);
    }
    return false;
}

async Task<bool> Until(Func<bool> condition, int seconds, string what)
{
    for (int i = 0; i < seconds * 10; i++) { if (condition()) return true; await Task.Delay(100); }
    Log($"timed out waiting for {what}");
    return false;
}

void WriteJson(string name, object value) =>
    File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

void Log(string message) => Console.WriteLine($"[demo {DateTime.Now:HH:mm:ss}] {message}");

static void CopyDirectory(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
}

static string? FindRepository(string start)
{
    for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "src", "examples")) && Directory.Exists(Path.Combine(d.FullName, "src", "OneOS"))) return d.FullName;
    return null;
}
