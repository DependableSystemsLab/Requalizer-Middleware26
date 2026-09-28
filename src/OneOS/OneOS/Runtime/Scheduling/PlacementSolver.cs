using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Google.OrTools.Sat;
using OneOS.Runtime.Language;

namespace OneOS.Runtime.Scheduling;

internal sealed record PlacementResult(string Status, long ObjectiveValue, long BestBound, IReadOnlyDictionary<string, long> Terms);

// Phases 4 and 5 (S§7, S§8): formulate the CP-SAT placement model, solve it, and on infeasibility
// find a core of constraint classes, relaxing H5/H7 into penalties when only they are in the way.
internal sealed class PlacementSolver
{
    // All objective coefficients are multiplied by this so fractional weights stay integral.
    private const long Scale = 1000;

    private readonly PlanContext _ctx;
    private readonly int H;

    public PlacementSolver(PlanContext ctx)
    {
        _ctx = ctx;
        H = ctx.HostCount;
    }

    public PlacementResult Solve(CancellationToken ct)
    {
        var relaxH5 = new HashSet<string>();
        var relaxH7 = new HashSet<string>();
        WarnUnspreadable(relaxH7);

        for (int round = 0; round < 3; round++)
        {
            var build = new ModelBuild(this, relaxH5, relaxH7, BuildMode.Optimize);
            var (status, solver) = Run(build, ct, RunMode.Optimize);
            if (status is CpSolverStatus.Optimal or CpSolverStatus.Feasible)
            {
                long objective = (long)Math.Round(solver.ObjectiveValue), bound = (long)Math.Round(solver.BestObjectiveBound);
                // Parallel search is fast but may return any of several equally good placements. Pick the
                // lexicographically first one among them, with a deterministic single-worker search.
                if (status == CpSolverStatus.Optimal && _ctx.Options.SolverWorkers > 1)
                {
                    var canon = new ModelBuild(this, relaxH5, relaxH7, BuildMode.Canonical, objective);
                    var (cstatus, csolver) = Run(canon, ct, RunMode.Canonical);
                    if (cstatus is CpSolverStatus.Optimal or CpSolverStatus.Feasible)
                        return Extract(canon, csolver, status, objective, bound);
                }
                return Extract(build, solver, status, objective, bound);
            }
            if (status == CpSolverStatus.ModelInvalid)
                throw new InvalidOperationException("invalid placement model: " + build.Model.Validate());
            if (status == CpSolverStatus.Unknown)
            {
                ct.ThrowIfCancellationRequested();
                throw new SchedulingException("SP005", $"solver time limit ({_ctx.Options.SolverTimeLimit.TotalSeconds:0.#} s) reached without a feasible solution");
            }

            // Infeasible: solve again with every constraint class behind an assumption literal (S§8.2).
            var diag = new ModelBuild(this, relaxH5, relaxH7, BuildMode.Diagnose);
            var (_, dsolver) = Run(diag, ct, RunMode.Diagnose);
            var core = dsolver.Response.SufficientAssumptionsForInfeasibility
                .Select(i => diag.Assumptions.FirstOrDefault(a => a.Index == i)).Where(a => a.Class != null).ToList();
            if (core.Count == 0 || core.Any(c => c.Class is not ("H5" or "H7")))
            {
                var classes = core.Count == 0 ? new[] { "no core found" } : core.Select(c => $"{c.Class} ({Explain(c.Class)}): {c.Subject}").Distinct().ToArray();
                throw new SchedulingException(new[] { new SpawnDiagnostic("SP007", Severity.Error, "placement model is infeasible", classes) }.Concat(_ctx.Warnings).ToList());
            }
            foreach (var c in core)
            {
                if ((c.Class == "H5" ? relaxH5 : relaxH7).Add(c.Subject))
                    _ctx.Relaxed.Add($"{c.Class}: {c.Subject}");
            }
        }
        throw new SchedulingException("SP007", "placement model is infeasible even with H5/H7 relaxed");
    }

