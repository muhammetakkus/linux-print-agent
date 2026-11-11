using System.Threading;
using System.Windows;
using PrintAgent.UI.Services;

namespace PrintAgent.UI;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.FirstOrDefault() == "--raw-worker")
        {
            try { Shutdown(RawPrintWorker.Run(e.Args)); }
            catch { Shutdown(1); }
            return;
        }
        _mutex = new Mutex(true, PrintAgent.Common.Constants.MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("NuxPrint Agent is already running.", "NuxPrint Agent", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AgentService.Instance.Stop();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
