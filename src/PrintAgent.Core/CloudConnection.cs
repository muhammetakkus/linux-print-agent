using PrintAgent.Common;
using PrintAgent.Common.Models;
using SocketIOClient;

namespace PrintAgent.Core;

/// <summary>Cloud connection only. Printer discovery and print execution are supplied by the host.</summary>
public sealed class CloudConnection : IDisposable
{
    private readonly CloudSession _session;
    private readonly Func<List<PrinterInfo>> _getPrinters;
    private readonly Func<PrintJobData, Task<PrintJobResult>> _handlePrintJob;
    private readonly Func<PrintJobData, CancellationToken, Task<PrintJobResult>>? _cancellablePrintJob;
    private readonly SemaphoreSlim _incomingJobs = new(8, 8);
    private readonly Func<string, SocketIOOptions, IAgentSocket> _createSocket;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Random _random;
    private readonly object _socketLock = new();
    private readonly object _printersLock = new();
    private readonly Dictionary<string, string> _printerIds = new(StringComparer.Ordinal);
    private readonly object _stateDebouncelock = new();
    private Timer? _stateDebounceTimer;
    private IAgentSocket? _socket;
    private bool _isConnected;
    public bool IsConnected => Volatile.Read(ref _isConnected);
    public List<PrinterInfo> Printers { get; private set; } = new();
    public event Action? StateChanged;
    public event Action<LogEntry>? LogAdded;

    // Discovery recreates local objects; cloud identity belongs to the account, not to a discovery result.
    public void ReplacePrinterSnapshot(List<PrinterInfo> printers)
    {
        lock (_printersLock)
        {
            foreach (var printer in printers)
                printer.ExternalId = _printerIds.GetValueOrDefault(printer.Name, "");
            Printers = printers;
        }
    }

    public CloudConnection(CloudSession session, Func<List<PrinterInfo>> getPrinters,
        Func<PrintJobData, Task<PrintJobResult>> handlePrintJob,
        Func<string, SocketIOOptions, IAgentSocket>? socketFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Random? random = null,
        Func<PrintJobData, CancellationToken, Task<PrintJobResult>>? cancellablePrintJob = null)
    {
        _session = session;
        _getPrinters = getPrinters;
        _handlePrintJob = handlePrintJob;
        _cancellablePrintJob = cancellablePrintJob;
        _createSocket = socketFactory ?? ((url, options) => new SocketIoAgentSocket(url, options));
        _delay = delay ?? ((time, ct) => Task.Delay(time, ct));
        _random = random ?? Random.Shared;
        _session.AuthenticationCleared += AuthenticationCleared;
    }

    private void AuthenticationCleared()
    {
        lock (_printersLock)
        {
            _printerIds.Clear();
            foreach (var printer in Printers) printer.ExternalId = "";
        }
        SetConnected(false);
        OnStateChanged();
    }

