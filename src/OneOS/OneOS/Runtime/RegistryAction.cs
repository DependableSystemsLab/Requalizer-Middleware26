using System.Linq;
using System;
using MessagePack;
using OneOS.Runtime;

namespace OneOS.Runtime
{
    [MessagePackObject]
    [Union(0, typeof(SetUserAction))]
    [Union(1, typeof(DeleteUserAction))]
    [Union(2, typeof(SetAgentAction))]
    [Union(3, typeof(DeleteAgentAction))]
    [Union(4, typeof(SetSessionAction))]
    [Union(5, typeof(DeleteSessionAction))]
    [Union(6, typeof(SetFileSystemNodeAction))]
    [Union(7, typeof(DeleteFileSystemNodeAction))]
    [Union(8, typeof(MoveFileSystemNodeAction))]
    [Union(9, typeof(AddTopicSubscriptionAction))]
    [Union(10, typeof(RemoveTopicSubscriptionAction))]
    [Union(11, typeof(TransactionAction))]
    [Union(12, typeof(SetPipeAction))]
    [Union(13, typeof(DeletePipeAction))]
    [Union(14, typeof(CommitGraphAction))]
    [Union(15, typeof(SetGraphAgentStateAction))]
    [Union(16, typeof(SetGraphStateAction))]
    [Union(17, typeof(DeleteGraphAction))]
    [Union(18, typeof(UpdateGraphPlanAction))]
    [Union(19, typeof(ReportGraphMetricsAction))]
    [Union(20, typeof(ReportGraphTaintAction))]
    [Union(21, typeof(ClaimSocketAction))]
    [Union(22, typeof(ReleaseSocketAction))]
    [Union(23, typeof(SetIODeviceAction))]
    [Union(24, typeof(DeleteIODeviceAction))]
    public abstract record RegistryAction
    {
        public abstract void ApplyTo(Registry registry);
    }

    [MessagePackObject]
    public record TransactionAction : RegistryAction
    {
        [Key(0)] public List<RegistryAction> Actions { get; set; } = new List<RegistryAction>();

        public override void ApplyTo(Registry registry)
        {
            foreach (var action in Actions)
            {
                action.ApplyTo(registry);
            }
        }
    }

    [MessagePackObject]
    public record SetPipeAction : RegistryAction
    {
        [Key(0)] public PipeInfo Info { get; set; } = new PipeInfo();

        public override void ApplyTo(Registry registry)
        {
            registry.Pipes[Info.Id] = Info;
        }
    }

    [MessagePackObject]
    public record DeletePipeAction : RegistryAction
    {
        [Key(0)] public string Id { get; set; } = string.Empty;

        public override void ApplyTo(Registry registry)
        {
            registry.Pipes.Remove(Id);
        }
    }

    [MessagePackObject]
    public record AddTopicSubscriptionAction : RegistryAction
    {
        [Key(0)] public string Topic { get; set; } = string.Empty;
        [Key(1)] public string AgentUri { get; set; } = string.Empty;

        public override void ApplyTo(Registry registry)
        {
            if (!registry.TopicSubscribers.TryGetValue(Topic, out var subs))
            {
                subs = new HashSet<string>();
                registry.TopicSubscribers[Topic] = subs;
            }
            subs.Add(AgentUri);
        }
    }

    [MessagePackObject]
    public record RemoveTopicSubscriptionAction : RegistryAction
    {
        [Key(0)] public string Topic { get; set; } = string.Empty;
        [Key(1)] public string AgentUri { get; set; } = string.Empty;

        public override void ApplyTo(Registry registry)
        {
            if (registry.TopicSubscribers.TryGetValue(Topic, out var subs))
            {
                subs.Remove(AgentUri);
                if (subs.Count == 0)
                {
                    registry.TopicSubscribers.Remove(Topic);
                }
            }
        }
    }

    [MessagePackObject]
    public record SetUserAction : RegistryAction
    {
        [Key(0)] public string Username { get; set; } = string.Empty;
        [Key(1)] public string Password { get; set; } = string.Empty;

        public override void ApplyTo(Registry registry)
        {
            registry.Users[Username] = Password;
        }
    }

