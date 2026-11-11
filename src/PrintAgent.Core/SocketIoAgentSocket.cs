using PrintAgent.Common.Models;
using SocketIOClient;

namespace PrintAgent.Core;

public sealed class SocketIoAgentSocket : IAgentSocket
{
    private readonly SocketIOClient.SocketIO _socket;
    public bool IsConnected => _socket.Connected;
    public event Func<Task>? Connected;
    public event Action? Disconnected;
    public event Action<string>? Error;

    public SocketIoAgentSocket(string url, SocketIOOptions options)
    {
        _socket = new SocketIOClient.SocketIO(url, options);
        _socket.OnConnected += async (_, _) =>
        {
            if (Connected != null) await Connected();
        };
        _socket.OnDisconnected += (_, _) => Disconnected?.Invoke();
        _socket.OnError += (_, error) => Error?.Invoke(error);
    }

    public void On(string name, Func<IAgentSocketMessage, Task> handler) =>
        _socket.On(name, async response => await handler(new IncomingMessage(response)));
    public Task ConnectAsync() => _socket.ConnectAsync();
    public Task DisconnectAsync() => _socket.DisconnectAsync();
    public Task EmitAsync(string name, object payload) => _socket.EmitAsync(name, payload);
    public void Dispose() => _socket.Dispose();

    private sealed class IncomingMessage(SocketIOResponse response) : IAgentSocketMessage
    {
        public T GetValue<T>() => response.GetValue<T>();
        public Task CallbackAsync(PrintJobResult result) => response.CallbackAsync(result);
    }
}
