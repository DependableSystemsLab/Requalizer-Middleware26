using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Common;
using OneOS.Runtime.Graphs;
using OneOS.Runtime.Language;
using OneOS.Runtime.Language.Models;
using OneOS.Runtime.Scheduling;

namespace OneOS.Runtime.Kernel
{
    using AgentState = OneOS.Runtime.Scheduling.AgentState;

    // Dataflow graph instances at the Registry level (tier-1; scheduling-spec §9.2–§9.4, §10.4–§10.5, §12):
    //  - spawning: plan, commit (retrying on version conflicts), wait for the agents to run, roll back on
    //    failure; stopping;
    //  - the controller, on the Raft leader: failover, replacement of failed agents, elastic scaling;
    //  - the Registry writes tier-0 hosts make about their graph agents (state, metrics, taint).
    // Running the agents is tier-0 work, done by each runtime's ExecutionManager.
    public class GraphManager : Agent
    {
        private readonly Runtime _runtime;
        private Timer? _controllerTimer;
        private readonly ConcurrentDictionary<string, byte> _busy = new();
        private readonly ConcurrentDictionary<string, (int Desired, int Streak)> _scaleVotes = new();

        public GraphManager(Runtime runtime, ILogger<GraphManager> logger, Agent? parent = null)
            : base($"{runtime.Config.URI}/GraphManager", logger, parent)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        // How many consecutive agreeing evaluations (2 s apart) a scaling decision needs.
        public int ScalingStreak { get; set; } = 3;

        public SchedulerOptions Options { get; set; } = new();

        // Options as the schedulers use them: with the cluster-wide sockets of JavaScript programs found in
        // their sources (plan step 8.5), unless the caller supplies its own detection.
        private SchedulerOptions SchedulingOptions => Options.ProgramSockets != null ? Options : Options with { ProgramSockets = ProgramSockets };

        // Program sources, prefetched before planning (programs in the distributed file system); local
        // programs are read on demand.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _programSources = new();

        private IReadOnlyList<string> ProgramSockets(Language.Models.GraphNode node, IReadOnlyList<string> argv)
        {
            if (JavaScriptSockets.Script(argv) is not { } script) return Array.Empty<string>();
            string? source = _programSources.TryGetValue(script, out var cached) ? cached
                : System.IO.Path.IsPathRooted(script) && System.IO.File.Exists(script) ? System.IO.File.ReadAllText(script) : null;
            return source == null ? Array.Empty<string>() : JavaScriptSockets.Find(source);
        }

        private async Task PrefetchProgramsAsync(Language.Models.CompiledGraph graph, IReadOnlyList<object?> args)
        {
            foreach (var node in graph.Nodes)
            {
                IReadOnlyList<string> argv;
                try { argv = node.Process.Argv ?? GraphScheduler.BindForSpawn(graph, args).Node(node.Name).Process.Argv!; }
                catch (Exception) { continue; }
                if (JavaScriptSockets.Script(argv) is not { } script || _programSources.ContainsKey(script)) continue;
                var path = script.StartsWith('/') ? script : "/" + script;
                if (!_runtime.FileSystemManager.IsFile(path)) continue;
                try { _programSources[script] = System.Text.Encoding.UTF8.GetString(await _runtime.FileSystemManager.ReadFileAsync(path, "/")); }
                catch (Exception ex) { _logger.LogDebug("Could not read {Script} to look for sockets: {Error}", path, ex.Message); }
            }
        }
        public IProfileStore Profiles { get; set; } = new InMemoryProfileStore();

        // --- Reports from tier-0 hosts ---

        public Task ReportAgentStateAsync(string graphId, string agentId, AgentState state) =>
            UpdateReliablyAsync(new SetGraphAgentStateAction { GraphId = graphId, AgentId = agentId, State = state.ToString() });

        // Per-agent rates [received/s, delivered/s, emitted/s, queued]; best effort, not retried.
        public async Task ReportMetricsAsync(string graphId, Dictionary<string, double[]> metrics)
        {
            try { await _runtime.UpdateRegistryAsync(new ReportGraphMetricsAction { GraphId = graphId, Metrics = metrics }); }
            catch (Exception ex) { _logger.LogDebug("Metrics report failed: {Error}", ex.Message); }
        }

