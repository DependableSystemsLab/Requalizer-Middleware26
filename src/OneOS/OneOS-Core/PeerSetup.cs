using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OneOS.Runtime;

namespace OneOS_Core
{
    // A runtime's TLS identity and its peers (the peers it connects to, and the certificate each must present):
    // used by `oneos config` and by `oneos peer`.
    static class PeerSetup
    {
        public static string CertificateFile(Configuration config) => Path.Combine(config.MountPath, config.CertificatePath);

        // The runtime's self-signed certificate (created when missing, named after the runtime's URI) and its hash.
        public static string EnsureCertificate(Configuration config, Action<string>? log = null)
        {
            var file = CertificateFile(config);
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                CertificateHelper.GenerateSelfSignedCertificate(file, config.URI);
                log?.Invoke($"Created a self-signed certificate for {config.URI} at {file}");
            }
            return CertificateHelper.GetCertHashFromFile(file);
        }

        // A peer as another runtime's mount describes it: its ID, address and certificate (created if missing).
        public static PeerInfo FromMount(string mountPath)
        {
            var config = Configuration.Load(mountPath) ?? throw new InvalidOperationException($"no OneOS configuration at {mountPath} (run `oneos config --mount {mountPath}`)");
            return new PeerInfo { ID = config.ID, Host = config.Host, Port = config.Port, CertificateHash = EnsureCertificate(config) };
        }

        // A peer's certificate hash from `source`: "fetch" (asks the running peer at host:port for its certificate),
        // a certificate file (.pfx/.p12/.cer/.crt/.pem), a runtime's mount directory, or the hash itself.
        public static async Task<string> ResolveHashAsync(string source, string host, int port)
        {
            if (source.Equals("fetch", StringComparison.OrdinalIgnoreCase))
                return await CertificateHelper.GetCertHashFromRemoteAsync(host, port);
            var path = ExpandHome(source);
            if (Directory.Exists(path)) return FromMount(path).CertificateHash;
            if (File.Exists(path)) return CertificateHelper.GetCertHashFromFile(path);
            var hash = source.Replace(":", "").Replace(" ", "").ToUpperInvariant();
            if (hash.Length == 40 && hash.All(Uri.IsHexDigit)) return hash;
            throw new ArgumentException($"'{source}' is not \"fetch\", a certificate file, a runtime's mount directory, or a 40-digit certificate hash");
        }

        // Adds or replaces a peer (by ID); a runtime isn't its own peer.
        public static void SetPeer(Configuration config, PeerInfo peer)
        {
            if (peer.ID == config.ID) throw new ArgumentException($"{peer.ID} is this runtime itself");
            config.Peers[peer.ID] = peer;
        }

        public static string Describe(PeerInfo peer) => $"{peer.ID,-12} {peer.Address,-22} {peer.CertificateHash}";

        public static string ExpandHome(string path) =>
            path == "~" || path.StartsWith("~/") ? Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", path.TrimStart('~', '/')) : path;

