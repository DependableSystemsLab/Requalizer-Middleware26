using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OneOS.Runtime.Language;
using OneOS.Runtime.Language.Models;
using OneOS.Runtime.Scheduling;

namespace OneOS.Runtime.Sidecar;

// Messages on pipes are Envelopes (OneOS.Runtime): the payload as bytes plus the middleware's metadata
// (L§8.5 "Envelope"): label, sequence number, flow entry time. Labels travel as names so envelopes are
// meaningful outside one lattice instance. The payload is one message as its port's framing cut it
// (L§5.2): UTF-8 JSON on record and `json` ports, opaque bytes on formats. The sidecar parses it only
// where a policy reads its fields, which the compiler allows on record-typed ports only.

// A message buffered by an ordered or sequenced input port.
public sealed record PortMessage(string Port, Envelope Env);

// An agent's middleware state as of a checkpoint (L§9.3): taint, ordered-merge and sequencing state,
// origin sequence counters, and the messages it holds. Serializable with System.Text.Json.
public sealed record AnsweredMessage(string Label, long? Sequence, DateTimeOffset? Entry, DateTimeOffset? OriginAt = null, string? OriginNode = null);
public sealed record SidecarState(
    string Taint,
    string? DynamicLabel,
    IReadOnlyDictionary<string, long> OriginCounters,
    IReadOnlyDictionary<string, long> MaxEmitted,
    IReadOnlyDictionary<string, OrderedMergeState<PortMessage>> Merges,
    IReadOnlyDictionary<string, SequencedPortState<PortMessage>> Sequenced,
    IReadOnlyList<AnsweredMessage> Answered,
    IReadOnlyDictionary<string, IReadOnlyList<Envelope>> Queued,    // by edge
    DateTimeOffset? LastOriginAt = null, string? LastOriginNode = null);

// A message handed to the agent's process on one of its input ports.
public sealed record Delivery(string Port, Envelope Envelope);

// A message the sidecar sends on a pipe.
public sealed record Outgoing(string PipeId, string DestinationAgentId, string DestinationPort, Envelope Envelope);

// Middleware reports (L§8.5, S§10): label_violation, no_compliant_route, order_promise_violated,
// order_buffer_overflow, sequence_gap_skipped, dynamic_bound_violated, host_bound_violated, ...
public sealed record SidecarEvent(string Kind, string AgentId, string Detail);

// Everything one agent's sidecar needs, cut from the deployment plan and the bound graph (S§10.1).
public sealed record SidecarConfig(
    CompiledGraph Graph,
    GraphInstanceInfo Instance,
    GraphAgentInfo Agent,
    string? HostLabel)
{
    public GraphNode Node => Graph.Node(Agent.NodeName);
    // Bypass pipes (plan 6.5b) carry raw byte streams past the sidecar; it never sees them.
    public IEnumerable<GraphPipeInfo> Inbound => Instance.Pipes.Where(p => p.DestinationAgentId == Agent.AgentId && !p.Bypass);
    public IEnumerable<GraphPipeInfo> Outbound => Instance.Pipes.Where(p => p.SourceAgentId == Agent.AgentId && !p.Bypass);
}

// The sidecar of one agent: computes and checks labels, tracks taint, orders and sequences input,
// and routes output (L§8.5, S§4.3, S§10.1). It has no I/O of its own: the runtime feeds it messages
// and a clock, and sends what it returns.
public sealed class AgentSidecar
{
    private SidecarConfig _c;
    private readonly LabelLattice L;
    private readonly Action<SidecarEvent> _report;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Random _random;

    private readonly Dictionary<string, OrderedMerge<PortMessage>> _merges = new();
    private readonly Dictionary<string, SequencedPort<PortMessage>> _sequenced = new();
    private readonly Dictionary<string, long> _originCounters = new();
    private readonly Queue<(int Label, long? Seq, DateTimeOffset? Entry, (DateTimeOffset At, string Node)? Origin)> _answered = new();   // @one_to_one FIFO
    private (DateTimeOffset At, string Node)? _lastOrigin;   // origin of the latest input handed to the process

