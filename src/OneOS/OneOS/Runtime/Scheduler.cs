using System;
using System.Linq;

namespace OneOS.Runtime
{
    public interface IClusterProvider
    {
        ClusterSnapshot TakeSnapshot();
    }

    // Placement for shell-spawned processes and pipelines (ProcessManager): a random live host.
    // Dataflow graphs are placed by OneOS.Runtime.Scheduling.GraphScheduler instead.
    public class Scheduler
    {
        private readonly IClusterProvider _cluster;

        public Scheduler(IClusterProvider cluster)
        {
            _cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));
        }

        public void AssignRuntime(AgentInfo info)
        {
            var hosts = _cluster.TakeSnapshot().Hosts.Where(h => h.Alive).Select(h => h.HostId).ToList();
            if (hosts.Count == 0) throw new InvalidOperationException("no live host to run the agent on");
            info.Runtime = hosts[Random.Shared.Next(hosts.Count)];
        }
    }
}
