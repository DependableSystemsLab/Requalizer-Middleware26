using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using OneOS.Runtime;

namespace OneOS.Common
{
    public static class AgentRpc
    {
        [AttributeUsage(AttributeTargets.Method)]
        public class MethodAttribute : Attribute
        {
            public string Name { get; }
            public MethodAttribute(string name) { Name = name; }
        }

        [MessagePackObject]
        [Union(0, typeof(RequestMessage))]
        [Union(1, typeof(ResponseMessage))]
        public abstract record RpcMessage { }

        [MessagePackObject]
        public record RequestMessage : RpcMessage
        {
            [Key(0)] public Guid TransactionId { get; set; }
            [Key(1)] public string Method { get; set; } = string.Empty;
            [Key(2)] public object[] Arguments { get; set; } = Array.Empty<object>();
        }

        [MessagePackObject]
        public record ResponseMessage : RpcMessage
        {
            [Key(0)] public Guid TransactionId { get; set; }
            [Key(1)] public bool IsError { get; set; }
            [Key(2)] public object? Result { get; set; }
            [Key(3)] public string? ErrorMessage { get; set; }
        }

        public class Client
        {
            private readonly Agent _owner;
            private readonly ConcurrentDictionary<Guid, (TaskCompletionSource<object?> Tcs, CancellationTokenRegistration Ctr)> _pendingRequests = new();

            public Client(Agent owner)
            {
                _owner = owner;
            }

            public async Task<T?> InvokeAsync<T>(string targetUri, string method, object[] args, TimeSpan timeout)
            {
                var txId = Guid.NewGuid();
                var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                
                var cts = new CancellationTokenSource(timeout);
                var ctr = cts.Token.Register(() => 
                {
                    if (_pendingRequests.TryRemove(txId, out var pending))
                    {
                        pending.Tcs.TrySetException(new TimeoutException($"RPC request {method} to {targetUri} timed out."));
                    }
                });

                _pendingRequests[txId] = (tcs, ctr);

                var req = new RequestMessage
                {
                    TransactionId = txId,
                    Method = method,
                    Arguments = args
                };

                var env = new Envelope
                {
                    MessageId = Guid.NewGuid(),
                    SenderAgentUri = _owner.URI,
                    Strategy = RoutingStrategy.Direct,
                    Target = targetUri,
                    Channel = "rpc",
                    Payload = MessagePackSerializer.Serialize<RpcMessage>(req)
                };

                if (_owner.OnMessageOut != null)
                {
                    await _owner.OnMessageOut(env);
                }

                var result = await tcs.Task;
                return result == null ? default : (T)result;
            }

            public void ProcessResponse(ResponseMessage response)
            {
                if (_pendingRequests.TryRemove(response.TransactionId, out var pending))
                {
                    pending.Ctr.Dispose();
                    
                    if (response.IsError)
                    {
                        pending.Tcs.TrySetException(new Exception(response.ErrorMessage ?? "Unknown RPC Error"));
                    }
                    else
                    {
                        pending.Tcs.TrySetResult(response.Result);
                    }
                }
            }
        }

        public class Server
        {
            private readonly Agent _owner;
            private readonly ConcurrentDictionary<string, Func<object, object[], object?>> _methods = new();

            public Server(Agent owner)
            {
                _owner = owner;
                RegisterMethods();
            }

            private void RegisterMethods()
            {
                var methods = _owner.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var method in methods)
                {
                    var attr = method.GetCustomAttribute<MethodAttribute>();
                    if (attr != null)
                    {
                        _methods[attr.Name] = CreateCompiledDelegate(method);
                    }
                }
            }

            private Func<object, object[], object?> CreateCompiledDelegate(MethodInfo methodInfo)
            {
                var instanceParam = Expression.Parameter(typeof(object), "instance");
                var argsParam = Expression.Parameter(typeof(object[]), "args");

                var castInstance = Expression.Convert(instanceParam, methodInfo.DeclaringType!);
                var parameters = methodInfo.GetParameters();
                var argExpressions = new Expression[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    var arrayAccess = Expression.ArrayIndex(argsParam, Expression.Constant(i));
                    argExpressions[i] = Expression.Convert(arrayAccess, parameters[i].ParameterType);
                }

                var methodCall = Expression.Call(castInstance, methodInfo, argExpressions);

                Expression returnExpression;
                if (methodInfo.ReturnType == typeof(void))
                {
                    var nullReturn = Expression.Constant(null, typeof(object));
                    returnExpression = Expression.Block(methodCall, nullReturn);
                }
                else if (typeof(Task).IsAssignableFrom(methodInfo.ReturnType))
                {
                    returnExpression = Expression.Convert(methodCall, typeof(object));
                }
                else
                {
                    returnExpression = Expression.Convert(methodCall, typeof(object));
                }

                var lambda = Expression.Lambda<Func<object, object[], object?>>(returnExpression, instanceParam, argsParam);
                return lambda.Compile();
            }

            public async Task ProcessRequestAsync(Envelope envelope, RequestMessage request)
            {
                var response = new ResponseMessage { TransactionId = request.TransactionId };
                
                try
                {
                    if (_methods.TryGetValue(request.Method, out var func))
                    {
                        var result = func(_owner, request.Arguments);
                        
                        if (result is Task taskResult)
                        {
                            await taskResult;
                            
                            var type = taskResult.GetType();
                            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                            {
                                response.Result = type.GetProperty("Result")?.GetValue(taskResult);
                            }
                        }
                        else
                        {
                            response.Result = result;
                        }
                    }
                    else
                    {
                        response.IsError = true;
                        response.ErrorMessage = $"Method {request.Method} not found.";
                    }
                }
                catch (Exception ex)
                {
                    response.IsError = true;
                    response.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
                }

                var resEnv = new Envelope
                {
                    MessageId = Guid.NewGuid(),
                    SenderAgentUri = _owner.URI,
                    Strategy = RoutingStrategy.Direct,
                    Target = envelope.SenderAgentUri,
                    Channel = "rpc",
                    Payload = MessagePackSerializer.Serialize<RpcMessage>(response)
                };

                if (_owner.OnMessageOut != null)
                {
                    await _owner.OnMessageOut(resEnv);
                }
            }
        }
    }
}
