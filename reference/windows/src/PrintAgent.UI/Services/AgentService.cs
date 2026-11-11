using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using SocketIOClient;
using PrintAgent.Common;
using PrintAgent.Common.Helpers;
using PrintAgent.Common.Models;
using PrintAgent.Common.Security;

namespace PrintAgent.UI.Services;

internal class PrinterIdMapping
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("externalId")]
    public string ExternalId { get; set; } = string.Empty;
}

public class AgentService
{
    private static AgentService? _instance;
    public static AgentService Instance => _instance ??= new AgentService();

    public string DeviceId { get; } = DeviceIdHelper.GetOrCreate();
    public string DeviceName { get; } = Environment.MachineName;

    public AuthData? Auth { get; private set; }
    // Signed in means "we hold a session token" (or, for credentials written by
    // an older build, the legacy JWT). The access token is disposable and its
    // absence never counts as being signed out.
    public bool IsAuthenticated =>
        !string.IsNullOrEmpty(Auth?.SessionToken) || !string.IsNullOrEmpty(Auth?.Token);
    public bool IsConnected => Volatile.Read(ref _isConnected);
    public List<PrinterInfo> Printers { get; private set; } = new();
    public List<LogEntry> RecentLogs { get; } = new();
    public List<PrintJobRecord> Jobs { get; } = new();
    private readonly object _jobsLock = new();

