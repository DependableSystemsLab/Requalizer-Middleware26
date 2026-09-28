using OneOS.Runtime;
using OneOS.Runtime.Scheduling;
using OneOS.Tests.Language;

namespace OneOS.Tests.Scheduling;

// Phase 1 (S§4) and the scheduling-spec §14 DIFT scenarios.
public class DiftTests
{
    private const string LMH = "labels { low < med; med < high; }\ntype m { v: string }\n";

    private static ClusterSnapshot Levels(bool idm = false) => Sched.Cluster(
        Host("low-1", "low", idm), Host("low-2", "low", idm), Host("med-1", "med", idm), Host("med-2", "med", idm), Host("high-1", "high", idm), Host("high-2", "high", idm));

    private static HostRuntimeInfo Host(string name, string label, bool idm) =>
        Sched.Host(name, label) with { IdmSupport = idm ? new List<string> { "python", "node" } : new List<string>() };

    private static SchedulerOptions With(InMemoryDift dift, Func<SchedulerOptions, SchedulerOptions>? f = null)
    {
        var o = Sched.Options with { Analyzer = dift };
        return f == null ? o : f(o);
    }

    private static DiftReportEntry Report(GraphInstanceInfo i, string node) => i.Plan.Audit.Dift.Single(d => d.Node == node);

    // --- Scenario 2: ETL ---

    [Fact]
    public void EtlPattern()
    {
        var dift = new InMemoryDift()
            .Source("extract", "db.read", ExternalLabel.Static("low"))
            .Sink("load", "warehouse.write", ExternalLabel.Static("high"))
            .Sink("load", "audit.log", ExternalLabel.Static("high"));
        var i = Sched.Plan(Sched.Example("etl.osh", "etl"), Levels(), options: With(dift));

        Assert.Equal(ComponentClass.XToI, Report(i, "extract").Class);
        Assert.Equal(ComponentClass.IToI, Report(i, "transform").Class);
        Assert.Equal(ComponentClass.IToX, Report(i, "load").Class);
        Assert.All(i.Plan.Audit.Dift, d => Assert.Equal(DiftMode.Disabled, d.Mode));
        Assert.All(i.Agents, a => Assert.Null(a.Idm));                        // no in-process tracking

        Assert.Equal(new[] { "low", "med", "high" }, Report(i, "transform").Lanes);
        Assert.Equal("high", Report(i, "load").ExternalSinkCeiling);
        Assert.Equal("low", i.Agent("g1/extract/0").SourceJoin);

        // Routing only sends labels upward: into transform@λ only labels ⊑ λ.
        var L = i.Lattice;
        foreach (var p in i.Pipes.Where(p => p.EdgeName == "e1"))
        {
            var lane = i.Agent(p.DestinationAgentId).LaneLabel!;
            Assert.All(p.Allowed, x => Assert.True(L.Leq(x, lane)));
        }
        Assert.Equal(new[] { "low" }, i.Pipes.First(p => p.DestinationAgentId == "g1/transform@low/0").Allowed);

        // Lanes are placed by their label: the high lane only on high hosts, the low lane anywhere.
        Assert.All(i.Agents.Where(a => a.LaneLabel == "high"), a => Assert.StartsWith("high-", a.HostId));
        Assert.Equal("low", i.Agent("g1/transform@low/0").PlacementLabel);
    }

    [Fact]
    public void LanesAreCappedByTheSinkCeilingAndMaxLanes()
    {
        var dift = new InMemoryDift()
            .Source("extract", "db.read", ExternalLabel.Static("low"))
            .Sink("load", "warehouse.write", ExternalLabel.Static("med"));
        var i = Sched.Plan(Sched.Example("etl.osh", "etl"), Levels(), options: With(dift, o => o with { MaxLanesPerNode = 2 }));
        Assert.Equal(new[] { "low", "med" }, Report(i, "load").Lanes);             // high would be rejected by C_ext = med
        Assert.Equal(new[] { "high" }, Report(i, "transform").Lanes);              // 3 labels > 2 lanes: maximal elements only
        Assert.Contains(i.Plan.Warnings, w => w.Code == "SPW08");
        Assert.Contains(i.Plan.Warnings, w => w.Code == "SPW07");                  // load may drop high messages
    }

    [Fact]
    public void LaneModeOff()
    {
        var dift = new InMemoryDift().Source("extract", "db.read", ExternalLabel.Static("low"));
        var i = Sched.Plan(Sched.Example("etl.osh", "etl"), Levels(), options: With(dift, o => o with { LaneMode = LaneMode.Off }));
        Assert.All(i.Agents, a => Assert.Null(a.LaneLabel));
        Assert.Equal("high", i.Agent("g1/transform/0").PlacementLabel);
    }

    // --- Scenarios 3, 3a, 4: X-to-X and I-to-X sinks ---

    private const string WebServer = LMH + "graph web() { topology { node req () => (o: m) = process('node', 'req.js'); node srv (i: m) = process('python', 'server.py'); edge req --> srv; } policy { label(med): req.o; } }";

