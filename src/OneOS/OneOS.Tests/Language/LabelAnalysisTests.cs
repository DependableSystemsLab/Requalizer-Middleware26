using System.Text.Json;
using OneOS.Runtime.Language;
using OneOS.Runtime.Language.Models;
using static OneOS.Tests.Language.TestUtil;

namespace OneOS.Tests.Language;

// The label-analysis cases of L§13, plus possible-label sets (S§0.1).
public class LabelAnalysisTests
{
    private const string Labels3 = "labels { public < internal; internal < secret; }\n";
    private const string Frame = "type frame { group: string, v: string }\n";

    [Fact]
    public void SourceLabellerInterval()
    {
        var p = CompileExample("camera.osh");
        var s1 = p.Graph("foo")!.Analysis!.Ports["s1.video"];
        Assert.Equal(("public", "internal"), (s1.Lo, s1.Up));
        Assert.Equal(new[] { "public", "internal" }, s1.PossibleLabels);
    }

    // An interior node n with base [public, secret], whose output labeller is `labeller`.
    private static CompiledProgram Interior(string labeller) => Compile(Labels3 + Frame +
        "graph g() { topology {" +
        " node a () => (o: frame) = process('a'); node b () => (o: frame) = process('b');" +
        " node n (i: frame) => (o: frame) = process('n'); node k (i: frame) = process('k');" +
        " edge a --> n; edge b --> n; edge n --> k; }" +
        $" policy {{ label(public): a.o; label(secret): b.o; label({labeller}): n.o; }} }}");

    [Fact]
    public void JoinWithInputIsNeverADeclassification()
    {
        var p = Interior("(e: frame, in: label) => join(in, internal)");
        AssertNoErrors(p);
        Assert.Empty(p.Graphs[0].Analysis!.Declassifications);
        var o = p.Graphs[0].Analysis!.Ports["n.o"];
        Assert.Equal(("internal", "secret"), (o.Lo, o.Up));
    }

    [Fact]
    public void ConstantLowLabelOnInteriorNodeIsADeclassification()
    {
        var p = Interior("(e: frame, in: label) => public");
        var site = Assert.Single(p.Graphs[0].Analysis!.Declassifications);
        Assert.Equal("n.o", site.Port);
        Assert.Equal(("public", "secret"), (site.BaseLo, site.BaseUp));
        Assert.Equal(new[] { "public" }, site.LowerResults);
        Assert.Contains(p.Diagnostics, d => d.Severity == Severity.Note && d.Message.StartsWith("declassification at n.o"));
    }

    // A constant label is a labeller that ignores its arguments (L§8.4), so `label(public)` on an
    // interior port whose base reaches `secret` is a declassification site too.
    [Fact]
    public void ConstantLabelAttachmentOnInteriorNodeIsADeclassification()
    {
        var p = Interior("public");
        var site = Assert.Single(p.Graphs[0].Analysis!.Declassifications);
        Assert.Equal("n.o", site.Port);
        Assert.Equal(new[] { "public" }, site.LowerResults);
        Assert.Equal("n", site.TrustedComponent);
        Assert.Contains(p.Diagnostics, d => d.Severity == Severity.Note && d.Message.StartsWith("declassification at n.o"));
        Assert.Empty(Interior("secret").Graphs[0].Analysis!.Declassifications);
    }

    // Strict mode never emits ⊥ (L§8.1); with one compartment that is the least declared label.
    private static CompiledProgram StrictChain(string policy, string flows = "") => Compile(
        "labels strict { low < high; }\n" + Frame +
        "graph g() { topology {" +
        " node a () => (o: frame) = process('a'); node n (i: frame) => (o: frame) = process('n'); node k (i: frame) = process('k');" +
        $" edge e1: a --> n; edge e2: n --> k; {flows} }} policy {{ label(high): a.o; {policy} }} }}");

    [Fact]
    public void StrictOutputThatCanOnlyCarryBottomIsAnError()
    {
        var p = StrictChain("label(low): n.o;");
        AssertHas(p, "E0734");
        Assert.Contains("n.o", p.Diagnostics.First(d => d.Code == "E0734").Message);
    }

