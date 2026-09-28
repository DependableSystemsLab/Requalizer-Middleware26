using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using OneOS.Runtime.Language.Ast;

namespace OneOS.Runtime.Language;

// The completed label lattice (L§8.1) with precomputed ⊑, ⊔ and ⊓ tables over label indices.
// Immutable and JSON-serializable, so it can travel inside the compiled graph (L§11, S§2.1).
public sealed class LabelLattice
{
    public const string BottomName = "bottom";
    public const string GlobalTopName = "top";

    public IReadOnlyList<string> Labels { get; }
    public IReadOnlyList<bool> Synthetic { get; }
    // Compartment index per label; -1 for the shared `bottom` and the global `top`.
    public IReadOnlyList<int> Compartments { get; }
    public int CompartmentCount { get; }
    public IReadOnlyList<IReadOnlyList<bool>> LeqTable { get; }
    // -1 where a pair has no least upper bound (E0703).
    public IReadOnlyList<IReadOnlyList<int>> JoinTable { get; }
    public IReadOnlyList<IReadOnlyList<int>> MeetTable { get; }
    public int BottomIndex { get; }
    public int TopIndex { get; }
    public bool ForbiddenTop { get; }
    public bool Strict { get; }
    public bool Declared { get; }

    private readonly Dictionary<string, int> _index;

    [JsonConstructor]
    public LabelLattice(IReadOnlyList<string> labels, IReadOnlyList<bool> synthetic, IReadOnlyList<int> compartments, int compartmentCount,
        IReadOnlyList<IReadOnlyList<bool>> leqTable, IReadOnlyList<IReadOnlyList<int>> joinTable, IReadOnlyList<IReadOnlyList<int>> meetTable,
        int bottomIndex, int topIndex, bool forbiddenTop, bool strict, bool declared)
    {
        Labels = labels; Synthetic = synthetic; Compartments = compartments; CompartmentCount = compartmentCount;
        LeqTable = leqTable; JoinTable = joinTable; MeetTable = meetTable;
        BottomIndex = bottomIndex; TopIndex = topIndex; ForbiddenTop = forbiddenTop; Strict = strict; Declared = declared;
        _index = new Dictionary<string, int>();
        for (int i = 0; i < labels.Count; i++) _index[labels[i]] = i;
    }

    public int Count => Labels.Count;
    public string Bottom => Labels[BottomIndex];
    public string Top => Labels[TopIndex];

    public bool Contains(string name) => _index.ContainsKey(name);
    public int IndexOf(string name) => _index.TryGetValue(name, out var i) ? i : throw new KeyNotFoundException($"unknown label '{name}'");
    public string Name(int i) => Labels[i];

    public bool Leq(int a, int b) => LeqTable[a][b];
    public bool Leq(string a, string b) => Leq(IndexOf(a), IndexOf(b));
    public int Join(int a, int b) => JoinTable[a][b] >= 0 ? JoinTable[a][b] : throw new InvalidOperationException($"no least upper bound for '{Labels[a]}' and '{Labels[b]}'");
    public string Join(string a, string b) => Labels[Join(IndexOf(a), IndexOf(b))];
    public int Meet(int a, int b) => MeetTable[a][b];
    public string Meet(string a, string b) => Labels[Meet(IndexOf(a), IndexOf(b))];

    public int JoinAll(IEnumerable<int> xs) => xs.Aggregate(BottomIndex, Join);
    public int MeetAll(IEnumerable<int> xs) => xs.Aggregate(TopIndex, Meet);

    // The label no data may carry and no instance may hold (only when there are several compartments).
    public bool IsForbidden(int x) => ForbiddenTop && x == TopIndex;

    // Labels x with lo ⊑ x ⊑ hi.
    public IEnumerable<int> Interval(int lo, int hi) => Enumerable.Range(0, Count).Where(x => Leq(lo, x) && Leq(x, hi));

    public IReadOnlyList<int> Maximal(IEnumerable<int> xs)
    {
        var set = xs.Distinct().ToList();
        return set.Where(x => !set.Any(y => y != x && Leq(x, y))).OrderBy(x => x).ToList();
    }

    // Deterministic order: by height in the lattice, then by name (used for lane ordering, S§4.5).
    public IReadOnlyList<int> TopologicalOrder() =>
        Enumerable.Range(0, Count).OrderBy(x => Enumerable.Range(0, Count).Count(y => y != x && Leq(y, x)))
            .ThenBy(x => Labels[x], StringComparer.Ordinal).ToList();

