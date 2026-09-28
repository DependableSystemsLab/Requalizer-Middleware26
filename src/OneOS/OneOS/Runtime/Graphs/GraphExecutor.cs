using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OneOS.Runtime.Language.Models;
using OneOS.Runtime.Scheduling;
using OneOS.Runtime.Sidecar;

namespace OneOS.Runtime.Graphs;
using AgentState = OneOS.Runtime.Scheduling.AgentState;

// Runs one agent: its process, its sidecar and its pipes (S§9.3, S§10.1).
//   pipes → sidecar.Receive → process stdin    process stdout → sidecar.Emit → pipes
public sealed class GraphAgentRunner
{
    private GraphInstanceInfo _instance;
    private readonly GraphAgentInfo _agent;
    private readonly IPipeHost _pipes;
    private readonly IProcessFactory _processes;
    private readonly Action<string, string> _log;          // level, message
    private readonly AgentSidecar _sidecar;
    private readonly object _lock = new();
    private readonly List<Func<IProcessEndpoint, Task>> _pendingInput = new();   // deliveries before the process is up
    private readonly ConcurrentDictionary<string, Outbound> _outbound = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _accepted = new();
    private IProcessEndpoint? _process;
    private Timer? _ticker;
    private Timer? _drift;
    private bool _stopping;

    // Checkpoints for the standby (L§9.3). While `_gate` is set, deliveries are held back so the process
    // snapshot and the exported sidecar state describe the same point in the input.
    private readonly TimeSpan? _checkpointInterval;
    private Timer? _checkpointTimer;
    private Outbound? _checkpointOut;
    private string? _checkpointPipeId;
    private long _checkpointSequence;
    private int _checkpointing;
    private bool _gate;
    private bool _checkpointUnsupported;
    private readonly List<Func<IProcessEndpoint, Task>> _held = new();

    // Typed streams (L§5.2, L§6.1): unless the node is @json_lines, each port is its own byte stream on a
    // descriptor (PortDescriptors), outputs are cut into messages by their port's framing, and delivered
    // messages are written to their port's descriptor as they are.
    private readonly bool _rawStreams;
    private readonly IReadOnlyDictionary<string, int> _descriptors;
    private readonly Dictionary<int, (string Port, Segmenter Segmenter)> _outputs = new();
    private Segmenter? _stderrLog;

    public GraphAgentRunner(CompiledGraph graph, GraphInstanceInfo instance, GraphAgentInfo agent, string? hostLabel,
        IPipeHost pipes, IProcessFactory processes, Action<SidecarEvent> events, Action<string, string> log,
        TimeSpan? checkpointInterval = null, Monitoring.RuntimeMetrics? metrics = null)
    {
        _instance = instance;
        _agent = agent;
        _pipes = pipes;
        _processes = processes;
        _log = log;
        _checkpointInterval = checkpointInterval;
        _metrics = metrics;
        _sidecar = new AgentSidecar(new SidecarConfig(graph, instance, agent, hostLabel), events);
        _rawStreams = !graph.Node(agent.NodeName).JsonLines;
        _descriptors = _rawStreams ? PortDescriptors.Assign(agent.Ports) : new Dictionary<string, int>();
        if (metrics != null)
        {
            _sidecar.EndToEnd += (from, to, latency) => metrics.RecordEndToEnd(instance.GraphInstanceId, from, to, latency);
            _sidecar.Arrived += (from, to, origin, arrived, bytes) =>
                metrics.RecordArrival(new Monitoring.MessageArrival(instance.GraphInstanceId, from, to, agent.AgentId, origin, arrived, bytes));
        }
    }

    // Always-on measurements (Monitoring): pipe ends this agent sends and receives on, end-to-end latency.
    private readonly Monitoring.RuntimeMetrics? _metrics;

    private Monitoring.PipeMeter? Meter(GraphPipeInfo pipe, Monitoring.PipeEnd end) =>
        _metrics?.Pipe(pipe.PipeId, end, new Monitoring.PipeMeta("graph", pipe.GraphInstanceId, pipe.EdgeName, pipe.SourceAgentId, pipe.DestinationAgentId,
            pipe.Bypass ? "raw" : "message"));

    public string AgentId => _agent.AgentId;
    public AgentState State { get; private set; } = AgentState.Pending;
    public event Action<GraphAgentRunner>? StateChanged;
    public AgentSidecar Sidecar => _sidecar;

    // The taint rose (label, and its rank: the number of labels at or below it, which only grows).
    // Hosts report it to the Registry, so a standby can start from the taint as of the failure (L§9.3).
    public event Action<GraphAgentRunner, string, int>? TaintRaised;

