using System;
using System.Threading;
using System.Threading.Tasks;

namespace OneOS.Common
{
    // Runs a loop that blocks on its own background thread instead of the thread pool. On Linux, .NET has no
    // truly asynchronous file I/O: an "async" read on a FIFO or other file handle holds a pool thread until data
    // arrives, which may be never. A runtime keeps a few such readers per process agent (stdout, stderr, extra
    // ports, the oneos.js IPC channels), so a graph with a dozen agents parked enough pool threads to stall the
    // whole runtime for seconds, Raft included. Blocking reads on dedicated threads cost one idle thread each and
    // leave the pool alone.
    public static class DedicatedThread
    {
        // Starts `body` on a new background thread; the task completes (or faults) when `body` returns (or throws).
        public static Task Run(string name, Action body)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { body(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
            })
            { IsBackground = true, Name = name };
            thread.Start();
            return done.Task;
        }
    }
}
