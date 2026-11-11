using System.Buffers.Binary;
using System.Text.Json;
using PrintAgent.Common.Models;

namespace PrintAgent.Common.Local;

public sealed class LocalRequest
{
    public int Version { get; set; } = 1;
    public string Method { get; set; } = "status";
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? PrinterName { get; set; }
    public int Offset { get; set; }
    public int PageSize { get; set; } = 100;
}

public sealed class AgentSnapshot
{
    public bool Running { get; set; }
    public bool IsAuthenticated { get; set; }
    public bool IsConnected { get; set; }
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Version { get; set; } = Constants.AppVersion;
    public CustomerInfo? Customer { get; set; }
    public List<PrinterInfo> Printers { get; set; } = [];
    public List<PrintJobRecord> Jobs { get; set; } = [];
    public List<LogEntry> Logs { get; set; } = [];
    public string? DefaultPrinter { get; set; }
    public int TotalJobs { get; set; }
    public int TotalLogs { get; set; }
}

public sealed class LocalResponse
{
    public bool Success { get; set; } = true;
    public string? Error { get; set; }
    public AgentSnapshot? State { get; set; }
    public UpdateInfo? Version { get; set; }
    public PrintJobResult? PrintResult { get; set; }
}

public static class LocalProtocol
{
    public const string DefaultSocketPath = "/run/nuxprint-agent/control.sock";
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        using var bounded = new BoundedBuffer();
        JsonSerializer.Serialize(bounded, value);
        var bytes = bounded.ToArray();
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private sealed class BoundedBuffer : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > MaxFrameBytes) throw new InvalidDataException("Local message exceeds size limit.");
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > MaxFrameBytes) throw new InvalidDataException("Local message exceeds size limit.");
            base.Write(buffer);
        }
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct, int maxBytes = MaxFrameBytes)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > Math.Min(maxBytes, MaxFrameBytes)) throw new InvalidDataException("Invalid local message length.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Empty local message.");
    }
}
