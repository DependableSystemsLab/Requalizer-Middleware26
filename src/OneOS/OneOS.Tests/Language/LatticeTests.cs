using OneOS.Runtime.Language;
using static OneOS.Tests.Language.TestUtil;

namespace OneOS.Tests.Language;

// L§8.1 and the lattice cases of L§13.
public class LatticeTests
{
    private static LabelLattice Lattice(string labels)
    {
        var p = Compile(labels);
        AssertNoErrors(p);
        return p.Lattice;
    }

    [Fact]
    public void ChainHasDeclaredBottomAndTop()
    {
        var L = Lattice("labels { public < internal; internal < secret; }");
        Assert.Equal("public", L.Bottom);
        Assert.Equal("secret", L.Top);
        Assert.False(L.ForbiddenTop);
        Assert.True(L.Leq("public", "secret"));
        Assert.Equal("internal", L.Join("public", "internal"));
    }

    [Fact]
    public void TreeGetsCompartmentTopAndNoBottom()
    {
        var L = Lattice("labels { internal < hr; internal < eng; }");
        Assert.Contains("top(internal)", L.Labels);
        Assert.DoesNotContain("bottom", L.Labels);
        Assert.Equal("top(internal)", L.Join("hr", "eng"));
        Assert.False(L.ForbiddenTop);
        Assert.Equal("top(internal)", L.Top);
        Assert.Equal("internal", L.Bottom);
    }

    [Fact]
    public void TreeWithPublicRootCompiles()
    {
        var L = Lattice("labels { public < internal; internal < hr; internal < eng; }");
        Assert.Equal("top(public)", L.Join("hr", "eng"));
    }

    [Fact]
    public void TwoCompartmentsGetSharedBottomAndForbiddenTop()
    {
        var L = Lattice("labels { a < b; c < d; }");
        Assert.Equal("bottom", L.Bottom);
        Assert.Equal("top", L.Top);
        Assert.True(L.ForbiddenTop);
        Assert.Equal("top", L.Join("b", "c"));
        Assert.True(L.Leq("bottom", "a") && L.Leq("bottom", "c"));
        Assert.False(L.SameCompartment(new[] { L.IndexOf("a"), L.IndexOf("d") }));
        Assert.True(L.SameCompartment(new[] { L.IndexOf("a"), L.IndexOf("b"), L.IndexOf("bottom") }));
        Assert.False(L.Strict);
    }

    [Fact]
    public void StrictModeKeepsTheSameCompletion()
    {
        var L = Lattice("labels strict { a < b; c < d; }");
        Assert.True(L.Strict);
        Assert.Equal("bottom", L.Bottom);
        Assert.True(L.ForbiddenTop);
    }

    [Fact]
    public void TwoCompartmentExampleFromSpec()
    {
        var L = Lattice("labels { a_public < a_internal; a_internal < a_hr; a_internal < a_eng; b_public < b_internal; b_internal < b_ops; }");
        Assert.Equal("top(a_public)", L.Join("a_hr", "a_eng"));
        Assert.Equal("top", L.Join("a_hr", "b_ops"));
    }

    [Fact]
    public void IsolatedLabelFormsItsOwnCompartment()
    {
        var L = Lattice("labels { a < b; solo; }");
        Assert.Equal(2, L.CompartmentCount);
        Assert.True(L.ForbiddenTop);
    }

    [Fact]
    public void NoLabelsBlockIsTheSingleBottom()
    {
        var p = Compile("type m { v: string }");
        Assert.Equal(new[] { "bottom" }, p.Lattice.Labels);
        Assert.False(p.Lattice.Declared);
    }

    [Fact]
    public void MeetTable()
    {
        var L = Lattice("labels { public < internal; internal < hr; internal < eng; }");
        Assert.Equal("internal", L.Meet("hr", "eng"));
        Assert.Equal("hr", L.Meet("hr", "top(public)"));
    }

    [Theory]
    [InlineData("labels { a < b; b < a; }", "E0701")]
    [InlineData("labels { a < c; a < d; b < c; b < d; }", "E0703")]
    [InlineData("labels { top < a; }", "E0706")]
    [InlineData("labels { bottom < a; }", "E0706")]
    public void InvalidLattices(string src, string code) => AssertHas(Compile(src), code);

    [Fact]
    public void E0703NamesThePair()
    {
        var d = Compile("labels { a < c; a < d; b < c; b < d; }").Diagnostics.Single(d => d.Code == "E0703");
        Assert.Contains("'a' and 'b'", d.Message);
        Assert.Contains("declare a label", d.Notes[0]);
    }

    [Fact]
    public void TwoLabelsBlocksAcrossFiles() =>
        AssertHas(Compile("labels { a < b; }", "labels { c < d; }"), "E0707");
}
