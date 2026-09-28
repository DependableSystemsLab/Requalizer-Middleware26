using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OneOS.Runtime.Scheduling;

// Finds the cluster-wide sockets a JavaScript program listens on (plan step 8.5), as external-source locations:
// "socket:<port>" for a literal port, "socket:*" otherwise. A light source scan, not an analysis: any
// `.listen(` call counts (net, http and express servers all listen through it). The runtime check at
// `listen` time (JavaScriptAgent) backs it up.
public static class JavaScriptSockets
{
    public const string LocationPrefix = "socket:";
    public const string AnyPort = LocationPrefix + "*";

    private static readonly Regex Listen = new(@"\.listen\s*\(\s*(?<port>\d+)?", RegexOptions.Compiled);

    public static IReadOnlyList<string> Find(string source) =>
        Listen.Matches(source).Select(m => m.Groups["port"].Success ? LocationPrefix + m.Groups["port"].Value : AnyPort).Distinct().ToList();

    // Whether a graph agent's external sources allow it to listen on `port`.
    public static bool Allows(IEnumerable<string> sourceLocations, int port) =>
        sourceLocations.Any(l => l == AnyPort || l == LocationPrefix + port);

    // The program file of a `node` command: the first operand after node's flags.
    public static string? Script(IReadOnlyList<string> argv) =>
        argv.Count > 1 && System.IO.Path.GetFileName(argv[0]) is "node" or "nodejs" ? argv.Skip(1).FirstOrDefault(a => !a.StartsWith("-")) : null;
}
