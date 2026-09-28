using OneOS.Runtime.Language;
using OneOS.Runtime.Language.Models;
using static OneOS.Tests.Language.TestUtil;

namespace OneOS.Tests.Language;

// The minimum conformance set of L§13.
public class ConformanceValidTests
{
    [Fact]
    public void MainExample()
    {
        var p = CompileExample("camera.osh");
        AssertNoErrors(p);
        var g = p.Graph("foo")!;
        var a = g.Analysis!;

        // Declassification at anon.out.
        var site = Assert.Single(a.Declassifications);
        Assert.Equal("anon.out", site.Port);
        Assert.Equal(new[] { "public" }, site.LowerResults);
        Assert.Equal(new[] { "redacted" }, site.FieldsRead);
        Assert.Equal("anon", site.TrustedComponent);

        // W0730 at sink.in and at sink.late_in (through the implicit route edge).
        var w = p.Diagnostics.Where(d => d.Code == "W0730").Select(d => d.Message).ToList();
        Assert.Equal(2, w.Count);
        Assert.Contains(w, m => m.Contains("sink.in"));
        Assert.Contains(w, m => m.Contains("sink.late_in"));

        // Placement constraints.
        string Held(string n) => a.Placement.Single(c => c.Node == n).MinHostLabel!;
        Assert.Equal("secret", Held("det"));
        Assert.Equal("secret", Held("anon"));
        Assert.Equal("internal", Held("sink"));
        // Deviation (plan.md): sources hold what their labelled outputs carry.
        Assert.Equal("internal", Held("s1"));
        Assert.Equal("secret", Held("s2"));

        // No W0503; flow fastpath; no W0901.
        AssertLacks(p, "W0503");
        AssertLacks(p, "W0901");
        var f = Assert.Single(g.Flows);
        Assert.Equal(new[] { "s1.video" }, f.EntryPorts);
        Assert.Equal(new[] { "anon.in" }, f.ExitPorts);
        Assert.Equal(new[] { "det" }, f.InteriorNodes);
        Assert.Equal(LatencyMode.EventTime, f.Mode);
        Assert.Equal(new[] { "ts" }, f.ClockField);
        Assert.Equal(500_000_000, f.MaxLatencyNanos);
        Assert.Equal(new[] { new[] { "s1", "det", "anon" } }, f.NodePaths);

        // Lateness defaults (L§13 valid program 6).
        Assert.Equal(50_000_000, g.Port("det.in").Ordering!.Default.LatenessNanos);
        Assert.False(g.Port("det.in").Ordering!.Default.LatenessFromPolicy);
        Assert.Equal(200_000_000, g.Port("sink.in").Ordering!.Default.LatenessNanos);
        Assert.Equal(OnLateMode.Route, g.Port("sink.in").Ordering!.Default.OnLate);
        Assert.Equal("late_in", g.Port("sink.in").Ordering!.Default.OnLateRoutePort);

        // Implicit route edge anon.out → sink.late_in.
        var implicitEdge = Assert.Single(g.Edges, e => e.Implicit);
        Assert.Equal("anon.out", implicitEdge.Source);
        Assert.Equal("sink.late_in", implicitEdge.Destination);
        Assert.Equal("e4", implicitEdge.ImplicitOf);

        // Witness path format (L§10).
        var sinkIn = p.Diagnostics.Single(d => d.Code == "W0730" && d.Message.Contains("sink.in:"));
        Assert.Equal("path: s2.video [secret] -e2-> det.in; det.out [public..secret] -e3-> anon.in; anon.out [public..secret] -e4-> sink.in [ceiling internal]", sinkIn.Notes[0]);
    }

    [Fact]
    public void LinearPipeline()
    {
        var p = CompileExample("pipeline.osh");
        AssertNoErrors(p);
        Assert.DoesNotContain(p.Diagnostics, d => d.Severity == Severity.Warning);
        Assert.Equal(new[] { "s1.out__s2.in", "s2.out__s3.in" }, p.Graph("linear")!.Edges.Select(e => e.Name));
    }