    private static string Explain(string cls) => cls switch
    {
        "H3" => "capacity", "H4" => "agents per host", "H5" => "standby separation", "H7" => "liveness spread", "H8" => "reachability", _ => cls,
    };

    // H7 applies only when at least two hosts are eligible; otherwise it's relaxed up front (SPW03).
    private void WarnUnspreadable(HashSet<string> relaxH7)
    {
        foreach (var grp in _ctx.Groups.Where(g => g.Keyless && g.Always))
        {
            var hosts = grp.Agents.SelectMany(a => a.Eligible).Distinct().Count();
            if (hosts >= 2) continue;
            relaxH7.Add(grp.Name);
            _ctx.Warn("SPW03", $"keyless 'always' node '{grp.Name}' has only {hosts} eligible host; its instances share one host");
        }
    }

    private enum RunMode { Optimize, Diagnose, Canonical }
    private enum BuildMode { Optimize, Diagnose, Canonical }

    private (CpSolverStatus, CpSolver) Run(ModelBuild build, CancellationToken ct, RunMode mode)
    {
        var o = _ctx.Options;
        var solver = new CpSolver();
        int workers = mode == RunMode.Optimize ? Math.Max(1, o.SolverWorkers) : 1;
        var p = new List<string>
        {
            $"max_time_in_seconds:{o.SolverTimeLimit.TotalSeconds.ToString(CultureInfo.InvariantCulture)}",
            $"num_workers:{workers}",
            $"random_seed:{o.RandomSeed}",
        };
        if (mode == RunMode.Canonical) p.Add("search_branching:FIXED_SEARCH");
        solver.StringParameters = string.Join(" ", p);
        using var reg = ct.Register(() => solver.StopSearch());
        var status = solver.Solve(build.Model);
        return (status, solver);
    }

    // --- Tables (S§7.2) ---

    // LatTable_q: host index pairs → µs, with index H meaning "not placed" (latency 0).
    internal long[] LatencyTable(double pct)
    {
        var t = new long[(H + 1) * (H + 1)];
        for (int s = 0; s < H; s++)
            for (int r = 0; r < H; r++)
                t[s * (H + 1) + r] = LatencyMicros(s, r, pct);
        return t;
    }

    internal long LatencyMicros(int s, int r, double pct)
    {
        var o = _ctx.Options;
        var q = _ctx.Snapshot.Quality(_ctx.Host(s).HostId, _ctx.Host(r).HostId);
        long? measured = null;
        if (q != null && q.LatencyQuantilesMicros.Count > 0)
        {
            // The smallest measured quantile at least pct; the largest one if none is.
            var keys = q.LatencyQuantilesMicros.Keys.OrderBy(k => k).ToList();
            var k = keys.FirstOrDefault(k => k >= pct, keys[^1]);
            measured = q.LatencyQuantilesMicros[k];
        }
        if (s == r) return measured ?? o.LocalLatencyMicros;
        // Without measurements, a cross-host hop costs at least a local hop; co-location still wins.
        return (measured ?? o.LocalLatencyMicros) + o.MarshallingMicros;
    }

    // BwTable in KiB/s; null when nothing is measured (the S2 term is then omitted).
    internal long[]? BandwidthTable()
    {
        if (!_ctx.Snapshot.Network.Any(q => q.BandwidthBytesPerSec != null)) return null;
        const long Unlimited = 1L << 40;
        var t = new long[(H + 1) * (H + 1)];
        for (int s = 0; s <= H; s++)
            for (int r = 0; r <= H; r++)
            {
                if (s == H || r == H || s == r) { t[s * (H + 1) + r] = Unlimited; continue; }
                var bw = _ctx.Snapshot.Quality(_ctx.Host(s).HostId, _ctx.Host(r).HostId)?.BandwidthBytesPerSec;
                t[s * (H + 1) + r] = bw is long b ? b / 1024 : Unlimited;
            }
        return t;
    }

    // --- The model (S§7) ---

