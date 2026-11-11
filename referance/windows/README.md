# NuxPrint Cloud Print Agent - Windows Native

C# .NET 8 + WPF single desktop application. Connects local printers to the cloud API via Socket.IO.

## Architecture

```
PrintAgent.UI (WPF) — tek exe
    ├── UI (login, dashboard, system tray)
    ├── Socket.IO (API bağlantısı)
    ├── WMI (yazıcı keşfi & izleme)
    └── SumatraPDF (sessiz yazdırma)
```

## Projects

| Project | Type | Description |
|---------|------|-------------|
| PrintAgent.Common | Class Library | Shared models, helpers |
| PrintAgent.UI | WPF App | Main application (UI + background services) |
| PrintAgent.Service | Worker Service | (Optional) Standalone Windows Service mode |

## Prerequisites

- Windows 10/11 (64-bit)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Git

## Quick Start (Clone to Run)

```powershell
# 1. Clone
git clone https://github.com/user/windows-print-agent.git
cd windows-print-agent

# 2. Restore packages
dotnet restore

# 3. Build
dotnet build src/PrintAgent.UI -c Release

# 4. Run
dotnet run --project src/PrintAgent.UI -c Release
```

## Publish (Single EXE)

```powershell
# Publish as self-contained single file
dotnet publish src/PrintAgent.UI -c Release -o publish

# Run
.\publish\PrintAgent.UI.exe
```

## Installer (Inno Setup)

```powershell
# Publish + installer in one step (requires Inno Setup installed).
# Reads the version from Constants.cs and always publishes fresh binaries first.
.\build-installer.ps1
```

> Not: `iscc setup.iss` komutunu tek basina calistirmayin — installer `publish\ui`
> klasorunu paketler; once publish edilmezse installer icine ESKI build gomulur
> (surum etiketi yeni gorunse bile eski uygulama kurulur).

## Development (Debug Mode)

```powershell
# Run in debug
dotnet run --project src/PrintAgent.UI

# Watch mode (auto-rebuild on change)
dotnet watch run --project src/PrintAgent.UI
```

## Configuration

| Setting | Value |
|---------|-------|
| API URL | `https://nuxprint.com/api` |
| Socket.IO Namespace | `/agent` |
| Heartbeat Interval | 30 seconds |
| Printer Poll Interval | 30 seconds |
| Data Directory | `%ProgramData%\CloudPrintAgent\` |

## How It Works

1. User opens the app and logs in with email/password
2. App connects to the API via Socket.IO (`/agent` namespace)
3. Registers the device and local printers
4. Listens for `print:job` events from the API
5. Downloads PDF (base64), prints silently via SumatraPDF
6. Reports printer status changes in real-time via WMI
7. Runs in system tray when minimized — close button minimizes to tray

## Auto-Start with Windows (Optional)

```powershell
# Add to startup via registry
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "CloudPrintAgent" /t REG_SZ /d "C:\path\to\PrintAgent.UI.exe" /f

# Remove from startup
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "CloudPrintAgent" /f
```
