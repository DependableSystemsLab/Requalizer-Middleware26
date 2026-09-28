using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OneOS.Common
{
    public class RemoteAgent : Agent
    {
        private readonly Func<IMessage, Task> _sendFunc;

        public RemoteAgent(string uri, Func<IMessage, Task> sendFunc, ILogger logger)
            : base(uri, logger)
        {
            _sendFunc = sendFunc;
        }

        protected override Task OnBeginAsync(CancellationToken ct) => Task.CompletedTask;
        protected override Task OnPauseAsync(CancellationToken ct) => Task.CompletedTask;
        protected override Task OnEndAsync(CancellationToken ct) => Task.CompletedTask;

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in Inbox.Reader.ReadAllAsync(ct))
                {
                    await _sendFunc(msg);
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
