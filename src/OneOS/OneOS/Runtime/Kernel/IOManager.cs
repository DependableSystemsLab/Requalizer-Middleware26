using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Common;
using OneOS.Runtime.Driver;

namespace OneOS.Runtime.Kernel
{
    // I/O devices across the cluster (tier-1; ported from OneOS-V5B): this runtime's drivers are published
    // in Registry.IO, and a device anywhere is read through a raw pipe from its driver (IODriver).
    public sealed class IOManager : Agent
    {
        private readonly Runtime _runtime;
        private readonly AgentRpc.Client _rpcClient;
        private readonly AgentRpc.Server _rpcServer;
        private readonly List<IODriver> _local = new();

        public IOManager(Runtime runtime, ILogger<IOManager> logger)
            : base($"{runtime.Config.URI}/IOManager", logger)
        {
            _runtime = runtime;
            _rpcClient = new AgentRpc.Client(this);
            _rpcServer = new AgentRpc.Server(this);
        }

        public IReadOnlyList<IODriver> LocalDrivers => _local;

        internal void AddLocalDriver(IODriver driver) => _local.Add(driver);

        // A device by its driver URI, "<runtime id>/<name>", or "<name>" when only one runtime has one by that name.
        public (string Uri, IOHandle Handle)? Resolve(string device)
        {
            var io = _runtime.Registry.IO;
            if (io.TryGetValue(device, out var exact)) return (device, exact);
            var parts = device.Split('/', 2);
            var matches = io.Where(kv => parts.Length == 2
                    ? kv.Value.HostRuntime == parts[0] && kv.Key.EndsWith("/io/" + parts[1], StringComparison.Ordinal)
                    : kv.Key.EndsWith("/io/" + device, StringComparison.Ordinal)).ToList();
            return matches.Count == 1 ? (matches[0].Key, matches[0].Value) : null;
        }

        // Opens a read stream from a device on any runtime. The pipe isn't listening yet: set OnReceive, then
        // StartListening; it ends when the device stops or the driver drops the stream. A device on this
        // runtime streams through an in-process channel; one elsewhere through a raw pipe from its runtime,
        // whose IOManager hands it to the driver.
        public async Task<Pipe> OpenReadStreamAsync(string device, CancellationToken ct = default)
        {
            var (uri, handle) = Resolve(device) ?? throw new FileNotFoundException($"no I/O device '{device}'");
            if (handle.HostRuntime == _runtime.Config.ID)
            {
                var driver = _local.FirstOrDefault(d => d.URI == uri) ?? throw new FileNotFoundException($"I/O device '{device}' isn't running here");
                var channel = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
                await driver.AddConsumerAsync(Guid.NewGuid().ToString("N"), new LocalRawOutputPipe(channel));
                return new ClosingInputPipe(channel);
            }
            var pipeId = Guid.NewGuid().ToString("N");
            var owner = $"{handle.HostRuntime}.{_runtime.Config.Domain}/IOManager";
            await _rpcClient.InvokeAsync<object>(owner, "PrepareDeviceReadStream", new object[] { uri, pipeId }, TimeSpan.FromSeconds(30));
            return await _runtime.EstablishRemoteRawInputPipe(handle.HostRuntime, pipeId).WaitAsync(ct);
        }

        // A consumer on another runtime asks for a read stream from one of this runtime's devices.
        [AgentRpc.Method("PrepareDeviceReadStream")]
        public void PrepareDeviceReadStream(string deviceUri, string pipeId)
        {
            var driver = _local.FirstOrDefault(d => d.URI == deviceUri) ?? throw new FileNotFoundException($"no I/O device {deviceUri} on {_runtime.Config.ID}");
            driver.PrepareReadStream(pipeId);
        }

        // Publishes this runtime's devices, and removes entries for devices it no longer has (retrying until
        // a leader takes the updates).
        public async Task PublishLocalDevicesAsync(CancellationToken ct)
        {
            for (int attempt = 0; attempt < 60 && !ct.IsCancellationRequested; attempt++)
            {
                var actions = new List<RegistryAction>();
                foreach (var d in _local)
                {
                    var handle = d.Handle;
                    if (!_runtime.Registry.IO.TryGetValue(d.URI, out var known) || known.DeviceType != handle.DeviceType || known.Driver != handle.Driver || known.HostRuntime != handle.HostRuntime)
                        actions.Add(new SetIODeviceAction { Uri = d.URI, Handle = handle });
                }
                foreach (var (uri, handle) in _runtime.Registry.IO.Where(kv => kv.Value.HostRuntime == _runtime.Config.ID).ToList())
                    if (_local.All(d => d.URI != uri)) actions.Add(new DeleteIODeviceAction { Uri = uri });
                if (actions.Count == 0) return;
                try
                {
                    if (await _runtime.UpdateRegistryAsync(new TransactionAction { Actions = actions }, ct))
                    {
                        _logger.LogInformation("Published {Count} I/O device update(s)", actions.Count);
                        return;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _logger.LogDebug("Publishing I/O devices failed: {Error}", ex.Message); }
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { return; }
            }
        }

        // A local consumer's end: closing it closes the channel, so the driver's next send fails and it drops
        // the consumer (and stops the device when it was the last).
        private sealed class ClosingInputPipe : LocalRawInputPipe
        {
            private readonly System.Threading.Channels.Channel<byte[]> _channel;
            public ClosingInputPipe(System.Threading.Channels.Channel<byte[]> channel) : base(channel) { _channel = channel; }
            public override Task CloseAsync() { _channel.Writer.TryComplete(); return Task.CompletedTask; }
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
                    if (msg is not Envelope { Channel: "rpc" } env) continue;
                    switch (MessagePack.MessagePackSerializer.Deserialize<AgentRpc.RpcMessage>(env.Payload))
                    {
                        case AgentRpc.ResponseMessage response: _rpcClient.ProcessResponse(response); break;
                        case AgentRpc.RequestMessage request: await _rpcServer.ProcessRequestAsync(env, request); break;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