    // A message reached this terminal node (no output ports): origin node, this node, latency since origin.
    public event Action<string, string, TimeSpan>? EndToEnd;
    // The same arrival in full, for the per-message log: origin node, this node, origin time, arrival time, payload bytes.
    public event Action<string, string, DateTimeOffset, DateTimeOffset, long>? Arrived;
    private readonly Dictionary<string, long> _maxEmitted = new();                              // ordered output promises
    private readonly Dictionary<(string Edge, string Label), Queue<Envelope>> _queued = new();   // no_compliant_route
    private Dictionary<(string Port, string Edge), RoutingTableInfo> _tables;

    public AgentSidecar(SidecarConfig config, Action<SidecarEvent>? report = null, Func<DateTimeOffset>? clock = null, int seed = 1)
    {
        _c = config;
        L = config.Graph.Lattice;
        _report = report ?? (_ => { });
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _random = new Random(seed);
        Taint = L.BottomIndex;
        _tables = config.Instance.RoutingTables.Where(t => t.SourceAgentId == config.Agent.AgentId).ToDictionary(t => (t.SourcePort, t.EdgeName));

        foreach (var p in config.Agent.Ports.Where(p => p.Direction == PortDirection.In && p.Order != null))
        {
            var gp = config.Graph.Port(config.Agent.NodeName, p.Name);
            var settings = gp.Ordering?.InstanceOverrides.GetValueOrDefault(config.Agent.InstanceIndex) ?? gp.Ordering?.Default;
            if (p.Order!.Kind == OrderKind.Ordered)
            {
                long unit = AppCompiler.UnitNanos(p.Order.ClockUnit ?? "ms");
                _merges[p.Name] = new OrderedMerge<PortMessage>((settings?.LatenessNanos ?? 0) / unit,
                    settings?.IdleTimeoutNanos is long it ? TimeSpan.FromTicks(it / 100) : null,
                    settings?.MaxBufferBytes ?? AppCompiler.DefaultMaxBufferBytes, settings?.OnLate ?? OnLateMode.Drop,
                    p.Order.Per != null, (k, d) => Report(k, $"{p.Name}: {d}"));
            }
            else
                _sequenced[p.Name] = new SequencedPort<PortMessage>(
                    settings?.GapTimeoutNanos is long gt ? TimeSpan.FromTicks(gt / 100) : null,
                    settings?.MaxBufferBytes ?? AppCompiler.DefaultMaxBufferBytes, (k, d) => Report(k, $"{p.Name}: {d}"));
        }

        var host = config.Agent.HostSourceRange;
        if (host != null && config.HostLabel is string hl && !(L.Leq(host.Lo, HostLabelName) && L.Leq(HostLabelName, host.Hi)))
            Report("host_bound_violated", $"host label {hl} is outside the declared range {host.Lo}..{host.Hi}");
    }

    // T_r: joins every label handed to the process, never decreases (L§8.5).
    public int Taint { get; private set; }
    public string TaintName => L.Name(Taint);
    // The number of labels at or below the taint: strictly grows whenever the taint does.
    public int TaintRank => Enumerable.Range(0, L.Labels.Count).Count(x => L.Leq(x, Taint));

    // D_r from the dynamic label monitor, and D_ext from the IDM (S§4.3).
    public int? DynamicLabel { get; private set; }
    public int? ExternalLabel { get; private set; }

    public int QueuedCount => _queued.Values.Sum(q => q.Count);

    // Counters for monitoring and scaling (S§10.5): messages that arrived, were handed to the process,
    // and were emitted by it.
    public long Received { get; private set; }
    public long Delivered { get; private set; }
    // Messages delivered although a check failed (only when the plan doesn't enforce delivery checks).
    public long AuditedViolations { get; private set; }
    public long Emitted { get; private set; }

    // Labels emitted per (port, edge) since the edge's routing table was last solved, for drift (S§10.2).
    private readonly Dictionary<(string Port, string Edge), Dictionary<string, long>> _observed = new();

    // A new plan version (membership change, scaling): pipes and routing tables change; taint, ordering
    // state, sequence counters and queued messages carry over. Returns queued messages that now have a route.
    public List<Outgoing> Reconfigure(GraphInstanceInfo instance)
    {
        _c = _c with { Instance = instance, Agent = instance.Agent(_c.Agent.AgentId) };
        var tables = instance.RoutingTables.Where(t => t.SourceAgentId == _c.Agent.AgentId).ToList();
        _tables = new Dictionary<(string, string), RoutingTableInfo>();
        foreach (var t in tables) _observed.Remove((t.SourcePort, t.EdgeName));
        var result = new List<Outgoing>();
        foreach (var t in tables) result.AddRange(SwapRoutingTable(t));
        return result;
    }

