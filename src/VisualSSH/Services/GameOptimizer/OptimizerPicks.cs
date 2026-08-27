using System.IO;
using System.Text.Json;
using VisualSSH.Models;
using VisualSSH.Services;

namespace VisualSSH.Services.GameOptimizer;

public sealed class OptimizerPick
{
    public string Host { get; set; } = "";
    public string RomPath { get; set; } = "";
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
        if (!string.IsNullOrWhiteSpace(pick.DisplayName))
            game.DisplayName = pick.DisplayName;
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

    public static void Remember(OptimizerGame game)
    {
        if (string.IsNullOrWhiteSpace(game.RomPath)) return;
        EnsureLoaded();
        var pick = new OptimizerPick
        {
            Host = CurrentKey,
            RomPath = game.RomPath,
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
        _map[Key(game.RomPath)] = pick;
        Save();
    }

    private static bool TryFind(OptimizerGame game, out OptimizerPick? pick)
    {
        pick = null;
        if (_map.TryGetValue(Key(game.RomPath), out var byPath))
        {
            pick = byPath;
            return true;
        }
        return _map.TryGetValue(Key(game.SystemId + "|" + game.FileName), out pick);
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
            _map = list
                .Where(p => !string.IsNullOrWhiteSpace(p.RomPath))
                .GroupBy(p => Key(p.RomPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _map = new Dictionary<string, OptimizerPick>(StringComparer.OrdinalIgnoreCase);
        }
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
