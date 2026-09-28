using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Runtime.Graphs;
using OneOS.Runtime.Language.Models;
using OneOS.Runtime.Scheduling;

namespace OneOS.Runtime
{
    // Tier-0 hosting of dataflow graph agents: this runtime runs its part of every committed graph
    // instance (its primaries, and the checkpoint store of the standbys placed here), reacting to the
    // Registry's graph actions. Registry writes about them go through the tier-1 GraphManager.
    public partial class ExecutionManager
    {
        private readonly ConcurrentDictionary<string, GraphExecutor> _graphs = new();
        private readonly ConcurrentDictionary<string, (long R, long D, long E, DateTime At)> _lastCounts = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _reportLocks = new();
        private Timer? _metricsTimer;

        // How often stateful primaries with a standby send it a checkpoint (L§9.3); null disables checkpoints.
        public TimeSpan? CheckpointInterval { get; set; } = TimeSpan.FromSeconds(10);

        // The latest checkpoint this host holds for a primary whose standby it runs (diagnostics).
        public AgentCheckpoint? CheckpointOf(string graphId, string primaryId) =>
            _graphs.TryGetValue(graphId, out var e) ? e.LatestCheckpoint(primaryId) : null;

        public void HandleGraphAction(RegistryAction action)
        {
            switch (action)
            {
                case CommitGraphAction c when _runtime.Registry.Graphs.TryGetValue(c.Record.Id, out var record):
                    _ = StartGraphAsync(record);
                    break;
                case UpdateGraphPlanAction u when _runtime.Registry.Graphs.TryGetValue(u.GraphId, out var rec) && rec.PlanVersion == u.BasePlanVersion + 1:
                    _ = ApplyGraphPlanAsync(rec);
                    break;
                case SetGraphStateAction { State: "Stopping" } s:
                    _ = StopGraphAsync(s.GraphId, report: true);
                    break;
                case DeleteGraphAction d:
                    _ = StopGraphAsync(d.GraphId, report: false);
                    break;
            }
        }

        // After a restart: run the local agents of graph instances that should be running.
        private async Task SynchronizeGraphsWithRegistry()
        {
            foreach (var record in _runtime.Registry.Graphs.Values.Where(r => r.State is "Pending" or "Running").ToList())
                await StartGraphAsync(record);
        }

        private async Task StartGraphAsync(GraphInstanceRecord record)
        {
            try
            {
                var plan = GraphSerialization.DeserializeInstance(record.PlanJson);
                var me = _runtime.Config.ID;
                // Hosts with only standbys run an executor too: it keeps their primaries' checkpoints.
                if (!plan.Agents.Any(a => a.HostId == me)) return;
                if (_graphs.ContainsKey(record.Id)) return;

                var graph = GraphSerialization.DeserializeGraph(record.GraphJson);
                var executor = new GraphExecutor(graph, plan, me, _runtime.Config.Label, this, this,
                    e => _logger.LogWarning("Sidecar {Agent}: {Kind}: {Detail}", e.AgentId, e.Kind, e.Detail),
                    (level, msg) => _logger.Log(level switch { "error" => LogLevel.Error, "warning" => LogLevel.Warning, _ => LogLevel.Information }, "{Message}", msg),
                    CheckpointInterval,
                    agent => _runtime.Registry.Graphs.TryGetValue(record.Id, out var r) ? r.Taints.GetValueOrDefault(agent) : null,
                    _runtime.Metrics);
                if (!_graphs.TryAdd(record.Id, executor)) return;
                executor.AgentStateChanged += r => _ = ReportAgentStateAsync(record.Id, r);
                executor.TaintRaised += (r, taint, rank) =>
                {
                    if (executor.Instance.Agent(r.AgentId).Kind == PartitionKind.Keyless) return;   // stateless: no failover
                    _ = _runtime.GraphManager.ReportTaintAsync(record.Id, r.AgentId, taint, rank);
                };
                _logger.LogInformation("Starting {Count} agents of graph instance {Id}", executor.Runners.Count, record.Id);
                await executor.StartAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start the local agents of graph instance {Id}", record.Id);
                await _runtime.GraphManager.ReportHostFailureAsync(record.Id, ex.Message);
            }
        }

        // A new plan version: reconcile this host's agents, or start them if this host just got some.
        private async Task ApplyGraphPlanAsync(GraphInstanceRecord record)
        {
            try
            {
                if (!_graphs.TryGetValue(record.Id, out var executor)) { await StartGraphAsync(record); return; }
                var plan = GraphSerialization.DeserializeInstance(record.PlanJson);
                _logger.LogInformation("Applying plan v{Version} of graph instance {Id}", record.PlanVersion, record.Id);
                await executor.ApplyPlanAsync(plan);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply plan v{Version} of graph instance {Id}", record.PlanVersion, record.Id);
            }
        }

        private async Task StopGraphAsync(string id, bool report)
        {
            if (!_graphs.TryRemove(id, out var executor)) return;
            try { await executor.StopAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to stop the local agents of graph instance {Id}", id); }
            if (!report) return;
            foreach (var r in executor.Runners)
                await _runtime.GraphManager.ReportAgentStateAsync(id, r.AgentId, Scheduling.AgentState.Stopped);
        }

        private async Task StopAllGraphsAsync()
        {
            foreach (var id in _graphs.Keys.ToList())
                if (_graphs.TryRemove(id, out var executor))
                    try { await executor.StopAsync(TimeSpan.FromSeconds(2)); }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to stop the local agents of graph instance {Id}", id); }
        }

        // Agent state reports are serialized per agent and read the state when sent, so an older state can't
        // overwrite a newer one (Starting and Running are raised back to back).
        private async Task ReportAgentStateAsync(string graphId, GraphAgentRunner runner)
        {
            var gate = _reportLocks.GetOrAdd($"{graphId}|{runner.AgentId}", _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try { await _runtime.GraphManager.ReportAgentStateAsync(graphId, runner.AgentId, runner.State); }
            finally { gate.Release(); }
        }

        // Per-agent rates for monitoring and scaling (every 5 s).
        private async Task ReportGraphMetricsAsync()
        {
            foreach (var (id, executor) in _graphs)
            {
                var metrics = new Dictionary<string, double[]>();
                var now = DateTime.UtcNow;
                foreach (var r in executor.Runners)
                {
                    var (rec, del, emi, q) = r.Metrics();
                    var last = _lastCounts.GetValueOrDefault(r.AgentId, (0, 0, 0, now));
                    double dt = Math.Max(0.001, (now - last.At).TotalSeconds);
                    if (last.At != now) metrics[r.AgentId] = new[] { (rec - last.R) / dt, (del - last.D) / dt, (emi - last.E) / dt, q };
                    _lastCounts[r.AgentId] = (rec, del, emi, now);
                }
                if (metrics.Count > 0) await _runtime.GraphManager.ReportMetricsAsync(id, metrics);
            }
        }
    }
}