        public Task ReportTaintAsync(string graphId, string agentId, string taint, int rank) =>
            UpdateReliablyAsync(new ReportGraphTaintAction { GraphId = graphId, AgentId = agentId, Taint = taint, Rank = rank });

        // A host could not run its part of the instance.
        public Task ReportHostFailureAsync(string graphId, string error) =>
            UpdateReliablyAsync(new SetGraphStateAction { GraphId = graphId, State = "Failed", Error = $"{_runtime.Config.ID}: {error}" });

    // --- Controller (on the Raft leader): failure handling (S§10.4) and elastic scaling (S§10.5) ---
    
        private async Task ControlAsync()
        {
            if (!_runtime.IsLeader) return;
            foreach (var record in _runtime.Registry.Graphs.Values.Where(r => r.State is "Running" or "Degraded").ToList())
            {
                if (!_busy.TryAdd(record.Id, 0)) continue;
                try { await ControlGraphAsync(record); }
                catch (Exception ex) { _logger.LogError(ex, "Controller: graph instance {Id}", record.Id); }
                finally { _busy.TryRemove(record.Id, out _); }
            }
        }
    
        private async Task ControlGraphAsync(GraphInstanceRecord record)
        {
            var plan = GraphSerialization.DeserializeInstance(record.PlanJson);
            var graph = GraphSerialization.DeserializeGraph(record.GraphJson);
            var alive = _runtime.TakeSnapshot().Hosts.Where(h => h.Alive).Select(h => h.HostId).ToHashSet();
            var failed = plan.Agents.Where(a => a.Role == AgentRole.Primary
                && (record.AgentStates.GetValueOrDefault(a.AgentId) is "Failed" || !alive.Contains(a.HostId!))).ToList();
    
            if (failed.Count > 0)
            {
                var stateful = failed.Where(a => a.Kind != PartitionKind.Keyless).ToList();
                // With `always`, a stateful primary fails over to its standby's host (L§9.3). Without a standby
                // (no `always`, or the standby's host is gone too), failure handling is left to the operator.
                var promote = stateful.Where(a => a.StandbyId != null && plan.Agent(a.StandbyId).HostId is string h && alive.Contains(h))
                    .Select(a => a.AgentId).ToHashSet();
                var stranded = stateful.Where(a => !promote.Contains(a.AgentId)).ToList();
                if (stranded.Count > 0 && record.State != "Degraded")
                    await UpdateReliablyAsync(new SetGraphStateAction
                    {
                        GraphId = record.Id, State = "Degraded",
                        Error = $"stateful agents failed without a live standby: {string.Join(", ", stranded.Select(a => a.AgentId))}",
                    });
                if (promote.Count > 0)
                {
                    var away = stateful.Select(a => a.HostId!).Concat(plan.Agents.Select(a => a.HostId!).Where(h => !alive.Contains(h))).ToHashSet();
                    await _runtime.RefreshClusterStateAsync();
                    var failover = new GraphScheduler(_runtime, SchedulingOptions, Profiles);
                    var promoted = await Task.Run(() => failover.Replan(graph, plan, new ReplanRequest(AvoidHosts: away, Trigger: "failover", Promote: promote)));
                    var reset = promote.Concat(promote.Select(p => p + "/standby")).ToList();
                    if (await CommitPlanAsync(record, promoted, reset, Array.Empty<string>()))
                        _logger.LogWarning("Controller: {Id}: failed over {Agents} to their standbys ({Hosts})", record.Id, string.Join(", ", promote),
                            string.Join(", ", promote.Select(a => promoted.Agent(a).HostId)));
                    return;
                }
                var keyless = failed.Where(a => a.Kind == PartitionKind.Keyless).Select(a => a.AgentId).ToHashSet();
                if (keyless.Count == 0) return;
    
                // 1. Stop routing to the failed instances right away (membership re-solve).
                if (plan.RoutingTables.Any(t => t.ReceiverAgentIds.Any(keyless.Contains)))
                {
                    var rerouted = GraphScheduler.RecomputeRoutingTables(plan, keyless, Options.LbSolveBudget);
                    if (!await CommitPlanAsync(record, rerouted, Array.Empty<string>(), Array.Empty<string>())) return;
                    _logger.LogWarning("Controller: {Id}: routing re-solved without {Agents}", record.Id, string.Join(", ", keyless));
                    plan = rerouted;
                    record = _runtime.Registry.Graphs[record.Id];
                }
    
                // 2. Place replacements in the same lanes, away from the failed hosts where possible.
                var avoid = failed.Select(a => a.HostId!).Concat(plan.Agents.Select(a => a.HostId!).Where(h => !alive.Contains(h))).ToHashSet();
                await _runtime.RefreshClusterStateAsync();
                var scheduler = new GraphScheduler(_runtime, SchedulingOptions, Profiles);
                var replaced = await Task.Run(() => scheduler.Replan(graph, plan, new ReplanRequest(Replace: keyless, AvoidHosts: avoid, Trigger: "membership")));
                if (await CommitPlanAsync(record, replaced, keyless.ToList(), Array.Empty<string>()))
                    _logger.LogWarning("Controller: {Id}: replaced {Agents} on {Hosts}", record.Id, string.Join(", ", keyless),
                        string.Join(", ", keyless.Select(a => replaced.Agent(a).HostId)));
                return;
            }
    
            // Elastic scaling of keyless groups (nodes or lanes) with an instance range.
            foreach (var group in plan.Agents.Where(a => a.Role == AgentRole.Primary && a.Kind == PartitionKind.Keyless)
                         .GroupBy(a => a.LaneLabel == null ? a.NodeName : $"{a.NodeName}@{a.LaneLabel}"))
            {
                var node = graph.Node(group.First().NodeName);
                if (!node.Instances.IsElastic) continue;
                double measured = group.Sum(a => record.Metrics.TryGetValue(a.AgentId, out var m) ? m[0] : 0);
                var profile = Profiles.Get(graph.Name, node.Name, node.Process.Argv?[0] ?? "");
                double? goal = group.First().LaneLabel == null ? graph.Policies.MinRates
                    .Where(r => (r.Target.Kind == TargetKind.Edge && graph.Edge(r.Target.Name)?.DestinationNode == node.Name)
                             || (r.Target.Kind == TargetKind.Port && r.Target.Name == node.Name && node.Inputs.Any(p => p.Name == r.Target.Port)))
                    .Select(r => (double?)r.MessagesPerSecond).Max() : null;
                int min = Math.Max(node.Instances.Min, graph.Policies.Always.Any(x => x.Node == node.Name && x.Instance == null) ? 2 : 1);
                int current = group.Count();
                int desired = ElasticScaling.Desired(current, Math.Min(min, node.Instances.Max), node.Instances.Max, measured, profile?.ThroughputPerInstance, goal);
                var key = $"{record.Id}|{group.Key}";
                if (desired == current) { _scaleVotes.TryRemove(key, out _); continue; }
                var vote = _scaleVotes.AddOrUpdate(key, (desired, 1), (_, v) => v.Desired == desired ? (desired, v.Streak + 1) : (desired, 1));
                if (vote.Streak < ScalingStreak) continue;
                _scaleVotes.TryRemove(key, out _);
    
                await _runtime.RefreshClusterStateAsync();
                var scheduler = new GraphScheduler(_runtime, SchedulingOptions, Profiles);
                var scaled = await Task.Run(() => scheduler.Replan(graph, plan, new ReplanRequest(GroupCounts: new Dictionary<string, int> { [group.Key] = desired }, Trigger: "scaling")));
                var removed = plan.Agents.Select(a => a.AgentId).Except(scaled.Agents.Select(a => a.AgentId)).ToList();
                if (await CommitPlanAsync(record, scaled, Array.Empty<string>(), removed))
                    _logger.LogWarning("Controller: {Id}: scaled {Group} from {From} to {To} instances (measured {Rate:0.#} msg/s)", record.Id, group.Key, current, desired, measured);
                return;   // one change per tick
            }
        }
    
