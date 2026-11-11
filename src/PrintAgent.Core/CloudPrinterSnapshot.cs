using System.Text.Json;
using System.Text.Json.Serialization;
using PrintAgent.Common.Models;

namespace PrintAgent.Core;

public sealed record CloudPrinterSnapshot(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("connectionType")] string ConnectionType,
    [property: JsonPropertyName("driverName")] string DriverName,
    [property: JsonPropertyName("capabilities")] PrinterCapabilities? Capabilities)
{
    public static CloudPrinterSnapshot[] Create(IEnumerable<PrinterInfo> printers) =>
        printers.Select(p => new CloudPrinterSnapshot(p.Name, p.Status, p.ConnectionType, p.DriverName, p.Capabilities)).ToArray();

    // Compare exactly what the cloud receives; discovery order and local identity are not wire changes.
    public static string Signature(IEnumerable<PrinterInfo> printers) =>
        JsonSerializer.Serialize(Create(printers.OrderBy(p => p.Name, StringComparer.Ordinal)));
}
