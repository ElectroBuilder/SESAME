using System.IO;
using System.Text.Json;
using Sesame.Models;
using Sesame.Services;

namespace Sesame.Services.GameOptimizer;

public sealed class OptimizerCacheFile
{
    public string Key { get; set; } = "";
    public string Host { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    public List<OptimizerCacheEntry> Games { get; set; } = new();
}

public sealed class OptimizerCacheEntry
{
    public bool Selected { get; set; } = true;
    public string DisplayName { get; set; } = "";
    public string FileName { get; set; } = "";
    public string RomPath { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string SystemId { get; set; } = "";
    public string SystemName { get; set; } = "";
    public string Status { get; set; } = "";
    public string ArtworkSource { get; set; } = "—";
    public string EmulatorName { get; set; } = "—";
    public string Target { get; set; } = "";
    public string StartDir { get; set; } = "";
    public string LaunchOptions { get; set; } = "";
    public string Category { get; set; } = "";
    public int Fps { get; set; }
    public string? CoverUrl { get; set; }
    public uint SteamAppId { get; set; }
    public bool InSteam { get; set; }
    public bool HasArtwork { get; set; }
    public string Note { get; set; } = "";
    public string SearchQuery { get; set; } = "";
    public string CorePath { get; set; } = "";
    public bool IsRetroArch { get; set; }
    public string RetroArchCoreName { get; set; } = "";
    public bool IsRomHack { get; set; }
    public bool IsTranslation { get; set; }
    public bool LaunchLocked { get; set; }
    public int? SteamGridDbId { get; set; }
    public string? SelectedGridUrl { get; set; }
    public string? SelectedWideUrl { get; set; }
    public string? SelectedHeroUrl { get; set; }
    public string? SelectedLogoUrl { get; set; }
    public string? SelectedIconUrl { get; set; }
    public string ShortcutKind { get; set; } = "Rom";
    public bool IsManual { get; set; }
    public string ManualId { get; set; } = "";
    public string ChosenLaunch { get; set; } = "";
    public List<OptimizerCacheLaunch> LaunchChoices { get; set; } = new();
}

public sealed class OptimizerCacheLaunch
{
    public string Exe { get; set; } = "";
    public string StartDir { get; set; } = "";
    public string Options { get; set; } = "";
    public string RomPath { get; set; } = "";
}

public static class OptimizerLibraryCache
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static IReadOnlyList<OptimizerGame> Load(string? key, string? host = null)
    {
        if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(host))
            return [];
        lock (Gate)
        {
            try
            {
                var file = Read(key) ?? ReadLegacy(host);
                if (file?.Games is null || file.Games.Count == 0) return [];
                if (!Matches(file, key, host)) return [];
                return ExtraShortcuts.Sanitize(file.Games.Select(ToGame)).ToList();
            }
            catch
            {
                return [];
            }
        }
    }

