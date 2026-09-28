using System;
using System.Collections.Generic;
using System.Linq;
using OneOS.Runtime.Language.Ast;
using OneOS.Runtime.Language.Models;

namespace OneOS.Runtime.Language;

public sealed record SourceFile(string Name, string Text);

// The DSL compiler (L§11): parse, resolve names, check types, topology, partitioning, ordering,
// flows and policies, analyze labels, and emit the IR. Diagnostics are collected, never thrown;
// the compiler continues after errors to report as many as it can (L§10).
public sealed partial class AppCompiler
{
    public const int DefaultKeyGroups = 128;
    public const long DefaultMaxBufferBytes = 64L << 20;

    private readonly FormatRegistry _formats;
    private readonly DiagnosticBag _diags = new();

    // Global scope (L§4.1).
    private LabelLattice _lattice = LabelLattice.Trivial();
    private readonly Dictionary<string, (string Kind, SourceSpan Span)> _globals = new();
    private readonly Dictionary<string, KeyDomainType> _keys = new();
    private readonly Dictionary<string, ClockDomainType> _clocks = new();
    private readonly Dictionary<string, RecordType> _records = new();
    private readonly Dictionary<string, (LabellerDecl Decl, List<(string Name, DslType Type)> Params)> _labellers = new();

    public AppCompiler(FormatRegistry? formats = null)
    {
        _formats = formats ?? FormatRegistry.CreateDefault();
    }

    public static CompiledProgram CompileSource(string text, string file = "<input>") =>
        new AppCompiler().Compile(new[] { new SourceFile(file, text) });

    public CompiledProgram Compile(IReadOnlyList<SourceFile> sources)
    {
        // Phase 1: parse.
        var parser = new AppParser();
        var items = new List<TopItem>();
        foreach (var src in sources)
        {
            var (ast, parseDiags) = parser.Parse(src.Text, src.Name);
            _diags.AddRange(parseDiags);
            items.AddRange(ast.Items);
        }

        // Phase 2–3: global names, lattice, types, labellers.
        CollectGlobals(items);
        ResolveTypes(items.OfType<TypeDecl>());
        CheckLabellers();

        // Phases 2–7 per graph.
        var contexts = items.OfType<GraphDecl>().Select(CompileGraphStructure).ToList();

        // Phase 8: label analysis, only if there are no structural errors anywhere (L§10).
        bool structural = _diags.HasStructuralErrors || contexts.Any(c => c.Diags.HasStructuralErrors);
        var globalErrors = _diags.Items.Where(d => d.IsError).ToList();
        var graphs = new List<CompiledGraph>();
        var all = new List<Diagnostic>(_diags.Items);
        foreach (var ctx in contexts)
        {
            var graph = ctx.Graph!;
            if (!structural)
            {
                var (analysis, labelDiags) = LabelAnalysis.Run(graph, args: null);
                ctx.Diags.AddRange(labelDiags);
                graph = graph with { Analysis = analysis };
            }
            graph = graph with { Diagnostics = globalErrors.Concat(ctx.Diags.Items).ToList() };
            all.AddRange(ctx.Diags.Items);
            graphs.Add(graph);
        }

        var types = _records.Values.ToDictionary(r => r.Name, ToRecordInfo);
        return new CompiledProgram(_lattice, types, _formats.Entries.ToList(), graphs, all);
    }

    // --- Globals (L§4.1) ---

    private void CollectGlobals(List<TopItem> items)
    {
        LabelsDecl? labels = null;
        foreach (var ld in items.OfType<LabelsDecl>())
        {
            if (labels == null) labels = ld;
            else _diags.Error("E0707", "more than one labels block in the compilation unit", ld.Span, $"note: first labels block at {labels.Span}");
        }
        if (labels != null)
        {
            _lattice = LabelLattice.Build(labels, _diags);
            foreach (var rel in labels.Relations)
                foreach (var name in rel.Chain.Distinct())
                    if (!_globals.ContainsKey(name) && _lattice.Contains(name)) _globals[name] = ("label", rel.Span);
        }

        foreach (var item in items)
        {
            switch (item)
            {
                case KeyDecl k:
                    if (!DeclareGlobal(k.Name, "key domain", k.Span)) break;
                    CheckNotBuiltinTypeName(k.Name, "key domain", k.Span);
                    var kr = ResolvePrim(k.Repr, intOnly: false);
                    if (kr != null) _keys[k.Name] = new KeyDomainType(k.Name, kr);
                    break;
                case ClockDecl c:
                    if (!DeclareGlobal(c.Name, "clock domain", c.Span)) break;
                    CheckNotBuiltinTypeName(c.Name, "clock domain", c.Span);
                    var cr = ResolvePrim(c.Repr, intOnly: true);
                    if (cr != null) _clocks[c.Name] = new ClockDomainType(c.Name, cr, c.Unit);
                    break;
                case TypeDecl t:
                    if (!DeclareGlobal(t.Name, "type", t.Span)) break;
                    CheckNotBuiltinTypeName(t.Name, "type", t.Span);
                    _records[t.Name] = new RecordType(t.Name);
                    break;
                case LabellerDecl l:
                    if (DeclareGlobal(l.Name, "labeller", l.Span)) _labellers[l.Name] = (l, new List<(string, DslType)>());
                    break;
                case GraphDecl g:
                    DeclareGlobal(g.Name, "graph", g.Span);
                    break;
            }
        }
    }

    private bool DeclareGlobal(string name, string kind, SourceSpan span)
    {
        if (_globals.TryGetValue(name, out var prev))
        {
            _diags.Error("E0101", $"duplicate global name '{name}' ({kind}); already declared as a {prev.Kind}", span, $"note: previous declaration at {prev.Span}");
            return false;
        }
        _globals[name] = (kind, span);
        return true;
    }