    // The declared order as flow rules "a -> b" (data labelled a may flow to b): the covering pairs of the
    // declared labels, i.e. a ⊏ b with no declared label strictly between. The synthetic ⊥ and ⊤ are left out.
    // For `labels { public < client; client < internal; }`: "public -> client", "client -> internal".
    public IReadOnlyList<string> FlowRules()
    {
        var declared = Enumerable.Range(0, Count).Where(x => !Synthetic[x]).ToList();
        bool Below(int a, int b) => a != b && Leq(a, b);
        return TopologicalOrder().Where(declared.Contains)
            .SelectMany(a => declared.Where(b => Below(a, b) && !declared.Any(c => Below(a, c) && Below(c, b)))
                .OrderBy(b => Labels[b], StringComparer.Ordinal).Select(b => $"{Labels[a]} -> {Labels[b]}"))
            .ToList();
    }

    // Whether all given labels lie within one compartment (synthetic bottom fits anywhere).
    public bool SameCompartment(IEnumerable<int> xs)
    {
        int c = -1;
        foreach (var x in xs)
        {
            if (x == BottomIndex && Compartments[x] < 0) continue;
            if (IsForbidden(x)) return false;
            if (c < 0) c = Compartments[x];
            else if (Compartments[x] != c) return false;
        }
        return true;
    }

    public override string ToString() => $"LabelLattice({string.Join(", ", Labels)}; ⊥={Bottom}, ⊤={Top}{(ForbiddenTop ? " forbidden" : "")})";

    // --- Construction (L§8.1) ---

    // The lattice used when there is no `labels` block: the single label ⊥.
    public static LabelLattice Trivial() => new(
        new[] { BottomName }, new[] { true }, new[] { -1 }, 0,
        new[] { new[] { true } }, new[] { new[] { 0 } }, new[] { new[] { 0 } }, 0, 0, false, false, false);

    public static LabelLattice Build(LabelsDecl decl, DiagnosticBag diags)
    {
        // Declared labels in order of first appearance, and the declared ⊏ pairs.
        var names = new List<string>();
        var index = new Dictionary<string, int>();
        var pairs = new List<(int Lo, int Hi, SourceSpan Span)>();
        int Intern(string n, SourceSpan span)
        {
            if (index.TryGetValue(n, out var i)) return i;
            if (n == BottomName || n == GlobalTopName)
                diags.Error("E0706", $"user label named '{n}': this name is reserved for synthetic labels", span);
            index[n] = names.Count;
            names.Add(n);
            return names.Count - 1;
        }
        foreach (var rel in decl.Relations)
        {
            var ids = rel.Chain.Select(n => Intern(n, rel.Span)).ToList();
            for (int k = 0; k + 1 < ids.Count; k++) pairs.Add((ids[k], ids[k + 1], rel.Span));
        }

        int n0 = names.Count;
        var succ = Enumerable.Range(0, n0).Select(_ => new HashSet<int>()).ToArray();
        foreach (var (lo, hi, _) in pairs) succ[lo].Add(hi);

        // Reflexive-transitive closure; a label that reaches itself through ⊏ is on a cycle (E0701).
        var reach = new bool[n0][];
        for (int x = 0; x < n0; x++)
        {
            reach[x] = new bool[n0];
            var stack = new Stack<int>(succ[x]);
            while (stack.Count > 0)
            {
                int y = stack.Pop();
                if (reach[x][y]) continue;
                reach[x][y] = true;
                foreach (var z in succ[y]) stack.Push(z);
            }
        }
        var onCycle = Enumerable.Range(0, n0).Where(x => reach[x][x]).ToList();
        if (onCycle.Count > 0)
        {
            var span = pairs.First(p => onCycle.Contains(p.Lo) && onCycle.Contains(p.Hi)).Span;
            diags.Error("E0701", $"cycle in label order involving {string.Join(", ", onCycle.Select(x => names[x]))}", span);
            // Keep the names resolvable; the order itself is meaningless, and label analysis won't run.
            for (int x = 0; x < n0; x++) reach[x][x] = true;
            return FromOrder(names, Enumerable.Repeat(false, n0).ToList(), Enumerable.Range(0, n0).ToList(), n0, reach, 0, 0, false, decl.IsStrict, diags, decl.Span, checkJoins: false);
        }
        for (int x = 0; x < n0; x++) reach[x][x] = true;

        // Compartments: connected components of the declared pairs, taken as undirected edges.
        var comp = Enumerable.Range(0, n0).ToArray();
        int Find(int x) => comp[x] == x ? x : comp[x] = Find(comp[x]);
        foreach (var (lo, hi, _) in pairs) comp[Find(lo)] = Find(hi);
        var roots = Enumerable.Range(0, n0).Select(Find).Distinct().ToList();
        var compOf = Enumerable.Range(0, n0).Select(x => roots.IndexOf(Find(x))).ToList();
        int compCount = roots.Count;

        var labelNames = new List<string>(names);
        var synthetic = Enumerable.Repeat(false, n0).ToList();
        var leq = new List<bool[]>(reach);
        int AddLabel(string name, int compartment)
        {
            int id = labelNames.Count;
            labelNames.Add(name);
            synthetic.Add(true);
            compOf.Add(compartment);
            for (int r = 0; r < leq.Count; r++) { var a = leq[r]; Array.Resize(ref a, id + 1); leq[r] = a; }
            var own = new bool[id + 1];
            own[id] = true;
            leq.Add(own);
            return id;
        }

        // 1. Compartment tops, for compartments with no unique greatest element that lack some join.
        for (int c = 0; c < compCount; c++)
        {
            var members = Enumerable.Range(0, n0).Where(x => compOf[x] == c).ToList();
            var maximal = members.Where(x => !members.Any(y => y != x && reach[x][y])).ToList();
            if (maximal.Count <= 1) continue;
            bool missingJoin = members.Any(a => members.Any(b => LeastUpperBound(members, reach, a, b) < 0));
            if (!missingJoin) continue;
            var minimal = members.Where(x => !members.Any(y => y != x && reach[y][x])).Select(x => names[x]).OrderBy(s => s, StringComparer.Ordinal).First();
            int t = AddLabel($"top({minimal})", c);
            foreach (var x in members) leq[x][t] = true;
        }

        // 2. Shared bottom, if the whole order has no unique least element.
        int count = labelNames.Count;
        var least = Enumerable.Range(0, count).Where(x => Enumerable.Range(0, count).All(y => leq[x][y])).ToList();
        int bottom;
        if (least.Count == 1) bottom = least[0];
        else
        {
            bottom = AddLabel(BottomName, -1);
            for (int x = 0; x < labelNames.Count; x++) leq[bottom][x] = true;
        }

        // 3. Global top, if there is more than one compartment. No data may carry it (forbidden top).
        count = labelNames.Count;
        var greatest = Enumerable.Range(0, count).Where(x => Enumerable.Range(0, count).All(y => leq[y][x])).ToList();
        bool forbidden = false;
        int top;
        if (compCount > 1)
        {
            top = AddLabel(GlobalTopName, -1);
            for (int x = 0; x < labelNames.Count; x++) leq[x][top] = true;
            forbidden = true;
        }
        else if (greatest.Count == 1) top = greatest[0];
        else
        {
            // An empty `labels {}` block: the only label is the synthetic bottom.
            top = bottom;
        }

        return FromOrder(labelNames, synthetic, compOf, compCount, leq.ToArray(), bottom, top, forbidden, decl.IsStrict, diags, decl.Span, checkJoins: true);
    }

