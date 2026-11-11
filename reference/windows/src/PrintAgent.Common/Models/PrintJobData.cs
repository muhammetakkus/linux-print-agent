using System.Text.Json.Serialization;

namespace PrintAgent.Common.Models;

public class PrintJobData
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("printerId")]
    public string PrinterId { get; set; } = string.Empty;

    [JsonPropertyName("printerName")]
    public string PrinterName { get; set; } = string.Empty;

    [JsonPropertyName("documentType")]
    public string DocumentType { get; set; } = "pdf";

    [JsonPropertyName("documentData")]
    public string DocumentData { get; set; } = string.Empty;

    [JsonPropertyName("documentName")]
    public string DocumentName { get; set; } = string.Empty;

    [JsonPropertyName("copies")]
    public int Copies { get; set; } = 1;

    [JsonPropertyName("options")]
    public PrintJobOptions? Options { get; set; }
}

// Optional per-job print settings. Applied to PDF jobs only (mapped to the
// SumatraPDF -print-settings string); raw jobs carry settings in the document.
public class PrintJobOptions
{
    [JsonPropertyName("duplex")]
    public string? Duplex { get; set; } // one-sided | long-edge | short-edge

    [JsonPropertyName("paperSize")]
    public string? PaperSize { get; set; } // e.g. A4, A5, Letter

    [JsonPropertyName("tray")]
    public string? Tray { get; set; } // input tray / bin

    [JsonPropertyName("color")]
    public bool? Color { get; set; } // false => monochrome

    [JsonPropertyName("orientation")]
    public string? Orientation { get; set; } // portrait | landscape

    [JsonPropertyName("fitToPage")]
    public bool? FitToPage { get; set; } // default true

    [JsonPropertyName("pages")]
    public string? Pages { get; set; } // page-range, e.g. "1,3-5"
}

public class PrintJobResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "failed";

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
