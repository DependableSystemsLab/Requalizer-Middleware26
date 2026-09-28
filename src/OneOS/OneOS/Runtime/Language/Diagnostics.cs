using System.Collections.Generic;
using System.Linq;
using System.Text;
using OneOS.Runtime.Language.Ast;

namespace OneOS.Runtime.Language;

public enum Severity { Error, Warning, Note }

// A compiler diagnostic (L§10). Notes carry witness paths and other detail lines.
public sealed record Diagnostic(string Code, Severity Severity, string Message, SourceSpan Span, IReadOnlyList<string> Notes)
{
    public bool IsError => Severity == Severity.Error;

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Severity switch { Severity.Error => "error", Severity.Warning => "warning", _ => "note" });
        if (Code.Length > 0) sb.Append(' ').Append(Code);
        sb.Append(": ").Append(Message);
        if (Span != SourceSpan.None) sb.Append("\n  --> ").Append(Span);
        foreach (var n in Notes) sb.Append("\n  ").Append(n);
        return sb.ToString();
    }
}

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = new();

    public IReadOnlyList<Diagnostic> Items => _items;
    public bool HasErrors => _items.Any(d => d.IsError);

    public void Error(string code, string message, SourceSpan? span, params string[] notes) =>
        _items.Add(new Diagnostic(code, Severity.Error, message, span ?? SourceSpan.None, notes));

    public void Warning(string code, string message, SourceSpan? span, params string[] notes) =>
        _items.Add(new Diagnostic(code, Severity.Warning, message, span ?? SourceSpan.None, notes));

    public void Note(string code, string message, SourceSpan? span, params string[] notes) =>
        _items.Add(new Diagnostic(code, Severity.Note, message, span ?? SourceSpan.None, notes));

    public void Add(Diagnostic d) => _items.Add(d);
    public void AddRange(IEnumerable<Diagnostic> ds) => _items.AddRange(ds);

    // L§10: label analysis MUST NOT run if any error in E0001–E0623 or E0701–E0724 exists.
    public bool HasStructuralErrors => _items.Any(d => d.IsError && IsStructural(d.Code));

    private static bool IsStructural(string code)
    {
        if (code.Length != 5 || code[0] != 'E' || !int.TryParse(code.AsSpan(1), out var n)) return false;
        return (n >= 1 && n <= 623) || (n >= 701 && n <= 724);
    }
}