    [Fact]
    public void JpegSequencedPipeline()
    {
        var p = CompileExample("jpeg.osh");
        AssertNoErrors(p);
        var g = p.Graph("jpeg_pipeline")!;
        Assert.True(g.Port("cam.out").Sequence!.Origin);
        Assert.True(g.Port("dec.out").Sequence!.Propagating);
        Assert.Equal("cam.out", g.Port("sink.in").Sequence!.OriginPort);
        Assert.Equal(new[] { "dec" }, g.Port("sink.in").Sequence!.PathNodes);
        Assert.Equal(2_000_000_000, g.Port("sink.in").Ordering!.Default.GapTimeoutNanos);
        Assert.Equal(new InstanceRange(2, 8), g.Node("dec").Instances);
        AssertLacks(p, "W0501");
    }

    [Fact]
    public void BroadcastConfigIntoKeyedNode()
    {
        var p = CompileExample("config_broadcast.osh");
        AssertNoErrors(p);
        var g = p.Graph("detect_with_config")!;
        Assert.Equal(Runtime.Language.Ast.EdgeOp.AllPartitions, g.Edge("cfg")!.Op);
        Assert.Equal(new[] { new[] { "cam" } }, g.Edge("frames")!.KeyPaths);
        Assert.Equal(PartitionKind.Keyed, g.Node("det").Partitioning);
        Assert.Equal(128, g.Node("det").KeyGroups);
    }

    [Fact]
    public void TupleKeyedNode()
    {
        var p = CompileExample("zones.osh");
        AssertNoErrors(p);
        var g = p.Graph("zones")!;
        Assert.Equal(new[] { "camera", "zone" }, g.Node("agg").KeyDomains);
        Assert.Equal(new[] { new[] { "cam" }, new[] { "z" } }, g.Edges[0].KeyPaths);
        Assert.Equal(64, g.Node("agg").KeyGroups);
    }

    [Fact]
    public void TreeHierarchyCompiles() => AssertNoErrors(Compile("labels { public < internal; internal < hr; internal < eng; }"));

    [Fact]
    public void StructuralTypingAcceptsExtraSenderFields() => AssertNoErrors(Compile(
        "type big { a: i32, b: i32, c: string } type small { a: i32 }\n" +
        "graph g() { topology { node x () => (o: big) = process('x'); node y (i: small) = process('y'); edge x --> y; } }"));

    [Fact]
    public void JpegIntoBytes() => AssertNoErrors(Compile(
        "graph g() { topology { node x () => (o: jpeg) = process('x'); node y (i: bytes) = process('y'); edge x --> y; } }"));

    [Fact]
    public void TenantKeyedNodeFromTwoCompartmentsIsW0733()
    {
        var p = CompileExample("tenants.osh");
        AssertNoErrors(p);
        var w = Assert.Single(p.Diagnostics, d => d.Code == "W0733");
        Assert.Equal(2, w.Notes.Count);
        Assert.Contains("a_src.out [a_internal]", w.Notes[0]);
        Assert.Contains("b_src.out [b_internal]", w.Notes[1]);
        Assert.Null(p.Graph("tenants")!.Analysis!.Placement.Single(c => c.Node == "agg").MinHostLabel);
    }

    [Fact]
    public void CorrelatedLatencyThroughOneToOne()
    {
        var p = CompileExample("alerts.osh");
        AssertNoErrors(p);
        var f = p.Graph("emergency")!.Flows.Single();
        Assert.Equal(LatencyMode.Correlated, f.Mode);
        Assert.Equal(99.9, f.LatencyPercentile);
        AssertLacks(p, "E0622");
    }

    [Fact]
    public void UncheckedFlowMasksMemberEdgesOnly()
    {
        var p = CompileExample("alerts.osh");
        var g = p.Graph("emergency")!;
        var w = Assert.Single(p.Diagnostics, d => d.Code == "W0730");
        Assert.Contains("archive.in", w.Message);
        var report = Assert.Single(g.Analysis!.UncheckedFlows);
        var masked = Assert.Single(report.Masked);
        Assert.Equal("W0730", masked.Code);
        Assert.Contains("pager.in", masked.Message);
        Assert.Equal(new[] { "cam", "pager" }, report.ExclusiveNodes);
        Assert.Equal(new[] { "detector" }, report.NonExclusiveNodes);
        AssertHas(p, "W0740");
        Assert.Null(g.Analysis.Placement.Single(c => c.Node == "pager").MinHostLabel);
        Assert.Equal("secret", g.Analysis.Placement.Single(c => c.Node == "detector").MinHostLabel);
    }

