using System.Text.Json;
using PrintAgent.Common.Local;
using PrintAgent.CLI;
using Xunit;

namespace PrintAgent.Desktop.Tests;

public class LocalProtocolTests
{
    [Fact]
    public async Task Framed_messages_roundtrip_and_reject_invalid_lengths()
    {
        using var stream = new MemoryStream();
        await LocalProtocol.WriteAsync(stream, new LocalRequest { Method = "login", Email = "test" }, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal("test", (await LocalProtocol.ReadAsync<LocalRequest>(stream, CancellationToken.None)).Email);
        using var invalid = new MemoryStream([0x7f, 0xff, 0xff, 0xff]);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalProtocol.ReadAsync<LocalRequest>(invalid, CancellationToken.None));
    }

    [Fact]
    public void Snapshot_contract_cannot_serialize_agent_access_or_session_tokens()
    {
        var json = JsonSerializer.Serialize(new LocalResponse { State = new() { Customer = new() { ApiKey = "visible-as-in-reference" } } });
        Assert.DoesNotContain("SessionToken", json);
        Assert.DoesNotContain("AccessToken", json);
        Assert.DoesNotContain("\"Token\"", json);
    }

    [Fact]
    public async Task Cli_password_uses_stdin_and_is_not_echoed_or_left_in_request()
    {
        var client = new FakeLocalClient();
        var output = new StringWriter(); var error = new StringWriter();
        var code = await CliApplication.RunAsync(["login", "user", "--password-stdin"], client, new StringReader("test-secret\n"), output, error);
        Assert.Equal(0, code);
        Assert.Equal("test-secret", client.PasswordReceived);
        Assert.Null(client.LastRequest!.Password);
        Assert.DoesNotContain("test-secret", output.ToString() + error);
    }

    [Fact]
    public async Task Cli_rejects_password_arguments_and_unknown_cloud_operations()
    {
        var client = new FakeLocalClient();
        Assert.Equal(2, await CliApplication.RunAsync(["login", "user", "secret"], client, TextReader.Null, TextWriter.Null, TextWriter.Null));
        Assert.Equal(2, await CliApplication.RunAsync(["reprint"], client, TextReader.Null, TextWriter.Null, TextWriter.Null));
        Assert.Null(client.LastRequest);
    }

    [Fact]
    public async Task Cli_reports_unavailable_service_without_starting_an_agent()
    {
        var error = new StringWriter();
        Assert.Equal(2, await CliApplication.RunAsync(["status"], new OfflineClient(), TextReader.Null, TextWriter.Null, error));
        Assert.Contains("Agent unavailable", error.ToString());
    }
}

internal sealed class FakeLocalClient : ILocalAgentClient
{
    public LocalRequest? LastRequest { get; private set; }
    public string? PasswordReceived { get; private set; }
    public AgentSnapshot State { get; } = new() { Running = true, DeviceId = "device", IsAuthenticated = false };
    public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
    {
        LastRequest = request; PasswordReceived = request.Password;
        if (request.Method == "login") State.IsAuthenticated = true;
        if (request.Method == "logout") State.IsAuthenticated = false;
        return Task.FromResult(new LocalResponse { State = State });
    }
}
internal sealed class OfflineClient : ILocalAgentClient
{
    public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default) => throw new IOException("No socket");
}
