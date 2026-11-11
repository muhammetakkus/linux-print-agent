using System.Diagnostics;
using System.Net.Http;
using PrintAgent.Common;
using PrintAgent.Common.Helpers;

namespace PrintAgent.UI.Services;

public static class SecureUpdater
{
    public static Uri ValidateTarget(string url, string version)
    {
        if (!Version.TryParse(version, out var parsed) || parsed <= Version.Parse(Constants.AppVersion)) throw new ArgumentException("Invalid update version.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "nuxprint.com" ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Update must use the trusted HTTPS origin.");
        return uri;
    }

    public static async Task InstallAsync(string url, string version)
    {
        var target = ValidateTarget(url, version);
        var pin = Path.Combine(AppContext.BaseDirectory, "trusted-update-signer.sha256");
        ExecutableTrust.RequireInstalledPath(pin);
        if (!File.Exists(pin)) throw new IOException("Automatic updates require a configured trusted signer. Install an authenticated package manually.");
        var directory = Path.Combine(UserDataDirectory.Ensure(), "update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "setup.exe");
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            response.EnsureSuccessStatusCode();
            const long maximum = 200 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximum) throw new IOException("Update is too large.");
            await using (var input = await response.Content.ReadAsStreamAsync(budget.Token))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, budget.Token)) > 0)
                {
                    total += read; if (total > maximum) throw new IOException("Update is too large.");
                    await output.WriteAsync(buffer.AsMemory(0, read), budget.Token);
                }
            }
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ExecutableTrust.VerifyPublisher(path, pin);
            var metadata = FileVersionInfo.GetVersionInfo(path);
            ValidateSignedMetadata(metadata.ProductName, metadata.ProductVersion, version);
            // The installer handles closing/restarting the agent. No command interpreter or untrusted version in a path.
            var start = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = directory };
            start.ArgumentList.Add("/SILENT"); start.ArgumentList.Add("/NORESTART");
            Process.Start(start)?.Dispose();
        }
        catch { if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); throw; }
    }

    public static void ValidateSignedMetadata(string? product, string? signedVersion, string requestedVersion)
    {
        static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
        if (product != "NuxPrint Agent" || !Version.TryParse(signedVersion, out var actual) ||
            !Version.TryParse(requestedVersion, out var requested) || Normalize(actual) != Normalize(requested) ||
            Normalize(actual) <= Normalize(Version.Parse(Constants.AppVersion)))
            throw new IOException("Signed installer product or version does not match the requested update.");
    }
}