    private sealed class ModelBuild
    {
        public readonly CpModel Model = new();
        public readonly Dictionary<AgentDraft, Dictionary<int, BoolVar>> X = new();
        public readonly Dictionary<AgentDraft, BoolVar> Act = new();
        public readonly Dictionary<AgentDraft, IntVar> Hv = new();
        public readonly List<(int Index, string Class, string Subject)> Assumptions = new();
        public readonly Dictionary<string, LinearExprBuilder> Terms = new();
        public readonly Dictionary<LatencyPath, IntVar> Excess = new();
        public readonly Dictionary<PipeDraft, IntVar> Shortfall = new();
        public readonly List<(AgentDraft Standby, BoolVar Violation)> H5Violations = new();
        public readonly List<BoolVar> ForwardSplits = new();

        private readonly PlacementSolver _s;
        private readonly PlanContext _ctx;
        private readonly bool _withAssumptions;

        private readonly BuildMode _mode;
        private readonly long _objectiveCap;

        public ModelBuild(PlacementSolver s, HashSet<string> relaxH5, HashSet<string> relaxH7, BuildMode mode, long objectiveCap = 0)
        {
            _s = s;
            _ctx = s._ctx;
            _mode = mode;
            _objectiveCap = objectiveCap;
            _withAssumptions = mode == BuildMode.Diagnose;
            foreach (var t in new[] { "S1", "S2", "S3", "S4", "S5", "S6" }) Terms[t] = LinearExpr.NewBuilder();
            BuildPlacement();
            BuildCapacity();
            BuildStandbys(relaxH5);
            BuildCounts();
            BuildSpread(relaxH7);
            BuildReachability();
            BuildObjective();
        }

        private int H => _s.H;

        // Returns the enforcement literal for a constraint class, or null when constraints are hard.
        private ILiteral? Guard(string cls, string subject)
        {
            if (!_withAssumptions) return null;
            var lit = Model.NewBoolVar($"assume_{cls}_{subject}");
            Model.AddAssumption(lit);
            Assumptions.Add((lit.GetIndex(), cls, subject));
            return lit;
        }

        private void Enforce(Constraint c, ILiteral? lit) { if (lit != null) c.OnlyEnforceIf(lit); }

        // H1, H2 and channeling: x[a,h] exists only for eligible hosts; Σ_h x = act; hv = host index or H.
        private void BuildPlacement()
        {
            foreach (var a in _ctx.Agents)
            {
                var xs = a.Eligible.ToDictionary(h => h, h => Model.NewBoolVar($"x_{a.Describe}_{h}"));
                X[a] = xs;
                var act = Model.NewBoolVar($"act_{a.Describe}");
                Act[a] = act;
                if (!a.Candidate) Model.Add(act == 1);
                if (xs.Count == 0) Model.Add(act == 0);
                Model.Add(LinearExpr.Sum(xs.Values) == act);
                var hv = Model.NewIntVar(0, H, $"hv_{a.Describe}");
                Hv[a] = hv;
                var sum = LinearExpr.NewBuilder();
                foreach (var (h, x) in xs) sum.AddTerm(x, h);
                sum.Add(H).AddTerm(act, -H);
                Model.Add(hv == sum);
            }
        }

