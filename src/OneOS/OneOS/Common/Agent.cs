using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OneOS.Common
{
    public enum AgentState
    {
        Created,
        Running,
        Paused,
        Stopped,
        Faulted
    }

    public enum AgentTrigger
    {
        Start,
        Pause,
        Stop,
        Fault
    }

    public abstract class Agent
    {
        public Agent? Parent { get; private set; }
        public List<Agent> Children { get; private set; }
        
        public Channel<IMessage> Inbox { get; protected set; }
        public Func<IMessage, Task>? OnMessageOut { get; set; }
        public Func<IMessage, Task>? OnMessageErr { get; set; }
        
        public string URI { get; protected set; }
        public StateMachine<AgentState, AgentTrigger> Fsm { get; }

        protected CancellationTokenSource? Cts;
        protected ILogger _logger;

        protected Agent(string uri, ILogger logger, Agent? parent = null)
        {
            URI = uri;
            _logger = logger;
            Parent = parent;
            Children = new List<Agent>();
            
            if (parent != null) 
            {
                parent.Children.Add(this);
            }

            var channelOptions = new UnboundedChannelOptions { SingleReader = true };
            Inbox = Channel.CreateUnbounded<IMessage>(channelOptions);

            Fsm = new StateMachine<AgentState, AgentTrigger>(AgentState.Created);
            ConfigureFsm();
        }

        protected Task? _runLoopTask;

        private void ConfigureFsm()
        {
            Fsm.AddTransition(AgentState.Created, AgentTrigger.Start, AgentState.Running);
            Fsm.AddTransition(AgentState.Running, AgentTrigger.Pause, AgentState.Paused);
            Fsm.AddTransition(AgentState.Paused, AgentTrigger.Start, AgentState.Running);
            Fsm.AddTransition(AgentState.Running, AgentTrigger.Stop, AgentState.Stopped);
            Fsm.AddTransition(AgentState.Paused, AgentTrigger.Stop, AgentState.Stopped);
            Fsm.AddTransition(AgentState.Created, AgentTrigger.Stop, AgentState.Stopped);
            
            Fsm.AddTransition(AgentState.Running, AgentTrigger.Fault, AgentState.Faulted);
            Fsm.AddTransition(AgentState.Paused, AgentTrigger.Fault, AgentState.Faulted);
            Fsm.AddTransition(AgentState.Created, AgentTrigger.Fault, AgentState.Faulted);
            Fsm.AddTransition(AgentState.Faulted, AgentTrigger.Stop, AgentState.Stopped);
            Fsm.AddTransition(AgentState.Faulted, AgentTrigger.Start, AgentState.Running);

            Fsm.StateChanged += (sender, e) => 
            {
                _logger.LogInformation("Agent [{URI}] state changed: {From} -> {To}", URI, e.PreviousState, e.CurrentState);
            };
        }

        public virtual async Task StartAsync(CancellationToken ct = default)
        {
            if (Fsm.CurrentState == AgentState.Running)
            {
                _logger.LogWarning("Agent [{URI}] StartAsync was called, but the agent is already running.", URI);
                return;
            }

            if (Fsm.CurrentState != AgentState.Created && Fsm.CurrentState != AgentState.Paused && Fsm.CurrentState != AgentState.Faulted)
                throw new InvalidOperationException($"Cannot start Agent from state {Fsm.CurrentState}");

            Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            await OnBeginAsync(Cts.Token).ConfigureAwait(false);
            
            Fsm.FireStrict(AgentTrigger.Start);

            foreach (var child in Children)
            {
                await child.StartAsync(Cts.Token).ConfigureAwait(false);
            }

            // Concrete classes will implement their own async loop or reading semantics
            _runLoopTask = RunLoopAsync(Cts.Token);
            
            _ = _runLoopTask.ContinueWith(t => 
            {
                if (t.IsFaulted)
                {
                    _logger.LogError(t.Exception, "Agent [{URI}] run loop faulted.", URI);
                    if (Fsm.CanFire(AgentTrigger.Fault))
                    {
                        Fsm.FireStrict(AgentTrigger.Fault);
                    }
                }
            }, TaskScheduler.Default);
        }

        public virtual async Task PauseAsync(CancellationToken ct = default)
        {
            if (Fsm.CurrentState != AgentState.Running)
                throw new InvalidOperationException($"Cannot pause Agent from state {Fsm.CurrentState}");

            await OnPauseAsync(ct).ConfigureAwait(false);

            Fsm.FireStrict(AgentTrigger.Pause);

            foreach (var child in Children)
            {
                await child.PauseAsync(ct).ConfigureAwait(false);
            }
        }

        public virtual async Task StopAsync(CancellationToken ct = default)
        {
            if (Fsm.CurrentState == AgentState.Stopped) return;

            foreach (var child in Children)
            {
                await child.StopAsync(ct).ConfigureAwait(false);
            }

            Cts?.Cancel();
            
            if (_runLoopTask != null)
            {
                try
                {
                    await _runLoopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Agent [{URI}] run loop threw exception during stop.", URI);
                }
            }

            await OnEndAsync(ct).ConfigureAwait(false);

            Fsm.FireStrict(AgentTrigger.Stop);

            Inbox.Writer.TryComplete();
        }

        protected abstract Task OnBeginAsync(CancellationToken ct);
        protected abstract Task OnPauseAsync(CancellationToken ct);
        protected abstract Task OnEndAsync(CancellationToken ct);
        
        /// <summary>
        /// Concrete subclasses should implement their message polling or reading logic here.
        /// </summary>
        protected abstract Task RunLoopAsync(CancellationToken ct);
    }
}
