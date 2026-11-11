using System.Security.AccessControl;
using System.Security.Principal;

namespace PrintAgent.Common.Helpers;

public static class UserDataDirectory
{
    public static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NuxPrintAgent");
    public static string Ensure(string? directoryPath = null)
    {
        var directory = Directory.CreateDirectory(directoryPath ?? PathName);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Agent data directory cannot be a link.");
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Cannot identify current user.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(user);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
        return directory.FullName;
    }
}