    [Fact]
    public void XToXWithUnboundedDynamicSinkEnablesDift()
    {
        var dift = new InMemoryDift()
            .Source("srv", "sessions.db", ExternalLabel.Static("med"))
            .Sink("srv", "http.response", ExternalLabel.Dynamic());
        var g = Sched.Graph(WebServer);
        var i = Sched.Plan(g, Levels(idm: true), options: With(dift));
        var srv = i.Agent("g1/srv/0");
        Assert.Equal(ComponentClass.XToX, srv.Class);
        Assert.Equal(DiftMode.Enabled, srv.Dift);
        Assert.NotNull(srv.Idm);
        Assert.Equal("python", srv.Idm!.LanguageRuntime);
        Assert.Equal(750, srv.Demand.CpuMillis);                                     // × IdmCpuFactor 1.5

        // A host without IDM support for python can't run it.
        var ex = Sched.Fails(g, Levels(idm: false), options: With(dift));
        Assert.Equal("SP004", ex.Code);
        Assert.Contains(ex.Diagnostics[0].Details, d => d.Contains("no IDM support for 'python'"));
    }

    [Fact]
    public void XToXWithLowerBoundedSinkIsDisabled()
    {
        var dift = new InMemoryDift()
            .Source("srv", "sessions.db", ExternalLabel.Static("med"))
            .Sink("srv", "http.response", ExternalLabel.Dynamic(lowerBound: "med"));
        var i = Sched.Plan(Sched.Graph(WebServer), Levels(), options: With(dift));
        Assert.Equal(DiftMode.Disabled, i.Agent("g1/srv/0").Dift);
        Assert.Null(i.Agent("g1/srv/0").Idm);
    }

    [Fact]
    public void XToXWhoseSinksAreAllAtTheTopIsDisabled()
    {
        var dift = new InMemoryDift()
            .Source("srv", "sessions.db", ExternalLabel.Static("med"))
            .Sink("srv", "vault", ExternalLabel.Static("high"))
            .Sink("srv", "archive", ExternalLabel.Static("high"));
        Assert.Equal(DiftMode.Disabled, Sched.Plan(Sched.Graph(WebServer), Levels(), options: With(dift)).Agent("g1/srv/0").Dift);

        // A sink below what may reach it keeps DIFT on.
        var low = new InMemoryDift().Source("srv", "sessions.db", ExternalLabel.Static("med")).Sink("srv", "public.log", ExternalLabel.Static("low"));
        Assert.Equal(DiftMode.Enabled, Sched.Plan(Sched.Graph(WebServer), Levels(idm: true), options: With(low)).Agent("g1/srv/0").Dift);
    }

    [Fact]
    public void IToXWithStaticAndLowerBoundedSinks()
    {
        var src = LMH + "graph g() { topology { node s () => (o: m) = process('node', 's.js'); node w (i: m) = process('python', 'w.py'); edge s --> w; } policy { label(low): s.o; } }";
        var dift = new InMemoryDift()
            .Sink("w", "public.page", ExternalLabel.Static("low"))
            .Sink("w", "user.page", ExternalLabel.Dynamic(lowerBound: "med"));
        var w = Sched.Plan(Sched.Graph(src), Levels(), options: With(dift)).Agent("g1/w/0");
        Assert.Equal(ComponentClass.IToX, w.Class);
        Assert.Equal(DiftMode.Disabled, w.Dift);
        Assert.Equal("low", w.ExternalSinkCeiling);
    }

    // --- Scenario 5: C_ext against the inputs ---

    [Theory]
    [InlineData("med", "SP010")]
    [InlineData("x: m => x.v == 'a' ? low : med", "SPW07")]
    public void ExternalSinkCeilingAgainstInputs(string sourceLabel, string code)
    {
        var src = LMH + "graph g() { topology { node s () => (o: m) = process('node', 's.js'); node w (i: m) = process('python', 'w.py'); edge s --> w; } policy { " +
                  $"label({sourceLabel}): s.o; }} }}";
        var dift = new InMemoryDift().Sink("w", "public.page", ExternalLabel.Static("low"));
        if (code == "SP010")
        {
            var ex = Sched.Fails(Sched.Graph(src), Levels(), options: With(dift));
            Assert.Equal("SP010", ex.Code);
            Assert.Contains("(E0731)", ex.Diagnostics[0].Details[0]);
            Assert.Contains(ex.Diagnostics[0].Details, d => d.StartsWith("path:"));   // witness path
        }
        else
            Assert.Contains(code, Sched.Codes(Sched.Plan(Sched.Graph(src), Levels(), options: With(dift))));
    }

    // --- Scenario 6 and analyzer failures ---

    [Fact]
    public void ExternalSourceWithoutALabel()
    {
        var dift = new InMemoryDift().Source("srv", "mystery.feed", ExternalLabel.Unknown);
        var g = Sched.Graph(WebServer);
        Assert.Equal("SP011", Sched.Fails(g, Levels(), options: With(dift)).Code);

        // UnlabeledExternalSource provides the label.
        var i = Sched.Plan(g, Levels(), options: With(dift, o => o with { UnlabeledExternalSource = "high" }));
        Assert.Equal("high", i.Agent("g1/srv/0").SourceJoin);
        Assert.Equal("high", i.Agent("g1/srv/0").PlacementLabel);
    }