    [MessagePackObject]
    public record DeleteUserAction : RegistryAction
    {
        [Key(0)] public string Username { get; set; } = string.Empty;

        public override void ApplyTo(Registry registry)
        {
            registry.Users.Remove(Username);
        }
    }

    // Commits a planned graph instance, conditional on the host versions (commit epochs) it was planned against
    // (scheduling-spec §9.2). A conflict is recorded in GraphCommitRejections instead.
    [MessagePackObject]
    public record CommitGraphAction : RegistryAction
    {
        [Key(0)] public GraphInstanceRecord Record { get; set; } = new GraphInstanceRecord();
        // The instance's primaries and pipes as first-class Registry entries (`ps`, `kill`, routing),
        // applied atomically with the record, and only if the commit is accepted.
        [Key(1)] public List<AgentInfo> Agents { get; set; } = new List<AgentInfo>();
        [Key(2)] public List<PipeInfo> Pipes { get; set; } = new List<PipeInfo>();

        public override void ApplyTo(Registry registry)
        {
            if (registry.Graphs.ContainsKey(Record.Id)) return;
            foreach (var (host, expected) in Record.ExpectedHostVersions)
            {
                if (registry.HostVersion(host) != expected)
                {
                    registry.GraphCommitRejections[Record.Id] = $"host {host} changed since the plan's snapshot";
                    return;
                }
            }
            foreach (var host in Record.Committed.Keys)
                registry.HostEpochs[host] = (registry.HostEpochs.TryGetValue(host, out var e) ? e : 0) + 1;
            Record.Version = 1;
            registry.Graphs[Record.Id] = Record;
            GraphEntries.Replace(registry, Record.Id, Agents, Pipes);
        }
    }

    // The Registry agent and pipe entries of one graph instance.
    internal static class GraphEntries
    {
        public static void Replace(Registry registry, string graphId, List<AgentInfo> agents, List<PipeInfo> pipes)
        {
            Remove(registry, graphId);
            foreach (var a in agents) registry.Agents[a.URI] = a;
            foreach (var p in pipes) registry.Pipes[p.Id] = p;
        }

        public static void Remove(Registry registry, string graphId)
        {
            foreach (var uri in registry.Agents.Where(kv => kv.Value.Graph == graphId).Select(kv => kv.Key).ToList()) registry.Agents.Remove(uri);
            foreach (var id in registry.Pipes.Where(kv => kv.Value.Graph == graphId).Select(kv => kv.Key).ToList()) registry.Pipes.Remove(id);
        }
    }

    [MessagePackObject]
    public record SetGraphAgentStateAction : RegistryAction
    {
        [Key(0)] public string GraphId { get; set; } = string.Empty;
        [Key(1)] public string AgentId { get; set; } = string.Empty;
        [Key(2)] public string State { get; set; } = string.Empty;

        public override void ApplyTo(Registry registry)
        {
            if (!registry.Graphs.TryGetValue(GraphId, out var g)) return;
            g.AgentStates[AgentId] = State;
            g.Version++;
        }
    }

    [MessagePackObject]
    public record SetGraphStateAction : RegistryAction
    {
        [Key(0)] public string GraphId { get; set; } = string.Empty;
        [Key(1)] public string State { get; set; } = string.Empty;
        [Key(2)] public string? Error { get; set; }

        public override void ApplyTo(Registry registry)
        {
            if (!registry.Graphs.TryGetValue(GraphId, out var g)) return;
            g.State = State;
            if (Error != null) g.Error = Error;
            g.Version++;
        }
    }