        // Registry records are live objects that Raft applies mutate, so the base version is captured here.
        private async Task<bool> CommitPlanAsync(GraphInstanceRecord record, GraphInstanceInfo plan, IReadOnlyList<string> reset, IReadOnlyList<string> removed)
        {
            long baseVersion = record.PlanVersion;
            var committed = Reservations(plan);
            var (agents, pipes) = RegistryEntries(plan, record.Owner);
            var action = new UpdateGraphPlanAction
            {
                Agents = agents, Pipes = pipes,
                GraphId = record.Id, BasePlanVersion = baseVersion, PlanJson = GraphSerialization.Serialize(plan), Committed = committed,
                ExpectedHostVersions = committed.Where(kv => !record.Committed.TryGetValue(kv.Key, out var b) || kv.Value[0] > b[0] || kv.Value[1] > b[1])
                    .ToDictionary(kv => kv.Key, kv => _runtime.Registry.HostVersion(kv.Key)),
                ResetAgents = reset.ToList(), RemovedAgents = removed.ToList(),
            };
            if (!await UpdateReliablyAsync(action)) return false;
            var outcome = await WaitFor(() => _runtime.Registry.Graphs.TryGetValue(record.Id, out var r) && r.PlanVersion > baseVersion ? "applied"
                : _runtime.Registry.GraphCommitRejections.TryGetValue(action.ConflictKey, out var why) ? why : null, TimeSpan.FromSeconds(10), CancellationToken.None);
            if (outcome != "applied") _logger.LogWarning("Controller: plan update of {Id} not applied: {Why}", record.Id, outcome ?? "timeout");
            return outcome == "applied";
        }
    