    private void CheckNotBuiltinTypeName(string name, string kind, SourceSpan span)
    {
        if (PrimType.All.ContainsKey(name) || _formats.Contains(name))
            _diags.Error("E0102", $"{kind} '{name}' has the name of a primitive type or format", span);
    }

    private PrimType? ResolvePrim(TypeRef t, bool intOnly)
    {
        if (t is NamedTypeRef n && PrimType.All.TryGetValue(n.Name, out var p) && (!intOnly || p.IsInteger)) return p;
        _diags.Error("E0002", intOnly ? "clock domains must be represented by i32, i64, u32 or u64" : "key domains must be represented by a primitive type", t.Span);
        return null;
    }

    // --- Types (L§5) ---

    private void ResolveTypes(IEnumerable<TypeDecl> decls)
    {
        foreach (var t in decls)
        {
            if (!_records.TryGetValue(t.Name, out var rec) || rec.Fields.Count > 0) continue;
            var seen = new HashSet<string>();
            foreach (var f in t.Fields)
            {
                if (!seen.Add(f.Name)) { _diags.Error("E0201", $"duplicate field '{f.Name}' in type '{t.Name}'", f.Span); continue; }
                rec.Fields.Add(new RecordField(f.Name, ResolveType(f.Type, allowBuiltins: false)));
            }
        }
        foreach (var t in decls)
            if (_records.TryGetValue(t.Name, out var rec) && TypeOps.IsRecursive(rec))
                _diags.Error("E0203", $"record type '{t.Name}' is recursive", t.Span);
    }

    private DslType ResolveType(TypeRef t, bool allowBuiltins)
    {
        switch (t)
        {
            case ListTypeRef l:
                return new ListType(ResolveType(l.Element, allowBuiltins));
            case NamedTypeRef n:
                if (PrimType.All.TryGetValue(n.Name, out var p)) return p;
                if (n.Name == "json") return JsonType.Instance;
                if (_formats.Contains(n.Name)) return new FormatType(n.Name);
                if (_keys.TryGetValue(n.Name, out var k)) return k;
                if (_clocks.TryGetValue(n.Name, out var c)) return c;
                if (_records.TryGetValue(n.Name, out var r)) return r;
                if (allowBuiltins && n.Name == "label") return LabelValueType.Instance;
                if (allowBuiltins && n.Name == "replica") return ReplicaType.Instance;
                if (n.Name is "label" or "replica")
                    _diags.Error("E0105", $"built-in type '{n.Name}' is only allowed in labeller parameters", n.Span);
                else
                    _diags.Error("E0105", $"unknown type '{n.Name}'", n.Span);
                return ErrorType.Instance;
        }
        return ErrorType.Instance;
    }

    private static TypeInfo ToTypeInfo(DslType t) => t switch
    {
        PrimType p => new TypeInfo(TypeInfoKind.Primitive, p.Name),
        JsonType => new TypeInfo(TypeInfoKind.Json, "json"),
        FormatType f => new TypeInfo(TypeInfoKind.Format, f.Name),
        KeyDomainType k => new TypeInfo(TypeInfoKind.KeyDomain, k.Name, Repr: k.Repr.Name),
        ClockDomainType c => new TypeInfo(TypeInfoKind.ClockDomain, c.Name, Repr: c.Repr.Name, ClockUnit: c.Unit),
        RecordType r => new TypeInfo(TypeInfoKind.Record, r.Name),
        ListType l => new TypeInfo(TypeInfoKind.List, l.Display, Element: ToTypeInfo(l.Element)),
        LabelValueType => new TypeInfo(TypeInfoKind.Label, "label"),
        ReplicaType => new TypeInfo(TypeInfoKind.Replica, "replica"),
        _ => new TypeInfo(TypeInfoKind.Json, t.Display),
    };

    private static RecordTypeInfo ToRecordInfo(RecordType r) =>
        new(r.Name, r.Fields.Select(f => new FieldInfo(f.Name, ToTypeInfo(f.Type))).ToList());

    // --- Labellers (L§8.3) ---

    private IReadOnlySet<string> LabellerNames => _labellers.Keys.ToHashSet();

    private void CheckLabellers()
    {
        foreach (var (name, entry) in _labellers)
        {
            var decl = entry.Decl;
            if (decl.Parameters.Count > 2)
                _diags.Error("E0002", $"labeller '{name}' has {decl.Parameters.Count} parameters; at most 2 are allowed", decl.Span);
            var scope = new ExprScope { Lattice = _lattice, Labellers = LabellerNames, IsLabeller = true };
            foreach (var p in decl.Parameters)
            {
                var t = ResolveType(p.Type, allowBuiltins: true);
                entry.Params.Add((p.Name, t));
                if (!scope.Parameters.TryAdd(p.Name, t))
                    _diags.Error("E0104", $"duplicate parameter '{p.Name}' in labeller '{name}'", p.Span);
                WarnIfShadowsLabel(p);
            }
            var bodyType = ExprChecker.TypeOf(decl.Body, scope, _diags);
            if (bodyType is not (LabelValueType or ErrorType))
                _diags.Error("E0710", $"labeller '{name}' has type '{bodyType.Display}', not 'label'", decl.Body.Span);
        }
    }

    private void WarnIfShadowsLabel(Parameter p)
    {
        if (_lattice.Declared && _lattice.Contains(p.Name))
            _diags.Warning("W0102", $"parameter '{p.Name}' shadows the label of the same name", p.Span);
    }
}