    [Theory]
    [InlineData("living.osh")]
    [InlineData("frauddetection.osh")]
    [InlineData("smartgrid.osh")]
    public void LegacyExamplesCompile(string name)
    {
        var p = new AppCompiler().Compile(new[] { new SourceFile(name, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "legacy", name))) });
        AssertNoErrors(p);
    }
}

// Framing (L§5.2): each port's framing is in the IR, and unsegmentable streams only go one-to-one.
public class FramingTests
{
    [Fact]
    public void PortsCarryTheirTypesFraming()
    {
        var p = Compile("type r { v: string }\ngraph g() { topology { node a () => (o: r, j: json, img: jpeg, raw: bytes, txt: lines) = process('a');"
            + " node b (i: r, j: json, img: bytes, raw: bytes, txt: lines) = process('b');"
            + " edge a.o --> b.i; edge a.j --> b.j; edge a.img --> b.img; edge a.raw --> b.raw; edge a.txt --> b.txt; } }");
        AssertNoErrors(p);
        var a = p.Graphs.Single().Node("a");
        Framing Of(string port) => a.Outputs.Single(x => x.Name == port).Framing;
        Assert.Equal(Framing.Ndjson, Of("o"));
        Assert.Equal(Framing.Ndjson, Of("j"));
        Assert.Equal("markers(ffd8, ffd9)", Of("img").ToString());
        Assert.False(Of("raw").Segmentable);
        Assert.Equal(Framing.Lines, Of("txt"));
    }

    [Fact]
    public void SegmentedFormatsMayBeLoadBalancedEvenIntoBytesPorts()
    {
        // jpeg is segmented at the sender, so the receiving port's type doesn't matter.
        var p = Compile("graph g() { topology { node a () => (o: jpeg) = process('a'); node b[] (i: bytes) = process('b'); edge a --> b; } }");
        AssertNoErrors(p);
    }

    [Fact]
    public void UnsegmentableStreamOneToOneIsFineAndStandbyWarns()
    {
        var p = Compile("graph g() { topology { node a () => (o: bytes) = process('a'); node b (i: bytes) = process('b'); edge a --> b; } policy { always: b; } }");
        AssertNoErrors(p);
        AssertHas(p, "W0902");
        AssertLacks(Compile("graph g() { topology { node a () => (o: bytes) = process('a'); node b (i: bytes) = process('b'); edge a --> b; } }"), "W0902");
    }

    [Fact]
    public void RegistryFormatsOptIntoSegmentation()
    {
        var formats = FormatRegistry.CreateDefault();
        formats.Add(new FormatEntry("bytes_lp32", Framing.Parse("length_prefix(4, le)"), new[] { "bytes" }));
        var p = new AppCompiler(formats).Compile(new[] { new SourceFile("t.dag",
            "graph g() { topology { node a () => (o: bytes_lp32) = process('a'); node b[] (i: bytes_lp32) = process('b'); edge a --> b; } }") });
        AssertNoErrors(p);
        Assert.Equal(FramingKind.LengthPrefix, p.Graphs.Single().Node("a").Outputs[0].Framing.Kind);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("ndjson")]
    [InlineData("lines")]
    [InlineData("delimiter(00)")]
    [InlineData("markers(ffd8, ffd9)")]
    [InlineData("length_prefix(4, le)")]
    [InlineData("length_prefix(2, be)")]
    [InlineData("fixed(188)")]
    public void FramingTextRoundTrips(string text) => Assert.Equal(text, Framing.Parse(text).ToString());

    [Theory]
    [InlineData("delimiter(0)")]
    [InlineData("length_prefix(3, le)")]
    [InlineData("fixed(0)")]
    [InlineData("markers(ffd8)")]
    [InlineData("chunked")]
    public void BadFramingIsRejected(string text) => Assert.Throws<FormatException>(() => Framing.Parse(text));
}

// The implicit `stderr` output (L§6.1, plan step 6.5a).
public class StderrPortTests
{
    private const string M = "type m { v: string }\n";

    [Fact]
    public void StderrBecomesAPortOnlyWhenReferenced()
    {
        var p = Compile(M + "graph g() { topology { node a () => (o: m) = process('a'); node b (i: m) = process('b'); node log (i: lines) = process('l');"
            + " edge a --> b; edge a.stderr --> log; edge b.stderr --> log; } }");
        AssertNoErrors(p);
        AssertLacks(p, "W0302");
        var g = p.Graphs.Single();
        Assert.Equal(Framing.Lines, g.Node("a").Outputs.Single(x => x.Name == "stderr").Framing);
        Assert.True(g.Node("b").IsSink);                          // stderr doesn't make b a non-sink
        Assert.DoesNotContain(g.Node("log").Outputs, x => x.Name == "stderr");   // never referenced: a log
    }

