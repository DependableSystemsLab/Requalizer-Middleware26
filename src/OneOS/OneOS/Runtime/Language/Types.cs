using System;
using System.Collections.Generic;
using System.Linq;

namespace OneOS.Runtime.Language;

// Semantic types (L§5). Record types are compared structurally; their name is only for display.
public abstract record DslType
{
    public abstract string Display { get; }
    public sealed override string ToString() => Display;
}

public sealed record PrimType(string Name) : DslType
{
    public static readonly PrimType Bool = new("bool"), I32 = new("i32"), I64 = new("i64"), U32 = new("u32"), U64 = new("u64"),
        F32 = new("f32"), F64 = new("f64"), String = new("string");
    public static readonly IReadOnlyDictionary<string, PrimType> All =
        new[] { Bool, I32, I64, U32, U64, F32, F64, String }.ToDictionary(p => p.Name);
    public bool IsInteger => Name is "i32" or "i64" or "u32" or "u64";
    public bool IsFloat => Name is "f32" or "f64";
    public bool IsNumeric => IsInteger || IsFloat;
    public override string Display => Name;
}

// Any JSON value (field type) or any JSON object (port type).
public sealed record JsonType : DslType { public static readonly JsonType Instance = new(); public override string Display => "json"; }

// An unstructured format from the format registry (L§5.2). `bytes` doubles as the binary field type.
public sealed record FormatType(string Name) : DslType { public override string Display => Name; }

public sealed record KeyDomainType(string Name, PrimType Repr) : DslType { public override string Display => Name; }
public sealed record ClockDomainType(string Name, PrimType Repr, string Unit) : DslType { public override string Display => Name; }

public sealed record RecordField(string Name, DslType Type);
public sealed record RecordType(string Name) : DslType
{
    // Filled in after all type declarations are resolved, so records may refer to each other.
    // Equality is by identity: one RecordType object exists per declaration.
    public List<RecordField> Fields { get; } = new();
    public bool Equals(RecordType? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    public RecordField? Field(string name) => Fields.FirstOrDefault(f => f.Name == name);
    public override string Display => Name;
}

public sealed record ListType(DslType Element) : DslType { public override string Display => $"list<{Element.Display}>"; }
public sealed record LabelValueType : DslType { public static readonly LabelValueType Instance = new(); public override string Display => "label"; }

// Built-in labeller parameter type for instance targets: fields node: string, index: u64 (L§8.3).
public sealed record ReplicaType : DslType
{
    public static readonly ReplicaType Instance = new();
    public static readonly IReadOnlyList<RecordField> Fields = new[] { new RecordField("node", PrimType.String), new RecordField("index", PrimType.U64) };
    public override string Display => "replica";
}

// Expression-only types.
public sealed record DurationType : DslType { public static readonly DurationType Instance = new(); public override string Display => "duration"; }
public sealed record SizeType : DslType { public static readonly SizeType Instance = new(); public override string Display => "size"; }
public sealed record IntLiteralType : DslType { public static readonly IntLiteralType Instance = new(); public override string Display => "integer"; }
public sealed record FloatLiteralType : DslType { public static readonly FloatLiteralType Instance = new(); public override string Display => "number"; }

// Poison type after an error; compatible with everything so errors don't cascade.
public sealed record ErrorType : DslType { public static readonly ErrorType Instance = new(); public override string Display => "<error>"; }

// How a byte stream on a port is cut into messages (L§5.2). The middleware segments a process's output
// stream with its port's framing; a port whose framing is `None` is unsegmentable, so its edges carry the
// stream as it is (raw, one-to-one; see E0412, E0413, E0515, E0735).
public enum FramingKind { None, Ndjson, Lines, Delimiter, Markers, LengthPrefix, Fixed }

// Byte sequences are hex strings (e.g. "ffd8"), so the IR stays readable.
public sealed record Framing(
    FramingKind Kind,
    string? Delimiter = null,                   // Delimiter: ends each message (kept with it)
    string? Start = null, string? End = null,   // Markers: each message runs from Start through End
    int Width = 0, bool BigEndian = false,      // LengthPrefix: a Width-byte unsigned length, then the message
    int Size = 0)                               // Fixed: every message is Size bytes
{
    public static readonly Framing None = new(FramingKind.None);
    public static readonly Framing Ndjson = new(FramingKind.Ndjson);
    public static readonly Framing Lines = new(FramingKind.Lines);

    [System.Text.Json.Serialization.JsonIgnore] public bool Segmentable => Kind != FramingKind.None;

    public override string ToString() => Kind switch
    {
        FramingKind.None => "none",
        FramingKind.Ndjson => "ndjson",
        FramingKind.Lines => "lines",
        FramingKind.Delimiter => $"delimiter({Delimiter})",
        FramingKind.Markers => $"markers({Start}, {End})",
        FramingKind.LengthPrefix => $"length_prefix({Width}, {(BigEndian ? "be" : "le")})",
        FramingKind.Fixed => $"fixed({Size})",
        _ => Kind.ToString(),
    };

    // The textual form ToString produces, as used in interpreter configuration:
    // none | ndjson | lines | delimiter(<hex>) | markers(<hex>, <hex>) | length_prefix(<1|2|4|8>, le|be) | fixed(<n>)
    public static Framing Parse(string text)
    {
        var t = text.Trim();
        int open = t.IndexOf('(');
        var name = (open < 0 ? t : t[..open]).Trim().ToLowerInvariant();
        var args = open < 0 ? new string[0]
            : t.EndsWith(')') ? t[(open + 1)..^1].Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToArray()
            : throw new FormatException($"framing '{text}': missing ')'");
        void Arity(int n) { if (args.Length != n) throw new FormatException($"framing '{text}': {name} takes {n} argument(s)"); }
        string Hex(string a)
        {
            var h = a.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? a[2..] : a;
            if (h.Length == 0 || h.Length % 2 != 0 || !h.All(Uri.IsHexDigit)) throw new FormatException($"framing '{text}': '{a}' is not a hex byte sequence");
            return h.ToLowerInvariant();
        }
        switch (name)
        {
            case "none": Arity(0); return None;
            case "ndjson": Arity(0); return Ndjson;
            case "lines": Arity(0); return Lines;
            case "delimiter": Arity(1); return new Framing(FramingKind.Delimiter, Delimiter: Hex(args[0]));
            case "markers": Arity(2); return new Framing(FramingKind.Markers, Start: Hex(args[0]), End: Hex(args[1]));
            case "length_prefix":
                Arity(2);
                if (!int.TryParse(args[0], out var w) || w is not (1 or 2 or 4 or 8)) throw new FormatException($"framing '{text}': width must be 1, 2, 4 or 8");
                if (args[1] is not ("le" or "be")) throw new FormatException($"framing '{text}': byte order must be le or be");
                return new Framing(FramingKind.LengthPrefix, Width: w, BigEndian: args[1] == "be");
            case "fixed":
                Arity(1);
                if (!int.TryParse(args[0], out var size) || size <= 0) throw new FormatException($"framing '{text}': size must be a positive integer");
                return new Framing(FramingKind.Fixed, Size: size);
            default: throw new FormatException($"framing '{text}': unknown kind '{name}'");
        }
    }

    public static byte[] Bytes(string hex) => Convert.FromHexString(hex);
}

// A format registry entry (L§5.2): name, framing rule, the formats it is assignable to, and a description.
public sealed record FormatEntry(string Name, Framing Framing, IReadOnlyList<string> AssignableTo, string? Description = null);

public sealed class FormatRegistry
{
    private readonly Dictionary<string, FormatEntry> _entries = new();

