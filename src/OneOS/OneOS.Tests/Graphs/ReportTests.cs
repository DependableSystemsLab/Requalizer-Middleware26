using OneOS.Runtime.Graphs;
using OneOS.Tests.Scheduling;

namespace OneOS.Tests.Graphs;

// The plan report (`oneos sim`, shell `graph plan`).
public class ReportTests
{
    [Fact]
    public void UncheckedPipesAndOrderingDetailsAreShown()
    {
        var g = Sched.Graph("""
            labels { public < secret; }
            key k: string;
            clock t: u64 unit ms;
            type m { id: k, ts: t }
            graph g () {
              topology {
                node s () => (o: m ordered within 50ms) = process('node', 's.js');
                node n[k] (i: m ordered per id) => (o: m) = process('node', 'n.js');
                node p (i: m) = process('node', 'p.js');
                edge e1: s --> n;
                edge e2: n --> p;
                flow f = { e2 };
              }
              policy {
                max_buffer(16MB): n[*].i;
                label(secret): s.o;
                label(public): p.i;
                unchecked('alerts'): f;
              }
            }
            """);
        var info = Sched.Plan(g, Sched.Cluster(Sched.Host("h1", "secret"), Sched.Host("h2", "secret")));
        var report = GraphReport.Format(info, g);
        Assert.Contains("ordered by ts per id, lateness 50ms, max_buffer 16MB", report);
        var e2 = report.Split('\n').SkipWhile(l => !l.StartsWith("  e2:")).Skip(1).First();
        Assert.Contains("checks skipped (unchecked)", e2);
    }

    [Fact]
    public void CompartmentBoundAgentsShowTheirAlternativesInBothSections()
    {
        var g = Sched.Graph("""
            labels { a_lo < a_hi; b_lo < b_hi; }
            key t: string;
            type m { k: t }
            graph g () {
              topology {
                node s () => (o: m) = process('node', 's.js');
                node n[t] (i: m) = process('node', 'n.js');
                edge s --> n;
              }
              policy { partitions(2): n; label((x: m) => x.k == 'a' ? a_hi : b_hi): s.o; }
            }
            """);
        var info = Sched.Plan(g, Sched.Cluster(Sched.Host("ha", "a_hi"), Sched.Host("hb", "b_hi")));
        var placement = GraphReport.Format(info, g, sections: GraphReport.Sections.Placement);
        Assert.Contains("one of a_hi/b_hi", placement);
        Assert.DoesNotContain("⊒ bottom", placement);
    }
}