    // Replaces a running instance's plan (failure handling, scaling; scheduling-spec §10.4–§10.5).
    // Conditional on the versions of the hosts whose reservation grows, and on the plan version it
    // was derived from; a conflict is recorded under "<id>@v<version>".
    [MessagePackObject]
    public record UpdateGraphPlanAction : RegistryAction
    {
        [Key(0)] public string GraphId { get; set; } = string.Empty;
        [Key(1)] public long BasePlanVersion { get; set; }
        [Key(2)] public string PlanJson { get; set; } = string.Empty;
        [Key(3)] public Dictionary<string, long[]> Committed { get; set; } = new Dictionary<string, long[]>();
        [Key(4)] public Dictionary<string, long> ExpectedHostVersions { get; set; } = new Dictionary<string, long>();
        [Key(5)] public List<string> ResetAgents { get; set; } = new List<string>();
        [Key(6)] public List<string> RemovedAgents { get; set; } = new List<string>();
        // The new plan's Registry entries; they replace the instance's current ones when the update applies.
        [Key(7)] public List<AgentInfo> Agents { get; set; } = new List<AgentInfo>();
        [Key(8)] public List<PipeInfo> Pipes { get; set; } = new List<PipeInfo>();

        [IgnoreMember] public string ConflictKey => $"{GraphId}@v{BasePlanVersion + 1}";

        public override void ApplyTo(Registry registry)
        {
            if (!registry.Graphs.TryGetValue(GraphId, out var g)) return;
            if (g.PlanVersion != BasePlanVersion)
            {
                registry.GraphCommitRejections[ConflictKey] = $"the plan changed (now v{g.PlanVersion})";
                return;
            }
            foreach (var (host, expected) in ExpectedHostVersions)
            {
                if (registry.HostVersion(host) != expected)
                {
                    registry.GraphCommitRejections[ConflictKey] = $"host {host} changed since the plan's snapshot";
                    return;
                }
            }
            foreach (var (host, c) in Committed)
            {
                var before = g.Committed.TryGetValue(host, out var b) ? b : new long[2];
                if (c[0] > before[0] || c[1] > before[1])
                    registry.HostEpochs[host] = (registry.HostEpochs.TryGetValue(host, out var e) ? e : 0) + 1;
            }
            g.PlanJson = PlanJson;
            g.Committed = Committed;
            g.PlanVersion++;
            GraphEntries.Replace(registry, GraphId, Agents, Pipes);
            foreach (var a in ResetAgents) g.AgentStates[a] = "Pending";
            foreach (var a in RemovedAgents) { g.AgentStates.Remove(a); g.Metrics.Remove(a); }
            g.Version++;
        }
    }

    // A stateful agent's taint rose. Reports can arrive out of order, so a lower rank never overwrites.
    [MessagePackObject]
    public record ReportGraphTaintAction : RegistryAction
    {
        [Key(0)] public string GraphId { get; set; } = string.Empty;
        [Key(1)] public string AgentId { get; set; } = string.Empty;
        [Key(2)] public string Taint { get; set; } = string.Empty;
        [Key(3)] public int Rank { get; set; }

        public override void ApplyTo(Registry registry)
        {
            if (!registry.Graphs.TryGetValue(GraphId, out var g)) return;
            if (g.TaintRanks.TryGetValue(AgentId, out var r) && r >= Rank) return;
            g.Taints[AgentId] = Taint;
            g.TaintRanks[AgentId] = Rank;
        }
    }

    [MessagePackObject]
    public record ReportGraphMetricsAction : RegistryAction
    {
        [Key(0)] public string GraphId { get; set; } = string.Empty;
        [Key(1)] public Dictionary<string, double[]> Metrics { get; set; } = new Dictionary<string, double[]>();

        public override void ApplyTo(Registry registry)
        {
            if (!registry.Graphs.TryGetValue(GraphId, out var g)) return;
            foreach (var (agent, m) in Metrics) g.Metrics[agent] = m;
        }
    }

    // Removes a graph instance and releases its resources.
    [MessagePackObject]
    public record DeleteGraphAction : RegistryAction
    {
        [Key(0)] public string GraphId { get; set; } = string.Empty;

        public override void ApplyTo(Registry registry)
        {
            registry.Graphs.Remove(GraphId);
            registry.GraphCommitRejections.Remove(GraphId);
            GraphEntries.Remove(registry, GraphId);
        }
    }

    [MessagePackObject]
    public record SetAgentAction : RegistryAction
    {
        [Key(0)] public string Uri { get; set; } = string.Empty;
        [Key(1)] public AgentInfo Info { get; set; } = new AgentInfo();

        public override void ApplyTo(Registry registry)
        {
            registry.Agents[Uri] = Info;
        }
    }

