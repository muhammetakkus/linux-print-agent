using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintAgent.Common.Models;
using PrintAgent.Core;
using PrintAgent.Linux;
using PrintAgent.Service;
using SocketIOClient;
using Xunit;

namespace PrintAgent.Linux.Tests;

public class AgentWorkerTests
{
    [Fact]
    public async Task Host_without_credentials_stays_running_headless_without_cloud_handshake()
    {
        using var fixture = new HostFixture();
        var delay = new WorkerDelay();
        using var host = fixture.Build(delay.WaitAsync, signedIn: false);
        await host.StartAsync();
        await delay.NextAsync();
        Assert.False(host.Services.GetRequiredService<CloudSession>().IsAuthenticated);
        Assert.False(fixture.Socket.Registration.Task.IsCompleted);
        Assert.False(fixture.Socket.IsConnected);
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Actual_GenericHost_starts_headless_registers_linux_prints_ACKs_and_stops()
    {
        using var fixture = new HostFixture();
        using var host = fixture.Build();
        await host.StartAsync();
        var registration = await fixture.Socket.Registration.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("linux", JsonDocument.Parse(registration).RootElement.GetProperty("platform").GetString());
        Assert.True(host.Services.GetRequiredService<CloudConnection>().IsConnected);
        Assert.Equal("queue", host.Services.GetRequiredService<PrinterCatalog>().DefaultPrinter);
        var job = PrintEngineTests.Pdf();
        var message = new HostMessage(JsonSerializer.Serialize(job));
        await fixture.Socket.Handlers["print:job"](message);
        Assert.Equal("done", JsonDocument.Parse(message.Callback!).RootElement.GetProperty("status").GetString());
        Assert.Single(fixture.Cups.Submissions);
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(host.Services.GetRequiredService<CloudConnection>().IsConnected);
        Assert.DoesNotContain(fixture.Socket.Emitted, item => item.Name.Contains("heartbeat"));
    }

    [Fact]
    public async Task Printer_poll_is_30_seconds_and_changed_status_uses_existing_event()
    {
        using var fixture = new HostFixture();
        var delay = new WorkerDelay();
        using var host = fixture.Build(delay.WaitAsync);
        await host.StartAsync();
        await fixture.Socket.Registration.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var tick = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), tick.Duration);
        fixture.Cups.Discover = _ => Task.FromResult(new CupsSnapshot([new() { Name = "queue", Status = "offline" }], "queue"));
        tick.Complete();
        await delay.NextAsync();
        var status = fixture.Socket.Emitted.Last(e => e.Name == "printer:status-update");
        Assert.Equal("offline", JsonDocument.Parse(status.Json).RootElement.GetProperty("printers")[0].GetProperty("status").GetString());
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Second_host_cannot_take_same_device_lock_until_first_host_is_disposed()
    {
        using var fixture = new HostFixture();
        using var first = fixture.Build();
        using var second = fixture.Build();
        await first.StartAsync();
        await Assert.ThrowsAsync<IOException>(() => second.StartAsync());
        await first.StopAsync();
        first.Dispose();
        using var lease = SingleInstanceLease.Acquire(Path.Combine(fixture.Options.RuntimeDirectory, "agent.lock"));
    }

    [Fact]
    public async Task CUPS_outage_does_not_erase_auth_or_trigger_print_retries()
    {
        using var fixture = new HostFixture();
        fixture.Cups.Discover = _ => throw new HttpRequestException("CUPS unavailable");
        using var host = fixture.Build();
        await host.StartAsync();
        await fixture.Socket.Registration.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(host.Services.GetRequiredService<PrinterCatalog>().Printers);
        Assert.True(host.Services.GetRequiredService<CloudSession>().IsAuthenticated);
        var message = new HostMessage(JsonSerializer.Serialize(PrintEngineTests.Pdf()));
        await fixture.Socket.Handlers["print:job"](message);
        Assert.Equal("failed", JsonDocument.Parse(message.Callback!).RootElement.GetProperty("status").GetString());
        Assert.Empty(fixture.Cups.Submissions);
        await host.StopAsync();
    }