    public static FormatRegistry CreateDefault()
    {
        var r = new FormatRegistry();
        r.Add(new FormatEntry("json", Framing.Ndjson, new string[0], "one JSON object per message, one per line"));
        r.Add(new FormatEntry("bytes", Framing.None, new string[0], "an opaque byte stream; not segmented"));
        r.Add(new FormatEntry("jpeg", new Framing(FramingKind.Markers, Start: "ffd8", End: "ffd9"), new[] { "bytes" },
            "one JPEG image per message, from the start-of-image to the end-of-image marker"));
        r.Add(new FormatEntry("lines", Framing.Lines, new string[0], "one line of text per message"));
        // The OneOS JavaScript environment's process.stdout.segment / process.stdin.segment (plan step 7.4).
        r.Add(new FormatEntry("segment", new Framing(FramingKind.LengthPrefix, Width: 4), new[] { "bytes" },
            "one length-prefixed binary message (4-byte little-endian length), as oneos.js .segment streams write"));
        return r;
    }

    public void Add(FormatEntry entry) => _entries[entry.Name] = entry;
    public FormatEntry? Get(string name) => _entries.GetValueOrDefault(name);

    // The framing of a port type: records and `json` are newline-delimited JSON; a format has its entry's.
    public Framing FramingOf(DslType portType) => portType switch
    {
        FormatType f => Get(f.Name)?.Framing ?? Framing.None,
        _ => Framing.Ndjson,
    };
    public bool Contains(string name) => _entries.ContainsKey(name);
    public IEnumerable<FormatEntry> Entries => _entries.Values;

    // Reflexive-transitive closure over the registry's assignable-to lists.
    public bool IsAssignable(string from, string to)
    {
        if (from == to) return true;
        var seen = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            var f = stack.Pop();
            if (!seen.Add(f) || !_entries.TryGetValue(f, out var e)) continue;
            foreach (var t in e.AssignableTo) { if (t == to) return true; stack.Push(t); }
        }
        return false;
    }
}

