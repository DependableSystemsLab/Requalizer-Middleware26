using System.Diagnostics;
using System.Text.Json;
using OneOS.Runtime.Driver;
using OneOS.Runtime.Graphs;
using static OneOS.Tests.Language.TestUtil;

namespace OneOS.Tests.Driver;

// The DIFT policy module an IDM-enabled JavaScript agent gets (instrument.js argv[7]): the graph's labellers
// translated to JavaScript, and its declared label order as rules.
public class DiftPolicyModuleTests
{
    private const string Program = """
        labels { public < client; client < internal; }
        type txn { sensitivity: i64, tag: string }
        labeller by_sensitivity (t: txn) = t.sensitivity == 0 ? public : (t.sensitivity == 1 ? client : internal);
        labeller by_tag (t: txn) = t.tag.startsWith('pii') || t.tag.contains('card') ? internal : join(public, client);
        // Not attached anywhere: only the program's injection points use it.
        labeller floor (new: txn) = leq(client, public) || new.sensitivity < -1 ? internal : meet(client, internal);
        graph g() {
          topology {
            node a () => (o: txn) = process('a');
            node b (i: txn) = process('b');
            edge e1: a.o --> b.i;
          }
          policy { label(by_sensitivity): a.o; }
        }
        """;

    [Fact]
    public void RulesAreTheDeclaredCoveringPairs()
    {
        var p = Compile("labels { public < client; client < internal; public < internal; }");
        AssertNoErrors(p);
        Assert.Equal(new[] { "public -> client", "client -> internal" }, p.Lattice.FlowRules());
    }

    [Fact]
    public async Task NodeLoadsTheModuleAndRunsTheLabellers()
    {
        var program = Compile(Program);
        AssertNoErrors(program);
        // As the runtime sees it: through the Registry's graph JSON.
        var graph = GraphSerialization.DeserializeGraph(GraphSerialization.Serialize(program.Graph("g")!));
        Assert.Equal(new[] { "by_sensitivity", "by_tag", "floor" }, graph.Labellers.Keys.OrderBy(k => k, StringComparer.Ordinal));

        var dir = Directory.CreateTempSubdirectory("oneos-dift-policy-");
        try
        {
            var module = Path.Combine(dir.FullName, "policy.js");
            await File.WriteAllTextAsync(module, DiftPolicyModule.Generate(graph, "agent-uri"));
            var psi = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("""
                const p = require(process.argv[1]);
                // Each labeller as the instrumented program gets it: rebuilt from its source text (Turnstile.stringify),
                // outside the module.
                const l = Object.fromEntries(Object.entries(p.labellers).map(([k, f]) => [k, new Function(`return (${f.toString()})`)()]));
                console.log(JSON.stringify({
                  rules: p.rules,
                  sensitivity: [0, 1, 2].map(s => l.by_sensitivity({ sensitivity: s })),
                  tag: ['pii-name', 'visa-card', 'other'].map(tag => l.by_tag({ tag })),
                  floor: [l.floor({ sensitivity: 0 }), l.floor({ sensitivity: -5 })],
                }));
                """);
            psi.ArgumentList.Add(module);
            using var node = Process.Start(psi)!;
            var stdout = await node.StandardOutput.ReadToEndAsync();
            var stderr = await node.StandardError.ReadToEndAsync();
            await node.WaitForExitAsync();
            Assert.True(node.ExitCode == 0, stderr + "\n" + await File.ReadAllTextAsync(module));

            var r = JsonDocument.Parse(stdout).RootElement;
            string[] Strings(string name) => r.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();
            Assert.Equal(new[] { "public -> client", "client -> internal" }, Strings("rules"));
            Assert.Equal(new[] { "public", "client", "internal" }, Strings("sensitivity"));
            Assert.Equal(new[] { "internal", "internal", "client" }, Strings("tag"));
            Assert.Equal(new[] { "client", "internal" }, Strings("floor"));
        }
        finally { dir.Delete(recursive: true); }
    }
}