    // Re-solves a routing table when the observed label frequencies drift from the ones it was solved for
    // (total variation distance above `threshold`, after at least `minMessages`). Returns the new tables.
    public List<RoutingTableInfo> CheckDrift(double threshold, long minMessages, TimeSpan budget)
    {
        var resolved = new List<RoutingTableInfo>();
        foreach (var (key, table) in _tables.ToList())
        {
            if (!_observed.TryGetValue(key, out var counts)) continue;
            long total = counts.Values.Sum();
            if (total < minMessages) continue;
            var observed = table.Labels.ToDictionary(l => l, l => counts.GetValueOrDefault(l) / (double)total);
            var solvedFor = SolvedFrequencies(table);
            double tv = 0.5 * table.Labels.Sum(l => Math.Abs(observed[l] - solvedFor[l]));
            if (tv <= threshold) continue;
            var pipes = _c.Outbound.Where(p => p.EdgeName == table.EdgeName).ToList();
            var problem = new LoadBalanceProblem(table.SourceAgentId, table.SourcePort, table.EdgeName, table.Labels, observed,
                table.ReceiverAgentIds, table.ReceiverAgentIds.ToDictionary(r => r, r => (IReadOnlySet<string>)pipes.First(p => p.DestinationAgentId == r).Allowed.ToHashSet()),
                table.ReceiverAgentIds.ToDictionary(r => r, _ => 1.0 / table.ReceiverAgentIds.Count), table.Redundancy);
            var next = LoadBalancer.Solve(problem, "drift", budget, table.Version + 1);
            _tables[key] = next;
            _observed.Remove(key);
            resolved.Add(next);
        }
        return resolved;
    }

    // The label frequencies a table was solved for: the per-label routed mass (renormalized).
    private static Dictionary<string, double> SolvedFrequencies(RoutingTableInfo t)
    {
        var mass = t.Labels.Select((l, i) => (l, m: t.Weights[i].Sum())).ToDictionary(x => x.l, x => x.m);
        double total = mass.Values.Sum();
        return mass.ToDictionary(kv => kv.Key, kv => total > 0 ? kv.Value / total : 1.0 / mass.Count);
    }

    private string HostLabelName => _c.HostLabel is string h && L.Contains(h) ? h : L.Bottom;

    // --- Checkpoints (L§9.3) ---

    public SidecarState ExportState() => new(
        L.Name(Taint), DynamicLabel is int d ? L.Name(d) : null,
        new Dictionary<string, long>(_originCounters), new Dictionary<string, long>(_maxEmitted),
        _merges.ToDictionary(kv => kv.Key, kv => kv.Value.Export()),
        _sequenced.ToDictionary(kv => kv.Key, kv => kv.Value.Export()),
        _answered.Select(a => new AnsweredMessage(L.Name(a.Label), a.Seq, a.Entry, a.Origin?.At, a.Origin?.Node)).ToList(),
        _queued.GroupBy(kv => kv.Key.Edge).ToDictionary(g => g.Key, g => (IReadOnlyList<Envelope>)g.SelectMany(kv => kv.Value).ToList()),
        _lastOrigin?.At, _lastOrigin?.Node);

    // Restores a checkpoint into a fresh sidecar of the same agent. Taint only ever rises (RestoreTaint).
    public void ImportState(SidecarState state)
    {
        long now = _clock().UtcTicks;
        RestoreTaint(state.Taint);
        if (state.DynamicLabel != null) DynamicLabel = L.IndexOf(state.DynamicLabel);
        foreach (var (port, n) in state.OriginCounters) _originCounters[port] = Math.Max(n, _originCounters.GetValueOrDefault(port));
        foreach (var (port, ts) in state.MaxEmitted) _maxEmitted[port] = ts;
        foreach (var (port, m) in state.Merges) if (_merges.TryGetValue(port, out var merge)) merge.Import(m, now);
        foreach (var (port, q) in state.Sequenced) if (_sequenced.TryGetValue(port, out var seq)) seq.Import(q, now);
        _answered.Clear();
        foreach (var a in state.Answered)
            _answered.Enqueue((L.IndexOf(a.Label), a.Sequence, a.Entry, a.OriginAt is { } at ? (at, a.OriginNode ?? "") : null));
        if (state.LastOriginAt is { } last) _lastOrigin = (last, state.LastOriginNode ?? "");
        foreach (var (edge, envs) in state.Queued)
            foreach (var env in envs)
            {
                if (!_queued.TryGetValue((edge, env.Label ?? L.Bottom), out var q)) _queued[(edge, env.Label ?? L.Bottom)] = q = new Queue<Envelope>();
                q.Enqueue(env);
            }
    }

