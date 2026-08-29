using System.IO;
using System.Text.Json;
using Sesame.Models;
using Sesame.Services;

namespace Sesame.Services.GameOptimizer;

public sealed class OptimizerPick
{
    public string Host { get; set; } = "";
    public string RomPath { get; set; } = "";
    public string PickKey { get; set; } = "";
    public bool Selected { get; set; } = true;
    public string DisplayName { get; set; } = "";
    public string SearchQuery { get; set; } = "";
    public int? SteamGridDbId { get; set; }
    public string? SelectedGridUrl { get; set; }
    public string? SelectedWideUrl { get; set; }
    public string? SelectedHeroUrl { get; set; }
    public string? SelectedLogoUrl { get; set; }
    public string? SelectedIconUrl { get; set; }
    public string? ArtworkSource { get; set; }
    public bool LaunchLocked { get; set; }
    public string? Target { get; set; }
    public string? StartDir { get; set; }
    public string? LaunchOptions { get; set; }
}

public static class OptimizerPicks
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static Dictionary<string, OptimizerPick> _map = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static string CurrentKey { get; set; } = "";

    public static void Apply(OptimizerGame game)
    {
        EnsureLoaded();
        if (!TryFind(game, out var pick) || pick is null)
            return;
        game.Selected = pick.Selected;
        if (!string.IsNullOrWhiteSpace(pick.DisplayName) && AllowRename(game, pick.DisplayName))
            game.DisplayName = pick.DisplayName;
        if (AllowArtwork(game, pick))
        {
            if (!string.IsNullOrWhiteSpace(pick.SearchQuery))
                game.SearchQuery = pick.SearchQuery;
            if (pick.SteamGridDbId is int id)
                game.SteamGridDbId = id;
            game.SelectedGridUrl = pick.SelectedGridUrl ?? game.SelectedGridUrl;
            game.SelectedWideUrl = pick.SelectedWideUrl ?? game.SelectedWideUrl;
            game.SelectedHeroUrl = pick.SelectedHeroUrl ?? game.SelectedHeroUrl;
            game.SelectedLogoUrl = pick.SelectedLogoUrl ?? game.SelectedLogoUrl;
            game.SelectedIconUrl = pick.SelectedIconUrl ?? game.SelectedIconUrl;
            if (!string.IsNullOrWhiteSpace(pick.ArtworkSource))
                game.ArtworkSource = pick.ArtworkSource;
        }
        if (pick.LaunchLocked &&
            HostAllowsLaunch(pick) &&
            LaunchComposer.ShouldKeepLaunch(pick.Target, pick.LaunchOptions))
        {
            game.LaunchLocked = true;
            if (!string.IsNullOrWhiteSpace(pick.Target))
                game.Target = pick.Target;
            if (!string.IsNullOrWhiteSpace(pick.StartDir))
                game.StartDir = pick.StartDir;
            if (pick.LaunchOptions is not null)
                game.LaunchOptions = pick.LaunchOptions;
        }
    }

    public static void Remember(OptimizerGame game, bool save = true)
    {
        EnsureLoaded();
        var key = ExtraShortcuts.KeyOf(game);
        if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(game.RomPath))
            return;
        var pick = new OptimizerPick
        {
            Host = CurrentKey,
            RomPath = game.IsRom ? game.RomPath : "",
            PickKey = key,
            Selected = game.Selected,
            DisplayName = game.DisplayName,
            SearchQuery = game.SearchQuery,
            SteamGridDbId = game.SteamGridDbId,
            SelectedGridUrl = game.SelectedGridUrl,
            SelectedWideUrl = game.SelectedWideUrl,
            SelectedHeroUrl = game.SelectedHeroUrl,
            SelectedLogoUrl = game.SelectedLogoUrl,
            SelectedIconUrl = game.SelectedIconUrl,
            ArtworkSource = game.ArtworkSource,
            LaunchLocked = game.LaunchLocked,
            Target = game.Target,
            StartDir = game.StartDir,
            LaunchOptions = game.LaunchOptions
        };
        if (!string.IsNullOrWhiteSpace(key))
            _map[key] = pick;
        // Flatpak (and similar) apps share one exe path — never index non-ROMs by RomPath.
        if (game.IsRom && !string.IsNullOrWhiteSpace(game.RomPath))
            _map[Key(game.RomPath)] = pick;
        if (save) Save();
    }

    public static void RememberAll(IEnumerable<OptimizerGame> games)
    {
        foreach (var game in games)
            Remember(game, save: false);
        Save();
    }

    private static bool TryFind(OptimizerGame game, out OptimizerPick? pick)
    {
        pick = null;
        var key = ExtraShortcuts.KeyOf(game);
        if (!string.IsNullOrWhiteSpace(key) && _map.TryGetValue(key, out pick))
            return true;
        if (game.IsRom && _map.TryGetValue(Key(game.RomPath), out pick))
            return true;
        return game.IsRom && _map.TryGetValue(Key(game.SystemId + "|" + game.FileName), out pick);
    }

    /// <summary>
    /// Old picks indexed every Flatpak app under /usr/bin/flatpak, so Firefox/Kodi/Lutris
    /// inherited Stremio's saved name. Ignore a rename that points at a different known app.
    /// </summary>
    private static bool AllowRename(OptimizerGame game, string proposed)
    {
        if (game.ShortcutKind != ShortcutKind.App) return true;
        if (!DeckApps.TryMatch(game.DisplayName, game.RomPath, game.LaunchOptions, out var current))
            return true;
        if (!DeckApps.TryMatch(proposed, proposed, "", out var fromPick))
            return true;
        return string.Equals(current.Id, fromPick.Id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Block re-applying Stremio (or other) artwork onto a different catalog app.
    /// </summary>
    private static bool AllowArtwork(OptimizerGame game, OptimizerPick pick)
    {
        if (game.ShortcutKind != ShortcutKind.App) return true;
        if (!DeckApps.TryMatch(game.DisplayName, game.RomPath, game.LaunchOptions, out var current))
            return true;
        foreach (var proposed in new[] { pick.DisplayName, pick.SearchQuery })
        {
            if (string.IsNullOrWhiteSpace(proposed)) continue;
            if (!DeckApps.TryMatch(proposed, proposed, "", out var fromPick))
                continue;
            if (!string.Equals(current.Id, fromPick.Id, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool HostAllowsLaunch(OptimizerPick pick) =>
        string.IsNullOrWhiteSpace(pick.Host) ||
        string.IsNullOrWhiteSpace(CurrentKey) ||
        string.Equals(pick.Host, CurrentKey, StringComparison.OrdinalIgnoreCase);

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var path = FilePath();
            if (!File.Exists(path)) return;
            AppDataPaths.RestrictFile(path);
            var list = JsonSerializer.Deserialize<List<OptimizerPick>>(File.ReadAllText(path), Json);
            if (list is null) return;
            var dirty = false;
            foreach (var pick in list)
            {
                if (ScrubContaminatedAppPick(pick))
                    dirty = true;
            }

            _map = list
                .Where(p => !string.IsNullOrWhiteSpace(p.RomPath) || !string.IsNullOrWhiteSpace(p.PickKey))
                .SelectMany(p =>
                {
                    var entries = new List<KeyValuePair<string, OptimizerPick>>();
                    if (!string.IsNullOrWhiteSpace(p.PickKey))
                        entries.Add(new(p.PickKey, p));
                    // Never index Flatpak apps by shared /usr/bin/flatpak (or any app| pick).
                    if (ShouldIndexRomPath(p))
                        entries.Add(new(Key(p.RomPath), p));
                    return entries;
                })
                .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
            if (dirty)
                Save();
        }
        catch
        {
            _map = new Dictionary<string, OptimizerPick>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool ShouldIndexRomPath(OptimizerPick pick)
    {
        if (string.IsNullOrWhiteSpace(pick.RomPath)) return false;
        if (IsAppPickKey(pick.PickKey)) return false;
        var path = pick.RomPath.Replace('\\', '/');
        if (path.Contains("/usr/bin/flatpak", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/flatpak", StringComparison.OrdinalIgnoreCase))
            return false;
        return LooksLikeRomPath(path);
    }

    private static bool LooksLikeRomPath(string path)
    {
        var leaf = Path.GetFileName(path);
        if (string.IsNullOrEmpty(leaf)) return false;
        var ext = Path.GetExtension(leaf);
        if (string.IsNullOrEmpty(ext)) return false;
        return ext.ToLowerInvariant() is
            ".iso" or ".wbfs" or ".rvz" or ".nso" or ".xci" or ".nsp" or ".nsz" or
            ".nes" or ".sfc" or ".smc" or ".n64" or ".z64" or ".v64" or ".gb" or ".gbc" or ".gba" or
            ".nds" or ".3ds" or ".cia" or ".cue" or ".chd" or ".pbp" or ".cso" or ".bin" or
            ".zip" or ".7z" or ".rar" or ".gcm" or ".gcz" or ".wad" or ".dol" or ".elf";
    }

    private static bool IsAppPickKey(string? key) =>
        (key ?? "").StartsWith("app|", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Clear Stremio (or other) artwork that was saved under a different app|id pick.
    /// </summary>
    private static bool ScrubContaminatedAppPick(OptimizerPick pick)
    {
        var key = pick.PickKey ?? "";
        if (!IsAppPickKey(key)) return false;
        var id = key["app|".Length..];
        var catalog = DeckApps.ById(id);
        if (catalog is null) return false;

        var changed = false;
        if (!string.IsNullOrWhiteSpace(pick.RomPath))
        {
            pick.RomPath = "";
            changed = true;
        }

        if (!PickTextMatchesOtherApp(pick.DisplayName, catalog.Id) &&
            !PickTextMatchesOtherApp(pick.SearchQuery, catalog.Id))
            return changed;

        pick.SelectedGridUrl = null;
        pick.SelectedWideUrl = null;
        pick.SelectedHeroUrl = null;
        pick.SelectedLogoUrl = null;
        pick.SelectedIconUrl = null;
        pick.SteamGridDbId = null;
        pick.ArtworkSource = null;
        pick.SearchQuery = catalog.Title;
        pick.DisplayName = catalog.Title;
        return true;
    }

    private static bool PickTextMatchesOtherApp(string? text, string catalogId)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!DeckApps.TryMatch(text, text, "", out var hit)) return false;
        return !string.Equals(hit.Id, catalogId, StringComparison.OrdinalIgnoreCase);
    }

    private static void Save()
    {
        try
        {
            var path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_map.Values.ToList(), Json));
            AppDataPaths.RestrictFile(path);
        }
        catch
        {
            /* keuze blijft in geheugen */
        }
    }

    private static string Key(string romPath) => (romPath ?? "").Trim().Replace('\\', '/');

    private static string FilePath() => AppDataPaths.Combine("optimizer-picks.json");
}