public enum Assignability { Yes, No, Unchecked }

public static class TypeOps
{
    public static bool IsStructured(DslType t) => t is RecordType;
    public static bool IsPortType(DslType t) => t is RecordType or JsonType or FormatType or ErrorType;

    // S ≤ D (L§5.3). `Unchecked` is json → record (W0305).
    public static Assignability Assignable(DslType s, DslType d, FormatRegistry formats)
    {
        if (s is ErrorType || d is ErrorType) return Assignability.Yes;
        if (d is JsonType) return s is FormatType ? Assignability.No : Assignability.Yes;
        if (s is JsonType) return d is RecordType ? Assignability.Unchecked : Assignability.No;
        if (s is FormatType fs && d is FormatType fd) return formats.IsAssignable(fs.Name, fd.Name) ? Assignability.Yes : Assignability.No;
        if (s is FormatType || d is FormatType) return Assignability.No;
        if (s is RecordType rs && d is RecordType rd) return RecordAssignable(rs, rd, formats, new HashSet<(RecordType, RecordType)>());
        return FieldAssignable(s, d, formats) ? Assignability.Yes : Assignability.No;
    }

    private static Assignability RecordAssignable(RecordType s, RecordType d, FormatRegistry formats, HashSet<(RecordType, RecordType)> visiting)
    {
        if (ReferenceEquals(s, d) || !visiting.Add((s, d))) return Assignability.Yes;
        var result = Assignability.Yes;
        foreach (var df in d.Fields)
        {
            var sf = s.Field(df.Name);
            if (sf == null) return Assignability.No;
            var r = sf.Type is RecordType sr && df.Type is RecordType dr
                ? RecordAssignable(sr, dr, formats, visiting)
                : FieldAssignable(sf.Type, df.Type, formats) ? Assignability.Yes : Assignability.No;
            if (r == Assignability.No) return Assignability.No;
        }
        return result;
    }

    // Field types: primitives only to themselves; json accepts any field type; lists covariantly;
    // key and clock domains to and from their representation types, never to another domain.
    public static bool FieldAssignable(DslType s, DslType d, FormatRegistry formats)
    {
        if (s is ErrorType || d is ErrorType) return true;
        if (d is JsonType) return true;
        switch (s, d)
        {
            case (PrimType a, PrimType b): return a.Name == b.Name;
            case (KeyDomainType a, KeyDomainType b): return a.Name == b.Name;
            case (ClockDomainType a, ClockDomainType b): return a.Name == b.Name;
            case (KeyDomainType a, PrimType b): return a.Repr.Name == b.Name;
            case (PrimType a, KeyDomainType b): return a.Name == b.Repr.Name;
            case (ClockDomainType a, PrimType b): return a.Repr.Name == b.Name;
            case (PrimType a, ClockDomainType b): return a.Name == b.Repr.Name;
            case (ListType a, ListType b): return FieldAssignable(a.Element, b.Element, formats);
            case (FormatType a, FormatType b): return formats.IsAssignable(a.Name, b.Name);
            case (RecordType a, RecordType b): return Assignable(a, b, formats) == Assignability.Yes;
            default: return false;
        }
    }

    // Resolves a field path within a type; returns null if a step doesn't exist.
    // `noFields` is set when the path steps into a type that has no fields at all (E0206).
    public static DslType? ResolvePath(DslType type, IReadOnlyList<string> path, out bool noFields)
    {
        noFields = false;
        var t = type;
        foreach (var step in path)
        {
            if (t is RecordType r)
            {
                var f = r.Field(step);
                if (f == null) return null;
                t = f.Type;
            }
            else if (t is ReplicaType)
            {
                var f = ReplicaType.Fields.FirstOrDefault(x => x.Name == step);
                if (f == null) return null;
                t = f.Type;
            }
            else if (t is ErrorType) return t;
            else { noFields = true; return null; }
        }
        return t;
    }

    // Top-level fields of a record whose type is the given domain (by name).
    public static IEnumerable<RecordField> FieldsOfDomain(RecordType r, string domain) =>
        r.Fields.Where(f => (f.Type is KeyDomainType k && k.Name == domain) || (f.Type is ClockDomainType c && c.Name == domain));

    public static bool IsRecursive(RecordType r)
    {
        var visiting = new HashSet<RecordType>();
        bool Visit(DslType t)
        {
            while (t is ListType l) t = l.Element;
            if (t is not RecordType rt) return false;
            if (ReferenceEquals(rt, r) && visiting.Count > 0) return true;
            if (!visiting.Add(rt)) return false;
            var hit = rt.Fields.Any(f => Visit(f.Type));
            visiting.Remove(rt);
            return hit;
        }
        return Visit(r);
    }
}
