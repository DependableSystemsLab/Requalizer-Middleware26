using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OneOS.Runtime.Language.Ast;

// Source location of an AST node (L§12). Lines and columns are 1-based; the end is exclusive.
public sealed record SourceSpan(string File, int StartLine, int StartCol, int EndLine, int EndCol)
{
    public static readonly SourceSpan None = new("", 0, 0, 0, 0);
    public override string ToString() => $"{File}:{StartLine}:{StartCol}";
}

public abstract record AppNode
{
    // Assigned by the parser once the node's extent is known.
    public SourceSpan Span { get; set; } = SourceSpan.None;
}

public abstract record TopItem : AppNode;
public sealed record AppAst(IReadOnlyList<TopItem> Items) : AppNode;

// --- Labels ---
// `a < b < c;` is a chain; `a;` is a chain of one (an isolated label).
public sealed record LabelRelation(IReadOnlyList<string> Chain) : AppNode;
public sealed record LabelsDecl(bool IsStrict, IReadOnlyList<LabelRelation> Relations) : TopItem;

// --- Domains and types ---
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NamedTypeRef), "named")]
[JsonDerivedType(typeof(ListTypeRef), "list")]
public abstract record TypeRef : AppNode;
public sealed record NamedTypeRef(string Name) : TypeRef;       // primitive, format, domain, record or built-in
public sealed record ListTypeRef(TypeRef Element) : TypeRef;

public sealed record KeyDecl(string Name, TypeRef Repr) : TopItem;
public sealed record ClockDecl(string Name, TypeRef Repr, string Unit) : TopItem;

public sealed record FieldDecl(string Name, TypeRef Type) : AppNode;
public sealed record TypeDecl(string Name, IReadOnlyList<FieldDecl> Fields) : TopItem;

// --- Labellers ---
public sealed record Parameter(string Name, TypeRef Type) : AppNode;
public sealed record LabellerDecl(string Name, IReadOnlyList<Parameter> Parameters, Expression Body, string SourceText) : TopItem;

// --- Graphs ---
public sealed record GraphDecl(
    string Name,
    IReadOnlyList<Parameter> Parameters,
    IReadOnlyList<NodeDecl> Nodes,
    IReadOnlyList<EdgeDecl> Edges,
    IReadOnlyList<FlowDecl> Flows,
    IReadOnlyList<PolicyStatement> Policies) : TopItem;

public sealed record NodeAttribute(string Name) : AppNode;

// Keys == null: unpartitioned. Keys empty: keyless `n[]`.
public sealed record PartitionSpec(IReadOnlyList<string> Keys) : AppNode;

public sealed record NodeDecl(
    IReadOnlyList<NodeAttribute> Attributes,
    string Name,
    PartitionSpec? Partition,
    IReadOnlyList<PortDecl> Inputs,
    IReadOnlyList<PortDecl> Outputs,
    ProcessExpr Process) : AppNode;

public sealed record PortDecl(string Name, TypeRef Type, OrderSpec? Order) : AppNode;

public abstract record OrderSpec : AppNode;
public sealed record OrderedSpec(IReadOnlyList<string>? By, IReadOnlyList<string>? Per, DurationLiteral? Within) : OrderSpec;
public sealed record SequencedSpec() : OrderSpec;

public sealed record ProcessExpr(Expression Command, IReadOnlyList<Expression> Args) : AppNode;

public enum EdgeOp { Routed, AllPartitions }

public sealed record Endpoint(string Node, string? Port) : AppNode;
public sealed record EdgeDecl(string? Name, Endpoint From, EdgeOp Op, Endpoint To, IReadOnlyList<IReadOnlyList<string>>? By) : AppNode;

public sealed record FlowDecl(string Name, IReadOnlyList<string> Edges) : AppNode;

