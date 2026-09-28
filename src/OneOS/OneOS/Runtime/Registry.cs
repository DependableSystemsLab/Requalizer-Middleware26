using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MessagePack;

namespace OneOS.Runtime
{
    [MessagePackObject]
    public class Registry
    {
        [Key(0)]
        public Dictionary<string, SessionInfo> Sessions { get; set; } = new Dictionary<string, SessionInfo>();

        [Key(1)]
        public Dictionary<string, string> Users { get; set; } = new Dictionary<string, string>()
        {
            { "root", "Y7TbJWtl6YVB4Tyv6qZIx24ncyBguf9u4Y100Fd1ECbaER+C80qKara3AtkOSic=" }
        };

        [Key(2)]
        public Dictionary<string, string> Kernels { get; set; } = new Dictionary<string, string>();

        [Key(3)]
        public Dictionary<string, AgentInfo> Agents { get; set; } = new Dictionary<string, AgentInfo>();

        [Key(4)]
        public DirectoryNode FileSystem { get; set; }

        [Key(5)]
        public Dictionary<string, PipeInfo> Pipes { get; set; } = new Dictionary<string, PipeInfo>();

        [Key(6)]
        public Dictionary<int, SocketInfo> Sockets { get; set; } = new Dictionary<int, SocketInfo>();

        [Key(7)]
        public Dictionary<string, IOHandle> IO { get; set; } = new Dictionary<string, IOHandle>();

        [Key(8)]
        public Dictionary<string, HashSet<string>> TopicSubscribers { get; set; } = new Dictionary<string, HashSet<string>>();

        // Deployed graph instances (scheduling-spec §9.2), and per-host commit epochs: every commit that
        // reserves resources on a host bumps its epoch, so plans made against an older view conflict.
        [Key(9)]
        public Dictionary<string, GraphInstanceRecord> Graphs { get; set; } = new Dictionary<string, GraphInstanceRecord>();

        [Key(10)]
        public Dictionary<string, long> HostEpochs { get; set; } = new Dictionary<string, long>();

        // Commits refused because a host changed since the plan's snapshot (graph instance id → reason).
        [Key(11)]
        public Dictionary<string, string> GraphCommitRejections { get; set; } = new Dictionary<string, string>();

        // A host's version as the scheduler sees it: its commit epoch, which every reservation growth bumps.
        public long HostVersion(string hostId) => HostEpochs.TryGetValue(hostId, out var e) ? e : 0;

        public Registry()
        {
            FileSystem = new DirectoryNode();
            InitializeFileSystem();
        }

        private void InitializeFileSystem()
        {
            FileSystem.Children["bin"] = new DirectoryNode();
            FileSystem.Children["etc"] = new DirectoryNode();
            FileSystem.Children["tmp"] = new DirectoryNode();
            FileSystem.Children["var"] = new DirectoryNode();
            
            var home = new DirectoryNode();
            var root = new DirectoryNode();
            home.Children["root"] = root;
            root.Children["Desktop"] = new DirectoryNode();
            FileSystem.Children["home"] = home;
        }

        public string ToJsonString()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        }
        
        public void Apply(RegistryAction action)
        {
            action.ApplyTo(this);
        }

        // Takes over another Registry's state (a Raft snapshot), keeping this instance, which the runtime's
        // components hold on to.
        public void ReplaceWith(Registry other)
        {
            Sessions = other.Sessions;
            Users = other.Users;
            Kernels = other.Kernels;
            Agents = other.Agents;
            FileSystem = other.FileSystem;
            Pipes = other.Pipes;
            Sockets = other.Sockets;
            IO = other.IO;
            TopicSubscribers = other.TopicSubscribers;
            Graphs = other.Graphs;
            HostEpochs = other.HostEpochs;
            GraphCommitRejections = other.GraphCommitRejections;
        }

        public IFileSystemNode? GetNode(string absolutePath)
        {
            var tokens = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            IFileSystemNode current = FileSystem;

            foreach (var token in tokens)
            {
                if (current is DirectoryNode dir && dir.Children.TryGetValue(token, out var child))
                {
                    current = child;
                }
                else
                {
                    return null;
                }
            }
            return current;
        }