    // Taint survives failover as of the moment of failure (L§9.3): a restored instance starts here.
    public void RestoreTaint(string label) => Taint = L.Join(Taint, L.IndexOf(label));

    // A bypass stream connects (plan 6.5b): it carries one label, the join of what its sender may emit, and
    // everything it delivers is handed to the process, so the taint takes it at once. Returns whether it rose.
    public bool JoinStreamLabel(IEnumerable<string> labels)
    {
        int before = Taint;
        Taint = L.Join(Taint, L.JoinAll(labels.Select(L.IndexOf)));
        return Taint != before;
    }

    public void SetDynamicLabel(string label)
    {
        int x = L.IndexOf(label);
        var range = _c.Agent.DynamicRange;
        if (range != null && !(L.Leq(L.IndexOf(range.Lo), x) && L.Leq(x, L.IndexOf(range.Hi))))
            Report("dynamic_bound_violated", $"monitor reported {label}, outside {range.Lo}..{range.Hi}; using it anyway");
        DynamicLabel = x;
    }

    public void SetExternalLabel(string label) => ExternalLabel = L.IndexOf(label);

    // --- Input path ---

    // A message arrives on an inbound pipe. Delivery checks run first (unless elided or unchecked);
    // then ordered/sequenced ports buffer it. Returns the messages now handed to the process.
    public List<Delivery> Receive(string pipeId, Envelope env)
    {
        var pipe = _c.Inbound.FirstOrDefault(p => p.PipeId == pipeId) ?? throw new ArgumentException($"{pipeId} is not an inbound pipe of {_c.Agent.AgentId}");
        Received++;
        int label = LabelOf(env);
        var port = pipe.DestinationPort;
        // Parsed at most once, and only when a ceiling labeller, the clock or a `per` field reads it.
        var body = new Body(env.Payload);
        var portInfo = _c.Agent.Ports.First(p => p.Name == port);
        bool checks = !pipe.Unchecked;
        bool readsFields = _merges.ContainsKey(port)
            || (checks && !pipe.Elided.HasFlag(ElidedChecks.PortCeiling) && portInfo.Ceiling is { Kind: not LabelSpecKind.Constant });
        if (readsFields && !body.TryParse(out var error))
        {
            Report("schema_mismatch", $"not delivered on {pipe.EdgeName} to {port}: the payload is not JSON ({error})");
            return new List<Delivery>();
        }
        if (checks && FailedCheck(pipe, label, body) is string failed)
        {
            if (_c.Instance.Plan.EnforceDeliveryChecks)
            {
                Report("label_violation", $"not delivered on {pipe.EdgeName} to {port}: {env.Label} fails the {failed} check");
                return new List<Delivery>();
            }
            AuditedViolations++;   // an evaluation plan (EnforceDeliveryChecks off): counted, delivered anyway
        }

        long now = _clock().UtcTicks;
        var result = new List<Delivery>();
        if (_merges.TryGetValue(port, out var merge))
        {
            var order = portInfo.Order!;
            long ts = ReadLong(body.Json, order.ClockField!);
            string? per = order.Per != null ? Read(body.Json, order.Per).GetRawText() : null;
            foreach (var (outcome, m) in merge.Offer(new Buffered<PortMessage>(pipe.StreamId, ts, per, env.Payload.Length, new PortMessage(port, env)), now))
                result.Add(Handle(outcome, m, port));
        }
        else if (_sequenced.TryGetValue(port, out var seq))
        {
            if (env.Sequence is not long n) { Report("label_violation", $"unsequenced message on sequenced port {port}"); return result; }
            foreach (var m in seq.Offer(new Buffered<PortMessage>(pipe.StreamId, n, null, env.Payload.Length, new PortMessage(port, env)), now))
                result.Add(Deliver(port, m.Item.Env));
        }
        else result.Add(Deliver(port, env));
        return result;
    }