        // The plan's primaries and pipes as Registry entries (step 5.4). A primary keeps its GPID across plan
        // versions; its standby appears as the entry's StandbyRuntime. The sidecar configuration stays in
        // the plan.
        private (List<AgentInfo> Agents, List<PipeInfo> Pipes) RegistryEntries(GraphInstanceInfo plan, string owner)
        {
            var domain = _runtime.Config.Domain;
            string Uri(string agentId) => GraphUris.Agent(owner, domain, agentId);
            var used = _runtime.Registry.Agents.Values.Select(a => a.GPID).ToHashSet();
            var agents = new List<AgentInfo>();
            foreach (var a in plan.Agents.Where(a => a.Role == AgentRole.Primary))
            {
                var uri = Uri(a.AgentId);
                int gpid = _runtime.Registry.Agents.TryGetValue(uri, out var existing) ? existing.GPID : 0;
                if (gpid == 0)
                    do { gpid = Random.Shared.Next(10000, 99999); } while (!used.Add(gpid));
                agents.Add(new AgentInfo
                {
                    URI = uri, GPID = gpid, Runtime = a.HostId ?? string.Empty,
                    StandbyRuntime = a.StandbyId != null ? plan.Agent(a.StandbyId).HostId ?? string.Empty : string.Empty,
                    User = owner, Graph = plan.GraphInstanceId, ReplicaSet = a.NodeName,
                    Language = a.Command switch
                    {
                        "node" => AgentInfo.LanguageInfo.JavaScript,
                        "python" or "python3" => AgentInfo.LanguageInfo.Python,
                        _ => AgentInfo.LanguageInfo.OneOS,
                    },
                    BinaryPath = a.Argv.Count > 0 ? a.Argv[0] : a.Command,
                    Arguments = a.Argv.Skip(1).ToList(),
                });
            }
            var pipes = plan.Pipes.Select(p => new PipeInfo
            {
                Id = p.PipeId, Graph = plan.GraphInstanceId,
                Sources = new List<string> { Uri(p.SourceAgentId) }, Sinks = new List<string> { Uri(p.DestinationAgentId) },
                Strategy = p.Routing switch
                {
                    RoutingMode.Broadcast => PipeInfo.PipeStrategy.Broadcast,
                    RoutingMode.LoadBalanced => PipeInfo.PipeStrategy.RoundRobin,
                    RoutingMode.Keyed => PipeInfo.PipeStrategy.Shuffle,
                    _ => PipeInfo.PipeStrategy.Direct,
                },
            }).ToList();
            return (agents, pipes);
        }

