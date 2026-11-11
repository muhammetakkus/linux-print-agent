using System.Net.Http.Json;
using System.Text.Json;
using PrintAgent.Common;
using PrintAgent.Common.Models;

namespace PrintAgent.Core;

/// <summary>Source-compatible HTTP/session behavior; storage is provided by the Linux host.</summary>
public sealed class CloudSession : IDisposable
{
    private readonly ICredentialStore _store;
    private readonly HttpClient _http;
    private readonly HttpClient _clockHttp;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _stateDebounceLock = new();
    private Timer? _stateDebounceTimer;
    private volatile string? _rejectedAccessToken;

    public string DeviceId { get; }
    public string DeviceName { get; }
    public string Platform { get; }
    public AuthData? Auth { get; private set; }
    public bool IsAuthenticated =>
        !string.IsNullOrEmpty(Auth?.SessionToken) || !string.IsNullOrEmpty(Auth?.Token);
    public event Action? StateChanged;
    public event Action? AuthenticationCleared;
    public event Action<LogEntry>? LogAdded;

    public CloudSession(DeviceInfo device, ICredentialStore store,
        HttpMessageHandler? httpHandler = null, HttpMessageHandler? clockHandler = null)
    {
        DeviceId = device.DeviceId;
        DeviceName = device.DeviceName;
        Platform = device.Platform;
        _store = store;
        _http = httpHandler == null ? new HttpClient() : new HttpClient(httpHandler);
        _http.Timeout = TimeSpan.FromSeconds(Constants.ConnectionTimeoutSeconds + 5);
        _clockHttp = new HttpClient(clockHandler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(5) };
    }

    public void LoadSavedAuth()
    {
        Auth = _store.Load();
    }