    // Advances idle and gap timeouts.
    public List<Delivery> Tick()
    {
        long now = _clock().UtcTicks;
        var result = new List<Delivery>();
        foreach (var (port, merge) in _merges)
            foreach (var (outcome, m) in merge.Tick(now)) result.Add(Handle(outcome, m, port));
        foreach (var (port, seq) in _sequenced)
            foreach (var m in seq.Tick(now)) result.Add(Deliver(port, m.Item.Env));
        return result;
    }

    // Graph shutdown: ordered buffers are flushed in order.
    public List<Delivery> Shutdown() =>
        _merges.SelectMany(kv => kv.Value.Flush().Select(m => Deliver(kv.Key, m.Item.Env))).ToList();

    private Delivery Handle(MergeOutcome outcome, Buffered<PortMessage> m, string port)
    {
        if (outcome == MergeOutcome.Routed)
        {
            var route = _c.Agent.Ports.First(p => p.Name == port).OnLateRoutePort!;
            return Deliver(route, m.Item.Env);
        }
        return Deliver(port, m.Item.Env);
    }

    // Before a message is handed to the process: T_r := T_r ⊔ label(m) (L§8.5).
    private Delivery Deliver(string port, Envelope env)
    {
        int label = LabelOf(env);
        Taint = L.JoinTable[Taint][label] >= 0 ? L.JoinTable[Taint][label] : L.TopIndex;
        (DateTimeOffset, string)? origin = env.OriginTimestamp is { } at ? (at, env.OriginNode ?? "") : null;
        if (_c.Agent.OneToOne) _answered.Enqueue((label, env.Sequence, env.EntryTimestamp, origin));
        if (origin != null)
        {
            _lastOrigin = origin;
            if (_c.Node.IsSink)
            {
                var now = _clock();
                EndToEnd?.Invoke(origin.Value.Item2, _c.Node.Name, now - origin.Value.Item1);
                Arrived?.Invoke(origin.Value.Item2, _c.Node.Name, origin.Value.Item1, now, env.Payload.Length);
            }
        }
        Delivered++;
        return new Delivery(port, env);
    }

    // The delivery checks of L§8.5 plus automatic and lane ceilings (S§4.3), skipping elided ones (S§9.1).
    private string? FailedCheck(GraphPipeInfo pipe, int label, Body body)
    {
        var e = pipe.Elided;
        var port = _c.Agent.Ports.First(p => p.Name == pipe.DestinationPort);
        if (!e.HasFlag(ElidedChecks.PortCeiling) && port.Ceiling != null && !L.Leq(label, CeilingLabel(port.Ceiling, () => body.Json)))
            return "port ceiling";
        if (!e.HasFlag(ElidedChecks.InstanceCeiling))
        {
            if (_c.Agent.InstanceCeiling != null && !L.Leq(label, CeilingLabel(_c.Agent.InstanceCeiling, Replica)))
                return "instance ceiling";
            if (_c.Agent.ExternalSinkCeiling is string cExt && !L.Leq(label, L.IndexOf(cExt)))
                return "external sink ceiling";
            if (_c.Agent.LaneLabel is string lane && !L.Leq(label, L.IndexOf(lane)))
                return "lane ceiling";
        }
        if (!e.HasFlag(ElidedChecks.Host) && !L.Leq(label, L.IndexOf(HostLabelName)))
            return "host";
        if (!e.HasFlag(ElidedChecks.Compartment) && L.ForbiddenTop && L.IsForbidden(L.JoinTable[Taint][label] is var j && j >= 0 ? j : L.TopIndex))
            return "compartment isolation";
        return null;
    }

    private int CeilingLabel(LabelSpec spec, Func<object> subject)
    {
        if (spec.Kind == LabelSpecKind.Constant) return L.IndexOf(spec.Label!);
        try { return ConcreteEval.EvalLabel(spec.Body!, Env(spec, subject(), null), L); }
        catch (EvalException ex)
        {
            Report("labeller_failed", $"ceiling {spec}: {ex.Message}; treating the ceiling as ⊥");
            return L.BottomIndex;
        }
    }

    private object Replica() => new Dictionary<string, object?> { ["node"] = _c.Agent.NodeName, ["index"] = (long)_c.Agent.InstanceIndex };