    // Receivers accept their inbound pipes before anything starts sending (reverse topological start).
    public void AcceptInbound()
    {
        foreach (var pipe in _instance.Pipes.Where(p => p.DestinationAgentId == _agent.AgentId))
        {
            if (!_accepted.Add(pipe.PipeId)) continue;
            if (pipe.Bypass) AcceptBypass(pipe);
            else _pipes.ExpectInbound(pipe.PipeId, PipeKind.Message, pipe.NativeFormat, input => Listen(input, frame => OnFrame(pipe, frame)));
        }
    }

    // A new plan version: accept new inbound pipes, connect new outbound ones, close removed ones, and
    // switch the sidecar to the new routing tables (S§10.4, S§10.5).
    public async Task ReconfigureAsync(GraphInstanceInfo instance)
    {
        List<Outgoing> released;
        lock (_lock)
        {
            _instance = instance;
            released = _sidecar.Reconfigure(instance);
        }
        AcceptInbound();
        var outgoing = instance.Pipes.Where(p => p.SourceAgentId == _agent.AgentId).ToDictionary(p => p.PipeId);
        foreach (var id in _outbound.Keys.Where(id => !outgoing.ContainsKey(id)).ToList())
            if (_outbound.TryRemove(id, out var gone)) await gone.CloseAsync();
        if (_process != null)
            foreach (var pipe in outgoing.Values.Where(p => !_outbound.ContainsKey(p.PipeId)))
                _outbound[pipe.PipeId] = new Outbound(this, pipe);
        foreach (var o in released)
            if (_outbound.TryGetValue(o.PipeId, out var ob)) ob.Send(o.Envelope);
        if (_process != null) await ConnectCheckpointChannelAsync();
    }

    public (long Received, long Delivered, long Emitted, int Queued) Metrics()
    {
        lock (_lock) return (_sidecar.Received, _sidecar.Delivered, _sidecar.Emitted, _sidecar.QueuedCount);
    }

