using PrintAgent.Common;
using PrintAgent.Common.Models;
using PrintAgent.Common.Security;

namespace PrintAgent.Linux;

public sealed class LinuxPrintEngine : IDisposable
{
    private readonly ICupsClient _cups;
    private readonly Func<List<PrinterInfo>> _getPrinters;
    private readonly string _cacheDirectory;
    private readonly PrintSafetyOptions _safety;
    private readonly PrintJobLedger _ledger;
    private readonly SemaphoreSlim _printLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _jobsLock = new();
    private volatile bool _printing;
    public bool IsPrinting => _printing;
    public List<PrintJobRecord> Jobs { get; } = [];
    public event Action<LogEntry>? LogAdded;

    public LinuxPrintEngine(ICupsClient cups, Func<List<PrinterInfo>> getPrinters, string cacheDirectory,
        PrintSafetyOptions? safety = null, PrintJobLedger? ledger = null)
    {
        _cups = cups; _getPrinters = getPrinters; _cacheDirectory = cacheDirectory;
        _safety = safety ?? new(); _safety.Validate(); _ledger = ledger ?? new();
    }
    public List<PrintJobRecord> GetJobsSnapshot()
    {
        lock (_jobsLock) return Jobs.Select(j => new PrintJobRecord
        { JobId = j.JobId, DocumentName = j.DocumentName, PrinterName = j.PrinterName,
          Status = j.Status, ReceivedAt = j.ReceivedAt, CompletedAt = j.CompletedAt, ErrorMessage = j.ErrorMessage }).ToList();
    }
    public void ClearCompletedJobs() { lock (_jobsLock) Jobs.RemoveAll(j => j.Status is "done" or "failed"); }
    public void CancelPending() => _shutdown.Cancel();
    public async Task WaitForIdleAsync(CancellationToken ct) { await _printLock.WaitAsync(ct); _printLock.Release(); }
    public Task<PrintJobResult> PrintAsync(PrintJobData job) => PrintAsync(job, CancellationToken.None);

    public async Task<PrintJobResult> PrintAsync(PrintJobData job, CancellationToken ct)
    {
        var error = _safety.ValidateJob(job);
        if (error != null) return Failed(error);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(_safety.JobTimeoutSeconds));
        var token = budget.Token;
        var acquired = false;
        PrintJobRecord? record = null;
        string? file = null;
        try
        {
            acquired = await _printLock.WaitAsync(TimeSpan.FromSeconds(Constants.PrintTimeoutSeconds / 2), token);
            if (!acquired) return Failed("Print queue busy");
            var printer = _getPrinters().FirstOrDefault(p => string.Equals(p.Name.Trim(), job.PrinterName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (printer == null) return Failed("Printer not found on this machine.");
            if ((error = _safety.ValidateJob(job, printer.Capabilities?.MaxCopies)) != null) return Failed(error);
            job = System.Text.Json.JsonSerializer.Deserialize<PrintJobData>(System.Text.Json.JsonSerializer.Serialize(job))!;
            job.PrinterName = printer.Name;
            var bytes = Convert.FromBase64String(job.DocumentData);
            if (bytes.Length == 0 || bytes.Length > _safety.MaxDocumentBytes) return Failed("Document is empty or exceeds the size limit.");
            var previous = _ledger.Reserve(job, _safety);
            if (previous != null) return previous;
            record = Track(job); _printing = true;
            Log("Info", $"Printing {job.DocumentName} to {job.PrinterName}", job.JobId);
            var raw = !job.DocumentType.Equals("pdf", StringComparison.OrdinalIgnoreCase);
            file = Path.Combine(_cacheDirectory, $"print_{Guid.NewGuid():N}{(raw ? ".raw" : ".pdf")}");
            var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (OperatingSystem.IsLinux()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(file, settings)) await stream.WriteAsync(bytes, token);
            PrintJobResult result = new() { Status = "done" };
            var options = raw ? Array.Empty<string>() : CupsPrintOptions.Build(job, bytes);
            for (var copy = 0; copy < (raw ? job.Copies : 1); copy++)
            {
                token.ThrowIfCancellationRequested();
                var command = await _cups.SubmitAsync(job.PrinterName, job.DocumentName, file, options, raw ? 1 : job.Copies, raw, token);
                if (command.TimedOut) { result = Failed("Print timed out"); break; }
                if (command.ExitCode != 0) { result = Failed($"CUPS submission failed (exit code {command.ExitCode}): {SafeText.Limit(command.Error)}"); break; }
            }
            _ledger.Complete(job.JobId, result);
            return Complete(record, result);
        }
        catch (OperationCanceledException)
        {
            var result = Failed("Print cancelled or timed out; check the printer queue before resubmitting.");
            if (record != null) Complete(record, result);
            return result; // Durable reservation stays uncertain; no automatic replay.
        }
        catch (Exception ex) when (ex is IOException or FormatException or ArgumentException or System.Text.Json.JsonException)
        {
            var result = Failed(SafeText.Limit(ex.Message));
            if (record != null) Complete(record, result);
            return result;
        }
        finally
        {
            if (file != null) try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("Warning", "Temporary document cleanup failed."); }
            if (acquired) { _printing = false; _printLock.Release(); }
        }
    }
    private PrintJobRecord Track(PrintJobData job)
    {
        var record = new PrintJobRecord { JobId = job.JobId, DocumentName = job.DocumentName, PrinterName = job.PrinterName, Status = "printing", ReceivedAt = DateTime.Now };
        lock (_jobsLock) { Jobs.Insert(0, record); if (Jobs.Count > 100) Jobs.RemoveAt(Jobs.Count - 1); }
        return record;
    }
    private PrintJobResult Complete(PrintJobRecord record, PrintJobResult result)
    {
        lock (_jobsLock) { record.Status = result.Status; record.ErrorMessage = result.ErrorMessage; record.CompletedAt = DateTime.Now; }
        Log(result.Status == "done" ? "Info" : "Error", result.Status == "done" ? $"Print completed: {record.DocumentName}" : result.ErrorMessage ?? "Print failed", record.JobId);
        return result;
    }
    private static PrintJobResult Failed(string error) => new() { Status = "failed", ErrorMessage = error };
    private void Log(string level, string message, string? jobId = null) => LogAdded?.Invoke(new() { Level = level, Message = SafeText.Limit(message), JobId = SafeText.Limit(jobId, 128) });
    public void Dispose() { _shutdown.Cancel(); _shutdown.Dispose(); _printLock.Dispose(); }
}