    [Fact]
    public void StrictOutputThatMayCarryBottomIsAWarning()
    {
        var p = StrictChain("label((f: frame) => f.group == 'x' ? low : high): n.o;");
        AssertHas(p, "W0734");
        AssertLacks(p, "E0734");
        AssertNoErrors(p);
    }

    [Fact]
    public void StrictBottomCheckIgnoresPortsOnlyOnUncheckedEdges()
    {
        var p = StrictChain("label(low): n.o; unchecked('test'): f;", "flow f = { e2 };");
        AssertNoErrors(p);
    }

    // A port spanning two compartments has the interval [bottom..top], but never carries `bottom`.
    [Fact]
    public void StrictBottomCheckUsesLabelSetsNotIntervals()
    {
        var p = Compile("labels strict { a_lo < a_hi; b_lo < b_hi; }\nkey t: string;\ntype m { k: t }\n" +
            "graph g() { topology { node s () => (o: m) = process('s'); node n[t] (i: m) => (o: m) = process('n'); node k[t] (i: m) = process('k');" +
            " edge s --> n; edge n --> k; } policy { label((x: m) => x.k == 'a' ? a_hi : b_hi): s.o; } }");
        AssertNoErrors(p);
        AssertLacks(p, "W0734");
    }

    [Fact]
    public void SourcePortsAreNeverDeclassificationSites()
    {
        var p = Compile(Labels3 + Frame + "graph g() { topology { node a () => (o: frame) = process('a'); } policy { label((e: frame, in: label) => public): a.o; } }");
        Assert.Empty(p.Graphs[0].Analysis!.Declassifications);
    }

    [Theory]
    [InlineData("public", "secret", "W0730")]
    [InlineData("secret", "secret", "E0730")]
    public void PortCeilingChecks(string lo, string hi, string code)
    {
        // A source whose output carries [lo, hi], into a port with ceiling `internal`.
        var labeller = lo == hi ? lo : $"(e: frame) => e.group == 'x' ? {lo} : {hi}";
        var p = Compile(Labels3 + Frame + "graph g() { topology { node a () => (o: frame) = process('a'); node k (i: frame) = process('k'); edge a --> k; }" +
            $" policy {{ label({labeller}): a.o; label(internal): k.i; }} }}");
        AssertHas(p, code);
        AssertLacks(p, code == "W0730" ? "E0730" : "W0730");
    }

    [Fact]
    public void PossibleLabelSetsUseJoinClosureForGeneralNodes()
    {
        var p = Compile("labels { internal < hr; internal < eng; }\n" + Frame +
            "graph g() { topology { node a () => (o: frame) = process('a'); node b () => (o: frame) = process('b');" +
            " node n (i: frame) => (o: frame) = process('n'); node t (i: frame) => (o: frame) = process('t'); node k (i: frame) = process('k'); node k2 (i: frame) = process('k2');" +
            " edge a --> n; edge b --> n; edge n --> k; edge a --> t; edge b --> t; edge t --> k2; }" +
            " policy { label(hr): a.o; label(eng): b.o; } }");
        // `n` is a general node: its taint is the join of what it consumed.
        var a = p.Graphs[0].Analysis!;
        Assert.Equal(new[] { "hr", "eng", "top(internal)" }, a.Ports["n.o"].PossibleLabels);
        Assert.Equal(new[] { "hr", "eng" }, a.Nodes["n"].InputSet);
    }

    [Fact]
    public void PossibleLabelSetsForOneToOneNodesAreTheInputSet()
    {
        var p = Compile("labels { internal < hr; internal < eng; }\n" + Frame +
            "graph g() { topology { node a () => (o: frame) = process('a'); node b () => (o: frame) = process('b');" +
            " @one_to_one node n (i: frame) => (o: frame) = process('n'); node k (i: frame) = process('k');" +
            " edge a --> n; edge b --> n; edge n --> k; }" +
            " policy { label(hr): a.o; label(eng): b.o; } }");
        Assert.Equal(new[] { "hr", "eng" }, p.Graphs[0].Analysis!.Ports["n.o"].PossibleLabels);
    }