    [Fact]
    public void StderrDoesNotCountAsADataOutput()
    {
        // Shorthand edges and @one_to_one still see one output port.
        var p = Compile(M + "graph g() { topology { node a () => (o: m) = process('a'); @one_to_one node f (i: m) => (o: m) = process('f');"
            + " node k (i: m) = process('k'); node log (i: lines) = process('l');"
            + " edge a.stderr --> log; edge a --> f; edge f.stderr --> log; edge f --> k; } }");
        AssertNoErrors(p);
    }

    [Fact]
    public void StderrCanBeDeclaredWithAnotherTypeButNotAsAnInput()
    {
        var p = Compile(M + "graph g() { topology { node a () => (o: m, stderr: m) = process('a'); node k (i: m) = process('k'); edge a.stderr --> k; } }");
        AssertNoErrors(p);
        Assert.Equal(Framing.Ndjson, p.Graphs.Single().Node("a").Outputs.Single(x => x.Name == "stderr").Framing);
        AssertHas(Compile(M + "graph g() { topology { node a (stderr: m) = process('a'); } }"), "E0104");
    }

    [Fact]
    public void StderrCarriesTheNodesLabelLikeAnyOutput()
    {
        // A secret source's stderr into a public sink can never pass: the same E0730 as a data edge.
        var p = Compile("labels { public < secret; }\n" + M + "graph g() { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k');"
            + " node log (i: lines) = process('l'); edge s --> k; edge s.stderr --> log; }"
            + " policy { label(secret): s.o; label(public): log.i; } }");
        AssertHas(p, "E0730");
        Assert.Contains(p.Diagnostics, d => d.Code == "E0730" && d.Message.Contains("s.stderr"));
    }

    [Fact]
    public void OneToOneStderrCarriesTheTaintNotPerMessageLabels()
    {
        var p = Compile("labels { public < secret; }\n" + M + "graph g() { topology { node s1 () => (o: m) = process('a'); node s2 () => (o: m) = process('b');"
            + " @one_to_one node f[] (i: m) => (o: m) = process('f'); node k (i: m) = process('k'); node log (i: lines) = process('l');"
            + " edge s1 --> f; edge s2 --> f; edge f --> k; edge f.stderr --> log; }"
            + " policy { label(public): s1.o; label(secret): s2.o; } }");
        AssertNoErrors(p);
        var ports = p.Graphs.Single().Analysis!.Ports;
        Assert.Equal(new[] { "public", "secret" }, ports["f.o"].PossibleLabels);
        Assert.Equal(new[] { "public", "secret" }, ports["f.stderr"].PossibleLabels);
    }
}

public class ConformanceInvalidTests
{
    private const string M = "type m { v: string }\n";