    private void DisposeSocket()
    {
        IAgentSocket? socketToDispose;
        lock (_socketLock)
        {
            socketToDispose = _socket;
            _socket = null;
        }
        if (socketToDispose != null)
        {
            _ = Task.Run(() =>
            {
                try { socketToDispose.Dispose(); } catch { }
            });
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        int retryCount = 0;

        while (!ct.IsCancellationRequested)
        {
            if (!_session.IsAuthenticated)
            {
                retryCount = 0;
                try { await _delay(TimeSpan.FromMilliseconds(1000), ct); } catch { return; }
                continue;
            }

            try
            {
                // A stale access token is not a logout: mint a new one from the
                // session token before every handshake. If that fails we back
                // off and try again — the stored session stays put.
                if (!await _session.EnsureAccessTokenAsync(ct))
                {
                    retryCount++;
                    if (!_session.IsAuthenticated) continue;
                    var wait = Math.Min(
                        Constants.ReconnectDelayMs * (1 << Math.Min(retryCount, 5)),
                        Constants.MaxReconnectDelayMs);
                    try { await _delay(TimeSpan.FromMilliseconds(wait), ct); } catch { return; }
                    continue;
                }

                await ConnectSocketAsync(ct);
                retryCount = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                retryCount++;
                AddLog("Error", $"Connection failed: {ex.Message}");
            }

            SetConnected(false);
            OnStateChanged();

            if (ct.IsCancellationRequested || !_session.IsAuthenticated) continue;

            var baseDelay = Constants.ReconnectDelayMs * (1 << Math.Min(retryCount, 5));
            var jitter = _random.Next(0, Math.Max(1, baseDelay / 4));
            var delay = Math.Min(baseDelay + jitter, Constants.MaxReconnectDelayMs);
            AddLog("Info", $"Reconnecting in {delay / 1000}s...");
            try { await _delay(TimeSpan.FromMilliseconds(delay), ct); } catch { return; }
        }
    }

    private async Task ConnectSocketAsync(CancellationToken ct)
    {
        DisposeSocket();

        var accessToken = _session.Auth!.Token;
        var socket = _createSocket($"{Constants.DefaultApiUrl}/agent", new SocketIOOptions
        {
            Auth = new { token = accessToken },
            Reconnection = false,
            ConnectionTimeout = TimeSpan.FromSeconds(Constants.ConnectionTimeoutSeconds),
            Path = "/api/socket.io",
        });

        lock (_socketLock) { _socket = socket; }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        socket.Connected += async () =>
        {
            SetConnected(true);
            AddLog("Info", "Connected to API");
            OnStateChanged();

            try
            {
                ReplacePrinterSnapshot(_getPrinters());

                await socket.EmitAsync("agent:register", new
                {
                    deviceId = _session.DeviceId,
                    deviceName = _session.DeviceName,
                    platform = _session.Platform,
                    printers = CloudPrinterSnapshot.Create(Printers),
                });
            }
            catch (Exception ex)
            {
                AddLog("Error", $"Registration failed: {ex.Message}");
                tcs.TrySetResult(false);
            }
        };

        socket.Disconnected += () =>
        {
            SetConnected(false);
            AddLog("Warning", "Disconnected from API");
            OnStateChanged();
            tcs.TrySetResult(false);
        };

        socket.Error += e =>
        {
            AddLog("Error", $"Socket error: {e}");

            // "Invalid token" only means the short-lived access token went
            // stale — the next loop mints a new one. Erasing credentials here
            // is what used to sign every machine out after seven days.
            if (e == "Session revoked")
            {
                _session.ClearAuth();
                AddLog("Warning", "Session ended from the panel — please sign in again");
            }
            else if (e == "Invalid token")
            {
                _session.RejectAccessToken(accessToken);
                AddLog("Info", "Access token expired, refreshing");
            }

            tcs.TrySetResult(false);
        };

        socket.On("print:job", async response =>
        {
            if (!_incomingJobs.Wait(0))
            {
                await response.CallbackAsync(new PrintJobResult { Status = "failed", ErrorMessage = "Too many pending print jobs." });
                return;
            }
            try
            {
                var data = response.GetValue<PrintJobData>();
                var result = _cancellablePrintJob == null ? await _handlePrintJob(data) : await _cancellablePrintJob(data, ct);
                await response.CallbackAsync(result);
            }
            catch (Exception ex)
            {
                await response.CallbackAsync(new PrintJobResult { Status = "failed", ErrorMessage = ex.Message });
            }
            finally { _incomingJobs.Release(); }
        });

        socket.On("printers:registered", response =>
        {
            try
            {
                var registered = response.GetValue<List<PrinterIdMapping>>();
                if (registered != null)
                {
                    lock (_printersLock)
                    {
                        if (!ReferenceEquals(socket, _socket) || !_session.IsAuthenticated) return Task.CompletedTask;
                        foreach (var mapping in registered)
                        {
                            _printerIds[mapping.Name] = mapping.ExternalId;
                            var printer = Printers.FirstOrDefault(p => p.Name == mapping.Name);
                            if (printer != null) printer.ExternalId = mapping.ExternalId;
                        }
                    }
                    OnStateChanged();
                }
            }
            catch (Exception ex)
            {
                AddLog("Warning", $"Failed to map printer IDs: {ex.Message}");
            }
            return Task.CompletedTask;
        });

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Constants.ConnectionTimeoutSeconds + 5));
        var connectTask = socket.ConnectAsync();
        var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);

        var completed = await Task.WhenAny(connectTask, timeoutTask);
        if (completed == timeoutTask || ct.IsCancellationRequested)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw new TimeoutException($"Connection timed out after {Constants.ConnectionTimeoutSeconds + 5}s");
        }

        await connectTask;

        using var reg = ct.Register(() => tcs.TrySetCanceled());

        try
        {
            await tcs.Task;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            DisposeSocket();
        }
    }

    public async Task DisconnectAsync()
    {
        IAgentSocket? socket;
        lock (_socketLock) { socket = _socket; }

        if (socket != null)
        {
            try { await socket.DisconnectAsync(); } catch (Exception ex) { AddLog("Warning", $"Socket disconnect error: {ex.Message}"); }
        }
        DisposeSocket();
        SetConnected(false);
        OnStateChanged();
    }

    public async Task EmitPrinterStatusAsync()
    {
        IAgentSocket? socket;
        lock (_socketLock) { socket = _socket; }

        if (socket?.IsConnected != true) return;

        try
        {
            await socket.EmitAsync("printer:status-update", new
            {
                printers = CloudPrinterSnapshot.Create(Printers),
            });
        }
        catch (Exception ex)
        {
            AddLog("Warning", $"Printer status emit failed: {ex.Message}");
        }
    }

    private void SetConnected(bool value) => Volatile.Write(ref _isConnected, value);

    private void AddLog(string level, string message) =>
        LogAdded?.Invoke(new LogEntry { Level = level, Message = message, Timestamp = DateTime.Now });

    private void OnStateChanged()
    {
        lock (_stateDebouncelock)
        {
            _stateDebounceTimer?.Dispose();
            _stateDebounceTimer = new Timer(_ => StateChanged?.Invoke(), null, 100, Timeout.Infinite);
        }
    }

    // The host cancels and awaits RunAsync before disposing this object.
    public void Dispose()
    {
        _session.AuthenticationCleared -= AuthenticationCleared;
        DisposeSocket();
        lock (_stateDebouncelock) { _stateDebounceTimer?.Dispose(); }
    }
}
