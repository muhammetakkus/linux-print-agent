using PrintAgent.Common.Models;

namespace PrintAgent.Common.Security;

public sealed class PrintSafetyOptions
{
    public int JobTimeoutSeconds { get; set; } = 60;

    public void Validate()
    {
        if (JobTimeoutSeconds is < 1 or > 300)
            throw new ArgumentException("Invalid print safety configuration.");
    }

    public string? ValidateJob(PrintJobData? job)
    {
        if (job == null) return "Print job is required.";
        if (!Field(job.JobId, 128) || !Field(job.PrinterId, 128) || !Field(job.DocumentName, 256) || !Field(job.PrinterName, 256, true))
            return "Invalid or oversized job identifier, document name or printer name.";
        if (!Field(job.DocumentType, 32)) return "Invalid document type.";
        if (string.IsNullOrEmpty(job.DocumentData)) return "Document is empty.";
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