    public static TheoryData<string, string> Programs => new()
    {
        { "E0306", M + "graph g() { topology { node a (i: m) => (o: m) = process('a'); node b (i: m) => (o: m) = process('b'); edge a --> b; edge b --> a; } }" },
        { "E0403", M + "graph g() { topology { node a () => (o: m) = process('a'); node b (i: m) = process('b'); edge a -*> b; } }" },
        { "E0406", "key camera: string; type f { c1: camera, c2: camera }\ngraph g() { topology { node a () => (o: f) = process('a'); node b[camera] (i: f) = process('b'); edge a --> b; } }" },
        { "E0411", "key camera: string;\ngraph g() { topology { node a () => (o: jpeg) = process('a'); node b[camera] (i: jpeg) = process('b'); edge a --> b; } }" },
        { "E0305", "type small { a: i32 } type big { a: i32, b: i32 }\ngraph g() { topology { node x () => (o: small) = process('x'); node y (i: big) = process('y'); edge x --> y; } }" },
        { "E0305", "graph g() { topology { node x () => (o: bytes) = process('x'); node y (i: jpeg) = process('y'); edge x --> y; } }" },
        { "E0308", M + "graph g() { topology { node s () => (o: m) = process('s'); @one_to_one node a (i: m, j: m) => (o: m) = process('a'); edge s --> a.i; edge s --> a.j; } }" },
        { "E0504", "clock c: u64; type r { ts: c }\ngraph g() { topology { node a () => (o: r) = process('a'); node b (i: r ordered) = process('b'); edge a --> b; } }" },
        { "E0502", "graph g() { topology { node a () => (o: bytes ordered) = process('a'); } }" },
        { "E0508", "clock c: u64; type r { ts: c }\ngraph g() { topology { node a () => (o: r ordered) = process('a'); node b (i: r ordered within 5ms) = process('b'); edge a --> b; } }" },
        { "E0511", M + "graph g() { topology { node s () => (o: m) = process('s'); node x[] (i: m) => (o: m) = process('x'); node k (i: m sequenced) = process('k'); edge s --> x; edge x --> k; } }" },
        { "E0610", "key k: string; type r { id: k }\ngraph g() { topology { node a () => (o: r) = process('a'); node b[k] (i: r) = process('b'); edge a --> b; } policy { partitions(2..8): b; } }" },
        { "E0608", "key k: string; type r { id: k }\ngraph g() { topology { node a () => (o: r) = process('a'); node b[k] (i: r) = process('b'); edge a --> b; } policy { partitions(200): b; } }" },
        { "E0609", M + "graph g() { topology { node a () => (o: m) = process('a'); node anon[] (i: m) = process('b'); edge a --> anon; } policy { always: anon[0]; } }" },
        { "E0705", "labels strict { a < b; }\n" + M + "graph g() { topology { node a () => (o: m) = process('a'); } }" },
        { "E0730", "labels { public < secret; }\n" + M + "graph g() { topology { node s1 () => (video: m) = process('a'); node sink (i: m) = process('b'); edge s1 --> sink; } policy { label(secret): s1.video; label(public): sink.i; } }" },
        { "E0733", "labels { a_public < a_internal; b_public < b_ops; }\n" + M + "graph g() { topology { node s1 () => (o: m) = process('a'); node s2 () => (o: m) = process('b'); node join (in: m) = process('c'); edge e1: s1 --> join; edge e2: s2 --> join; } policy { label(a_internal): s1.o; label(b_ops): s2.o; } }" },
        { "E0721", "labels { public < secret; }\n" + M + "graph g() { topology { node n () => (o: m) = process('a'); } policy { label(dynamic: secret..public): n; } }" },
        { "E0620", M + "graph g() { topology { node a () => (o: m) = process('a'); node b (i: m) = process('b'); edge a --> b; flow f = { nope }; } }" },
        { "E0621", M + "graph g() { topology { node a () => (o: m) = process('a'); node b (i: m) = process('b'); node c () => (o: m) = process('c'); node d (i: m) = process('d'); edge e1: a --> b; edge e4: c --> d; flow f = { e1, e4 }; } }" },
        { "E0622", M + "graph g() { topology { node a () => (o: m) = process('a'); node b (i: m) => (o: m) = process('b'); node c (i: m) = process('c'); edge e1: a --> b; edge e2: b --> c; flow f = { e1, e2 }; } policy { max_latency(10ms): f; } }" },
        { "E0623", "clock c: u64; type r { ts: c } type q { v: string }\ngraph g() { topology { node a () => (o: r) = process('a'); @one_to_one node b (i: r) => (o: q) = process('b'); node k (i: q) = process('c'); edge e1: a --> b; edge e2: b --> k; flow f = { e1, e2 }; } policy { max_latency(10ms) by ts: f; } }" },
        { "E0603", M + "graph g() { topology { node a () => (o: m) = process('a'); node b (i: m) = process('b'); edge e: a --> b; } policy { unchecked('fast'): e; } }" },
        { "E0602", M + "graph g() { topology { node a () => (o: m) = process('a'); node b (i: m) = process('b'); edge e: a --> b; flow f = { e }; } policy { unchecked(''): f; } }" },
        { "E0602", M + "graph g() { topology { node a () => (o: m) = process('a'); node b (i: m) = process('b'); edge e: a --> b; } policy { min_rate(10, 100): e; } }" },
        // Unsegmentable streams (plain `bytes`, framing none; L§5.2).
        { "E0412", "graph g() { topology { node a () => (o: bytes) = process('a'); node b[] (i: bytes) = process('b'); edge a --> b; } }" },
        { "E0412", "graph g() { topology { node a () => (o: bytes) = process('a'); node b[] (i: bytes) = process('b'); edge a -*> b; } }" },
        { "E0413", "graph g() { topology { node a[] () => (o: bytes) = process('a'); node b (i: bytes) = process('b'); edge a --> b; } }" },
        { "E0413", "graph g() { topology { node a () => (o: bytes) = process('a'); node c () => (o: jpeg) = process('c'); node b (i: bytes) = process('b'); edge a --> b; edge c --> b; } }" },
        { "E0515", "graph g() { topology { node a () => (o: bytes) = process('a'); node b (i: bytes sequenced) = process('b'); edge a --> b; } }" },
        { "E0735", "labels { public < secret; }\ngraph g() { topology { node s () => (o: bytes) = process('s'); node k (i: bytes) = process('k'); edge s --> k; } policy { label(dynamic: public..secret): s; } }" },
        { "E0735", "labels { public < secret; }\n" + M + "graph g() { topology { node s1 () => (o: m) = process('a'); node s2 () => (o: m) = process('b'); node mid (i: m) => (o: bytes) = process('c'); node k (i: bytes) = process('d'); edge s1 --> mid; edge s2 --> mid; edge r: mid --> k; } policy { label(public): s1.o; label(secret): s2.o; label(public): k.i; } }" },
        { "E0735", "labels { a_public < a_internal; b_public < b_ops; }\n" + M + "labeller f (x: m) = x.v == 'a' ? a_internal : b_ops;\ngraph g() { topology { node s () => (o: m) = process('a'); node mid (i: m) => (o: bytes) = process('c'); node z (i: bytes) = process('z'); edge s --> mid; edge mid --> z; } policy { label(f): s.o; } }" },
        // Found in review.
        { "E0601", M + "graph g() { topology { node a () => (o: m) = process('a'); } policy { frobnicate(3): a; } }" },
        { "E0307", M + "graph g() { topology { node b (i: m) = process('b'); } }" },
        { "E0105", "graph g() { topology { node a () => (o: nosuchtype) = process('a'); } }" },
        { "E0405", "key k: string; type r { v: string }\ngraph g() { topology { node a () => (o: r) = process('a'); node b[k] (i: r) = process('b'); edge a --> b; } }" },
        { "E0103", M + "graph g() { topology { node a () => (o: m) = process('a'); node b (i: m) = process('b'); edge a: a --> b; } }" },
        { "E0101", M + "graph g() { topology { node a () => (o: m) = process('a'); } } graph g() { topology { node a () => (o: m) = process('a'); } }" },
        { "E0204", "type r { a: i32 } graph g(x: r) { topology { } }" },
        { "E0301", M + "graph g(n: i32) { topology { node a () => (o: m) = process('x', n); } }" },
        { "E0302", M + "graph g() { topology { @fast node a () => (o: m) = process('a'); } }" },
        { "E0304", M + "graph g() { topology { node a () => (o: m, p: m) = process('a'); node b (i: m) = process('b'); edge a --> b; } }" },
        { "E0303", M + "graph g() { topology { node a () => (o: m) = process('a'); node b (i: m) = process('b'); edge b.i --> a.o; } }" },
        { "E0401", "type r { v: string } graph g() { topology { node a[r] (i: r) = process('a'); } }" },
        { "E0409", M + "graph g() { topology { node a () => (o: m) = process('a'); node b (i: m) = process('b'); edge a --> b by v; } }" },
        { "E0410", "key k: string; type r { id: k }\ngraph g() { topology { node a () => (o: r) = process('a'); node b[k] (i: r) = process('b'); edge a -*> b; } }" },
        { "E0513", M + "graph g() { topology { node s[] () => (o: m) = process('s'); node k (i: m sequenced) = process('k'); edge s --> k; } }" },
        { "E0606", M + "graph g() { topology { node a () => (o: m) = process('a'); node b (i: m) = process('b'); edge a --> b; } policy { lateness(5ms): b.i; } }" },
        { "E0607", "clock c: u64; type r { ts: c } type q { v: string }\ngraph g() { topology { node a () => (o: r ordered) = process('a'); node b (i: r ordered, late: q) = process('b'); edge a --> b.i; } policy { on_late(route(b.late)): b.i; } }" },
        { "E0604", M + "graph g() { topology { node a () => (o: m) = process('a'); } policy { pin('h*'): a[0]; } }" },
        { "E0704", M + "graph g() { topology { node a () => (o: m) = process('a'); } policy { label(x): a.o; } }" },
        { "E0710", "labels { a; } type r { n: i32 } labeller f (x: r) = x.n;" },
        { "E0711", "labels { a; } type r { n: i32 } labeller f (x: r) = a; labeller h (x: r) = f(x);" },
        { "E0712", "labels { a < b; } type r { n: i32 } type q { s: string } labeller f (x: q) = a;\ngraph g() { topology { node s () => (o: r) = process('s'); } policy { label(f): s.o; } }" },
        { "E0713", "labels { a < b; } type r { n: i32 } labeller f (x: r, y: label) = y;\ngraph g() { topology { node s () => (o: r) = process('s'); node k (i: r) = process('k'); edge s --> k; } policy { label(f): k.i; } }" },
        { "E0720", "labels { a < b; }\n" + M + "graph g() { topology { node s () => (o: m) = process('s'); } policy { label(dynamic: a..b): s.o; } }" },
        { "E0722", "labels { a < b; }\n" + M + "graph g() { topology { node s () => (o: m) = process('s'); } policy { label(host: a..b): s[*]; } }" },
        { "E0723", "labels { a < b; }\n" + M + "graph g() { topology { node s () => (o: m) = process('s'); } policy { label(a): s.o; label(b): s.o; } }" },
        { "E0724", "labels { a < b; }\n" + M + "graph g() { topology { node s () => (o: m) = process('s'); node k (i: m) = process('k'); edge s --> k; } policy { label(a): k.i; label(b): k[*].i; } }" },
        { "E0731", "labels { public < secret; }\n" + M + "graph g() { topology { node s () => (o: m) = process('a'); node k (i: m) = process('b'); edge s --> k; } policy { label(secret): s.o; label(public): k; } }" },
        { "E0206", "labels { a; } labeller f (x: json) = x.v == 'y' ? a : a;" },
        { "E0203", "type r { next: r }" },
        { "E0102", "type i32 { a: string }" },
        { "E0104", M + "graph g() { topology { node a (i: m, i: m) = process('a'); } }" },
        { "E0201", "type r { a: i32, a: string }" },
        { "E0202", "graph g() { topology { node a () => (o: i32) = process('a'); } }" },
        { "E0402", "key k: string; type r { id: k }\ngraph g() { topology { node a () => (o: r) = process('a'); node b[k, k] (i: r) = process('b'); edge a --> b; } }" },
        { "E0404", "key k: string; type r { id: k }\ngraph g() { topology { node a () => (o: r) = process('a'); node b[k] (i: r) = process('b'); edge a --> b; edge a -*> b by id; } }" },
        { "E0407", "key k: string; type r { id: k }\ngraph g() { topology { node a () => (o: r) = process('a'); node b[k] (i: r) = process('b'); edge a --> b by (id, id); } }" },
        { "E0408", "key k: string; type r { id: k, s: string }\ngraph g() { topology { node a () => (o: r) = process('a'); node b[k] (i: r) = process('b'); edge a --> b by s; } }" },
        { "E0501", "type r { n: i64 }\ngraph g() { topology { node a () => (o: r ordered by n) = process('a'); } }" },
        { "E0503", "clock c: u64; type r { t1: c, t2: c }\ngraph g() { topology { node a () => (o: r ordered) = process('a'); } }" },
        { "E0505", "clock c: u64; clock d: u64; type r { ts: c } type q { ts: d }\ngraph g() { topology { node a () => (o: r ordered) = process('a'); node b (i: q ordered) = process('b'); edge a --> b; } }" },
        { "E0507", "clock c: u64; type r { ts: c }\ngraph g() { topology { node a () => (o: r ordered) = process('a'); node b (i: r ordered per nope) = process('b'); edge a --> b; } }" },
        { "E0509", "clock c: u64; type r { ts: c, k: string }\ngraph g() { topology { node a () => (o: r ordered per k) = process('a'); } }" },
        { "E0510", M + "graph g() { topology { node a () => (o: m) = process('a'); node b () => (o: m) = process('b'); node k (i: m sequenced) = process('k'); edge a --> k; edge b --> k; } }" },
        { "E0512", M + "graph g() { topology { node a () => (o: m) = process('a'); node b () => (o: m) = process('b'); @one_to_one node x[] (i: m) => (o: m) = process('x'); node k (i: m sequenced) = process('k'); edge a --> x; edge b --> x; edge x --> k; } }" },
        { "E0514", M + "graph g() { topology { node a () => (o: m sequenced) = process('a'); } }" },
    };

