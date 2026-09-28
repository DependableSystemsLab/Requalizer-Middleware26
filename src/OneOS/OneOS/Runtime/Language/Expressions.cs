using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using OneOS.Runtime.Language.Ast;

namespace OneOS.Runtime.Language;

public enum NameKind { Parameter, GraphParameter, Label }

// Names visible to an expression, in resolution order (L§4.4): lambda/labeller parameters,
// graph parameters (inline lambdas and process expressions only), then label names.
public sealed class ExprScope
{
    public Dictionary<string, DslType> Parameters { get; } = new();
    public Dictionary<string, DslType> GraphParameters { get; } = new();
    public LabelLattice Lattice { get; init; } = LabelLattice.Trivial();
    public IReadOnlySet<string> Labellers { get; init; } = new HashSet<string>();
    // Where the expression lives, for diagnostic codes: labeller bodies (E0711) or process arguments (E0301).
    public bool IsLabeller { get; init; }

    public (NameKind Kind, DslType Type)? Resolve(string name)
    {
        if (Parameters.TryGetValue(name, out var p)) return (NameKind.Parameter, p);
        if (GraphParameters.TryGetValue(name, out var g)) return (NameKind.GraphParameter, g);
        if (Lattice.Declared && Lattice.Contains(name)) return (NameKind.Label, LabelValueType.Instance);
        return null;
    }
}

// Static typing of expressions (L§3.7, L§8.3).
public static class ExprChecker
{
    private static readonly HashSet<string> LabelBuiltins = new() { "join", "meet", "leq" };

    public static DslType TypeOf(Expression e, ExprScope scope, DiagnosticBag diags)
    {
        string opCode = scope.IsLabeller ? "E0711" : "E0301";
        switch (e)
        {
            case StringLiteral: return PrimType.String;
            case IntLiteral: return IntLiteralType.Instance;
            case FloatLiteral: return FloatLiteralType.Instance;
            case BoolLiteral: return PrimType.Bool;
            case DurationLiteral: return DurationType.Instance;
            case SizeLiteral: return SizeType.Instance;
            case ErrorExpr: return ErrorType.Instance;

            case IdentExpr id:
            {
                var r = scope.Resolve(id.Name);
                if (r != null) return r.Value.Type;
                if (scope.Labellers.Contains(id.Name))
                    diags.Error("E0711", $"labeller '{id.Name}' cannot be used as a value", id.Span);
                else
                    diags.Error("E0105", $"unresolved identifier '{id.Name}'", id.Span);
                return ErrorType.Instance;
            }

            case MemberExpr m:
            {
                var t = TypeOf(m.Target, scope, diags);
                if (t is ErrorType) return t;
                var resolved = TypeOps.ResolvePath(t, new[] { m.Name }, out var noFields);
                if (resolved != null) return resolved;
                if (noFields) diags.Error("E0206", $"field access '.{m.Name}' on a value of type '{t.Display}', which has no fields", m.Span);
                else diags.Error("E0105", $"type '{t.Display}' has no field '{m.Name}'", m.Span);
                return ErrorType.Instance;
            }

            case CallExpr c when c.Callee is IdentExpr fn:
            {
                var args = c.Args.Select(a => TypeOf(a, scope, diags)).ToList();
                if (LabelBuiltins.Contains(fn.Name) && scope.Resolve(fn.Name) == null)
                {
                    if (args.Count != 2 || args.Any(a => a is not (LabelValueType or ErrorType)))
                    {
                        diags.Error(opCode, $"'{fn.Name}' takes two labels", c.Span);
                        return ErrorType.Instance;
                    }
                    return fn.Name == "leq" ? PrimType.Bool : LabelValueType.Instance;
                }
                if (scope.Labellers.Contains(fn.Name))
                    diags.Error("E0711", $"labellers cannot call other labellers ('{fn.Name}')", c.Span);
                else
                    diags.Error(opCode, $"call to '{fn.Name}' is not allowed; only join, meet and leq are built in", c.Span);
                return ErrorType.Instance;
            }

            case CallExpr c when c.Callee is MemberExpr method:
            {
                var recv = TypeOf(method.Target, scope, diags);
                var args = c.Args.Select(a => TypeOf(a, scope, diags)).ToList();
                if (recv is ErrorType) return recv;
                var rt = Repr(recv);
                if ((method.Name is "startsWith" or "endsWith" or "contains") && rt == PrimType.String)
                {
                    if (args.Count != 1 || !IsString(args[0])) diags.Error(opCode, $"'{method.Name}' takes one string argument", c.Span);
                    return PrimType.Bool;
                }
                if (method.Name == "contains" && recv is ListType lt)
                {
                    if (args.Count != 1 || !Comparable(args[0], lt.Element)) diags.Error(opCode, $"'contains' argument must match the list element type '{lt.Element.Display}'", c.Span);
                    return PrimType.Bool;
                }
                diags.Error(opCode, $"method '{method.Name}' is not available on type '{recv.Display}'", c.Span);
                return ErrorType.Instance;
            }

            case CallExpr c:
                diags.Error(opCode, "only named built-ins and methods can be called", c.Span);
                return ErrorType.Instance;

            case UnaryExpr u:
            {
                var t = TypeOf(u.Operand, scope, diags);
                if (t is ErrorType) return t;
                if (u.Op == "!" && IsBool(t)) return PrimType.Bool;
                if (u.Op == "-" && IsNumeric(t)) return t;
                diags.Error(opCode, $"operator '{u.Op}' does not apply to '{t.Display}'", u.Span);
                return ErrorType.Instance;
            }

            case BinaryExpr b:
            {
                var l = TypeOf(b.Left, scope, diags);
                var r = TypeOf(b.Right, scope, diags);
                if (l is ErrorType || r is ErrorType) return b.Op is "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||" ? PrimType.Bool : ErrorType.Instance;
                switch (b.Op)
                {
                    case "&&" or "||" when IsBool(l) && IsBool(r): return PrimType.Bool;
                    case "==" or "!=" when Comparable(l, r): return PrimType.Bool;
                    case "<" or "<=" or ">" or ">=" when (IsNumeric(l) && IsNumeric(r) && Comparable(l, r)) || (IsString(l) && IsString(r))
                        || (l is DurationType && r is DurationType) || (l is SizeType && r is SizeType): return PrimType.Bool;
                    case "+" when IsString(l) && IsString(r): return PrimType.String;
                    case "+" or "-" when IsNumeric(l) && IsNumeric(r) && Comparable(l, r): return Wider(l, r);
                    case "+" or "-" when l is DurationType && r is DurationType: return l;
                }
                var hint = b.Op is "<" or "<=" or ">" or ">=" && l is LabelValueType ? " (compare labels with leq(a, b))" : "";
                diags.Error(opCode, $"operator '{b.Op}' does not apply to '{l.Display}' and '{r.Display}'{hint}", b.Span);
                return ErrorType.Instance;
            }

            case TernaryExpr t:
            {
                var c = TypeOf(t.Condition, scope, diags);
                if (!IsBool(c) && c is not ErrorType) diags.Error(opCode, $"condition must be bool, not '{c.Display}'", t.Condition.Span);
                var a = TypeOf(t.Then, scope, diags);
                var b2 = TypeOf(t.Else, scope, diags);
                if (a is ErrorType) return b2;
                if (b2 is ErrorType) return a;
                if (!Comparable(a, b2)) { diags.Error(opCode, $"branches have different types '{a.Display}' and '{b2.Display}'", t.Span); return ErrorType.Instance; }
                return Wider(a, b2);
            }
        }
        return ErrorType.Instance;
    }

