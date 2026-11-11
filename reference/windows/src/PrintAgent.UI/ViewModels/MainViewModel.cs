using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintAgent.Common;
using PrintAgent.Common.Models;
using PrintAgent.UI.Services;

namespace PrintAgent.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AgentService _agent = AgentService.Instance;

    [ObservableProperty] private bool _isConnectedToApi;
    [ObservableProperty] private bool _isAuthenticated;
    [ObservableProperty] private string _connectionState = "Starting...";
    [ObservableProperty] private CustomerInfo? _customer;
    [ObservableProperty] private string _deviceId = "";

    /// Kurulu surum (derlemeden gelir) ve API'nin bildirdigi yayindaki surum.
    /// Ikisi de arayuzde gorunur: alt bar kisa etiketi, Account sekmesi ayrintiyi.
    [ObservableProperty] private string _appVersion = Constants.AppVersion;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateAvailable))]
    [NotifyPropertyChangedFor(nameof(VersionLabel))]
    [NotifyPropertyChangedFor(nameof(LatestVersionLabel))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private string _latestVersion = "";

    /// Yayindaki surumun Windows indirme adresi; acilis kutusu gecildikten sonra
    /// alt bardaki Guncelle dugmesi de ayni adresi kullanir.
    private string _latestDownloadUrl = "";

    /// Indirme + kurulum surerken dugme tekrar tiklanamaz, etiketi degisir.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateButtonLabel))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private bool _isUpdating;

    public bool UpdateAvailable =>
        !string.IsNullOrEmpty(LatestVersion) && AgentService.IsNewerVersion(LatestVersion, Constants.AppVersion);

    /// Alt bar: normalde sadece surum, yeni surum varsa parantez icinde hedef surum.
    /// Zorunlu olsun olmasin daha yeni bir surum varsa gosterilir.
    public string VersionLabel =>
        UpdateAvailable ? $"v{Constants.AppVersion} (v{LatestVersion})" : $"v{Constants.AppVersion}";

    public string UpdateButtonLabel => IsUpdating ? "Güncelleniyor..." : "Güncelle";

    /// Account sekmesi: API'den gelen yayindaki surum, henuz okunmadiysa bos.
    public string LatestVersionLabel =>
        string.IsNullOrEmpty(LatestVersion)
            ? ""
            : UpdateAvailable ? $"Yeni sürüm mevcut: v{LatestVersion}" : "En güncel sürüm";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginFormReady))]
    private string _email = "";
    [ObservableProperty] private string _loginError = "";
    [ObservableProperty] private bool _isLoggingIn;
    [ObservableProperty] private bool _isLoggingOut;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginFormReady))]
    private bool _hasPassword;

    public bool LoginFormReady => !string.IsNullOrWhiteSpace(Email) && HasPassword && !IsLoggingIn;

    public ObservableCollection<PrinterInfo> Printers { get; } = new();
    public ObservableCollection<LogEntry> Logs { get; } = new();
    public ObservableCollection<PrintJobRecord> Jobs { get; } = new();

    public MainViewModel()
    {
        DeviceId = _agent.DeviceId;

        _agent.StateChanged += () => Application.Current.Dispatcher.Invoke(SyncState);
        _agent.LogAdded += entry => Application.Current.Dispatcher.Invoke(() =>
        {
            Logs.Insert(0, entry);
            if (Logs.Count > 200) Logs.RemoveAt(Logs.Count - 1);
        });

        _agent.LoadSavedAuth();

        if (_agent.IsAuthenticated)
        {
            IsAuthenticated = true;
            Customer = _agent.Auth?.Customer;
            ConnectionState = "Connecting...";
        }
        else
        {
            ConnectionState = "Not logged in";
        }

        _agent.Start();
        _ = CheckForUpdateAsync();
        _ = VersionRefreshLoopAsync();
    }

    // Agent gunlerce/haftalarca acik kalabiliyor; surum etiketi yalnizca acilista
    // okunursa hep bayat kalir. Sunucu tarafi cache'li oldugu icin bu yoklama
    // ucuz.
    private async Task VersionRefreshLoopAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromHours(6));
            await CheckForUpdateAsync(prompt: false);
        }
    }

    private void SyncState()
    {
        IsConnectedToApi = _agent.IsConnected;
        IsAuthenticated = _agent.IsAuthenticated;
        Customer = _agent.Auth?.Customer;
        ConnectionState = _agent.IsConnected ? "Connected" : (_agent.IsAuthenticated ? "Connecting..." : "Not logged in");

        SyncPrinters();
        SyncJobs();
    }

    private void SyncPrinters()
    {
        var source = _agent.Printers;
        var existingIds = Printers.ToDictionary(p => p.Name, p => p.ExternalId);

        var sourceNames = new HashSet<string>(source.Select(p => p.Name));
        for (int i = Printers.Count - 1; i >= 0; i--)
        {
            if (!sourceNames.Contains(Printers[i].Name))
                Printers.RemoveAt(i);
        }

        for (int i = 0; i < source.Count; i++)
        {
            var p = source[i];
            if (string.IsNullOrEmpty(p.ExternalId) && existingIds.TryGetValue(p.Name, out var savedId))
                p.ExternalId = savedId;

            var existing = Printers.FirstOrDefault(x => x.Name == p.Name);
            if (existing == null)
            {
                Printers.Insert(Math.Min(i, Printers.Count), p);
            }
            else
            {
                existing.Status = p.Status;
                existing.ConnectionType = p.ConnectionType;
                existing.DriverName = p.DriverName;
                existing.PortName = p.PortName;
                if (!string.IsNullOrEmpty(p.ExternalId))
                    existing.ExternalId = p.ExternalId;
            }
        }
    }

    private void SyncJobs()
    {
        var source = _agent.Jobs;

        for (int i = Jobs.Count - 1; i >= 0; i--)
        {
            if (!source.Any(j => j.JobId == Jobs[i].JobId))
                Jobs.RemoveAt(i);
        }

        for (int i = 0; i < source.Count; i++)
        {
            var j = source[i];
            var existing = Jobs.FirstOrDefault(x => x.JobId == j.JobId);
            if (existing == null)
            {
                Jobs.Insert(Math.Min(i, Jobs.Count), j);
            }
            else
            {
                existing.Status = j.Status;
                existing.CompletedAt = j.CompletedAt;
                existing.ErrorMessage = j.ErrorMessage;
            }
        }
    }

    public async Task DoLoginAsync(string password)
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(password))
        {
            LoginError = "Email and password required";
            return;
        }

        IsLoggingIn = true;
        LoginError = "";

        try
        {
            var result = await _agent.LoginAsync(Email, password);
            if (!string.IsNullOrEmpty(result?.SessionToken))
            {
                _agent.SetAuth(new AuthData
                {
                    SessionToken = result!.SessionToken,
                    Token = result.AccessToken,
                    Customer = result.Customer,
                });
                IsAuthenticated = true;
                Customer = result.Customer;
                ConnectionState = "Connecting...";
            }
            else
            {
                LoginError = "Login failed: unexpected response";
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            LoginError = "Invalid email or password";
        }
        catch (ClockSkewException ex)
        {
            // Saat farki: kullaniciya yonlendirici mesaj; ham detay log'a.
            _agent.AddLog("Error", $"Login blocked by clock skew: {ex.Message} | {ex.InnerException}");
            LoginError = ex.Message;
        }
        catch (Exception ex)
        {
            var fullError = ex.ToString();
            _agent.AddLog("Error", $"Login exception: {fullError}");
            LoginError = $"Login failed: {fullError}";
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    public event Action? LoggedOut;

    [RelayCommand]
    private async Task LogoutAsync()
    {
        IsLoggingOut = true;
        await Task.Run(() => _agent.Stop());
        // Sign-out is the only thing that ends a session, so tell the server:
        // otherwise the revoked-device list in the panel would drift.
        await _agent.LogoutAsync();
        IsAuthenticated = false;
        IsConnectedToApi = false;
        Customer = null;
        ConnectionState = "Not logged in";
        Printers.Clear();
        Logs.Clear();
        Jobs.Clear();
        LoginError = "";
        IsLoggingOut = false;
        LoggedOut?.Invoke();
        _agent.Start();
    }

    [RelayCommand]
    private async Task TestPrintAsync(string printerName)
    {
        await _agent.TestPrintAsync(printerName);
    }

    [RelayCommand]
    private void Refresh()
    {
        SyncState();
    }

    [RelayCommand]
    private void ClearJobs()
    {
        lock (_agent.Jobs)
        {
            _agent.Jobs.RemoveAll(j => j.Status is "done" or "failed");
        }
        SyncJobs();
    }

    /// <summary>
    /// Surum bilgisini API'den okur; <paramref name="prompt"/> yalnizca acilista
    /// true'dur. Periyodik yenilemede kullaniciya tekrar tekrar kutu acilmaz,
    /// sadece alt bardaki etiket guncellenir.
    /// </summary>
    private async Task CheckForUpdateAsync(bool prompt = true)
    {
        try
        {
            var update = await _agent.FetchVersionInfoAsync();
            if (update == null) return;

            // Once adres, sonra surum: LatestVersion degisince UpdateCommand'in
            // CanExecute'u yeniden degerlendirilir ve adresin hazir olmasi gerekir.
            _latestDownloadUrl = update.GetWindowsUrl();
            LatestVersion = update.LatestVersion;
            UpdateCommand.NotifyCanExecuteChanged();
            if (!prompt || !UpdateAvailable || string.IsNullOrEmpty(_latestDownloadUrl)) return;

            var msg = $"Yeni sürüm mevcut: v{update.LatestVersion}\n\n{update.ReleaseNotes}\n\nGüncellemek ister misiniz?";
            var result = MessageBox.Show(msg, "Güncelleme Mevcut", MessageBoxButton.YesNo, MessageBoxImage.Information);

            // Hayir denirse alt bardaki Guncelle dugmesi kalir; kullanici sonra da baslatabilir.
            if (result != MessageBoxResult.Yes) return;

            await UpdateAsync();
        }
        catch (Exception ex)
        {
            _agent.AddLog("Warning", $"Update check error: {ex.Message}");
        }
    }

    private bool CanUpdate() => UpdateAvailable && !IsUpdating && !string.IsNullOrEmpty(_latestDownloadUrl);

    /// <summary>
    /// Alt bardaki Guncelle dugmesi ve acilis kutusundaki Evet ayni yoldan gecer:
    /// installer indirilir, sessiz kurulum baslatilir ve uygulama kapanir
    /// (kurulum agent'i kendisi yeniden baslatir).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        if (!CanUpdate()) return;
        IsUpdating = true;
        try
        {
            await _agent.DownloadAndInstallUpdateAsync(_latestDownloadUrl, LatestVersion);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _agent.AddLog("Error", $"Update download failed: {ex.Message}");
            MessageBox.Show($"Güncelleme indirilemedi:\n{ex.Message}", "Güncelleme Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsUpdating = false;
        }
    }
}
