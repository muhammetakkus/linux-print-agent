namespace PrintAgent.Common.Models;

public class PrintJobRecord
{
    public string JobId { get; set; } = string.Empty;
    public string DocumentName { get; set; } = string.Empty;
    public string PrinterName { get; set; } = string.Empty;
    public string Status { get; set; } = "printing";
    public DateTime ReceivedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
