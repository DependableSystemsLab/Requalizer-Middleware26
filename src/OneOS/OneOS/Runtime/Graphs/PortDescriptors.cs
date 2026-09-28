using System.Collections.Generic;
using System.Linq;
using OneOS.Runtime.Language.Models;
using OneOS.Runtime.Scheduling;

namespace OneOS.Runtime.Graphs;

// Which descriptor carries each port of a typed-stream process (L§6.1; plan step 6, Q6.1):
//   - the default input port is stdin (0): the only input, else the one named `in`, else the first declared;
//   - the default output port is stdout (1): likewise, with `out`;
//   - an output port named `stderr` is stderr (2);
//   - every other port gets 3, 4, … in declaration order.
// The process finds them in its environment as ONEOS_PORT_<name>=<fd>.
public static class PortDescriptors
{
    public const string StderrPort = "stderr";

    public static IReadOnlyDictionary<string, int> Assign(IReadOnlyList<AgentPortInfo> ports)
    {
        var map = new Dictionary<string, int>();
        var inputs = ports.Where(p => p.Direction == PortDirection.In).ToList();
        var outputs = ports.Where(p => p.Direction == PortDirection.Out && p.Name != StderrPort).ToList();
        if (Default(inputs, "in") is { } i) map[i.Name] = 0;
        if (Default(outputs, "out") is { } o) map[o.Name] = 1;
        if (ports.Any(p => p.Direction == PortDirection.Out && p.Name == StderrPort)) map[StderrPort] = 2;
        int next = 3;
        foreach (var p in ports)
            if (!map.ContainsKey(p.Name)) map[p.Name] = next++;
        return map;
    }

    private static AgentPortInfo? Default(List<AgentPortInfo> ports, string name) =>
        ports.Count == 1 ? ports[0] : ports.FirstOrDefault(p => p.Name == name) ?? ports.FirstOrDefault();
}
