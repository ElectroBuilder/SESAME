using System.IO;

namespace VisualSSH.Services;

public sealed class RomSaveResult
{
    public required string Path { get; init; }
    public bool UsedLocalFallback { get; init; }
    public string Message { get; init; } = "";
}

public static class RomOutput
{
    public static string LocalDir()
    {
        var dir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SESAME")
            : AppDataPaths.Combine("rom-output");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static Task<RomSaveResult> SaveAsync(
        byte[] rom, string fileName, Action<int, string>? progress, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(97, "Bestand schrijven…");

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var destName = Path.GetFileNameWithoutExtension(fileName) + " " + stamp + Path.GetExtension(fileName);
            var dest = Path.Combine(LocalDir(), destName);
            var tmp = Path.Combine(Path.GetTempPath(), "vssh-" + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                File.WriteAllBytes(tmp, rom);
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(99, "Bestand afronden…");
                File.Move(tmp, dest);
            }
            finally
            {
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
                catch { /* tmp is optioneel */ }
            }

            if (!File.Exists(dest) || new FileInfo(dest).Length != rom.Length)
                throw new IOException("Het ROM-bestand is niet volledig weggeschreven.");

            progress?.Invoke(100, "ROM klaar.");
            return new RomSaveResult
            {
                Path = dest,
                UsedLocalFallback = false,
                Message = "Bestand: " + dest
            };
        }, ct);
}
