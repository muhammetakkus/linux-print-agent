using PrintAgent.Common.Models;
using PrintAgent.Linux;

namespace PrintAgent.Service;

public sealed class PrinterCatalog(ICupsClient cups)
{
    private List<PrinterInfo> _printers = [];
    public List<PrinterInfo> Printers => Volatile.Read(ref _printers);
    public string? DefaultPrinter { get; private set; }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var snapshot = await cups.DiscoverAsync(ct);
        Volatile.Write(ref _printers, snapshot.Printers);
        DefaultPrinter = snapshot.DefaultPrinter;
    }
    public void Clear() { Volatile.Write(ref _printers, []); DefaultPrinter = null; }
}