        private Dictionary<string, long[]> Reservations(GraphInstanceInfo plan)
        {
            var committed = new Dictionary<string, long[]>();
            foreach (var a in plan.Agents.Where(a => a.Role == AgentRole.Primary || Options.ReserveStandbyCapacity))
            {
                if (!committed.TryGetValue(a.HostId!, out var c)) committed[a.HostId!] = c = new long[2];
                c[0] += a.Demand.CpuMillis;
                c[1] += a.Demand.MemoryBytes;
            }
            return committed;
        }

    // --- Spawner side ---
    
        // Every scheduling run first refreshes the cluster state (cluster-info exchange), so it plans against
        // current host descriptions; nothing is exchanged otherwise.
        public async Task<GraphInstanceInfo> PlanAsync(CompiledGraph graph, IReadOnlyList<object?> args, CancellationToken ct)
        {
            await _runtime.RefreshClusterStateAsync(ct);
            await PrefetchProgramsAsync(graph, args);
            return await Task.Run(() => new GraphScheduler(_runtime, SchedulingOptions, Profiles).PlanGraph(graph, args, ct), ct);
        }
    
        // `owner`: the user the instance's agents run for (their Registry URIs start with it).
        public async Task<GraphInstanceInfo> SpawnAsync(CompiledGraph graph, IReadOnlyList<object?> args, CancellationToken ct, string? owner = null)
        {
            var bound = GraphScheduler.BindForSpawn(graph, args);
            await PrefetchProgramsAsync(bound, Array.Empty<object?>());
            var scheduler = new GraphScheduler(_runtime, SchedulingOptions, Profiles);
            GraphInstanceInfo? plan = null;
            var conflicts = new List<string>();
    
            // Commit (S§9.2): one Registry transaction, conditional on the planned hosts' versions.
            for (int attempt = 0; attempt <= Options.MaxCommitRetries && plan == null; attempt++)
            {
                await _runtime.RefreshClusterStateAsync(ct);
                var candidate = await Task.Run(() => scheduler.PlanGraph(bound, Array.Empty<object?>(), ct), ct);
                var record = Record(bound, candidate, owner ?? "system");
                var (agents, pipes) = RegistryEntries(candidate, record.Owner);
                if (!await UpdateReliablyAsync(new CommitGraphAction { Record = record, Agents = agents, Pipes = pipes }, ct))
                    throw new SchedulingException("SP008", "the Registry did not accept the commit (no leader reachable)");
                var outcome = await WaitFor(() => _runtime.Registry.Graphs.ContainsKey(record.Id) ? "committed"
                    : _runtime.Registry.GraphCommitRejections.TryGetValue(record.Id, out var why) ? why : null, TimeSpan.FromSeconds(10), ct);
                if (outcome == "committed") plan = candidate;
                else conflicts.Add(outcome ?? "the commit was not applied in time");
            }
            if (plan == null)
                throw new SchedulingException("SP008", $"Registry version conflicts persisted after {Options.MaxCommitRetries} retries", conflicts.ToArray());
    
            // Start (S§9.3): hosts start their agents; wait until every primary runs.
            var id = plan.GraphInstanceId;
            var primaries = plan.Agents.Where(a => a.Role == AgentRole.Primary).Select(a => a.AgentId).ToList();
            try
            {
                var result = await WaitFor(() =>
                {
                    if (!_runtime.Registry.Graphs.TryGetValue(id, out var r)) return "removed";
                    if (r.State == "Failed") return r.Error ?? "failed";
                    var failed = primaries.Where(a => r.AgentStates.GetValueOrDefault(a) is "Failed").ToList();
                    if (failed.Count > 0) return $"agents failed: {string.Join(", ", failed)}";
                    return primaries.All(a => r.AgentStates.GetValueOrDefault(a) is "Running") ? "running" : null;
                }, Options.StartupTimeout, ct);
                if (result != "running")
                {
                    await RollbackAsync(id);
                    throw new SchedulingException("SP009", $"graph instance {id} failed to start: {result ?? "startup timed out"}");
                }
            }
            catch (OperationCanceledException)
            {
                await RollbackAsync(id);
                throw;
            }
    
            await UpdateReliablyAsync(new SetGraphStateAction { GraphId = id, State = "Running" });
            return plan with
            {
                State = GraphInstanceState.Running,
                Agents = plan.Agents.Select(a => a with { State = a.Role == AgentRole.Primary ? AgentState.Running : AgentState.Pending }).ToList(),
            };
        }
    
