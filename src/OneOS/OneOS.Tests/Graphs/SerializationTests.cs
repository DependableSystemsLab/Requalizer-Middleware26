using OneOS.Runtime.Graphs;
using OneOS.Runtime.Language;
using OneOS.Tests.Language;
using OneOS.Tests.Scheduling;

namespace OneOS.Tests.Graphs;

public class SerializationTests
{
    [Theory]
    [InlineData("camera.osh", "foo", "a|b")]
    [InlineData("alerts.osh", "emergency", "")]
    [InlineData("tenants.osh", "tenants", "")]
    [InlineData("jpeg.osh", "jpeg_pipeline", "")]
    public void BoundGraphAndPlanRoundTrip(string file, string graph, string args)
    {
        var argv = args.Length == 0 ? Array.Empty<object?>() : args.Split('|').Cast<object?>().ToArray();
        var (bound, _) = GraphBinder.Bind(TestUtil.CompileExample(file).Graph(graph)!, argv);
        var json = GraphSerialization.Serialize(bound!);
        var back = GraphSerialization.DeserializeGraph(json);
        Assert.Equal(json, GraphSerialization.Serialize(back));
        Assert.Equal(bound!.Args, back.Args);
        Assert.Equal(bound.Lattice.Labels, back.Lattice.Labels);

        var cluster = Sched.Cluster(Sched.Host("h1", "secret"), Sched.Host("h2", "secret"), Sched.Host("a", "a_internal", cpu: 8000), Sched.Host("b", "b_internal", cpu: 8000));
        var plan = Sched.Plan(bound, cluster);
        var pjson = GraphSerialization.Serialize(plan);
        var pback = GraphSerialization.DeserializeInstance(pjson);
        Assert.Equal(pjson, GraphSerialization.Serialize(pback));
        Assert.Equal(plan.Args, pback.Args);
    }
}
