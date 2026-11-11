using System.Net.Sockets;

namespace PrintAgent.Common.Local;

public interface ILocalAgentClient
{
    Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default);
}

public sealed class LocalAgentClient(string socketPath = LocalProtocol.DefaultSocketPath) : ILocalAgentClient
{
    public async Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await LocalProtocol.WriteAsync(stream, request, timeout.Token);
        return await LocalProtocol.ReadAsync<LocalResponse>(stream, timeout.Token);
    }
}