        // `peer list|add|remove|link`.
        public static async Task<int> RunAsync(string[] args)
        {
            const string usage = """
                Usage: oneos peer list   [--mount <path>]
                       oneos peer add    [--mount <path>] --from <peer's mount>
                       oneos peer add    [--mount <path>] <id> <host:port> [--hash <source>]
                                           source: fetch (default; the peer must be running), a certificate file,
                                                   the peer's mount directory, or the 40-digit hash
                       oneos peer remove [--mount <path>] <id>
                       oneos peer link   <mount> <mount> [<mount>...]   register each runtime as a peer of the others
                Changes take effect when a runtime (re)starts.
                """;
            string mountPath = Configuration.DefaultMountPath;
            string? from = null, hashSource = null;
            var positional = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--mount" && i + 1 < args.Length) mountPath = ExpandHome(args[++i]);
                else if (args[i] == "--from" && i + 1 < args.Length) from = ExpandHome(args[++i]);
                else if (args[i] == "--hash" && i + 1 < args.Length) hashSource = args[++i];
                else if (args[i].StartsWith("--")) { Console.Error.WriteLine(usage); return 2; }
                else positional.Add(args[i]);
            }
            if (positional.Count == 0) { Console.Error.WriteLine(usage); return 2; }
            var verb = positional[0];
            positional.RemoveAt(0);
            try
            {
                if (verb == "link")
                {
                    if (positional.Count < 2) { Console.Error.WriteLine(usage); return 2; }
                    var mounts = positional.Select(ExpandHome).ToList();
                    var configs = mounts.Select(m => Configuration.Load(m) ?? throw new InvalidOperationException($"no OneOS configuration at {m}")).ToList();
                    var peers = mounts.Select(FromMount).ToList();
                    for (int a = 0; a < configs.Count; a++)
                    {
                        for (int b = 0; b < configs.Count; b++) if (a != b) SetPeer(configs[a], peers[b]);
                        configs[a].Save(mounts[a]);
                        Console.WriteLine($"{configs[a].ID} ({mounts[a]}): peers {string.Join(", ", configs[a].Peers.Keys.OrderBy(k => k))}");
                    }
                    return 0;
                }

                var config = Configuration.Load(mountPath) ?? throw new InvalidOperationException($"no OneOS configuration at {mountPath} (run `oneos config --mount {mountPath}`)");
                switch (verb)
                {
                    case "list":
                        Console.WriteLine($"{config.ID} ({config.Host}:{config.Port}) certificate {EnsureCertificate(config, Console.WriteLine)}");
                        foreach (var p in config.Peers.Values.OrderBy(p => p.ID)) Console.WriteLine("  " + Describe(p));
                        if (config.Peers.Count == 0) Console.WriteLine("  (no peers)");
                        return 0;
                    case "add":
                        PeerInfo peer;
                        if (from != null) peer = FromMount(from);
                        else
                        {
                            if (positional.Count != 2) { Console.Error.WriteLine(usage); return 2; }
                            var address = positional[1].Split(':');
                            if (address.Length != 2 || !int.TryParse(address[1], out var port)) throw new ArgumentException($"invalid address '{positional[1]}' (host:port)");
                            peer = new PeerInfo { ID = positional[0], Host = address[0], Port = port, CertificateHash = await ResolveHashAsync(hashSource ?? "fetch", address[0], port) };
                        }
                        SetPeer(config, peer);
                        config.Save(mountPath);
                        Console.WriteLine($"{config.ID}: peer {Describe(peer)}");
                        return 0;
                    case "remove":
                        if (positional.Count != 1) { Console.Error.WriteLine(usage); return 2; }
                        if (!config.Peers.Remove(positional[0])) { Console.Error.WriteLine($"{config.ID} has no peer {positional[0]}"); return 1; }
                        config.Save(mountPath);
                        Console.WriteLine($"{config.ID}: removed peer {positional[0]}");
                        return 0;
                    default:
                        Console.Error.WriteLine(usage);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"oneos peer: {ex.Message}");
                return 1;
            }
        }

        // The interactive part of `oneos config`: the certificate, then the peers.
        public static void Configure(Configuration config)
        {
            Console.WriteLine();
            Console.WriteLine($"This runtime's certificate hash: {EnsureCertificate(config, Console.WriteLine)}");
            Console.WriteLine();
            Console.WriteLine("Peers (the runtimes this one connects to, and the certificate each must present):");
            foreach (var p in config.Peers.Values.OrderBy(p => p.ID)) Console.WriteLine("  " + Describe(p));
            if (config.Peers.Count == 0) Console.WriteLine("  (none)");
            while (true)
            {
                Console.Write("Peer ID to add or update ('-ID' removes it; blank when done): ");
                var id = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(id)) break;
                if (id.StartsWith('-'))
                {
                    Console.WriteLine(config.Peers.Remove(id[1..]) ? $"  removed {id[1..]}" : $"  no peer {id[1..]}");
                    continue;
                }
                if (id == config.ID) { Console.WriteLine("  that is this runtime itself"); continue; }
                config.Peers.TryGetValue(id, out var existing);
                Console.Write($"  Host [{existing?.Host ?? "127.0.0.1"}]: ");
                var host = Console.ReadLine()?.Trim();
                host = string.IsNullOrEmpty(host) ? existing?.Host ?? "127.0.0.1" : host;
                Console.Write($"  Port [{existing?.Port.ToString() ?? "none"}]: ");
                var portText = Console.ReadLine()?.Trim();
                int port;
                if (string.IsNullOrEmpty(portText) && existing != null) port = existing.Port;
                else if (!int.TryParse(portText, out port)) { Console.WriteLine("  a port is required; peer not changed"); continue; }
                Console.WriteLine("  Certificate: 'fetch' (from the running peer), a certificate file, the peer's mount directory,");
                Console.Write($"  or the hash [{(existing != null ? existing.CertificateHash : "fetch")}]: ");
                var source = Console.ReadLine()?.Trim();
                string hash;
                try
                {
                    hash = string.IsNullOrEmpty(source) && existing != null && existing.Host == host && existing.Port == port
                        ? existing.CertificateHash
                        : ResolveHashAsync(string.IsNullOrEmpty(source) ? "fetch" : source, host, port).GetAwaiter().GetResult();
                }
                catch (Exception ex) { Console.WriteLine($"  couldn't get the certificate ({ex.Message}); peer not changed"); continue; }
                var peer = new PeerInfo { ID = id, Host = host, Port = port, CertificateHash = hash };
                config.Peers[id] = peer;
                Console.WriteLine($"  {Describe(peer)}");
            }
        }
    }
}