// --- Policy ---
public enum TargetIndexKind { None, All, At }
public sealed record Target(string Name, TargetIndexKind IndexKind, int Index, string? Port) : AppNode
{
    public override string ToString() => Name
        + (IndexKind == TargetIndexKind.All ? "[*]" : IndexKind == TargetIndexKind.At ? $"[{Index}]" : "")
        + (Port != null ? "." + Port : "");
}

public abstract record PolicyStatement(IReadOnlyList<Target> Targets) : AppNode;
public sealed record PolicyArg(Expression Value, Expression? RangeEnd) : AppNode;
public sealed record GenericPolicy(string Name, IReadOnlyList<PolicyArg> Args, IReadOnlyList<string>? By, IReadOnlyList<Target> Targets) : PolicyStatement(Targets);
public sealed record LabelPolicy(LabelSource Source, IReadOnlyList<Target> Targets) : PolicyStatement(Targets);

public abstract record LabelSource : AppNode;
public sealed record NamedLabelSource(string Name) : LabelSource;                          // label or labeller
public sealed record LambdaLabelSource(IReadOnlyList<Parameter> Parameters, Expression Body, string SourceText) : LabelSource;
public sealed record DynamicLabelSource(string Lo, string Hi) : LabelSource;
public sealed record HostLabelSource(string Lo, string Hi) : LabelSource;

// --- Interpreter command (L§9.1) ---
public sealed record SpawnCommand(string Graph, IReadOnlyList<Expression> Args) : AppNode;

// --- Expressions ---
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StringLiteral), "string")]
[JsonDerivedType(typeof(IntLiteral), "int")]
[JsonDerivedType(typeof(FloatLiteral), "float")]
[JsonDerivedType(typeof(BoolLiteral), "bool")]
[JsonDerivedType(typeof(DurationLiteral), "duration")]
[JsonDerivedType(typeof(SizeLiteral), "size")]
[JsonDerivedType(typeof(IdentExpr), "ident")]
[JsonDerivedType(typeof(MemberExpr), "member")]
[JsonDerivedType(typeof(CallExpr), "call")]
[JsonDerivedType(typeof(UnaryExpr), "unary")]
[JsonDerivedType(typeof(BinaryExpr), "binary")]
[JsonDerivedType(typeof(TernaryExpr), "ternary")]
[JsonDerivedType(typeof(ErrorExpr), "error")]
public abstract record Expression : AppNode;

public sealed record StringLiteral(string Value) : Expression;
public sealed record IntLiteral(long Value) : Expression;
public sealed record FloatLiteral(double Value) : Expression;
public sealed record BoolLiteral(bool Value) : Expression;

public sealed record DurationLiteral(long Value, string Unit) : Expression
{
    public long Nanoseconds => Value * Unit switch
    {
        "ns" => 1L, "us" => 1_000L, "ms" => 1_000_000L, "s" => 1_000_000_000L,
        "m" => 60_000_000_000L, "h" => 3_600_000_000_000L, _ => 0L
    };
    public override string ToString() => $"{Value}{Unit}";
}

public sealed record SizeLiteral(long Value, string Unit) : Expression
{
    // Binary multiples (L§2.4).
    public long Bytes => Value * Unit switch { "B" => 1L, "KB" => 1L << 10, "MB" => 1L << 20, "GB" => 1L << 30, _ => 0L };
    public override string ToString() => $"{Value}{Unit}";
}

public sealed record IdentExpr(string Name) : Expression;
public sealed record MemberExpr(Expression Target, string Name) : Expression;
public sealed record CallExpr(Expression Callee, IReadOnlyList<Expression> Args) : Expression;
public sealed record UnaryExpr(string Op, Expression Operand) : Expression;
public sealed record BinaryExpr(Expression Left, string Op, Expression Right) : Expression;
public sealed record TernaryExpr(Expression Condition, Expression Then, Expression Else) : Expression;

// Placeholder for a literal that failed to lex; the lexical error (E0001) is already reported.
public sealed record ErrorExpr(string Text) : Expression;