    public static DslType Repr(DslType t) => t switch { KeyDomainType k => k.Repr, ClockDomainType c => c.Repr, _ => t };
    public static bool IsBool(DslType t) => Repr(t) == PrimType.Bool || t is ErrorType;
    public static bool IsString(DslType t) => Repr(t) == PrimType.String || t is ErrorType;
    public static bool IsNumeric(DslType t) => (Repr(t) is PrimType p && p.IsNumeric) || t is IntLiteralType or FloatLiteralType or ErrorType;

    // Whether two types may be compared or unified (literals adopt the other side's numeric type).
    public static bool Comparable(DslType a, DslType b)
    {
        if (a is ErrorType || b is ErrorType) return true;
        a = Repr(a); b = Repr(b);
        if (a == b) return true;
        if (a is IntLiteralType) return b is IntLiteralType or FloatLiteralType || b is PrimType { IsNumeric: true };
        if (b is IntLiteralType) return a is FloatLiteralType || a is PrimType { IsNumeric: true };
        if (a is FloatLiteralType) return b is FloatLiteralType || b is PrimType { IsFloat: true };
        if (b is FloatLiteralType) return a is PrimType { IsFloat: true };
        if (a is RecordType ra && b is RecordType rb) return ReferenceEquals(ra, rb);
        return a is ListType la && b is ListType lb && Comparable(la.Element, lb.Element);
    }

    private static DslType Wider(DslType a, DslType b) =>
        a is IntLiteralType or FloatLiteralType ? (b is IntLiteralType && a is FloatLiteralType ? a : b) : a;

