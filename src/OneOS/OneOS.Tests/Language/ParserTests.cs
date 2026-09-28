using OneOS.Runtime.Language;
using OneOS.Runtime.Language.Ast;
using static OneOS.Tests.Language.TestUtil;

namespace OneOS.Tests.Language;

public class ParserTests
{
    private static (AppAst Ast, IReadOnlyList<Diagnostic> Diags) Parse(string src) => new AppParser().Parse(src, "t.osh");

    private static GraphDecl OnlyGraph(string src)
    {
        var (ast, diags) = Parse(src);
        Assert.Empty(diags);
        return Assert.Single(ast.Items.OfType<GraphDecl>());
    }

    private const string M = "type m { v: string }\n";

    [Fact]
    public void UnnamedEdgesParse()
    {
        var g = OnlyGraph(M + "graph g() { topology { node a () => (o: m) = process('x'); node b (i: m) = process('y'); edge a.o --> b.i; edge a --> b; } }");
        Assert.Equal(2, g.Edges.Count);
        Assert.All(g.Edges, e => Assert.Null(e.Name));
        Assert.Null(g.Edges[1].From.Port);
    }

    [Fact]
    public void RelationalOperatorsParse()
    {
        var (ast, diags) = Parse("labels { a < b; } type r { n: i32 } labeller f (x: r) = x.n < 3 && x.n > 1 || x.n >= 7 && x.n <= 9 ? a : b;");
        Assert.Empty(diags);
        var body = (TernaryExpr)ast.Items.OfType<LabellerDecl>().Single().Body;
        var or = Assert.IsType<BinaryExpr>(body.Condition);
        Assert.Equal("||", or.Op);
        Assert.Equal("&&", Assert.IsType<BinaryExpr>(or.Left).Op);
        Assert.Equal("<", Assert.IsType<BinaryExpr>(((BinaryExpr)or.Left).Left).Op);
    }

    [Fact]
    public void PrecedenceFollowsSpec()
    {
        var (ast, _) = Parse("labels { a; } type r { n: i32 } labeller f (x: r) = x.n + 1 == 3 - -x.n ? a : a;");
        var cond = (BinaryExpr)((TernaryExpr)ast.Items.OfType<LabellerDecl>().Single().Body).Condition;
        Assert.Equal("==", cond.Op);
        Assert.Equal("+", ((BinaryExpr)cond.Left).Op);
        var minus = (BinaryExpr)cond.Right;
        Assert.Equal("-", minus.Op);
        Assert.Equal("-", Assert.IsType<UnaryExpr>(minus.Right).Op);
    }

    [Fact]
    public void TrailingCommaInTypeAndStringEscapes()
    {
        var (ast, diags) = Parse("type r { a: i32, b: i32, }\n" + "graph g() { topology { node a () => (o: r) = process('it\\'s', 'a\\\\b', 'x\\ny'); } }");
        Assert.Empty(diags);
        Assert.Equal(2, ast.Items.OfType<TypeDecl>().Single().Fields.Count);
        var proc = ast.Items.OfType<GraphDecl>().Single().Nodes[0].Process;
        Assert.Equal("it's", ((StringLiteral)proc.Command).Value);
        Assert.Equal("a\\b", ((StringLiteral)proc.Args[0]).Value);
        Assert.Equal("x\ny", ((StringLiteral)proc.Args[1]).Value);
    }

    [Fact]
    public void LabelSources()
    {
        var g = OnlyGraph("labels { host < b; }\n" + M + @"graph g() { topology { node a () => (o: m) = process('x'); }
            policy {
              label(x: m => b): a.o;
              label((x: m, y: label) => join(y, host)): a.o;
              label(host): a.o;
              label(host: host..b): a;
              label(dynamic: host..b): a;
            } }");
        var sources = g.Policies.OfType<LabelPolicy>().Select(p => p.Source).ToList();
        Assert.IsType<LambdaLabelSource>(sources[0]);
        Assert.Equal(2, Assert.IsType<LambdaLabelSource>(sources[1]).Parameters.Count);
        Assert.Equal("host", Assert.IsType<NamedLabelSource>(sources[2]).Name);
        Assert.IsType<HostLabelSource>(sources[3]);
        Assert.IsType<DynamicLabelSource>(sources[4]);
        Assert.Equal("x: m => b", ((LambdaLabelSource)sources[0]).SourceText);
    }

