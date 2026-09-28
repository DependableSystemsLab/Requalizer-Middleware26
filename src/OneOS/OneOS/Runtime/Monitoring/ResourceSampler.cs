using System;
using System.Collections.Generic;
using System.Diagnostics;
using MessagePack;

namespace OneOS.Runtime.Monitoring
{
    // CPU and memory of one process: the runtime's own (Agent null) or an agent's.
    [MessagePackObject]
    public sealed class ProcessSample
    {
        [Key(0)] public string? Agent { get; set; }
        [Key(1)] public int Pid { get; set; }
        // Percent of one core (like top) since this process's previous sample; null on its first.
        [Key(2)] public double? CpuPct { get; set; }
        [Key(3)] public long RssBytes { get; set; }
        [Key(4)] public int Threads { get; set; }
        // The runtime process only.
        [Key(5)] public long? PrivateBytes { get; set; }
        [Key(6)] public long? GcHeapBytes { get; set; }
        [Key(7)] public int[]? GcCollections { get; set; }
    }

    // Samples the runtime process and its agents' processes (used by the Profiler's resources.jsonl and the
    // EventHub's "metrics" topic). CPU is a rate, so the sampler remembers each process's previous sample.
    public sealed class ResourceSampler
    {
        private readonly Func<IEnumerable<(string Agent, int Pid)>> _agents;
        private readonly object _lock = new();
        private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _cpu = new();

        public ResourceSampler(Func<IEnumerable<(string Agent, int Pid)>> agents) { _agents = agents; }

        public (ProcessSample Runtime, List<ProcessSample> Agents) Sample(DateTime now)
        {
            lock (_lock)
            {
                ProcessSample runtime;
                using (var self = Process.GetCurrentProcess())
                    runtime = new ProcessSample
                    {
                        Pid = self.Id, CpuPct = CpuPercent(self, now), RssBytes = self.WorkingSet64, Threads = self.Threads.Count,
                        PrivateBytes = self.PrivateMemorySize64, GcHeapBytes = GC.GetTotalMemory(false),
                        GcCollections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
                    };
                var agents = new List<ProcessSample>();
                foreach (var (agent, pid) in _agents())
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        agents.Add(new ProcessSample { Agent = agent, Pid = pid, CpuPct = CpuPercent(p, now), RssBytes = p.WorkingSet64, Threads = p.Threads.Count });
                    }
                    catch (Exception) { _cpu.Remove(pid); }   // exited between listing and sampling
                }
                return (runtime, agents);
            }
        }

        private double? CpuPercent(Process p, DateTime now)
        {
            var cpu = p.TotalProcessorTime;
            double? pct = _cpu.TryGetValue(p.Id, out var last) && now > last.At
                ? Math.Round(100.0 * (cpu - last.Cpu).TotalSeconds / (now - last.At).TotalSeconds, 1) : null;
            _cpu[p.Id] = (cpu, now);
            return pct;
        }
    }
}
