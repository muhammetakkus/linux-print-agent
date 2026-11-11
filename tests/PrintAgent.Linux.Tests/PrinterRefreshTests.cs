using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PrintAgent.Common.Models;
using PrintAgent.Linux;
using PrintAgent.Service;
using Xunit;

namespace PrintAgent.Linux.Tests;

public class PrinterRefreshTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rediscovery_preserves_cloud_printer_identity(bool temporaryOutage)
    {
        using var fixture = new HostFixture(); var delay = new WorkerDelay();
        using var host = fixture.Build(delay.WaitAsync);
        await host.StartAsync();
        try
        {
            await fixture.Socket.Registration.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Socket.Handlers["printers:registered"](new HostMessage("[{\"name\":\"queue\",\"externalId\":\"cloud-id\"}]"));
            if (temporaryOutage)
            {
                fixture.Cups.Discover = _ => throw new IOException("Temporary CUPS outage");
                (await delay.NextAsync()).Complete();
                var recovery = await delay.NextAsync();
                fixture.Cups.Discover = _ => Task.FromResult(new CupsSnapshot([new() { Name = "queue", Status = "online" }], "queue"));
                recovery.Complete();
                await delay.NextAsync();
            }
            else
            {
                var before = fixture.Socket.Emitted.Count(e => e.Name == "printer:status-update");
                (await delay.NextAsync()).Complete();
                await delay.NextAsync();
                Assert.Equal(before, fixture.Socket.Emitted.Count(e => e.Name == "printer:status-update"));
            }
            Assert.Equal("cloud-id", host.Services.GetRequiredService<PrinterCatalog>().Printers.Single().ExternalId);
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("capabilities")]
    [InlineData("driver")]
    [InlineData("connection")]
    public async Task Rediscovery_notifies_cloud_when_features_change_without_status_change(string field)
    {
        using var fixture = new HostFixture(); var delay = new WorkerDelay();
        using var host = fixture.Build(delay.WaitAsync);
        await host.StartAsync();
        try
        {
            await fixture.Socket.Registration.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var before = fixture.Socket.Emitted.Count(e => e.Name == "printer:status-update");
            var tick = await delay.NextAsync();
            fixture.Cups.Discover = _ => Task.FromResult(new CupsSnapshot([new PrinterInfo
            {
                Name = "queue", Status = "online",
                Capabilities = field == "capabilities" ? new() { Duplex = true, MaxCopies = 12 } : null,
                DriverName = field == "driver" ? "new-driver" : "",
                ConnectionType = field == "connection" ? "USB" : "Network"
            }], "queue"));
            tick.Complete();
            await delay.NextAsync();
            Assert.Equal(before + 1, fixture.Socket.Emitted.Count(e => e.Name == "printer:status-update"));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Account_logout_clears_existing_printer_identity()
    {
        using var fixture = new HostFixture(); var delay = new WorkerDelay();
        using var host = fixture.Build(delay.WaitAsync);
        await host.StartAsync();
        try
        {
            var catalog = host.Services.GetRequiredService<PrinterCatalog>();
            catalog.Printers.Single().ExternalId = "old-account-id";
            var controller = host.Services.GetRequiredService<LocalAgentController>();
            Assert.True((await controller.ExecuteAsync(new() { Method = "logout" })).Success);
            Assert.Empty(catalog.Printers.Single().ExternalId);
        }
        finally { await host.StopAsync(); }
    }
}
