using PrintAgent.Linux;
using Xunit;

namespace PrintAgent.Linux.Tests;

public class PrinterMappingTests
{
    [Theory]
    [InlineData(3, true, "online")]
    [InlineData(4, true, "online")]
    [InlineData(5, true, "offline")]
    [InlineData(3, false, "offline")]
    [InlineData(null, true, "unknown")]
    public void Ipp_state_mapping_does_not_invent_ready_state(int? state, bool accepting, string expected)
    {
        var group = new IppGroup(4);
        group.Attributes["printer-name"] = ["queue"];
        group.Attributes["printer-is-accepting-jobs"] = [accepting];
        if (state != null) group.Attributes["printer-state"] = [state.Value];
        var printer = CupsPrinterMapper.Map(group);
        Assert.Equal(expected, printer.Status);
        Assert.Empty(printer.DriverName);
        Assert.Null(printer.Capabilities);
    }

    [Fact]
    public void Usb_network_and_unknown_device_URIs_are_distinguished()
    {
        foreach (var pair in new[] { ("usb://Vendor/Device", "USB"), ("ipp://host/ipp/print", "Network"), ("socket://host", "Network"), ("", "") })
        {
            var group = new IppGroup(4);
            group.Attributes["device-uri"] = [pair.Item1];
            Assert.Equal(pair.Item2, CupsPrinterMapper.Map(group).ConnectionType);
        }
    }

    [Fact]
    public void Capabilities_use_only_reported_dimensions_and_no_fake_Windows_identifiers()
    {
        var group = new IppGroup(4);
        group.Attributes["printer-name"] = ["queue"];
        group.Attributes["sides-supported"] = ["one-sided", "two-sided-long-edge"];
        group.Attributes["print-color-mode-supported"] = ["monochrome", "color"];
        group.Attributes["media-supported"] = ["iso_a4_210x297mm", "unknown-media"];
        group.Attributes["media-source-supported"] = ["main", "manual"];
        group.Attributes["copies-supported"] = [new IppRange(1, 99)];
        group.Attributes["printer-resolution-supported"] = [new IppResolution(300, 600, 3)];
        var caps = CupsPrinterMapper.Map(group).Capabilities!;
        Assert.True(caps.Duplex);
        Assert.True(caps.Color);
        Assert.Equal(99, caps.MaxCopies);
        Assert.Equal("300x600", Assert.Single(caps.Dpis));
        Assert.Equal(2100, caps.Papers[0].Width);
        Assert.Equal(2970, caps.Papers[0].Height);
        Assert.Equal(0, caps.Papers[1].Width);
        Assert.All(caps.Papers, p => Assert.Equal(0, p.Id));
        Assert.All(caps.Bins, b => Assert.Equal(0, b.Id));
        Assert.Equal(0, caps.Fields);
    }
}