    public static TheoryData<string, string> WarningPrograms => new()
    {
        { "W0101", M + "graph g() { topology { node m () => (o: m) = process('a'); } }" },
        { "W0102", "labels { a < b; } type r { n: i32 } labeller f (a: r) = b;" },
        { "W0301", "graph g() { topology { node a = process('a'); } }" },
        { "W0302", M + "graph g() { topology { node a () => (o: m) = process('a'); } }" },
        { "W0305", M + "graph g() { topology { node a () => (o: json) = process('a'); node b (i: m) = process('b'); edge a --> b; } }" },
        { "W0501", M + "graph g() { topology { node s () => (o: m) = process('s'); @one_to_one node x (i: m) => (o: m) = process('x'); node k (i: m sequenced) = process('k'); edge s --> x; edge x --> k; } }" },
        { "W0503", "clock c: u64; type r { ts: c }\ngraph g() { topology { node a () => (o: r ordered) = process('a'); node b () => (o: r ordered) = process('b'); node k (i: r ordered) = process('k'); edge a --> k; edge b --> k; } }" },
        { "W0731", "labels { public < secret; }\n" + M + "graph g() { topology { node s () => (o: m) = process('a'); node k (i: m) = process('b'); edge s --> k; } policy { label(x: m => x.v == 'y' ? secret : public): s.o; label(public): k; } }" },
    };

