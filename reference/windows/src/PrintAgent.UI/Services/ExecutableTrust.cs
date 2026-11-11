using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PrintAgent.UI.Services;

public static class ExecutableTrust
{
    public static void RequireInstalledPath(string path)
    {
        var full = Path.GetFullPath(path);
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        if (!roots.Any(root => !string.IsNullOrEmpty(root) && full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Trusted executables must be installed under Program Files.");
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Trusted executable path cannot contain links.");
    }

    public static void VerifyHash(string path, string manifest)
    {
        RequireInstalledPath(path); RequireInstalledPath(manifest);
        var expected = File.ReadAllText(manifest).Trim();
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (expected.Length != 64 || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Executable integrity check failed.");
    }

    public static void VerifyPublisher(string path, string pinFile)
    {
        RequireInstalledPath(pinFile);
        var expected = File.ReadAllText(pinFile).Trim();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new IOException("No trusted update signer configured. Install an authenticated package manually.");
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, pointer, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, RevocationChecks = 1, UnionChoice = 1, File = pointer, ProviderFlags = 0x80 };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) throw new IOException("Update signature is invalid or untrusted.");
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            if (!certificate.GetCertHashString(HashAlgorithmName.SHA256).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Update was signed by an unexpected publisher.");
        }
        finally { Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr Policy;
        public IntPtr Sip;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr State;
        public IntPtr Url;
        public uint ProviderFlags;
        public uint UiContext;
    }
}
