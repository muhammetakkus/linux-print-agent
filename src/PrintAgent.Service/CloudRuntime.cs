using PrintAgent.Core;

namespace PrintAgent.Service;

// Host lifecycle only: mirrors Windows Stop -> Logout -> Start without stopping the IPC server.
public sealed class CloudRuntime(CloudConnection connection) : IDisposable
{
    private CancellationToken _hostToken;
    private CancellationTokenSource? _cancellation;
    private Task _task = Task.CompletedTask;
    private readonly SemaphoreSlim _control = new(1, 1);
    private bool _started;

    public void Start(CancellationToken hostToken)
    {
        _hostToken = hostToken;
        _started = true;
        Resume();
    }
    public void Resume()
    {
        _control.Wait();
        try
        {
            if (!_started || _cancellation != null || _hostToken.IsCancellationRequested) return;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_hostToken);
            _task = connection.RunAsync(_cancellation.Token);
        }
        finally { _control.Release(); }
    }
    public async Task StopAsync()
    {
        await _control.WaitAsync();
        try
        {
            var cancellation = _cancellation;
            if (cancellation == null) return;
            cancellation.Cancel();
            await _task;
            await connection.DisconnectAsync();
            cancellation.Dispose();
            _cancellation = null;
        }
        finally { _control.Release(); }
    }
    public void Dispose() { _cancellation?.Cancel(); _cancellation?.Dispose(); }
}
