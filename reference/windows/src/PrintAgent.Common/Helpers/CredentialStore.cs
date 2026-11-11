using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PrintAgent.Common.Models;

namespace PrintAgent.Common.Helpers;

public static class CredentialStore
{
    private static string FilePath => Path.Combine(UserDataDirectory.Ensure(), "credentials.dat");

    public static void Save(AuthData auth)
    {
        var json = JsonSerializer.Serialize(auth);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var path = FilePath;
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Credentials cannot be a link.");
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static AuthData? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(FilePath);
            if ((File.GetAttributes(FilePath) & FileAttributes.ReparsePoint) != 0) return null;
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AuthData>(Encoding.UTF8.GetString(decrypted));
        }
        catch { return null; }
    }

    public static void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