    // Field paths read from the given parameter, e.g. `e.redacted` → ["redacted"] (declassification report, L§8.6 step 5).
    public static IReadOnlyList<string> FieldsRead(Expression e, string param)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(Expression x)
        {
            switch (x)
            {
                case MemberExpr m:
                {
                    var path = new List<string>();
                    Expression cur = m;
                    while (cur is MemberExpr mm) { path.Insert(0, mm.Name); cur = mm.Target; }
                    if (cur is IdentExpr id && id.Name == param) result.Add(string.Join(".", path));
                    else Walk(cur);
                    break;
                }
                case CallExpr c: Walk(c.Callee is MemberExpr cm ? cm.Target : c.Callee); foreach (var a in c.Args) Walk(a); break;
                case UnaryExpr u: Walk(u.Operand); break;
                case BinaryExpr b: Walk(b.Left); Walk(b.Right); break;
                case TernaryExpr t: Walk(t.Condition); Walk(t.Then); Walk(t.Else); break;
            }
        }
        Walk(e);
        return result.ToList();
    }

    public static bool Mentions(Expression e, string name) => e switch
    {
        IdentExpr id => id.Name == name,
        MemberExpr m => Mentions(m.Target, name),
        CallExpr c => (c.Callee is not IdentExpr && Mentions(c.Callee, name)) || c.Args.Any(a => Mentions(a, name)),
        UnaryExpr u => Mentions(u.Operand, name),
        BinaryExpr b => Mentions(b.Left, name) || Mentions(b.Right, name),
        TernaryExpr t => Mentions(t.Condition, name) || Mentions(t.Then, name) || Mentions(t.Else, name),
        _ => false,
    };
}

// A label value produced by evaluation.
public readonly record struct LabelValue(int Index);

// Abstract values for constant folding and label-range computation (L§8.6 step 2).
public abstract record AVal;
public sealed record KnownVal(object Value) : AVal;          // bool, long, double, string, LabelValue, DurationLiteral, SizeLiteral
public sealed record UnknownVal : AVal { public static readonly UnknownVal Instance = new(); }
public sealed record LabelSetVal(IReadOnlySet<int> Labels) : AVal;

public sealed class AbstractEnv
{
    public Dictionary<string, AVal> Values { get; } = new();
    public required LabelLattice Lattice { get; init; }
}

public static class AbstractEval
{
    public static AVal Eval(Expression e, AbstractEnv env)
    {
        var L = env.Lattice;
        switch (e)
        {
            case StringLiteral s: return new KnownVal(s.Value);
            case IntLiteral i: return new KnownVal(i.Value);
            case FloatLiteral f: return new KnownVal(f.Value);
            case BoolLiteral b: return new KnownVal(b.Value);
            case DurationLiteral d: return new KnownVal(d);
            case SizeLiteral z: return new KnownVal(z);
            case IdentExpr id:
                if (env.Values.TryGetValue(id.Name, out var v)) return v;
                if (L.Declared && L.Contains(id.Name)) return new KnownVal(new LabelValue(L.IndexOf(id.Name)));
                return UnknownVal.Instance;
            case MemberExpr: return UnknownVal.Instance;

            case CallExpr c when c.Callee is IdentExpr fn && fn.Name is "join" or "meet" or "leq" && c.Args.Count == 2:
            {
                var a = LabelsOf(Eval(c.Args[0], env), L);
                var b = LabelsOf(Eval(c.Args[1], env), L);
                if (fn.Name == "leq")
                {
                    var outcomes = a.SelectMany(x => b.Select(y => L.Leq(x, y))).Distinct().ToList();
                    return outcomes.Count == 1 ? new KnownVal(outcomes[0]) : UnknownVal.Instance;
                }
                var r = a.SelectMany(x => b.Select(y => fn.Name == "join" ? SafeJoin(L, x, y) : L.Meet(x, y))).ToHashSet();
                return r.Count == 1 ? new KnownVal(new LabelValue(r.First())) : new LabelSetVal(r);
            }

            case CallExpr c when c.Callee is MemberExpr m && c.Args.Count == 1:
            {
                var recv = Eval(m.Target, env);
                var arg = Eval(c.Args[0], env);
                if (recv is KnownVal { Value: string s } && arg is KnownVal { Value: string x })
                    return m.Name switch
                    {
                        "startsWith" => new KnownVal(s.StartsWith(x, StringComparison.Ordinal)),
                        "endsWith" => new KnownVal(s.EndsWith(x, StringComparison.Ordinal)),
                        "contains" => new KnownVal(s.Contains(x, StringComparison.Ordinal)),
                        _ => UnknownVal.Instance,
                    };
                return UnknownVal.Instance;
            }

            case UnaryExpr u:
            {
                var x = Eval(u.Operand, env);
                if (x is not KnownVal k) return UnknownVal.Instance;
                return (u.Op, k.Value) switch
                {
                    ("!", bool b) => new KnownVal(!b),
                    ("-", long l) => new KnownVal(-l),
                    ("-", double d) => new KnownVal(-d),
                    _ => UnknownVal.Instance,
                };
            }

            case BinaryExpr b:
            {
                var l = Eval(b.Left, env);
                if (b.Op == "&&" && l is KnownVal { Value: false }) return l;
                if (b.Op == "||" && l is KnownVal { Value: true }) return l;
                var r = Eval(b.Right, env);
                if (b.Op == "&&" && r is KnownVal { Value: false }) return r;
                if (b.Op == "||" && r is KnownVal { Value: true }) return r;
                if (l is KnownVal kl && r is KnownVal kr) return Binary(b.Op, kl.Value, kr.Value);
                return UnknownVal.Instance;
            }

            case TernaryExpr t:
            {
                var c = Eval(t.Condition, env);
                if (c is KnownVal { Value: bool cond }) return Eval(cond ? t.Then : t.Else, env);
                var a = Eval(t.Then, env);
                var b2 = Eval(t.Else, env);
                if (a is KnownVal ka && b2 is KnownVal kb && Equals(ka.Value, kb.Value)) return a;
                if (IsLabel(a) && IsLabel(b2)) return new LabelSetVal(LabelsOf(a, L).Union(LabelsOf(b2, L)).ToHashSet());
                return UnknownVal.Instance;
            }
        }
        return UnknownVal.Instance;
    }

