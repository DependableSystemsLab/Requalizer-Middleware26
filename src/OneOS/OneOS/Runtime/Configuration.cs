using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneOS.Runtime
{
    public class VMConfiguration
    {
        public string Name { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public string Bin { get; set; } = string.Empty;
    }

    public class IOConfiguration
    {
        public string Name { get; set; } = string.Empty;
        public string Driver { get; set; } = string.Empty;
        public List<string> Arguments { get; set; } = new List<string>();
    }

    public class PeerInfo
    {
        public string ID { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 5000;
        public string CertificateHash { get; set; } = string.Empty;
        
        [JsonIgnore]
        public string Address => $"{Host}:{Port}";
    }

    public class Configuration
    {
        [JsonIgnore]
        public static string DefaultMountPath => Path.Combine(
            Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX
                ? Environment.GetEnvironmentVariable("HOME") ?? ""
                : Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%"),
            ".oneos"
        );

        public string Domain { get; set; } = "default.domain";
        public string ID { get; set; } = "node1";
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 5000;
        public string StoragePath { get; set; } = "data";
        public string LogPath { get; set; } = "log";
        public string CertificatePath { get; set; } = "cert.pfx";

        public List<VMConfiguration> VMs { get; set; } = new List<VMConfiguration>();
        public List<IOConfiguration> IO { get; set; } = new List<IOConfiguration>();

        // Using an array for easy JSON serialization instead of ValueTuple
        public int[] Cores { get; set; } = new int[] { 1, 2000 };
        public long Memory { get; set; } = 1024;
        public long Disk { get; set; } = 10240;

        public List<string> Tags { get; set; } = new List<string>();

        // Scheduling inventory (scheduling-spec §2.2). Label: the host's IFC label, null for ⊥.
        public string? Label { get; set; }
        public string? Zone { get; set; }
        public int? MaxAgents { get; set; }
        public List<string> IdmSupport { get; set; } = new List<string>();

        public Dictionary<string, PeerInfo> Peers { get; set; } = new Dictionary<string, PeerInfo>();

        // Cluster-wide sockets (plan step 8). A user component's listening socket binds this address (a
        // loopback address, so it is never reachable from outside); every runtime proxies the port on its
        // ProxyAddresses. Runtimes sharing a machine need distinct values of both.
        public const string DefaultLoopbackAddress = "127.124.124.1";
        public string LoopbackAddress { get; set; } = DefaultLoopbackAddress;
        // Addresses the proxies bind (never the wildcard, which would take the port from the component's
        // socket). Null: every non-loopback IPv4 address of the host, and 127.0.0.1.
        public List<string>? ProxyAddresses { get; set; }

        [JsonIgnore]
        public string URI => $"{ID}.{Domain}";

        [JsonIgnore]
        public string MountPath { get; set; } = string.Empty;

        [JsonIgnore]
        public string TempPath => Path.Combine(MountPath, "temp");

        public static Configuration? Load(string? mountPath = null)
        {
            if (string.IsNullOrEmpty(mountPath)) mountPath = DefaultMountPath;

            string configPath = Path.Combine(mountPath, "config.json");
            if (!File.Exists(configPath))
            {
                return null;
            }

            string json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<Configuration>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config != null)
            {
                config.MountPath = mountPath;
            }
            return config;
        }

        public void Save(string? mountPath = null)
        {
            if (string.IsNullOrEmpty(mountPath)) mountPath = string.IsNullOrEmpty(MountPath) ? DefaultMountPath : MountPath;

            if (!Directory.Exists(mountPath))
            {
                Directory.CreateDirectory(mountPath);
            }

            string configPath = Path.Combine(mountPath, "config.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(configPath, json);
        }
    }
}
