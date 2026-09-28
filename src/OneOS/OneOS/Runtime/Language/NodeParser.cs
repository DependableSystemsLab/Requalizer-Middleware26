using System.Collections.Generic;
using System.Linq;
using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;
using OneOS.Runtime.Language.Ast;
using OneOS.Runtime.Language.Models;

namespace OneOS.Runtime.Language;

public class NodeParser
{
    private static readonly Parser<char, string> Identifier =
        Map((first, rest) => first + rest,
            Letter.Or(Char('_')),
            LetterOrDigit.Or(Char('_')).ManyString());

    private static Parser<char, T> Tok<T>(Parser<char, T> p) =>
        p.Between(SkipWhitespaces);

    private static Parser<char, string> Keyword(string kw) =>
        Try(Tok(String(kw)));

    private static Parser<char, string> TokId => Tok(Identifier);

    private static readonly Parser<char, double> Number =
        Tok(Real);

    private static readonly Parser<char, string> StringLiteralParser =
        Tok(Char('"').Then(Token(c => c != '"').ManyString()).Before(Char('"')));

    private static readonly Parser<char, ScriptExpression> LitExpr =
        Number.Select(n => (ScriptExpression)new LiteralExpression(n))
        .Or(StringLiteralParser.Select(s => (ScriptExpression)new StringLiteralExpression(s)));

    private static readonly Parser<char, ScriptExpression> VarExpr =
        TokId.Select(n => (ScriptExpression)new VariableExpression(n));

    private static readonly Parser<char, ScriptExpression> BaseExpr =
        LitExpr.Or(VarExpr);

    private static readonly Parser<char, ScriptExpression> BinaryExpr =
        from left in BaseExpr
        from op in Tok(Char('+').Or(Char('-')).Or(Char('*')).Or(Char('/'))).Select(c => c.ToString())
        from right in BaseExpr
        select (ScriptExpression)new BinaryExpression(left, op, right);

    private static readonly Parser<char, ScriptExpression> Expr = Try(BinaryExpr).Or(BaseExpr);

    private static readonly Parser<char, ScriptStatement> LetStmt =
        from _ in Keyword("let")
        from id in TokId
        from eq in Tok(Char('='))
        from expr in Expr
        from semi in Tok(Char(';'))
        select (ScriptStatement)new LetStatement(id, expr);

    private static readonly Parser<char, ScriptStatement> AssignStmt =
        from id in TokId
        from eq in Tok(Char('='))
        from expr in Expr
        from semi in Tok(Char(';'))
        select (ScriptStatement)new AssignStatement(id, expr);

    private static readonly Parser<char, ScriptStatement> PrintStmt =
        from _ in Keyword("print")
        from expr in Expr
        from semi in Tok(Char(';'))
        select (ScriptStatement)new PrintStatement(expr);

    private static readonly Parser<char, ScriptStatement> Stmt = Try(LetStmt).Or(Try(AssignStmt)).Or(PrintStmt);

    private static readonly Parser<char, IEnumerable<ScriptStatement>> ExecStmt =
        from _ in Keyword("exec")
        from open in Tok(Char('{'))
        from stmts in Stmt.Many()
        from close in Tok(Char('}'))
        select stmts;

    private static readonly Parser<char, ChannelDefinition> ChannelStmt =
        from _ in Keyword("channel")
        from dirStr in Keyword("in").Or(Keyword("out"))
        from id1 in TokId
        from id2 in TokId.Optional()
        from semi in Tok(Char(';'))
        select BuildChannel(dirStr, id1, id2);

    private static ChannelDefinition BuildChannel(string dirStr, string id1, Maybe<string> id2)
    {
        var dir = dirStr == "in" ? ChannelDirection.In : ChannelDirection.Out;
        if (id2.HasValue)
        {
            return new ChannelDefinition(dir, id1, id2.Value);
        }
        return new ChannelDefinition(dir, null, id1);
    }

    private class ParsedNodeFile
    {
        public List<ChannelDefinition> Channels { get; } = new();
        public List<ScriptStatement> Statements { get; } = new();
    }

    private static readonly Parser<char, ParsedNodeFile> NodeFileParser =
        SkipWhitespaces.Then(
            ChannelStmt.Select(c => (object)c)
                .Or(ExecStmt.Select(e => (object)e))
                .Many()
        ).Before(SkipWhitespaces).Before(End).Select(ProcessStatements);

    private static ParsedNodeFile ProcessStatements(IEnumerable<object> stmts)
    {
        var res = new ParsedNodeFile();
        foreach (var s in stmts)
        {
            if (s is ChannelDefinition c) res.Channels.Add(c);
            else if (s is IEnumerable<ScriptStatement> e) res.Statements.AddRange(e);
        }
        return res;
    }

    public NodeParser() { }

    public NodeFileAst Parse(string content)
    {
        var parsedFile = NodeFileParser.ParseOrThrow(content);
        return new NodeFileAst(parsedFile.Channels, parsedFile.Statements);
    }
}
