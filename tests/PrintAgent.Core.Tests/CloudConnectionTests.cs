using System.Collections.Concurrent;
using System.Text.Json;
using PrintAgent.Common.Models;
using SocketIOClient;
using Xunit;

namespace PrintAgent.Core.Tests;

public class CloudConnectionTests
{
    [Fact]
    public async Task Unsigned_agent_waits_one_second_without_opening_socket()
    {
        using var session = CloudSessionTests.Create(new TestCredentialStore(), new StubHttp((_, _) => throw new InvalidOperationException()));
        var delay = new TestDelay();
        using var connection = new CloudConnection(session, () => [], _ => throw new InvalidOperationException(),
            (_, _) => throw new Xunit.Sdk.XunitException("Unauthenticated socket"), delay.WaitAsync);
        using var cts = new CancellationTokenSource();
        var run = connection.RunAsync(cts.Token);
        Assert.Equal(1000, (await delay.NextAsync()).Milliseconds);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Connection_uses_original_handshake_registration_and_only_known_event_handlers()
    {
        using var session = SignedIn();
        var socket = new TestSocket();
        string? actualUrl = null;
        SocketIOOptions? options = null;
        using var connection = new CloudConnection(session,
            () => [new PrinterInfo { Name = "queue", DriverName = "driver", PortName = "private-port", ExternalId = "private-id" }],
            _ => throw new InvalidOperationException("No printing in this stage"),
            (url, opts) => { actualUrl = url; options = opts; return socket; });
        using var cts = new CancellationTokenSource();
        var run = connection.RunAsync(cts.Token);
        await socket.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("https://nuxprint.com/agent", actualUrl);
        Assert.Equal("/api/socket.io", options!.Path);
        Assert.False(options.Reconnection);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ConnectionTimeout);
        Assert.Equal("{\"token\":\"" + session.Auth!.Token + "\"}", JsonSerializer.Serialize(options.Auth));
        Assert.Equal(new[] { "print:job", "printers:registered" }, socket.Handlers.Keys.Order());
        Assert.Equal("{\"deviceId\":\"device-1\",\"deviceName\":\"host-1\",\"platform\":\"windows\",\"printers\":[{\"name\":\"queue\",\"status\":\"offline\",\"connectionType\":\"Network\",\"driverName\":\"driver\",\"capabilities\":null}]}", socket.Emitted.Single().Json);
        Assert.True(connection.IsConnected);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Printer_mapping_and_status_emit_keep_exact_source_fields()
    {
        using var session = SignedIn();
        var socket = new TestSocket();
        var printer = new PrinterInfo { Name = "queue" };
        using var connection = Create(session, socket, () => [printer]);
        using var cts = new CancellationTokenSource();
        var run = connection.RunAsync(cts.Token);
        await socket.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await socket.ReceiveAsync("printers:registered", "[{\"name\":\"queue\",\"externalId\":\"server-id\"}]");
        Assert.Equal("server-id", connection.Printers.Single().ExternalId);
        await connection.EmitPrinterStatusAsync();
        Assert.Equal("printer:status-update", socket.Emitted.Last().Name);
        Assert.Equal("{\"printers\":[{\"name\":\"queue\",\"status\":\"offline\",\"connectionType\":\"Network\",\"driverName\":\"\",\"capabilities\":null}]}", socket.Emitted.Last().Json);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Print_event_passes_contract_to_handler_and_returns_result_in_same_callback()
    {
        using var session = SignedIn();
        var socket = new TestSocket();
        PrintJobData? received = null;
        using var connection = new CloudConnection(session, () => [], job =>
        {
            received = job;
            return Task.FromResult(new PrintJobResult { Status = "failed", ErrorMessage = "test handler" });
        }, (_, _) => socket);
        using var cts = new CancellationTokenSource();
        var run = connection.RunAsync(cts.Token);
        await socket.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var message = await socket.ReceiveAsync("print:job", "{\"jobId\":\"j-1\",\"printerId\":\"p-1\",\"printerName\":\"queue\",\"documentData\":\"AQI=\",\"copies\":2}");
        Assert.Equal("j-1", received!.JobId);
        Assert.Equal("pdf", received.DocumentType);
        Assert.Equal(2, received.Copies);
        Assert.Equal("{\"status\":\"failed\",\"errorMessage\":\"test handler\"}", message.CallbackJson);
        Assert.Single(socket.Emitted); // No invented result event or heartbeat.
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("bad-json")]
    [InlineData("{}")]
    public async Task Bad_payload_or_handler_exception_uses_failed_callback(string payload)
    {
        using var session = SignedIn();
        var socket = new TestSocket();
        using var connection = new CloudConnection(session, () => [], _ => throw new InvalidOperationException("test-only failure"), (_, _) => socket);
        using var cts = new CancellationTokenSource();
        var run = connection.RunAsync(cts.Token);
        await socket.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var response = await socket.ReceiveAsync("print:job", payload);
        using var json = JsonDocument.Parse(response.CallbackJson!);
        Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
        Assert.NotEmpty(json.RootElement.GetProperty("errorMessage").GetString()!);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("Session revoked", false)]
    [InlineData("Invalid token", true)]
    [InlineData("other error", true)]
    public async Task Socket_error_keeps_source_revocation_rule(string error, bool remainsAuthenticated)
    {
        using var session = SignedIn();
        var socket = new TestSocket();
        var delay = new TestDelay();
        using var connection = Create(session, socket, delay: delay.WaitAsync);
        using var cts = new CancellationTokenSource();
        var run = connection.RunAsync(cts.Token);
        await socket.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        socket.RaiseError(error);
        if (remainsAuthenticated) await delay.NextAsync();
        Assert.Equal(remainsAuthenticated, session.IsAuthenticated);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Failed_connections_back_off_exponentially_with_jitter_and_30_second_cap()
    {
        using var session = SignedIn();
        var delay = new TestDelay();
        using var connection = new CloudConnection(session, () => [], _ => throw new InvalidOperationException(),
            (_, _) => new TestSocket { ConnectFailure = new HttpRequestException("offline") }, delay.WaitAsync, new HighestRandom());
        using var cts = new CancellationTokenSource();
        var run = connection.RunAsync(cts.Token);
        foreach (var expected in new[] { 2499, 4999, 9999, 19999, 30000, 30000 })
        {
            var wait = await delay.NextAsync();
            Assert.Equal(expected, wait.Milliseconds);
            wait.Release();
        }
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.IsAuthenticated);
    }

    [Fact]
    public async Task Refresh_failure_retries_without_jitter_and_without_erasing_session()
    {
        using var session = CloudSessionTests.Create(new TestCredentialStore(), new StubHttp((_, _) => throw new HttpRequestException("offline")));
        session.SetAuth(new AuthData { SessionToken = "saved" });
        var delay = new TestDelay();
        using var connection = new CloudConnection(session, () => [], _ => throw new InvalidOperationException(),
            (_, _) => throw new Xunit.Sdk.XunitException("Socket must not connect"), delay.WaitAsync, new HighestRandom());
        using var cts = new CancellationTokenSource();
        var run = connection.RunAsync(cts.Token);
        var wait = await delay.NextAsync();
        Assert.Equal(2000, wait.Milliseconds);
        Assert.Equal("saved", session.Auth!.SessionToken);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Disconnect_reconnects_after_one_second_without_forcing_token_refresh()
    {
        using var session = SignedIn();
        var delay = new TestDelay();
        var first = new TestSocket();
        var second = new TestSocket();
        var sockets = new Queue<TestSocket>([first, second]);
        using var connection = new CloudConnection(session, () => [], _ => throw new InvalidOperationException(),
            (_, _) => sockets.Dequeue(), delay.WaitAsync, new ZeroRandom());
        using var cts = new CancellationTokenSource();
        var run = connection.RunAsync(cts.Token);
        await first.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        first.RaiseDisconnected();
        var wait = await delay.NextAsync();
        Assert.Equal(1000, wait.Milliseconds);
        Assert.False(connection.IsConnected);
        wait.Release();
        await second.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(connection.IsConnected);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static CloudSession SignedIn()
    {
        var session = CloudSessionTests.Create(new TestCredentialStore(), new StubHttp((_, _) => throw new Xunit.Sdk.XunitException("Unexpected HTTP")));
        session.SetAuth(new AuthData { SessionToken = "saved", Token = CloudSessionTests.FutureJwt() });
        return session;
    }

    private static CloudConnection Create(CloudSession session, TestSocket socket, Func<List<PrinterInfo>>? printers = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(session, printers ?? (() => []), _ => throw new InvalidOperationException(), (_, _) => socket, delay);
}

internal sealed class TestSocket : IAgentSocket
{
    public event Func<Task>? Connected;
    public event Action? Disconnected;
    public event Action<string>? Error;
    public bool IsConnected { get; private set; }
    public Exception? ConnectFailure { get; init; }
    public TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Dictionary<string, Func<IAgentSocketMessage, Task>> Handlers { get; } = new();
    public ConcurrentQueue<(string Name, string Json)> Emitted { get; } = new();
    public void On(string name, Func<IAgentSocketMessage, Task> handler) => Handlers.Add(name, handler);
    public async Task ConnectAsync()
    {
        if (ConnectFailure != null) throw ConnectFailure;
        IsConnected = true;
        if (Connected != null) await Connected();
    }
    public Task EmitAsync(string name, object payload)
    {
        Emitted.Enqueue((name, JsonSerializer.Serialize(payload)));
        if (name == "agent:register") Registered.TrySetResult();
        return Task.CompletedTask;
    }
    public async Task<TestMessage> ReceiveAsync(string name, string payload)
    {
        var message = new TestMessage(payload);
        await Handlers[name](message);
        return message;
    }
    public void RaiseError(string error) => Error?.Invoke(error);
    public void RaiseDisconnected() { IsConnected = false; Disconnected?.Invoke(); }
    public Task DisconnectAsync() { RaiseDisconnected(); return Task.CompletedTask; }
    public void Dispose() => IsConnected = false;
}

internal sealed class TestMessage(string payload) : IAgentSocketMessage
{
    public string? CallbackJson { get; private set; }
    public T GetValue<T>() => JsonSerializer.Deserialize<T>(payload)!;
    public Task CallbackAsync(PrintJobResult result) { CallbackJson = JsonSerializer.Serialize(result); return Task.CompletedTask; }
}

internal sealed class TestDelay
{
    private readonly System.Threading.Channels.Channel<DelayWait> _waits = System.Threading.Channels.Channel.CreateUnbounded<DelayWait>();
    public Task WaitAsync(TimeSpan time, CancellationToken ct)
    {
        var wait = new DelayWait((int)time.TotalMilliseconds);
        _waits.Writer.TryWrite(wait);
        return wait.Task.WaitAsync(ct);
    }
    public async Task<DelayWait> NextAsync() => await _waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
}

internal sealed class DelayWait(int milliseconds)
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Milliseconds { get; } = milliseconds;
    public Task Task => _completion.Task;
    public void Release() => _completion.TrySetResult();
}
internal sealed class HighestRandom : Random { public override int Next(int minValue, int maxValue) => maxValue - 1; }
internal sealed class ZeroRandom : Random { public override int Next(int minValue, int maxValue) => minValue; }
