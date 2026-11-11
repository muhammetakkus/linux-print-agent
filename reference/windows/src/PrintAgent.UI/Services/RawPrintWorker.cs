using System.Diagnostics;
using PrintAgent.Common.Helpers;
using PrintAgent.Common.Models;
using PrintAgent.Common.Security;

namespace PrintAgent.UI.Services;

// Isolate blocking WinSpool calls in a child that can actually be terminated on timeout.
public static class RawPrintWorker
{
    public static int Run(string[] args)
    {
        if (args.Length != 5 || !int.TryParse(args[3], out var copies)) return 2;
        var root = UserDataDirectory.Ensure() + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(args[1]);
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file) ||
            (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(file).Length > 20 * 1024 * 1024 || copies is < 1 or > 100)
            return 2;
        return RawPrintHelper.SendBytes(args[2], File.ReadAllBytes(file), copies, SafeText.Limit(args[4], 256)).ok ? 0 : 1;
    }

    public static async Task<PrintJobResult> SubmitAsync(PrintJobData job, CancellationToken token)
    {
        var file = Path.Combine(UserDataDirectory.Ensure(), "print-" + Guid.NewGuid().ToString("N") + ".raw");
        try
        {
            await File.WriteAllBytesAsync(file, Convert.FromBase64String(job.DocumentData), token);
            var executable = Environment.ProcessPath ?? throw new IOException("Cannot locate agent executable.");
            ExecutableTrust.RequireInstalledPath(executable);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--raw-worker", file, job.PrinterName, job.Copies.ToString(System.Globalization.CultureInfo.InvariantCulture), job.DocumentName }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Raw worker could not start.");
            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); throw; }
            return new() { Status = process.ExitCode == 0 ? "done" : "failed", ErrorMessage = process.ExitCode == 0 ? null : "Raw print failed. Check the printer queue before resubmitting." };
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
