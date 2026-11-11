using System.Diagnostics;

namespace PrintAgent.Linux;

public sealed record CommandResult(int ExitCode, string Output, string Error, bool TimedOut = false);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string command, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, TimeSpan? timeout, CancellationToken ct = default);
}

public sealed class ProcessRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string command, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, TimeSpan? timeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(info) ?? throw new IOException($"Could not start {command}");
        var output = ReadBoundedAsync(process.StandardOutput);
        var error = ReadBoundedAsync(process.StandardError);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue) cancellation.CancelAfter(timeout.Value);
        var timedOut = false;
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }
        return new(process.ExitCode, await output, await error, timedOut);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
            if (text.Length < 8192) text.Append(buffer, 0, Math.Min(read, 8192 - text.Length));
        return text.ToString(); // Continue draining after the limit so the child cannot block on a full pipe.
    }
}