    [Fact]
    public async Task Stop_waits_for_active_submission_and_does_not_resend()
    {
        using var fixture = new HostFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Cups.Submit = (_, _, _, _, _, _) => { entered.TrySetResult(); return finish.Task; };
        using var host = fixture.Build();
        await host.StartAsync();
        await fixture.Socket.Registration.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var printing = fixture.Socket.Handlers["print:job"](new HostMessage(JsonSerializer.Serialize(PrintEngineTests.Pdf())));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = host.StopAsync();
        Assert.False(stopping.IsCompleted);
        finish.SetResult(new CommandResult(0, "accepted", ""));
        await Task.WhenAll(stopping, printing).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Cups.Submissions);
    }
}

internal sealed class HostFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nuxprint-host-" + Guid.NewGuid().ToString("N"));
    public AgentOptions Options { get; }
    public FakeCups Cups { get; } = new();
    public HostSocket Socket { get; } = new();
    public HostFixture() => Options = new() { StateDirectory = Path.Combine(_root, "state"), CacheDirectory = Path.Combine(_root, "cache"), RuntimeDirectory = Path.Combine(_root, "run") };
    public IHost Build(Func<TimeSpan, CancellationToken, Task>? delay = null, bool signedIn = true)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddNuxPrintAgent(Options);
        builder.Services.AddSingleton<ICupsClient>(Cups);
        builder.Services.AddSingleton<ICredentialStore>(new MemoryStore(signedIn));
        builder.Services.AddSingleton<Func<string, SocketIOOptions, IAgentSocket>>((_, _) => Socket);
        if (delay != null) builder.Services.AddSingleton(delay);
        return builder.Build();
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

internal sealed class MemoryStore(bool signedIn = true) : ICredentialStore
{
    private AuthData? _auth = signedIn ? new() { SessionToken = "test-only", Token = "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":4102444800}")).TrimEnd('=') + ".signature" } : null;
    public AuthData? Load() => _auth;
    public void Save(AuthData auth) => _auth = auth;
    public void Clear() => _auth = null;
}

internal sealed class HostSocket : IAgentSocket
{
    public event Func<Task>? Connected;
    public event Action? Disconnected;
    public event Action<string>? Error { add { } remove { } }
    public bool IsConnected { get; private set; }
    public TaskCompletionSource<string> Registration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<(string Name, string Json)> Emitted { get; } = new();
    public Dictionary<string, Func<IAgentSocketMessage, Task>> Handlers { get; } = [];
    public void On(string name, Func<IAgentSocketMessage, Task> handler) => Handlers[name] = handler;
    public async Task ConnectAsync() { IsConnected = true; if (Connected != null) await Connected(); }
    public Task DisconnectAsync() { IsConnected = false; Disconnected?.Invoke(); return Task.CompletedTask; }
    public Task EmitAsync(string name, object payload)
    {
        var json = JsonSerializer.Serialize(payload); Emitted.Enqueue((name, json));
        if (name == "agent:register") Registration.TrySetResult(json);
        return Task.CompletedTask;
    }
    public void Dispose() => IsConnected = false;
}

internal sealed class HostMessage(string json) : IAgentSocketMessage
{
    public string? Callback { get; private set; }
    public T GetValue<T>() => JsonSerializer.Deserialize<T>(json)!;
    public Task CallbackAsync(PrintJobResult result) { Callback = JsonSerializer.Serialize(result); return Task.CompletedTask; }
}

internal sealed class WorkerDelay
{
    private readonly Channel<WorkerTick> _ticks = Channel.CreateUnbounded<WorkerTick>();
    public Task WaitAsync(TimeSpan duration, CancellationToken ct)
    {
        var tick = new WorkerTick(duration); _ticks.Writer.TryWrite(tick); return tick.Task.WaitAsync(ct);
    }
    public Task<WorkerTick> NextAsync() => _ticks.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
}
internal sealed class WorkerTick(TimeSpan duration)
{
    private readonly TaskCompletionSource _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TimeSpan Duration { get; } = duration;
    public Task Task => _source.Task;
    public void Complete() => _source.TrySetResult();
}
