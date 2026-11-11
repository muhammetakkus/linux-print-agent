namespace PrintAgent.Common;

public static class Constants
{
    public const string AppName = "CloudPrintAgent";
    public const string AppVersion = "1.9.5";
    public const string DefaultApiUrl = "https://nuxprint.com";
    public const string MutexName = "Global\\CloudPrintAgentUI";
    public const int ReconnectDelayMs = 1000;
    public const int MaxReconnectDelayMs = 30000;
    public const int ConnectionTimeoutSeconds = 10;
    public const int PrintTimeoutSeconds = 60;
    public const int PrinterPollIntervalMs = 30000;

    public static readonly string[] ExcludedPrinters = new[]
    {
        "Microsoft Print to PDF",
        "Microsoft XPS Document Writer",
        "Fax",
        "Send to OneNote 2016",
        "OneNote (Desktop)",
        "OneNote for Windows 10",
        "OneNote 2016",
        "OneNote 16",
        "AnyDesk Printer",
        "Adobe PDF",
        "Foxit PDF Printer",
        "Foxit Reader PDF Printer"
    };
}
