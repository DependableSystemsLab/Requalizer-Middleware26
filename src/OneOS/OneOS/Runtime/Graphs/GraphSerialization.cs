using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OneOS.Runtime.Language.Models;
using OneOS.Runtime.Scheduling;

namespace OneOS.Runtime.Graphs;

// JSON round-tripping of the bound graph and its deployment plan, which travel to every host inside
// the Registry's graph-instance record.
public static class GraphSerialization
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(CompiledGraph graph) => JsonSerializer.Serialize(graph, Options);
    public static string Serialize(GraphInstanceInfo instance) => JsonSerializer.Serialize(instance, Options);

    public static CompiledGraph DeserializeGraph(string json)
    {
        var g = JsonSerializer.Deserialize<CompiledGraph>(json, Options)!;
        return g with { Args = g.Args?.Select(Scalar).ToList() };
    }

    public static GraphInstanceInfo DeserializeInstance(string json)
    {
        var i = JsonSerializer.Deserialize<GraphInstanceInfo>(json, Options)!;
        return i with { Args = i.Args.Select(Scalar).ToList() };
    }

    // Spawn arguments come back as JsonElements; restore the CLR values the binder produced.
    private static object? Scalar(object? v) => v is JsonElement e ? e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        _ => null,
    } : v;
}