    public static void Save(string? key, string? host, IEnumerable<OptimizerGame> games)
    {
        if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(host))
            return;
        lock (Gate)
        {
            try
            {
                var path = FilePath(key, host);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var file = new OptimizerCacheFile
                {
                    Key = key ?? "",
                    Host = host ?? "",
                    SavedUtc = DateTime.UtcNow,
                    Games = games.Select(FromGame).ToList()
                };
                File.WriteAllText(path, JsonSerializer.Serialize(file, Json));
                AppDataPaths.RestrictFile(path);
            }
            catch
            {
                /* cache is optioneel */
            }
        }
    }

    private static bool Matches(OptimizerCacheFile file, string? key, string? host)
    {
        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(file.Key))
            return string.Equals(file.Key, key, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(file.Host))
            return string.Equals(file.Host, host, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static OptimizerCacheFile? Read(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var path = FilePath(key, null);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<OptimizerCacheFile>(File.ReadAllText(path), Json);
    }

    private static OptimizerCacheFile? ReadLegacy(string? host)
    {
        var path = AppDataPaths.Combine("optimizer-cache.json");
        if (!File.Exists(path)) return null;
        var file = JsonSerializer.Deserialize<OptimizerCacheFile>(File.ReadAllText(path), Json);
        if (file is null) return null;
        if (string.IsNullOrWhiteSpace(host) ||
            !string.Equals(file.Host, host, StringComparison.OrdinalIgnoreCase))
            return null;
        return file;
    }

    private static OptimizerCacheEntry FromGame(OptimizerGame game) => new()
    {
        Selected = game.Selected,
        DisplayName = game.DisplayName,
        FileName = game.FileName,
        RomPath = game.RomPath,
        FolderName = game.FolderName,
        SystemId = game.SystemId,
        SystemName = game.SystemName,
        Status = game.Status,
        ArtworkSource = game.ArtworkSource,
        EmulatorName = game.EmulatorName,
        Target = game.Target,
        StartDir = game.StartDir,
        LaunchOptions = game.LaunchOptions,
        Category = game.Category,
        Fps = game.Fps,
        CoverUrl = game.CoverUrl,
        SteamAppId = game.SteamAppId,
        InSteam = game.InSteam,
        HasArtwork = game.HasArtwork,
        Note = game.Note,
        SearchQuery = game.SearchQuery,
        CorePath = game.CorePath,
        IsRetroArch = game.IsRetroArch,
        RetroArchCoreName = game.RetroArchCoreName,
        IsRomHack = game.IsRomHack,
        IsTranslation = game.IsTranslation,
        LaunchLocked = game.LaunchLocked,
        SteamGridDbId = game.SteamGridDbId,
        SelectedGridUrl = game.SelectedGridUrl,
        SelectedWideUrl = game.SelectedWideUrl,
        SelectedHeroUrl = game.SelectedHeroUrl,
        SelectedLogoUrl = game.SelectedLogoUrl,
        SelectedIconUrl = game.SelectedIconUrl,
        ShortcutKind = game.ShortcutKind.ToString(),
        IsManual = game.IsManual,
        ManualId = game.ManualId,
        ChosenLaunch = game.ChosenLaunch,
        LaunchChoices = game.LaunchChoices.Select(c => new OptimizerCacheLaunch
        {
            Exe = c.Exe,
            StartDir = c.StartDir,
            Options = c.Options,
            RomPath = c.RomPath
        }).ToList()
    };

    private static OptimizerGame ToGame(OptimizerCacheEntry e)
    {
        var game = new OptimizerGame
        {
            Selected = e.Selected,
            DisplayName = e.DisplayName,
            FileName = e.FileName,
            RomPath = e.RomPath,
            FolderName = e.FolderName,
            SystemId = e.SystemId,
            SystemName = e.SystemName,
            Status = e.Status,
            ArtworkSource = e.ArtworkSource,
            EmulatorName = e.EmulatorName,
            Target = e.Target,
            StartDir = e.StartDir,
            LaunchOptions = e.LaunchOptions,
            Category = e.Category,
            Fps = e.Fps,
            CoverUrl = e.CoverUrl,
            SteamAppId = e.SteamAppId,
            InSteam = e.InSteam,
            HasArtwork = e.HasArtwork,
            Note = e.Note,
            SearchQuery = e.SearchQuery,
            CorePath = e.CorePath,
            IsRetroArch = e.IsRetroArch,
            RetroArchCoreName = e.RetroArchCoreName,
            IsRomHack = e.IsRomHack,
            IsTranslation = e.IsTranslation,
            LaunchLocked = e.LaunchLocked,
            SteamGridDbId = e.SteamGridDbId,
            SelectedGridUrl = e.SelectedGridUrl,
            SelectedWideUrl = e.SelectedWideUrl,
            SelectedHeroUrl = e.SelectedHeroUrl,
            SelectedLogoUrl = e.SelectedLogoUrl,
            SelectedIconUrl = e.SelectedIconUrl,
            ShortcutKind = Enum.TryParse<ShortcutKind>(e.ShortcutKind, true, out var kind)
                ? kind
                : ShortcutKind.Rom,
            IsManual = e.IsManual,
            ManualId = e.ManualId ?? "",
            ChosenLaunch = e.ChosenLaunch ?? "",
            LaunchChoices = (e.LaunchChoices ?? []).Select(c => new LaunchChoice
            {
                Exe = c.Exe,
                StartDir = c.StartDir,
                Options = c.Options,
                RomPath = c.RomPath
            }).ToList()
        };
        OptimizerPicks.Apply(game);
        return game;
    }

    private static string FilePath(string? key, string? host)
    {
        var name = AppDataPaths.SafeFileName(
            !string.IsNullOrWhiteSpace(key) ? key! :
            !string.IsNullOrWhiteSpace(host) ? host! : "default");
        return AppDataPaths.Combine("optimizer-cache", name + ".json");
    }
}
