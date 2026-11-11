using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintAgent.Common.Local;
using PrintAgent.Common.Models;

namespace PrintAgent.UI;

public partial class MainViewModel(ILocalAgentClient client) : ObservableObject
{
    [ObservableProperty] private bool _isAuthenticated;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionState = "Starting...";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LoginFormReady))] private string _email = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LoginFormReady))] private string _password = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LoginFormReady))] private bool _busy;
    [ObservableProperty] private bool _showPassword;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _notification = "";
    [ObservableProperty] private string _deviceId = "";
    [ObservableProperty] private string _versionLabel = "";
    [ObservableProperty] private string _latestVersionLabel = "";
    [ObservableProperty] private CustomerInfo? _customer;
    [ObservableProperty] private bool _updateAvailable;
    private string _latestVersion = "";
    public bool LoginFormReady => !Busy && !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrEmpty(Password);
    public ObservableCollection<PrinterInfo> Printers { get; } = [];
    public ObservableCollection<PrintJobRecord> Jobs { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public char PasswordCharacter => ShowPassword ? '\0' : '●';
    partial void OnShowPasswordChanged(bool value) => OnPropertyChanged(nameof(PasswordCharacter));

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await client.SendAsync(new() { Method = "status" }, ct);
            if (!response.Success) { Error = response.Error ?? "Agent request failed"; return; }
            if (response.State != null) Apply(response.State);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
        { ConnectionState = "Service unavailable"; IsConnected = false; Error = ex.Message; }
    }

    public void Apply(AgentSnapshot state)
    {
        IsAuthenticated = state.IsAuthenticated; IsConnected = state.IsConnected; Customer = state.Customer;
        DeviceId = state.DeviceId; VersionLabel = "v" + state.Version + (UpdateAvailable ? $" (v{_latestVersion})" : "");
        ConnectionState = state.IsConnected ? "Connected" : state.IsAuthenticated ? "Connecting..." : "Not logged in";
        for (var i = Printers.Count - 1; i >= 0; i--) if (!state.Printers.Any(p => p.Name == Printers[i].Name)) Printers.RemoveAt(i);
        for (var i = 0; i < state.Printers.Count; i++)
        {
            var incoming = state.Printers[i];
            var existing = Printers.FirstOrDefault(p => p.Name == incoming.Name);
            if (existing == null) Printers.Insert(Math.Min(i, Printers.Count), incoming);
            else
            {
                existing.Status = incoming.Status; existing.DriverName = incoming.DriverName;
                existing.ConnectionType = incoming.ConnectionType; existing.PortName = incoming.PortName;
                existing.ExternalId = incoming.ExternalId;
                existing.Capabilities = incoming.Capabilities; existing.IsActive = incoming.IsActive;
            }
        }
        ReplaceChanged(Jobs, state.Jobs, j => j.JobId);
        ReplaceChanged(Logs, state.Logs, l => $"{l.Timestamp.Ticks}:{l.JobId}:{l.Message}");
    }

    private static void ReplaceChanged<T>(ObservableCollection<T> target, List<T> source, Func<T, string> key)
    {
        for (var i = target.Count - 1; i >= 0; i--) if (!source.Any(s => key(s) == key(target[i]))) target.RemoveAt(i);
        for (var i = 0; i < source.Count; i++)
        {
            var existing = target.FirstOrDefault(s => key(s) == key(source[i]));
            if (existing == null) target.Insert(Math.Min(i, target.Count), source[i]);
            else if (System.Text.Json.JsonSerializer.Serialize(existing) != System.Text.Json.JsonSerializer.Serialize(source[i])) target[target.IndexOf(existing)] = source[i];
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password)) { Error = "Email and password required"; return; }
        if (Busy) return;
        try { await SendUserActionAsync(new() { Method = "login", Email = Email, Password = Password }); }
        finally { Password = ""; ShowPassword = false; }
    }
    [RelayCommand] private async Task LogoutAsync()
    {
        await SendUserActionAsync(new() { Method = "logout" });
        if (!IsAuthenticated) { Password = ""; ShowPassword = false; }
    }
    [RelayCommand] private Task Refresh() => RefreshAsync();
    [RelayCommand] private Task ClearJobs() => SendUserActionAsync(new() { Method = "clear-jobs" });
    [RelayCommand] private Task TestPrint(string printer) => SendUserActionAsync(new() { Method = "test-print", PrinterName = printer });
    [RelayCommand] private void Update() => Notification = "Automatic Linux update is unavailable. Install the matching NuxPrint DEB package.";

    private async Task SendUserActionAsync(LocalRequest request)
    {
        if (Busy) return;
        Busy = true; Error = "";
        try
        {
            var response = await client.SendAsync(request);
            if (!response.Success) Error = response.Error ?? "Agent request failed";
            if (response.State != null) Apply(response.State);
            if (response.PrintResult != null)
                Notification = $"Test print: {response.PrintResult.Status} {response.PrintResult.ErrorMessage}";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { request.Password = null; Busy = false; }
    }

    public async Task CheckVersionAsync(CancellationToken ct)
    {
        try
        {
            var response = await client.SendAsync(new() { Method = "version" }, ct);
            if (response.Version != null)
            {
                _latestVersion = response.Version.LatestVersion;
                UpdateAvailable = Version.TryParse(_latestVersion, out var latest) && Version.TryParse(PrintAgent.Common.Constants.AppVersion, out var current)
                    ? latest > current : string.Compare(_latestVersion, PrintAgent.Common.Constants.AppVersion, StringComparison.OrdinalIgnoreCase) > 0;
                LatestVersionLabel = UpdateAvailable ? "New version available: v" + _latestVersion : "Up to date";
            }
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException) { }
    }
}
