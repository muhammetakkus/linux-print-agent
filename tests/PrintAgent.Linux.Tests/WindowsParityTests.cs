using PrintAgent.Linux;
using Xunit;

namespace PrintAgent.Linux.Tests;

public class WindowsParityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Jobs_without_an_identifier_are_still_submitted(string? jobId)
    {
        using var fixture = new PrintFixture();
        var job = PrintEngineTests.Pdf(); job.JobId = jobId!;
        Assert.Equal("done", (await fixture.Engine.PrintAsync(job)).Status);
        Assert.Single(fixture.Cups.Submissions);
    }

    [Fact]
    public async Task Null_document_type_defaults_to_pdf()
    {
        using var fixture = new PrintFixture();
        var job = PrintEngineTests.Pdf(); job.DocumentType = null!;
        fixture.Cups.Submit = (_, _, _, options, copies, raw) =>
        {
            Assert.False(raw); Assert.Equal(1, copies);
            Assert.Contains("print-scaling=fit", options);
            return Task.FromResult(new CommandResult(0, "", ""));
        };
        Assert.Equal("done", (await fixture.Engine.PrintAsync(job)).Status);
        Assert.Single(fixture.Cups.Submissions);
    }

    [Fact]
    public async Task Waiting_in_queue_does_not_consume_the_printing_timeout()
    {
        using var fixture = new PrintFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submissions = 0;
        fixture.Cups.CancellableSubmit = async ct =>
        {
            var index = Interlocked.Increment(ref submissions);
            entered.TrySetResult();
            await Task.Delay(index == 1 ? 1100 : 1500, ct);
            return new(0, "", "");
        };
        using var engine = new LinuxPrintEngine(fixture.Cups, () => fixture.Printers, fixture.Cache, new() { JobTimeoutSeconds = 2 });
        var first = engine.PrintAsync(PrintEngineTests.Pdf());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = engine.PrintAsync(PrintEngineTests.Pdf());
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.All(results, result => Assert.Equal("done", result.Status));
        Assert.Equal(2, submissions);
    }
}
