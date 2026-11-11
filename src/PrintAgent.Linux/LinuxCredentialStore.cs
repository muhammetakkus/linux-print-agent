using System.Text.Json;
using System.Runtime.Versioning;
using PrintAgent.Common.Models;
using PrintAgent.Core;

namespace PrintAgent.Linux;

// Approved Linux storage: account-only file permissions, not disk encryption.
[SupportedOSPlatform("linux")]
public sealed class LinuxCredentialStore(string directory) : ICredentialStore
{
    private string FilePath => Path.Combine(directory, "credentials.json");

    public AuthData? Load()
    {
        EnsureDirectory();
        if (!File.Exists(FilePath)) return null;
        try
        {
            RejectLink(FilePath);
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return JsonSerializer.Deserialize<AuthData>(File.ReadAllText(FilePath));
        }
        catch { return null; } // Same missing/unreadable/corrupt credential behavior as Windows.
    }

    public void Save(AuthData auth)
    {
        EnsureDirectory();
        var temporary = Path.Combine(directory, $".credentials-{Guid.NewGuid():N}");
        try
        {
            using (var file = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            })) JsonSerializer.Serialize(file, auth);
            if (File.Exists(FilePath)) RejectLink(FilePath);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Clear()
    {
        EnsureDirectory();
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    private void EnsureDirectory()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux credential storage requires Linux.");
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RejectLink(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Credential path must not be a symbolic link.");
    }
}
