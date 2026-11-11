using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PrintAgent.Common.Models;

public class PrinterInfo : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _driverName = string.Empty;
    private string _portName = string.Empty;
    private string _status = "offline";
    private string _connectionType = "Network";
    private bool _isActive = true;
    private string _externalId = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get => _name; set => Set(ref _name, value); }

    [JsonPropertyName("driverName")]
    public string DriverName { get => _driverName; set => Set(ref _driverName, value); }

    [JsonPropertyName("portName")]
    public string PortName { get => _portName; set => Set(ref _portName, value); }

    [JsonPropertyName("status")]
    public string Status { get => _status; set => Set(ref _status, value); }

    [JsonPropertyName("connectionType")]
    public string ConnectionType { get => _connectionType; set => Set(ref _connectionType, value); }

    [JsonPropertyName("isActive")]
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    [JsonPropertyName("externalId")]
    public string ExternalId { get => _externalId; set => Set(ref _externalId, value); }

    // Win32 DeviceCapabilities snapshot (null if enumeration failed/unsupported).
    [JsonPropertyName("capabilities")]
    public PrinterCapabilities? Capabilities { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class PrinterBin
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

public class PrinterPaper
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("width")] public int Width { get; set; }   // 0.1 mm
    [JsonPropertyName("height")] public int Height { get; set; } // 0.1 mm
}

public class PrinterCapabilities
{
    [JsonPropertyName("bins")] public List<PrinterBin> Bins { get; set; } = new();
    [JsonPropertyName("papers")] public List<PrinterPaper> Papers { get; set; } = new();
    [JsonPropertyName("duplex")] public bool Duplex { get; set; }
    [JsonPropertyName("color")] public bool Color { get; set; }
    [JsonPropertyName("collate")] public bool Collate { get; set; }
    [JsonPropertyName("maxCopies")] public int MaxCopies { get; set; }
    [JsonPropertyName("dpis")] public List<string> Dpis { get; set; } = new();
    [JsonPropertyName("fields")] public int Fields { get; set; }
}
