using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OneOS.Common;

namespace OneOS.Runtime
{
    public class Router
    {
        private readonly Runtime _runtime;
        private readonly ILogger<Router> _logger;
        private readonly int _linkIndex;
        
        private readonly ConcurrentDictionary<string, ChannelWriter<IMessage>> _localInboxes = new();
        private readonly ConcurrentDictionary<string, ChannelWriter<IMessage>> _remoteInboxes = new();
        
        public Router(Runtime runtime, ILoggerFactory loggerFactory, int linkIndex = 0)
        {
            _runtime = runtime;
            _logger = loggerFactory.CreateLogger<Router>();
            _linkIndex = linkIndex;
        }

        public void RegisterLocalAgent(Agent agent)
        {
            _localInboxes[agent.URI] = agent.Inbox;
            
            agent.OnMessageOut = async (msg) => 
            {
                if (msg is Envelope env)
                {
                    await RouteAsync(env, _runtime.Config.ID);
                }
            };
            
            agent.OnMessageErr = async (msg) => 
            {
                if (msg is Envelope env)
                {
                    await RouteAsync(env, _runtime.Config.ID);
                }
            };
            
            _logger.LogInformation("Local agent {AgentUri} registered with Router on LinkIndex {LinkIndex}.", agent.URI, _linkIndex);
        }

        public void UnregisterLocalAgent(string agentUri)
        {
            if (_localInboxes.TryRemove(agentUri, out _))
            {
                _logger.LogInformation("Local agent {AgentUri} unregistered from Router on LinkIndex {LinkIndex}.", agentUri, _linkIndex);
            }
        }

        public void RegisterRemoteAgent(Agent agent)
        {
            _remoteInboxes[agent.URI] = agent.Inbox;
            _logger.LogInformation("Remote agent {AgentUri} registered with Router on LinkIndex {LinkIndex}.", agent.URI, _linkIndex);
        }

        public void UnregisterRemoteAgent(string agentUri)
        {
            if (_remoteInboxes.TryRemove(agentUri, out _))
            {
                _logger.LogInformation("Remote agent {AgentUri} unregistered from Router on LinkIndex {LinkIndex}.", agentUri, _linkIndex);
            }
        }

        public async Task RouteAsync(Envelope envelope, string senderRuntimeId)
        {
            if (envelope.Strategy == RoutingStrategy.Direct)
            {
                await RouteDirectAsync(envelope, senderRuntimeId);
            }
            else if (envelope.Strategy == RoutingStrategy.Topic)
            {
                await RouteTopicAsync(envelope, senderRuntimeId);
            }
        }

        private async Task RouteDirectAsync(Envelope envelope, string senderRuntimeId)
        {
            if (_localInboxes.TryGetValue(envelope.Target, out var localInbox))
            {
                _logger.LogDebug("Routing Direct message to local agent {Target}", envelope.Target);
                await localInbox.WriteAsync(envelope);
            }
            else if (_remoteInboxes.TryGetValue(envelope.Target, out var remoteInbox))
            {
                _logger.LogDebug("Routing Direct message to remote agent {Target}", envelope.Target);
                await remoteInbox.WriteAsync(envelope);
            }
            else
            {
                // If this is from a remote node and we don't have it, we drop it.
                if (senderRuntimeId != _runtime.Config.ID)
                {
                    _logger.LogWarning("Received Direct message for {Target} from {SenderId}, but agent is not local.", envelope.Target, envelope.SenderId);
                    return;
                }

                // Case 2: Local agent sends to remote agent
                if (_runtime.Registry.Agents.TryGetValue(envelope.Target, out var agentInfo))
                {
                    if (agentInfo.Runtime != _runtime.Config.ID)
                    {
                        _logger.LogDebug("Forwarding Direct message to remote runtime {Runtime} for {Target} on LinkIndex {LinkIndex}", agentInfo.Runtime, envelope.Target, _linkIndex);
                        await _runtime.ForwardEnvelopeAsync(agentInfo.Runtime, envelope, _linkIndex);
                    }
                    else
                    {
                        _logger.LogWarning("Agent {Target} is assigned to local runtime but no inbox is registered.", envelope.Target);
                    }
                }
                else
                {
                    _logger.LogWarning("Agent {Target} not found in Registry.", envelope.Target);
                }
            }
        }

        private async Task RouteTopicAsync(Envelope envelope, string senderRuntimeId)
        {
            var isLocalOrigin = senderRuntimeId == _runtime.Config.ID;

            // Find all agents subscribed to this topic
            if (_runtime.Registry.TopicSubscribers.TryGetValue(envelope.Target, out var subscribers))
            {
                // We must group subscribers by Runtime
                var runtimesToForward = new HashSet<string>();

                foreach (var subscriberUri in subscribers)
                {
                    if (_runtime.Registry.Agents.TryGetValue(subscriberUri, out var agentInfo))
                    {
                        // Case 4 & 5: Deliver to local subscribers
                        if (agentInfo.Runtime == _runtime.Config.ID)
                        {
                            if (_localInboxes.TryGetValue(subscriberUri, out var localInbox))
                            {
                                await localInbox.WriteAsync(envelope);
                            }
                        }
                        else if (isLocalOrigin) // Case 4: Forward to remote runtimes (only if local origin)
                        {
                            runtimesToForward.Add(agentInfo.Runtime);
                        }
                    }
                }

                if (isLocalOrigin)
                {
                    foreach (var remoteRuntimeId in runtimesToForward)
                    {
                        _logger.LogDebug("Forwarding Topic message to remote runtime {Runtime} for Topic {Target} on LinkIndex {LinkIndex}", remoteRuntimeId, envelope.Target, _linkIndex);
                        await _runtime.ForwardEnvelopeAsync(remoteRuntimeId, envelope, _linkIndex);
                    }
                }
            }
            else
            {
                _logger.LogDebug("No subscribers found for Topic {Target}", envelope.Target);
            }
        }
    }
}
