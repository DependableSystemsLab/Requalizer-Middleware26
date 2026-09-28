using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOS.Common;
using OneOS.Runtime;

namespace OneOS.Runtime
{
    public partial class ExecutionManager : Agent, IPipeHost, IProcessFactory
    {
        private readonly Runtime _runtime;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ConcurrentDictionary<string, Agent> _managedAgents = new();
        private readonly ConcurrentDictionary<string, RemoteAgent> _remoteAgents = new();
        private readonly ConcurrentBag<RemoteRawInputPipe> _remoteInputs = new();
        private readonly ConcurrentBag<RemoteRawOutputPipe> _remoteOutputs = new();

        public ExecutionManager(Runtime runtime, ILoggerFactory loggerFactory, Agent? parent = null)
            : base($"{runtime.Config.URI}/ExecutionManager", loggerFactory.CreateLogger<ExecutionManager>(), parent)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        }

        public RemoteRawInputPipe CreateRemoteInput(TcpSocket socket)
        {
            var pipe = new RemoteRawInputPipe(socket);
            _remoteInputs.Add(pipe);
            return pipe;
        }

        public RemoteRawOutputPipe CreateRemoteOutput(TcpSocket socket)
        {
            var pipe = new RemoteRawOutputPipe(socket);
            _remoteOutputs.Add(pipe);
            return pipe;
        }

        private readonly ConcurrentDictionary<string, Action<TcpSocket>> _pendingPipes = new();
        private readonly ConcurrentDictionary<string, TcpSocket> _unhandledPipes = new();

        public void ExpectPipe(string pipeId, Action<TcpSocket> onSocketReady)
        {
            if (_unhandledPipes.TryRemove(pipeId, out var socket))
            {
                _logger.LogInformation("ExecutionManager: Fulfilled pipe {PipeId} immediately from unhandled cache.", pipeId);
                onSocketReady(socket);
            }
            else
            {
                _pendingPipes[pipeId] = onSocketReady;
                _logger.LogInformation("ExecutionManager: Expecting pipe {PipeId}", pipeId);
            }
        }

        public bool FulfillPipe(string pipeId, TcpSocket socket)
        {
            if (_pendingPipes.TryRemove(pipeId, out var onReady))
            {
                onReady(socket);
                _logger.LogInformation("ExecutionManager: Fulfilled pipe {PipeId}", pipeId);
                return true;
            }
            
            _logger.LogWarning("ExecutionManager: Received unhandled pipe {PipeId}, caching for 30 seconds.", pipeId);
            _unhandledPipes[pipeId] = socket;
            
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
            {
                if (_unhandledPipes.TryRemove(pipeId, out var unhandledSocket))
                {
                    _logger.LogError("ExecutionManager: Unhandled pipe {PipeId} expired.", pipeId);
                    unhandledSocket.StopAsync();
                }
            });
            