    [Theory]
    [MemberData(nameof(WarningPrograms))]
    public void ReportsExpectedWarning(string code, string src)
    {
        var p = Compile(src);
        AssertNoErrors(p);
        AssertHas(p, code);
    }

    [Theory]
    [MemberData(nameof(Programs))]
    public void ReportsExpectedError(string code, string src) => AssertHas(Compile(src), code);

    [Fact]
    public void LatenessBelowSenderPromiseIsW0502()
    {
        var p = Compile("clock c: u64; type r { ts: c }\ngraph g() { topology { node a () => (o: r ordered within 100ms) = process('a'); node b (i: r ordered) = process('b'); edge a --> b; } policy { lateness(50ms): b.i; } }");
        AssertNoErrors(p);
        AssertHas(p, "W0502");
    }

    [Fact]
    public void LatenessAlongFlowAboveBoundIsW0901()
    {
        var p = Compile("clock c: u64; type r { ts: c }\ngraph g() { topology { node a () => (o: r ordered) = process('a'); @one_to_one node b (i: r ordered) => (o: r ordered) = process('b'); node k (i: r ordered) = process('k'); edge e1: a --> b; edge e2: b --> k; flow f = { e1, e2 }; } policy { lateness(50ms): b.i; max_latency(10ms): f; } }");
        AssertNoErrors(p);
        AssertHas(p, "W0901");
    }

