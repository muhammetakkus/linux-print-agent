using System.Text.Json.Serialization;

namespace PrintAgent.Common.Models;

public class PrinterIdMapping
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("externalId")]
    public string ExternalId { get; set; } = string.Empty;
}
