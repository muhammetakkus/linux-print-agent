using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using PrintAgent.Common.Local;

namespace PrintAgent.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TrayIcon? _tray;
    private bool _quitting;
    private bool _polling;
    private DateTimeOffset _nextVersion = DateTimeOffset.MinValue;

    public MainWindow() : this(new MainViewModel(new LocalAgentClient())) { }
    public MainWindow(MainViewModel viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = viewModel; DataContext = viewModel;
        Opened += async (_, _) => { await PollAsync(); _timer.Start(); };
        _timer.Tick += async (_, _) => await PollAsync();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized && _tray != null)
            { Hide(); Notify("NuxPrint Agent", "Running in background"); }
        };
        if (Application.Current is { } application)
        {
            var show = new NativeMenuItem("Show"); show.Click += (_, _) => ShowWindow();
            var quit = new NativeMenuItem("Quit"); quit.Click += (_, _) => { _quitting = true; Close(); };
            var menu = new NativeMenu(); menu.Items.Add(show); menu.Items.Add(quit);
            _tray = new TrayIcon { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PrintAgent.UI/Assets/icon.png"))), ToolTipText = "NuxPrint Agent", Menu = menu };
            _tray.Clicked += (_, _) => ShowWindow();
            if (_tray.NativeMenuExporter != null) TrayIcon.SetIcons(application, new TrayIcons { _tray });
            else { _tray.Dispose(); _tray = null; }
        }
    }

    private async Task PollAsync()
    {
        if (_polling || _lifetime.IsCancellationRequested) return;
        _polling = true;
        var token = _lifetime.Token;
        try
        {
            await _viewModel.RefreshAsync(token);
            if (token.IsCancellationRequested) return;
            if (DateTimeOffset.UtcNow >= _nextVersion)
            { await _viewModel.CheckVersionAsync(token); _nextVersion = DateTimeOffset.UtcNow.AddHours(6); }
        }
        finally { _polling = false; }
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private async void CopyApiKey(object? sender, RoutedEventArgs e) => await CopyAsync(_viewModel.Customer?.ApiKey, "API Key copied");
    private async void CopyPrinterId(object? sender, RoutedEventArgs e) => await CopyAsync((sender as Button)?.Tag as string, "Printer ID copied");
    private async Task CopyAsync(string? text, string message)
    {
        if (string.IsNullOrEmpty(text) || Clipboard == null) return;
        try { await Clipboard.SetTextAsync(text); _viewModel.Notification = message; Notify("Copied", message); }
        catch (Exception ex) { _viewModel.Error = ex.Message; }
    }
    private static void Notify(string title, string message)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("/usr/bin/notify-send") { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(title); info.ArgumentList.Add(message);
            System.Diagnostics.Process.Start(info)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_quitting && _tray != null) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop(); _lifetime.Cancel(); _tray?.Dispose(); _lifetime.Dispose();
        base.OnClosed(e);
        // Closing this client never stops the Agent Service.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
