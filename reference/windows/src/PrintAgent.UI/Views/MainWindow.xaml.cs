using System.Windows;
using System.Windows.Input;
using PrintAgent.UI.ViewModels;

namespace PrintAgent.UI.Views;

public partial class MainWindow : Window
{
    private MainViewModel VM => (MainViewModel)DataContext;
    private bool _passwordVisible;
    private bool _syncingPassword;

    public MainWindow()
    {
        InitializeComponent();
        VM.LoggedOut += () => Dispatcher.Invoke(() =>
        {
            PasswordBox.Clear();
            PasswordTextBox.Clear();
            _passwordVisible = false;
            PasswordBox.Visibility = Visibility.Visible;
            PasswordTextBox.Visibility = Visibility.Collapsed;
        });

        StateChanged += (s, e) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                TrayIcon.ShowBalloonTip("NuxPrint Agent", "Running in background", Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
            }
        };
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        var pw = _passwordVisible ? PasswordTextBox.Text : PasswordBox.Password;
        try { await VM.DoLoginAsync(pw); } finally { PasswordBox.Clear(); PasswordTextBox.Clear(); }
    }

    private async void LoginForm_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var pw = _passwordVisible ? PasswordTextBox.Text : PasswordBox.Password;
            try { await VM.DoLoginAsync(pw); } finally { PasswordBox.Clear(); PasswordTextBox.Clear(); }
        }
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        VM.HasPassword = PasswordBox.Password.Length > 0;
        _syncingPassword = true;
        PasswordTextBox.Text = PasswordBox.Password;
        _syncingPassword = false;
    }

    private void PasswordTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncingPassword) return;
        VM.HasPassword = PasswordTextBox.Text.Length > 0;
        _syncingPassword = true;
        PasswordBox.Password = PasswordTextBox.Text;
        _syncingPassword = false;
    }

    private void TogglePassword_Click(object sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;
        if (_passwordVisible)
        {
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordTextBox.Visibility = Visibility.Visible;
            PasswordTextBox.Focus();
            PasswordTextBox.CaretIndex = PasswordTextBox.Text.Length;
        }
        else
        {
            PasswordTextBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            PasswordBox.Focus();
        }
    }

    private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void CopyApiKey_Click(object sender, RoutedEventArgs e)
    {
        var key = VM.Customer?.ApiKey;
        if (!string.IsNullOrEmpty(key))
        {
            Clipboard.SetText(key);
            TrayIcon.ShowBalloonTip("Copied", "API Key copied", Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
        }
    }

    private void CopyPrinterId_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string id && !string.IsNullOrEmpty(id))
        {
            Clipboard.SetText(id);
            TrayIcon.ShowBalloonTip("Copied", $"Printer ID {id} copied", Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
        }
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        TrayIcon.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}

