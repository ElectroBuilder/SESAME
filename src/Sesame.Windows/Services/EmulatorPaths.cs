using System.Text.RegularExpressions;
using Sesame.Models;

namespace Sesame.Services;

public sealed class EmulatorPathOverrides
{
    public string? UserRoot { get; set; }
    public string? NandRoot { get; set; }
    public string? ModsRoot { get; set; }
    public string? TexturesRoot { get; set; }
    public string? SavesRoot { get; set; }

    internal EmulatorPathOverrides Normalized() => new()
    {
        UserRoot = EmulatorPaths.NormalizeOverride(UserRoot),
        NandRoot = EmulatorPaths.NormalizeOverride(NandRoot),
        ModsRoot = EmulatorPaths.NormalizeOverride(ModsRoot),
        TexturesRoot = EmulatorPaths.NormalizeOverride(TexturesRoot),
        SavesRoot = EmulatorPaths.NormalizeOverride(SavesRoot)
    };
}

/// <summary>Stateless resolver over the overrides owned and persisted by LibraryPaths.</summary>
public static class EmulatorPaths
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "dolphin", "duckstation", "pcsx2", "eden", "yuzu", "ryujinx", "citron"
    };

    public static bool IsKnownEmulator(string? emulator) =>
        !string.IsNullOrWhiteSpace(emulator) && Known.Contains(emulator.Trim());

    public static EmulatorPathOverrides Overrides(string emulator)
    {
        var id = NormalizeEmulator(emulator);
        if (!LibraryPaths.Current.EmulatorOverrides.TryGetValue(id, out var value) || value is null)
        {
            value = new EmulatorPathOverrides();
            LibraryPaths.Current.EmulatorOverrides[id] = value;
        }
        return value;
    }

    public static void ResetOverrides(string emulator) =>
        LibraryPaths.Current.EmulatorOverrides.Remove(NormalizeEmulator(emulator));

    public static string UserRoot(string emulator)
    {
        var id = NormalizeEmulator(emulator);
        var value = GetOverride(id, static x => x.UserRoot);
        if (value.Length > 0) return value;
        return id switch
        {
            "dolphin" => Join(LibraryPaths.Current.StorageRoot, "dolphin-emu"),
            "duckstation" => Join(LibraryPaths.Current.StorageRoot, "duckstation"),
            "pcsx2" => Join(LibraryPaths.Current.StorageRoot, "pcsx2"),
            _ => Join(LibraryPaths.Current.StorageRoot, id)
        };
    }

    public static string TexturesRoot(string emulator)
    {
        var id = NormalizeEmulator(emulator);
        var value = GetOverride(id, static x => x.TexturesRoot);
        if (value.Length > 0) return value;
        return id switch
        {
            "dolphin" => Join(UserRoot(id), "Load", "Textures"),
            "duckstation" or "pcsx2" => Join(UserRoot(id), "textures"),
            _ => ModsRoot(id)
        };
    }

    public static string ModsRoot(string emulator)
    {
        var id = NormalizeEmulator(emulator);
        var value = GetOverride(id, static x => x.ModsRoot);
        if (value.Length > 0) return value;
        return id switch
        {
            "dolphin" => Join(UserRoot(id), "Load", "GraphicMods"),
            "duckstation" or "pcsx2" => Join(UserRoot(id), "cheats"),
            "eden" or "yuzu" or "ryujinx" or "citron" => LibraryPaths.Current.SwitchMods(id),
            _ => Join(UserRoot(id), "mods")
        };
    }

    public static string SavesRoot(string emulator, string? systemFolder = null)
    {
        var id = NormalizeEmulator(emulator);
        var value = GetOverride(id, static x => x.SavesRoot);
        if (value.Length > 0) return value;
        return id switch
        {
            "dolphin" => UserRoot(id),
            "duckstation" => Join(LibraryPaths.Current.SavesRoot, "psx", "duckstation"),
            "pcsx2" => Join(LibraryPaths.Current.SavesRoot, "ps2", "pcsx2"),
            "eden" or "yuzu" or "ryujinx" or "citron" => LibraryPaths.Current.SwitchSaves(id),
            _ => Join(LibraryPaths.Current.SavesRoot, (systemFolder ?? id).Trim().Trim('/'))
        };
    }

    public static string RomFolder(string systemFolder) =>
        LibraryPaths.Current.RomFolder(StoreGame.FoldSystem(systemFolder));

    public static string TextureDestination(string emulator, PlatformId? platformId) =>
        platformId is { } id ? Join(TexturesRoot(emulator), id.Value) : TexturesRoot(emulator);

    internal static string? NormalizeOverride(string? value)
    {
        var normalized = (value ?? "").Trim().Replace('\\', '/').TrimEnd('/');
        return normalized.Length == 0 ? null : normalized;
    }

    private static string GetOverride(string emulator, Func<EmulatorPathOverrides, string?> get)
    {
        if (!LibraryPaths.Current.EmulatorOverrides.TryGetValue(emulator, out var value) || value is null)
            return "";
        return NormalizeOverride(get(value)) ?? "";
    }

    private static string NormalizeEmulator(string emulator)
    {
        var id = (emulator ?? "").Trim().ToLowerInvariant();
        if (!Known.Contains(id))
            throw new ArgumentOutOfRangeException(nameof(emulator), emulator, "Unknown emulator id.");
        return id;
    }

    private static string Join(params string[] parts)
    {
        var path = NormalizeOverride(parts[0]) ?? "";
        for (var i = 1; i < parts.Length; i++)
            path = DeckClient.Combine(path, parts[i].Trim().Trim('/'));
        return path;
    }
}