    [Fact]
    public void CeilingsRestrictDeliveredSets()
    {
        var p = CompileExample("camera.osh");
        var a = p.Graph("foo")!.Analysis!;
        Assert.Equal(new[] { "public", "internal", "secret" }, a.Ports["anon.in"].PossibleLabels);
        Assert.Equal(new[] { "public", "internal" }, a.Ports["sink.in"].PossibleLabels);
        // The interval of an input port is clamped by its ceiling.
        Assert.Equal("internal", a.Ports["sink.in"].Up);
    }

    [Fact]
    public void DynamicAndHostRanges()
    {
        var p = Compile(Labels3 + Frame + "graph g() { topology { node a () => (o: frame) = process('a'); node m (i: frame) => (o: frame) = process('m'); node k (i: frame) = process('k'); edge a --> m; edge m --> k; }" +
            " policy { label(dynamic: public..internal): m; label(host: public..secret): a; } }");
        AssertNoErrors(p);
        var an = p.Graphs[0].Analysis!;
        Assert.Equal(("public", "secret"), (an.Ports["a.o"].Lo, an.Ports["a.o"].Up));
        Assert.Equal("internal", an.Nodes["m"].BaseSet.Last());
        Assert.Equal("secret", an.Nodes["m"].Held);       // InUp (secret, from the host range) ⊔ hi
        // A dynamic node whose input may exceed lo is a potential declassification by the monitor.
        Assert.Contains(an.Declassifications, d => d.DynamicMonitor && d.Port == "m");
    }

    [Fact]
    public void W0710ForBranchEqualToTheBase()
    {
        var p = Interior("(e: frame, in: label) => e.group == 'x' ? join(in, public) : public");
        AssertHas(p, "W0710");
        AssertLacks(Interior("(e: frame, in: label) => e.group == 'x' ? in : public"), "W0710");
    }

    [Fact]
    public void StrictModeExcludesBottomFromOutputs()
    {
        var p = Compile("labels strict { a < b; c < d; }\n" + Frame + "graph g() { topology { node s () => (o: frame) = process('s'); node k (i: frame) = process('k'); edge s --> k; } policy { label(a): s.o; } }");
        AssertNoErrors(p);
        Assert.Equal(new[] { "a" }, p.Graphs[0].Analysis!.Ports["s.o"].PossibleLabels);
    }

    [Fact]
    public void CompiledGraphSerializesToJson()
    {
        var g = CompileExample("camera.osh").Graph("foo")!;
        var json = g.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("foo", doc.RootElement.GetProperty("Name").GetString());
        Assert.Equal("Keyed", doc.RootElement.GetProperty("Nodes")[2].GetProperty("Partitioning").GetString());
        Assert.Contains("\"kind\": \"ternary\"", json);   // labeller bodies travel as expression trees
        var lattice = JsonSerializer.Deserialize<LabelLattice>(doc.RootElement.GetProperty("Lattice").GetRawText(), CompiledGraph.JsonOptions)!;
        Assert.Equal("internal", lattice.Join("public", "internal"));
    }
}

public class SpawnTests
{
    private const string Labels2 = "labels { public < secret; }\n";
    private const string M = "type m { v: string }\n";

    private static (CompiledGraph? Graph, IReadOnlyList<Diagnostic> Diags) Spawn(CompiledProgram p, string cmd)
    {
        var (c, d) = new AppParser().ParseSpawn(cmd);
        Assert.Empty(d);
        return GraphBinder.Spawn(p, c!);
    }

    [Fact]
    public void BindsArgumentsAndEvaluatesArgv()
    {
        var p = CompileExample("camera.osh");
        var (g, diags) = Spawn(p, "spawn foo('cam-a', 'cam-b')");
        Assert.NotNull(g);
        Assert.Equal(new object?[] { "cam-a", "cam-b" }, g!.Args);
        Assert.Equal(new[] { "node", "source1.js", "cam-a" }, g.Node("s1").Process.Argv);
        Assert.Equal(new[] { "python", "detect.py" }, g.Node("det").Process.Argv);
        Assert.NotNull(g.Analysis);
        Assert.DoesNotContain(diags, d => d.IsError);
    }

