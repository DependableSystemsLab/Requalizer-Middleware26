using System;
using System.Threading.Tasks;

namespace OneOS.Common
{
    public abstract class Pipe
    {
        protected Pipe? TargetPipe;
        protected Action<byte[]>? OnReceiveAction;

        public virtual void PipeTo(Pipe target)
        {
            TargetPipe = target;
            OnReceive(payload => target.Send(payload));
        }

        public virtual void OnReceive(Action<byte[]> action)
        {
            OnReceiveAction = action;
        }

        public abstract Task Send(byte[] payload);
        public abstract Task CloseAsync();
        public virtual Task StartListening() => Task.CompletedTask;
    }

    public abstract class MessagePipe : Pipe { }
    public abstract class RawPipe : Pipe { }

    public class RemoteMessageInputPipe : MessagePipe
    {
        private readonly TcpSocket _socket;

        public RemoteMessageInputPipe(TcpSocket socket)
        {
            _socket = socket;
        }

        public override Task StartListening()
        {
            return _socket.Listen(payload => OnReceiveAction?.Invoke(payload));
        }

        public override Task Send(byte[] payload) => throw new NotSupportedException("RemoteMessageInputPipe only receives data from the remote socket.");
        public override Task CloseAsync() => _socket.StopAsync();
    }

    public class RemoteMessageOutputPipe : MessagePipe
    {
        private readonly TcpSocket _socket;

        public RemoteMessageOutputPipe(TcpSocket socket)
        {
            _socket = socket;
        }

        public override Task Send(byte[] payload) => _socket.Send(payload);
        public override Task CloseAsync() => _socket.StopAsync();
    }

    public class RemoteRawInputPipe : RawPipe
    {
        private readonly TcpSocket _socket;

        public RemoteRawInputPipe(TcpSocket socket)
        {
            _socket = socket;
        }

        public override Task StartListening()
        {
            return _socket.ListenRaw(payload => OnReceiveAction?.Invoke(payload));
        }

        public override Task Send(byte[] payload) => throw new NotSupportedException("RemoteRawInputPipe only receives data from the remote socket.");
        public override Task CloseAsync() => _socket.StopAsync();
    }

    public class RemoteRawOutputPipe : RawPipe
    {
        private readonly TcpSocket _socket;

        public RemoteRawOutputPipe(TcpSocket socket)
        {
            _socket = socket;
        }

        public override Task Send(byte[] payload) => _socket.SendRaw(payload);
        public override Task CloseAsync() => _socket.StopAsync();
    }

    public class LocalRawInputPipe : RawPipe
    {
        private readonly System.Threading.Channels.Channel<byte[]> _channel;

        public LocalRawInputPipe(System.Threading.Channels.Channel<byte[]> channel)
        {
            _channel = channel;
        }

        public override async Task StartListening()
        {
            await foreach (var item in _channel.Reader.ReadAllAsync())
            {
                OnReceiveAction?.Invoke(item);
            }
        }

        public override Task Send(byte[] payload) => throw new NotSupportedException("LocalRawInputPipe only receives data.");
        public override Task CloseAsync() => Task.CompletedTask;
    }

    public class LocalRawOutputPipe : RawPipe
    {
        private readonly System.Threading.Channels.Channel<byte[]> _channel;

        public LocalRawOutputPipe(System.Threading.Channels.Channel<byte[]> channel)
        {
            _channel = channel;
        }

        public override async Task Send(byte[] payload)
        {
            await _channel.Writer.WriteAsync(payload);
        }

        public override Task CloseAsync()
        {
            _channel.Writer.TryComplete();
            return Task.CompletedTask;
        }
    }

    public class LocalMessageInputPipe : MessagePipe
    {
        private readonly System.Threading.Channels.Channel<byte[]> _channel;

        public LocalMessageInputPipe(System.Threading.Channels.Channel<byte[]> channel)
        {
            _channel = channel;
        }

        public override async Task StartListening()
        {
            await foreach (var item in _channel.Reader.ReadAllAsync())
            {
                OnReceiveAction?.Invoke(item);
            }
        }

        public override Task Send(byte[] payload) => throw new NotSupportedException("LocalMessageInputPipe only receives data.");
        public override Task CloseAsync() => Task.CompletedTask;
    }

    public class LocalMessageOutputPipe : MessagePipe
    {
        private readonly System.Threading.Channels.Channel<byte[]> _channel;

        public LocalMessageOutputPipe(System.Threading.Channels.Channel<byte[]> channel)
        {
            _channel = channel;
        }

        public override async Task Send(byte[] payload)
        {
            await _channel.Writer.WriteAsync(payload);
        }

        public override Task CloseAsync()
        {
            _channel.Writer.TryComplete();
            return Task.CompletedTask;
        }
    }
}