        // H3 capacity and H4 agents per host. Memory is counted in MiB to keep coefficients small.
        private void BuildCapacity()
        {
            for (int h = 0; h < H; h++)
            {
                var host = _ctx.Host(h);
                if (!host.Alive) continue;
                var placed = _ctx.Agents.Where(a => X[a].ContainsKey(h)).ToList();
                if (placed.Count == 0) continue;
                var counted = placed.Where(a => a.Role == AgentRole.Primary || _ctx.Options.ReserveStandbyCapacity).ToList();

                var lit = Guard("H3", host.HostId);
                var cpu = LinearExpr.NewBuilder();
                var mem = LinearExpr.NewBuilder();
                foreach (var a in counted)
                {
                    cpu.AddTerm(X[a][h], a.Demand.CpuMillis);
                    mem.AddTerm(X[a][h], Mib(a.Demand.MemoryBytes));
                }
                Enforce(Model.Add(cpu <= Math.Max(0, host.Free.CpuMillis)), lit);
                Enforce(Model.Add(mem <= Math.Max(0, host.Free.MemoryBytes >> 20)), lit);

                if (host.MaxAgents is int max)
                    Enforce(Model.Add(LinearExpr.Sum(placed.Select(a => X[a][h])) <= max), Guard("H4", host.HostId));

                // S4 load: util[h] (per mille) ≥ usage / capacity for each resource.
                if (host.Capacity.CpuMillis > 0 && host.Capacity.MemoryBytes > 0)
                {
                    var util = Model.NewIntVar(0, 1_000_000, $"util_{h}");
                    var cpuUse = LinearExpr.NewBuilder().Add(host.Committed.CpuMillis * 1000);
                    var memUse = LinearExpr.NewBuilder().Add(Mib(host.Committed.MemoryBytes) * 1000);
                    foreach (var a in counted)
                    {
                        cpuUse.AddTerm(X[a][h], a.Demand.CpuMillis * 1000);
                        memUse.AddTerm(X[a][h], Mib(a.Demand.MemoryBytes) * 1000);
                    }
                    Model.Add(LinearExpr.Term(util, host.Capacity.CpuMillis) >= cpuUse);
                    Model.Add(LinearExpr.Term(util, Math.Max(1, Mib(host.Capacity.MemoryBytes))) >= memUse);
                    _utils.Add(util);
                }
            }
        }

        private readonly List<IntVar> _utils = new();

        // H5 standby separation, or a penalty when relaxed. Same-zone standbys are penalized (S5).
        private void BuildStandbys(HashSet<string> relax)
        {
            foreach (var s in _ctx.Agents.Where(a => a.Role == AgentRole.Standby))
            {
                var p = s.Primary!;
                var shared = X[s].Keys.Intersect(X[p].Keys).ToList();
                if (relax.Contains(p.Node.Name))
                {
                    var v = Model.NewBoolVar($"h5v_{s.Describe}");
                    foreach (var h in shared) Model.Add(v >= X[p][h] + X[s][h] - 1);
                    Terms["S5"].AddTerm(v, _s.Coef(_ctx.Options.Weights.RelaxedViolation, 1));
                    H5Violations.Add((s, v));
                }
                else
                {
                    var lit = Guard("H5", p.Node.Name);
                    foreach (var h in shared) Enforce(Model.Add(X[p][h] + X[s][h] <= 1), lit);
                }

                var zones = X[s].Keys.Concat(X[p].Keys).Select(h => _ctx.Host(h).Zone).Where(z => z != null).Distinct().ToList();
                if (zones.Count == 0) continue;
                var sameZone = Model.NewBoolVar($"zone_{s.Describe}");
                foreach (var z in zones)
                {
                    LinearExpr InZone(AgentDraft a) => LinearExpr.Sum(X[a].Where(kv => _ctx.Host(kv.Key).Zone == z).Select(kv => kv.Value));
                    Model.Add(sameZone >= InZone(p) + InZone(s) - 1);
                }
                Terms["S5"].AddTerm(sameZone, _s.Coef(_ctx.Options.Weights.SameZoneStandby, 1));
            }
        }

        // H6: candidates activate in order; symmetry breaking within interchangeable keyless groups.
        private void BuildCounts()
        {
            foreach (var grp in _ctx.Groups)
            {
                var agents = grp.Agents;
                for (int i = 0; i + 1 < agents.Count; i++)
                {
                    if (agents[i].Candidate || agents[i + 1].Candidate)
                        Model.Add(Act[agents[i]] >= Act[agents[i + 1]]);
                    if (grp.Keyless && grp.Interchangeable)
                        Model.Add(Hv[agents[i]] <= Hv[agents[i + 1]]);
                }
                foreach (var a in agents.Where(a => a.Candidate)) Terms["S6"].AddTerm(Act[a], _s.Coef(_ctx.Options.Weights.Instance, 1));
            }
        }

