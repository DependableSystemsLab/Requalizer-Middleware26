using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Pidgin;
using Pidgin.Expression;
using OneOS.Runtime.Language.Ast;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

namespace OneOS.Runtime.Language;

// Parser for the dataflow DSL (L§2, L§3).
//
// Tokens consume their *trailing* trivia; leading trivia is skipped once per top-level item,
// so every span starts at the node's first token. Each top-level item is parsed separately:
// on a syntax error (E0002) the parser resumes at the next top-level keyword (L§12).
// Lexical errors (E0001) are found by a pre-scan that follows the same token rules.
//
// Instances are not thread-safe; create one per compilation.
public sealed class AppParser
{
    public static readonly IReadOnlySet<string> ReservedKeywords = new HashSet<string>
    {
        "graph", "topology", "policy", "node", "edge", "flow", "labels", "key", "clock", "type", "labeller",
        "process", "ordered", "sequenced", "by", "per", "within", "unit", "dynamic", "list", "true", "false",
    };

    private static readonly HashSet<string> TopKeywords = new() { "labels", "key", "clock", "type", "labeller", "graph" };
    private static readonly HashSet<string> DurationUnits = new() { "ns", "us", "ms", "s", "m", "h" };
    private static readonly HashSet<string> SizeUnits = new() { "B", "KB", "MB", "GB" };
    private const string Punctuation = "{}()[]<>:;,.?!+-*@=&|";

    // Per-parse state read by the parser closures.
    private string _text = "";
    private string _file = "";
    private int _base;
    private int _lastTokenEnd;
    private int[] _lineStarts = Array.Empty<int>();

    private readonly Parser<char, Unit> _trivia;
    private readonly Parser<char, Maybe<(TopItem Item, int End)>> _topItem;
    private readonly Parser<char, SpawnCommand> _spawn;

