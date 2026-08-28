using System.Diagnostics;
using System.IO;
using Sesame.Models;

namespace Sesame.Services;

public static class LocalOpen
{
    public const long OpenLimitBytes = 80L * 1024 * 1024;

    public static string CacheFolder =>
        Path.Combine(Path.GetTempPath(), "SESAME", "open");

    public static string DownloadAndOpen(DeckClient client, RemoteItem item)
    {
        var folder = Path.Combine(CacheFolder, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var local = Path.Combine(folder, item.Name);
        client.DownloadFile(item.FullPath, local);
        Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        return local;
    }

    public static void OpenLocalPath(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
