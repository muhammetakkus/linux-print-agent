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
    [InlineData(2, 3, "done")]
    [InlineData(999, 101, "done")]
    [InlineData(0, 100, "done")]
    public async Task Copy_count_is_passed_through_like_Windows(int printerMaximum, int copies, string status)
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
    public async Task Rapid_jobs_and_copies_are_not_subject_to_a_local_usage_quota()
    {
        using var fixture = new PrintFixture();
        for (var index = 0; index < 65; index++)
        {
            var job = PrintEngineTests.Pdf(); job.Copies = 100;
            Assert.Equal("done", (await fixture.Engine.PrintAsync(job)).Status);
        }
        Assert.Equal(65, fixture.Cups.Submissions.Count);
    }

    [Fact]
    public async Task Document_larger_than_previous_20_MiB_cap_is_submitted()
    {
        using var fixture = new PrintFixture();
        var job = PrintEngineTests.Pdf();
        job.DocumentData = Convert.ToBase64String(new byte[21 * 1024 * 1024]);
        Assert.Equal("done", (await fixture.Engine.PrintAsync(job)).Status);
        Assert.Single(fixture.Cups.Submissions);
    }

    [Fact]
    public async Task Raw_submission_failure_stops_remaining_copies()
    {
        using var fixture = new PrintFixture();
        fixture.Cups.Submit = (_, _, _, _, _, _) => Task.FromResult(new PrintAgent.Linux.CommandResult(1, "", "controlled stop"));
        var result = await fixture.Engine.PrintAsync(new PrintJobData { JobId = "copies", PrinterName = "queue", DocumentType = "raw", DocumentData = "AQI=", Copies = int.MaxValue });
        Assert.Equal("failed", result.Status);
        Assert.Single(fixture.Cups.Submissions);
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
    public async Task Non_pdf_document_type_is_sent_as_raw_like_Windows()
    {
        using var fixture = new PrintFixture();
        var job = PrintEngineTests.Pdf(); job.DocumentType = "unknown";
        Assert.Equal("done", (await fixture.Engine.PrintAsync(job)).Status);
        Assert.Single(fixture.Cups.Submissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_job_is_submitted_each_time_API_sends_it(bool changeContent)
    {
        using var fixture = new PrintFixture();
        var job = PrintEngineTests.Pdf(); job.JobId = "repeat";
        Assert.Equal("done", (await fixture.Engine.PrintAsync(job)).Status);
        if (changeContent) job.DocumentName = "changed.pdf";
        Assert.Equal("done", (await fixture.Engine.PrintAsync(job)).Status);
        Assert.Equal(2, fixture.Cups.Submissions.Count);
    }
}
