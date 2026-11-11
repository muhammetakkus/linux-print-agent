using PrintAgent.Common.Local;
using PrintAgent.UI;
using Xunit;

namespace PrintAgent.Desktop.Tests;

public class PrinterSnapshotTests
{
    [Fact]
    public void Updated_snapshot_replaces_features_and_clears_previous_account_identity()
    {
        var model = new MainViewModel(new FakeLocalClient());
        model.Apply(new AgentSnapshot { Printers = [new() { Name = "queue", ExternalId = "old-account", Capabilities = new() { Duplex = false } }] });
        model.Apply(new AgentSnapshot { Printers = [new() { Name = "queue", ExternalId = "", Capabilities = new() { Duplex = true }, IsActive = false }] });
        Assert.Empty(model.Printers.Single().ExternalId);
        Assert.True(model.Printers.Single().Capabilities!.Duplex);
        Assert.False(model.Printers.Single().IsActive);
    }
}
