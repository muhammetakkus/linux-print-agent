using PrintAgent.Common.Models;

namespace PrintAgent.Linux;

public sealed record CupsSnapshot(List<PrinterInfo> Printers, string? DefaultPrinter);
public sealed record CupsQueueJob(int? Id, string? Name, int? State);

public interface ICupsClient
{
    Task<CupsSnapshot> DiscoverAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CupsQueueJob>> GetQueueAsync(string printer, CancellationToken ct = default);
    Task<CommandResult> SubmitAsync(string printer, string documentName, string file,
        IReadOnlyList<string> options, int copies, bool raw, CancellationToken ct = default);
}

public sealed class CupsClient(IppClient ipp, ICommandRunner runner) : ICupsClient
{
    private static readonly string[] Attributes =
    ["printer-name", "printer-make-and-model", "device-uri", "printer-state", "printer-state-reasons",
     "printer-is-accepting-jobs", "sides-supported", "color-supported", "print-color-mode-supported",
     "media-supported", "media-col-database", "media-source-supported", "copies-supported",
     "multiple-document-handling-supported", "printer-resolution-supported"];
    public event Action<string>? Warning;

    public async Task<CupsSnapshot> DiscoverAsync(CancellationToken ct = default)
    {
        var response = await ipp.QueryAsync(0x4002, "/", [IppAttribute.Text(0x44, "requested-attributes", Attributes)], ct);
        var printers = new List<PrinterInfo>();
        foreach (var group in response.Groups.Where(g => g.Tag == 4))
        {
            var name = group.Text("printer-name");
            if (name == null) continue;
            try
            {
                var details = await ipp.QueryAsync(0x000b, "/printers/" + Uri.EscapeDataString(name),
                    [IppAttribute.Text(0x45, "printer-uri", ipp.PrinterUri(name)), IppAttribute.Text(0x44, "requested-attributes", Attributes)], ct);
                var full = details.Groups.FirstOrDefault(g => g.Tag == 4);
                if (full != null)
                    foreach (var attribute in full.Attributes) group.Attributes[attribute.Key] = attribute.Value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Warning?.Invoke($"Capabilities/status for {name}: {ex.Message}"); }
            printers.Add(CupsPrinterMapper.Map(group));
        }
        string? defaultPrinter;
        try
        {
            var result = await ipp.QueryAsync(0x4001, "/", [IppAttribute.Text(0x44, "requested-attributes", "printer-name")], ct);
            defaultPrinter = result.Groups.FirstOrDefault(g => g.Tag == 4)?.Text("printer-name");
        }
        catch (IppException ex) when (ex.Status == 0x0406) { defaultPrinter = null; }
        return new(printers, defaultPrinter);
    }

    public async Task<IReadOnlyList<CupsQueueJob>> GetQueueAsync(string printer, CancellationToken ct = default)
    {
        var response = await ipp.QueryAsync(0x000a, "/printers/" + Uri.EscapeDataString(printer),
            [IppAttribute.Text(0x45, "printer-uri", ipp.PrinterUri(printer)),
             IppAttribute.Text(0x44, "which-jobs", "not-completed"),
             IppAttribute.Text(0x44, "requested-attributes", "job-id", "job-name", "job-state")], ct);
        return response.Groups.Where(g => g.Tag == 2).Select(g => new CupsQueueJob(g.Integer("job-id"), g.Text("job-name"), g.Integer("job-state"))).ToArray();
    }

    public Task<CommandResult> SubmitAsync(string printer, string documentName, string file,
        IReadOnlyList<string> options, int copies, bool raw, CancellationToken ct = default)
    {
        var arguments = new List<string> { "-d", printer, "-t", string.IsNullOrEmpty(documentName) && raw ? "Nuxprint Raw Job" : documentName };
        if (ipp.SocketPath == null && ipp.Server.Scheme == "https") arguments.Add("-E");
        if (!raw && copies > 1) arguments.AddRange(["-n", copies.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (raw) arguments.AddRange(["-o", "raw"]);
        else foreach (var option in options) arguments.AddRange(["-o", option]);
        arguments.AddRange(["--", file]);
        var server = OperatingSystem.IsLinux() && ipp.SocketPath != null ? ipp.SocketPath : ipp.Server.Authority;
        return runner.RunAsync("/usr/bin/lp", arguments, new Dictionary<string, string> { ["LC_ALL"] = "C", ["CUPS_SERVER"] = server },
            TimeSpan.FromSeconds(60), ct);
    }
}