        // H7: keyless `always` groups use at least two distinct hosts (or a penalty when relaxed).
        private void BuildSpread(HashSet<string> relax)
        {
            foreach (var grp in _ctx.Groups.Where(g => g.Keyless && g.Always))
            {
                var hosts = grp.Agents.SelectMany(a => X[a].Keys).Distinct().OrderBy(h => h).ToList();
                if (hosts.Count < 2) continue;
                var used = new List<BoolVar>();
                foreach (var h in hosts)
                {
                    var y = Model.NewBoolVar($"used_{grp.Name}_{h}");
                    Model.Add(y <= LinearExpr.Sum(grp.Agents.Where(a => X[a].ContainsKey(h)).Select(a => X[a][h])));
                    used.Add(y);
                }
                if (relax.Contains(grp.Name))
                {
                    var v = Model.NewIntVar(0, 2, $"h7v_{grp.Name}");
                    Model.Add(v >= 2 - LinearExpr.Sum(used));
                    Terms["S5"].AddTerm(v, _s.Coef(_ctx.Options.Weights.RelaxedViolation, 1));
                }
                else Enforce(Model.Add(LinearExpr.Sum(used) >= 2), Guard("H7", grp.Name));
            }
        }

        // H8: a pipe's endpoints must be on hosts that can reach each other.
        private void BuildReachability()
        {
            var unreachable = _ctx.Snapshot.Network.Where(q => !q.Reachable).ToList();
            if (unreachable.Count == 0) return;
            var index = Enumerable.Range(0, H).ToDictionary(h => _ctx.Host(h).HostId);
            foreach (var edge in _ctx.Pipes.GroupBy(p => p.Edge.Name))
            {
                var lit = Guard("H8", edge.Key);
                foreach (var p in edge)
                    foreach (var q in unreachable)
                        if (index.TryGetValue(q.From, out var hs) && index.TryGetValue(q.To, out var hr)
                            && X[p.Source].TryGetValue(hs, out var xs) && X[p.Destination].TryGetValue(hr, out var xr))
                            Enforce(Model.Add(xs + xr <= 1), lit);
            }
        }

