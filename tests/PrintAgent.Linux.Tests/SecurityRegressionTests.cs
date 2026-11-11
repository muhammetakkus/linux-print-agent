using PrintAgent.Common.Models;
using PrintAgent.Common.Security;
using PrintAgent.Linux;
using Xunit;

namespace PrintAgent.Linux.Tests;

public class SecurityRegressionTests
{
    [Fact]
    public async Task Media_option_cannot_inject_additional_CUPS_options()
    {
        using var fixture = new PrintFixture();
        var job = PrintEngineTests.Pdf(); job.Options = new() { PaperSize = "A4 sides=two-sided-long-edge" };
        Assert.Equal("failed", (await fixture.Engine.PrintAsync(job)).Status);
        Assert.Empty(fixture.Cups.Submissions);
    }
    [Theory]
    [InlineData(2, 2, "done")]
    [InlineData(2, 3, "failed")]
    [InlineData(999, 101, "failed")]
    [InlineData(0, 100, "done")]
    public async Task Lower_of_application_and_printer_limit_is_enforced(int printerMaximum, int copies, string status)
    {
        using var fixture = new PrintFixture();
        fixture.Printers[0].Capabilities = new() { MaxCopies = printerMaximum };
        var job = PrintEngineTests.Pdf(); job.Copies = copies;
        Assert.Equal(status, (await fixture.Engine.PrintAsync(job)).Status);
        Assert.Equal(status == "done" ? 1 : 0, fixture.Cups.Submissions.Count);
    }

    [Fact]
    public async Task Timeout_releases_global_lock_and_cleans_partial_files()
    {
        using var fixture = new PrintFixture();
        fixture.Cups.CancellableSubmit = async ct => { await Task.Delay(Timeout.Infinite, ct); return new(0, "", ""); };
        using var engine = new LinuxPrintEngine(fixture.Cups, () => fixture.Printers, fixture.Cache, new() { JobTimeoutSeconds = 1 });
        Assert.Equal("failed", (await engine.PrintAsync(PrintEngineTests.Pdf()).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.False(engine.IsPrinting);
        Assert.Empty(Directory.GetFiles(fixture.Cache));
        fixture.Cups.CancellableSubmit = null;
        Assert.Equal("done", (await engine.PrintAsync(PrintEngineTests.Pdf())).Status);
    }

    [Fact]
    public async Task Host_cancellation_reaches_raw_submission()
    {
        using var fixture = new PrintFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Cups.CancellableSubmit = async ct => { Assert.True(ct.CanBeCanceled); entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new(0, "", ""); };
        using var stop = new CancellationTokenSource();
        var job = PrintEngineTests.Pdf(); job.DocumentType = "raw";
        var task = fixture.Engine.PrintAsync(job, stop.Token); await entered.Task; stop.Cancel();
        Assert.Equal("failed", (await task.WaitAsync(TimeSpan.FromSeconds(3))).Status);
        Assert.False(fixture.Engine.IsPrinting);
    }

    [Fact]
    public void Durable_receipt_survives_restart_and_rejects_changed_content()
    {
        var directory = Path.Combine(Path.GetTempPath(), "np-ledger-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "receipts.json"); var options = new PrintSafetyOptions(); var job = PrintEngineTests.Pdf();
            var first = new PrintJobLedger(path); Assert.Null(first.Reserve(job, options));
            Assert.Contains("uncertain", new PrintJobLedger(path).Reserve(job, options)!.ErrorMessage);
            first.Complete(job.JobId, new() { Status = "done" });
            var restarted = new PrintJobLedger(path); Assert.Equal("done", restarted.Reserve(job, options)!.Status);
            job.DocumentData = "AQI="; Assert.Equal("failed", restarted.Reserve(job, options)!.Status);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Device_rate_limit_and_raw_disable_are_enforced()
    {
        var options = new PrintSafetyOptions { MaxJobsPerMinute = 1, AllowRaw = false };
        var job = PrintEngineTests.Pdf(); var ledger = new PrintJobLedger(); Assert.Null(ledger.Reserve(job, options));
        Assert.Contains("rate limit", ledger.Reserve(PrintEngineTests.Pdf(), options)!.ErrorMessage);
        job.DocumentType = "raw"; Assert.NotNull(options.ValidateJob(job));
    }

    [Fact]
    public async Task Oversized_base64_is_rejected_before_decoding()
    {
        using var fixture = new PrintFixture();
        using var engine = new LinuxPrintEngine(fixture.Cups, () => fixture.Printers, fixture.Cache, new() { MaxDocumentBytes = 3 });
        var job = PrintEngineTests.Pdf(); job.DocumentData = "AQIDBA==";
        Assert.Equal("failed", (await engine.PrintAsync(job)).Status); Assert.Empty(fixture.Cups.Submissions);
    }
    [Fact]
    public async Task Excessive_raw_copies_are_rejected_before_any_submission()
    {
        using var fixture = new PrintFixture();
        fixture.Cups.Submit = (_, _, _, _, _, _) => Task.FromResult(new PrintAgent.Linux.CommandResult(1, "", "controlled stop"));
        var result = await fixture.Engine.PrintAsync(new PrintJobData { JobId = "copies", PrinterName = "queue", DocumentType = "raw", DocumentData = "AQI=", Copies = int.MaxValue });
        Assert.Equal("failed", result.Status);
        Assert.Empty(fixture.Cups.Submissions);
    }

    [Fact]
    public async Task Oversized_title_never_enters_history_or_CUPS()
    {
        using var fixture = new PrintFixture();
        var job = PrintEngineTests.Pdf(); job.DocumentName = new string('x', 2 * 1024 * 1024);
        Assert.Equal("failed", (await fixture.Engine.PrintAsync(job)).Status);
        Assert.Empty(fixture.Engine.GetJobsSnapshot());
        Assert.Empty(fixture.Cups.Submissions);
    }

    [Fact]
    public async Task Unknown_document_type_is_rejected()
    {
        using var fixture = new PrintFixture();
        var job = PrintEngineTests.Pdf(); job.DocumentType = "unknown";
        Assert.Equal("failed", (await fixture.Engine.PrintAsync(job)).Status);
        Assert.Empty(fixture.Cups.Submissions);
    }

    [Fact]
    public async Task Same_job_is_submitted_only_once()
    {
        using var fixture = new PrintFixture();
        var job = PrintEngineTests.Pdf(); job.JobId = "repeat";
        Assert.Equal("done", (await fixture.Engine.PrintAsync(job)).Status);
        Assert.Equal("done", (await fixture.Engine.PrintAsync(job)).Status);
        Assert.Single(fixture.Cups.Submissions);
    }
}