    private static int LeastUpperBound(IReadOnlyList<int> universe, bool[][] leq, int a, int b)
    {
        var ubs = universe.Where(u => leq[a][u] && leq[b][u]).ToList();
        var least = ubs.Where(u => ubs.All(v => leq[u][v])).ToList();
        return least.Count == 1 ? least[0] : -1;
    }

    private static LabelLattice FromOrder(List<string> labels, List<bool> synthetic, List<int> compartments, int compCount,
        bool[][] leq, int bottom, int top, bool forbidden, bool strict, DiagnosticBag diags, SourceSpan span, bool checkJoins)
    {
        int n = labels.Count;
        var all = Enumerable.Range(0, n).ToList();
        var join = new int[n][];
        var meet = new int[n][];
        for (int a = 0; a < n; a++)
        {
            join[a] = new int[n];
            meet[a] = new int[n];
            for (int b = 0; b < n; b++)
            {
                join[a][b] = LeastUpperBound(all, leq, a, b);
                var lbs = all.Where(u => leq[u][a] && leq[u][b]).ToList();
                var greatest = lbs.Where(u => lbs.All(v => leq[v][u])).ToList();
                meet[a][b] = greatest.Count == 1 ? greatest[0] : bottom;
            }
        }

        // 4. Every pair must have a least upper bound (E0703). The compiler adds no labels for this.
        if (checkJoins)
        {
            for (int a = 0; a < n; a++)
                for (int b = a + 1; b < n; b++)
                {
                    if (join[a][b] >= 0) continue;
                    var ubs = all.Where(u => leq[a][u] && leq[b][u]).ToList();
                    var minimal = ubs.Where(u => !ubs.Any(v => v != u && leq[v][u])).Select(u => labels[u]);
                    diags.Error("E0703", $"labels '{labels[a]}' and '{labels[b]}' have no least upper bound (minimal upper bounds: {string.Join(", ", minimal)})",
                        span, $"help: declare a label above '{labels[a]}' and '{labels[b]}' and below each of their upper bounds");
                }
        }

        return new LabelLattice(labels, synthetic, compartments, compCount,
            leq.Select(r => (IReadOnlyList<bool>)r.ToArray()).ToList(),
            join.Select(r => (IReadOnlyList<int>)r).ToList(),
            meet.Select(r => (IReadOnlyList<int>)r).ToList(),
            bottom, top, forbidden, strict, declared: true);
    }
}