    private Dictionary<string, object?> Env(LabelSpec spec, object subject, int? inLabel)
    {
        var env = new Dictionary<string, object?>();
        if (spec.LabellerName == null)
            for (int i = 0; i < _c.Graph.Parameters.Count; i++) env[_c.Graph.Parameters[i].Name] = _c.Graph.Args?[i];
        var ps = spec.Parameters ?? Array.Empty<LabellerParamInfo>();
        if (ps.Count > 0) env[ps[0].Name] = subject;
        if (ps.Count > 1 && inLabel is int x) env[ps[1].Name] = new LabelValue(x);
        return env;
    }

    // --- Output path ---

    // The process emits a message on an output port. Computes its label, runs the emission check,
    // stamps sequencing metadata and routes it. Returns the copies to send.
    public List<Outgoing> Emit(string port, ReadOnlyMemory<byte> payload) => Emit(port, new Body(payload.ToArray()));

    // A JSON message (record and `json` ports), already parsed.
    public List<Outgoing> Emit(string port, JsonElement payload) =>
        Emit(port, new Body(System.Text.Encoding.UTF8.GetBytes(payload.GetRawText()), payload));

    private List<Outgoing> Emit(string port, Body body)
    {
        Emitted++;
        var agent = _c.Agent;
        var node = _c.Node;

        // base (L§8.5, S§4.3): D_r for declared dynamic (also when SPW09 turned the internal monitor off), the answered input for @one_to_one, else T_r.
        // stderr output answers no input: it takes the taint, and leaves the @one_to_one queue alone.
        bool stderr = port == Graphs.PortDescriptors.StderrPort;
        (int Label, long? Seq, DateTimeOffset? Entry, (DateTimeOffset At, string Node)? Origin)? answered =
            agent.OneToOne && !stderr && _answered.Count > 0 ? _answered.Dequeue() : null;
        bool declaredDynamic = agent.DynamicRange != null;
        int baseLabel = declaredDynamic ? DynamicLabel ?? L.IndexOf(agent.DynamicRange!.Hi)
            : answered?.Label ?? Taint;
        // A source's stderr carries what it holds: the labels its data ports may carry (static, sound).
        if (stderr && node.IsSource)
            baseLabel = L.Join(baseLabel, L.JoinAll(agent.Ports.First(p => p.Name == port).PossibleLabels.Select(L.IndexOf)));

        // ext: static source joins and output-evaluable sources when Disabled; D_ext when Enabled.
        int ext = L.BottomIndex;
        if (agent.Dift == DiftMode.Disabled)
        {
            if (agent.SourceJoin != null) ext = L.IndexOf(agent.SourceJoin);
            // Output-evaluable labellers are trusted components run by the node's own runtime; the sidecar
            // can't evaluate them, so it joins their upper bound, which is sound.
            ext = L.Join(ext, L.JoinAll(agent.OutputEvaluable.SelectMany(s => s.Label.Range ?? Array.Empty<string>()).Select(L.IndexOf)));
        }
        else if (!declaredDynamic) ext = ExternalLabel ?? ExternalBound();

        int input = L.Join(baseLabel, ext);
        if (agent.HostSourceRange != null) input = L.Join(input, L.IndexOf(HostLabelName));

        // Fields are read by a labeller, an order promise or key routing; a payload they can't parse is
        // dropped before anything else happens to it.
        var portInfo = agent.Ports.First(p => p.Name == port);
        var labeller = _c.Graph.Labels.DataLabellers.GetValueOrDefault($"{node.Name}.{port}");
        bool readsFields = labeller is { Kind: not LabelSpecKind.Constant }
            || portInfo.Order is { Kind: OrderKind.Ordered, ClockField: not null }
            || _c.Outbound.Any(p => p.SourcePort == port && p.Routing == RoutingMode.Keyed);
        if (readsFields && !body.TryParse(out var error))
        {
            Report("schema_mismatch", $"{node.Name}.{port}: the payload is not JSON ({error}); not emitted");
            return new List<Outgoing>();
        }

        int label = input;
        if (labeller != null)
        {
            if (labeller.Kind == LabelSpecKind.Constant) label = L.IndexOf(labeller.Label!);
            else
            {
                try { label = ConcreteEval.EvalLabel(labeller.Body!, Env(labeller, body.Json, input), L); }
                catch (EvalException ex)
                {
                    Report("labeller_failed", $"{node.Name}.{port}: {ex.Message}; keeping the input label {L.Name(input)}");
                    label = input;
                }
            }
        }

        // Sequencing (L§6.6.4): the origin stamps, @one_to_one nodes inherit.
        long? seq = portInfo.SequenceOrigin ? NextSequence(port) : portInfo.SequencePropagating ? answered?.Seq : null;
        var entry = answered?.Entry ?? _clock();
        // Lineage for end-to-end latency: sources start it; others pass on the answered input's origin
        // (@one_to_one) or the latest input's (user decision); a node that emits before any input starts one.
        var origin = node.IsSource ? (_clock(), node.Name) : answered?.Origin ?? _lastOrigin ?? (_clock(), node.Name);
        CheckOrderPromise(portInfo, body);

        var env = new Envelope
        {
            SenderAgentUri = agent.AgentId, Strategy = RoutingStrategy.Direct,
            Payload = body.Bytes,
            Label = L.Name(label), Sequence = seq, EntryTimestamp = entry,
            OriginTimestamp = origin.Item1, OriginNode = origin.Item2,
        };
        var result = new List<Outgoing>();
        foreach (var edge in _c.Outbound.Where(p => p.SourcePort == port).GroupBy(p => p.EdgeName))
        {
            // The emission check is skipped only for copies on unchecked member edges (L§8.7).
            bool unchecked_ = edge.First().Unchecked;
            if (!unchecked_ && (L.IsForbidden(label) || (L.Strict && label == L.BottomIndex)))
            {
                Report("label_violation", $"not emitted on {edge.Key}: label {L.Name(label)} may not be carried");
                continue;
            }
            result.AddRange(Route(edge.Key, edge.ToList(), env, label, body));
        }
        return result;
    }