        public bool SetNode(string absolutePath, IFileSystemNode node)
        {
            var tokens = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return false;

            IFileSystemNode current = FileSystem;
            for (int i = 0; i < tokens.Length - 1; i++)
            {
                var token = tokens[i];
                if (current is DirectoryNode dir)
                {
                    if (!dir.Children.TryGetValue(token, out var next))
                    {
                        next = new DirectoryNode();
                        dir.Children[token] = next;
                    }
                    current = next;
                }
                else
                {
                    return false; // Intermediate node is not a directory
                }
            }

            if (current is DirectoryNode finalDir)
            {
                finalDir.Children[tokens[^1]] = node;
                return true;
            }
            return false;
        }

        public bool DeleteNode(string absolutePath)
        {
            var tokens = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return false;

            IFileSystemNode current = FileSystem;
            for (int i = 0; i < tokens.Length - 1; i++)
            {
                var token = tokens[i];
                if (current is DirectoryNode dir && dir.Children.TryGetValue(token, out var next))
                {
                    current = next;
                }
                else
                {
                    return false;
                }
            }

            if (current is DirectoryNode finalDir)
            {
                return finalDir.Children.Remove(tokens[^1]);
            }
            return false;
        }

        public bool MoveNode(string srcPath, string dstPath)
        {
            var srcNode = GetNode(srcPath);
            if (srcNode == null) return false;

            if (GetNode(dstPath) != null) return false;

            if (!SetNode(dstPath, srcNode)) return false;

            if (!DeleteNode(srcPath))
            {
                // Rollback if delete fails (highly unlikely given SetNode succeeded, but just in case)
                DeleteNode(dstPath);
                return false;
            }

            return true;
        }
    }

    // A deployed graph instance. The plan and the bound graph travel as JSON (OneOS.Runtime.Graphs.GraphSerialization).
    [MessagePackObject]
    public class GraphInstanceRecord
    {
        [Key(0)] public string Id { get; set; } = string.Empty;
        [Key(1)] public string GraphName { get; set; } = string.Empty;
        [Key(2)] public string State { get; set; } = "Pending";             // Pending, Running, Stopping, Failed
        [Key(3)] public string PlanJson { get; set; } = string.Empty;
        [Key(4)] public string GraphJson { get; set; } = string.Empty;
        // Resources reserved per host: [CPU millicores, memory bytes].
        [Key(5)] public Dictionary<string, long[]> Committed { get; set; } = new Dictionary<string, long[]>();
        [Key(6)] public Dictionary<string, long> ExpectedHostVersions { get; set; } = new Dictionary<string, long>();
        [Key(7)] public Dictionary<string, string> AgentStates { get; set; } = new Dictionary<string, string>();
        [Key(8)] public string? Error { get; set; }
        [Key(9)] public string Owner { get; set; } = string.Empty;
        [Key(10)] public long Version { get; set; }
        // Per-agent rates reported by hosts: [received/s, delivered/s, emitted/s, queued].
        [Key(11)] public Dictionary<string, double[]> Metrics { get; set; } = new Dictionary<string, double[]>();
        // The plan version in PlanJson (bumped by every re-plan).
        [Key(12)] public long PlanVersion { get; set; } = 1;
        // Stateful agents' taints as last reported (L§9.3: a standby takes over with the taint as of the
        // failure), with each taint's rank (the number of labels at or below it), which only grows.
        [Key(13)] public Dictionary<string, string> Taints { get; set; } = new Dictionary<string, string>();
        [Key(14)] public Dictionary<string, int> TaintRanks { get; set; } = new Dictionary<string, int>();
    }

    [MessagePackObject]
    public class SessionInfo
    {
        [Key(0)] public string User { get; set; } = string.Empty;
        [Key(1)] public string ShellUri { get; set; } = string.Empty;
        [Key(2)] public string ClientUri { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class AgentInfo
    {
        public enum StartMode { New, Load }
        public enum LanguageInfo { OneOSKernel, OneOS, OneOSLambda, CSharp, JavaScript, Python, Java, Docker }

        [Key(0)] public string URI { get; set; } = string.Empty;
        [Key(1)] public int GPID { get; set; }
        [Key(2)] public string Runtime { get; set; } = string.Empty;
        [Key(3)] public string StandbyRuntime { get; set; } = string.Empty;
        [Key(4)] public string User { get; set; } = string.Empty;
        [Key(5)] public string Session { get; set; } = string.Empty;
        [Key(6)] public string Graph { get; set; } = string.Empty;
        [Key(7)] public string ReplicaSet { get; set; } = string.Empty;
        [Key(8)] public StartMode Mode { get; set; }
        [Key(9)] public LanguageInfo Language { get; set; }
        [Key(10)] public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();
        [Key(11)] public bool OutputToShell { get; set; }
        [Key(12)] public string BinaryPath { get; set; } = string.Empty;
        // The process's arguments, after BinaryPath (an argument vector: no shell or quoting involved).
        [Key(13)] public List<string> Arguments { get; set; } = new List<string>();
        [Key(14)] public List<string> RuntimePool { get; set; } = new List<string>();
        [Key(15)] public List<string> Subscriptions { get; set; } = new List<string>();
        [Key(16)] public int CheckpointInterval { get; set; }
        [Key(17)] public float OutputRateLimit { get; set; }
        [Key(18)] public string? LastCheckpoint { get; set; }
        [Key(19)] public List<string> Children { get; set; } = new List<string>();
    }

    [MessagePackObject]
    public class PipeInfo
    {
        public enum PipeStrategy { Direct, Broadcast, RoundRobin, Shuffle }

        [Key(0)] public List<string> Sources { get; set; } = new List<string>();
        [Key(1)] public List<string> Sinks { get; set; } = new List<string>();
        [Key(2)] public string? Graph { get; set; }
        [Key(3)] public PipeStrategy Strategy { get; set; } = PipeStrategy.Direct;
        [Key(4)] public string? OrderBy { get; set; }
        [Key(5)] public double MinRate { get; set; }
        [Key(6)] public double MaxRate { get; set; } = 9999.0;
        [Key(7)] public string Id { get; set; } = string.Empty;
        
        [IgnoreMember]
        public string Key => Id;
    }

    [Union(0, typeof(DirectoryNode))]
    [Union(1, typeof(FileNode))]
    [Union(2, typeof(SocketInfo))]
    [Union(3, typeof(IOHandle))]
    public interface IFileSystemNode { }

    [MessagePackObject]
    public class DirectoryNode : IFileSystemNode
    {
        [Key(0)] public Dictionary<string, IFileSystemNode> Children { get; set; } = new Dictionary<string, IFileSystemNode>();
    }

    [MessagePackObject]
    public class FileNode : IFileSystemNode
    {
        [Key(0)] public Dictionary<string, string> Copies { get; set; } = new Dictionary<string, string>();
        [Key(1)] public string? Checksum { get; set; }
        [Key(2)] public long Size { get; set; }
    }

    // A cluster-wide socket (plan step 8): port Port is claimed by agent Owner, which runs on HostRuntime and
    // listens on that runtime's loopback address; every runtime proxies the port to it. IsIndependent: the
    // owner listens on the public interface itself, and there are no proxies.
    [MessagePackObject]
    public class SocketInfo : IFileSystemNode
    {
        [Key(0)] public string Owner { get; set; } = string.Empty;
        [Key(1)] public int Port { get; set; }
        [Key(2)] public string HostRuntime { get; set; } = string.Empty;
        [Key(3)] public bool IsIndependent { get; set; }
    }

    [MessagePackObject]
    // An I/O device exposed to the cluster (Registry.IO, keyed by its driver's URI): what kind of device, the
    // driver serving it, and the runtime it is attached to.
    public class IOHandle : IFileSystemNode
    {
        [Key(0)] public string DeviceType { get; set; } = string.Empty;
        [Key(1)] public string Driver { get; set; } = string.Empty;
        [Key(2)] public string HostRuntime { get; set; } = string.Empty;
    }
}