    [Fact]
    public void AnalyzerFailure()
    {
        var dift = new InMemoryDift().Unanalyzable("srv");
        var g = Sched.Graph(WebServer);
        Assert.Equal("SP012", Sched.Fails(g, Levels(), options: With(dift)).Code);

        var i = Sched.Plan(g, Levels(idm: true), options: With(dift, o => o with { AllowUnanalyzedNodes = true, UnlabeledExternalSource = "med" }));
        Assert.Equal(DiftMode.Enabled, i.Agent("g1/srv/0").Dift);
        Assert.Equal(ComponentClass.XToX, i.Agent("g1/srv/0").Class);
        Assert.Contains(i.Plan.Warnings, w => w.Code == "SPW08" && w.Message.Contains("'srv'"));
    }

    // --- Scenario 10: declared dynamic on an I-to-I node ---

    [Fact]
    public void InternalMonitorOnInternalNodeIsTurnedOffButDynamicLabelStays()
    {
        var src = LMH + "graph g() { topology { node s () => (o: m) = process('node', 's.js'); node t (i: m) => (o: m) = process('node', 't.js'); node k (i: m) = process('node', 'k.js'); edge s --> t; edge t --> k; }" +
                  " policy { label(low): s.o; label(dynamic: med..high): t; } }";
        var i = Sched.Plan(Sched.Graph(src), Levels());
        var t = i.Agent("g1/t/0");
        Assert.True(t.InternalMonitorDisabled);
        Assert.Contains(i.Plan.Warnings, w => w.Code == "SPW09");
        Assert.Equal(new[] { "med", "high" }, t.Ports.Single(p => p.Name == "o").PossibleLabels);   // D_r still labels outputs

        var keep = Sched.Plan(Sched.Graph(src), Levels(), options: Sched.Options with { DisableInternalMonitorWhenInternal = false });
        Assert.False(keep.Agent("g1/t/0").InternalMonitorDisabled);
        Assert.Equal(new[] { "med", "high" }, keep.Agent("g1/t/0").Ports.Single(p => p.Name == "o").PossibleLabels);
    }

    // --- Scenario 12: unchecked flows ---

    [Fact]
    public void UncheckedFlowExclusiveNodes()
    {
        var dift = new InMemoryDift()
            .Sink("pager", "sms", ExternalLabel.Static("public"))
            .Sink("detector", "log", ExternalLabel.Static("public"));
        var i = Sched.Plan(Sched.Example("alerts.osh", "emergency"),
            Sched.Cluster(Sched.Host("p1", "public"), Sched.Host("s1", "secret")), options: With(dift));
        var pager = i.Agent("g1/pager/0");
        Assert.Equal(DiftMode.Disabled, pager.Dift);
        Assert.Contains("exclusive node of unchecked flow", pager.DiftRationale);
        Assert.Null(pager.ExternalSinkCeiling);
        Assert.Null(pager.PlacementLabel);
        Assert.Null(pager.LaneLabel);
        // The non-exclusive detector keeps its automatic ceiling.
        Assert.Equal("public", i.Agent("g1/detector/0").ExternalSinkCeiling);
    }

    // --- Report and defaults ---

    [Fact]
    public void DiftReportCountsElidedChecksAndNamesTheRule()
    {
        var cluster = Sched.Cluster(Sched.Host("p1", "public"), Sched.Host("p2", "public"), Sched.Host("i1", "internal"), Sched.Host("i2", "internal"), Sched.Host("s1", "secret"), Sched.Host("s2", "secret"));
        var i = Sched.Plan(Sched.Example("camera.osh", "foo"), cluster, new object?[] { "a", "b" });
        var anon = Report(i, "anon");
        Assert.Equal(ComponentClass.IToI, anon.Class);
        Assert.Contains("no interface analyzer", anon.Rule);
        Assert.Equal(new[] { "public", "internal", "secret" }, anon.Lanes);
        Assert.Equal(24, anon.ElidedChecksPerPipe.Count);
        Assert.All(anon.ElidedChecksPerPipe.Values, n => Assert.InRange(n, 0, 4));
    }

    [Fact]
    public void DiftPolicyFromJson()
    {
        var dift = InMemoryDift.FromJson("""
            { "extract": { "sources": [ { "location": "db.read", "label": "low" }, { "location": "api", "range": ["low", "med"], "text": "r => r.tier" } ] },
              "load":    { "sinks":   [ { "location": "wh", "dynamic": true, "lowerBound": "high" } ] },
              "x":       { "unanalyzable": true } }
            """);
        var i = Sched.Plan(Sched.Example("etl.osh", "etl"), Levels(), options: With(dift));
        var oe = Assert.Single(i.Agent("g1/extract/0").OutputEvaluable);
        Assert.Equal(("api", "r => r.tier"), (oe.Location, oe.Label.Text));
        Assert.Equal(new[] { "extract: api: r => r.tier" }, Report(i, "extract").TrustedLabellers);
        Assert.Equal("high", Report(i, "load").ExternalSinkCeiling);
    }
}