    public AppParser()
    {
        var lineComment = Try(String("//")).Then(Token(c => c != '\n').SkipMany());
        var blockComment = Try(String("/*")).Then(Any.SkipUntil(Try(String("*/"))));
        _trivia = OneOf(Whitespace.SkipAtLeastOnce(), lineComment, blockComment).SkipMany();

        var identStart = Token(c => char.IsAsciiLetter(c) || c == '_');
        var identChar = Token(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        var rawIdent = Map((f, r) => f + r, identStart, identChar.ManyString());

        Parser<char, string> Kw(string k) => Tok(Try(String(k).Before(Not(identChar)))).Labelled($"'{k}'");
        Parser<char, string> Ctx(string w) => Tok(Try(String(w).Before(Not(identChar)))).Labelled($"'{w}'");
        Parser<char, string> Sym(string s) => Tok(Try(String(s))).Labelled($"'{s}'");

        var ident = Tok(Try(rawIdent.Where(s => !ReservedKeywords.Contains(s)))).Labelled("identifier");
        var comma = Sym(",");
        var colon = Sym(":");
        var semi = Sym(";");
        var dot = Tok(Try(Char('.').Before(Not(Char('.'))))).Labelled("'.'");
        var range = Sym("..");
        var assign = Tok(Try(Char('=').Before(Not(Char('=').Or(Char('>')))))).Labelled("'='");
        var arrow = Sym("=>");

        // --- Literals ---
        var number = Tok(Map((s, digits, frac, suffix, e) => MakeNumber(digits, frac, suffix),
            CurrentOffset,
            Digit.AtLeastOnceString(),
            Try(Char('.').Then(Digit.AtLeastOnceString())).Optional(),
            Token(char.IsAsciiLetter).ManyString(),
            CurrentOffset)).Labelled("number");

        var escape = Char('\\').Then(OneOf(
            Char('\''), Char('\\'), Char('n').ThenReturn('\n'), Char('t').ThenReturn('\t')));
        var stringChar = Token(c => c != '\'' && c != '\\' && c != '\n' && c != '\r').Or(escape);
        var stringLit = Tok(Char('\'').Then(stringChar.ManyString()).Before(Char('\'')))
            .Select(s => (Expression)new StringLiteral(s)).Labelled("string");

        var boolLit = Kw("true").ThenReturn((Expression)new BoolLiteral(true))
            .Or(Kw("false").ThenReturn((Expression)new BoolLiteral(false)));

        var fieldPath = Map((first, rest) => (IReadOnlyList<string>)new[] { first }.Concat(rest).ToList(),
            ident, Try(dot.Then(ident)).Many()).Labelled("field path");

        // --- Expressions (L§3.7) ---
        Parser<char, Expression> expr = null!;
        var exprRec = Rec(() => expr);

        var primary = Spanned(OneOf(
            number,
            stringLit,
            boolLit,
            ident.Select(n => (Expression)new IdentExpr(n)),
            Sym("(").Then(exprRec).Before(Sym(")"))));

        var callArgs = Sym("(").Then(exprRec.Separated(comma)).Before(Sym(")")).Select(a => (IReadOnlyList<Expression>)a.ToList());
        var postfixOp = OneOf(
            callArgs.Select<Func<Expression, Expression>>(args => t => new CallExpr(t, args)),
            Try(dot.Then(ident)).Select<Func<Expression, Expression>>(name => t => new MemberExpr(t, name)));
        var postfix = Map((start, p, ops) =>
            {
                var result = p;
                foreach (var (op, end) in ops)
                {
                    result = op(result);
                    result.Span = MakeSpan(start, end);
                }
                return result;
            },
            CurrentOffset, primary, Map((op, end) => (op, end), postfixOp, CurrentOffset).Many());

        Parser<char, Func<Expression, Expression, Expression>> Bin(string op, Parser<char, string> tok) =>
            tok.ThenReturn<Func<Expression, Expression, Expression>>((l, r) =>
                new BinaryExpr(l, op, r) { Span = Merge(l.Span, r.Span) });
        Parser<char, Func<Expression, Expression>> Pre(string op, Parser<char, string> tok) =>
            Map((s, _) => s, CurrentOffset, tok).Select<Func<Expression, Expression>>(s => e =>
                new UnaryExpr(op, e) { Span = Merge(MakeSpan(s, s + 1), e.Span) });

        var notTok = Tok(Try(String("!").Before(Not(Char('='))))).Labelled("'!'");
        var minusTok = Tok(Try(String("-").Before(Not(Char('-').Or(Char('*')))))).Labelled("'-'");
        var ltTok = Tok(Try(String("<").Before(Not(Char('='))))).Labelled("'<'");
        var gtTok = Tok(Try(String(">").Before(Not(Char('='))))).Labelled("'>'");

        var orExpr = ExpressionParser.Build(postfix, new[]
        {
            Operator.PrefixChainable(Pre("!", notTok), Pre("-", minusTok)),
            Operator.InfixL(Bin("+", Sym("+"))).And(Operator.InfixL(Bin("-", minusTok))),
            Operator.InfixL(Bin("<=", Sym("<="))).And(Operator.InfixL(Bin(">=", Sym(">="))))
                .And(Operator.InfixL(Bin("<", ltTok))).And(Operator.InfixL(Bin(">", gtTok))),
            Operator.InfixL(Bin("==", Sym("=="))).And(Operator.InfixL(Bin("!=", Sym("!=")))),
            Operator.InfixL(Bin("&&", Sym("&&"))),
            Operator.InfixL(Bin("||", Sym("||"))),
        });

        expr = Map((c, rest) => rest.HasValue
                ? new TernaryExpr(c, rest.Value.Item1, rest.Value.Item2) { Span = Merge(c.Span, rest.Value.Item2.Span) }
                : c,
            orExpr,
            Sym("?").Then(Map((t, f) => (t, f), exprRec, colon.Then(exprRec))).Optional()).Labelled("expression");

        // --- Types ---
        Parser<char, TypeRef> typeRef = null!;
        var typeRefRec = Rec(() => typeRef);
        typeRef = Spanned(OneOf(
            Kw("list").Then(Sym("<")).Then(typeRefRec).Before(Sym(">")).Select(t => (TypeRef)new ListTypeRef(t)),
            ident.Select(n => (TypeRef)new NamedTypeRef(n)))).Labelled("type");

        var param = Spanned(Map((n, t) => new Parameter(n, t), ident, colon.Then(typeRef)));

        // --- Top-level declarations ---
        var labelRel = Spanned(Map((first, rest) => new LabelRelation(new[] { first }.Concat(rest).ToList()),
            ident, Sym("<").Then(ident).Many()).Before(semi));
        var labelsDecl = Spanned(Map((strict, rels) => (TopItem)new LabelsDecl(strict.HasValue, rels.ToList()),
            Kw("labels").Then(Ctx("strict").Optional()), Sym("{").Then(labelRel.Many()).Before(Sym("}"))));

        var keyDecl = Spanned(Map((n, t) => (TopItem)new KeyDecl(n, t),
            Kw("key").Then(ident), colon.Then(typeRef).Before(semi)));

        var timeUnit = Tok(Try(OneOf(Try(String("ns")), Try(String("us")), Try(String("ms")), String("s"))
            .Before(Not(identChar)))).Labelled("time unit (ns, us, ms, s)");
        var clockDecl = Spanned(Map((n, t, u) => (TopItem)new ClockDecl(n, t, u.GetValueOrDefault("ms")),
            Kw("clock").Then(ident), colon.Then(typeRef), Kw("unit").Then(timeUnit).Optional().Before(semi)));

        var field = Spanned(Map((n, t) => new FieldDecl(n, t), ident, colon.Then(typeRef)));
        var typeDecl = Spanned(Map((n, fs) => (TopItem)new TypeDecl(n, fs.ToList()),
            Kw("type").Then(ident), Sym("{").Then(field.SeparatedAndOptionallyTerminated(comma)).Before(Sym("}"))));

        var labellerDecl = Spanned(WithText(Map((n, ps, body) => (n, ps.ToList(), body),
                Kw("labeller").Then(ident),
                Sym("(").Then(param.SeparatedAtLeastOnce(comma)).Before(Sym(")")),
                assign.Then(expr).Before(semi)))
            .Select(x => (TopItem)new LabellerDecl(x.Value.n, x.Value.Item2, x.Value.body, x.Text)));

        // --- Topology ---
        var withinDuration = number.Bind(e => e is DurationLiteral d
            ? Return(d)
            : Fail<DurationLiteral>("expected a duration"));
        var orderSpec = Spanned(OneOf(
            Map((by, per, within) => (OrderSpec)new OrderedSpec(by.GetValueOrDefault(), per.GetValueOrDefault(), within.GetValueOrDefault()),
                Kw("ordered").Then(Kw("by").Then(fieldPath).Optional()),
                Kw("per").Then(fieldPath).Optional(),
                Kw("within").Then(Spanned(withinDuration)).Optional()),
            Kw("sequenced").ThenReturn((OrderSpec)new SequencedSpec())));

        var port = Spanned(Map((n, t, o) => new PortDecl(n, t, o.GetValueOrDefault()),
            ident, colon.Then(typeRef), orderSpec.Optional()));
        var portList = Sym("(").Then(port.Separated(comma)).Before(Sym(")")).Select(ps => (IReadOnlyList<PortDecl>)ps.ToList());

        var processExpr = Spanned(Kw("process").Then(Sym("(")).Then(expr.SeparatedAtLeastOnce(comma)).Before(Sym(")"))
            .Select(args => { var l = args.ToList(); return new ProcessExpr(l[0], l.Skip(1).ToList()); }));

        var attribute = Spanned(Sym("@").Then(ident).Select(n => new NodeAttribute(n)));
        var partitionSpec = Spanned(Sym("[").Then(ident.Separated(comma)).Before(Sym("]")).Select(ks => new PartitionSpec(ks.ToList())));
        var nodeDecl = Spanned(Map((attrs, name, part, ins, outs, proc) =>
                new NodeDecl(attrs.ToList(), name, part.GetValueOrDefault(),
                    ins.GetValueOrDefault(Array.Empty<PortDecl>()), outs.GetValueOrDefault(Array.Empty<PortDecl>()), proc),
            attribute.Many(),
            Kw("node").Then(ident),
            partitionSpec.Optional(),
            portList.Optional(),
            arrow.Then(portList).Optional(),
            assign.Then(processExpr).Before(semi)));

        var endpoint = Spanned(Map((n, p) => new Endpoint(n, p.GetValueOrDefault()), ident, Try(dot.Then(ident)).Optional()));
        var edgeOp = Sym("-->").ThenReturn(EdgeOp.Routed).Or(Sym("-*>").ThenReturn(EdgeOp.AllPartitions)).Labelled("'-->' or '-*>'");
        var keyFields = Sym("(").Then(fieldPath.SeparatedAtLeastOnce(comma)).Before(Sym(")")).Select(ps => (IReadOnlyList<IReadOnlyList<string>>)ps.ToList())
            .Or(fieldPath.Select(p => (IReadOnlyList<IReadOnlyList<string>>)new[] { p }));
        var edgeDecl = Spanned(Map((name, from, op, to, by) => new EdgeDecl(name.GetValueOrDefault(), from, op, to, by.GetValueOrDefault()),
            Kw("edge").Then(Try(ident.Before(colon)).Optional()),
            endpoint, edgeOp, endpoint,
            Kw("by").Then(keyFields).Optional().Before(semi)));

        var flowDecl = Spanned(Map((n, es) => new FlowDecl(n, es.ToList()),
            Kw("flow").Then(ident), assign.Then(Sym("{")).Then(ident.SeparatedAtLeastOnce(comma)).Before(Sym("}")).Before(semi)));

        var topologyItem = OneOf(
            nodeDecl.Select(x => (AppNode)x),
            edgeDecl.Select(x => (AppNode)x),
            flowDecl.Select(x => (AppNode)x));
        var topology = Kw("topology").Then(Sym("{")).Then(topologyItem.Many()).Before(Sym("}"));

        // --- Policy (L§3.6) ---
        var index = Sym("*").ThenReturn((TargetIndexKind.All, 0))
            .Or(Tok(Digit.AtLeastOnceString()).Select(d => (TargetIndexKind.At, int.Parse(d, CultureInfo.InvariantCulture))));
        var target = Spanned(Map((n, idx, p) => new Target(n, idx.HasValue ? idx.Value.Item1 : TargetIndexKind.None,
                idx.HasValue ? idx.Value.Item2 : 0, p.GetValueOrDefault()),
            ident, Sym("[").Then(index).Before(Sym("]")).Optional(), Try(dot.Then(ident)).Optional())).Labelled("policy target");
        var targets = colon.Then(target.SeparatedAtLeastOnce(comma)).Before(semi).Select(ts => (IReadOnlyList<Target>)ts.ToList());

        var lambdaHead = Try(param.Select(p => (IReadOnlyList<Parameter>)new[] { p }).Before(arrow))
            .Or(Try(Sym("(").Then(param.SeparatedAtLeastOnce(comma)).Before(Sym(")")).Before(arrow)).Select(ps => (IReadOnlyList<Parameter>)ps.ToList()));
        var labelSource = Spanned(OneOf(
            Map((lo, hi) => (LabelSource)new DynamicLabelSource(lo, hi), Kw("dynamic").Then(colon).Then(ident), range.Then(ident)),
            Map((lo, hi) => (LabelSource)new HostLabelSource(lo, hi), Try(Ctx("host").Then(colon).Then(ident).Before(range)), ident),
            WithText(Map((ps, body) => (ps, body), lambdaHead, expr))
                .Select(x => (LabelSource)new LambdaLabelSource(x.Value.ps, x.Value.body, x.Text)),
            ident.Select(n => (LabelSource)new NamedLabelSource(n)))).Labelled("label source");
        var labelStmt = Spanned(Map((src, ts) => (PolicyStatement)new LabelPolicy(src, ts),
            Try(Ctx("label").Before(Lookahead(Char('(')))).Then(Sym("(")).Then(labelSource).Before(Sym(")")), targets));

        var policyArg = Spanned(Map((v, end) => new PolicyArg(v, end.GetValueOrDefault()), expr, range.Then(expr).Optional()));
        var genericStmt = Spanned(Map((n, args, by, ts) => (PolicyStatement)new GenericPolicy(n,
                args.HasValue ? args.Value.ToList() : new List<PolicyArg>(), by.GetValueOrDefault(), ts),
            ident, Sym("(").Then(policyArg.Separated(comma)).Before(Sym(")")).Optional(), Kw("by").Then(fieldPath).Optional(), targets));

        var policy = Kw("policy").Then(Sym("{")).Then(labelStmt.Or(genericStmt).Many()).Before(Sym("}"));

        var graphDecl = Spanned(Map((name, ps, topo, pol) =>
            {
                var items = topo.ToList();
                return (TopItem)new GraphDecl(name, ps.ToList(),
                    items.OfType<NodeDecl>().ToList(), items.OfType<EdgeDecl>().ToList(), items.OfType<FlowDecl>().ToList(),
                    pol.HasValue ? pol.Value.ToList() : new List<PolicyStatement>());
            },
            Kw("graph").Then(ident),
            Sym("(").Then(param.Separated(comma)).Before(Sym(")")),
            Sym("{").Then(topology),
            policy.Optional().Before(Sym("}"))));

        var topItem = OneOf(labelsDecl, keyDecl, clockDecl, typeDecl, labellerDecl, graphDecl).Labelled("top-level declaration");
        _topItem = _trivia.Then(
            Map((item, end) => (item, end), topItem, CurrentOffset).Select(x => Maybe.Just(x))
            .Or(End.ThenReturn(Maybe.Nothing<(TopItem, int)>())));

        _spawn = _trivia.Then(Spanned(Map((g, args) => new SpawnCommand(g, args.ToList()),
            Ctx("spawn").Then(ident), Sym("(").Then(expr.Separated(comma)).Before(Sym(")"))))).Before(End);
    }

    // --- Parser helpers that depend on per-parse state ---

    private Parser<char, T> Tok<T>(Parser<char, T> p) =>
        Map((v, end) => { _lastTokenEnd = _base + end; return v; }, p, CurrentOffset).Before(_trivia);

    private Parser<char, T> Spanned<T>(Parser<char, T> p) where T : AppNode =>
        Map((s, v, e) => { v.Span = MakeSpan(s, e); return v; }, CurrentOffset, p, CurrentOffset);

    private Parser<char, (T Value, string Text)> WithText<T>(Parser<char, T> p) =>
        Map((s, v, e) => (v, _text.Substring(_base + s, TrimEnd(_base + s, _base + e) - (_base + s))), CurrentOffset, p, CurrentOffset);

    // --- Entry points ---

    public (AppAst Ast, IReadOnlyList<Diagnostic> Diagnostics) Parse(string text, string file = "<input>")
    {
        var diags = new DiagnosticBag();
        var (lexErrors, topKeywordOffsets) = Prescan(text, file, diags);
        BeginParse(text, file);

        var items = new List<TopItem>();
        int pos = 0;
        while (pos < text.Length)
        {
            _base = pos;
            var result = _topItem.Parse(text.AsSpan(pos));
            if (result.Success)
            {
                if (!result.Value.HasValue) break;
                items.Add(result.Value.Value.Item);
                pos += result.Value.Value.End;
                continue;
            }

            int errAt = pos + result.Error!.ErrorOffset;
            if (!lexErrors.Any(o => o >= pos && o <= errAt))
                diags.Error("E0002", RenderSyntaxError(result.Error), PointSpan(errAt));
            int next = topKeywordOffsets.FirstOrDefault(o => o >= errAt && o > pos, -1);
            if (next < 0) break;
            pos = next;
        }

        var ast = new AppAst(items) { Span = MakeAbsSpan(0, text.Length) };
        return (ast, diags.Items);
    }

    public (SpawnCommand? Command, IReadOnlyList<Diagnostic> Diagnostics) ParseSpawn(string text)
    {
        var diags = new DiagnosticBag();
        var (lexErrors, _) = Prescan(text, "<spawn>", diags);
        BeginParse(text, "<spawn>");
        var result = _spawn.Parse(text);
        if (result.Success) return (result.Value, diags.Items);
        if (lexErrors.Count == 0) diags.Error("E0002", RenderSyntaxError(result.Error!), PointSpan(result.Error!.ErrorOffset));
        return (null, diags.Items);
    }

    private void BeginParse(string text, string file)
    {
        _text = text;
        _file = file;
        _base = 0;
        _lastTokenEnd = 0;
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        _lineStarts = starts.ToArray();
    }

    // --- Lexical pre-scan (E0001) ---

    // Walks the text with the lexer's token rules. Returns the offsets of lexical errors and of
    // top-level keywords outside comments and strings (the resume points for error recovery).
    private static (List<int> Errors, List<int> TopKeywords) Prescan(string text, string file, DiagnosticBag diags)
    {
        var errors = new List<int>();
        var keywords = new List<int>();
        var lines = new List<int> { 0 };
        for (int k = 0; k < text.Length; k++) if (text[k] == '\n') lines.Add(k + 1);
        SourceSpan At(int s, int e)
        {
            (int, int) Pos(int o) { int l = lines.BinarySearch(o); if (l < 0) l = ~l - 1; return (l + 1, o - lines[l] + 1); }
            var (sl, sc) = Pos(s); var (el, ec) = Pos(e);
            return new SourceSpan(file, sl, sc, el, ec);
        }
        void Report(int s, int e, string msg) { errors.Add(s); diags.Error("E0001", msg, At(s, e)); }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) { Report(i, text.Length, "unterminated block comment"); break; }
                i = close + 2;
                continue;
            }
            if (c == '\'')
            {
                int start = i++;
                bool closed = false;
                while (i < text.Length && text[i] != '\n' && text[i] != '\r')
                {
                    if (text[i] == '\\')
                    {
                        if (i + 1 >= text.Length || "'\\nt".IndexOf(text[i + 1]) < 0)
                            Report(i, Math.Min(i + 2, text.Length), $"invalid escape sequence '\\{(i + 1 < text.Length ? text[i + 1] : ' ')}'");
                        i += 2;
                        continue;
                    }
                    if (text[i] == '\'') { closed = true; i++; break; }
                    i++;
                }
                if (!closed) Report(start, i, "unterminated string literal");
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                int start = i;
                while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                bool frac = false;
                if (i + 1 < text.Length && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
                {
                    frac = true;
                    i++;
                    while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                }
                int suffixStart = i;
                while (i < text.Length && char.IsAsciiLetter(text[i])) i++;
                var suffix = text.Substring(suffixStart, i - suffixStart);
                var digits = text.Substring(start, (frac ? text.IndexOf('.', start) : suffixStart) - start);
                if (suffix.Length > 0 && !DurationUnits.Contains(suffix) && !SizeUnits.Contains(suffix))
                    Report(start, i, $"unrecognized literal suffix '{suffix}' in '{text.Substring(start, i - start)}'");
                else if (suffix.Length > 0 && frac)
                    Report(start, i, $"'{suffix}' literals must be integers: '{text.Substring(start, i - start)}'");
                else if (!frac && !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    Report(start, i, $"integer literal '{digits}' is out of range");
                if (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '_'))
                {
                    while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    Report(start, i, $"malformed literal '{text.Substring(start, i - start)}'");
                }
                continue;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
                if (TopKeywords.Contains(text.Substring(start, i - start))) keywords.Add(start);
                continue;
            }
            if (c == '&' || c == '|')
            {
                if (i + 1 < text.Length && text[i + 1] == c) { i += 2; continue; }
                Report(i, i + 1, $"invalid character '{c}' (did you mean '{c}{c}'?)");
                i++;
                continue;
            }
            if (Punctuation.IndexOf(c) >= 0) { i++; continue; }
            Report(i, i + 1, $"invalid character '{c}'");
            i++;
        }
        return (errors, keywords);
    }

    // --- Literal classification ---

    private static Expression MakeNumber(string digits, Maybe<string> frac, string suffix)
    {
        if (suffix.Length == 0)
        {
            if (frac.HasValue)
                return new FloatLiteral(double.Parse($"{digits}.{frac.Value}", CultureInfo.InvariantCulture));
            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
                ? new IntLiteral(v) : new ErrorExpr(digits);
        }
        var text = digits + (frac.HasValue ? "." + frac.Value : "") + suffix;
        if (frac.HasValue || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return new ErrorExpr(text);
        if (DurationUnits.Contains(suffix)) return new DurationLiteral(n, suffix);
        if (SizeUnits.Contains(suffix)) return new SizeLiteral(n, suffix);
        return new ErrorExpr(text);
    }

    // --- Spans ---

    private SourceSpan MakeSpan(int relStart, int relEnd)
    {
        int s = _base + relStart;
        int e = _lastTokenEnd >= s && _lastTokenEnd <= _base + relEnd ? _lastTokenEnd : TrimEnd(s, _base + relEnd);
        return MakeAbsSpan(s, e);
    }

    private int TrimEnd(int s, int e)
    {
        while (e > s && char.IsWhiteSpace(_text[e - 1])) e--;
        return e;
    }

    private SourceSpan PointSpan(int abs) => MakeAbsSpan(abs, Math.Min(abs + 1, _text.Length));

    private SourceSpan MakeAbsSpan(int s, int e)
    {
        var (sl, sc) = LineCol(s);
        var (el, ec) = LineCol(e);
        return new SourceSpan(_file, sl, sc, el, ec);
    }

    private (int Line, int Col) LineCol(int offset)
    {
        int l = Array.BinarySearch(_lineStarts, offset);
        if (l < 0) l = ~l - 1;
        return (l + 1, offset - _lineStarts[l] + 1);
    }

    private static SourceSpan Merge(SourceSpan a, SourceSpan b) =>
        a == SourceSpan.None ? b : b == SourceSpan.None ? a : new SourceSpan(a.File, a.StartLine, a.StartCol, b.EndLine, b.EndCol);

    private static string RenderSyntaxError(ParseError<char> err)
    {
        var found = err.EOF ? "end of input" : err.Unexpected.HasValue ? $"'{err.Unexpected.Value}'" : "input";
        var expected = err.Expected.Select(RenderExpected).Where(s => s.Length > 0).Distinct().OrderBy(s => s).ToList();
        var msg = $"syntax error: unexpected {found}";
        if (expected.Count > 0) msg += $"; expected {string.Join(", ", expected)}";
        else if (!string.IsNullOrEmpty(err.Message)) msg += $"; {err.Message}";
        return msg;
    }

    private static string RenderExpected(Expected<char> e) =>
        e.Label ?? (e.Tokens.IsDefault ? "end of input" : $"'{new string(e.Tokens.ToArray())}'");
}
