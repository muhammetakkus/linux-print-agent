using System.Text.Json;
using PrintAgent.Common.Models;
using Xunit;

namespace PrintAgent.Core.Tests;

public class ModelContractTests
{
    private static readonly JsonSerializerOptions HttpJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void Local_log_and_history_keep_original_case_defaults_and_timestamp_kind()
    {
        var timestamp = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal("{\"JobId\":\"\",\"DocumentName\":\"\",\"PrinterName\":\"\",\"Status\":\"printing\",\"ReceivedAt\":\"2026-10-21T12:00:00\",\"CompletedAt\":null,\"ErrorMessage\":null}", JsonSerializer.Serialize(new PrintJobRecord { ReceivedAt = timestamp }));
        Assert.Equal("{\"Timestamp\":\"2026-10-21T12:00:00\",\"Level\":\"Info\",\"Message\":\"\",\"JobId\":null}", JsonSerializer.Serialize(new LogEntry { Timestamp = timestamp }));
    }

    [Fact]
    public void Missing_job_fields_keep_Windows_defaults_and_null_options()
    {
        var job = JsonSerializer.Deserialize<PrintJobData>("{}")!;
        Assert.Equal("{\"jobId\":\"\",\"printerId\":\"\",\"printerName\":\"\",\"documentType\":\"pdf\",\"documentData\":\"\",\"documentName\":\"\",\"copies\":1,\"options\":null}", JsonSerializer.Serialize(job));
    }

    [Fact]
    public void Nullable_options_and_result_fields_are_not_silently_removed()
    {
        Assert.Equal("{\"duplex\":null,\"paperSize\":null,\"tray\":null,\"color\":null,\"orientation\":null,\"fitToPage\":null,\"pages\":null}", JsonSerializer.Serialize(new PrintJobOptions()));
        Assert.Equal("{\"status\":\"failed\",\"errorMessage\":null}", JsonSerializer.Serialize(new PrintJobResult()));
    }

    [Fact]
    public void Printer_wire_defaults_and_capability_types_match_reference()
    {
        Assert.Equal("{\"name\":\"\",\"driverName\":\"\",\"portName\":\"\",\"status\":\"offline\",\"connectionType\":\"Network\",\"isActive\":true,\"externalId\":\"\",\"capabilities\":null}", JsonSerializer.Serialize(new PrinterInfo()));
        Assert.Equal("{\"bins\":[],\"papers\":[],\"duplex\":false,\"color\":false,\"collate\":false,\"maxCopies\":0,\"dpis\":[],\"fields\":0}", JsonSerializer.Serialize(new PrinterCapabilities()));
    }

    [Fact]
    public void Auth_storage_keeps_PascalCase_and_computed_legacy_property()
    {
        Assert.Equal("{\"SessionToken\":\"\",\"Token\":\"old\",\"Customer\":null,\"IsLegacy\":true}", JsonSerializer.Serialize(new AuthData { Token = "old" }));
        var auth = JsonSerializer.Deserialize<AgentSessionResponse>("{\"sessionToken\":\"session\",\"accessToken\":\"access\",\"customer\":{\"id\":\"c\",\"email\":\"e\",\"name\":\"n\",\"apiKey\":\"k\"}}", HttpJson)!;
        Assert.Equal("access", auth.AccessToken);
        Assert.Equal("k", auth.Customer!.ApiKey);
    }

    [Fact]
    public void Device_defaults_are_preserved_including_original_platform()
    {
        Assert.Equal("{\"deviceId\":\"\",\"deviceName\":\"\",\"platform\":\"windows\",\"isOnline\":false}", JsonSerializer.Serialize(new DeviceInfo(), HttpJson));
    }

    [Fact]
    public void Explicit_print_options_roundtrip_without_type_coercion()
    {
        const string json = "{\"jobId\":\"job-1\",\"printerId\":\"p-1\",\"printerName\":\"queue\",\"documentType\":\"raw\",\"documentData\":\"AQI=\",\"documentName\":\"label\",\"copies\":2,\"options\":{\"duplex\":\"short-edge\",\"paperSize\":\"A4\",\"tray\":\"2\",\"color\":false,\"orientation\":\"landscape\",\"fitToPage\":false,\"pages\":\"1,3-5\"}}";
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<PrintJobData>(json)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PrintJobData>("{\"copies\":\"2\"}"));
    }

    [Fact]
    public void Printer_change_notification_preserves_no_change_behavior()
    {
        var printer = new PrinterInfo();
        var names = new List<string?>();
        printer.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        printer.Name = "queue";
        printer.Name = "queue";
        Assert.Equal(new[] { "Name" }, names);
    }
}
