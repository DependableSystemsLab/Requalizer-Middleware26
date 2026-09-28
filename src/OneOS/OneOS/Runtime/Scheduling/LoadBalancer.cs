using System;
using System.Collections.Generic;
using System.Linq;
using Google.OrTools.LinearSolver;

namespace OneOS.Runtime.Scheduling;

// A routing table for one sender agent's output port on one load-balanced edge (S§10.2).
// A message with label Labels[l] goes to ReceiverAgentIds[s] with probability Weights[l][s] / p_l,
// or uniformly over the active routes (Active[l][s]) when p_l = 0. Labels in Unroutable have no
// compliant receiver: their messages queue at the sender (`no_compliant_route`).
public sealed record RoutingTableInfo(
    string SourceAgentId, string SourcePort, string EdgeName,
    IReadOnlyList<string> Labels, IReadOnlyList<string> ReceiverAgentIds,
    IReadOnlyList<IReadOnlyList<double>> Weights,   // w[l][s]
    IReadOnlyList<IReadOnlyList<bool>> Active,      // f[l][s]
    int Redundancy,                                  // c
    IReadOnlyList<string> Unroutable,
    double Deviation,                                // Σ δ_s
    DateTimeOffset SolvedAt, string Trigger,         // spawn | backpressure | membership | drift
    long Version)
{
    // The receivers a message with this label may go to, with their probabilities.
    public IEnumerable<(string Receiver, double Probability)> Routes(string label)
    {
        int l = Labels.ToList().IndexOf(label);
        if (l < 0) yield break;
        double total = Weights[l].Sum();
        int active = Active[l].Count(a => a);
        for (int s = 0; s < ReceiverAgentIds.Count; s++)
        {
            if (!Active[l][s]) continue;
            yield return (ReceiverAgentIds[s], total > 0 ? Weights[l][s] / total : 1.0 / active);
        }
    }
}

// One load-balancing problem: labels on the edge with their frequencies, receivers with the labels
// each may be sent (Allowed) and their target share, and the redundancy target.
public sealed record LoadBalanceProblem(
    string SourceAgentId, string SourcePort, string EdgeName,
    IReadOnlyList<string> Labels,
    IReadOnlyDictionary<string, double> Frequency,               // p_l, normalized over Labels
    IReadOnlyList<string> Receivers,
    IReadOnlyDictionary<string, IReadOnlySet<string>> Allowed,   // receiver → labels (S§5.2)
    IReadOnlyDictionary<string, double> TargetShare,             // q_s, normalized over Receivers
    int Redundancy);

public static class LoadBalancer
{
    // Target shares inversely proportional to each receiver's latency (S§10.2), normalized.
    public static Dictionary<string, double> SharesFromLatency(IReadOnlyDictionary<string, long> latencyMicros)
    {
        var inv = latencyMicros.ToDictionary(kv => kv.Key, kv => 1.0 / Math.Max(1, kv.Value));
        double sum = inv.Values.Sum();
        return inv.ToDictionary(kv => kv.Key, kv => sum > 0 ? kv.Value / sum : 1.0 / inv.Count);
    }

    // Requalizer Algorithm 1: minimize Σ δ_s subject to input conservation, target deviation,
    // DIFT compliance (C1), route binding and per-label redundancy c_l = min(c, compliant receivers) (C3).
    public static RoutingTableInfo Solve(LoadBalanceProblem p, string trigger, TimeSpan budget, long version = 1)
    {
        var receivers = p.Receivers;
        var routable = p.Labels.Where(l => receivers.Any(r => p.Allowed[r].Contains(l))).ToList();
        var unroutable = p.Labels.Except(routable).ToList();

        var solver = Solver.CreateSolver("SCIP") ?? Solver.CreateSolver("CBC")
            ?? throw new InvalidOperationException("no MILP backend (SCIP or CBC) is available");
        solver.SetTimeLimit((long)Math.Max(1, budget.TotalMilliseconds));

        // Frequencies renormalized over the routable labels; unroutable traffic queues and carries no load.
        double routableMass = routable.Sum(l => p.Frequency.GetValueOrDefault(l));
        double P(string l) => routableMass > 0 ? p.Frequency.GetValueOrDefault(l) / routableMass : 1.0 / Math.Max(1, routable.Count);

        var w = new Dictionary<(string, string), Variable>();
        var f = new Dictionary<(string, string), Variable>();
        foreach (var l in routable)
            foreach (var r in receivers.Where(r => p.Allowed[r].Contains(l)))     // C1: no variables for non-compliant routes
            {
                w[(l, r)] = solver.MakeNumVar(0, 1, $"w_{l}_{r}");
                f[(l, r)] = solver.MakeBoolVar($"f_{l}_{r}");
                solver.Add(w[(l, r)] <= f[(l, r)]);                             // route binding
            }

        foreach (var l in routable)
        {
            var routes = receivers.Where(r => w.ContainsKey((l, r))).ToList();
            solver.Add(Sum(routes.Select(r => w[(l, r)])) == P(l));             // input conservation
            int c = Math.Min(p.Redundancy, routes.Count);
            solver.Add(Sum(routes.Select(r => f[(l, r)])) >= c);                // C3: redundancy
        }

        var delta = new Dictionary<string, Variable>();
        foreach (var r in receivers)
        {
            delta[r] = solver.MakeNumVar(0, double.PositiveInfinity, $"d_{r}");
            var load = Sum(routable.Where(l => w.ContainsKey((l, r))).Select(l => w[(l, r)]));
            double q = p.TargetShare.GetValueOrDefault(r);
            solver.Add(q - load <= delta[r]);
            solver.Add(load - q <= delta[r]);
        }
        var objective = solver.Objective();
        foreach (var d in delta.Values) objective.SetCoefficient(d, 1);
        objective.SetMinimization();

        var status = solver.Solve();
        if (status is not (Solver.ResultStatus.OPTIMAL or Solver.ResultStatus.FEASIBLE))
            throw new InvalidOperationException($"load-balancing model for {p.SourceAgentId}.{p.SourcePort} ({p.EdgeName}) is {status}");

        var weights = p.Labels.Select(l => (IReadOnlyList<double>)receivers.Select(r => w.TryGetValue((l, r), out var v) ? Math.Max(0, v.SolutionValue()) : 0).ToList()).ToList();
        var active = p.Labels.Select(l => (IReadOnlyList<bool>)receivers.Select(r => f.TryGetValue((l, r), out var v) && v.SolutionValue() > 0.5).ToList()).ToList();
        return new RoutingTableInfo(p.SourceAgentId, p.SourcePort, p.EdgeName, p.Labels, receivers, weights, active,
            p.Redundancy, unroutable, objective.Value(), DateTimeOffset.UtcNow, trigger, version);
    }

    private static LinearExpr Sum(IEnumerable<Variable> vars)
    {
        LinearExpr e = new LinearExpr();
        foreach (var v in vars) e += v;
        return e;
    }
}
