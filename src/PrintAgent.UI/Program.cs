using Avalonia;
using PrintAgent.UI;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("NuxPrint Desktop supports Linux only.");
AppBuilder.Configure<App>().UseX11().UseSkia().LogToTrace().StartWithClassicDesktopLifetime(args);
