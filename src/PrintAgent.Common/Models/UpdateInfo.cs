namespace PrintAgent.Common.Models;

public class UpdateInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("downloadUrls")]
    public System.Text.Json.JsonElement? DownloadUrls { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("releaseNotes")]
    public string ReleaseNotes { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; }

    public string GetWindowsUrl()
    {
        if (DownloadUrls?.ValueKind == System.Text.Json.JsonValueKind.Object &&
            DownloadUrls.Value.TryGetProperty("windows", out var win))
            return win.GetString() ?? string.Empty;
        return string.Empty;
    }
}
