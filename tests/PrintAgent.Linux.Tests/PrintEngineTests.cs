using System.Text;
using PrintAgent.Common.Models;
using PrintAgent.Linux;
using Xunit;

namespace PrintAgent.Linux.Tests;

public class PrintEngineTests
{
    [Fact]
    public async Task Concurrent_jobs_keep_one_global_print_lock_instead_of_new_per_printer_queues()
    {
        using var fixture = new PrintFixture();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Cups.Submit = (_, _, _, _, _, _) => { firstEntered.TrySetResult(); return releaseFirst.Task; };
        var first = fixture.Engine.PrintAsync(Pdf());
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.Engine.PrintAsync(Pdf());
        Assert.Single(fixture.Cups.Submissions);
        Assert.False(second.IsCompleted);
        releaseFirst.SetResult(new CommandResult(0, "accepted", ""));
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Equal("done", result.Status));
        Assert.Equal(2, fixture.Cups.Submissions.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Raw_nonpositive_copies_are_rejected(int copies)
    {
        using var fixture = new PrintFixture();
        Assert.Equal("failed", (await fixture.Engine.PrintAsync(new PrintJobData { JobId = Guid.NewGuid().ToString(), PrinterName = "queue", DocumentType = "raw", DocumentData = "AQI=", Copies = copies })).Status);
        Assert.Empty(fixture.Cups.Submissions);
    }

    [Fact]
    public async Task Pdf_is_decoded_options_prepared_submitted_once_and_cleaned_before_done()
    {
        using var fixture = new PrintFixture();
        fixture.Cups.Submit = (_, _, file, options, copies, raw) =>
        {
            Assert.Equal("%PDF /MediaBox [0 0 297 210]", File.ReadAllText(file));
            Assert.Equal(new[] { "print-scaling=fit", "orientation-requested=4", "sides=two-sided-long-edge", "print-color-mode=monochrome", "media=A4,main", "page-ranges=1,3-5" }, options);
            Assert.Equal(2, copies);
            Assert.False(raw);
            return Task.FromResult(new CommandResult(0, "request id is queue-12 (1 file(s))", ""));
        };
        var job = Pdf();
        job.Copies = 2;
        job.Options = new() { Duplex = "long-edge", Color = false, PaperSize = "A4", Tray = "main", Pages = "1,3-5" };
        var result = await fixture.Engine.PrintAsync(job);
        Assert.Equal("done", result.Status);
        Assert.Empty(Directory.GetFiles(fixture.Cache));
        Assert.Equal("done", fixture.Engine.Jobs.Single().Status);
    }

    [Theory]
    [InlineData(1, "", "done")]
    [InlineData(2, "format not supported", "failed")]
    public async Task Cups_exit_status_controls_callback_without_waiting_for_physical_completion(int exit, string error, string expected)
    {
        using var fixture = new PrintFixture();
        fixture.Cups.Submit = (_, _, _, _, _, _) => Task.FromResult(new CommandResult(exit - 1, "", error));
        Assert.Equal(expected, (await fixture.Engine.PrintAsync(Pdf())).Status);
        Assert.Single(fixture.Cups.Submissions);
        Assert.Empty(Directory.GetFiles(fixture.Cache));
    }

    [Fact]
    public async Task Raw_unknown_document_types_keep_Windows_branch_and_one_submission_per_copy()
    {
        using var fixture = new PrintFixture();
        fixture.Cups.Submit = (_, _, file, options, copies, raw) =>
        {
            Assert.Equal(new byte[] { 0, 27, 255 }, File.ReadAllBytes(file));
            Assert.True(raw);
            Assert.Empty(options);
            Assert.Equal(1, copies);
            return Task.FromResult(new CommandResult(0, "accepted", ""));
        };
        var result = await fixture.Engine.PrintAsync(new PrintJobData { JobId = Guid.NewGuid().ToString(), PrinterName = "queue", DocumentType = "zpl", DocumentData = "ABv/", Copies = 3, Options = new() { Duplex = "long-edge" } });
        Assert.Equal("done", result.Status);
        Assert.Equal(3, fixture.Cups.Submissions.Count);
        Assert.Empty(Directory.GetFiles(fixture.Cache));
    }

    [Fact]
    public async Task Raw_failure_stops_remaining_copies_and_is_not_retried()
    {
        using var fixture = new PrintFixture();
        fixture.Cups.Submit = (_, _, _, _, _, _) => Task.FromResult(new CommandResult(1, "", "raw not supported"));
        var result = await fixture.Engine.PrintAsync(new PrintJobData { JobId = Guid.NewGuid().ToString(), PrinterName = "queue", DocumentType = "raw", DocumentData = "AQI=", Copies = 3 });
        Assert.Equal("failed", result.Status);
        Assert.Contains("raw not supported", result.ErrorMessage);
        Assert.Single(fixture.Cups.Submissions);
    }

    [Fact]
    public async Task No_printers_and_unmatched_names_fail_before_submission()
    {
        using var fixture = new PrintFixture();
        fixture.Printers.Clear();
        Assert.Equal("failed", (await fixture.Engine.PrintAsync(Pdf())).Status);
        fixture.Printers.Add(new PrinterInfo { Name = "other" });
        Assert.Equal("failed", (await fixture.Engine.PrintAsync(Pdf())).Status);
        Assert.Empty(fixture.Cups.Submissions);
    }

    [Fact]
    public async Task Pdf_decode_error_reaches_existing_socket_failure_handler_without_inventing_validation()
    {
        using var fixture = new PrintFixture();
        var job = Pdf(); job.DocumentData = "not base64";
        Assert.Equal("failed", (await fixture.Engine.PrintAsync(job)).Status);
        Assert.False(fixture.Engine.IsPrinting);
        Assert.Empty(fixture.Engine.Jobs);
        Assert.Empty(fixture.Cups.Submissions);
    }

    [Fact]
    public async Task Timed_out_submission_reports_failure_and_does_not_resend()
    {
        using var fixture = new PrintFixture();
        fixture.Cups.Submit = (_, _, _, _, _, _) => Task.FromResult(new CommandResult(-1, "", "", true));
        var result = await fixture.Engine.PrintAsync(Pdf());
        Assert.Equal("failed", result.Status);
        Assert.Equal("Print timed out", result.ErrorMessage);
        Assert.Single(fixture.Cups.Submissions);
    }

    [Fact]
    public async Task Same_job_id_is_deduplicated_and_printer_names_keep_trim_case_matching()
    {
        using var fixture = new PrintFixture();
        var job = Pdf(); job.JobId = "same"; job.PrinterName = " QUEUE ";
        Assert.Equal("done", (await fixture.Engine.PrintAsync(job)).Status);
        Assert.Equal("queue", fixture.Engine.GetJobsSnapshot().Single().PrinterName);
        Assert.Equal("done", (await fixture.Engine.PrintAsync(job)).Status);
        Assert.Single(fixture.Cups.Submissions);
    }

    [Fact]
    public void Explicit_portrait_fit_false_and_source_sanitization_are_preserved()
    {
        var job = Pdf();
        job.Options = new() { Orientation = "portrait", FitToPage = false, Duplex = "short-edge", Color = true, PaperSize = " A\"4, ", Pages = "1x,3-5!" };
        Assert.Equal(new[] { "print-scaling=none", "orientation-requested=3", "sides=two-sided-short-edge", "print-color-mode=color", "media=A4", "page-ranges=1,3-5" }, CupsPrintOptions.Build(job, Convert.FromBase64String(job.DocumentData)));
    }

    internal static PrintJobData Pdf() => new() { JobId = Guid.NewGuid().ToString(), PrinterName = "queue", DocumentName = "test.pdf", DocumentData = Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF /MediaBox [0 0 297 210]")) };
}

internal sealed class PrintFixture : IDisposable
{
    public string Cache { get; } = Path.Combine(Path.GetTempPath(), "nuxprint-print-" + Guid.NewGuid().ToString("N"));
    public FakeCups Cups { get; } = new();
    public List<PrinterInfo> Printers { get; } = [new() { Name = "queue" }];
    public LinuxPrintEngine Engine { get; }
    public PrintFixture() { Directory.CreateDirectory(Cache); Engine = new(Cups, () => Printers, Cache); }
    public void Dispose() { Engine.Dispose(); Directory.Delete(Cache, true); }
}

internal sealed class FakeCups : ICupsClient
{
    public Func<CancellationToken, Task<CommandResult>>? CancellableSubmit { get; set; }
    public Func<string, string, string, IReadOnlyList<string>, int, bool, Task<CommandResult>> Submit { get; set; } = (_, _, _, _, _, _) => Task.FromResult(new CommandResult(0, "accepted", ""));
    public List<string> Submissions { get; } = [];
    public Func<CancellationToken, Task<CupsSnapshot>> Discover { get; set; } = _ => Task.FromResult(new CupsSnapshot([new() { Name = "queue", Status = "online" }], "queue"));
    public Task<CupsSnapshot> DiscoverAsync(CancellationToken ct = default) => Discover(ct);
    public Task<IReadOnlyList<CupsQueueJob>> GetQueueAsync(string printer, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CupsQueueJob>>([]);
    public Task<CommandResult> SubmitAsync(string printer, string name, string file, IReadOnlyList<string> options, int copies, bool raw, CancellationToken ct = default)
    { Submissions.Add(file); return CancellableSubmit?.Invoke(ct) ?? Submit(printer, name, file, options, copies, raw); }
}
