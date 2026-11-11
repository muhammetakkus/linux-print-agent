using System.Runtime.InteropServices;

namespace PrintAgent.Linux;

public sealed class SingleInstanceLease : IDisposable
{
    private readonly FileStream _file;
    private SingleInstanceLease(FileStream file) => _file = file;

    public static SingleInstanceLease Acquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var file = new FileStream(path, options);
        if (OperatingSystem.IsLinux() && flock(file.SafeFileHandle.DangerousGetHandle().ToInt32(), 2 | 4) != 0)
        {
            file.Dispose();
            throw new IOException("Another NuxPrint Agent already holds the device lock.");
        }
        return new SingleInstanceLease(file);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);
    // Do not delete the lock path: unlinking it allows another process to lock a different inode.
    public void Dispose() => _file.Dispose();
}