        // Rollback (S§9.4): stop every started agent, then remove the records and release the resources.
        private async Task RollbackAsync(string id)
        {
            try { await StopAsync(id, CancellationToken.None); }
            catch (Exception ex) { _logger.LogError(ex, "Rollback of graph instance {Id} failed", id); }
        }
    
        // StopGraph (S§12): hosts stop their agents sources-first and flush ordered buffers; then the
        // records are removed, which releases the reserved resources.
        public async Task StopAsync(string id, CancellationToken ct)
        {
            if (!_runtime.Registry.Graphs.TryGetValue(id, out var record)) return;
            await UpdateReliablyAsync(new SetGraphStateAction { GraphId = id, State = "Stopping" }, ct);
            var plan = GraphSerialization.DeserializeInstance(record.PlanJson);
            var primaries = plan.Agents.Where(a => a.Role == AgentRole.Primary).Select(a => a.AgentId).ToList();
            await WaitFor(() => !_runtime.Registry.Graphs.TryGetValue(id, out var r) || primaries.All(a => r.AgentStates.GetValueOrDefault(a) is not ("Running" or "Starting"))
                ? "stopped" : null, TimeSpan.FromSeconds(15), ct);
            await UpdateReliablyAsync(new DeleteGraphAction { GraphId = id }, ct);
        }
    
        public IReadOnlyDictionary<string, GraphInstanceInfo> Instances => _runtime.Registry.Graphs.Values.ToDictionary(r => r.Id, r =>
        {
            var plan = GraphSerialization.DeserializeInstance(r.PlanJson);
            var state = Enum.TryParse<GraphInstanceState>(r.State, out var s) ? s : GraphInstanceState.Pending;
            return plan with
            {
                State = state,
                Agents = plan.Agents.Select(a => Enum.TryParse<AgentState>(r.AgentStates.GetValueOrDefault(a.AgentId), out var st) ? a with { State = st } : a).ToList(),
            };
        });
    
        private GraphInstanceRecord Record(CompiledGraph bound, GraphInstanceInfo plan, string owner)
        {
            var committed = Reservations(plan);
            return new GraphInstanceRecord
            {
                Id = plan.GraphInstanceId,
                GraphName = plan.GraphName,
                State = "Pending",
                PlanJson = GraphSerialization.Serialize(plan),
                GraphJson = GraphSerialization.Serialize(bound),
                Committed = committed,
                ExpectedHostVersions = plan.Plan.HostVersions.ToDictionary(kv => kv.Key, kv => kv.Value),
                Owner = owner,
            };
        }

    // Registry updates the graph protocol depends on (agent and graph states, stop, delete) are retried:
        // a follower can briefly know no leader, e.g. right after the cluster forms or during an election.
        private async Task<bool> UpdateReliablyAsync(RegistryAction action, CancellationToken ct = default)
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    if (await _runtime.UpdateRegistryAsync(action, ct)) return true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger.LogDebug("Registry update {Action} failed: {Error}", action.GetType().Name, ex.Message); }
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, 100 * (attempt + 1))), ct);
            }
            _logger.LogError("Registry update {Action} failed after retries", action.GetType().Name);
            return false;
        }
    
        private static async Task<string?> WaitFor(Func<string?> probe, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (probe() is string s) return s;
                await Task.Delay(50, ct);
            }
            return probe();
        }

        // --- Agent lifecycle ---

        protected override Task OnBeginAsync(CancellationToken ct)
        {
            _logger.LogInformation("GraphManager starting...");
            _controllerTimer = new Timer(_ => _ = ControlAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            return Task.CompletedTask;
        }

        protected override Task OnPauseAsync(CancellationToken ct) => Task.CompletedTask;

        protected override async Task OnEndAsync(CancellationToken ct)
        {
            _logger.LogInformation("GraphManager stopping...");
            if (_controllerTimer != null) await _controllerTimer.DisposeAsync();
        }

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in Inbox.Reader.ReadAllAsync(ct)) _logger.LogInformation("GraphManager received message.");
            }
            catch (OperationCanceledException) { }
        }
    }
}
