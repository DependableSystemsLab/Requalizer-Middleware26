using System.Collections.Generic;
using OneOS.Runtime.Language.Models;

namespace OneOS.Runtime.Language.Ast;

public abstract record ScriptStatement;

public record LetStatement(string Identifier, ScriptExpression Value) : ScriptStatement;
public record AssignStatement(string Identifier, ScriptExpression Value) : ScriptStatement;
public record PrintStatement(ScriptExpression Value) : ScriptStatement;

public abstract record ScriptExpression;

public record BinaryExpression(ScriptExpression Left, string Operator, ScriptExpression Right) : ScriptExpression;
public record LiteralExpression(double Value) : ScriptExpression;
public record StringLiteralExpression(string Value) : ScriptExpression;
public record VariableExpression(string Identifier) : ScriptExpression;

public record NodeFileAst(IReadOnlyList<ChannelDefinition> Channels, IReadOnlyList<ScriptStatement> Statements);
