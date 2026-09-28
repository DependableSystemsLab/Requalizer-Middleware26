using System;
using System.Text.Json.Serialization;
using MessagePack;

namespace OneOS.Runtime
{
    public enum RoutingStrategy { Direct, Topic }

    [MessagePackObject]
    [Union(0, typeof(HandshakeRequest))]
    [Union(1, typeof(HandshakeResponse))]
    [Union(2, typeof(TerminalConnectRequest))]
    [Union(3, typeof(TerminalRedirectResponse))]
    [Union(4, typeof(TerminalConnectResponse))]
    [Union(5, typeof(RegistryUpdateRequest))]
    [Union(6, typeof(RegistryUpdateResponse))]
    [Union(7, typeof(Envelope))]
    [Union(8, typeof(RawPipeRequest))]
    [Union(9, typeof(RawPipeResponse))]
    [Union(10, typeof(ClusterInfoRequest))]
    [Union(11, typeof(ClusterInfoResponse))]
    [Union(12, typeof(ClusterInfoUpdate))]
    [Union(13, typeof(SocketConnectRequest))]
    [Union(14, typeof(FileUploadRequest))]
    [Union(15, typeof(FileUploadResponse))]
    [Union(16, typeof(FileDownloadRequest))]
    [Union(17, typeof(FileDownloadResponse))]
    [Union(18, typeof(BrowserConnectRequest))]
    [Union(19, typeof(BrowserConnectResponse))]
    [Union(20, typeof(SubscribeRequest))]
    [Union(21, typeof(UnsubscribeRequest))]
    [Union(22, typeof(RegistrySnapshotEvent))]
    [Union(23, typeof(RegistryUpdateEvent))]
    [Union(24, typeof(ResourceMetricsEvent))]
    [Union(25, typeof(ResourceMetricsRequest))]
    [JsonDerivedType(typeof(HandshakeRequest), typeDiscriminator: "HandshakeRequest")]
    [JsonDerivedType(typeof(HandshakeResponse), typeDiscriminator: "HandshakeResponse")]
    [JsonDerivedType(typeof(TerminalConnectRequest), typeDiscriminator: "TerminalConnectRequest")]
    [JsonDerivedType(typeof(TerminalRedirectResponse), typeDiscriminator: "TerminalRedirectResponse")]
    [JsonDerivedType(typeof(TerminalConnectResponse), typeDiscriminator: "TerminalConnectResponse")]
    [JsonDerivedType(typeof(RegistryUpdateRequest), typeDiscriminator: "RegistryUpdateRequest")]
    [JsonDerivedType(typeof(RegistryUpdateResponse), typeDiscriminator: "RegistryUpdateResponse")]
    [JsonDerivedType(typeof(Envelope), typeDiscriminator: "Envelope")]
    [JsonDerivedType(typeof(RawPipeRequest), typeDiscriminator: "RawPipeRequest")]
    [JsonDerivedType(typeof(RawPipeResponse), typeDiscriminator: "RawPipeResponse")]
    [JsonDerivedType(typeof(ClusterInfoRequest), typeDiscriminator: "ClusterInfoRequest")]
    [JsonDerivedType(typeof(ClusterInfoResponse), typeDiscriminator: "ClusterInfoResponse")]
    [JsonDerivedType(typeof(ClusterInfoUpdate), typeDiscriminator: "ClusterInfoUpdate")]
    [JsonDerivedType(typeof(SocketConnectRequest), typeDiscriminator: "SocketConnectRequest")]
    [JsonDerivedType(typeof(FileUploadRequest), typeDiscriminator: "FileUploadRequest")]
    [JsonDerivedType(typeof(FileUploadResponse), typeDiscriminator: "FileUploadResponse")]
    [JsonDerivedType(typeof(FileDownloadRequest), typeDiscriminator: "FileDownloadRequest")]
    [JsonDerivedType(typeof(FileDownloadResponse), typeDiscriminator: "FileDownloadResponse")]
    [JsonDerivedType(typeof(BrowserConnectRequest), typeDiscriminator: "BrowserConnectRequest")]
    [JsonDerivedType(typeof(BrowserConnectResponse), typeDiscriminator: "BrowserConnectResponse")]
    [JsonDerivedType(typeof(SubscribeRequest), typeDiscriminator: "SubscribeRequest")]
    [JsonDerivedType(typeof(UnsubscribeRequest), typeDiscriminator: "UnsubscribeRequest")]
    [JsonDerivedType(typeof(RegistrySnapshotEvent), typeDiscriminator: "RegistrySnapshotEvent")]
    [JsonDerivedType(typeof(RegistryUpdateEvent), typeDiscriminator: "RegistryUpdateEvent")]
    [JsonDerivedType(typeof(ResourceMetricsEvent), typeDiscriminator: "ResourceMetricsEvent")]
    [JsonDerivedType(typeof(ResourceMetricsRequest), typeDiscriminator: "ResourceMetricsRequest")]
    public abstract record RuntimeMessage : OneOS.Common.IMessage
    {
        [Key(0)]
        public Guid MessageId { get; set; }

