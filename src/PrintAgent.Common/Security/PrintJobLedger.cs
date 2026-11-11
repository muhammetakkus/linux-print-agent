using System.Security.Cryptography;
using System.Text.Json;
using PrintAgent.Common.Models;

namespace PrintAgent.Common.Security;

// Persist before submission; interrupted submissions must be reconciled manually, never reprinted automatically.
public sealed class PrintJobLedger(string? path = null)
{
    private const int Capacity = 10000;
    private readonly Dictionary<string, Receipt> _receipts = Load(path);
    private readonly object _gate = new();
    private readonly Queue<(DateTimeOffset Time, int Copies)> _recent = new();

    public PrintJobResult? Reserve(PrintJobData job, PrintSafetyOptions options)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(job)));
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _receipts.Where(p => now - p.Value.Time > TimeSpan.FromDays(7)).Select(p => p.Key).ToArray()) _receipts.Remove(key);
            if (_receipts.TryGetValue(job.JobId, out var previous))
                return previous.Fingerprint != fingerprint
                    ? new() { Status = "failed", ErrorMessage = "Job ID was already used with different content." }
                    : new() { Status = previous.Status, ErrorMessage = previous.Error };
            while (_recent.TryPeek(out var item) && now - item.Time >= TimeSpan.FromMinutes(1)) _recent.Dequeue();
            if (_receipts.Count >= Capacity) return new() { Status = "failed", ErrorMessage = "Job receipt capacity reached." };
            if (_recent.Count >= options.MaxJobsPerMinute || _recent.Sum(i => i.Copies) + job.Copies > options.MaxCopiesPerMinute)
                return new() { Status = "failed", ErrorMessage = "Device print rate limit reached." };
            _receipts[job.JobId] = new(fingerprint, now, "failed", "Submission outcome is uncertain. Check the printer queue before creating a new job.");
            try { Save(); } catch { _receipts.Remove(job.JobId); throw; }
            _recent.Enqueue((now, job.Copies));
            return null;
        }
    }

    public void Complete(string jobId, PrintJobResult result)
    {
        lock (_gate)
        {
            _receipts[jobId] = _receipts[jobId] with { Status = result.Status, Error = SafeText.Limit(result.ErrorMessage, 256) };
            Save();
        }
    }

    private void Save()
    {
        if (path == null) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (OperatingSystem.IsLinux()) Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(directory);
        RejectLink(directory); if (File.Exists(path)) RejectLink(path);
        var temporary = Path.Combine(directory, ".receipts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, settings)) { JsonSerializer.Serialize(stream, _receipts); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static Dictionary<string, Receipt> Load(string? file)
    {
        if (file == null || !File.Exists(file)) return new(StringComparer.Ordinal);
        RejectLink(file);
        if (new FileInfo(file).Length > 32 * 1024 * 1024) throw new InvalidDataException("Job receipt file exceeds its size limit.");
        var data = JsonSerializer.Deserialize<Dictionary<string, Receipt>>(File.ReadAllText(file)) ?? throw new InvalidDataException("Invalid job receipt file.");
        if (data.Count > Capacity) throw new InvalidDataException("Job receipt capacity exceeded.");
        return data;
    }
    private static void RejectLink(string file)
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Job receipt path cannot be a link.");
    }
    public sealed record Receipt(string Fingerprint, DateTimeOffset Time, string Status, string? Error);
}
