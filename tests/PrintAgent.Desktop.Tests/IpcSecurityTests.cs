using System.Buffers.Binary;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintAgent.Common.Local;
using PrintAgent.Common.Models;
using PrintAgent.Core;
using PrintAgent.Linux;
using PrintAgent.Service;
using Xunit;

namespace PrintAgent.Desktop.Tests;

public class IpcSecurityTests
{
    [Fact]
    public async Task Slow_client_times_out_and_frees_the_only_client_slot()
    {
        using var fixture = new IpcFixture(); await fixture.Host.StartAsync();
        using var slow = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await slow.ConnectAsync(new UnixDomainSocketEndPoint(fixture.SocketPath));
        var request = new LocalAgentClient(fixture.SocketPath).SendAsync(new());
        Assert.True((await request.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        await fixture.Host.StopAsync();
    }

    [Fact]
    public async Task Oversized_request_is_rejected_from_its_header_before_payload_allocation()
    {
        using var fixture = new IpcFixture(); await fixture.Host.StartAsync();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(fixture.SocketPath));
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, 1024 * 1024);
        await socket.SendAsync(header, SocketFlags.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await socket.ReceiveAsync(new byte[1], SocketFlags.None, stop.Token));
        Assert.True((await new LocalAgentClient(fixture.SocketPath).SendAsync(new())).Success);
        await fixture.Host.StopAsync();
    }

    [Fact]
    public async Task Oversized_response_returns_small_error_instead_of_breaking_protocol()
    {
        using var fixture = new IpcFixture(hugePrinter: true); await fixture.Host.StartAsync();
        var response = await new LocalAgentClient(fixture.SocketPath).SendAsync(new());
        Assert.False(response.Success); Assert.Contains("size limit", response.Error);
        await fixture.Host.StopAsync();
    }

    [Fact]
    public async Task Password_is_cleared_after_failed_login()
    {
        var model = new PrintAgent.UI.MainViewModel(new FailedLoginClient()) { Email = "user", Password = "test-only", ShowPassword = true };
        await model.LoginCommand.ExecuteAsync(null);
        Assert.Empty(model.Password); Assert.False(model.ShowPassword);
    }

    private sealed class FailedLoginClient : ILocalAgentClient
    {
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default) => throw new IOException("unavailable");
    }

    private sealed class IpcFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "np-" + Guid.NewGuid().ToString("N")[..8]);
        public IHost Host { get; }
        public string SocketPath => Path.Combine(_directory, "r", "control.sock");
        public IpcFixture(bool hugePrinter = false)
        {
            var options = new AgentOptions { StateDirectory = Path.Combine(_directory, "s"), CacheDirectory = Path.Combine(_directory, "c"),
                RuntimeDirectory = Path.Combine(_directory, "r"), MaxLocalClients = 1, LocalRequestTimeoutSeconds = 2 };
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
            builder.Services.AddNuxPrintAgent(options);
            var store = new TestStore(); builder.Services.AddSingleton<ICredentialStore>(store);
            builder.Services.AddSingleton<ICupsClient>(new FixtureCups(hugePrinter));
            builder.Services.AddSingleton(sp => { _ = sp.GetRequiredService<SingleInstanceLease>(); return new CloudSession(new DeviceInfo { DeviceId = "test" }, store, new LoginHttp()); });
            Host = builder.Build();
        }
        public void Dispose() { Host.Dispose(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
    private sealed class FixtureCups(bool huge) : ICupsClient
    {
        public Task<CupsSnapshot> DiscoverAsync(CancellationToken ct = default) => Task.FromResult(new CupsSnapshot(huge ? [new() { Name = new string('x', 5 * 1024 * 1024) }] : [], null));
        public Task<IReadOnlyList<CupsQueueJob>> GetQueueAsync(string printer, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CommandResult> SubmitAsync(string p, string n, string f, IReadOnlyList<string> o, int c, bool r, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
