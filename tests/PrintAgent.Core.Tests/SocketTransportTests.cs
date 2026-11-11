using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintAgent.Common.Models;
using Xunit;

namespace PrintAgent.Core.Tests;

public class SocketTransportTests
{
    [Fact]
    public async Task Real_SocketIOClient_serializes_register_receives_job_and_ACKs_over_loopback()
    {
        await using var server = await LocalSocketServer.StartAsync();
        using var session = CloudSessionTests.Create(new TestCredentialStore(), new StubHttp((_, _) => throw new InvalidOperationException("No cloud access")));
        session.SetAuth(new AuthData { SessionToken = "test-session", Token = CloudSessionTests.FutureJwt() });
        var received = new TaskCompletionSource<PrintJobData>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new CloudConnection(session, () => [new PrinterInfo { Name = "queue" }], job =>
        {
            received.TrySetResult(job);
            return Task.FromResult(new PrintJobResult { Status = "failed", ErrorMessage = "test handler" });
        }, (_, options) => new SocketIoAgentSocket(server.Url + "/agent", options));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = connection.RunAsync(cts.Token);
        try
        {
            var auth = await server.Auth.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal("{\"token\":\"" + session.Auth!.Token + "\"}", auth);
            var registration = await server.Registration.Task.WaitAsync(TimeSpan.FromSeconds(8));
            // Uses the actual 3.1.1 default serializer, including null capability fields.
            Assert.Equal("42/agent,[\"agent:register\",{\"deviceId\":\"device-1\",\"deviceName\":\"host-1\",\"platform\":\"linux\",\"printers\":[{\"name\":\"queue\",\"status\":\"offline\",\"connectionType\":\"Network\",\"driverName\":\"\",\"capabilities\":null}]}]", registration);
            await server.SendAsync("42/agent,7[\"print:job\",{\"jobId\":\"loopback-job\",\"printerName\":\"queue\",\"documentData\":\"AQI=\"}]");
            var job = await received.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal("loopback-job", job.JobId);
            Assert.Equal("pdf", job.DocumentType);
            Assert.Equal("43/agent,7[{\"status\":\"failed\",\"errorMessage\":\"test handler\"}]", await server.Ack.Task.WaitAsync(TimeSpan.FromSeconds(8)));
            await connection.EmitPrinterStatusAsync();
            Assert.Equal("42/agent,[\"printer:status-update\",{\"printers\":[{\"name\":\"queue\",\"status\":\"offline\",\"connectionType\":\"Network\",\"driverName\":\"\",\"capabilities\":null}]}]", await server.Status.Task.WaitAsync(TimeSpan.FromSeconds(8)));
        }
        finally
        {
            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}

// Minimal Engine.IO v4 polling peer used only by the compatibility test.
// It is not a replacement implementation of the NuxPrint server.
internal sealed class LocalSocketServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>();
    public string Url { get; private set; } = "";
    public TaskCompletionSource<string> Auth { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string> Registration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string> Ack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string> Status { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LocalSocketServer(WebApplication app) => _app = app;

    public static async Task<LocalSocketServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new LocalSocketServer(app);
        app.Run(server.HandleAsync);
        await app.StartAsync();
        server.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return server;
    }

    public ValueTask SendAsync(string packet) => _outgoing.Writer.WriteAsync(packet);

    private async Task HandleAsync(HttpContext context)
    {
        Assert.Equal("/api/socket.io/", context.Request.Path.Value);
        Assert.Equal("polling", context.Request.Query["transport"].ToString());
        Assert.Equal("4", context.Request.Query["EIO"].ToString());
        context.Response.ContentType = "text/plain; charset=UTF-8";
        if (context.Request.Method == "GET")
        {
            if (!context.Request.Query.ContainsKey("sid"))
                await context.Response.WriteAsync("0{\"sid\":\"local-session\",\"upgrades\":[],\"pingInterval\":25000,\"pingTimeout\":20000,\"maxPayload\":1000000}");
            else
                await context.Response.WriteAsync(await _outgoing.Reader.ReadAsync(context.RequestAborted));
            return;
        }

        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        foreach (var packet in body.Split('\u001e'))
        {
            if (packet.StartsWith("40/agent,"))
            {
                Auth.TrySetResult(packet[9..]);
                await SendAsync("40/agent,{\"sid\":\"local-agent\"}");
            }
            else if (packet.StartsWith("42/agent,[\"agent:register\"")) Registration.TrySetResult(packet);
            else if (packet.StartsWith("42/agent,[\"printer:status-update\"")) Status.TrySetResult(packet);
            else if (packet.StartsWith("43/agent,7")) Ack.TrySetResult(packet);
        }
        await context.Response.WriteAsync("ok");
    }

    public async ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _app.StopAsync(timeout.Token);
        await _app.DisposeAsync();
    }
}
