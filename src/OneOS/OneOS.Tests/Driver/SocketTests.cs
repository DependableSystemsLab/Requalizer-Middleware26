using OneOS.Runtime;
using OneOS.Runtime.Language;
using OneOS.Runtime.Scheduling;
using OneOS.Tests.Language;
using OneOS.Tests.Scheduling;

namespace OneOS.Tests.Driver;

// Cluster-wide sockets (plan step 8): detection in JavaScript programs, Registry claims, and DIFT marking.
public class SocketTests
{
    [Fact]
    public void JavaScriptListensAreFound()
    {
        Assert.Equal(new[] { "socket:7070" }, JavaScriptSockets.Find("const s = net.createServer(h);\ns.listen(7070, () => {});"));
        Assert.Equal(new[] { "socket:*" }, JavaScriptSockets.Find("app.listen(process.env.PORT)"));
        Assert.Equal(new[] { "socket:80", "socket:*" }, JavaScriptSockets.Find("a.listen( 80 ); b.listen(port); c.listen(80)"));
        Assert.Empty(JavaScriptSockets.Find("server['listen'](7171)"));
        Assert.True(JavaScriptSockets.Allows(new[] { "socket:*" }, 1234));
        Assert.True(JavaScriptSockets.Allows(new[] { "socket:7070" }, 7070));
        Assert.False(JavaScriptSockets.Allows(new[] { "socket:7070", "file:/x" }, 8080));
        Assert.Equal("app.js", JavaScriptSockets.Script(new[] { "node", "--inspect", "app.js", "5" }));
        Assert.Null(JavaScriptSockets.Script(new[] { "python3", "app.py" }));
    }

    [Fact]
    public void ClaimsAreExclusiveAndFollowTheirOwner()
    {
        var registry = new Registry();
        SocketInfo Claim(string owner, string host) => new() { Owner = owner, Port = 7070, HostRuntime = host };
        new ClaimSocketAction { Info = Claim("a/server", "test1") }.ApplyTo(registry);
        new ClaimSocketAction { Info = Claim("b/other", "test2") }.ApplyTo(registry);         // taken: nothing changes
        Assert.Equal(("a/server", "test1"), (registry.Sockets[7070].Owner, registry.Sockets[7070].HostRuntime));

        new ClaimSocketAction { Info = Claim("a/server", "test0") }.ApplyTo(registry);         // the owner, restarted elsewhere
        Assert.Equal("test0", registry.Sockets[7070].HostRuntime);
        new ReleaseSocketAction { Port = 7070, Owner = "a/server", HostRuntime = "test1" }.ApplyTo(registry);   // a late release by the old incarnation
        Assert.True(registry.Sockets.ContainsKey(7070));
        new ReleaseSocketAction { Port = 7070, Owner = "b/other", HostRuntime = "test0" }.ApplyTo(registry);    // not the owner
        Assert.True(registry.Sockets.ContainsKey(7070));
        new ReleaseSocketAction { Port = 7070, Owner = "a/server", HostRuntime = "test0" }.ApplyTo(registry);
        Assert.False(registry.Sockets.ContainsKey(7070));

        new ClaimSocketAction { Info = Claim("a/server", "test1") }.ApplyTo(registry);
        new DeleteAgentAction { Uri = "a/server" }.ApplyTo(registry);                          // the agent's sockets go with it
        Assert.Empty(registry.Sockets);
    }

    private const string Server = """
        labels { public < secret; }
        type m { v: string }
        graph web () {
          topology {
            node server () => (out: m) = process('node', 'server.js');
            node log (in: m) = process('python3', 'log.py');
            edge server --> log;
          }
        }
        """;

    private static HostRuntimeInfo WithIdm(HostRuntimeInfo h) => h with { IdmSupport = new[] { "node" } };

    [Fact]
    public void AListeningNodeHasExternalInputAndRunsWithTheIdm()
    {
        var g = Sched.Graph(Server);
        var options = Sched.Options with
        {
            ProgramSockets = (node, argv) => node.Name == "server" ? new[] { "socket:7070" } : Array.Empty<string>(),
            DiftPolicy = new InMemoryDift().Source("server", "socket:7070", ExternalLabel.Static("public")),
        };
        var plan = Sched.Plan(g, Sched.Cluster(WithIdm(Sched.Host("h1", "secret"))), options: options);
        var server = plan.Agents.Single(a => a.NodeName == "server");
        Assert.Equal(DiftMode.Enabled, server.Dift);
        Assert.Contains("network socket", server.DiftRationale);
        Assert.Equal(new[] { "socket:7070" }, server.Idm!.Sources.Select(s => s.Location));
        Assert.Equal(DiftMode.Disabled, plan.Agents.Single(a => a.NodeName == "log").Dift);

        // Without IDM support for node, no host qualifies; without a label for the socket, SP011.
        Assert.Equal("SP004", Sched.Fails(g, Sched.Cluster(Sched.Host("h1", "secret")), options: options).Code);
        Assert.Equal("SP011", Sched.Fails(g, Sched.Cluster(WithIdm(Sched.Host("h1", "secret"))), options: options with { DiftPolicy = null }).Code);
    }
}
