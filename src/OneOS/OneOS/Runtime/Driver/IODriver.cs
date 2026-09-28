using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Common;

namespace OneOS.Runtime.Driver
{
    // A local I/O device a runtime exposes to the whole cluster (ported from OneOS-V5B). Each configured
    // device (Configuration.IO) gets a driver agent at <runtime URI>/io/<name>, published in Registry.IO.
    // Like VolumeDriver, a consumer anywhere asks for a stream over RPC (PrepareReadStream, with a pipe id)
    // and connects a raw pipe; the driver sends the device's bytes to every connected consumer. The device
    // runs only while someone reads it: it starts with the first consumer and stops after the last leaves.
    public abstract class IODriver : Agent
    {
        // Driver names (Configuration.IO[].Driver) to factories.
        private static readonly ConcurrentDictionary<string, Func<Runtime, IOConfiguration, ILoggerFactory, IODriver>> Factories = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ffmpeg"] = (runtime, config, lf) => new FfmpegReader(runtime, config, lf),
        };

        public static void Register(string driver, Func<Runtime, IOConfiguration, ILoggerFactory, IODriver> factory) => Factories[driver] = factory;

        public static IODriver? Create(Runtime runtime, IOConfiguration config, ILoggerFactory loggerFactory) =>
            Factories.TryGetValue(config.Driver, out var factory) ? factory(runtime, config, loggerFactory) : null;

        public static string UriOf(Runtime runtime, string name) => $"{runtime.Config.URI}/io/{name}";

        protected readonly Runtime _runtime;
        private readonly AgentRpc.Server _rpcServer;
        private readonly ConcurrentDictionary<string, Pipe> _consumers = new();
        private readonly SemaphoreSlim _lifecycle = new(1, 1);
        private CancellationTokenSource? _deviceCts;

        protected IODriver(Runtime runtime, IOConfiguration config, ILogger logger)
            : base(UriOf(runtime, config.Name), logger)
        {
            _runtime = runtime;
            Config = config;
            _rpcServer = new AgentRpc.Server(this);
        }

        public IOConfiguration Config { get; }
        public string Name => Config.Name;

        // What the device is ("video", …), for Registry.IO.
        public abstract string DeviceType { get; }

        public IOHandle Handle => new() { DeviceType = DeviceType, Driver = Config.Driver, HostRuntime = _runtime.Config.ID };

        public int ConsumerCount => _consumers.Count;
        public bool DeviceRunning => _deviceCts != null;

        // Starts reading the device; produce its data with PublishAsync until `ct` is cancelled.
        protected abstract Task StartDeviceAsync(CancellationToken ct);

        // Stops reading the device (after `ct` of StartDeviceAsync was cancelled).
        protected abstract Task StopDeviceAsync();

        // A consumer on any runtime: expect its raw pipe, then stream to it.
        [AgentRpc.Method("PrepareReadStream")]
        public void PrepareReadStream(string pipeId)
        {
            _logger.LogInformation("{Uri}: expecting read stream {PipeId}", URI, pipeId);
            _runtime.ExecutionManager.ExpectPipe(pipeId, socket => _ = AddConsumerAsync(pipeId, new RemoteRawOutputPipe(socket)));
        }

        // A consumer on this runtime can attach any output pipe directly.
        public async Task AddConsumerAsync(string id, Pipe pipe)
        {
            _consumers[id] = pipe;
            await _lifecycle.WaitAsync();
            try
            {
                if (_deviceCts == null)
                {
                    _deviceCts = new CancellationTokenSource();
                    _logger.LogInformation("{Uri}: starting the device", URI);
                    await StartDeviceAsync(_deviceCts.Token);
                }
            }
            finally { _lifecycle.Release(); }
        }

        public async Task RemoveConsumerAsync(string id)
        {
            if (_consumers.TryRemove(id, out var pipe))
                try { await pipe.CloseAsync(); } catch (Exception) { }
            await StopIfIdleAsync();
        }

        // Sends a chunk of device data to every consumer; one that can't take it any more is dropped.
        protected async Task PublishAsync(byte[] chunk)
        {
            foreach (var (id, pipe) in _consumers.ToList())
            {
                try { await pipe.Send(chunk); }
                catch (Exception ex)
                {
                    _logger.LogInformation("{Uri}: consumer {Consumer} is gone ({Error})", URI, id, ex.Message);
                    await RemoveConsumerAsync(id);
                }
            }
        }

        // The device ended by itself (e.g. its process exited): consumers see the end of their streams.
        protected async Task DeviceEndedAsync()
        {
            foreach (var id in _consumers.Keys.ToList())
                if (_consumers.TryRemove(id, out var pipe)) try { await pipe.CloseAsync(); } catch (Exception) { }
            await StopIfIdleAsync();
        }

        private async Task StopIfIdleAsync()
        {
            await _lifecycle.WaitAsync();
            try
            {
                if (_deviceCts == null || !_consumers.IsEmpty) return;
                _deviceCts.Cancel();
                _deviceCts = null;
                _logger.LogInformation("{Uri}: no consumers left; stopping the device", URI);
                await StopDeviceAsync();
            }
            finally { _lifecycle.Release(); }
        }

        protected override Task OnBeginAsync(CancellationToken ct) => Task.CompletedTask;
        protected override Task OnPauseAsync(CancellationToken ct) => Task.CompletedTask;

        protected override async Task OnEndAsync(CancellationToken ct)
        {
            await DeviceEndedAsync();
        }

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in Inbox.Reader.ReadAllAsync(ct))
                {
                    if (msg is Envelope env && env.Channel == "rpc"
                        && MessagePack.MessagePackSerializer.Deserialize<AgentRpc.RpcMessage>(env.Payload) is AgentRpc.RequestMessage request)
                        await _rpcServer.ProcessRequestAsync(env, request);
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
