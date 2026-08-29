using System.Text.RegularExpressions;
using Sesame.Models;

namespace Sesame.Services.GameOptimizer;

public static class SteamTabGrouping
{
    public const string IdPrefix = "sesame-";
    public const string LegacyPrefix = "vssh-";
    public const string SrmPrefix = "from-tag-";
    public const string EmulationName = "Emulation";

    public static string TabName(OptimizerGame game)
    {
        if (game.ShortcutKind == ShortcutKind.Hydra) return "Hydra";
        if (game.ShortcutKind == ShortcutKind.Game)
            return string.IsNullOrWhiteSpace(game.SystemName) ? "Games" : game.SystemName.Trim();
        if (game.ShortcutKind == ShortcutKind.App) return "Apps";
        return OptimizerSettings.SteamTabScheme switch
        {
            SteamTabScheme.Emulation => EmulationName,
            SteamTabScheme.Brand => BrandName(game),
            _ => PlatformName(game)
        };
    }

    public static string TabId(string tabName)
    {
        var slug = Regex.Replace((tabName ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "overig";
        return IdPrefix + slug;
    }

    public static string PlatformName(OptimizerGame game)
    {
        var profile = Resolve(game);
        if (profile is not null)
            return SteamName(profile);
        if (!string.IsNullOrWhiteSpace(game.SystemName))
            return game.SystemName.Trim();
        if (!string.IsNullOrWhiteSpace(game.FolderName))
            return game.FolderName.Trim();
        return "Overig";
    }

    public static string BrandName(OptimizerGame game)
    {
        var profile = Resolve(game);
        var id = (profile?.Id ?? game.SystemId ?? "").Trim().ToLowerInvariant();
        return id switch
        {
            "nes" or "snes" or "n64" or "gc" or "wii" or "wiiu" or "switch"
                or "gb" or "gbc" or "gba" or "nds" or "3ds" => "Nintendo",
            "ps1" or "ps2" or "psp" or "vita" or "ps3" => "PlayStation",
            "genesis" or "sms" or "saturn" or "dc" => "Sega",
            "arcade" => "Arcade",
            "xbox" => "Xbox",
            _ => BrandFromText(profile?.Category ?? game.Category, PlatformName(game))
        };
    }

    public static bool IsManagedId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase))
            return true;
        return OptimizerSettings.SteamTabIds.Any(x =>
            string.Equals(x, id, StringComparison.OrdinalIgnoreCase) &&
            x.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyCollection<string> KnownTabNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            EmulationName, "Nintendo", "PlayStation", "Sega", "Arcade", "Xbox", "Overig",
            "Hydra", "Lutris", "Games", "Apps", "PSX", "PS2", "GameCube", "Sega Genesis"
        };
        foreach (var profile in SystemCatalog.All)
            names.Add(SteamName(profile));
        return names;
    }

    private static string BrandFromText(string? category, string fallback)
    {
        var c = (category ?? "").ToLowerInvariant();
        if (c.Contains("nintendo")) return "Nintendo";
        if (c.Contains("sony") || c.Contains("playstation")) return "PlayStation";
        if (c.Contains("sega")) return "Sega";
        if (c.Contains("xbox") || c.Contains("microsoft")) return "Xbox";
        if (c.Contains("arcade") || c.Contains("mame")) return "Arcade";
        if (c.Contains("atari")) return "Atari";
        if (c.Contains("snk") || c.Contains("neo geo") || c.Contains("neogeo")) return "SNK";
        return string.IsNullOrWhiteSpace(fallback) ? "Overig" : fallback;
    }

    public static string SteamName(SystemProfile profile) => profile.Id switch
    {
        "genesis" => "Sega Genesis",
        "sms" => "Sega Master System",
        "saturn" => "Sega Saturn",
        "dc" => "Dreamcast",
        "ps1" => "PSX",
        "ps2" => "PS2",
        "psp" => "PSP",
        "gc" => "GameCube",
        _ => string.IsNullOrWhiteSpace(profile.Name) ? profile.Id : profile.Name.Trim()
    };

    private static SystemProfile? Resolve(OptimizerGame game) =>
        SystemCatalog.FromFolder(game.FolderName) ??
        SystemCatalog.All.FirstOrDefault(p =>
            p.Id.Equals(game.SystemId, StringComparison.OrdinalIgnoreCase));
}
