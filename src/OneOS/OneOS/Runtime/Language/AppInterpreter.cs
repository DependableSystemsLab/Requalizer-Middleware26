using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OneOS.Runtime.Language.Models;
using OneOS.Runtime.Scheduling;

namespace OneOS.Runtime.Language;

// Front door for the DSL: collects the source files of a compilation unit, compiles them, and
// handles `spawn` commands by binding the graph and handing it to the runtime (L§9.1).
public sealed class AppInterpreter
{
    private readonly IVirtualRuntime _runtime;
    private readonly FormatRegistry _formats;
    private readonly List<SourceFile> _sources = new();

    public AppInterpreter(IVirtualRuntime runtime, FormatRegistry? formats = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _formats = formats ?? FormatRegistry.CreateDefault();
    }

    public CompiledProgram? Program { get; private set; }

    public void AddSource(string text, string fileName = "<input>")
    {
        _sources.Add(new SourceFile(fileName, text));
        Program = null;
    }

    public CompiledProgram Compile() => Program ??= new AppCompiler(_formats).Compile(_sources);

    // Parses and runs `spawn G(args)`: checks the command (E0801–E0804), then hands the graph and
    // arguments to the runtime, which plans and commits it (scheduling-spec §1.3). Scheduling
    // failures come back as SpawnDiagnostics carrying the SP-code.
    public async Task<SpawnOutcome> SpawnAsync(string command, CancellationToken ct = default)
    {
        var program = Compile();
        var (cmd, parseDiags) = new AppParser().ParseSpawn(command);
        if (cmd == null) return new SpawnOutcome(null, null, parseDiags, Array.Empty<SpawnDiagnostic>());
        var (bound, diags) = GraphBinder.Spawn(program, cmd);
        if (bound == null) return new SpawnOutcome(null, null, diags, Array.Empty<SpawnDiagnostic>());
        try
        {
            var handle = await _runtime.SpawnGraph(bound, bound.Args!, ct);
            return new SpawnOutcome(handle, bound, diags, handle.Plan.Warnings);
        }
        catch (SchedulingException ex)
        {
            return new SpawnOutcome(null, bound, diags, ex.Diagnostics);
        }
    }
}

public sealed record SpawnOutcome(GraphInstanceHandle? Handle, CompiledGraph? Graph, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<SpawnDiagnostic> SchedulerDiagnostics)
{
    public bool Succeeded => Handle != null;
}
