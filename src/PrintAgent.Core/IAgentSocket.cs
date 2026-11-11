using PrintAgent.Common.Models;

namespace PrintAgent.Core;

// Transport seam: the production adapter keeps SocketIOClient 3.1.1 defaults.
public interface IAgentSocket : IDisposable
{
    bool IsConnected { get; }
    event Func<Task>? Connected;
    event Action? Disconnected;
    event Action<string>? Error;
    void On(string name, Func<IAgentSocketMessage, Task> handler);
    Task ConnectAsync();
    Task DisconnectAsync();
    Task EmitAsync(string name, object payload);
}

public interface IAgentSocketMessage
{
    T GetValue<T>();
    Task CallbackAsync(PrintJobResult result);
}
