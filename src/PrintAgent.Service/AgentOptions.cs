namespace PrintAgent.Service;

public sealed class AgentOptions
{
    public string StateDirectory { get; set; } = "/var/lib/nuxprint-agent";
    public string CacheDirectory { get; set; } = "/var/cache/nuxprint-agent";
    public string RuntimeDirectory { get; set; } = "/run/nuxprint-agent";
    public string CupsServer { get; set; } = "http://localhost:631";
    public string? CupsSocketPath { get; set; } = "/run/cups/cups.sock";
    public PrintAgent.Common.Security.PrintSafetyOptions PrintSafety { get; set; } = new();
    public int MaxLocalClients { get; set; } = 16;
    public int LocalRequestTimeoutSeconds { get; set; } = 90;
}