        // S1–S6 (S§7.4).
        private void BuildObjective()
        {
            var w = _ctx.Options.Weights;
            var tables = new Dictionary<double, long[]>();
            var lat = new Dictionary<PipeDraft, IntVar>();
            IntVar Idx(PipeDraft p)
            {
                var idx = Model.NewIntVar(0, (H + 1) * (H + 1) - 1, "");
                Model.Add(idx == LinearExpr.Term(Hv[p.Source], H + 1) + Hv[p.Destination]);
                return idx;
            }

            // S1: flow latency excess per node-level path.
            var edgeMax = new Dictionary<string, IntVar>();
            foreach (var p in _ctx.Pipes.Where(p => p.LatencyPercentile != null))
            {
                var pct = p.LatencyPercentile!.Value;
                if (!tables.TryGetValue(pct, out var table)) tables[pct] = table = _s.LatencyTable(pct);
                var l = Model.NewIntVar(0, table.Max(), "");
                Model.AddElement(Idx(p), table, l);
                lat[p] = l;
            }
            foreach (var path in _ctx.LatencyPaths)
            {
                var total = LinearExpr.NewBuilder().Add(path.FixedMicros);
                long worst = path.FixedMicros;
                foreach (var e in path.Edges)
                {
                    if (!edgeMax.TryGetValue(e.Name, out var m))
                    {
                        var pipes = _ctx.Pipes.Where(p => p.Edge == e && lat.ContainsKey(p)).ToList();
                        m = Model.NewIntVar(0, pipes.Count == 0 ? 0 : pipes.Max(p => tables[p.LatencyPercentile!.Value].Max()), "");
                        foreach (var p in pipes) Model.Add(m >= lat[p]);
                        edgeMax[e.Name] = m;
                    }
                    total.Add(m);
                    worst += m.Domain.Max();
                }
                var excess = Model.NewIntVar(0, Math.Max(0, worst - path.BoundMicros), $"excess_{path.Owner}");
                Model.Add(excess >= total - path.BoundMicros);
                Excess[path] = excess;
                Terms["S1"].AddTerm(excess, _s.Coef(w.FlowLatencyPerMs, 1000));
            }

            // S2 and S3 for pipes with a known demand.
            var bw = _s.BandwidthTable();
            foreach (var p in _ctx.Pipes.Where(p => p.Bandwidth is > 0))
            {
                long demKib = (p.Bandwidth!.Value + 1023) / 1024;
                if (bw != null)
                {
                    var avail = Model.NewIntVar(0, bw.Max(), "");
                    Model.AddElement(Idx(p), bw, avail);
                    var shortfall = Model.NewIntVar(0, demKib, "");
                    Model.Add(shortfall >= demKib - avail);
                    Shortfall[p] = shortfall;
                    Terms["S2"].AddTerm(shortfall, _s.Coef(w.BandwidthShortfallPerMBps, 1024));
                }
                var differ = Model.NewBoolVar("");
                Model.Add(Hv[p.Source] != Hv[p.Destination]).OnlyEnforceIf(differ);
                Model.Add(Hv[p.Source] == Hv[p.Destination]).OnlyEnforceIf(differ.Not());
                var cross = Model.NewBoolVar("");
                Model.Add(cross >= differ + Act[p.Source] + Act[p.Destination] - 2);
                Terms["S3"].AddTerm(cross, demKib * _s.Coef(w.CrossHostPerMBps, 1024));
            }

            // S3, forwarding hint (L§6.5): on a forward-eligible edge, sender instance i and receiver instance i
            // own the same key groups, so most messages stay on that pair. Prefer placing each pair together.
            foreach (var p in ForwardPairs(_ctx))
            {
                var split = Model.NewBoolVar("");
                Model.Add(Hv[p.Source] != Hv[p.Destination]).OnlyEnforceIf(split);
                Model.Add(Hv[p.Source] == Hv[p.Destination]).OnlyEnforceIf(split.Not());
                ForwardSplits.Add(split);
                Terms["S3"].AddTerm(split, _s.Coef(w.ForwardPairSplit, 1));
            }

            // S4: the busiest host's utilization.
            if (_utils.Count > 0)
            {
                var max = Model.NewIntVar(0, 1_000_000, "max_util");
                Model.AddMaxEquality(max, _utils);
                Terms["S4"].AddTerm(max, _s.Coef(w.LoadPerMille, 1));
                _maxUtil = max;
            }

            var objective = LinearExpr.NewBuilder();
            foreach (var t in Terms.Values) objective.Add(t);
            switch (_mode)
            {
                case BuildMode.Optimize:
                    Model.Minimize(objective);
                    break;
                case BuildMode.Canonical:
                    // Any placement as good as the optimum; the first one in a fixed variable and value order.
                    Model.Add(objective <= _objectiveCap);
                    Model.AddDecisionStrategy(_ctx.Agents.Select(a => Hv[a]), DecisionStrategyProto.Types.VariableSelectionStrategy.ChooseFirst,
                        DecisionStrategyProto.Types.DomainReductionStrategy.SelectMinValue);
                    break;
                // Diagnose: feasibility only.
            }
        }

        private IntVar? _maxUtil;
        public IntVar? MaxUtil => _maxUtil;
    }

    private static long Mib(long bytes) => (bytes + (1 << 20) - 1) >> 20;

    // Integer objective coefficient for a weight given per `perUnit` model units.
    // Pipes between the matching primaries of a forward-eligible edge: same key groups and, for compartment
    // lanes (F3), the same compartment.
    internal static IEnumerable<PipeDraft> ForwardPairs(PlanContext ctx) => ctx.Pipes.Where(p => p.Edge.ForwardEligible
        && p.Source.Role == AgentRole.Primary && p.Destination.Role == AgentRole.Primary
        && p.Source.OwnedKeys != null && p.Source.OwnedKeys == p.Destination.OwnedKeys
        && (p.Source.Lane is int a ? ctx.Graph.Lattice.Compartments[a] : -1) == (p.Destination.Lane is int b ? ctx.Graph.Lattice.Compartments[b] : -1));