    // Without an IDM report, an Enabled node's external label is bounded by its declared sources.
    private int ExternalBound()
    {
        var sources = _c.Agent.Idm?.Sources ?? Array.Empty<ExternalSourceInfo>();
        return L.JoinAll(sources.SelectMany(s => new[] { s.Label.Label }.Concat(s.Label.Range ?? Array.Empty<string>()))
            .Where(x => x != null && L.Contains(x)).Select(x => L.IndexOf(x!)));
    }

    private long NextSequence(string port)
    {
        long n = _originCounters.GetValueOrDefault(port);
        _originCounters[port] = n + 1;
        return n;
    }

    // `ordered [within D]` on an output port is a promise; violations are reported, never dropped (L§6.6.2).
    private void CheckOrderPromise(AgentPortInfo port, Body body)
    {
        if (port.Order is not { Kind: OrderKind.Ordered, ClockField: { } field }) return;
        long ts = ReadLong(body.Json, field);
        long slack = port.Order.WithinClockUnits ?? 0;
        if (_maxEmitted.TryGetValue(port.Name, out var max) && ts < max - slack)
            Report("order_promise_violated", $"{port.Name}: {ts} < {max} - {slack}");
        _maxEmitted[port.Name] = Math.Max(max, ts);
    }

    // Routing (L§6.5, S§10.2). A copy is never sent on a pipe whose Allowed set lacks the label.
    private IEnumerable<Outgoing> Route(string edge, List<GraphPipeInfo> pipes, Envelope env, int label, Body body)
    {
        var labelName = L.Name(label);
        IEnumerable<GraphPipeInfo> targets;
        switch (pipes[0].Routing)
        {
            case RoutingMode.Direct:
            case RoutingMode.Broadcast:
                targets = pipes;
                break;
            case RoutingMode.Keyed:
            {
                var dest = _c.Graph.Node(_c.Graph.Edge(edge)!.DestinationNode);
                int g;
                try { g = KeyRouter.KeyGroup(KeyRouter.ExtractKey(body.Json, pipes[0].KeyPaths!), dest.KeyGroups); }
                catch (KeyNotFoundException ex) { Report("key_missing", $"{edge}: {ex.Message}"); yield break; }
                // Compartment lanes (F3): each lane owns every key group, so pick the owner in a lane that
                // admits the label. Without one, the first owner is chosen and the copy is refused below.
                var owners = pipes.Where(p => p.DestinationKeyGroups is { } r && g >= r.From && g < r.To).ToList();
                targets = owners.Where(p => p.Allowed.Contains(labelName)).Concat(owners).Take(1);
                break;
            }
            default:
            {
                var port = pipes[0].SourcePort;
                if (!_tables.TryGetValue((port, edge), out var table))
                {
                    targets = pipes.Where(p => p.Allowed.Contains(labelName)).Take(1);
                    break;
                }
                if (!_observed.TryGetValue((port, edge), out var counts)) _observed[(port, edge)] = counts = new Dictionary<string, long>();
                counts[labelName] = counts.GetValueOrDefault(labelName) + 1;
                var routes = table.Routes(labelName).ToList();
                if (routes.Count == 0)
                {
                    // Queue at the sender; never send to a non-compliant receiver (S§10.2).
                    Report("no_compliant_route", $"{edge}: no compliant receiver for label {labelName}; queued");
                    if (!_queued.TryGetValue((edge, labelName), out var q)) _queued[(edge, labelName)] = q = new Queue<Envelope>();
                    q.Enqueue(env);
                    yield break;
                }
                double x = _random.NextDouble(), acc = 0;
                var chosen = routes[^1].Receiver;
                foreach (var (r, prob) in routes) { acc += prob; if (x < acc) { chosen = r; break; } }
                targets = pipes.Where(p => p.DestinationAgentId == chosen);
                break;
            }
        }

        foreach (var p in targets)
        {
            if (!p.Allowed.Contains(labelName))
            {
                // Broadcast copies to lanes that can't admit the label are dropped by the lane ceiling (S§5.2).
                Report("label_violation", $"{edge}: label {labelName} is not allowed on pipe {p.PipeId}; not sent");
                continue;
            }
            yield return new Outgoing(p.PipeId, p.DestinationAgentId, p.DestinationPort, env with { Target = p.DestinationAgentId, Channel = p.DestinationPort });
        }
    }