    [Fact]
    public void NumberTokens()
    {
        var g = OnlyGraph(M + "graph g() { topology { node a () => (o: m) = process('x'); } policy { partitions(2..8): a; lateness(200ms): a.o; max_buffer(64MB): a.o; min_rate(1.5, 99.9): a.o; } }");
        var p = g.Policies.OfType<GenericPolicy>().ToList();
        Assert.Equal(2, ((IntLiteral)p[0].Args[0].Value).Value);
        Assert.Equal(8, ((IntLiteral)p[0].Args[0].RangeEnd!).Value);
        Assert.Equal(200_000_000, ((DurationLiteral)p[1].Args[0].Value).Nanoseconds);
        Assert.Equal(64L << 20, ((SizeLiteral)p[2].Args[0].Value).Bytes);
        Assert.Equal(1.5, ((FloatLiteral)p[3].Args[0].Value).Value);
    }

    [Theory]
    [InlineData("type r { a: i32 } graph g() { topology { node a () => (o: r) = process('x'); } policy { lateness(200xs): a.o; } }", "unrecognized literal suffix")]
    [InlineData("type r { a: i32 } /* never closed", "unterminated block comment")]
    [InlineData("type r { a: i32 } graph g() { topology { node a () => (o: r) = process('x); } }", "unterminated string")]
    [InlineData("type r { a: i32 # }", "invalid character")]
    [InlineData("type r { a: i32 } graph g() { topology { node a () => (o: r) = process('x'); } policy { lateness(1.5s): a.o; } }", "must be integers")]
    public void LexicalErrorsAreE0001(string src, string message)
    {
        var (_, diags) = Parse(src);
        var d = Assert.Single(diags);
        Assert.Equal("E0001", d.Code);
        Assert.Contains(message, d.Message);
    }

    [Fact]
    public void SyntaxErrorsRecoverAtNextTopLevelItem()
    {
        var (ast, diags) = Parse("type a { x: i32 }\ntype b { x i32 }\ntype c { x: i32 }\ngraph g( { }\ntype d { x: i32 }");
        Assert.Equal(new[] { "E0002", "E0002" }, diags.Select(d => d.Code));
        Assert.Equal(new[] { "a", "c", "d" }, ast.Items.OfType<TypeDecl>().Select(t => t.Name));
        Assert.Contains("expected", diags[0].Message);
        Assert.Equal(2, diags[0].Span.StartLine);
    }

    [Fact]
    public void SpansCoverEveryNodeAndExcludeLeadingTrivia()
    {
        var src = M + "// comment\ngraph g() {\n  topology {\n    node a () => (o: m) = process('x');\n    node b (i: m) = process('y');\n    edge e: a.o --> b.i;\n  }\n  policy { always: b; }\n}";
        var g = OnlyGraph(src);
        Assert.Equal(new SourceSpan("t.osh", 3, 1, 10, 2), g.Span);
        Assert.Equal(new SourceSpan("t.osh", 5, 5, 5, 40), g.Nodes[0].Span);
        Assert.Equal(new SourceSpan("t.osh", 7, 5, 7, 25), g.Edges[0].Span);
        var policy = g.Policies[0];
        Assert.Equal(9, policy.Span.StartLine);
        Assert.Equal(12, policy.Span.StartCol);
        Assert.Equal(20, policy.Targets[0].Span.StartCol);

        // Walk every AST node reachable through records and check it has a span.
        var missing = new List<string>();
        void Walk(object? o)
        {
            switch (o)
            {
                case AppNode n:
                    if (n.Span == SourceSpan.None) missing.Add(n.GetType().Name);
                    foreach (var prop in n.GetType().GetProperties().Where(p => p.Name != "Span" && p.GetIndexParameters().Length == 0))
                        Walk(prop.GetValue(n));
                    break;
                case string: break;
                case System.Collections.IEnumerable e:
                    foreach (var x in e) Walk(x);
                    break;
            }
        }
        Walk(g);
        Assert.Empty(missing);
    }

    [Fact]
    public void SpawnCommand()
    {
        var (cmd, diags) = new AppParser().ParseSpawn("spawn foo('cam-a', 'cam-b', 3, true)");
        Assert.Empty(diags);
        Assert.Equal("foo", cmd!.Graph);
        Assert.Equal(4, cmd.Args.Count);
    }

    [Fact]
    public void ReservedKeywordsAreNotIdentifiers()
    {
        var (_, diags) = Parse("type node { x: i32 }");
        Assert.Equal("E0002", Assert.Single(diags).Code);
    }

    [Fact]
    public void ContextualKeywordsAreIdentifiers()
    {
        var (ast, diags) = Parse("type label { in: i32, host: string, strict: bool, spawn: i32 }");
        Assert.Empty(diags);
        Assert.Equal(4, ast.Items.OfType<TypeDecl>().Single().Fields.Count);
    }
}