    public async Task<AgentSessionResponse?> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.PostAsJsonAsync($"{Constants.DefaultApiUrl}/api/agent/session", new
            {
                email,
                password,
                deviceId = DeviceId,
                deviceName = DeviceName,
                platform = Platform,
                agentVersion = Constants.AppVersion,
            }, _json, ct);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadFromJsonAsync<AgentSessionResponse>(_json, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            // TLS el sikismasi dustu. Sahada en yaygin neden yanlis sistem saati:
            // sunucu saati TLS gerektirmeyen duz HTTP yanitinin Date basligindan
            // olculur; fark 5 dk'yi asiyorsa kullaniciyi yonlendiren mesaj gosterilir.
            // Diger tum durumlar (kok sertifika, AV SSL filtresi vb.) ham hata olarak
            // aynen yukari firlatilir — UI onlari degistirmeden basar.
            var skew = await TryGetClockSkewAsync();
            if (skew.HasValue && Math.Abs(skew.Value.TotalMinutes) > 5)
                throw new ClockSkewException(BuildClockSkewMessage(skew.Value), ex);
            throw;
        }
    }

    // Duz HTTP (TLS'siz) yanitin Date basligi ile yerel UTC arasindaki farki olcer.
    // Sonda basarisiz olursa null doner; cagiran orijinal hatayi oldugu gibi iletir.
    private async Task<TimeSpan?> TryGetClockSkewAsync()
    {
        try
        {
            var plainUrl = Constants.DefaultApiUrl.Replace("https://", "http://");
            using var res = await _clockHttp.GetAsync(plainUrl, HttpCompletionOption.ResponseHeadersRead);
            if (res.Headers.Date is not DateTimeOffset serverDate) return null;
            return DateTimeOffset.UtcNow - serverDate.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static string BuildClockSkewMessage(TimeSpan skew)
    {
        var abs = skew.Duration();
        var fark = abs.TotalDays >= 1 ? $"{(int)abs.TotalDays} gün"
            : abs.TotalHours >= 1 ? $"{(int)abs.TotalHours} saat"
            : $"{(int)abs.TotalMinutes} dakika";
        var yon = skew > TimeSpan.Zero ? "ileri" : "geri";
        return $"Bilgisayarınızın tarih/saat ayarı yanlış görünüyor (sunucudan yaklaşık {fark} {yon}). " +
               "Lütfen Windows tarih ve saat ayarını düzeltin (otomatik ayarlamayı açın) ve tekrar deneyin.";
    }

    /// <summary>
    /// Trades the JWT written by a pre-1.9.0 build for a session token, so an
    /// update never costs the user a login. Best effort: if the old token has
    /// already expired the user signs in once and gets a session that will not
    /// expire again.
    /// </summary>
    private async Task<bool> TryAdoptLegacyAuthAsync(CancellationToken ct)
    {
        var legacyToken = Auth?.Token;
        if (string.IsNullOrEmpty(legacyToken)) return false;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Constants.DefaultApiUrl}/api/agent/session/adopt")
        {
            Content = JsonContent.Create(new
            {
                deviceId = DeviceId,
                deviceName = DeviceName,
                platform = Platform,
                agentVersion = Constants.AppVersion,
            }, options: _json),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", legacyToken);

        try
        {
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return false;

            var data = await res.Content.ReadFromJsonAsync<AgentSessionResponse>(_json, ct);
            if (data == null || string.IsNullOrEmpty(data.SessionToken)) return false;

            SetAuth(new AuthData
            {
                SessionToken = data.SessionToken,
                Token = data.AccessToken,
                Customer = data.Customer ?? Auth?.Customer,
            });
            AddLog("Info", "Session upgraded — this machine will stay signed in");
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Mints a fresh access token from the stored session token.
    ///
    /// Only one outcome erases credentials: the server answering 401, which it
    /// reserves for a session that was actually revoked. Timeouts, DNS failures,
    /// 5xx and every other transport problem return false and leave the stored
    /// session untouched, so an offline machine simply retries — for as long as
    /// it takes.
    /// </summary>
    public async Task<bool> EnsureAccessTokenAsync(CancellationToken ct)
    {
        var auth = Auth;
        if (auth == null) return false;

        if (auth.IsLegacy)
        {
            // Credentials from a pre-1.9.0 build: swap them for a session token
            // while the old JWT still works. If that fails (offline, or it has
            // already expired) fall through and let the old token try.
            if (await TryAdoptLegacyAuthAsync(ct)) auth = Auth!;
            else return !string.IsNullOrEmpty(auth.Token);
        }

        if (string.IsNullOrEmpty(auth.SessionToken)) return !string.IsNullOrEmpty(auth.Token);

        // Still-valid access token: reconnect without touching the network. A
        // flapping link must not turn into one refresh call per retry.
        if (auth.Token != _rejectedAccessToken && !IsAccessTokenExpiring(auth.Token)) return true;

        try
        {
            using var res = await _http.PostAsJsonAsync(
                $"{Constants.DefaultApiUrl}/api/agent/session/refresh",
                new { sessionToken = auth.SessionToken, agentVersion = Constants.AppVersion },
                _json,
                ct);

            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Only the API's own "session_revoked" ends a session. A bare
                // 401 from something in between (proxy, captive portal, WAF) is
                // treated as a transport failure, not as a sign-out.
                var body = await res.Content.ReadAsStringAsync(ct);
                if (body.Contains("session_revoked", StringComparison.OrdinalIgnoreCase))
                {
                    AddLog("Warning", "Session ended from the panel - please sign in again");
                    ClearAuth();
                }
                else
                {
                    AddLog("Warning", "Unexpected 401 while refreshing session, will retry");
                }
                return false;
            }

            if (!res.IsSuccessStatusCode)
            {
                AddLog("Warning", $"Could not refresh session ({(int)res.StatusCode}), will retry");
                return false;
            }

            var data = await res.Content.ReadFromJsonAsync<AgentRefreshResponse>(_json, ct);
            if (data == null || string.IsNullOrEmpty(data.AccessToken)) return false;

            SetAuth(new AuthData
            {
                SessionToken = auth.SessionToken,
                Token = data.AccessToken,
                Customer = data.Customer ?? auth.Customer,
            });
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            // Network down, machine just woke from sleep, DNS not ready - keep
            // the session and try again on the next loop.
            AddLog("Warning", $"Session refresh failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// True when the access token is missing, unreadable, or within five
    /// minutes of expiry. Read locally from the JWT's `exp` claim - no
    /// signature check, because this only decides whether to ask for a new one.
    /// </summary>
    private static bool IsAccessTokenExpiring(string? token)
    {
        if (string.IsNullOrEmpty(token)) return true;

        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return true;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!doc.RootElement.TryGetProperty("exp", out var exp)) return true;

            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
            return expiresAt - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(5);
        }
        catch { return true; }
    }

    /// <summary>Revokes the session server-side; local credentials are cleared either way.</summary>
    public async Task LogoutAsync()
    {
        var sessionToken = Auth?.SessionToken;
        if (!string.IsNullOrEmpty(sessionToken))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _http.PostAsJsonAsync(
                    $"{Constants.DefaultApiUrl}/api/agent/session/logout",
                    new { sessionToken },
                    _json,
                    cts.Token);
            }
            catch { /* signing out locally must succeed even with no network */ }
        }
        ClearAuth();
    }

    public void SetAuth(AuthData auth)
    {
        Auth = auth;
        _store.Save(auth);
        OnStateChanged();
    }

    // Keep the persistent session; only the exact access token rejected by this socket must be refreshed.
    public void RejectAccessToken(string token) => _rejectedAccessToken = token;

    public void ClearAuth()
    {
        Auth = null;
        _rejectedAccessToken = null;
        _store.Clear();
        AuthenticationCleared?.Invoke();
        OnStateChanged();
    }

    public async Task<UpdateInfo?> FetchVersionInfoAsync()
    {
        try
        {
            var res = await _http.GetAsync($"{Constants.DefaultApiUrl}/api/agent/version");
            if (!res.IsSuccessStatusCode) return null;

            var info = await res.Content.ReadFromJsonAsync<UpdateInfo>(_json);
            if (info == null || string.IsNullOrEmpty(info.LatestVersion)) return null;
            return info;
        }
        catch (Exception ex)
        {
            AddLog("Warning", $"Version check failed: {ex.Message}");
            return null;
        }
    }

    private void AddLog(string level, string message) =>
        LogAdded?.Invoke(new LogEntry { Level = level, Message = message, Timestamp = DateTime.Now });

    private void OnStateChanged()
    {
        lock (_stateDebounceLock)
        {
            _stateDebounceTimer?.Dispose();
            _stateDebounceTimer = new Timer(_ => StateChanged?.Invoke(), null, 100, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (_stateDebounceLock) { _stateDebounceTimer?.Dispose(); }
        _http.Dispose();
        _clockHttp.Dispose();
    }
}

public sealed class ClockSkewException(string message, Exception inner) : Exception(message, inner);
