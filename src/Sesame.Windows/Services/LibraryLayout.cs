using Sesame.Models;

namespace Sesame.Services;

/// <summary>
/// Empty ROM / Hydra / Switch folders on the Deck. No game list is created.
/// </summary>
public static class LibraryLayout
{
    public static IReadOnlyList<string> FolderPaths(AppCatalog catalog)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in LibraryPaths.Current.FolderPaths())
            AddDir(paths, path);
        AddDir(paths, catalog.EdenMods);
        AddDir(paths, catalog.EdenSaves);
        return paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    public static void Ensure(DeckClient client, AppCatalog catalog)
    {
        var paths = FolderPaths(catalog);
        if (paths.Count == 0) return;

        // One SSH round-trip instead of dozens of SFTP mkdir calls (was blocking UI list).
        try
        {
            var quoted = string.Join(" ", paths.Select(DeckClient.ShQuote));
            client.Execute("mkdir -p " + quoted + " 2>/dev/null || true", 25);
            return;
        }
        catch
        {
            // fall through to per-folder SFTP
        }

        foreach (var path in paths)
        {
            try { client.EnsureDirectory(path); }
            catch { /* folder layout is best-effort */ }
        }
    }

    private static void AddDir(HashSet<string> paths, string path)
    {
        path = (path ?? "").Trim().Replace('\\', '/').TrimEnd('/');
        if (path.Length == 0) return;
        paths.Add(path);
    }
}
