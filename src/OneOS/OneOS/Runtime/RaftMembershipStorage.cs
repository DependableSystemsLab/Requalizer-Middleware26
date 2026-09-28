using System;
using System.Collections.Generic;
using System.Net;
using DotNext.Buffers;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Membership;

namespace OneOS.Runtime
{
    // The Raft cluster's membership (DotNext's cluster configuration), persisted next to the Raft log. With
    // DotNext's default in-memory storage, every node forgot the membership at restart: the seed node
    // cold-started as a one-node cluster (a majority on its own) and re-added its peers, and the others knew no
    // members until the seed replicated them. Persisted, a restarted cluster elects among its known members.
    // An address is stored as [length of the IP address bytes][IP address bytes][port, 4 bytes little-endian].
    internal sealed class RaftMembershipStorage : PersistentClusterConfigurationStorage<EndPoint>
    {
        public RaftMembershipStorage(string path)
            : base(path, 4096, EqualityComparer<EndPoint>.Default, allocator: null)
        {
        }

        // Whether a membership was persisted at `path` (the active list is a fingerprint, 8 bytes, then the members).
        // Checked on the file rather than by loading it: DotNext loads the storage itself when the cluster starts,
        // and a second load reads from the end of the file.
        public static bool HasMembers(string path)
        {
            var active = new System.IO.FileInfo(System.IO.Path.Combine(path, "active.list"));
            return active.Exists && active.Length > 8;
        }

        protected override void Encode(EndPoint address, ref BufferWriterSlim<byte> output)
        {
            if (address is not IPEndPoint ip) throw new NotSupportedException($"Raft member address {address} isn't an IP endpoint");
            var bytes = ip.Address.GetAddressBytes();
            output.Add((byte)bytes.Length);
            output.Write(bytes);
            Span<byte> port = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(port, ip.Port);
            output.Write(port);
        }

        protected override EndPoint Decode(ref SequenceReader reader)
        {
            Span<byte> length = stackalloc byte[1];
            reader.Read(length);
            var bytes = new byte[length[0]];
            reader.Read(bytes);
            Span<byte> port = stackalloc byte[4];
            reader.Read(port);
            return new IPEndPoint(new IPAddress(bytes), System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(port));
        }
    }
}