    // The set of labels an abstract value may denote. An unknown label could be anything.
    public static IReadOnlySet<int> LabelsOf(AVal v, LabelLattice L) => v switch
    {
        KnownVal { Value: LabelValue lv } => new HashSet<int> { lv.Index },
        LabelSetVal s => s.Labels,
        _ => Enumerable.Range(0, L.Count).ToHashSet(),
    };

    private static bool IsLabel(AVal v) => v is LabelSetVal || v is KnownVal { Value: LabelValue };

    private static int SafeJoin(LabelLattice L, int x, int y) => L.JoinTable[x][y] >= 0 ? L.JoinTable[x][y] : L.TopIndex;

    internal static AVal Binary(string op, object l, object r)
    {
        switch (op)
        {
            case "==": return new KnownVal(ValueEquals(l, r));
            case "!=": return new KnownVal(!ValueEquals(l, r));
            case "&&" when l is bool a && r is bool b: return new KnownVal(a && b);
            case "||" when l is bool a && r is bool b: return new KnownVal(a || b);
            case "+" when l is string a && r is string b: return new KnownVal(a + b);
            case "+" when l is long a && r is long b: return new KnownVal(a + b);
            case "-" when l is long a && r is long b: return new KnownVal(a - b);
            case "+" or "-" when ToDouble(l) is double a && ToDouble(r) is double b: return new KnownVal(op == "+" ? a + b : a - b);
            case "<" or "<=" or ">" or ">=":
            {
                int? cmp = (l, r) switch
                {
                    (string a, string b) => string.CompareOrdinal(a, b),
                    (DurationLiteral a, DurationLiteral b) => a.Nanoseconds.CompareTo(b.Nanoseconds),
                    (SizeLiteral a, SizeLiteral b) => a.Bytes.CompareTo(b.Bytes),
                    _ when ToDouble(l) is double a && ToDouble(r) is double b => a.CompareTo(b),
                    _ => null,
                };
                if (cmp == null) return UnknownVal.Instance;
                return new KnownVal(op switch { "<" => cmp < 0, "<=" => cmp <= 0, ">" => cmp > 0, _ => cmp >= 0 });
            }
        }
        return UnknownVal.Instance;
    }

    private static double? ToDouble(object o) => o switch { long l => l, double d => d, _ => null };

    private static bool ValueEquals(object l, object r) =>
        ToDouble(l) is double a && ToDouble(r) is double b ? a == b : Equals(l, r);

