namespace Sesame.Services.GameOptimizer;

/// <summary>
/// Native Steam Deck apps SESAME tracks (not ROM games, not Hydra library entries).
/// </summary>
public static class DeckApps
{
    public sealed record Entry(string Id, string Title, string[] Needles);

    public static readonly Entry[] Catalog =
    [
        new("kodi", "Kodi", ["kodi", "tv.kodi"]),
        new("stremio", "Stremio", ["stremio", "com.stremio"]),
        new("hydra", "Hydra", ["hydralauncher", "com.hydralauncher", "io.hydralauncher"]),
        new("emudeck", "EmuDeck", ["emudeck", "com.emudeck"]),
        new("lutris", "Lutris", ["lutris", "net.lutris"]),
        new("chrome", "Google Chrome", ["google-chrome", "com.google.chrome", "chrome"]),
        new("chromium", "Chromium", ["chromium", "org.chromium"]),
        new("firefox", "Firefox", ["firefox", "org.mozilla.firefox"]),
        new("opera", "Opera", ["opera", "com.opera"]),
        new("brave", "Brave", ["brave", "com.brave"]),
        new("edge", "Microsoft Edge", ["microsoft-edge", "com.microsoft.edge", "msedge"]),
        new("plex", "Plex", ["plex", "tv.plex"]),
        new("jellyfin", "Jellyfin", ["jellyfin", "org.jellyfin"])
    ];

    /// <summary>
    /// Match launch target (exe + options) before DisplayName so a wrong title
    /// cannot steal another Flatpak app's id (e.g. Firefox named "Stremio").
    /// </summary>
    public static bool TryMatch(string title, string exe, string options, out Entry entry)
    {
        var launch = (exe + " " + options).Replace('\\', '/');
        foreach (var item in Catalog)
        {
            if (item.Needles.Any(n => TokenHit(launch, n)))
            {
                entry = item;
                return true;
            }
        }

        var name = (title ?? "").Replace('\\', '/');
        foreach (var item in Catalog)
        {
            if (item.Needles.Any(n => TokenHit(name, n)))
            {
                entry = item;
                return true;
            }
        }

        entry = Catalog[0];
        return false;
    }

    public static Entry? ById(string? id) =>
        Catalog.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public static int LaunchRank(string exe, string options)
    {
        var hay = (exe + " " + options).Replace('\\', '/').ToLowerInvariant();
        if (hay.Contains("flatpak", StringComparison.Ordinal)) return 0;
        if (hay.Contains("/usr/bin/", StringComparison.Ordinal)) return 1;
        return 2;
    }

    private static bool TokenHit(string hay, string needle)
    {
        if (string.IsNullOrEmpty(hay) || string.IsNullOrEmpty(needle)) return false;
        var start = 0;
        while (start < hay.Length)
        {
            var i = hay.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            var before = i == 0 || !char.IsLetterOrDigit(hay[i - 1]);
            var after = i + needle.Length >= hay.Length || !char.IsLetterOrDigit(hay[i + needle.Length]);
            if (before && after) return true;
            start = i + 1;
        }

        return false;
    }
}