    // `restore`: the latest checkpoint of the failed primary this agent takes over from; `taintFloor`: its
    // taint as of the failure (from the Registry), which may be above the checkpoint's (L§9.3).
    public async Task StartAsync(IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> environment,
        AgentCheckpoint? restore = null, string? taintFloor = null)
    {
        SetState(AgentState.Starting);
        if (restore != null || taintFloor != null)
            lock (_lock)
            {
                if (restore != null) _sidecar.ImportState(restore.Sidecar);
                if (taintFloor != null) _sidecar.RestoreTaint(taintFloor);
            }

        // The process is wired up before it starts, so none of its output is missed.
        if (_rawStreams)
        {
            var env = new Dictionary<string, string>(environment);
            foreach (var (port, fd) in _descriptors) env[$"ONEOS_PORT_{port}"] = fd.ToString(System.Globalization.CultureInfo.InvariantCulture);
            environment = env;
        }
        var process = _processes.CreateProcess(_agent.AgentId, argv, environment);
        if (_rawStreams)
        {
            SetUpOutputs();
            process.Output += OnOutput;
            process.OutputEnded += OnOutputEnded;
        }
        else
        {
            process.StdoutLine += OnStdout;
            process.StderrLine += line =>
            {
                if (StderrIsPort) EmitRaw(PortDescriptors.StderrPort, System.Text.Encoding.UTF8.GetBytes(line));
                else _log("info", $"{AgentId} stderr: {line}");
            };
        }
        process.Exited += code =>
        {
            if (_stopping) return;
            _log(code == 0 ? "info" : "error", $"{AgentId}: process exited with code {code}");
            SetState(code == 0 ? AgentState.Stopped : AgentState.Failed);
        };
        foreach (var pipe in _instance.Pipes.Where(p => p.SourceAgentId == _agent.AgentId))
            _outbound[pipe.PipeId] = new Outbound(this, pipe);

        try
        {
            var extra = _descriptors.Where(kv => kv.Value >= 3)
                .Select(kv => new ExtraDescriptor(kv.Value, _agent.Ports.First(p => p.Name == kv.Key).Direction == PortDirection.In)).ToList();
            Task Start(ProcessSnapshot? from) => _rawStreams ? process.StartRawAsync(from, extra, _cts.Token) : process.StartAsync(from, _cts.Token);
            if (restore?.Process is { } snapshot)
            {
                try { await Start(snapshot); }
                catch (NotSupportedException ex)
                {
                    _log("warning", $"{AgentId}: can't restore the process ({ex.Message}); starting it fresh");
                    await Start(null);
                }
            }
            else await Start(null);
            if (restore != null)
                _log(restore.Process != null ? "info" : "warning", $"{AgentId}: took over from checkpoint {restore.Sequence} ({restore.TakenAt:O})"
                    + (restore.Process == null ? "; the checkpoint has no process state, so the process starts fresh" : "")
                    + $"; taint {_sidecar.TaintName}");
        }
        catch (Exception ex)
        {
            _log("error", $"{AgentId}: failed to start {string.Join(" ", argv)}: {ex.Message}");
            SetState(AgentState.Failed);
            return;
        }

        // A stop that arrived while the process was starting couldn't reach it; stop it now.
        bool stopNow;
        lock (_lock)
        {
            stopNow = _stopping;
            if (!stopNow)
            {
                _process = process;
                foreach (var write in _pendingInput) _ = write(process);
                _pendingInput.Clear();
            }
        }
        if (stopNow)
        {
            await process.StopAsync(TimeSpan.FromSeconds(2));
            return;
        }

        _ticker = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        _drift = new Timer(_ => CheckDrift(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        await ConnectCheckpointChannelAsync();
        if (_checkpointInterval is TimeSpan every)
            _checkpointTimer = new Timer(_ => _ = CheckpointAsync(), null, every, every);
        SetState(AgentState.Running);
    }

    // The checkpoint channel follows the standby: a new plan may have moved it.
    private async Task ConnectCheckpointChannelAsync()
    {
        var channel = AgentCheckpoint.Channel(_instance, _instance.Agent(_agent.AgentId));
        if (channel?.PipeId == _checkpointPipeId) return;
        var old = _checkpointOut;
        _checkpointOut = channel == null ? null : new Outbound(this, channel);
        _checkpointPipeId = channel?.PipeId;
        if (old != null) await old.CloseAsync();
    }

    // Takes a checkpoint and sends it to the standby's host. Returns null when there is nothing to do.
    public async Task<AgentCheckpoint?> CheckpointAsync(CancellationToken ct = default)
    {
        if (_process == null || _stopping || _checkpointOut == null) return null;
        if (Interlocked.Exchange(ref _checkpointing, 1) == 1) return null;
        try
        {
            SidecarState state;
            lock (_lock)
            {
                _gate = true;
                state = _sidecar.ExportState();
            }
            ProcessSnapshot? snapshot = null;
            try
            {
                if (!_checkpointUnsupported) snapshot = await _process.CheckpointAsync(ct);
            }
            catch (NotSupportedException ex)
            {
                _checkpointUnsupported = true;
                _log("warning", $"{AgentId}: process checkpoints are not available ({ex.Message}); checkpoints carry middleware state only");
            }
            catch (Exception ex)
            {
                _log("error", $"{AgentId}: process checkpoint failed: {ex.Message}");
                return null;
            }
            finally
            {
                lock (_lock)
                {
                    _gate = false;
                    foreach (var write in _held) _ = write(_process);
                    _held.Clear();
                }
            }
            var checkpoint = new AgentCheckpoint(AgentId, ++_checkpointSequence, DateTimeOffset.UtcNow, snapshot, state);
            if (_checkpointOut is { } channel)
                foreach (var chunk in CheckpointStream.Split(checkpoint, channel.DestinationAgentId)) channel.Send(chunk, meter: false);
            return checkpoint;
        }
        finally { Interlocked.Exchange(ref _checkpointing, 0); }
    }

    // Sources-first shutdown: flush ordered buffers into the process, close its stdin, let it exit.
    public async Task StopAsync(TimeSpan grace)
    {
        lock (_lock) _stopping = true;
        _ticker?.Dispose();
        _drift?.Dispose();
        _checkpointTimer?.Dispose();
        List<Delivery> flushed;
        lock (_lock) flushed = _sidecar.Shutdown();
        foreach (var d in flushed) await WriteDelivery(d);
        IProcessEndpoint? process;
        lock (_lock) process = _process;
        if (process != null) await process.StopAsync(grace);
        _cts.Cancel();
        foreach (var o in _outbound.Values) await o.CloseAsync();
        if (_checkpointOut != null) await _checkpointOut.CloseAsync();
        SetState(AgentState.Stopped);
    }

    // A bypass edge (plan 6.5b): the sender's bytes go to the port's descriptor as they come. The stream's
    // label joins the taint before any of it arrives.
    private void AcceptBypass(GraphPipeInfo pipe)
    {
        var labels = _instance.Agent(pipe.SourceAgentId).Ports.FirstOrDefault(p => p.Name == pipe.SourcePort)?.PossibleLabels ?? Array.Empty<string>();
        bool raised;
        lock (_lock) raised = _sidecar.JoinStreamLabel(labels);
        if (raised) TaintRaised?.Invoke(this, _sidecar.TaintName, _sidecar.TaintRank);
        _pipes.ExpectInbound(pipe.PipeId, PipeKind.Raw, pipe.NativeFormat, input => Listen(input, chunk =>
        {
            if (!_instance.Pipes.Any(p => p.PipeId == pipe.PipeId)) return;   // removed by a newer plan
            Meter(pipe, Monitoring.PipeEnd.In)?.Record(chunk.Length);
            if (!_descriptors.TryGetValue(pipe.DestinationPort, out int fd))
            {
                _log("warning", $"{AgentId}: bypass edge {pipe.EdgeName} into port '{pipe.DestinationPort}', which has no descriptor; dropped");
                return;
            }
            _ = Enqueue(p => p.WriteAsync(fd, chunk));
        }));
    }

    // Graph pipes are message pipes: each unit received is exactly one serialized Envelope.
    internal static void Listen(OneOS.Common.Pipe input, Action<byte[]> onFrame)
    {
        input.OnReceive(onFrame);
        _ = input.StartListening();
    }

    private void OnFrame(GraphPipeInfo pipe, byte[] frame)
    {
        List<Delivery> deliveries;
        if (!_instance.Pipes.Any(p => p.PipeId == pipe.PipeId)) return;   // removed by a newer plan
        int before, after;
        try
        {
            var env = MessagePack.MessagePackSerializer.Deserialize<Envelope>(frame);
            // Counted on arrival, before delivery checks: dropped messages are still traffic on the pipe.
            Meter(pipe, Monitoring.PipeEnd.In)?.Record(frame.Length, env.Label,
                env.SentAt is { } sent ? DateTimeOffset.UtcNow - sent : null);
            lock (_lock)
            {
                before = _sidecar.Taint;
                deliveries = _sidecar.Receive(pipe.PipeId, env);
                after = _sidecar.Taint;
            }
        }
        catch (Exception ex)
        {
            _log("error", $"{AgentId}: bad frame on {pipe.PipeId}: {ex.Message}");
            return;
        }
        foreach (var d in deliveries) _ = WriteDelivery(d);
        if (after != before) TaintRaised?.Invoke(this, _sidecar.TaintName, _sidecar.TaintRank);
    }

    private void Tick()
    {
        List<Delivery> deliveries;
        int before, after;
        lock (_lock)
        {
            before = _sidecar.Taint;
            deliveries = _sidecar.Tick();
            after = _sidecar.Taint;
        }
        foreach (var d in deliveries) _ = WriteDelivery(d);
        if (after != before) TaintRaised?.Invoke(this, _sidecar.TaintName, _sidecar.TaintRank);
    }

    // Label frequencies drifted: re-solve the routing table locally (S§10.2).
    private void CheckDrift()
    {
        List<RoutingTableInfo> resolved;
        lock (_lock) resolved = _sidecar.CheckDrift(0.2, 200, TimeSpan.FromMilliseconds(100));
        foreach (var t in resolved) _log("info", $"{AgentId}: routing table for {t.EdgeName} re-solved after label-frequency drift (v{t.Version})");
    }

    private Task WriteDelivery(Delivery d)
    {
        Func<IProcessEndpoint, Task> write;
        if (_rawStreams)
        {
            if (!_descriptors.TryGetValue(d.Port, out int fd))
            {
                _log("warning", $"{AgentId}: no descriptor for port '{d.Port}'; message not delivered");
                return Task.CompletedTask;
            }
            var bytes = RawDelivery(d);
            write = p => p.WriteAsync(fd, bytes);
        }
        else
        {
            string line;
            try { line = DeliveryLine(d); }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                // Nothing upstream read this message's fields, so nothing parsed it before.
                _log("warning", $"{AgentId}: schema_mismatch: a message for port '{d.Port}' is not JSON ({ex.Message}); not delivered");
                return Task.CompletedTask;
            }
            write = p => p.WriteLineAsync(line);
        }
        return Enqueue(write);
    }

    // Writes reach the process in order: queued until it starts, and held while a checkpoint is taken.
    private Task Enqueue(Func<IProcessEndpoint, Task> write)
    {
        lock (_lock)
        {
            if (_process == null) { _pendingInput.Add(write); return Task.CompletedTask; }
            if (_gate) { _held.Add(write); return Task.CompletedTask; }
            // Started under the lock, so writes keep their order across a checkpoint's release of held writes.
            return write(_process);
        }
    }

    // A message as its port's byte stream carries it. `ndjson` and `lines` segments lost their newline to
    // the sender's segmenter; it goes back, so the process reads one line per message.
    private byte[] RawDelivery(Delivery d)
    {
        var framing = _agent.Ports.FirstOrDefault(p => p.Name == d.Port)?.Framing;
        var payload = d.Envelope.Payload;
        if (framing?.Kind is not (Language.FramingKind.Ndjson or Language.FramingKind.Lines)) return payload;
        var line = new byte[payload.Length + 1];
        payload.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    // One segmenter per output descriptor. stderr is the `stderr` port when the node declares one (L§6.1,
    // plan 6.5a), otherwise a log, line by line.
    private void SetUpOutputs()
    {
        foreach (var (port, fd) in _descriptors)
        {
            var info = _agent.Ports.First(p => p.Name == port);
            if (info.Direction != PortDirection.Out || (port == PortDescriptors.StderrPort && !StderrIsPort)) continue;
            var segmenter = Segmenter.For(info.Framing);
            segmenter.Error += e => _log("warning", $"{AgentId}: framing_error ({e.Kind}) on port '{port}' at byte {e.Offset}: {e.Message}");
            _outputs[fd] = (port, segmenter);
        }
        if (!_outputs.ContainsKey(2)) _stderrLog = Segmenter.For(Language.Framing.Lines);
    }

    // stderr feeds its port only while an edge carries it somewhere; otherwise it is the agent's log.
    private bool StderrIsPort => _instance.Pipes.Any(p => p.SourceAgentId == _agent.AgentId && p.SourcePort == PortDescriptors.StderrPort);

    private void OnOutput(int fd, ReadOnlyMemory<byte> chunk)
    {
        if (_outputs.TryGetValue(fd, out var o))
        {
            // Bypass edges take the chunk as it is; the other edges of the port (if any) get messages.
            bool messages = true;
            byte[]? copy = null;
            foreach (var ob in _outbound.Values.Where(x => x.Pipe.SourcePort == o.Port && x.Pipe.Bypass))
            {
                ob.SendRaw(copy ??= chunk.ToArray());
                messages = _outbound.Values.Any(x => x.Pipe.SourcePort == o.Port && !x.Pipe.Bypass);
            }
            if (messages)
                foreach (var segment in o.Segmenter.Push(chunk.Span)) EmitRaw(o.Port, segment);
        }
        else if (fd == 2 && _stderrLog != null)
            foreach (var line in _stderrLog.Push(chunk.Span)) _log("info", $"{AgentId} stderr: {System.Text.Encoding.UTF8.GetString(line)}");
    }

    private void OnOutputEnded(int fd)
    {
        if (_outputs.TryGetValue(fd, out var o))
            foreach (var segment in o.Segmenter.Complete()) EmitRaw(o.Port, segment);
        else if (fd == 2 && _stderrLog != null)
            foreach (var line in _stderrLog.Complete()) _log("info", $"{AgentId} stderr: {System.Text.Encoding.UTF8.GetString(line)}");
    }

    private void EmitRaw(string port, byte[] segment)
    {
        List<Outgoing> outgoing;
        lock (_lock) outgoing = _sidecar.Emit(port, segment);
        foreach (var o in outgoing)
            if (_outbound.TryGetValue(o.PipeId, out var ob)) ob.Send(o.Envelope);
    }

    private void OnStdout(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        string port;
        JsonElement data;
        try
        {
            using var doc = JsonDocument.Parse(line);
            port = doc.RootElement.GetProperty("port").GetString()!;
            data = doc.RootElement.GetProperty("data").Clone();
        }
        catch (Exception)
        {
            _log("warning", $"{AgentId}: stdout line is not a {{\"port\", \"data\"}} message; ignored: {Truncate(line)}");
            return;
        }
        var portInfo = _agent.Ports.FirstOrDefault(p => p.Name == port && p.Direction == PortDirection.Out);
        if (portInfo == null)
        {
            _log("warning", $"{AgentId}: no output port '{port}'; message ignored");
            return;
        }
        byte[]? bytes = null;
        if (portInfo.Framing.Kind != Language.FramingKind.Ndjson)
        {
            try { bytes = PortBytes(portInfo.Framing, data); }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                _log("warning", $"{AgentId}: data on port '{port}' ({portInfo.PortType}) must be {PortDataForm(portInfo.Framing)}; message ignored");
                return;
            }
        }
        List<Outgoing> outgoing;
        lock (_lock) outgoing = bytes == null ? _sidecar.Emit(port, data) : _sidecar.Emit(port, bytes);
        foreach (var o in outgoing)
            if (_outbound.TryGetValue(o.PipeId, out var ob)) ob.Send(o.Envelope);
    }

    // One MessagePack Envelope per message-pipe frame (the pipe delimits frames on the wire).
    private static byte[] Frame(Envelope envelope) => MessagePack.MessagePackSerializer.Serialize(envelope);

    // In the JSON-lines port protocol, `data` is the message itself on record and `json` ports, a JSON
    // string on `lines` ports, and base64 on other formats (their messages are arbitrary bytes).
    private static byte[] PortBytes(Language.Framing framing, JsonElement data) => framing.Kind == Language.FramingKind.Lines
        ? System.Text.Encoding.UTF8.GetBytes(data.GetString()!)
        : Convert.FromBase64String(data.GetString()!);

    private static string PortDataForm(Language.Framing framing) => framing.Kind == Language.FramingKind.Lines ? "a JSON string" : "a base64 string";

    // The port protocol's input line {"port": ..., "data": ...}.
    private string DeliveryLine(Delivery d)
    {
        var framing = _agent.Ports.FirstOrDefault(p => p.Name == d.Port)?.Framing ?? Language.Framing.Ndjson;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("port", d.Port);
            switch (framing.Kind)
            {
                case Language.FramingKind.Ndjson:
                    w.WritePropertyName("data");
                    w.WriteRawValue(d.Envelope.Payload);
                    break;
                case Language.FramingKind.Lines:
                    w.WriteString("data", System.Text.Encoding.UTF8.GetString(d.Envelope.Payload));
                    break;
                default:
                    w.WriteBase64String("data", d.Envelope.Payload);
                    break;
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] + "…" : s;

    private void SetState(AgentState s)
    {
        State = s;
        StateChanged?.Invoke(this);
    }

    // One outbound pipe: connects in the background, queuing frames until the receiver is there.
    private sealed class Outbound
    {
        private readonly GraphAgentRunner _owner;
        private readonly GraphPipeInfo _pipe;
        private readonly Task<OneOS.Common.Pipe?> _sink;
        private readonly SemaphoreSlim _order = new(1, 1);

        public Outbound(GraphAgentRunner owner, GraphPipeInfo pipe)
        {
            _owner = owner;
            _pipe = pipe;
            _sink = ConnectAsync();
        }

        public GraphPipeInfo Pipe => _pipe;

        private async Task<OneOS.Common.Pipe?> ConnectAsync()
        {
            var kind = _pipe.Bypass ? PipeKind.Raw : PipeKind.Message;
            for (int attempt = 0; !_owner._cts.IsCancellationRequested; attempt++)
            {
                try { return await _owner._pipes.OpenOutboundAsync(_pipe.PipeId, kind, _pipe.DestinationHostId!, _pipe.NativeFormat, _owner._cts.Token); }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex)
                {
                    if (attempt % 10 == 0) _owner._log("warning", $"{_owner.AgentId}: pipe {_pipe.PipeId} not connected yet ({ex.Message}); retrying");
                    try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, 100 * (attempt + 1))), _owner._cts.Token); }
                    catch (OperationCanceledException) { return null; }
                }
            }
            return null;
        }

        public string DestinationAgentId => _pipe.DestinationAgentId;

        // A bypass edge: a chunk of the sender's byte stream, as it is (counted as bytes and chunks).
        public void SendRaw(byte[] chunk)
        {
            _owner.Meter(_pipe, Monitoring.PipeEnd.Out)?.Record(chunk.Length);
            _ = SendAsync(chunk);
        }

        // A message copy: stamped with its send time (pipe latency), serialized, and counted. A frame over
        // the message pipe limit would end the receiver's listen loop, so it is dropped here instead.
        public void Send(Envelope envelope, bool meter = true)
        {
            var frame = Frame(envelope with { SentAt = DateTimeOffset.UtcNow });
            if (frame.Length > OneOS.Common.Socket.MaxFrameSize)
            {
                _owner._log("error", $"{_owner.AgentId}: message on {_pipe.PipeId} is {frame.Length} bytes, over the {OneOS.Common.Socket.MaxFrameSize}-byte message frame limit; dropped");
                return;
            }
            if (meter) _owner.Meter(_pipe, Monitoring.PipeEnd.Out)?.Record(frame.Length, envelope.Label);
            _ = SendAsync(frame);
        }

        private async Task SendAsync(byte[] frame)
        {
            var sink = await _sink;
            if (sink == null) return;
            await _order.WaitAsync();
            try { await sink.Send(frame); }
            catch (Exception ex) { _owner._log("error", $"{_owner.AgentId}: send on {_pipe.PipeId} failed: {ex.Message}"); }
            finally { _order.Release(); }
        }

        public async Task CloseAsync()
        {
            if (_sink.IsCompletedSuccessfully && _sink.Result is { } s) await s.CloseAsync();
        }
    }
}

