using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OneOS.Runtime.Language;
using OneOS.Runtime.Scheduling;

namespace OneOS.Runtime.Graphs;

// The shell's `graph` command:
//   graph check <files>                 compile and show diagnostics
//   graph plan  <files> <G(args)>       plan without deploying
//   graph spawn <files> <G(args)>       plan, commit and start
//   graph ls                            list graph instances
//   graph stop  <instance-id>           stop a graph instance
// <files> is a comma-separated list of DSL files in the distributed file system.
public static class GraphShellCommand
{
    public const string Usage =
        "usage: graph check <files> | graph plan <files> <Graph(args)> | graph spawn <files> <Graph(args)> | graph ls | graph stop <id>\n";

    public static async Task<string> RunAsync(IVirtualRuntime rt, string args, Func<string, string> resolvePath, string cwd)
    {
        var parts = args.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return Usage;
        try
        {
            switch (parts[0])
            {
                case "ls":
                {
                    var sb = new StringBuilder($"{"INSTANCE",-24} {"GRAPH",-20} {"STATE",-10} AGENTS\n");
                    foreach (var g in rt.GetGraphInstances().Values.OrderBy(g => g.CreatedAt))
                        sb.Append($"{g.GraphInstanceId,-24} {g.GraphName,-20} {g.State,-10} {g.Agents.Count(a => a.State == AgentState.Running)}/{g.Agents.Count(a => a.Role == AgentRole.Primary)} running\n");
                    return sb.ToString();
                }
                case "stop" when parts.Length > 1:
                    await rt.StopGraph(parts[1]);
                    return $"graph instance {parts[1]} stopped\n";
                case "check" when parts.Length > 1:
                {
                    var interp = await Load(rt, parts[1], resolvePath, cwd);
                    var program = interp.Compile();
                    var sb = new StringBuilder();
                    foreach (var d in program.Diagnostics) sb.Append(d).Append('\n');
                    sb.Append(program.HasErrors ? "compilation failed\n" : $"ok: {string.Join(", ", program.Graphs.Select(g => g.Name))}\n");
                    return sb.ToString();
                }
                case "plan" or "spawn" when parts.Length > 2:
                {
                    var interp = await Load(rt, parts[1], resolvePath, cwd);
                    var program = interp.Compile();
                    if (program.HasErrors)
                        return string.Join("\n", program.Diagnostics.Where(d => d.IsError)) + "\ncompilation failed\n";
                    if (parts[0] == "plan")
                    {
                        var (cmd, pd) = new AppParser().ParseSpawn("spawn " + parts[2]);
                        if (cmd == null) return string.Join("\n", pd) + "\n";
                        var (bound, bd) = GraphBinder.Spawn(program, cmd);
                        if (bound == null) return string.Join("\n", bd) + "\n";
                        return GraphReport.Format(await rt.PlanGraph(bound, bound.Args!), bound, rt.TakeSnapshot());
                    }
                    var outcome = await interp.SpawnAsync("spawn " + parts[2]);
                    var text = new StringBuilder();
                    foreach (var d in outcome.Diagnostics.Where(d => d.IsError)) text.Append(d).Append('\n');
                    foreach (var d in outcome.SchedulerDiagnostics) text.Append(d).Append('\n');
                    if (outcome.Handle != null) text.Append(GraphReport.Format(outcome.Handle.Info, outcome.Graph, rt.TakeSnapshot(),
                        GraphReport.Sections.Summary | GraphReport.Sections.Placement));
                    return text.ToString();
                }
            }
        }
        catch (SchedulingException ex)
        {
            return string.Join("\n", ex.Diagnostics) + "\n";
        }
        catch (Exception ex)
        {
            return $"graph: {ex.Message}\n";
        }
        return Usage;
    }

    private static async Task<AppInterpreter> Load(IVirtualRuntime rt, string files, Func<string, string> resolvePath, string cwd)
    {
        var interp = new AppInterpreter(rt);
        foreach (var f in files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = resolvePath(f);
            interp.AddSource(Encoding.UTF8.GetString(await rt.ReadFileAsync(path, cwd)), path);
        }
        return interp;
    }
}
