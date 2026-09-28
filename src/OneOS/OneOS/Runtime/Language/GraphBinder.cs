using System;
using System.Collections.Generic;
using System.Linq;
using OneOS.Runtime.Language.Ast;
using OneOS.Runtime.Language.Models;

namespace OneOS.Runtime.Language;

// Spawn-time binding (L§9.1): check the arguments, bind the graph parameters, evaluate every
// `process` argument vector, and re-run label analysis with the parameters constant-folded.
public static class GraphBinder
{
    // `spawn G(args)` against a compiled program.
    public static (CompiledGraph? Graph, IReadOnlyList<Diagnostic> Diagnostics) Spawn(CompiledProgram program, SpawnCommand cmd)
    {
        var diags = new DiagnosticBag();
        var graph = program.Graph(cmd.Graph);
        if (graph == null)
        {
            diags.Error("E0801", $"spawn: unknown graph '{cmd.Graph}'", cmd.Span);
            return (null, diags.Items);
        }

        var values = new List<object?>();
        foreach (var a in cmd.Args)
        {
            var v = AbstractEval.Eval(a, new AbstractEnv { Lattice = LabelLattice.Trivial() });
            if (v is KnownVal { Value: string or long or double or bool } k) values.Add(k.Value);
            else
            {
                diags.Error("E0803", $"spawn: argument '{AppCompiler.RenderExpr(a)}' is not a literal value", a.Span);
                values.Add(null);
            }
        }
        if (diags.HasErrors) return (null, diags.Items);
        return Bind(graph, values, cmd.Span);
    }

    // Binds positional arguments. Values are string, long, double or bool.
    public static (CompiledGraph? Graph, IReadOnlyList<Diagnostic> Diagnostics) Bind(CompiledGraph graph, IReadOnlyList<object?> args, SourceSpan? span = null)
    {
        var diags = new DiagnosticBag();
        if (graph.HasErrors)
        {
            diags.AddRange(graph.Diagnostics.Where(d => d.IsError));
            return (null, diags.Items);
        }
        if (args.Count != graph.Parameters.Count)
        {
            diags.Error("E0802", $"spawn {graph.Name}: expected {graph.Parameters.Count} argument{(graph.Parameters.Count == 1 ? "" : "s")}, got {args.Count}", span);
            return (null, diags.Items);
        }

        var values = new List<object?>();
        for (int i = 0; i < args.Count; i++)
        {
            var p = graph.Parameters[i];
            var v = Coerce(args[i], p.Type);
            if (v == null)
                diags.Error("E0803", $"spawn {graph.Name}: argument {i + 1} ({AbstractEval.Render(args[i])}) does not have parameter '{p.Name}' type '{p.Type}'", span);
            values.Add(v);
        }
        if (diags.HasErrors) return (null, diags.Items);

        // Evaluate argv (L§6.1): each expression becomes exactly one argument vector entry.
        var env = new AbstractEnv { Lattice = graph.Lattice };
        for (int i = 0; i < values.Count; i++) env.Values[graph.Parameters[i].Name] = new KnownVal(values[i]!);
        var nodes = new List<GraphNode>();
        foreach (var n in graph.Nodes)
        {
            var argv = new List<string>();
            foreach (var e in new[] { n.Process.Command }.Concat(n.Process.Args))
            {
                if (AbstractEval.Eval(e, env) is KnownVal { Value: string s }) argv.Add(s);
                else diags.Error("E0804", $"spawn {graph.Name}: process argument '{AppCompiler.RenderExpr(e)}' of '{n.Name}' did not evaluate to a string", e.Span);
            }
            nodes.Add(n with { Process = n.Process with { Argv = argv } });
        }
        if (diags.HasErrors) return (null, diags.Items);

        // Re-run label analysis with bound parameters; refuse the spawn on errors (E0804).
        var (analysis, labelDiags) = LabelAnalysis.Run(graph with { Nodes = nodes }, values);
        var errors = labelDiags.Where(d => d.IsError).ToList();
        if (errors.Count > 0)
        {
            diags.Add(new Diagnostic("E0804", Severity.Error,
                $"spawn {graph.Name}: label analysis with bound parameters failed", span ?? graph.Span,
                errors.SelectMany(d => new[] { $"{d.Code}: {d.Message}" }.Concat(d.Notes.Select(n => "  " + n))).ToList()));
            diags.AddRange(errors);
            return (null, diags.Items);
        }

        var kept = graph.Diagnostics.Where(d => !IsLabelAnalysisDiagnostic(d));
        var bound = graph with { Args = values, Nodes = nodes, Analysis = analysis, Diagnostics = kept.Concat(labelDiags).ToList() };
        return (bound, labelDiags);
    }

    private static bool IsLabelAnalysisDiagnostic(Diagnostic d) =>
        d.Code.StartsWith("E073", StringComparison.Ordinal) || d.Code.StartsWith("W073", StringComparison.Ordinal)
        || d.Code is "W0710" or "W0711" || (d.Severity == Severity.Note && d.Code.Length == 0);

    // Checks a value against a primitive parameter type and normalizes it (integers as long, floats as double).
    private static object? Coerce(object? v, string type) => (v, type) switch
    {
        (string s, "string") => s,
        (bool b, "bool") => b,
        (long l, "i32") when l >= int.MinValue && l <= int.MaxValue => l,
        (long l, "u32") when l >= 0 && l <= uint.MaxValue => l,
        (long l, "i64") => l,
        (long l, "u64") when l >= 0 => l,
        (int i, _) => Coerce((long)i, type),
        (long l, "f32" or "f64") => (double)l,
        (double d, "f32" or "f64") => d,
        (float f, "f32" or "f64") => (double)f,
        _ => null,
    };
}
