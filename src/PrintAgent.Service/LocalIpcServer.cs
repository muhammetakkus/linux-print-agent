using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintAgent.Common.Local;
using PrintAgent.Common.Security;

namespace PrintAgent.Service;

public sealed class LocalIpcServer(AgentOptions options, LocalAgentController controller, ILogger<LocalIpcServer> logger) : BackgroundService
{
    private Socket? _listener;
    private readonly SemaphoreSlim _slots = new(options.MaxLocalClients, options.MaxLocalClients);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Task> _clients = new();
    private long _next;
    private string PathName => Path.Combine(options.RuntimeDirectory, "control.sock");
    public override Task StartAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(options.RuntimeDirectory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(options.RuntimeDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        if (File.Exists(PathName)) File.Delete(PathName);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(PathName));
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(PathName,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        _listener.Listen(options.MaxLocalClients);
        return base.StartAsync(ct);
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _slots.WaitAsync(ct);
                Socket socket;
                try { socket = await _listener!.AcceptAsync(ct); }
                catch { _slots.Release(); throw; }
                var id = Interlocked.Increment(ref _next);
                var task = ServeAsync(socket, ct);
                _clients[id] = task;
                _ = task.ContinueWith(_ => { _clients.TryRemove(id, out var ignored); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private async Task ServeAsync(Socket socket, CancellationToken hostToken)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(hostToken))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(options.LocalRequestTimeoutSeconds));
            Task monitor = Task.CompletedTask;
            LocalRequest? request = null;
            try
            {
                using var read = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                read.CancelAfter(TimeSpan.FromSeconds(15));
                request = await LocalProtocol.ReadAsync<LocalRequest>(stream, read.Token, 16 * 1024);
                monitor = MonitorDisconnectAsync(socket, deadline);
                LocalResponse response;
                try { response = await controller.ExecuteAsync(request, deadline.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { response = new() { Success = false, Error = SafeText.Limit(ex.Message) }; }
                using var write = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                write.CancelAfter(TimeSpan.FromSeconds(5));
                try { await LocalProtocol.WriteAsync(stream, response, write.Token); }
                catch (InvalidDataException)
                {
                    await LocalProtocol.WriteAsync(stream, new LocalResponse { Success = false, Error = "Response exceeds the size limit. Request a smaller page." }, write.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or System.Text.Json.JsonException)
            { logger.LogDebug("Local client disconnected or sent invalid data."); }
            finally
            {
                if (request != null) request.Password = null;
                deadline.Cancel(); await monitor;
                _slots.Release();
            }
        }
    }
    private static async Task MonitorDisconnectAsync(Socket socket, CancellationTokenSource cancellation)
    {
        try { await socket.ReceiveAsync(new byte[1], SocketFlags.Peek, cancellation.Token); cancellation.Cancel(); }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        _listener?.Dispose();
        await Task.WhenAll(_clients.Values).WaitAsync(ct);
        if (File.Exists(PathName)) File.Delete(PathName);
    }
    public override void Dispose() { base.Dispose(); _listener?.Dispose(); }
}
