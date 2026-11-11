namespace PrintAgent.Common.Models;

public class DeviceInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Platform { get; set; } = "windows";
    public bool IsOnline { get; set; }
}
