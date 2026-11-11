using System.Text.Json;
using PrintAgent.Common.Models;
using Xunit;

namespace PrintAgent.Core.Tests;

public class RejectedTokenTests
{
    [Fact]
    public async Task Closed_socket_cannot_restore_a_logged_out_accounts_printer_identity()
    {
        using var session = CloudSessionTests.Create(new TestCredentialStore(), new StubHttp((_, _) => throw new InvalidOperationException()));
        session.SetAuth(new AuthData { SessionToken = "old-session", Token = CloudSessionTests.FutureJwt() });
        var socket = new TestSocket();
        using var connection = new CloudConnection(session, () => [new() { Name = "queue" }], _ => throw new InvalidOperationException(), (_, _) => socket);
        using var stop = new CancellationTokenSource();
        var running = connection.RunAsync(stop.Token);
        try
        {
            await socket.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            session.ClearAuth();
            session.SetAuth(new AuthData { SessionToken = "new-session", Token = CloudSessionTests.FutureJwt() });
            await socket.ReceiveAsync("printers:registered", "[{\"name\":\"queue\",\"externalId\":\"old-account-id\"}]");
            connection.ReplacePrinterSnapshot([new() { Name = "queue" }]);
            Assert.Empty(connection.Printers.Single().ExternalId);
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Rejected_unexpired_token_is_refreshed_before_reconnecting_without_losing_session()
    {
        var refreshed = 0;
        const string replacement = "replacement-access-token";
        using var session = CloudSessionTests.Create(new TestCredentialStore(), new StubHttp((request, body) =>
        {
            Assert.EndsWith("/session/refresh", request.RequestUri!.AbsolutePath);
            Assert.Equal("saved-session", JsonDocument.Parse(body!).RootElement.GetProperty("sessionToken").GetString());
            refreshed++;
            return CloudSessionTests.Reply(JsonSerializer.Serialize(new { accessToken = replacement }));
        }));
        session.SetAuth(new AuthData { SessionToken = "saved-session", Token = CloudSessionTests.FutureJwt() });
        var first = new TestSocket(); var second = new TestSocket();
        var sockets = new Queue<TestSocket>([first, second]);
        var delay = new TestDelay();
        var tokens = new List<string>();
        using var connection = new CloudConnection(session, () => [], _ => throw new InvalidOperationException(),
            (_, options) => { tokens.Add(JsonSerializer.SerializeToElement(options.Auth).GetProperty("token").GetString()!); return sockets.Dequeue(); },
            delay.WaitAsync);
        using var stop = new CancellationTokenSource();
        var running = connection.RunAsync(stop.Token);
        try
        {
            await first.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first.RaiseError("Invalid token");
            (await delay.NextAsync()).Release();
            await second.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, refreshed);
            Assert.Equal(replacement, tokens[1]);
            Assert.Equal("saved-session", session.Auth!.SessionToken);
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
