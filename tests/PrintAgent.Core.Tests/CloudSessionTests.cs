using System.Net;
using System.Text;
using System.Text.Json;
using PrintAgent.Common.Models;
using Xunit;

namespace PrintAgent.Core.Tests;

public class CloudSessionTests
{
    [Theory]
    [InlineData(240, true)]
    [InlineData(360, false)]
    [InlineData(-60, true)]
    public async Task Access_token_refresh_keeps_original_five_minute_window(int expiresInSeconds, bool shouldRefresh)
    {
        var refreshed = false;
        using var handler = new StubHttp((_, _) =>
        {
            refreshed = true;
            return Reply(JsonSerializer.Serialize(new { accessToken = FutureJwt() }));
        });
        using var session = Create(new TestCredentialStore(), handler);
        var payload = JsonSerializer.Serialize(new { exp = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds).ToUnixTimeSeconds() });
        session.SetAuth(new AuthData { SessionToken = "saved", Token = "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature" });
        Assert.True(await session.EnsureAccessTokenAsync(CancellationToken.None));
        Assert.Equal(shouldRefresh, refreshed);
    }

    [Fact]
    public async Task Tls_login_error_uses_HTTP_Date_only_for_clock_diagnosis()
    {
        using var handler = new StubHttp((_, _) => throw new HttpRequestException("TLS", new System.Security.Authentication.AuthenticationException("test")));
        using var clock = new StubHttp((request, body) =>
        {
            Assert.Equal("http://nuxprint.com/", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Null(body);
            var reply = Reply("");
            reply.Headers.Date = DateTimeOffset.UtcNow.AddHours(-2);
            return reply;
        });
        using var session = new CloudSession(new DeviceInfo(), new TestCredentialStore(), handler, clock);
        var error = await Assert.ThrowsAsync<ClockSkewException>(() => session.LoginAsync("test", "test-only"));
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Fact]
    public async Task Missing_clock_header_keeps_original_TLS_exception()
    {
        var original = new HttpRequestException("TLS", new System.Security.Authentication.AuthenticationException("test"));
        using var handler = new StubHttp((_, _) => throw original);
        using var clock = new StubHttp((_, _) => Reply(""));
        using var session = new CloudSession(new DeviceInfo(), new TestCredentialStore(), handler, clock);
        Assert.Same(original, await Assert.ThrowsAsync<HttpRequestException>(() => session.LoginAsync("test", "test-only")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("invalid-json")]
    public async Task Invalid_refresh_response_does_not_erase_session(string body)
    {
        using var handler = new StubHttp((_, _) => Reply(body));
        using var session = Create(new TestCredentialStore(), handler);
        session.SetAuth(new AuthData { SessionToken = "saved" });
        Assert.False(await session.EnsureAccessTokenAsync(CancellationToken.None));
        Assert.Equal("saved", session.Auth!.SessionToken);
    }

    [Fact]
    public async Task Login_sends_exact_reference_request_and_does_not_implicitly_store_auth()
    {
        using var handler = new StubHttp((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://nuxprint.com/api/agent/session", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("{\"email\":\"user\",\"password\":\"test-only\",\"deviceId\":\"device-1\",\"deviceName\":\"host-1\",\"platform\":\"windows\",\"agentVersion\":\"1.9.5\"}", body);
            return Reply("{\"sessionToken\":\"test-session\",\"accessToken\":\"test-access\",\"customer\":null}");
        });
        var store = new TestCredentialStore();
        using var session = Create(store, handler);
        var result = await session.LoginAsync("user", "test-only");
        Assert.Equal("test-session", result!.SessionToken);
        Assert.False(session.IsAuthenticated);
        session.SetAuth(new AuthData { SessionToken = result.SessionToken, Token = result.AccessToken });
        Assert.True(session.IsAuthenticated);
        Assert.Same(session.Auth, store.Value);
    }

    [Fact]
    public async Task Valid_access_token_reconnect_does_not_refresh()
    {
        using var handler = new StubHttp((_, _) => throw new Xunit.Sdk.XunitException("Unexpected request"));
        using var session = Create(new TestCredentialStore(), handler);
        session.SetAuth(new AuthData { SessionToken = "test-session", Token = FutureJwt() });
        Assert.True(await session.EnsureAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Refresh_uses_session_in_body_and_preserves_customer_when_response_omits_it()
    {
        using var handler = new StubHttp((request, body) =>
        {
            Assert.Equal("https://nuxprint.com/api/agent/session/refresh", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("{\"sessionToken\":\"test-session\",\"agentVersion\":\"1.9.5\"}", body);
            return Reply("{\"accessToken\":\"replacement\"}");
        });
        using var session = Create(new TestCredentialStore(), handler);
        var customer = new CustomerInfo { Id = "c-1" };
        session.SetAuth(new AuthData { SessionToken = "test-session", Token = "expired", Customer = customer });
        Assert.True(await session.EnsureAccessTokenAsync(CancellationToken.None));
        Assert.Equal("test-session", session.Auth!.SessionToken);
        Assert.Equal("replacement", session.Auth.Token);
        Assert.Same(customer, session.Auth.Customer);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "proxy denied", true)]
    [InlineData(HttpStatusCode.Unauthorized, "SESSION_REVOKED", false)]
    [InlineData(HttpStatusCode.InternalServerError, "failure", true)]
    public async Task Only_revoked_refresh_401_erases_saved_auth(HttpStatusCode code, string body, bool staysSignedIn)
    {
        using var handler = new StubHttp((_, _) => Reply(body, code));
        var store = new TestCredentialStore();
        using var session = Create(store, handler);
        session.SetAuth(new AuthData { SessionToken = "test-session" });
        Assert.False(await session.EnsureAccessTokenAsync(CancellationToken.None));
        Assert.Equal(staysSignedIn, session.IsAuthenticated);
        Assert.Equal(staysSignedIn, store.Value != null);
    }

    [Fact]
    public async Task Transport_failure_keeps_session_for_later_retry()
    {
        using var handler = new StubHttp((_, _) => throw new HttpRequestException("offline"));
        using var session = Create(new TestCredentialStore(), handler);
        session.SetAuth(new AuthData { SessionToken = "test-session" });
        Assert.False(await session.EnsureAccessTokenAsync(CancellationToken.None));
        Assert.Equal("test-session", session.Auth!.SessionToken);
    }

    [Fact]
    public async Task Legacy_adopt_uses_Bearer_and_same_device_fields()
    {
        using var handler = new StubHttp((request, body) =>
        {
            Assert.Equal("https://nuxprint.com/api/agent/session/adopt", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("old-jwt", request.Headers.Authorization.Parameter);
            Assert.Equal("{\"deviceId\":\"device-1\",\"deviceName\":\"host-1\",\"platform\":\"windows\",\"agentVersion\":\"1.9.5\"}", body);
            return Reply(JsonSerializer.Serialize(new { sessionToken = "upgraded", accessToken = FutureJwt() }));
        });
        using var session = Create(new TestCredentialStore(), handler);
        session.SetAuth(new AuthData { Token = "old-jwt" });
        Assert.True(await session.EnsureAccessTokenAsync(CancellationToken.None));
        Assert.Equal("upgraded", session.Auth!.SessionToken);
    }

    [Fact]
    public async Task Failed_adopt_falls_back_to_legacy_token_without_clearing_it()
    {
        using var handler = new StubHttp((_, _) => Reply("denied", HttpStatusCode.Unauthorized));
        using var session = Create(new TestCredentialStore(), handler);
        session.SetAuth(new AuthData { Token = "old-jwt" });
        Assert.True(await session.EnsureAccessTokenAsync(CancellationToken.None));
        Assert.True(session.Auth!.IsLegacy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_always_clears_local_auth_even_when_offline(bool offline)
    {
        using var handler = new StubHttp((request, body) =>
        {
            Assert.Equal("https://nuxprint.com/api/agent/session/logout", request.RequestUri!.AbsoluteUri);
            Assert.Equal("{\"sessionToken\":\"test-session\"}", body);
            if (offline) throw new HttpRequestException("offline");
            return Reply("{}");
        });
        var store = new TestCredentialStore();
        using var session = Create(store, handler);
        session.SetAuth(new AuthData { SessionToken = "test-session" });
        await session.LogoutAsync();
        Assert.False(session.IsAuthenticated);
        Assert.Null(store.Value);
    }

    [Fact]
    public void Saved_auth_can_be_loaded_without_network()
    {
        var store = new TestCredentialStore { Value = new AuthData { SessionToken = "saved" } };
        using var session = Create(store, new StubHttp((_, _) => throw new InvalidOperationException()));
        session.LoadSavedAuth();
        Assert.Equal("saved", session.Auth!.SessionToken);
    }

    [Fact]
    public async Task Version_endpoint_uses_original_model_and_download_field()
    {
        using var handler = new StubHttp((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://nuxprint.com/api/agent/version", request.RequestUri!.AbsoluteUri);
            return Reply("{\"latestVersion\":\"1.9.5\",\"downloadUrls\":{\"windows\":\"https://example.invalid/setup.exe\"},\"releaseNotes\":\"note\",\"mandatory\":true}");
        });
        using var session = Create(new TestCredentialStore(), handler);
        var version = await session.FetchVersionInfoAsync();
        Assert.Equal("1.9.5", version!.LatestVersion);
        Assert.True(version.Mandatory);
        Assert.Equal("https://example.invalid/setup.exe", version.DownloadUrls!.Value.GetProperty("windows").GetString());
    }

    internal static CloudSession Create(TestCredentialStore store, HttpMessageHandler handler) =>
        new(new DeviceInfo { DeviceId = "device-1", DeviceName = "host-1" }, store, handler);

    internal static string FutureJwt() => "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":4102444800}")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
    internal static HttpResponseMessage Reply(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

internal sealed class TestCredentialStore : ICredentialStore
{
    public AuthData? Value { get; set; }
    public AuthData? Load() => Value;
    public void Save(AuthData auth) => Value = auth;
    public void Clear() => Value = null;
}

internal sealed class StubHttp(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        respond(request, request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
}