// Runs the agents of one graph instance that are placed on this host.
public sealed class GraphExecutor
{
    private readonly CompiledGraph _graph;
    private GraphInstanceInfo _instance;
    private readonly string _hostId;
    private readonly string? _hostLabel;
    private readonly IPipeHost _pipes;
    private readonly Action<SidecarEvent> _events;
    private readonly Action<string, string> _log;
    private readonly List<GraphAgentRunner> _runners = new();
    private readonly IProcessFactory _processes;
    private readonly TimeSpan? _checkpointInterval;
    private readonly Func<string, string?> _taintFloor;
    private readonly Monitoring.RuntimeMetrics? _metrics;
    // Latest checkpoint per primary whose standby is on this host, and the channels accepted for them.
    private readonly ConcurrentDictionary<string, AgentCheckpoint> _checkpoints = new();
    private readonly HashSet<string> _checkpointChannels = new();

    // `checkpointInterval`: how often stateful primaries with a standby checkpoint (null: never).
    // `taintFloor`: an agent's last reported taint (from the Registry), for agents taking over (L§9.3).
    public GraphExecutor(CompiledGraph graph, GraphInstanceInfo instance, string hostId, string? hostLabel,
        IPipeHost pipes, IProcessFactory processes, Action<SidecarEvent> events, Action<string, string> log,
        TimeSpan? checkpointInterval = null, Func<string, string?>? taintFloor = null, Monitoring.RuntimeMetrics? metrics = null)
    {
        _checkpointInterval = checkpointInterval;
        _metrics = metrics;
        _taintFloor = taintFloor ?? (_ => null);
        _graph = graph;
        _instance = instance;
        _hostId = hostId;
        _hostLabel = hostLabel;
        _pipes = pipes;
        _events = events;
        _log = log;
        _processes = processes;
        // Standbys are not started (S§9.3); their hosts keep the primaries' latest checkpoints.
        foreach (var a in instance.Agents.Where(a => a.HostId == hostId && a.Role == AgentRole.Primary))
            _runners.Add(NewRunner(instance, a));
    }