    [MessagePackObject]
    public record DeleteAgentAction : RegistryAction
    {
        [Key(0)] public string Uri { get; set; } = string.Empty;

        public override void ApplyTo(Registry registry)
        {
            registry.Agents.Remove(Uri);
            // The agent's cluster-wide sockets go with it (plan step 8).
            foreach (var port in registry.Sockets.Where(kv => kv.Value.Owner == Uri).Select(kv => kv.Key).ToList()) registry.Sockets.Remove(port);
        }
    }

    // Claims a cluster-wide socket (plan step 8). Applied the same way on every replica: the port goes to the
    // claimant if it is free or already the claimant's (a restarted or failed-over agent reclaims its port,
    // possibly on another runtime); otherwise nothing changes, and the claimant sees another owner.
    [MessagePackObject]
    public record ClaimSocketAction : RegistryAction
    {
        [Key(0)] public SocketInfo Info { get; set; } = new SocketInfo();

        public override void ApplyTo(Registry registry)
        {
            if (registry.Sockets.TryGetValue(Info.Port, out var existing) && existing.Owner != Info.Owner) return;
            registry.Sockets[Info.Port] = Info;
        }
    }

    // An I/O device a runtime exposes to the cluster (IODriver), keyed by the driver's URI.
    [MessagePackObject]
    public record SetIODeviceAction : RegistryAction
    {
        [Key(0)] public string Uri { get; set; } = string.Empty;
        [Key(1)] public IOHandle Handle { get; set; } = new IOHandle();

        public override void ApplyTo(Registry registry) => registry.IO[Uri] = Handle;
    }

    [MessagePackObject]
    public record DeleteIODeviceAction : RegistryAction
    {
        [Key(0)] public string Uri { get; set; } = string.Empty;

        public override void ApplyTo(Registry registry) => registry.IO.Remove(Uri);
    }

    [MessagePackObject]
    public record ReleaseSocketAction : RegistryAction
    {
        [Key(0)] public int Port { get; set; }
        [Key(1)] public string Owner { get; set; } = string.Empty;
        // The runtime the released claim was made from: a late release by an ended incarnation doesn't remove
        // the claim its replacement made on another runtime.
        [Key(2)] public string HostRuntime { get; set; } = string.Empty;

        public override void ApplyTo(Registry registry)
        {
            if (registry.Sockets.TryGetValue(Port, out var existing) && existing.Owner == Owner && existing.HostRuntime == HostRuntime)
                registry.Sockets.Remove(Port);
        }
    }

    [MessagePackObject]
    public record SetSessionAction : RegistryAction
    {
        [Key(0)] public string SessionToken { get; set; } = string.Empty;
        [Key(1)] public SessionInfo Info { get; set; } = new SessionInfo();

        public override void ApplyTo(Registry registry)
        {
            registry.Sessions[SessionToken] = Info;
        }
    }

    [MessagePackObject]
    public record DeleteSessionAction : RegistryAction
    {
        [Key(0)] public string SessionToken { get; set; } = string.Empty;

        public override void ApplyTo(Registry registry)
        {
            registry.Sessions.Remove(SessionToken);
        }
    }

    [MessagePackObject]
    public record SetFileSystemNodeAction : RegistryAction
    {
        [Key(0)] public string Path { get; set; } = string.Empty;
        [Key(1)] public IFileSystemNode Node { get; set; } = default!;

        public override void ApplyTo(Registry registry)
        {
            registry.SetNode(Path, Node);
        }
    }

    [MessagePackObject]
    public record DeleteFileSystemNodeAction : RegistryAction
    {
        [Key(0)] public string Path { get; set; } = string.Empty;

        public override void ApplyTo(Registry registry)
        {
            registry.DeleteNode(Path);
        }
    }

    [MessagePackObject]
    public record MoveFileSystemNodeAction : RegistryAction
    {
        [Key(0)] public string SrcPath { get; set; } = string.Empty;
        [Key(1)] public string DstPath { get; set; } = string.Empty;

        public override void ApplyTo(Registry registry)
        {
            registry.MoveNode(SrcPath, DstPath);
        }
    }
}
