using System.Globalization;
using System.Text.RegularExpressions;
using PrintAgent.Common.Models;

namespace PrintAgent.Linux;

public static class CupsPrinterMapper
{
    public static PrinterInfo Map(IppGroup attributes)
    {
        var deviceUri = attributes.Text("device-uri") ?? "";
        var state = attributes.Integer("printer-state");
        var accepting = attributes.Boolean("printer-is-accepting-jobs");
        var reasons = attributes.Values("printer-state-reasons").OfType<string>().ToArray();
        var offline = accepting == false || state == 5 || reasons.Any(r => r is "offline" or "offline-report" or "paused" or "shutdown");
        return new PrinterInfo
        {
            Name = attributes.Text("printer-name") ?? "",
            DriverName = attributes.Text("printer-make-and-model") ?? "",
            PortName = deviceUri,
            ConnectionType = deviceUri.StartsWith("usb:", StringComparison.OrdinalIgnoreCase) ? "USB"
                : Uri.TryCreate(deviceUri, UriKind.Absolute, out var uri) && uri.Scheme is "ipp" or "ipps" or "socket" or "lpd" or "dnssd" or "http" or "https" or "smb" ? "Network" : "",
            Status = offline ? "offline" : state is 3 or 4 ? "online" : "unknown",
            IsActive = true,
            Capabilities = ReadCapabilities(attributes)
        };
    }

    private static PrinterCapabilities? ReadCapabilities(IppGroup group)
    {
        if (!group.Attributes.Keys.Any(k => k.EndsWith("-supported", StringComparison.Ordinal) || k == "media-col-database")) return null;
        var caps = new PrinterCapabilities
        {
            Duplex = group.Values("sides-supported").OfType<string>().Any(s => s.StartsWith("two-sided-", StringComparison.Ordinal)),
            Color = group.Boolean("color-supported") == true || group.Values("print-color-mode-supported").Contains("color"),
            Collate = group.Values("multiple-document-handling-supported").Contains("separate-documents-collated-copies"),
            MaxCopies = group.Values("copies-supported").OfType<IppRange>().FirstOrDefault()?.Upper ?? 0,
            Bins = group.Values("media-source-supported").OfType<string>().Select(s => new PrinterBin { Name = s }).ToList()
        };
        foreach (var resolution in group.Values("printer-resolution-supported").OfType<IppResolution>())
        {
            if (resolution.Units is not (3 or 4)) continue;
            var scale = resolution.Units == 4 ? 2.54 : 1;
            caps.Dpis.Add($"{Math.Round(resolution.X * scale)}x{Math.Round(resolution.Y * scale)}");
        }
        foreach (var name in group.Values("media-supported").OfType<string>())
        {
            var paper = new PrinterPaper { Name = name };
            // PWG media keywords contain dimensions; these are reported data, not a guessed paper table.
            var match = Regex.Match(name, @"_(\d+(?:\.\d+)?)x(\d+(?:\.\d+)?)(mm|in)$", RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var scale = match.Groups[3].Value == "in" ? 254 : 10;
                paper.Width = (int)Math.Round(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * scale);
                paper.Height = (int)Math.Round(double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * scale);
            }
            caps.Papers.Add(paper);
        }
        foreach (var media in group.Values("media-col-database").OfType<IppGroup>())
        {
            var name = media.Text("media-size-name") ?? media.Text("media-key");
            var size = media.Values("media-size").OfType<IppGroup>().FirstOrDefault();
            if (name == null || size == null) continue;
            var paper = caps.Papers.FirstOrDefault(p => p.Name == name);
            if (paper == null) { paper = new PrinterPaper { Name = name }; caps.Papers.Add(paper); }
            paper.Width = size.Integer("x-dimension") is int x ? x / 10 : paper.Width;
            paper.Height = size.Integer("y-dimension") is int y ? y / 10 : paper.Height;
        }
        // Windows bin/paper IDs and DC_FIELDS have no IPP equivalent: keep model defaults (0).
        return caps;
    }
}
