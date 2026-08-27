using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VisualSSH.Services;

namespace VisualSSH.Services.GameOptimizer;

public static class ArtworkCache
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string Dir = AppDataPaths.CacheDir("art-cache");

    public static async Task<byte[]?> GetOrFetchAsync(string url, Func<CancellationToken, Task<byte[]?>> fetch,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        Directory.CreateDirectory(Dir);
        var key = Hash(url);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var path = Path.Combine(Dir, key + ".bin");
            if (File.Exists(path))
            {
                var cached = await File.ReadAllBytesAsync(path, ct);
                if (cached.Length > 200) return cached;
            }

            var bytes = await fetch(ct);
            if (bytes is { Length: > 200 })
            {
                try { await File.WriteAllBytesAsync(path, bytes, ct); }
                catch { /* cache is optioneel */ }
            }
            return bytes;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string Hash(string url) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url.Trim()))).ToLowerInvariant();
}