    private long Coef(double weight, double perUnit) => weight <= 0 ? 0 : Math.Max(1, (long)Math.Round(weight * Scale / perUnit));

    // --- Extraction (S§8.3) ---

    private PlacementResult Extract(ModelBuild b, CpSolver solver, CpSolverStatus status, long objective, long bound)
    {
        foreach (var a in _ctx.Agents)
        {
            a.Active = solver.BooleanValue(b.Act[a]);
            a.Host = a.Active ? b.X[a].First(kv => solver.BooleanValue(kv.Value)).Key : -1;
        }
        foreach (var p in _ctx.Pipes.Where(p => p.Source.Active && p.Destination.Active))
            p.ExpectedLatencyMicros = LatencyMicros(p.Source.Host, p.Destination.Host, p.LatencyPercentile ?? 99);

        if (status == CpSolverStatus.Feasible)
            _ctx.Warn("SPW04", "the placement is feasible but not proven optimal (solver time limit reached)");

        foreach (var path in _ctx.LatencyPaths)
        {
            long total = path.FixedMicros;
            foreach (var e in path.Edges)
                total += _ctx.Pipes.Where(p => p.Edge == e && p.ExpectedLatencyMicros != null).Select(p => p.ExpectedLatencyMicros!.Value).DefaultIfEmpty(0).Max();
            path.ExpectedMicros = total;
        }
        foreach (var owner in _ctx.LatencyPaths.Where(p => p.ExpectedMicros > p.BoundMicros).GroupBy(p => p.Owner))
        {
            var worst = owner.MaxBy(p => p.ExpectedMicros)!;
            _ctx.Warn("SPW05", $"latency goal of {Describe(owner.Key)} is likely missed: expected {worst.ExpectedMicros} µs, bound {worst.BoundMicros} µs",
                $"path: {string.Join(" -> ", worst.NodePath)}");
        }
        foreach (var edge in b.Shortfall.Where(kv => solver.Value(kv.Value) > 0).GroupBy(kv => kv.Key.Edge.Name))
            _ctx.Warn("SPW06", $"rate goal on edge '{edge.Key}' is likely missed: the available bandwidth is below its demand");
        foreach (var (s, v) in b.H5Violations.Where(x => solver.BooleanValue(x.Violation)))
            _ctx.Warn("SPW02", $"standby of {s.Primary!.Describe} is placed on its primary's host ({_ctx.Host(s.Host).HostId})");
        foreach (var grp in _ctx.Groups.Where(g => g.Keyless && g.Always && _ctx.Relaxed.Contains($"H7: {g.Name}")))
            if (grp.Agents.Where(a => a.Active).Select(a => a.Host).Distinct().Count() < 2)
                _ctx.Warn("SPW03", $"keyless 'always' node '{grp.Name}' is placed on a single host");

        var terms = new Dictionary<string, long>
        {
            ["S1_latency_excess_us"] = b.Excess.Values.Sum(v => solver.Value(v)),
            ["S2_bandwidth_shortfall_kibps"] = b.Shortfall.Values.Sum(v => solver.Value(v)),
            ["S3_cross_host_kibps"] = _ctx.Pipes.Where(p => p.Bandwidth is > 0 && p.Source.Active && p.Destination.Active && p.Source.Host != p.Destination.Host)
                .Sum(p => (p.Bandwidth!.Value + 1023) / 1024),
            ["S3_forward_pairs_split"] = b.ForwardSplits.Count(v => solver.BooleanValue(v)),
            ["S4_max_util_permille"] = b.MaxUtil != null ? solver.Value(b.MaxUtil) : 0,
            ["S6_active_candidates"] = _ctx.Agents.Count(a => a.Candidate && a.Active),
        };
        return new PlacementResult(status == CpSolverStatus.Optimal ? "OPTIMAL" : "FEASIBLE",
            (long)Math.Round((double)objective / Scale), (long)Math.Round((double)bound / Scale), terms);
    }

    private static string Describe(string owner) => owner.StartsWith("edge:", StringComparison.Ordinal) ? $"edge '{owner[5..]}'" : $"flow '{owner}'";
}
