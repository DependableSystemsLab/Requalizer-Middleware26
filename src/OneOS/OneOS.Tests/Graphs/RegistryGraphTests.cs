using MessagePack;
using OneOS.Runtime;

namespace OneOS.Tests.Graphs;

// The Registry side of phase 6 (S§9.2): conditional commits, commit epochs, agent states, deletion.
public class RegistryGraphTests
{
    // Hosts aren't Registry state (they come from cluster-info exchanges); commits are conditional on
    // each host's commit epoch only.
    private static Registry WithHosts() => new Registry();

    private static GraphInstanceRecord Record(string id, Registry r, params string[] hosts) => new()
    {
        Id = id, GraphName = "g", PlanJson = "{}", GraphJson = "{}",
        Committed = hosts.ToDictionary(h => h, _ => new long[] { 500, 1L << 28 }),
        ExpectedHostVersions = hosts.ToDictionary(h => h, r.HostVersion),
    };

    [Fact]
    public void ConcurrentCommitsOnTheSameHostConflict()
    {
        var r = WithHosts();
        // Both spawns planned against the same snapshot.
        var a = Record("g-a", r, "h1");
        var b = Record("g-b", r, "h1", "h2");
        r.Apply(new CommitGraphAction { Record = a });
        r.Apply(new CommitGraphAction { Record = b });
        Assert.True(r.Graphs.ContainsKey("g-a"));
        Assert.False(r.Graphs.ContainsKey("g-b"));
        Assert.Contains("h1", r.GraphCommitRejections["g-b"]);

        // Replanned against the new view, the second commit goes through; a plan that doesn't touch h1 never conflicted.
        r.Apply(new CommitGraphAction { Record = Record("g-b2", r, "h1", "h2") });
        Assert.True(r.Graphs.ContainsKey("g-b2"));
        var c = Record("g-c", r, "h2");
        r.Apply(new CommitGraphAction { Record = Record("g-d", r, "h1") });
        r.Apply(new CommitGraphAction { Record = c });
        Assert.True(r.Graphs.ContainsKey("g-c"));
    }

    // A commit that doesn't grow a host's reservation leaves its epoch alone.
    [Fact]
    public void OnlyReservationGrowthBumpsTheEpoch()
    {
        var r = WithHosts();
        r.Apply(new CommitGraphAction { Record = Record("g-a", r, "h1") });
        Assert.Equal(1, r.HostVersion("h1"));
        Assert.Equal(0, r.HostVersion("h2"));
        r.Apply(new SetGraphStateAction { GraphId = "g-a", State = "Running" });
        Assert.Equal(1, r.HostVersion("h1"));
    }

    // Step 5.4: graph agents and pipes are Registry entries, applied with the commit, replaced by plan
    // updates, and removed with the instance.
    [Fact]
    public void GraphAgentsAndPipesAreRegistryEntries()
    {
        var r = WithHosts();
        AgentInfo Agent(string id, string host) => new() { URI = $"alice.ubc/graphs/{id}", GPID = 12345, Runtime = host, Graph = "g-a", BinaryPath = "python3", Arguments = new() { "w.py" } };
        PipeInfo Pipe(string id, string from, string to) => new() { Id = id, Graph = "g-a", Sources = new() { $"alice.ubc/graphs/{from}" }, Sinks = new() { $"alice.ubc/graphs/{to}" } };
        r.Agents["alice.ubc/agents/shell"] = new AgentInfo { URI = "alice.ubc/agents/shell", Graph = "" };   // not part of the graph

        RegistryAction commit = new CommitGraphAction
        {
            Record = Record("g-a", r, "h1"),
            Agents = new() { Agent("g-a/s/0", "h1"), Agent("g-a/k/0", "h1") },
            Pipes = new() { Pipe("p0", "g-a/s/0", "g-a/k/0") },
        };
        r.Apply(MessagePackSerializer.Deserialize<RegistryAction>(MessagePackSerializer.Serialize(commit)));
        Assert.Equal(new[] { "w.py" }, r.Agents["alice.ubc/graphs/g-a/s/0"].Arguments);
        Assert.Equal("alice.ubc/graphs/g-a/k/0", r.Pipes["p0"].Sinks.Single());

        // A rejected commit adds no entries.
        var stale = new CommitGraphAction { Record = Record("g-b", WithHosts(), "h1"), Agents = new() { Agent("g-b/s/0", "h1") } };
        r.Apply(stale);
        Assert.DoesNotContain("alice.ubc/graphs/g-b/s/0", r.Agents.Keys);

        // A plan update replaces the instance's entries.
        r.Apply(new UpdateGraphPlanAction
        {
            GraphId = "g-a", BasePlanVersion = 1, PlanJson = "{}", Committed = new() { ["h1"] = new long[] { 500, 1L << 28 } },
            Agents = new() { Agent("g-a/s/0", "h2") }, Pipes = new(),
        });
        Assert.Equal("h2", r.Agents["alice.ubc/graphs/g-a/s/0"].Runtime);
        Assert.DoesNotContain("alice.ubc/graphs/g-a/k/0", r.Agents.Keys);
        Assert.Empty(r.Pipes);

        r.Apply(new DeleteGraphAction { GraphId = "g-a" });
        Assert.Equal(new[] { "alice.ubc/agents/shell" }, r.Agents.Keys);
    }

    [Fact]
    public void AgentStatesStateAndDeletion()
    {
        var r = WithHosts();
        r.Apply(new CommitGraphAction { Record = Record("g-a", r, "h1") });
        r.Apply(new SetGraphAgentStateAction { GraphId = "g-a", AgentId = "g-a/n/0", State = "Running" });
        r.Apply(new SetGraphStateAction { GraphId = "g-a", State = "Running" });
        Assert.Equal("Running", r.Graphs["g-a"].AgentStates["g-a/n/0"]);
        Assert.Equal("Running", r.Graphs["g-a"].State);
        Assert.Equal(3, r.Graphs["g-a"].Version);
        r.Apply(new DeleteGraphAction { GraphId = "g-a" });
        Assert.Empty(r.Graphs);
    }

    [Fact]
    public void ActionsAndRegistryRoundTripThroughMessagePack()
    {
        var r = WithHosts();
        RegistryAction commit = new CommitGraphAction { Record = Record("g-a", r, "h1") };
        var back = (CommitGraphAction)MessagePackSerializer.Deserialize<RegistryAction>(MessagePackSerializer.Serialize(commit));
        Assert.Equal(500, back.Record.Committed["h1"][0]);
        r.Apply(back);
        foreach (RegistryAction a in new RegistryAction[]
        {
            new SetGraphAgentStateAction { GraphId = "g-a", AgentId = "x", State = "Running" },
            new SetGraphStateAction { GraphId = "g-a", State = "Stopping", Error = "e" },
        })
            r.Apply(MessagePackSerializer.Deserialize<RegistryAction>(MessagePackSerializer.Serialize(a)));
        var rb = MessagePackSerializer.Deserialize<Registry>(MessagePackSerializer.Serialize(r));
        Assert.Equal("Stopping", rb.Graphs["g-a"].State);
        Assert.Equal("Running", rb.Graphs["g-a"].AgentStates["x"]);
        Assert.Equal(1, rb.HostEpochs["h1"]);
        Assert.Equal(r.HostVersion("h1"), rb.HostVersion("h1"));
    }
}