            return true;
        }

        private readonly ConcurrentDictionary<string, Action<System.Threading.Channels.Channel<byte[]>>> _pendingLocalPipes = new();
        private readonly ConcurrentDictionary<string, System.Threading.Channels.Channel<byte[]>> _unhandledLocalPipes = new();

        public void ExpectLocalPipe(string pipeId, Action<System.Threading.Channels.Channel<byte[]>> onChannelReady)
        {
            if (_unhandledLocalPipes.TryRemove(pipeId, out var channel))
            {
                _logger.LogInformation("ExecutionManager: Fulfilled local pipe {PipeId} immediately from unhandled cache.", pipeId);
                onChannelReady(channel);
            }
            else
            {
                _pendingLocalPipes[pipeId] = onChannelReady;
                _logger.LogInformation("ExecutionManager: Expecting local pipe {PipeId}", pipeId);
            }
        }

        public void FulfillLocalPipe(string pipeId, System.Threading.Channels.Channel<byte[]> channel)
        {
            if (_pendingLocalPipes.TryRemove(pipeId, out var onReady))
            {
                onReady(channel);
                _logger.LogInformation("ExecutionManager: Fulfilled local pipe {PipeId}", pipeId);
                return;
            }
            
            _logger.LogWarning("ExecutionManager: Received unhandled local pipe {PipeId}, caching for 30 seconds.", pipeId);
            _unhandledLocalPipes[pipeId] = channel;
            
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
            {
                if (_unhandledLocalPipes.TryRemove(pipeId, out var unhandledChannel))
                {
                    _logger.LogError("ExecutionManager: Unhandled local pipe {PipeId} expired.", pipeId);
                    unhandledChannel.Writer.TryComplete();
                }
            });
        }

        // --- IPipeHost: the receiver expects the inbound end; the sender creates the local channel or dials
        // the receiver's runtime (whose FulfillPipe hands the socket to the expecting side).

        public void ExpectInbound(string pipeId, PipeKind kind, bool local, Action<Pipe> onReady)
        {
            if (local) ExpectLocalPipe(pipeId, channel => onReady(kind == PipeKind.Message ? new LocalMessageInputPipe(channel) : new LocalRawInputPipe(channel)));
            else ExpectPipe(pipeId, socket => onReady(kind == PipeKind.Message ? new RemoteMessageInputPipe(socket) : new RemoteRawInputPipe(socket)));
        }

        public async Task<Pipe> OpenOutboundAsync(string pipeId, PipeKind kind, string receiverRuntimeId, bool local, CancellationToken ct)
        {
            if (local)
            {
                var channel = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
                FulfillLocalPipe(pipeId, channel);
                return kind == PipeKind.Message ? new LocalMessageOutputPipe(channel) : new LocalRawOutputPipe(channel);
            }
            return kind == PipeKind.Message
                ? await _runtime.EstablishRemoteMessageOutputPipe(receiverRuntimeId, pipeId).WaitAsync(ct)
                : await _runtime.EstablishRemoteRawOutputPipe(receiverRuntimeId, pipeId).WaitAsync(ct);
        }

        // --- IProcessFactory: graph agents run as ProcessAgents, through the language driver for their
        // command (node, python) or as plain processes. `ONEOS_` is added to environment keys by the agent.

        // The agent is the graph agent's Registry entry (committed with its graph instance); like other
        // local agents it is managed here and registered with the router, so `kill` reaches it.
        public IProcessEndpoint CreateProcess(string agentId, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> environment)
        {
            var owner = _runtime.Registry.Graphs.TryGetValue(Graphs.GraphUris.InstanceOf(agentId), out var record) ? record.Owner : "system";
            var uri = Graphs.GraphUris.Agent(owner, _runtime.Config.Domain, agentId);
            var registered = _runtime.Registry.Agents.GetValueOrDefault(uri);
            var info = new AgentInfo
            {
                URI = uri,
                GPID = registered?.GPID ?? 0,
                Runtime = _runtime.Config.ID,
                StandbyRuntime = registered?.StandbyRuntime ?? string.Empty,
                User = owner,
                Graph = Graphs.GraphUris.InstanceOf(agentId),
                ReplicaSet = registered?.ReplicaSet ?? string.Empty,
                BinaryPath = argv[0],
                Arguments = argv.Skip(1).ToList(),
                Environment = environment.ToDictionary(kv => kv.Key.StartsWith("ONEOS_") ? kv.Key["ONEOS_".Length..] : kv.Key, kv => kv.Value),
            };
            // Unlike shell-spawned agents, a graph agent's end is not a reason to delete its Registry entry:
            // its host reports the exit to GraphManager, whose controller replaces or fails it over. Its pipes
            // and start (line mode, possibly from a checkpoint) belong to its port adapter.
            var agent = CreateDriverAgent(uri, info);
            agent.Fsm.StateChanged += (_, e) =>
            {
                if (e.CurrentState is not (AgentState.Stopped or AgentState.Faulted)) return;
                // A newer incarnation may already be registered under the same URI.
                if (_managedAgents.TryGetValue(uri, out Agent? current) && current == agent && _managedAgents.TryRemove(uri, out Agent? _))
                    _runtime.GetRouter(1).UnregisterLocalAgent(uri);
            };
            return agent;
        }

        // Graph agents are hosted from their graph instance's plan (ExecutionManager.Graphs.cs), not from
        // their individual Registry entries; legacy pipelines also use AgentInfo.Graph, for their own ids.
        private bool IsGraphInstanceAgent(AgentInfo info) => info.Graph.Length > 0 && _runtime.Registry.Graphs.ContainsKey(info.Graph);

        // The process agents this runtime runs (profiling samples their OS processes).
        public IEnumerable<ProcessAgent> ProcessAgents => _managedAgents.Values.OfType<ProcessAgent>();

        public async Task<Agent?> WaitForAgentAsync(string uri, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (_managedAgents.TryGetValue(uri, out var agent))
                    {
                        return agent;
                    }
                    await Task.Delay(100, cts.Token);
                }
            }
            catch (TaskCanceledException) { }
            
            return null;
        }

        public async Task HandleAgentUpdateAsync(SetAgentAction action)
        {
            if (IsGraphInstanceAgent(action.Info)) return;
            if (action.Info.Runtime != _runtime.Config.ID)
            {
                if (!_remoteAgents.ContainsKey(action.Uri))
                {
                    _logger.LogInformation("ExecutionManager: Registering remote agent {Uri}...", action.Uri);
                    var remoteLogger = _loggerFactory.CreateLogger<RemoteAgent>();
                    var remoteAgent = new RemoteAgent(action.Uri, async msg =>
                    {
                        if (msg is Envelope env)
                        {
                            await _runtime.ForwardEnvelopeAsync(action.Info.Runtime, env, 1);
                        }
                    }, remoteLogger);

                    _remoteAgents.TryAdd(action.Uri, remoteAgent);
                    _runtime.GetRouter(1).RegisterRemoteAgent(remoteAgent);
                    await remoteAgent.StartAsync(default);
                }
                return;
            }

            if (!_managedAgents.ContainsKey(action.Uri))
            {
                _logger.LogInformation("ExecutionManager: Spawning new agent {Uri}...", action.Uri);
                if (action.Info.BinaryPath == "UserShell")
                {
                    await SpawnUserShellAsync(action.Uri, action.Info);
                }
                else
                {
                    await SpawnProcessAgentAsync(action.Uri, action.Info);
                }
            }
        }

        public async Task HandleAgentDeleteAsync(DeleteAgentAction action)
        {
            if (_managedAgents.TryGetValue(action.Uri, out var agent))
            {
                _logger.LogInformation("ExecutionManager: Killing agent {Uri}...", action.Uri);
                await agent.StopAsync();
                _managedAgents.TryRemove(action.Uri, out _);
                _runtime.GetRouter(1).UnregisterLocalAgent(action.Uri);
            }
            else if (_remoteAgents.TryGetValue(action.Uri, out var remoteAgent))
            {
                _logger.LogInformation("ExecutionManager: Killing remote agent {Uri}...", action.Uri);
                await remoteAgent.StopAsync();
                _remoteAgents.TryRemove(action.Uri, out _);
                _runtime.GetRouter(1).UnregisterRemoteAgent(action.Uri);
            }
        }

        public async Task SynchronizeWithRegistry()
        {
            await SynchronizeGraphsWithRegistry();
            foreach (var kvp in _runtime.Registry.Agents)
            {
                var uri = kvp.Key;
                var info = kvp.Value;
                if (IsGraphInstanceAgent(info)) continue;

                if (info.Runtime != _runtime.Config.ID)
                {
                    if (!_remoteAgents.ContainsKey(uri))
                    {
                        _logger.LogInformation("ExecutionManager: Synchronizing - Registering missing remote agent {Uri}...", uri);
                        var remoteLogger = _loggerFactory.CreateLogger<RemoteAgent>();
                        var remoteAgent = new RemoteAgent(uri, async msg =>
                        {
                            if (msg is Envelope env)
                            {
                                await _runtime.ForwardEnvelopeAsync(info.Runtime, env, 1);
                            }
                        }, remoteLogger);

                        _remoteAgents.TryAdd(uri, remoteAgent);
                        _runtime.GetRouter(1).RegisterRemoteAgent(remoteAgent);
                        await remoteAgent.StartAsync(default);
                    }
                    continue;
                }

                if (!_managedAgents.ContainsKey(uri))
                {
                    _logger.LogInformation("ExecutionManager: Synchronizing - Spawning missing agent {Uri}...", uri);
                    if (info.BinaryPath == "UserShell")
                    {
                        await SpawnUserShellAsync(uri, info);
                    }
                    else
                    {
                        await SpawnProcessAgentAsync(uri, info);
                    }
                }
            }
        }

        public async Task SpawnUserShellAsync(string uri, AgentInfo info)
        {
            var shellLogger = _loggerFactory.CreateLogger<UserShell>();
            var shell = new UserShell(_runtime, uri, info, shellLogger, null);
            
            _managedAgents.TryAdd(uri, shell);
            _runtime.GetRouter(1).RegisterLocalAgent(shell);

            shell.Fsm.StateChanged += async (sender, e) =>
            {
                if (e.CurrentState == AgentState.Stopped || e.CurrentState == AgentState.Faulted)
                {
                    _logger.LogInformation("ExecutionManager: User shell {Uri} terminated. Removing from registry...", uri);
                    try
                    {
                        // The shell's jobs end with it: every agent it started in this session (a pipeline's
                        // upstream agents too, not only the one printing to the shell). Graph agents outlive it; shells (C#) are left alone.
                        var jobs = _runtime.Registry.Agents.Values
                            .Where(a => a.Session == info.Session && a.URI != uri && string.IsNullOrEmpty(a.Graph) && a.Language != AgentInfo.LanguageInfo.CSharp)
                            .Select(a => a.URI).ToList();
                        foreach (var job in jobs)
                        {
                            try { await _runtime.ProcessManager.KillAgentAsync(job); }
                            catch (Exception ex) { _logger.LogDebug("Stopping {Uri} with its shell: {Error}", job, ex.Message); }
                        }

                        await _runtime.ProcessManager.KillAgentAsync(uri);
                        await _runtime.SessionManager.SignOutUser(info.Session);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to remove terminated shell {Uri} from registry.", uri);
                    }
                }
            };

            _logger.LogInformation("Spawned UserShell for {Username} at {Uri}", info.User, uri);
            _ = StartAgentSequentiallyAsync(shell, info);
        }

        // A process agent for `info`, not started: the language driver for its command (node, python) or a
        // plain process, managed here and registered with the router. Shared by shell-spawned agents
        // (SpawnProcessAgentAsync) and dataflow graph agents (CreateProcess), which differ in how they are
        // wired to pipes, started, and cleaned up.
        private ProcessAgent CreateDriverAgent(string uri, AgentInfo info)
        {
            ProcessAgent agent = info.BinaryPath switch
            {
                "node" => new Driver.JavaScriptAgent(_runtime, uri, info, _loggerFactory.CreateLogger<Driver.JavaScriptAgent>()),
                "python" or "python3" => new Driver.PythonAgent(_runtime, uri, info, _loggerFactory.CreateLogger<Driver.PythonAgent>()),
                _ => new ProcessAgent(_runtime, uri, info, _loggerFactory.CreateLogger<ProcessAgent>()),
            };
            _managedAgents[uri] = agent;
            _runtime.GetRouter(1).RegisterLocalAgent(agent);
            return agent;
        }

        // A shell-spawned agent (single process or pipeline stage): its Registry entry is removed when it
        // ends, and it is joined to its pipeline's raw pipes before it starts.
        public Task SpawnProcessAgentAsync(string uri, AgentInfo info)
        {
            var agent = CreateDriverAgent(uri, info);

            agent.Fsm.StateChanged += async (sender, e) =>
            {
                if (e.CurrentState == AgentState.Stopped || e.CurrentState == AgentState.Faulted)
                {
                    _logger.LogInformation("ExecutionManager: Agent {Uri} terminated. Removing from registry...", uri);
                    try
                    {
                        await _runtime.ProcessManager.KillAgentAsync(uri);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to remove terminated agent {Uri} from registry.", uri);
                    }
                }
            };

            _logger.LogInformation("Spawned {Type} at {Uri}", agent.GetType().Name, uri);
            _ = StartAgentSequentiallyAsync(agent, info);
            return Task.CompletedTask;
        }

        private async Task StartAgentSequentiallyAsync(Agent agent, AgentInfo info)
        {
            try
            {
                var outputPipes = new Dictionary<string, Pipe>();
                var outgoingPipes = _runtime.Registry.Pipes.Values.Where(p => p.Sources.Contains(info.URI)).ToList();

                if (outgoingPipes.Count > 0)
                {
                    _logger.LogInformation("ExecutionManager: Agent {Uri} waiting for {Count} downstream pipes...", info.URI, outgoingPipes.Count);
                    foreach (var pipe in outgoingPipes)
                    {
                        var downstreamRuntimeId = _runtime.Registry.Agents[pipe.Sinks[0]].Runtime;
                        if (downstreamRuntimeId == _runtime.Config.ID)
                        {
                            var tcs = new TaskCompletionSource<System.Threading.Channels.Channel<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
                            ExpectLocalPipe(pipe.Id, channel => tcs.TrySetResult(channel));
                            var channel = await tcs.Task;
                            outputPipes[pipe.Id] = Metered(new LocalRawOutputPipe(channel), pipe, Monitoring.PipeEnd.Out);
                        }
                        else
                        {
                            var tcs = new TaskCompletionSource<TcpSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
                            ExpectPipe(pipe.Id, socket => tcs.TrySetResult(socket));
                            var socket = await tcs.Task;
                            outputPipes[pipe.Id] = Metered(new RemoteRawOutputPipe(socket), pipe, Monitoring.PipeEnd.Out);
                        }
                    }
                    _logger.LogInformation("ExecutionManager: Agent {Uri} all downstream pipes fulfilled.", info.URI);
                }

                var inputPipes = new Dictionary<string, Pipe>();
                var incomingPipes = _runtime.Registry.Pipes.Values.Where(p => p.Sinks.Contains(info.URI)).ToList();
                if (incomingPipes.Count > 0)
                {
                    _logger.LogInformation("ExecutionManager: Agent {Uri} connecting to {Count} upstream pipes...", info.URI, incomingPipes.Count);
                    foreach (var pipe in incomingPipes)
                    {
                        var upstreamRuntimeId = _runtime.Registry.Agents[pipe.Sources[0]].Runtime;
                        if (upstreamRuntimeId == _runtime.Config.ID)
                        {
                            var channel = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
                            FulfillLocalPipe(pipe.Id, channel);
                            inputPipes[pipe.Id] = Metered(new LocalRawInputPipe(channel), pipe, Monitoring.PipeEnd.In);
                        }
                        else
                        {
                            var inputPipe = await _runtime.EstablishRemoteRawInputPipe(upstreamRuntimeId, pipe.Id);
                            inputPipes[pipe.Id] = Metered(inputPipe, pipe, Monitoring.PipeEnd.In);
                        }
                    }
                    _logger.LogInformation("ExecutionManager: Agent {Uri} all upstream pipes connected.", info.URI);
                }

                if (agent is ProcessAgent processAgent)
                {
                    if (inputPipes.Count > 0) processAgent.ConnectInputPipe(inputPipes.Values.First());
                    if (outputPipes.Count > 0) processAgent.OutputPipe = outputPipes.Values.First();
                }
                else if (agent is UserShell userShell)
                {
                    if (inputPipes.Count > 0) userShell.ConnectPipes(inputPipes.Values.First(), null);
                    if (outputPipes.Count > 0) userShell.ConnectPipes(null, outputPipes.Values.First());
                }

                await agent.StartAsync(default);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExecutionManager: Failed to start agent {Uri} sequentially.", info.URI);
            }
        }

        // Shell pipeline pipe ends are metered (chunks and bytes) for monitoring and profiling.
        private Pipe Metered(Pipe inner, PipeInfo pipe, Monitoring.PipeEnd end) =>
            new Monitoring.MeteredPipe(inner, _runtime.Metrics.Pipe(pipe.Id, end,
                new Monitoring.PipeMeta("shell", pipe.Graph, null, pipe.Sources.FirstOrDefault(), pipe.Sinks.FirstOrDefault(), "raw")));

        public async Task PauseAgentAsync(string uri)
        {
            if (_managedAgents.TryGetValue(uri, out var agent))
            {
                await agent.PauseAsync(default);
            }
        }

        public async Task StopAgentAsync(string uri)
        {
            if (_managedAgents.TryRemove(uri, out var agent))
            {
                await agent.StopAsync();
            }
        }

        public Task SnapshotAgentAsync(string uri)
        {
            _logger.LogWarning("SnapshotAgentAsync not implemented yet.");
            return Task.CompletedTask;
        }

        protected override Task OnBeginAsync(CancellationToken ct)
        {
            _logger.LogInformation("ExecutionManager starting...");
            _metricsTimer = new Timer(_ => _ = ReportGraphMetricsAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            return Task.CompletedTask;
        }

        protected override Task OnPauseAsync(CancellationToken ct)
        {
            _logger.LogInformation("ExecutionManager pausing...");
            return Task.CompletedTask;
        }

        protected override async Task OnEndAsync(CancellationToken ct)
        {
            _logger.LogInformation("ExecutionManager stopping... tearing down managed agents.");
            if (_metricsTimer != null) await _metricsTimer.DisposeAsync();
            await StopAllGraphsAsync();
            foreach (var agent in _managedAgents.Values)
            {
                // One agent failing to stop must not abort the runtime's shutdown (which stops Raft after this).
                try { await agent.StopAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "ExecutionManager: failed to stop agent {Uri}", agent.URI); }
            }
            _managedAgents.Clear();

            foreach (var agent in _remoteAgents.Values)
            {
                await agent.StopAsync();
            }
            _remoteAgents.Clear();
        }

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in Inbox.Reader.ReadAllAsync(ct))
                {
                    _logger.LogInformation("ExecutionManager received message.");
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful cancellation
            }
        }
    }
}