    // A new routing table (re-solved after backpressure, membership changes or drift) replaces the old
    // one atomically; queued messages that now have a compliant route are sent (S§10.2).
    public List<Outgoing> SwapRoutingTable(RoutingTableInfo table)
    {
        _tables = new Dictionary<(string, string), RoutingTableInfo>(_tables) { [(table.SourcePort, table.EdgeName)] = table };
        var result = new List<Outgoing>();
        foreach (var key in _queued.Keys.Where(k => k.Edge == table.EdgeName).ToList())
        {
            if (!table.Routes(key.Label).Any()) continue;
            var queue = _queued[key];
            _queued.Remove(key);
            var pipes = _c.Outbound.Where(p => p.EdgeName == table.EdgeName).ToList();
            // Queued copies are load-balanced (no key routing), so their payloads are never parsed here.
            foreach (var env in queue) result.AddRange(Route(table.EdgeName, pipes, env, LabelOf(env), new Body(env.Payload)));
        }
        return result;
    }

    // --- Helpers ---

    // A message without a label (not produced by a sidecar) carries ⊥.
    private int LabelOf(Envelope env) => env.Label is { } l ? L.IndexOf(l) : L.BottomIndex;

    // The payload as JSON (record and `json` ports: UTF-8 JSON).
    public static JsonElement Json(Envelope env) => Parse(env.Payload);

    private static JsonElement Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    // A message body: its bytes, parsed as JSON on first use.
    private sealed class Body
    {
        private JsonElement? _json;
        public Body(byte[] bytes, JsonElement? json = null) { Bytes = bytes; _json = json; }
        public byte[] Bytes { get; }
        public JsonElement Json => _json ??= Parse(Bytes);

        public bool TryParse(out string? error)
        {
            error = null;
            try { _ = Json; return true; }
            catch (JsonException ex) { error = ex.Message; return false; }
        }
    }

    private static JsonElement Read(JsonElement payload, IReadOnlyList<string> path)
    {
        var cur = payload;
        foreach (var step in path)
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(step, out cur))
                throw new KeyNotFoundException($"message has no field '{string.Join(".", path)}'");
        return cur;
    }

    private static long ReadLong(JsonElement payload, IReadOnlyList<string> path) => Read(payload, path).GetInt64();

    private void Report(string kind, string detail) => _report(new SidecarEvent(kind, _c.Agent.AgentId, detail));
}