    [Fact]
    public void StringConcatenationInArgv()
    {
        var p = new AppCompiler().Compile(new[] { new SourceFile("s.osh", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "legacy", "smartgrid.osh"))) });
        var (g, _) = Spawn(p, "spawn DSP_smartgrid('grid.csv', 'out/run1')");
        Assert.Equal(new[] { "node", "/home/root/writer.js", "out/run1.outlier.out" }, g!.Node("oSink").Process.Argv);
    }

    [Theory]
    [InlineData("spawn nope()", "E0801")]
    [InlineData("spawn foo('x')", "E0802")]
    [InlineData("spawn foo('x', 3)", "E0803")]
    public void SpawnErrors(string cmd, string code)
    {
        var (g, diags) = Spawn(CompileExample("camera.osh"), cmd);
        Assert.Null(g);
        Assert.Contains(diags, d => d.Code == code);
    }

    [Fact]
    public void IntegerRangeChecks()
    {
        var p = Compile(M + "graph g(n: u32) { topology { node a () => (o: m) = process('a'); } }");
        Assert.Null(Spawn(p, "spawn g(-1)").Graph);
        Assert.NotNull(Spawn(p, "spawn g(7)").Graph);
    }

    [Fact]
    public void LabelAnalysisIsRerunWithBoundParameters()
    {
        // At compile time the mode is unknown, so the source may emit public: only W0730.
        var p = Compile(Labels2 + M + "graph g(mode: string) { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge s --> k; }" +
            " policy { label(x: m => mode == 'strict' ? secret : public): s.o; label(public): k.i; } }");
        AssertNoErrors(p);
        AssertHas(p, "W0730");

        var (ok, okDiags) = Spawn(p, "spawn g('lenient')");
        Assert.NotNull(ok);
        Assert.Equal(("public", "public"), (ok!.Analysis!.Ports["s.o"].Lo, ok.Analysis.Ports["s.o"].Up));
        Assert.DoesNotContain(okDiags, d => d.Code == "W0730");

        var (bad, badDiags) = Spawn(p, "spawn g('strict')");
        Assert.Null(bad);
        var e = Assert.Single(badDiags, d => d.Code == "E0804");
        Assert.Contains(e.Notes, n => n.StartsWith("E0730"));
    }

    [Fact]
    public void ConditionsOverBoundParametersFoldWithoutW0711()
    {
        // Conditions that don't read the message fold once parameters are bound; W0711 must not fire.
        var p = Compile(Labels2 + M + "graph g(mode: string) { topology { node s () => (o: m) = process('s'); node t (i: m) => (o: m) = process('t'); node k (i: m) = process('k'); edge s --> t; edge t --> k; }" +
            " policy { label((x: m, y: label) => leq(y, public) && mode.startsWith(mode) == mode.contains(x.v) ? y : secret): t.o; } }");
        AssertNoErrors(p);
        var (g, diags) = Spawn(p, "spawn g('a')");
        Assert.NotNull(g);
        Assert.DoesNotContain(diags, d => d.Code == "W0711");

        var p2 = Compile(Labels2 + M + "graph g(n: i64) { topology { node s () => (o: m) = process('s'); node t (i: m) => (o: m) = process('t'); node k (i: m) = process('k'); edge s --> t; edge t --> k; }" +
            " policy { label((x: m, y: label) => n > 3 ? y : secret): t.o; } }");
        Assert.DoesNotContain(Spawn(p2, "spawn g(5)").Diags, d => d.Code == "W0711");
    }

    [Fact]
    public void TopLevelLabellersDoNotSeeGraphParameters()
    {
        // The graph parameter `secret` shadows the label inside inline lambdas only.
        var p = Compile(Labels2 + M + "labeller hi (x: m) = secret;\n" +
            "graph g(secret: string) { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge s --> k; } policy { label(hi): s.o; } }");
        AssertNoErrors(p);
        var (g, _) = Spawn(p, "spawn g('x')");
        Assert.Equal("secret", g!.Analysis!.Ports["s.o"].Up);
    }

    [Fact]
    public void GraphWithErrorsCannotBeSpawned()
    {
        var p = Compile(M + "graph g() { topology { node b (i: m) = process('b'); } }");
        var (g, diags) = Spawn(p, "spawn g()");
        Assert.Null(g);
        Assert.Contains(diags, d => d.Code == "E0307");
    }
}
