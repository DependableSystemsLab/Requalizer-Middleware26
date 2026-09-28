using OneOS.Runtime.Scheduling;

namespace OneOS.Tests.Scheduling;

// S§10.2 and scheduling-spec §14 scenarios 7–9.
public class LoadBalancerTests
{
    private static readonly string[] Labels = { "low", "med", "high" };

    // Lanes low, med, high with two instances each; a lane-λ instance accepts labels ⊑ λ.
    private static LoadBalanceProblem Lanes(IReadOnlyDictionary<string, long>? latency = null, params string[] dead)
    {
        var receivers = new[] { "low/0", "low/1", "med/0", "med/1", "high/0", "high/1" }.Except(dead).ToList();
        var allowed = receivers.ToDictionary(r => r, r => (IReadOnlySet<string>)(r.StartsWith("low") ? new HashSet<string> { "low" }
            : r.StartsWith("med") ? new HashSet<string> { "low", "med" } : new HashSet<string> { "low", "med", "high" }));
        var shares = latency == null ? receivers.ToDictionary(r => r, _ => 1.0 / receivers.Count)
            : LoadBalancer.SharesFromLatency(receivers.ToDictionary(r => r, r => latency.GetValueOrDefault(r, 100)));
        return new LoadBalanceProblem("src/0", "out", "e", Labels, Labels.ToDictionary(l => l, _ => 1.0 / 3), receivers, allowed, shares, Redundancy: 2);
    }

    private static double W(RoutingTableInfo t, string label, string receiver)
    {
        int l = t.Labels.ToList().IndexOf(label), s = t.ReceiverAgentIds.ToList().IndexOf(receiver);
        return s < 0 ? 0 : t.Weights[l][s];
    }

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public void Scenario7_RedundantCompliantRoutesWithMinimalDeviation()
    {
        var t = LoadBalancer.Solve(Lanes(), "spawn", Budget);
        for (int l = 0; l < Labels.Length; l++) Assert.True(t.Active[l].Count(a => a) >= 2, $"label {Labels[l]} has fewer than 2 routes");
        foreach (var r in new[] { "low/0", "low/1", "med/0", "med/1" }) Assert.Equal(0, W(t, "high", r));
        foreach (var r in new[] { "low/0", "low/1" }) Assert.Equal(0, W(t, "med", r));
        Assert.Equal(0, t.Deviation, 6);                                   // every lane can get exactly its share
        foreach (var label in Labels) Assert.Equal(1.0, t.Routes(label).Sum(x => x.Probability), 6);
        Assert.Empty(t.Unroutable);
    }

    [Fact]
    public void Scenario8_CongestionShiftsHighTrafficWithinTheHighLaneOnly()
    {
        var before = LoadBalancer.Solve(Lanes(), "spawn", Budget);
        var after = LoadBalancer.Solve(Lanes(new Dictionary<string, long> { ["high/0"] = 2000 }), "backpressure", Budget);
        Assert.True(W(after, "high", "high/1") > W(after, "high", "high/0"));
        Assert.True(W(after, "high", "high/0") < W(before, "high", "high/0") + 1e-9);
        foreach (var r in new[] { "low/0", "low/1", "med/0", "med/1" }) Assert.Equal(0, W(after, "high", r));
        Assert.True(after.Active[2].Count(a => a) >= 2);                  // still two routes for high
        Assert.Equal("backpressure", after.Trigger);
    }

    [Fact]
    public void Scenario9_LaneFailureRoutesUpwardAndQueuesNothing()
    {
        var t = LoadBalancer.Solve(Lanes(null, "med/0", "med/1"), "membership", Budget);
        Assert.Empty(t.Unroutable);
        Assert.Equal(1.0, t.Routes("med").Sum(x => x.Probability), 6);
        Assert.All(t.Routes("med"), x => Assert.StartsWith("high/", x.Receiver));
    }

    [Fact]
    public void LabelsWithoutACompliantRouteQueue()
    {
        var t = LoadBalancer.Solve(Lanes(null, "high/0", "high/1"), "membership", Budget);
        Assert.Equal(new[] { "high" }, t.Unroutable);
        Assert.Empty(t.Routes("high"));
        Assert.Equal(1.0, t.Routes("low").Sum(x => x.Probability), 6);
    }

    [Fact]
    public void SpawnComputesTablesForLoadBalancedEdges()
    {
        var cluster = Sched.Cluster(Sched.Host("p1", "public"), Sched.Host("p2", "public"), Sched.Host("i1", "internal"), Sched.Host("i2", "internal"), Sched.Host("s1", "secret"), Sched.Host("s2", "secret"));
        var i = Sched.Plan(Sched.Example("camera.osh", "foo"), cluster, new object?[] { "a", "b" });
        // det → anon (e3) is load-balanced: one table per det instance, over the six lane instances.
        var tables = i.RoutingTables.Where(t => t.EdgeName == "e3").ToList();
        Assert.Equal(4, tables.Count);
        foreach (var t in tables)
        {
            Assert.Equal(6, t.ReceiverAgentIds.Count);
            Assert.Equal(2, t.Redundancy);                                 // anon has `always`
            Assert.All(t.Routes("secret"), x => Assert.Contains("anon@secret", x.Receiver));
            Assert.All(t.Routes("internal"), x => Assert.DoesNotContain("anon@public", x.Receiver));
            Assert.Equal("spawn", t.Trigger);
        }
        Assert.DoesNotContain(i.RoutingTables, t => t.EdgeName is "e1" or "e4");   // keyed and direct edges don't use tables
    }
}