/// <summary>A validated, system-specific disc identifier; never a Switch Title ID.</summary>
public readonly record struct PlatformId
{
    private static readonly Regex Dolphin = new("^[A-Z0-9]{6}$", RegexOptions.Compiled);
    private static readonly Regex PlayStation = new(
        "^(?<prefix>(?:S[CL]|SC|SL|PBP|PAPX|PCPX|PUPX|NP)[A-Z]{1,3})[-_ ]?(?<first>[0-9]{3})[._-]?(?<last>[0-9]{2})$",
        RegexOptions.Compiled);

    public string System { get; }
    public string Value { get; }

    private PlatformId(string system, string value)
    {
        System = system;
        Value = value;
    }

    public static bool TryCreate(string? system, string? candidate, out PlatformId id)
    {
        id = default;
        var folded = StoreGame.FoldSystem(system ?? "");
        var value = (candidate ?? "").Trim().ToUpperInvariant();
        var valid = folded switch
        {
            "wii" or "gc" => Dolphin.IsMatch(value),
            "ps1" or "ps2" => PlayStation.IsMatch(value),
            _ => false
        };
        if (!valid) return false;
        if (folded is "ps1" or "ps2")
        {
            var match = PlayStation.Match(value);
            value = $"{match.Groups["prefix"].Value}-{match.Groups["first"].Value}{match.Groups["last"].Value}";
        }
        id = new PlatformId(folded, value);
        return true;
    }

    public static bool TryExtractLibraryMetadata(string? system, string? fileName, out PlatformId id)
    {
        id = default;
        var folded = StoreGame.FoldSystem(system ?? "");
        var text = (fileName ?? "").ToUpperInvariant();
        var pattern = folded is "wii" or "gc"
            ? @"[\[\(]([A-Z0-9]{6})[\]\)]"
            : folded is "ps1" or "ps2"
                ? @"(?<![A-Z0-9])((?:S[CL]|SC|SL|PBP|PAPX|PCPX|PUPX|NP)[A-Z]{1,3}[-_ ]?[0-9]{3}[._-]?[0-9]{2})(?![A-Z0-9])"
                : "(?!)";
        var match = Regex.Match(text, pattern);
        return match.Success && TryCreate(folded, match.Groups[1].Value, out id);
    }

    public override string ToString() => Value;
}
