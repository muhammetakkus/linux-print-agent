using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PrintAgent.UI;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(PrintAgent.Desktop.Tests.UiTestApp))]

namespace PrintAgent.Desktop.Tests;

public static class UiTestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class UiTests
{
    [AvaloniaFact]
    public void Reference_login_and_printer_views_render_to_PNG()
    {
        var client = new FakeLocalClient();
        var model = new MainViewModel(client);
        var window = new MainWindow(model); window.Show();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "NuxPrint.Linux.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var output = Path.Combine(root.FullName, "artifacts", "preview"); Directory.CreateDirectory(output);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using (var frame = window.CaptureRenderedFrame())
        { Assert.NotNull(frame); frame.Save(Path.Combine(output, "login.png")); }
        client.State.IsAuthenticated = true;
        client.State.Printers.Add(new() { Name = "Office Printer", Status = "online", DriverName = "CUPS IPP", ConnectionType = "Network", ExternalId = "demo-printer-id" });
        model.Apply(client.State);
        window.FindControl<TabControl>("AgentTabs")!.SelectedIndex = 1;
        window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using (var frame = window.CaptureRenderedFrame())
        { Assert.NotNull(frame); frame.Save(Path.Combine(output, "printers.png")); }
        window.Close();
    }

    [AvaloniaFact]
    public async Task Login_uses_IPC_and_shows_only_reference_tabs()
    {
        var client = new FakeLocalClient();
        var model = new MainViewModel(client) { Email = "user", Password = "test-only" };
        var window = new MainWindow(model);
        window.Show();
        await model.LoginCommand.ExecuteAsync(null);
        Assert.Equal("login", client.LastRequest!.Method);
        Assert.True(model.IsAuthenticated);
        Assert.Empty(model.Password);
        Assert.False(model.ShowPassword);
        var tabs = window.FindControl<TabControl>("AgentTabs")!;
        Assert.True(tabs.IsVisible);
        Assert.Equal(new[] { "Account", "Printers", "Jobs", "Log" }, tabs.Items.Cast<TabItem>().Select(t => t.Header));
        window.Close();
        Assert.NotEqual("logout", client.LastRequest.Method);
    }

    [AvaloniaFact]
    public async Task Closing_window_does_not_logout_or_stop_service_and_errors_stay_in_view()
    {
        var client = new FakeLocalClient();
        var model = new MainViewModel(client);
        var window = new MainWindow(model);
        window.Show();
        await model.LoginCommand.ExecuteAsync(null);
        Assert.Equal("Email and password required", model.Error);
        window.Close();
        Assert.DoesNotContain(typeof(MainViewModel).Assembly.GetReferencedAssemblies(), a => a.Name is "SocketIOClient" or "PrintAgent.Core" or "PrintAgent.Service");
    }
}