    [Fact]
    public void E0730CarriesAWitnessPath()
    {
        var p = Compile(Programs.Single(x => (string)x[0] == "E0730")[1].ToString()!);
        var d = p.Diagnostics.Single(d => d.Code == "E0730");
        Assert.Equal("path: s1.video [secret] -s1.video__sink.i-> sink.i [ceiling public]", d.Notes[0]);
    }

    [Fact]
    public void E0733HasOnePathPerCompartment()
    {
        var p = Compile(Programs.Single(x => (string)x[0] == "E0733")[1].ToString()!);
        var d = p.Diagnostics.Single(d => d.Code == "E0733");
        Assert.Contains("a_internal ⊔ b_ops = top", d.Message);
        Assert.Equal(2, d.Notes.Count);
        Assert.Contains("-e1->", d.Notes[0]);
        Assert.Contains("-e2->", d.Notes[1]);
    }

    [Fact]
    public void LabelAnalysisDoesNotRunAfterStructuralErrors()
    {
        var p = Compile("labels { public < secret; }\n" + M + "graph g() { topology { node s1 () => (o: m) = process('a'); node sink (i: m) = process('b'); edge s1 --> sink; node bad (i: m) = process('c'); } policy { label(secret): s1.o; label(public): sink.i; } }");
        AssertHas(p, "E0307");
        AssertLacks(p, "E0730");
        Assert.Null(p.Graphs.Single().Analysis);
    }
}