    private bool _isConnected;
    private readonly object _logLock = new();
    private readonly object _socketLock = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(Constants.ConnectionTimeoutSeconds + 5) };
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private SocketIOClient.SocketIO? _socket;
    private CancellationTokenSource? _cts;
    private string? _sumatraPath;
    private readonly SemaphoreSlim _printLock = new(1, 1);
    private readonly PrintSafetyOptions _printSafety = new();
    private readonly PrintJobLedger _jobLedger = new(Path.Combine(UserDataDirectory.Ensure(), "job-receipts.json"));
    // Bir is basilirken WMI enumerasyonu yapilmaz: spooler o anda surucuyle
    // mesgul ve en kirilgan halinde. Yoklama is bitene kadar ertelenir.
    private volatile bool _printing;
    private System.Management.ManagementEventWatcher? _printerCreateWatcher;
    private System.Management.ManagementEventWatcher? _printerDeleteWatcher;
    private Timer? _debounceTimer;
    private readonly object _debounceLock = new();
    private Timer? _stateDebounceTimer;
    // Capabilities are static per (printer, driver); cache so the driver-loading
    // Win32 enumeration runs once, not on every 60s/status refresh.
    private readonly Dictionary<string, PrinterCapabilities?> _capsCache = new();
    private readonly object _capsLock = new();
    private readonly object _stateDebouncelock = new();

    public event Action? StateChanged;
    public event Action<LogEntry>? LogAdded;

    private AgentService() { }

    public void LoadSavedAuth()
    {
        Auth = CredentialStore.Load();
    }

    public async Task<AgentSessionResponse?> LoginAsync(string email, string password)
    {
        try
        {
            var res = await _http.PostAsJsonAsync($"{Constants.DefaultApiUrl}/api/agent/session", new
            {
                email,
                password,
                deviceId = DeviceId,
                deviceName = DeviceName,
                platform = "windows",
                agentVersion = Constants.AppVersion,
            }, _json);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadFromJsonAsync<AgentSessionResponse>(_json);
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
    private static async Task<TimeSpan?> TryGetClockSkewAsync()
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var plainUrl = Constants.DefaultApiUrl.Replace("https://", "http://");
            using var res = await http.GetAsync(plainUrl, HttpCompletionOption.ResponseHeadersRead);
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
                platform = "windows",
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
    private async Task<bool> EnsureAccessTokenAsync(CancellationToken ct)
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
        if (!IsAccessTokenExpiring(auth.Token)) return true;

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
        CredentialStore.Save(auth);
        OnStateChanged();
    }

    public void ClearAuth()
    {
        Auth = null;
        CredentialStore.Clear();
        SetConnected(false);
        OnStateChanged();
    }

    public void AddLog(string level, string message, string? jobId = null)
    {
        var entry = new LogEntry { Level = SafeText.Limit(level, 16), Message = SafeText.Limit(message), JobId = SafeText.Limit(jobId, 128), Timestamp = DateTime.Now };
        lock (_logLock)
        {
            RecentLogs.Insert(0, entry);
            if (RecentLogs.Count > 200) RecentLogs.RemoveAt(RecentLogs.Count - 1);
        }
        LogAdded?.Invoke(entry);
    }

    public void OnStateChanged()
    {
        lock (_stateDebouncelock)
        {
            _stateDebounceTimer?.Dispose();
            _stateDebounceTimer = new Timer(_ => StateChanged?.Invoke(), null, 100, Timeout.Infinite);
        }
    }

    private void SetConnected(bool value)
    {
        Volatile.Write(ref _isConnected, value);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        // The packaged helper is verified immediately before each PDF submission.
        _ = Task.Run(() => ConnectionLoopAsync(ct), ct);
        StartPrinterWatchers();
        _ = Task.Run(() => PrinterFallbackLoopAsync(ct), ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        StopPrinterWatchers();
        DisposeSocket();
    }

    private void DisposeSocket()
    {
        SocketIOClient.SocketIO? socketToDispose;
        lock (_socketLock)
        {
            socketToDispose = _socket;
            _socket = null;
        }
        if (socketToDispose != null)
        {
            _ = Task.Run(() =>
            {
                try { socketToDispose.Dispose(); } catch { }
            });
        }
    }

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        int retryCount = 0;

        while (!ct.IsCancellationRequested)
        {
            if (!IsAuthenticated)
            {
                retryCount = 0;
                try { await Task.Delay(1000, ct); } catch { return; }
                continue;
            }

            try
            {
                // A stale access token is not a logout: mint a new one from the
                // session token before every handshake. If that fails we back
                // off and try again — the stored session stays put.
                if (!await EnsureAccessTokenAsync(ct))
                {
                    retryCount++;
                    if (!IsAuthenticated) continue;
                    var wait = Math.Min(
                        Constants.ReconnectDelayMs * (1 << Math.Min(retryCount, 5)),
                        Constants.MaxReconnectDelayMs);
                    try { await Task.Delay(wait, ct); } catch { return; }
                    continue;
                }

                await ConnectSocketAsync(ct);
                retryCount = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                retryCount++;
                AddLog("Error", $"Connection failed: {ex.Message}");
            }

            SetConnected(false);
            OnStateChanged();

            if (ct.IsCancellationRequested || !IsAuthenticated) continue;

            var baseDelay = Constants.ReconnectDelayMs * (1 << Math.Min(retryCount, 5));
            var jitter = Random.Shared.Next(0, Math.Max(1, baseDelay / 4));
            var delay = Math.Min(baseDelay + jitter, Constants.MaxReconnectDelayMs);
            AddLog("Info", $"Reconnecting in {delay / 1000}s...");
            try { await Task.Delay(delay, ct); } catch { return; }
        }
    }

    private async Task ConnectSocketAsync(CancellationToken ct)
    {
        DisposeSocket();

        var socket = new SocketIOClient.SocketIO($"{Constants.DefaultApiUrl}/agent", new SocketIOOptions
        {
            Auth = new { token = Auth!.Token },
            Reconnection = false,
            ConnectionTimeout = TimeSpan.FromSeconds(Constants.ConnectionTimeoutSeconds),
            Path = "/api/socket.io",
        });

        lock (_socketLock) { _socket = socket; }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        socket.OnConnected += async (s, e) =>
        {
            SetConnected(true);
            AddLog("Info", "Connected to API");
            OnStateChanged();

            try
            {
                Printers = DiscoverPrinters();

                await socket.EmitAsync("agent:register", new
                {
                    deviceId = DeviceId,
                    deviceName = DeviceName,
                    platform = "windows",
                    printers = Printers.Select(p => new
                    {
                        name = p.Name,
                        status = p.Status,
                        connectionType = p.ConnectionType,
                        driverName = p.DriverName,
                        capabilities = p.Capabilities,
                    }).ToArray(),
                });

                // Iki asamali register: WMI listesi probe'suz aninda gitti (device
                // online, baski sistemi calisiyor). Ag yazicilarinin erisilebilirligi
                // arka planda PARALEL dogrulanir ve sonuc printer:status-update ile
                // duzeltilir. Tek bir olu ag yazicisi artik ne diger yazicilari ne de
                // cihazin online olmasini bekletebilir.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (await ResolveNetworkStatusesAsync(Printers))
                        {
                            OnStateChanged();
                            await EmitPrinterStatusAsync();
                        }
                    }
                    catch (Exception probeEx) { AddLog("Warning", $"Printer probe failed: {probeEx.Message}"); }
                });
            }
            catch (Exception ex)
            {
                AddLog("Error", $"Registration failed: {ex.Message}");
                tcs.TrySetResult(false);
            }
        };

        socket.OnDisconnected += (s, e) =>
        {
            SetConnected(false);
            AddLog("Warning", "Disconnected from API");
            OnStateChanged();
            tcs.TrySetResult(false);
        };

        socket.OnError += (s, e) =>
        {
            AddLog("Error", $"Socket error: {e}");

            // "Invalid token" only means the short-lived access token went
            // stale — the next loop mints a new one. Erasing credentials here
            // is what used to sign every machine out after seven days.
            if (e == "Session revoked")
            {
                ClearAuth();
                AddLog("Warning", "Session ended from the panel — please sign in again");
            }
            else if (e == "Invalid token")
            {
                AddLog("Info", "Access token expired, refreshing");
            }

            tcs.TrySetResult(false);
        };

        socket.On("print:job", async response =>
        {
            try
            {
                var data = response.GetValue<PrintJobData>();
                var result = await PrintAsync(data);
                await response.CallbackAsync(result);
            }
            catch (Exception ex)
            {
                await response.CallbackAsync(new PrintJobResult { Status = "failed", ErrorMessage = ex.Message });
            }
        });

        socket.On("printers:registered", response =>
        {
            try
            {
                var registered = response.GetValue<List<PrinterIdMapping>>();
                if (registered != null)
                {
                    foreach (var mapping in registered)
                    {
                        var printer = Printers.FirstOrDefault(p => p.Name == mapping.Name);
                        if (printer != null)
                            printer.ExternalId = mapping.ExternalId;
                    }
                    OnStateChanged();
                }
            }
            catch (Exception ex)
            {
                AddLog("Warning", $"Failed to map printer IDs: {ex.Message}");
            }
        });

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Constants.ConnectionTimeoutSeconds + 5));
        var connectTask = socket.ConnectAsync();
        var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);

        var completed = await Task.WhenAny(connectTask, timeoutTask);
        if (completed == timeoutTask || ct.IsCancellationRequested)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw new TimeoutException($"Connection timed out after {Constants.ConnectionTimeoutSeconds + 5}s");
        }

        await connectTask;

        using var reg = ct.Register(() => tcs.TrySetCanceled());

        try
        {
            await tcs.Task;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            DisposeSocket();
        }
    }

    public async Task DisconnectAsync()
    {
        SocketIOClient.SocketIO? socket;
        lock (_socketLock) { socket = _socket; }

        if (socket != null)
        {
            try { await socket.DisconnectAsync(); } catch (Exception ex) { AddLog("Warning", $"Socket disconnect error: {ex.Message}"); }
        }
        DisposeSocket();
        SetConnected(false);
        OnStateChanged();
    }

    private void StartPrinterWatchers()
    {
        try
        {
            // WMI'nin Win32_Printer icin olay saglayicisi yoktur: __Instance*Event
            // sorgulari WITHIN araliginda POLL edilir ve her yoklama printer
            // listesini spoolsv.exe uzerinden cikarir.
            //
            // Eskiden burada uc izleyici WITHIN 1 ile duruyordu -> saniyede uc kez
            // tam printer enumerasyonu, sonsuza kadar. Bir is basilirken bu,
            // spooler'in surucuyle en mesgul oldugu anda ustune biniyor ve
            // spoolsv cokup (SCM varsayilani: iki denemeden sonra pes eder)
            // yazdirma servisi tamamen durabiliyordu.
            //
            // __InstanceModificationEvent tamamen kaldirildi: Win32_Printer'in
            // PrinterStatus / JobCountSinceLastReset gibi alanlari yazdirma
            // sirasinda surekli degistigi icin tam da en kotu anda olay firtinasi
            // uretiyordu. Yazici durumu zaten 60 sn'lik yoklamayla guncelleniyor.
            var interval = TimeSpan.FromSeconds(30);

            _printerCreateWatcher = new System.Management.ManagementEventWatcher(
                new System.Management.WqlEventQuery("__InstanceCreationEvent", interval, "TargetInstance ISA 'Win32_Printer'"));
            _printerDeleteWatcher = new System.Management.ManagementEventWatcher(
                new System.Management.WqlEventQuery("__InstanceDeletionEvent", interval, "TargetInstance ISA 'Win32_Printer'"));

            _printerCreateWatcher.EventArrived += (s, e) => DebouncedRefresh();
            _printerDeleteWatcher.EventArrived += (s, e) => DebouncedRefresh();

            _printerCreateWatcher.Start();
            _printerDeleteWatcher.Start();

            AddLog("Info", "Printer monitoring started (event-based)");
        }
        catch (Exception ex)
        {
            AddLog("Warning", $"WMI watcher failed, using polling only: {ex.Message}");
        }
    }

    private void StopPrinterWatchers()
    {
        try { _printerCreateWatcher?.Stop(); _printerCreateWatcher?.Dispose(); } catch (Exception ex) { AddLog("Warning", $"Watcher cleanup error: {ex.Message}"); }
        try { _printerDeleteWatcher?.Stop(); _printerDeleteWatcher?.Dispose(); } catch (Exception ex) { AddLog("Warning", $"Watcher cleanup error: {ex.Message}"); }
        _printerCreateWatcher = null;
        _printerDeleteWatcher = null;

        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private void DebouncedRefresh()
    {
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => RefreshPrinters(), null, 1000, Timeout.Infinite);
        }
    }

    private async Task PrinterFallbackLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(3000, ct); } catch { return; }
        RefreshPrinters();

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(60_000, ct); } catch { return; }
            RefreshPrinters();
        }
    }

    private void RefreshPrinters()
    {
        if (_printing)
        {
            // Baski suruyor: enumerasyonu erteleyip birazdan tekrar dene.
            lock (_debounceLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(_ => RefreshPrinters(), null, 5000, Timeout.Infinite);
            }
            return;
        }

        try
        {
            var oldPrinters = Printers;
            var newPrinters = DiscoverPrinters();
            // Poll yolunda 'unknown' disari sizmaz: probe'lar paralel cozulur ve liste
            // ancak kesinlesmis durumlarla yayinlanir (60 sn'de bir UI'da "kontrol
            // ediliyor" titremesi olmamasi icin). Arka plan thread'i, sinir ~9.5 sn.
            ResolveNetworkStatusesAsync(newPrinters).GetAwaiter().GetResult();
            Printers = newPrinters;

            var changed = oldPrinters.Count != newPrinters.Count ||
                !oldPrinters.Select(p => $"{p.Name}:{p.Status}").SequenceEqual(newPrinters.Select(p => $"{p.Name}:{p.Status}"));

            if (changed)
            {
                OnStateChanged();
                _ = EmitPrinterStatusAsync();
            }
        }
        catch (Exception ex)
        {
            AddLog("Warning", $"Printer refresh failed: {ex.Message}");
        }
    }

    private async Task EmitPrinterStatusAsync()
    {
        SocketIOClient.SocketIO? socket;
        lock (_socketLock) { socket = _socket; }

        if (socket?.Connected != true) return;

        try
        {
            await socket.EmitAsync("printer:status-update", new
            {
                printers = Printers.Select(p => new
                {
                    name = p.Name,
                    status = p.Status,
                    connectionType = p.ConnectionType,
                    driverName = p.DriverName,
                    capabilities = p.Capabilities,
                }).ToArray(),
            });
        }
        catch (Exception ex)
        {
            AddLog("Warning", $"Printer status emit failed: {ex.Message}");
        }
    }

    public List<PrinterInfo> DiscoverPrinters()
    {
        var printers = new List<PrinterInfo>();
        try
        {
            // SELECT * spooler'i kullanmadigimiz alanlari da doldurmaya zorlar
            // (bazilari surucuye/porta gider). Yalnizca okudugumuz alanlari iste.
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, DriverName, PortName, PrinterStatus, WorkOffline FROM Win32_Printer");
            foreach (System.Management.ManagementObject printer in searcher.Get())
            {
                var name = printer["Name"]?.ToString() ?? "";

                if (Constants.ExcludedPrinters.Any(e => name.Equals(e, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var driver = printer["DriverName"]?.ToString() ?? "Unknown";
                var port = printer["PortName"]?.ToString() ?? "";
                var statusCode = Convert.ToUInt32(printer["PrinterStatus"] ?? 0);
                var workOffline = Convert.ToBoolean(printer["WorkOffline"] ?? false);

                var connType = port.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ||
                               port.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)
                    ? "USB" : "Network";

                // 0/3 = hazır/boşta, 4 = yazdırıyor, 5 = ısınıyor — hepsi online sayılır
                // (aksi halde baskı yapan yazıcı offline görünüyordu).
                var status = (statusCode is 0 or 3 or 4 or 5) && !workOffline ? "online" : "offline";

                // Iki asamali tespit: WMI 'online' diyen ag yazicisina burada probe
                // YAPILMAZ — probe register'i ve dolayisiyla TUM cihazin baskiya hazir
                // olmasini bekletiyordu (olu yazici basina ~9.5 sn, seri ve toplanarak).
                // Erisilebilirlik ResolveNetworkStatusesAsync ile arka planda PARALEL
                // dogrulanir; o zamana kadar durum "unknown" (UI: kontrol ediliyor).
                if (connType == "Network" && status == "online")
                    status = "unknown";

                var info = new PrinterInfo
                {
                    Name = name,
                    DriverName = driver,
                    PortName = port,
                    Status = status,
                    ConnectionType = connType,
                    IsActive = true,
                };
                info.Capabilities = GetCachedCapabilities(name, driver, port);
                printers.Add(info);
            }
        }
        catch (Exception ex) { AddLog("Error", $"Printer discovery failed: {ex.Message}"); }
        return printers;
    }

    // "unknown" ag yazicilarini PARALEL problar; her yazici kendi basina
    // online/offline'a cozulur — toplam sure "en yavas tek yazici" kadar.
    // Tek kacan ping yanlis offline uretmesin diye basarisiz kontrol ~2 sn sonra
    // bir kez daha denenir (eski seri davranisla ayni dogruluk).
    // Donus: cozulen yazici oldu mu (status-update emit karari icin).
    private async Task<bool> ResolveNetworkStatusesAsync(List<PrinterInfo> printers)
    {
        var pending = printers.Where(p => p.Status == "unknown").ToList();
        if (pending.Count == 0) return false;

        await Task.WhenAll(pending.Select(p => Task.Run(async () =>
        {
            var ip = GetPrinterIp(p.PortName) ?? GetPrinterIpFromWmi(p.Name);
            if (string.IsNullOrEmpty(ip)) { p.Status = "online"; return; }

            if (IsReachable(ip!)) { p.Status = "online"; return; }
            await Task.Delay(2000);
            p.Status = IsReachable(ip!) ? "online" : "offline";
        })));

        return true;
    }

    // Returns cached capabilities for (printer, driver), enumerating via Win32 only
    // on a cache miss. Capabilities are static, so 60s/status refreshes reuse this.
    private PrinterCapabilities? GetCachedCapabilities(string name, string driver, string port)
    {
        var key = $"{name}|{driver}";
        lock (_capsLock)
        {
            if (_capsCache.TryGetValue(key, out var cached)) return cached;
        }

        PrinterCapabilities? caps = null;
        try { caps = PrinterCapabilitiesHelper.Get(name, port, m => AddLog("Warning", m)); }
        catch (Exception ex) { AddLog("Warning", $"Capability read failed for {name}: {ex.Message}"); }

        lock (_capsLock) { _capsCache[key] = caps; }
        return caps;
    }

    private string? GetPrinterIp(string portName)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT HostAddress FROM Win32_TCPIPPrinterPort WHERE Name='{portName.Replace("'", "\\'")}'");
            foreach (System.Management.ManagementObject port in searcher.Get())
                return port["HostAddress"]?.ToString();
        }
        catch (Exception ex) { AddLog("Warning", $"GetPrinterIp WMI error for {portName}: {ex.Message}"); }

        if (System.Net.IPAddress.TryParse(portName, out _))
            return portName;

        var ipMatch = System.Text.RegularExpressions.Regex.Match(portName, @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");
        if (ipMatch.Success)
            return ipMatch.Groups[1].Value;

        return null;
    }

    private string? GetPrinterIpFromWmi(string printerName)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT PortName, Location FROM Win32_Printer WHERE Name='" + printerName.Replace("'", "\\'") + "'");
            foreach (System.Management.ManagementObject printer in searcher.Get())
            {
                var portName = printer["PortName"]?.ToString() ?? "";

                var tcpIp = GetPrinterIp(portName);
                if (tcpIp != null) return tcpIp;

                if (portName.StartsWith("WSD-", StringComparison.OrdinalIgnoreCase))
                {
                    var location = printer["Location"]?.ToString() ?? "";
                    var ipMatch = System.Text.RegularExpressions.Regex.Match(location, @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");
                    if (ipMatch.Success) return ipMatch.Groups[1].Value;
                }
            }
        }
        catch (Exception ex) { AddLog("Warning", $"GetPrinterIpFromWmi error for {printerName}: {ex.Message}"); }
        return null;
    }

    private bool IsReachable(string host)
    {
        // Yavaş Wi-Fi/uyanmakta olan yazıcılar 500ms'e sığmıyordu; 1500ms daha toleranslı.
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = ping.Send(host, 1500);
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                return true;
        }
        catch { }

        // ICMP engelli yazıcılar için port denemesi: RAW (9100) → IPP (631) → LPR (515).
        foreach (var portNum in new[] { 9100, 631, 515 })
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var result = tcp.BeginConnect(host, portNum, null, null);
                var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(750));
                if (success)
                {
                    tcp.EndConnect(result);
                    return true;
                }
            }
            catch { }
        }

        return false;
    }

    public Task EnsureSumatraAsync()
    {
        _sumatraPath = Path.Combine(AppContext.BaseDirectory, "tools", "SumatraPDF.exe");
        ExecutableTrust.VerifyHash(_sumatraPath, Path.Combine(AppContext.BaseDirectory, "tools", "SumatraPDF.sha256"));
        return Task.CompletedTask;
    }
    private PrintJobRecord TrackJob(PrintJobData job)
    {
        var record = new PrintJobRecord
        {
            JobId = job.JobId,
            DocumentName = job.DocumentName,
            PrinterName = job.PrinterName,
            Status = "printing",
            ReceivedAt = DateTime.Now,
        };
        lock (_jobsLock)
        {
            Jobs.Insert(0, record);
            if (Jobs.Count > 100) Jobs.RemoveAt(Jobs.Count - 1);
        }
        OnStateChanged();
        return record;
    }

    private void CompleteJob(PrintJobRecord record, string status, string? error = null)
    {
        record.Status = status;
        record.CompletedAt = DateTime.Now;
        record.ErrorMessage = error;
        OnStateChanged();
    }

    public async Task<PrintJobResult> PrintAsync(PrintJobData job)
    {
        var error = _printSafety.ValidateJob(job);
        if (error != null) return new() { Status = "failed", ErrorMessage = error };
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
        budget.CancelAfter(TimeSpan.FromSeconds(_printSafety.JobTimeoutSeconds));
        var held = false; PrintJobRecord? record = null;
        try
        {
            held = await _printLock.WaitAsync(TimeSpan.FromSeconds(30), budget.Token);
            if (!held) return new() { Status = "failed", ErrorMessage = "Print queue busy" };
            var printer = Printers.FirstOrDefault(p => string.Equals(p.Name.Trim(), job.PrinterName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (printer == null) return new() { Status = "failed", ErrorMessage = "Printer not found." };
            if ((error = _printSafety.ValidateJob(job, printer.Capabilities?.MaxCopies)) != null) return new() { Status = "failed", ErrorMessage = error };
            job = JsonSerializer.Deserialize<PrintJobData>(JsonSerializer.Serialize(job))!;
            job.PrinterName = printer.Name;
            var decoded = Convert.FromBase64String(job.DocumentData);
            if (decoded.Length == 0 || decoded.Length > _printSafety.MaxDocumentBytes) return new() { Status = "failed", ErrorMessage = "Invalid document size." };
            var previous = _jobLedger.Reserve(job, _printSafety);
            if (previous != null) return previous;
            record = TrackJob(job); _printing = true;
            var result = job.DocumentType.Equals("pdf", StringComparison.OrdinalIgnoreCase)
                ? await PrintPdfAsync(job, record, budget.Token)
                : await RawPrintWorker.SubmitAsync(job, budget.Token);
            CompleteJob(record, result.Status, result.ErrorMessage);
            _jobLedger.Complete(job.JobId, result);
            return result;
        }
        catch (Exception ex)
        {
            var result = new PrintJobResult { Status = "failed", ErrorMessage = ex is OperationCanceledException
                ? "Print cancelled or timed out; check the printer queue before resubmitting." : SafeText.Limit(ex.Message) };
            if (record != null) CompleteJob(record, result.Status, result.ErrorMessage);
            return result;
        }
        finally { if (held) { _printing = false; _printLock.Release(); } }
    }
    /// <summary>
    /// Print Spooler servisinin durumunu insan okunur bicimde dondurur. Yalnizca
    /// teshis icin; servise mudahale edilmez.
    /// </summary>
    private static string DescribeSpoolerState()
    {
        // ServiceController ayri bir NuGet paketi ister; zaten referansli olan
        // WMI yeterli. Win32_Service spooler'a bagli degildir, yani spooler olmus
        // olsa bile bu sorgu cevap verir.
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT State FROM Win32_Service WHERE Name='Spooler'");
            foreach (System.Management.ManagementObject svc in searcher.Get())
                return $"Print Spooler service is {svc["State"]}";
            return "Print Spooler service not found";
        }
        catch (Exception ex)
        {
            return $"Print Spooler state unknown: {ex.Message}";
        }
    }

    /// <summary>Metnin son <paramref name="max"/> karakteri (bastan kirpar).</summary>
    private static string Tail(string text, int max) =>
        text.Length <= max ? text : "..." + text[^max..];

    /// <summary>
    /// Istenen yazici adini yerel listedeki gercek adla eslestirir (buyuk-kucuk
    /// harf duyarsiz). Liste henuz doldurulmadiysa (acilisin ilk saniyeleri)
    /// engel olmamak icin istenen ad oldugu gibi kabul edilir.
    /// </summary>
    private string? ResolvePrinterName(string requested)
    {
        var printers = Printers;
        if (printers.Count == 0) return requested;

        var exact = printers.FirstOrDefault(p => p.Name == requested);
        if (exact != null) return exact.Name;

        var loose = printers.FirstOrDefault(
            p => string.Equals(p.Name.Trim(), requested.Trim(), StringComparison.OrdinalIgnoreCase));
        return loose?.Name;
    }

    private async Task<PrintJobResult> PrintPdfAsync(PrintJobData job, PrintJobRecord record, CancellationToken token)
    {
        var file = Path.Combine(UserDataDirectory.Ensure(), "print-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var bytes = Convert.FromBase64String(job.DocumentData);
            await using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.WriteAsync(bytes, token);
            await EnsureSumatraAsync();
            using var executableLock = new FileStream(_sumatraPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            ExecutableTrust.VerifyHash(_sumatraPath!, Path.Combine(AppContext.BaseDirectory, "tools", "SumatraPDF.sha256"));
            var args = new[] { "-print-to", job.PrinterName, "-print-settings", BuildPdfPrintSettings(job, bytes), "-silent", file };
            var result = await new PrintAgent.Linux.ProcessRunner().RunAsync(_sumatraPath!, args,
                new Dictionary<string, string>(), TimeSpan.FromSeconds(60), token);
            return new() { Status = result.ExitCode == 0 && !result.TimedOut ? "done" : "failed",
                ErrorMessage = result.TimedOut ? "Print timed out; check the printer queue." : result.ExitCode == 0 ? null : SafeText.Limit(result.Error) };
        }
        finally { if (File.Exists(file)) try { File.Delete(file); } catch (IOException) { AddLog("Warning", "Temporary document cleanup failed."); } }
    }
    // Builds the SumatraPDF -print-settings string from the optional job options.
    // When options are absent the result is identical to the previous behavior
    // ("fit"[, "landscape" if auto-detected][, "{n}x" copies]). Free-string
    // values are stripped of quotes/commas as a defensive second layer (the
    // server already validates them).
    private string BuildPdfPrintSettings(PrintJobData job, byte[] pdfBytes)
    {
        static string Safe(string s) => s.Replace("\"", "").Replace(",", "").Trim();

        var opts = job.Options;
        var parts = new List<string>();

        // fit-to-page (default true)
        parts.Add(opts?.FitToPage == false ? "noscale" : "fit");

        // orientation: explicit override wins; otherwise keep auto-detect
        // (auto-detect only ever forces landscape, unchanged from before).
        if (!string.IsNullOrEmpty(opts?.Orientation))
        {
            if (opts.Orientation == "landscape") parts.Add("landscape");
            else if (opts.Orientation == "portrait") parts.Add("portrait");
        }
        else if (DetectPdfOrientation(pdfBytes) == "landscape")
        {
            parts.Add("landscape");
        }

        // duplex
        switch (opts?.Duplex)
        {
            case "long-edge": parts.Add("duplexlong"); break;
            case "short-edge": parts.Add("duplexshort"); break;
            case "one-sided": parts.Add("simplex"); break;
        }

        // color
        if (opts?.Color == false) parts.Add("monochrome");
        else if (opts?.Color == true) parts.Add("color");

        // paper size
        if (!string.IsNullOrEmpty(opts?.PaperSize)) parts.Add($"paper={Safe(opts.PaperSize)}");

        // input tray / bin
        if (!string.IsNullOrEmpty(opts?.Tray)) parts.Add($"bin={Safe(opts.Tray)}");

        // page range — commas are the -print-settings list separator, so they
        // must be KEPT: "1,3-5" has to join into two tokens (1 and 3-5), not the
        // single broken token "13-5". Strip only unsafe chars, preserve digits,
        // commas and hyphens (the server already restricts to ^[0-9,-]).
        if (!string.IsNullOrEmpty(opts?.Pages))
        {
            var pages = System.Text.RegularExpressions.Regex.Replace(opts.Pages, "[^0-9,-]", "");
            if (pages.Length > 0) parts.Add(pages);
        }

        // copies
        if (job.Copies > 1) parts.Add($"{job.Copies}x");

        return string.Join(",", parts);
    }

    private string DetectPdfOrientation(byte[] pdfBytes)
    {
        try
        {
            var text = System.Text.Encoding.ASCII.GetString(pdfBytes, 0, Math.Min(pdfBytes.Length, 8192));
            var match = System.Text.RegularExpressions.Regex.Match(text, @"/MediaBox\s*\[\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*\]");
            if (match.Success)
            {
                var width = double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)
                          - double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var height = double.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture)
                           - double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                return width > height ? "landscape" : "portrait";
            }
        }
        catch (Exception ex) { AddLog("Warning", $"PDF orientation detection failed: {ex.Message}"); }
        return "portrait";
    }

    private static async Task<bool> WaitForExitAsync(Process proc, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async Task TestPrintAsync(string printerName)
    {
        var testPdf = GenerateTestPdf();
        var result = await PrintAsync(new PrintJobData
        {
            JobId = Guid.NewGuid().ToString(),
            PrinterName = printerName,
            DocumentName = "test-print.pdf",
            DocumentData = testPdf,
            Copies = 1,
        });
        AddLog(result.Status == "done" ? "Info" : "Error",
            $"Test print to {printerName}: {result.Status} {result.ErrorMessage ?? ""}");
    }

    private static string GenerateTestPdf()
    {
        var pdf = "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 164 113]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n4 0 obj<</Length 40>>stream\nBT /F1 14 Tf 50 50 Td (TEST) Tj ET\nendstream\nendobj\n5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\nxref\n0 6\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \n0000000266 00000 n \n0000000360 00000 n \ntrailer<</Size 6/Root 1 0 R>>\nstartxref\n441\n%%EOF";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pdf));
    }

    /// <summary>
    /// Yayindaki surum bilgisini oldugu gibi dondurur (guncelleme olmasa da).
    /// Arayuz, kurulu surumun yaninda yayindakini de gosterebilsin diye
    /// "yalnizca yenisi varsa dondur" davranisi <see cref="CheckForUpdateAsync"/>
    /// icinde tutuluyor.
    /// </summary>
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

    public static bool IsNewerVersion(string latest, string current)
    {
        if (Version.TryParse(latest, out var l) && Version.TryParse(current, out var c))
            return l > c;
        return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
    }

    public Task DownloadAndInstallUpdateAsync(string downloadUrl, string newVersion) => SecureUpdater.InstallAsync(downloadUrl, newVersion);

}

public class UpdateInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("downloadUrls")]
    public System.Text.Json.JsonElement? DownloadUrls { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("releaseNotes")]
    public string ReleaseNotes { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; }

    public string GetWindowsUrl()
    {
        if (DownloadUrls?.ValueKind == System.Text.Json.JsonValueKind.Object &&
            DownloadUrls.Value.TryGetProperty("windows", out var win))
            return win.GetString() ?? string.Empty;
        return string.Empty;
    }
}

// Yalnizca saat-farki tespitinde firlatilir; UI bu tipi yakalayip ham stack yerine
// yonlendirici mesaji gosterir. Diger tum hatalar ham gosterilmeye devam eder.
public class ClockSkewException : Exception
{
    public ClockSkewException(string message, Exception inner) : base(message, inner) { }
}
