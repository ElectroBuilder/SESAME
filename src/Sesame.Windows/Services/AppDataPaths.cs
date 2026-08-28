using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Sesame.Services;

/// <summary>
/// App data for SESAME. Windows: %APPDATA%\SESAME (migrated from VisualSSH).
/// Linux: ~/.local/share/sesame. Access is limited to the current user.
/// </summary>
public static class AppDataPaths
{
    public static string Root => _root.Value;
    private static readonly Lazy<string> _root = new(ResolveRoot);

    public static string Combine(params string[] parts)
    {
        var items = new string[parts.Length + 1];
        items[0] = Root;
        Array.Copy(parts, 0, items, 1, parts.Length);
        return Path.Combine(items);
    }

    public static string CacheDir(string name)
    {
        var dir = Combine(name);
        Directory.CreateDirectory(dir);
        RestrictDirectory(dir);
        return dir;
    }

    public static string SafeFileName(string value)
    {
        var text = (value ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            text = text.Replace(c, '-');
        return string.IsNullOrEmpty(text) ? "default" : text;
    }

    public static int ClearCaches()
    {
        var n = 0;
        n += DeleteTree(Combine("art-cache"));
        n += DeleteTree(Combine("store-cache"));
        n += DeleteTree(Combine("optimizer-cache"));
        n += DeleteFile(Combine("optimizer-cache.json"));
        EnsureProtected();
        return n;
    }

    public static void EnsureProtected()
    {
        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.Combine(Root, "secrets"));
            RestrictDirectory(Root);
            RestrictDirectory(Path.Combine(Root, "secrets"));
            RestrictDirectory(Path.Combine(Root, "art-cache"));
            RestrictDirectory(Path.Combine(Root, "store-cache"));
            RestrictDirectory(Path.Combine(Root, "optimizer-cache"));
        }
        catch
        {
            /* ACL/chmod is best-effort */
        }
    }

    private static string ResolveRoot()
    {
        var dest = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SESAME")
            : UnixDataDir();
        MigrateIfNeeded(LegacyRoot(), dest);
        return dest;
    }

    private static string UnixDataDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            return Path.Combine(xdg, "sesame");
        return Path.Combine(home, ".local", "share", "sesame");
    }

    private static string LegacyRoot() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VisualSSH")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "VisualSSH");

    private static void MigrateIfNeeded(string from, string to)
    {
        try
        {
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(from)) return;
            if (!Directory.Exists(to))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                try
                {
                    Directory.Move(from, to);
                    return;
                }
                catch
                {
                    Directory.CreateDirectory(to);
                }
            }
            CopyMissing(from, to);
        }
        catch
        {
            /* migratie is best-effort; nieuwe map wordt alsnog aangemaakt */
        }
    }

    private static void CopyMissing(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, dir);
            Directory.CreateDirectory(Path.Combine(to, rel));
        }
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            if (File.Exists(dest)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }

    private static int DeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            var n = Directory.GetFiles(path, "*", SearchOption.AllDirectories).Length;
            Directory.Delete(path, recursive: true);
            return n;
        }
        catch
        {
            return 0;
        }
    }

    private static int DeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            File.Delete(path);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    public static void RestrictFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (OperatingSystem.IsWindows())
                RestrictFileWindows(path);
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            /* verbergen voor andere accounts is best-effort */
        }
    }

    public static void RestrictDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            if (OperatingSystem.IsWindows())
                RestrictDirectoryWindows(path);
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            /* map-ACL is best-effort */
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictFileWindows(string path)
    {
        var info = new FileInfo(path);
        var security = new FileSecurity();
        ApplyCurrentUserOnly(security, directory: false);
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictDirectoryWindows(string path)
    {
        var info = new DirectoryInfo(path);
        var security = new DirectorySecurity();
        ApplyCurrentUserOnly(security, directory: true);
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyCurrentUserOnly(FileSystemSecurity security, bool directory)
    {
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = directory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        Allow(security, WindowsIdentity.GetCurrent().User, inherit);
        Allow(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), inherit);
    }

    [SupportedOSPlatform("windows")]
    private static void Allow(FileSystemSecurity security, SecurityIdentifier? sid, InheritanceFlags inherit)
    {
        if (sid is null) return;
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            inherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }
}