    public IReadOnlyList<GraphAgentRunner> Runners => _runners;

    // The latest checkpoint received for a primary whose standby is on this host.
    public AgentCheckpoint? LatestCheckpoint(string primaryId) => _checkpoints.GetValueOrDefault(primaryId);

    public event Action<GraphAgentRunner, string, int>? TaintRaised;

    private GraphAgentRunner NewRunner(GraphInstanceInfo instance, GraphAgentInfo a)
    {
        var r = new GraphAgentRunner(_graph, instance, a, _hostLabel, _pipes, _processes, _events, _log, _checkpointInterval, _metrics);
        r.TaintRaised += (runner, label, rank) => { if (_runners.Contains(runner)) TaintRaised?.Invoke(runner, label, rank); };
        return r;
    }

    // Accepts the checkpoint channels of the primaries whose standbys are on this host.
    private void AcceptCheckpoints()
    {
        foreach (var s in _instance.Agents.Where(a => a.HostId == _hostId && a.Role == AgentRole.Standby && a.PrimaryId != null))
        {
            var primary = _instance.Agent(s.PrimaryId!);
            if (AgentCheckpoint.Channel(_instance, primary) is not { } channel || !_checkpointChannels.Add(channel.PipeId)) continue;
            var assembler = new CheckpointAssembler();
            _pipes.ExpectInbound(channel.PipeId, PipeKind.Message, channel.NativeFormat, input => GraphAgentRunner.Listen(input, frame =>
            {
                try
                {
                    if (assembler.Push(MessagePack.MessagePackSerializer.Deserialize<Envelope>(frame)) is not { } cp) return;
                    _checkpoints.AddOrUpdate(cp.AgentId, cp, (_, old) => cp.Sequence > old.Sequence || cp.TakenAt > old.TakenAt ? cp : old);
                }
                catch (Exception ex) { _log("error", $"checkpoint for {primary.AgentId} on {channel.PipeId} is unreadable: {ex.Message}"); }
            }));
        }
    }

