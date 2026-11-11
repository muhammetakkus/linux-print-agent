using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintAgent.CLI;
using PrintAgent.Common.Local;
using PrintAgent.Common.Models;
using PrintAgent.Core;
using PrintAgent.Linux;
using PrintAgent.Service;
using SocketIOClient;
using Xunit;

namespace PrintAgent.Desktop.Tests;

public class IpcIntegrationTests
{
    [Fact]
    public async Task Real_Unix_socket_cli_login_status_disconnect_and_logout_share_one_agent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "np-" + Guid.NewGuid().ToString("N")[..8]);
        var options = new AgentOptions { StateDirectory = Path.Combine(directory, "s"), CacheDirectory = Path.Combine(directory, "c"), RuntimeDirectory = Path.Combine(directory, "r") };
        var store = new TestStore(); var connections = 0;
        var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
        builder.Services.AddNuxPrintAgent(options);
        builder.Services.AddSingleton<ICredentialStore>(store);
        builder.Services.AddSingleton<ICupsClient>(new NoHardwareCups());
        builder.Services.AddSingleton(sp =>
        {
            _ = sp.GetRequiredService<SingleInstanceLease>();
            return new CloudSession(new DeviceInfo { DeviceId = "test-device", DeviceName = "host", Platform = "linux" }, store, new LoginHttp());
        });
        builder.Services.AddSingleton<Func<string, SocketIOOptions, IAgentSocket>>((_, _) => { Interlocked.Increment(ref connections); return new NoCloudSocket(); });
        using var host = builder.Build();
        try
        {
            await host.StartAsync();
            var path = Path.Combine(options.RuntimeDirectory, "control.sock");
            var client = new LocalAgentClient(path);
            var output = new StringWriter();
            Assert.Equal(0, await CliApplication.RunAsync(["login", "user", "--password-stdin"], client, new StringReader("test-only\n"), output, TextWriter.Null));
            Assert.Equal("test-session", store.Auth!.SessionToken);
            var status = await client.SendAsync(new() { Method = "status" });
            Assert.True(status.State!.Running);
            Assert.True(status.State.IsAuthenticated);
            Assert.DoesNotContain("test-session", JsonSerializer.Serialize(status));
            await Task.Delay(1200); // Existing unauthenticated cloud loop polls at one second.
            Assert.Equal(1, connections);
            var originalAuth = store.Auth;
            var duplicateLogin = await client.SendAsync(new() { Method = "login", Email = "other-user", Password = "test-only" });
            Assert.False(duplicateLogin.Success);
            Assert.Contains("Sign out", duplicateLogin.Error);
            Assert.Same(originalAuth, store.Auth);
            Assert.Equal(1, connections);
            await new LocalAgentClient(path).SendAsync(new() { Method = "printers" });
            Assert.Equal(1, connections); // Closing local request streams never creates/stops a cloud agent.
            var invalid = await client.SendAsync(new() { Version = 999 });
            Assert.False(invalid.Success);
            Assert.False((await client.SendAsync(new() { Method = "unknown" })).Success);
            Assert.True((await client.SendAsync(new() { Method = "logout" })).Success);
            Assert.Null(store.Auth);
            Assert.True((await client.SendAsync(new() { Method = "status" })).State!.Running);
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { host.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

internal sealed class TestStore : ICredentialStore
{
    public AuthData? Auth { get; private set; }
    public AuthData? Load() => Auth;
    public void Save(AuthData auth) => Auth = auth;
    public void Clear() => Auth = null;
}
internal sealed class LoginHttp : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var jwt = "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":4102444800}")).TrimEnd('=') + ".signature";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { sessionToken = "test-session", accessToken = jwt }), Encoding.UTF8, "application/json") });
    }
}
internal sealed class NoHardwareCups : ICupsClient
{
    public Task<CupsSnapshot> DiscoverAsync(CancellationToken ct = default) => Task.FromResult(new CupsSnapshot([], null));
    public Task<IReadOnlyList<CupsQueueJob>> GetQueueAsync(string printer, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CupsQueueJob>>([]);
    public Task<CommandResult> SubmitAsync(string printer, string name, string file, IReadOnlyList<string> options, int copies, bool raw, CancellationToken ct = default) => throw new InvalidOperationException("No hardware printing in this test.");
}
internal sealed class NoCloudSocket : IAgentSocket
{
    public bool IsConnected { get; private set; }
    public event Func<Task>? Connected;
    public event Action? Disconnected;
    public event Action<string>? Error { add { } remove { } }
    public void On(string name, Func<IAgentSocketMessage, Task> handler) { }
    public async Task ConnectAsync() { IsConnected = true; if (Connected != null) await Connected(); }
    public Task DisconnectAsync() { IsConnected = false; Disconnected?.Invoke(); return Task.CompletedTask; }
    public Task EmitAsync(string name, object payload) => Task.CompletedTask;
    public void Dispose() => IsConnected = false;
}
