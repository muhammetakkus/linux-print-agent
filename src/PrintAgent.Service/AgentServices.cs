using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrintAgent.Common.Models;
using PrintAgent.Core;
using PrintAgent.Linux;
using SocketIOClient;

namespace PrintAgent.Service;

public static class AgentServices
{
    public static IServiceCollection AddNuxPrintAgent(this IServiceCollection services, AgentOptions options)
    {
        options.PrintSafety.Validate();
        if (options.MaxLocalClients is < 1 or > 128 || options.LocalRequestTimeoutSeconds is < 1 or > 300)
            throw new ArgumentException("Invalid local IPC limits.");
        services.AddSingleton(options);
        services.AddSingleton(_ => SingleInstanceLease.Acquire(Path.Combine(options.RuntimeDirectory, "agent.lock")));
        services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(90));
        services.AddSystemd();
        services.AddSingleton(_ => new IppClient(new Uri(options.CupsServer), options.CupsSocketPath));
        services.AddSingleton<ICommandRunner, ProcessRunner>();
        services.AddSingleton<ICupsClient, CupsClient>();
        services.AddSingleton<PrinterCatalog>();
        services.AddSingleton<ICredentialStore>(_ => OperatingSystem.IsLinux()
            ? new LinuxCredentialStore(options.StateDirectory)
            : throw new PlatformNotSupportedException("Linux credential storage requires Linux."));
        services.AddSingleton(sp =>
        {
            // Acquire the device lease before creating/reading its persistent identity.
            _ = sp.GetRequiredService<SingleInstanceLease>();
            if (OperatingSystem.IsLinux())
                Directory.CreateDirectory(options.StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else Directory.CreateDirectory(options.StateDirectory);
            var identity = DeviceIdentity.GetOrCreate(Path.Combine(options.StateDirectory, "device_id"));
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(options.StateDirectory, "device_id"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new CloudSession(new DeviceInfo { DeviceId = identity, DeviceName = Environment.MachineName, Platform = "linux" }, sp.GetRequiredService<ICredentialStore>());
        });
        services.AddSingleton(sp => new LinuxPrintEngine(sp.GetRequiredService<ICupsClient>(), () => sp.GetRequiredService<PrinterCatalog>().Printers,
            options.CacheDirectory, options.PrintSafety));
        services.AddSingleton<Func<string, SocketIOOptions, IAgentSocket>>(_ => (url, settings) => new SocketIoAgentSocket(url, settings));
        services.AddSingleton(sp => new CloudConnection(sp.GetRequiredService<CloudSession>(), () => sp.GetRequiredService<PrinterCatalog>().Printers,
            sp.GetRequiredService<LinuxPrintEngine>().PrintAsync, sp.GetRequiredService<Func<string, SocketIOOptions, IAgentSocket>>(),
            cancellablePrintJob: sp.GetRequiredService<LinuxPrintEngine>().PrintAsync));
        services.AddSingleton<AgentWorker>();
        services.AddSingleton<CloudRuntime>();
        services.AddSingleton<AgentLogBuffer>();
        services.AddSingleton<LocalAgentController>();
        services.AddHostedService(sp => sp.GetRequiredService<AgentWorker>());
        services.AddHostedService<LocalIpcServer>();
        return services;
    }
}
