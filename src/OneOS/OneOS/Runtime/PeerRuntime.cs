using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MessagePack;
using OneOS.Common;

namespace OneOS.Runtime
{
    public class PeerRuntime
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RuntimeMessage>> _pendingRequests = new();
        public SemaphoreSlim ConnectionSemaphore { get; } = new SemaphoreSlim(1, 1);

        public string Id { get; }
        
        public ConcurrentDictionary<int, TcpSocket> ActiveSockets { get; } = new();
        public ConcurrentDictionary<int, TcpSocket> PassiveSockets { get; } = new();

        public bool IsHealthy => ActiveSockets.ContainsKey(0) && PassiveSockets.ContainsKey(0);

        public Func<RuntimeMessage, int, Task<RuntimeMessage?>>? RequestHandler { get; set; }

        public PeerRuntime(string id, ILogger logger)
        {
            Id = id;
            _logger = logger;
        }

        public void DetachActiveSocket(int linkIndex = 0) 
        {
            if (ActiveSockets.TryRemove(linkIndex, out _))
            {
                _logger.LogInformation("Peer [{Id}] active socket {LinkIndex} detached. Healthy: {Healthy}", Id, linkIndex, IsHealthy);
            }
        }

        public void AttachActiveSocket(TcpSocket socket, int linkIndex = 0)
        {
            ActiveSockets[linkIndex] = socket;
            _logger.LogInformation("Peer [{Id}] active socket {LinkIndex} attached. Healthy: {Healthy}", Id, linkIndex, IsHealthy);
        }

        public void DetachPassiveSocket(int linkIndex = 0) 
        {
            if (PassiveSockets.TryRemove(linkIndex, out _))
            {
                _logger.LogInformation("Peer [{Id}] passive socket {LinkIndex} detached. Healthy: {Healthy}", Id, linkIndex, IsHealthy);
            }
        }

        public void AttachPassiveSocket(TcpSocket socket, int linkIndex = 0)
        {
            PassiveSockets[linkIndex] = socket;
            _logger.LogInformation("Peer [{Id}] passive socket {LinkIndex} attached. Healthy: {Healthy}", Id, linkIndex, IsHealthy);
        }

        public void RegisterPendingRequest(Guid messageId, TaskCompletionSource<RuntimeMessage> tcs)
        {
            _pendingRequests.TryAdd(messageId, tcs);
        }

        public async Task SendAsync(RuntimeMessage message, int linkIndex = 0, CancellationToken ct = default)
        {
            if (!ActiveSockets.TryGetValue(linkIndex, out var targetSocket))
                throw new InvalidOperationException($"Cannot send message. Active socket for LinkIndex {linkIndex} to Peer [{Id}] is not attached.");

            var payload = MessagePackSerializer.Serialize(message);
            try 
            {
                await targetSocket.Send(payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send message to Peer [{Id}] on LinkIndex {LinkIndex}. Detaching socket.", Id, linkIndex);
                DetachActiveSocket(linkIndex);
                throw;
            }
        }

        public async Task<RuntimeMessage> RequestAsync(RuntimeMessage request, int linkIndex = 0, CancellationToken ct = default)
        {
            if (request.MessageId == Guid.Empty)
                throw new ArgumentException("Request must have a valid MessageId.", nameof(request));

            var tcs = new TaskCompletionSource<RuntimeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_pendingRequests.TryAdd(request.MessageId, tcs))
                throw new InvalidOperationException($"A request with ID {request.MessageId} is already pending.");

            try
            {
                await SendAsync(request, linkIndex, ct).ConfigureAwait(false);

                await using (ct.Register(() => tcs.TrySetCanceled(ct)))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                // A request that timed out or failed to send must not leave its entry behind.
                _pendingRequests.TryRemove(request.MessageId, out _);
            }
        }

        public void HandleIncomingMessage(RuntimeMessage message, int linkIndex = 0)
        {
            if (message.MessageId != Guid.Empty && _pendingRequests.TryRemove(message.MessageId, out var tcs))
            {
                tcs.TrySetResult(message);
                return;
            }

            if (RequestHandler is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var response = await RequestHandler(message, linkIndex).ConfigureAwait(false);
                        if (response != null)
                        {
                            if (ActiveSockets.ContainsKey(linkIndex))
                            {
                                await SendAsync(response, linkIndex).ConfigureAwait(false);
                            }
                            else
                            {
                                _logger.LogWarning("Cannot send response {MessageId} to {Id} because ActiveSocket for LinkIndex {LinkIndex} is null.", response.MessageId, Id, linkIndex);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error handling request {MessageId} from {Id} on LinkIndex {LinkIndex}", message.MessageId, Id, linkIndex);
                    }
                });
            }
            else
            {
                _logger.LogWarning("No RequestHandler registered. Dropping message {MessageId} from {Id}", message.MessageId, Id);
            }
        }
    }
}
