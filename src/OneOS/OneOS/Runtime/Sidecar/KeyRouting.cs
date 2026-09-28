using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace OneOS.Runtime.Sidecar;

// XXH64 (seed 0): a deterministic 64-bit hash, stable across processes and restarts (L§6.4).
public static class XxHash64
{
    private const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL,
        P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;

    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        int len = data.Length, i = 0;
        ulong h;
        if (len >= 32)
        {
            ulong v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
            for (; i + 32 <= len; i += 32)
            {
                v1 = Round(v1, Read64(data, i));
                v2 = Round(v2, Read64(data, i + 8));
                v3 = Round(v3, Read64(data, i + 16));
                v4 = Round(v4, Read64(data, i + 24));
            }
            h = Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18);
            h = Merge(Merge(Merge(Merge(h, v1), v2), v3), v4);
        }
        else h = seed + P5;
        h += (ulong)len;
        for (; i + 8 <= len; i += 8) h = Rotl(h ^ Round(0, Read64(data, i)), 27) * P1 + P4;
        if (i + 4 <= len) { h = Rotl(h ^ (BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) * P1), 23) * P2 + P3; i += 4; }
        for (; i < len; i++) h = Rotl(h ^ (data[i] * P5), 11) * P1;
        h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
        return h;
    }

    private static ulong Read64(ReadOnlySpan<byte> d, int i) => BinaryPrimitives.ReadUInt64LittleEndian(d[i..]);
    private static ulong Rotl(ulong x, int r) => (x << r) | (x >> (64 - r));
    private static ulong Round(ulong acc, ulong input) => Rotl(acc + input * P2, 31) * P1;
    private static ulong Merge(ulong acc, ulong v) => (acc ^ Round(0, v)) * P1 + P4;
}

// Key extraction and key-group routing for keyed receivers (L§6.4, L§6.5).
public static class KeyRouter
{
    // Injective canonical encoding of a key tuple: per component, a type tag, a length and the value's
    // canonical text (strings as UTF-8, numbers in invariant form, booleans as true/false).
    public static byte[] Canonical(IReadOnlyList<JsonElement> tuple)
    {
        var buf = new List<byte>();
        foreach (var v in tuple)
        {
            var (tag, text) = v.ValueKind switch
            {
                JsonValueKind.String => ((byte)'s', v.GetString()!),
                JsonValueKind.Number when v.TryGetInt64(out var l) => ((byte)'i', l.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                JsonValueKind.Number => ((byte)'f', v.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                JsonValueKind.True => ((byte)'b', "true"),
                JsonValueKind.False => ((byte)'b', "false"),
                _ => throw new ArgumentException($"a key must be a primitive value, not {v.ValueKind}"),
            };
            var bytes = Encoding.UTF8.GetBytes(text);
            buf.Add(tag);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, bytes.Length);
            buf.AddRange(len.ToArray());
            buf.AddRange(bytes);
        }
        return buf.ToArray();
    }

    public static IReadOnlyList<JsonElement> ExtractKey(JsonElement message, IReadOnlyList<IReadOnlyList<string>> keyPaths)
    {
        var tuple = new List<JsonElement>();
        foreach (var path in keyPaths)
        {
            var cur = message;
            foreach (var step in path)
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(step, out cur))
                    throw new KeyNotFoundException($"message has no key field '{string.Join(".", path)}'");
            tuple.Add(cur);
        }
        return tuple;
    }

    // g = H(canonical(t)) mod G.
    public static int KeyGroup(IReadOnlyList<JsonElement> tuple, int keyGroups) => (int)(XxHash64.Hash(Canonical(tuple)) % (ulong)keyGroups);

    // instance = floor(g · n / G).
    public static int Instance(int keyGroup, int instances, int keyGroups) => (int)((long)keyGroup * instances / keyGroups);

    // The key groups instance i owns under that rule: [ceil(i·G/n), ceil((i+1)·G/n)).
    public static (int From, int To) OwnedGroups(int instance, int instances, int keyGroups) =>
        ((int)(((long)instance * keyGroups + instances - 1) / instances), (int)(((long)(instance + 1) * keyGroups + instances - 1) / instances));
}
