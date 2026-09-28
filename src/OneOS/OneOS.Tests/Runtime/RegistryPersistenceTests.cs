using System.Reflection;
using MessagePack;
using OneOS.Runtime;

namespace OneOS.Tests.Persistence;

// The Registry persists through its Raft log alone: snapshots carry the whole Registry.
public class RegistryPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oneos-raft-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static async Task Commit(RegistryPersistentState log, RegistryAction action) =>
        await log.CommitAsync(await log.AppendAsync(new Synchronizer.ActionLogEntry(MessagePackSerializer.Serialize(action), 0)));

    private static byte[] Bytes(Registry r) => MessagePackSerializer.Serialize(r);

    private static async Task<Registry> Open(string dir, Func<RegistryPersistentState, Task>? use = null, bool compactAtStop = false)
    {
        var registry = new Registry();
        using var log = new RegistryPersistentState(dir, registry);
        await log.InitializeAsync();
        if (use != null) await use(log);
        if (compactAtStop) await log.ForceCompactionAsync(long.MaxValue, CancellationToken.None);
        return registry;
    }

    [Fact]
    public async Task StateSurvivesRestartsAcrossAutomaticAndForcedCompaction()
    {
        var expected = new Registry();
        async Task Write(RegistryPersistentState log, RegistryAction a) { expected.Apply(a); await Commit(log, a); }

        // 250 commits span several 100-entry partitions, so commits compact automatically.
        var live = await Open(_dir, async log =>
        {
            for (int i = 0; i < 250; i++)
            {
                await Write(log, new SetAgentAction { Uri = $"u.ubc/agents/{i}", Info = new AgentInfo { URI = $"u.ubc/agents/{i}", GPID = 10000 + i, Runtime = "test0" } });
                if (i % 10 == 9) await Write(log, new DeleteAgentAction { Uri = $"u.ubc/agents/{i - 5}" });
            }
            await Write(log, new SetUserAction { Username = "alice", Password = "pw" });
        });
        Assert.Equal(Bytes(expected), Bytes(live));

        var restored = await Open(_dir, compactAtStop: true);
        Assert.Equal(Bytes(expected), Bytes(restored));
        Assert.Equal("pw", restored.Users["alice"]);
        Assert.Equal(225, restored.Agents.Count);

        // After compacting everything at stop (as Synchronizer.StopAsync does), the snapshot alone restores it.
        var again = await Open(_dir);
        Assert.Equal(Bytes(expected), Bytes(again));
    }

    // The reported case: a file downloaded with wget into the home directory survives a restart.
    [Fact]
    public async Task NestedFileSystemNodesSurviveRestart()
    {
        var file = new FileNode { Size = 42, Checksum = "abc", Copies = new() { ["test0"] = "/data/x" } };
        await Open(_dir, log => Commit(log, new SetFileSystemNodeAction { Path = "/home/root/timer.js", Node = file }));
        var replayed = await Open(_dir, compactAtStop: true);
        Assert.IsType<FileNode>(replayed.GetNode("/home/root/timer.js"));
        var fromSnapshot = await Open(_dir);
        var restored = Assert.IsType<FileNode>(fromSnapshot.GetNode("/home/root/timer.js"));
        Assert.Equal(42, restored.Size);
    }

    // Counters (graph record versions, host commit epochs) must come back as they were, not applied twice.
    [Fact]
    public async Task RestoreDoesNotApplyEntriesTwice()
    {
        var record = new GraphInstanceRecord { Id = "g", Committed = new() { ["h1"] = new long[] { 500, 1 } }, ExpectedHostVersions = new() { ["h1"] = 0 } };
        await Open(_dir, async log =>
        {
            await Commit(log, new CommitGraphAction { Record = record });
            await Commit(log, new SetGraphAgentStateAction { GraphId = "g", AgentId = "g/a/0", State = "Running" });
        });
        var restored = await Open(_dir);
        Assert.Equal(1, restored.HostEpochs["h1"]);
        Assert.Equal(2, restored.Graphs["g"].Version);
        Assert.Equal("Running", restored.Graphs["g"].AgentStates["g/a/0"]);
    }

    // A snapshot install swaps the state into the existing instance; every serialized property must follow.
    [Fact]
    public void ReplaceWithCoversEveryRegistryProperty()
    {
        var keyed = typeof(Registry).GetProperties().Where(p => p.GetCustomAttribute<KeyAttribute>() != null).ToList();
        var source = new Registry();
        var target = new Registry();
        target.ReplaceWith(source);
        foreach (var p in keyed)
            Assert.True(ReferenceEquals(p.GetValue(source), p.GetValue(target)), $"Registry.ReplaceWith does not copy {p.Name}");
    }
}