    public static string Render(object? v) => v switch
    {
        null => "null",
        string s => $"'{s}'",
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}

public sealed class EvalException : Exception
{
    public EvalException(string message) : base(message) { }
}

// Evaluates an expression on concrete values at runtime (labellers over real messages, L§8.5).
// Values: bool, long, double, string, LabelValue, JsonElement (message data), or a dictionary
// (the `replica` record). Missing fields and type mismatches throw EvalException.
public static class ConcreteEval
{
    public static object Eval(Expression e, IReadOnlyDictionary<string, object?> env, LabelLattice lattice)
    {
        switch (e)
        {
            case StringLiteral s: return s.Value;
            case IntLiteral i: return i.Value;
            case FloatLiteral f: return f.Value;
            case BoolLiteral b: return b.Value;
            case DurationLiteral d: return d;
            case SizeLiteral z: return z;
            case IdentExpr id:
                if (env.TryGetValue(id.Name, out var v) && v != null) return Normalize(v);
                if (lattice.Contains(id.Name)) return new LabelValue(lattice.IndexOf(id.Name));
                throw new EvalException($"unbound name '{id.Name}'");
            case MemberExpr m:
                return Member(Eval(m.Target, env, lattice), m.Name);
            case CallExpr { Callee: IdentExpr { Name: "join" or "meet" or "leq" } fn } c when c.Args.Count == 2:
            {
                var a = Label(Eval(c.Args[0], env, lattice));
                var b = Label(Eval(c.Args[1], env, lattice));
                return fn.Name switch
                {
                    "join" => new LabelValue(lattice.JoinTable[a][b] >= 0 ? lattice.JoinTable[a][b] : lattice.TopIndex),
                    "meet" => new LabelValue(lattice.Meet(a, b)),
                    _ => lattice.Leq(a, b),
                };
            }
            case CallExpr { Callee: MemberExpr method } c when c.Args.Count == 1:
            {
                var recv = Eval(method.Target, env, lattice);
                var arg = Eval(c.Args[0], env, lattice);
                if (recv is string s && arg is string x)
                    return method.Name switch
                    {
                        "startsWith" => s.StartsWith(x, StringComparison.Ordinal),
                        "endsWith" => s.EndsWith(x, StringComparison.Ordinal),
                        "contains" => s.Contains(x, StringComparison.Ordinal),
                        _ => throw new EvalException($"unknown method '{method.Name}'"),
                    };
                if (recv is JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } list && method.Name == "contains")
                    return list.EnumerateArray().Any(item => Equals(Normalize(item), arg) || (ToDouble(Normalize(item)) is double p && ToDouble(arg) is double q && p == q));
                throw new EvalException($"method '{method.Name}' does not apply here");
            }
            case UnaryExpr u:
            {
                var x = Eval(u.Operand, env, lattice);
                return (u.Op, x) switch
                {
                    ("!", bool b) => !b,
                    ("-", long l) => -l,
                    ("-", double d) => -d,
                    _ => throw new EvalException($"operator '{u.Op}' does not apply to {x}"),
                };
            }
            case BinaryExpr b:
            {
                var l = Eval(b.Left, env, lattice);
                if (b.Op == "&&") return AsBool(l) && AsBool(Eval(b.Right, env, lattice));
                if (b.Op == "||") return AsBool(l) || AsBool(Eval(b.Right, env, lattice));
                var r = Eval(b.Right, env, lattice);
                return AbstractEval.Binary(b.Op, l, r) is KnownVal k
                    ? k.Value : throw new EvalException($"operator '{b.Op}' does not apply to {l} and {r}");
            }
            case TernaryExpr t:
                return Eval(AsBool(Eval(t.Condition, env, lattice)) ? t.Then : t.Else, env, lattice);
        }
        throw new EvalException($"cannot evaluate {e.GetType().Name}");
    }

    public static int EvalLabel(Expression e, IReadOnlyDictionary<string, object?> env, LabelLattice lattice) => Label(Eval(e, env, lattice));

    private static object Member(object target, string name) => target switch
    {
        JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } o when o.TryGetProperty(name, out var p) => Normalize(p),
        IReadOnlyDictionary<string, object?> d when d.TryGetValue(name, out var v) && v != null => Normalize(v),
        _ => throw new EvalException($"no field '{name}'"),
    };

    // JSON scalars become C# values; objects and arrays stay JsonElements.
    private static object Normalize(object v) => v switch
    {
        JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } s => s.GetString()!,
        JsonElement { ValueKind: System.Text.Json.JsonValueKind.True } => true,
        JsonElement { ValueKind: System.Text.Json.JsonValueKind.False } => false,
        JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } n => n.TryGetInt64(out var l) ? l : n.GetDouble(),
        int i => (long)i,
        _ => v,
    };

    private static bool AsBool(object v) => v is bool b ? b : throw new EvalException($"expected a bool, got {v}");
    private static int Label(object v) => v is LabelValue l ? l.Index : throw new EvalException($"expected a label, got {v}");
    private static double? ToDouble(object o) => o switch { long l => l, double d => d, _ => null };
}