        [Key(1)]
        public string SenderId { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public record RegistryUpdateRequest : RuntimeMessage
    {
        [Key(2)]
        public RegistryAction Action { get; set; } = null!;
    }

    [MessagePackObject]
    public record RegistryUpdateResponse : RuntimeMessage
    {
        [Key(2)]
        public bool Success { get; set; }
        
        [Key(3)]
        public string ErrorMessage { get; set; } = string.Empty;

        // The leader's commit index once the action committed: the requester waits until its own Registry has
        // applied that far, so it reads its own write (a follower learns commits on the next heartbeat).
        [Key(4)]
        public long CommitIndex { get; set; }
    }

    [MessagePackObject]
    public record HandshakeRequest : RuntimeMessage
    {
        [Key(2)]
        public string NodeId { get; set; } = string.Empty;

        [Key(3)]
        public string ClusterDomain { get; set; } = string.Empty;

        [Key(4)]
        public int LinkIndex { get; set; } = 0;
    }

    [MessagePackObject]
    public record HandshakeResponse : RuntimeMessage
    {
        [Key(2)]
        public bool Accepted { get; set; }

        [Key(3)]
        public string Reason { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public record TerminalConnectRequest : RuntimeMessage
    {
        [Key(2)]
        public string Username { get; set; } = string.Empty;

        [Key(3)]
        public string Password { get; set; } = string.Empty;

        // `oneos connect -c`: run this command line (commands separated by ';' or newlines) in a new shell with no
        // prompts and no terminal input except Ctrl+C, then close the connection once the last foreground agent has
        // ended. Null for an interactive session.
        [Key(4)]
        public string? Command { get; set; }
    }

    [MessagePackObject]
    public record TerminalRedirectResponse : RuntimeMessage
    {
        [Key(2)]
        public string RedirectAddress { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public record TerminalConnectResponse : RuntimeMessage
    {
        [Key(2)]
        public bool Accepted { get; set; }

        [Key(3)]
        public string Reason { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public record Envelope : RuntimeMessage
    {
        [Key(2)]
        public string SenderAgentUri { get; set; } = string.Empty;

        [Key(3)]
        public RoutingStrategy Strategy { get; set; }

        [Key(4)]
        public string Target { get; set; } = string.Empty;

        [Key(5)]
        public string Channel { get; set; } = "default";

        [Key(6)]
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        // Dataflow graph messages (on pipes between graph agents; Channel is the receiving port, Target and
        // SenderAgentUri are graph agent ids, Payload is the message as UTF-8 JSON):
        // the data label (L§8.5), the sequence number on sequenced paths (L§6.6.4), and when the message
        // entered its latency flow, for correlated latency measurement (L§6.7).
        [Key(7)]
        public string? Label { get; set; }

        [Key(8)]
        public long? Sequence { get; set; }

        [Key(9)]
        public DateTimeOffset? EntryTimestamp { get; set; }

        // Profiling: when this copy was sent on its pipe (pipe latency), and when and at which source node
        // the message's lineage started (end-to-end latency; an output inherits the origin of the input it
        // answers, for @one_to_one nodes, else of the latest input its node received).
        [Key(10)]
        public DateTimeOffset? SentAt { get; set; }

        [Key(11)]
        public DateTimeOffset? OriginTimestamp { get; set; }

        [Key(12)]
        public string? OriginNode { get; set; }
    }

    // Opens the connecting side of a pipe between runtimes (both raw and message pipes: the handshake is
    // the same, and the expecting side already knows the pipe's kind; `Kind` is informational).
    [MessagePackObject]
    public record RawPipeRequest : RuntimeMessage
    {
        [Key(2)] public string PipeId { get; set; } = string.Empty;
        [Key(3)] public string Mode { get; set; } = string.Empty;
        [Key(4)] public PipeKind Kind { get; set; } = PipeKind.Raw;
    }

    // A proxy's tunnel for one client connection to a cluster-wide socket (plan step 8): sent to the runtime
    // hosting the socket's owner, which connects to its loopback socket and answers with a RawPipeResponse;
    // the connection then carries the client's bytes both ways.
    [MessagePackObject]
    public record SocketConnectRequest : RuntimeMessage
    {
        [Key(2)] public int Port { get; set; }
    }

    // `oneos cp` (FileUpload): the first message of a connection, authenticated like a terminal's.
    //   Resolve    where a copy of local SourceName to Path goes (cp: inside Path when it is a directory)
    //   Directory  creates directory Path
    //   File       after an accepting response, exactly Size raw bytes follow; a second response confirms
    //              that file Path was written
    [MessagePackObject]
    public record FileUploadRequest : RuntimeMessage
    {
        public enum UploadKind { Resolve, Directory, File }

        [Key(2)] public string Username { get; set; } = string.Empty;
        [Key(3)] public string Password { get; set; } = string.Empty;
        [Key(4)] public UploadKind Kind { get; set; }
        [Key(5)] public string Path { get; set; } = string.Empty;
        [Key(6)] public long Size { get; set; }
        [Key(7)] public string SourceName { get; set; } = string.Empty;
        [Key(8)] public bool SourceIsDirectory { get; set; }
    }

    [MessagePackObject]
    public record FileUploadResponse : RuntimeMessage
    {
        [Key(2)] public bool Accepted { get; set; }
        [Key(3)] public string Path { get; set; } = string.Empty;
        [Key(4)] public string Error { get; set; } = string.Empty;
    }

    // `oneos cp oneos:<path> <local>` (FileDownload), authenticated like a terminal's.
    //   List  the tree at Path: the entry itself ("") and, for a directory, everything under it
    //   File  the response carries the file's length; exactly that many raw bytes follow
    [MessagePackObject]
    public record FileDownloadRequest : RuntimeMessage
    {
        public enum DownloadKind { List, File }

        [Key(2)] public string Username { get; set; } = string.Empty;
        [Key(3)] public string Password { get; set; } = string.Empty;
        [Key(4)] public DownloadKind Kind { get; set; }
        [Key(5)] public string Path { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public record FileDownloadEntry
    {
        [Key(0)] public string Path { get; set; } = string.Empty;     // relative to the listed path; "" for itself
        [Key(1)] public bool IsDirectory { get; set; }
    }

    [MessagePackObject]
    public record FileDownloadResponse : RuntimeMessage
    {
        [Key(2)] public bool Accepted { get; set; }
        [Key(3)] public string Error { get; set; } = string.Empty;
        [Key(4)] public long Size { get; set; }
        [Key(5)] public List<FileDownloadEntry> Entries { get; set; } = new();
    }

    [MessagePackObject]
    public record RawPipeResponse : RuntimeMessage
    {
        [Key(2)] public bool Accepted { get; set; }
    }

    // Cluster-info exchange (control plane), run on demand just before scheduling:
    //   1. an initiator (any runtime) sends ClusterInfoRequest to every peer;
    //   2. each peer answers with ClusterInfoResponse carrying its own HostInfo;
    //   3. the initiator aggregates the answers into a ClusterState and sends it to every peer in a
    //      ClusterInfoUpdate; every runtime keeps the latest state it has seen.
    [MessagePackObject]
    public record ClusterInfoRequest : RuntimeMessage
    {
        [Key(2)] public Guid RoundId { get; set; }
    }

    [MessagePackObject]
    public record ClusterInfoResponse : RuntimeMessage
    {
        [Key(2)] public HostInfo Info { get; set; } = new HostInfo();
    }

    [MessagePackObject]
    public record ClusterInfoUpdate : RuntimeMessage
    {
        [Key(2)] public ClusterState State { get; set; } = new ClusterState();
    }

    // --- Browser (publish-subscribe) clients; see EventHub ---
    // A browser client (OneOS.Client.Browser) connects to any runtime with BrowserConnectRequest (authenticated
    // like a terminal); the connection then stays framed: the client sends SubscribeRequest/UnsubscribeRequest,
    // the runtime sends the events of the subscribed topics. Subscriptions live only as long as the connection.
    //   "registry"  RegistrySnapshotEvent (the whole Registry as of a Raft index), then a RegistryUpdateEvent for
    //               each action applied after it, in order
    //   "metrics"   ResourceMetricsEvent: every runtime's resource sample (CPU, memory of the runtime process and
    //               of each agent process it runs), every EventHub.MetricsInterval

    [MessagePackObject]
    public record BrowserConnectRequest : RuntimeMessage
    {
        [Key(2)] public string Username { get; set; } = string.Empty;
        [Key(3)] public string Password { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public record BrowserConnectResponse : RuntimeMessage
    {
        [Key(2)] public bool Accepted { get; set; }
        [Key(3)] public string Reason { get; set; } = string.Empty;
        // The runtime the client is connected to.
        [Key(4)] public string RuntimeId { get; set; } = string.Empty;
        // The topics this runtime publishes.
        [Key(5)] public System.Collections.Generic.List<string> Topics { get; set; } = new();
    }

    [MessagePackObject]
    public record SubscribeRequest : RuntimeMessage
    {
        [Key(2)] public System.Collections.Generic.List<string> Topics { get; set; } = new();
    }

    [MessagePackObject]
    public record UnsubscribeRequest : RuntimeMessage
    {
        [Key(2)] public System.Collections.Generic.List<string> Topics { get; set; } = new();
    }

    // The whole Registry (MessagePack-encoded Registry) as of the Raft log entry `Index`. Sent when "registry" is
    // subscribed, and again whenever the runtime's Registry is replaced (a snapshot installed from the leader).
    [MessagePackObject]
    public record RegistrySnapshotEvent : RuntimeMessage
    {
        [Key(2)] public long Index { get; set; }
        [Key(3)] public byte[] Registry { get; set; } = Array.Empty<byte>();
    }

    // An action applied to the Registry at Raft log entry `Index` (after the latest snapshot's).
    [MessagePackObject]
    public record RegistryUpdateEvent : RuntimeMessage
    {
        [Key(2)] public long Index { get; set; }
        [Key(3)] public RegistryAction Action { get; set; } = null!;
    }

    // One runtime's resource sample. Unreachable: the runtime didn't answer in time (no samples).
    [MessagePackObject]
    public record ResourceMetricsEvent : RuntimeMessage
    {
        [Key(2)] public string Runtime { get; set; } = string.Empty;
        [Key(3)] public DateTime Time { get; set; }
        [Key(4)] public bool Unreachable { get; set; }
        [Key(5)] public Monitoring.ProcessSample? Process { get; set; }
        [Key(6)] public System.Collections.Generic.List<Monitoring.ProcessSample> Agents { get; set; } = new();
    }

    // A runtime asks a peer for its resource sample (answered with a ResourceMetricsEvent).
    [MessagePackObject]
    public record ResourceMetricsRequest : RuntimeMessage
    {
    }
}
