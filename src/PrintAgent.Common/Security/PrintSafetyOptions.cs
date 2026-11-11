using PrintAgent.Common.Models;

namespace PrintAgent.Common.Security;

public sealed class PrintSafetyOptions
{
    public int MaxCopiesPerJob { get; set; } = 100;
    public int MaxDocumentBytes { get; set; } = 20 * 1024 * 1024;
    public int JobTimeoutSeconds { get; set; } = 60;
    public int MaxCopiesPerMinute { get; set; } = 1000;
    public int MaxJobsPerMinute { get; set; } = 60;
    public bool AllowRaw { get; set; } = true;
    public string[] AllowedRawTypes { get; set; } = ["raw", "zpl", "escpos", "escp", "pcl", "postscript", "ps", "epl", "tspl", "cpcl", "text"];

    public void Validate()
    {
        if (MaxCopiesPerJob is < 1 or > 10000 || MaxDocumentBytes is < 1 or > 100 * 1024 * 1024 ||
            JobTimeoutSeconds is < 1 or > 300 || MaxCopiesPerMinute < MaxCopiesPerJob ||
            MaxCopiesPerMinute > 100000 || MaxJobsPerMinute is < 1 or > 10000 || AllowedRawTypes == null)
            throw new ArgumentException("Invalid print safety configuration.");
    }

    public string? ValidateJob(PrintJobData? job, int? printerMaxCopies = null)
    {
        if (job == null) return "Print job is required.";
        if (!Field(job.JobId, 128, true) || !Field(job.PrinterId, 128) || !Field(job.DocumentName, 256) || !Field(job.PrinterName, 256, true))
            return "Invalid or oversized job identifier, document name or printer name.";
        var maximum = printerMaxCopies > 0 ? Math.Min(MaxCopiesPerJob, printerMaxCopies.Value) : MaxCopiesPerJob;
        if (job.Copies < 1 || job.Copies > maximum) return $"Copies must be between 1 and {maximum}.";
        if (!Field(job.DocumentType, 32, true)) return "Invalid document type.";
        if (!job.DocumentType.Equals("pdf", StringComparison.OrdinalIgnoreCase) &&
            (!AllowRaw || !AllowedRawTypes.Contains(job.DocumentType, StringComparer.OrdinalIgnoreCase))) return "Document type is not allowed.";
        if (string.IsNullOrEmpty(job.DocumentData) || job.DocumentData.Length > ((long)MaxDocumentBytes + 2) / 3 * 4)
            return "Document is empty or exceeds the size limit.";
        if (job.Options is { } o && (!Field(o.PaperSize, 64) || !Field(o.Tray, 64) || !Field(o.Pages, 256) || !Field(o.Orientation, 32) || !Field(o.Duplex, 32)))
            return "Invalid or oversized print options.";
        if (job.Options is { } settings && (!Keyword(settings.PaperSize) || !Keyword(settings.Tray) ||
            settings.Orientation is not (null or "" or "portrait" or "landscape") ||
            settings.Duplex is not (null or "" or "one-sided" or "long-edge" or "short-edge") ||
            settings.Pages?.Any(c => !char.IsAsciiDigit(c) && c is not (',' or '-')) == true))
            return "Print options contain unsupported values.";
        return null;
    }
    private static bool Keyword(string? value) => value == null || value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    private static bool Field(string? value, int maximum, bool required = false) =>
        (!required || !string.IsNullOrWhiteSpace(value)) && (value == null || value.Length <= maximum && !value.Any(char.IsControl));
}

public static class SafeText
{
    public static string Limit(string? value, int maximum = 1024) => string.IsNullOrEmpty(value) ? "" :
        new string(value.Take(maximum).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
