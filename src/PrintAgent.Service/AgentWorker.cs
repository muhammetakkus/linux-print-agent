using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintAgent.Common.Models;
using PrintAgent.Core;
using PrintAgent.Linux;

namespace PrintAgent.Service;

public sealed class AgentWorker : BackgroundService
{
    private readonly CloudSession _session;
    private readonly CloudConnection _connection;
    private readonly PrinterCatalog _catalog;
    private readonly LinuxPrintEngine _printing;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentWorker> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CloudRuntime _runtime;

    public AgentWorker(CloudSession session, CloudConnection connection, PrinterCatalog catalog,
        LinuxPrintEngine printing, AgentOptions options, ILogger<AgentWorker> logger, CloudRuntime runtime,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _session = session; _connection = connection; _catalog = catalog; _printing = printing;
        _options = options; _logger = logger; _delay = delay ?? ((time, ct) => Task.Delay(time, ct));
        _runtime = runtime;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
            Directory.CreateDirectory(_options.CacheDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(_options.CacheDirectory); // Host tests use a temporary Windows directory.
        _session.LoadSavedAuth();
        if (!_session.IsAuthenticated) _logger.LogInformation("No saved credentials. Waiting for device sign-in; no cloud connection will be opened.");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _session.LogAdded += Log;
        _connection.LogAdded += Log;
        _printing.LogAdded += Log;
        try
        {
            await RefreshAsync(stoppingToken);
            _runtime.Start(stoppingToken);
            await PollAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await _runtime.StopAsync();
            _session.LogAdded -= Log; _connection.LogAdded -= Log; _printing.LogAdded -= Log;
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _delay(TimeSpan.FromSeconds(30), ct);
            while (_printing.IsPrinting) await _delay(TimeSpan.FromSeconds(5), ct);
            await RefreshAsync(ct);
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var old = CloudPrinterSnapshot.Signature(_catalog.Printers);
        try { await _catalog.RefreshAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogWarning("Printer discovery failed: {Message}", ex.Message); _catalog.Clear(); }
        var printers = _catalog.Printers;
        _connection.ReplacePrinterSnapshot(printers);
        var changed = old != CloudPrinterSnapshot.Signature(printers);
        if (changed) await _connection.EmitPrinterStatusAsync();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _printing.CancelPending();
        await base.StopAsync(cancellationToken);
        await _connection.DisconnectAsync();
        await _printing.WaitForIdleAsync(cancellationToken);
        // Keep the lease until host disposal; dependencies may still be unwinding callbacks.
    }

    private void Log(LogEntry entry) => _logger.Log(entry.Level switch
    { "Error" => LogLevel.Error, "Warning" => LogLevel.Warning, _ => LogLevel.Information }, "{Message} (job {JobId})", entry.Message, entry.JobId);

}