    // Only runners this host still owns report state: a retired runner (its agent moved or was
    // replaced) must not overwrite the state its replacement reports.
    private void Forward(GraphAgentRunner r)
    {
        if (_runners.Contains(r)) AgentStateChanged?.Invoke(r);
    }
    public GraphInstanceInfo Instance => _instance;
    public event Action<GraphAgentRunner>? AgentStateChanged;

    // Receivers first: every local agent accepts its inbound pipes, then processes start in reverse
    // topological order of their nodes.
    public async Task StartAsync()
    {
        AcceptCheckpoints();
        var order = TopologicalIndex();
        foreach (var r in _runners)
        {
            r.StateChanged += Forward;
            r.AcceptInbound();
        }
        foreach (var r in _runners.OrderByDescending(r => order[Agent(r).NodeName]))
            await StartRunner(r);
    }

    // Reconciles this host with a new plan version: agents that left are stopped, new ones started,
    // and the rest reconfigured in place.
    public async Task ApplyPlanAsync(GraphInstanceInfo instance)
    {
        var mine = instance.Agents.Where(a => a.HostId == _hostId && a.Role == AgentRole.Primary).Select(a => a.AgentId).ToHashSet();
        // Agents that left this host, and dead agents that the plan still lists here (replacements), stop
        // and are removed; the latter start again below as new incarnations.
        foreach (var r in _runners.Where(r => !mine.Contains(r.AgentId) || r.State is AgentState.Failed or AgentState.Stopped).ToList())
        {
            _runners.Remove(r);
            try { await r.StopAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { _log("error", $"{r.AgentId}: stop failed: {ex.Message}"); }
        }
        _instance = instance;
        AcceptCheckpoints();
        var added = new List<GraphAgentRunner>();
        var restore = new Dictionary<GraphAgentRunner, AgentCheckpoint?>();
        foreach (var id in mine.Where(id => !_runners.Any(r => r.AgentId == id)))
        {
            var r = NewRunner(instance, instance.Agent(id));
            r.StateChanged += Forward;
            r.AcceptInbound();
            added.Add(r);
            // A primary taking over on the host that held its standby resumes from the latest checkpoint.
            restore[r] = _checkpoints.TryRemove(id, out var cp) ? cp : null;
        }
        // Checkpoints of primaries whose standby left this host are stale.
        var standbysHere = instance.Agents.Where(a => a.HostId == _hostId && a.Role == AgentRole.Standby).Select(a => a.PrimaryId).ToHashSet();
        foreach (var id in _checkpoints.Keys.Where(id => !standbysHere.Contains(id)).ToList()) _checkpoints.TryRemove(id, out _);

        foreach (var r in _runners) await r.ReconfigureAsync(instance);
        _runners.AddRange(added);
        var order = TopologicalIndex();
        foreach (var r in added.OrderByDescending(r => order[Agent(r).NodeName])) await StartRunner(r, restore[r]);
    }

    private async Task StartRunner(GraphAgentRunner r, AgentCheckpoint? restore = null)
    {
        var a = Agent(r);
        var env = new Dictionary<string, string>
        {
            ["ONEOS_GRAPH_INSTANCE"] = _instance.GraphInstanceId,
            ["ONEOS_AGENT_ID"] = a.AgentId,
            ["ONEOS_NODE"] = a.NodeName,
            ["ONEOS_INSTANCE_INDEX"] = a.InstanceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ONEOS_INPUT_PORTS"] = string.Join(",", a.Ports.Where(p => p.Direction == PortDirection.In).Select(p => p.Name)),
            ["ONEOS_OUTPUT_PORTS"] = string.Join(",", a.Ports.Where(p => p.Direction == PortDirection.Out).Select(p => p.Name)),
        };
        // A stateful agent that is a new incarnation (failover) never starts below its last reported taint.
        string? floor = a.Kind != PartitionKind.Keyless ? _taintFloor(a.AgentId) : null;
        await r.StartAsync(a.Argv, env, restore, floor);
    }

    // Sources first (S§12 StopGraph): upstream agents stop before their receivers flush.
    public async Task StopAsync(TimeSpan grace)
    {
        var order = TopologicalIndex();
        foreach (var r in _runners.OrderBy(r => order[Agent(r).NodeName]))
        {
            // One agent failing to stop cleanly must not leave the others running.
            try { await r.StopAsync(grace); }
            catch (Exception ex) { _log("error", $"{r.AgentId}: stop failed: {ex.Message}"); }
        }
    }

    private GraphAgentInfo Agent(GraphAgentRunner r) => _instance.Agent(r.AgentId);

    private Dictionary<string, int> TopologicalIndex()
    {
        var indeg = _graph.Nodes.ToDictionary(n => n.Name, _ => 0);
        foreach (var e in _graph.Edges) indeg[e.DestinationNode]++;
        var ready = new SortedSet<int>(_graph.Nodes.Where(n => indeg[n.Name] == 0).Select(n => n.Index));
        var index = new Dictionary<string, int>();
        while (ready.Count > 0)
        {
            var n = _graph.Nodes[ready.Min];
            ready.Remove(ready.Min);
            index[n.Name] = index.Count;
            foreach (var e in _graph.EdgesOutOf(n.Name))
                if (--indeg[e.DestinationNode] == 0) ready.Add(_graph.Node(e.DestinationNode).Index);
        }
        return index;
    }
}
