using System.IO;
using System.Text.RegularExpressions;
using Sesame.Models;

namespace Sesame.Services;

public enum PackActivationState { Active, Staged, Unsupported, RequiresLayoutValidation }
public enum DiscPackCapability { None, Texture, Mod }

public sealed record PackRoutePlan(
    string System,
    string Destination,
    PackActivationState State,
    string Message,
    string? EmulatorId = null,
    PlatformId? GameId = null,
    DiscPackCapability Capability = DiscPackCapability.None,
    string? PreparedPayload = null);

public static class DiscPackRouting
{
    private static readonly Regex Pcsx2Cheat = new("^[0-9A-F]{8}\\.pnach$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsDiscSystem(string? system) =>
        StoreGame.FoldSystem(system ?? "") is "wii" or "gc" or "ps1" or "ps2";

    public static PlatformId? ResolveGameId(string system, GameEntry? selectedLibraryGame,
        StoreGame? selectedStoreGame, PackHit hit, AppCatalog catalog)
    {
        if (Matches(system, selectedLibraryGame?.GameId)) return selectedLibraryGame!.GameId;
        if (Matches(system, selectedStoreGame?.GameId)) return selectedStoreGame!.GameId;
        if (selectedStoreGame is { IsAll: false })
        {
            var known = catalog.StoreGames.FirstOrDefault(game =>
                game.MatchesSystem(system) && game.SameIdentity(selectedStoreGame));
            if (Matches(system, known?.GameId)) return known!.GameId;
        }
        if (Matches(system, hit.GameId)) return hit.GameId;
        return null;
    }

    public static PackRoutePlan Plan(PackHit hit, string system, PlatformId? gameId)
    {
        var folded = StoreGame.FoldSystem(system);
        var packName = SafePackName(hit.Title);
        if (folded is not ("wii" or "gc" or "ps1" or "ps2"))
            throw new ArgumentOutOfRangeException(nameof(system), system, "Not a supported disc system.");

        if (hit.Section == "Saves")
            return new PackRoutePlan(folded, "", PackActivationState.Unsupported,
                "Save installation for Wii, GameCube, PS1 and PS2 requires a format-specific importer and is not supported yet.");

        var emulator = folded is "wii" or "gc" ? "dolphin" : folded == "ps1" ? "duckstation" : "pcsx2";
        if (hit.Section == "Texture packs")
        {
            if (gameId is { } id)
                return new PackRoutePlan(folded, EmulatorPaths.TextureDestination(emulator, id),
                    PackActivationState.RequiresLayoutValidation,
                    "Texture pack layout will be validated before activation.", emulator, id,
                    DiscPackCapability.Texture);
            return Staged(folded, EmulatorPaths.TexturesRoot(emulator), packName, emulator,
                "No validated game ID is known. The texture pack is staged under _incoming and is not active.");
        }

        if (hit.Section == "Mods")
            return new PackRoutePlan(folded, EmulatorPaths.ModsRoot(emulator),
                PackActivationState.RequiresLayoutValidation,
                "The emulator-specific mod layout will be validated before activation.", emulator, gameId,
                DiscPackCapability.Mod);

        return Staged(folded, EmulatorPaths.ModsRoot(emulator), packName, emulator,
            $"{hit.Section} is not a recognized active layout for {system}; it is staged for manual review.");
    }

    public static PackRoutePlan ValidatePreparedLayout(PackRoutePlan route, string preparedPath, string packTitle)
    {
        if (route.State != PackActivationState.RequiresLayoutValidation) return route;
        if (!Directory.Exists(preparedPath) && !File.Exists(preparedPath))
            throw new FileNotFoundException("Prepared pack is missing.", preparedPath);

        var files = EnumerateFiles(preparedPath).ToList();
        var folded = StoreGame.FoldSystem(route.System);
        if (route.Capability == DiscPackCapability.Texture && folded is "wii" or "gc")
        {
            var payload = TexturePayload(preparedPath, files, route.GameId, folded);
            if (payload is not null)
                return route with { State = PackActivationState.Active,
                    Message = "Recognized texture payload; the pack will be active.", PreparedPayload = payload };
        }
        else if (route.Capability == DiscPackCapability.Texture && folded == "ps1")
        {
            var payload = ReplacementTexturePayload(preparedPath, "ps1", route.GameId);
            if (payload is not null)
                return route with { State = PackActivationState.Active,
                    Message = "Recognized DuckStation SERIAL/replacements layout; the textures will be active.",
                    PreparedPayload = payload };
        }
        else if (route.Capability == DiscPackCapability.Texture && folded == "ps2")
        {
            var payload = ReplacementTexturePayload(preparedPath, "ps2", route.GameId);
            if (payload is not null)
                return route with { State = PackActivationState.Active,
                    Message = "Recognized PCSX2 SERIAL/replacements layout; the textures will be active.",
                    PreparedPayload = payload };
        }
        else if (route.Capability != DiscPackCapability.Mod)
        {
            return Staged(folded, route.Destination, SafePackName(packTitle), route.EmulatorId ?? "",
                "The pack does not have a supported emulator capability and is staged for review.", route.GameId);
        }

        if (folded is "wii" or "gc")
        {
            var payload = DolphinGraphicModPayload(preparedPath, files);
            if (payload is not null)
                return route with { State = PackActivationState.Active,
                    Destination = DeckClient.Combine(route.Destination, SafePackName(packTitle)),
                    Message = "Recognized Dolphin Graphic Mod layout; the pack will be active.",
                    PreparedPayload = payload };
        }
        else if (folded == "ps1")
        {
            var payload = SingleParentCheatPayload(preparedPath, files, ".cht", path =>
                PlatformId.TryCreate("ps1", Path.GetFileNameWithoutExtension(path), out _));
            if (payload is not null)
                return route with { State = PackActivationState.Active,
                    Message = "Recognized DuckStation .cht layout; the cheats will be active.",
                    PreparedPayload = payload };
        }
        else if (folded == "ps2")
        {
            var payload = SingleParentCheatPayload(preparedPath, files, ".pnach", path =>
                Pcsx2Cheat.IsMatch(Path.GetFileName(path)));
            if (payload is not null)
                return route with { State = PackActivationState.Active,
                    Message = "Recognized PCSX2 .pnach layout; the cheats will be active.",
                    PreparedPayload = payload };
        }

        return Staged(folded, route.Destination, SafePackName(packTitle), route.EmulatorId ?? "",
            "The archive does not have a recognized emulator layout. It is staged under _incoming and is not active.",
            route.GameId);
    }

    public static string SafePackName(string? value)
    {
        var safe = Regex.Replace((value ?? "").Trim(), @"[^A-Za-z0-9._ -]+", "-");
        safe = safe.Trim(' ', '.', '-');
        if (safe.Length > 80) safe = safe[..80].TrimEnd(' ', '.', '-');
        return safe.Length == 0 ? "pack" : safe;
    }

    private static bool Matches(string system, PlatformId? id) =>
        id is { } value && StoreGame.FoldSystem(system) == value.System;

    private static PackRoutePlan Staged(string system, string root, string packName, string emulator,
        string message, PlatformId? id = null) =>
        new(system, DeckClient.Combine(DeckClient.Combine(root, "_incoming"), packName),
            PackActivationState.Staged, message, emulator, id);

    private static IEnumerable<string> EnumerateFiles(string path) =>
        File.Exists(path) ? [path] : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);

    private static string? DolphinGraphicModPayload(string root, IReadOnlyList<string> files)
    {
        if (files.Count == 0 || !Directory.Exists(root)) return null;
        var valid = files.Where(path => Path.GetFileName(path).Equals("metadata.json", StringComparison.OrdinalIgnoreCase))
            .Select(manifest =>
            {
                var directory = Path.GetDirectoryName(manifest)!;
                return (Directory.Exists(Path.Combine(directory, "assets")) ||
                        Directory.Exists(Path.Combine(directory, "codes")) ||
                        Directory.Exists(Path.Combine(directory, "config")) ||
                        Directory.Exists(Path.Combine(directory, "riivolution")) ||
                        Directory.Exists(Path.Combine(directory, "textures"))) ? directory : null;
            }).Where(path => path is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return valid.Count == 1 ? valid[0] : null;
    }

    private static string? ReplacementTexturePayload(string root, string system, PlatformId? selectedId)
    {
        if (!Directory.Exists(root) || selectedId is not { } trusted || trusted.System != system) return null;
        var replacements = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Equals("replacements", StringComparison.OrdinalIgnoreCase))
            .Where(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any(IsTextureFile))
            .ToList();
        if (replacements.Count != 1) return null;
        var payload = Path.GetDirectoryName(replacements[0])!;
        var leaf = Path.GetFileName(payload);
        if (system == "ps2" && Regex.IsMatch(leaf, "_[0-9A-F]{8}$", RegexOptions.IgnoreCase) &&
            PlatformId.TryCreate("ps2", leaf[..^9], out _))
            return null;
        // A wrapper named like a serial is layout evidence only. It may never select or override the target ID.
        if (PlatformId.TryCreate(system, leaf, out var archiveId) && archiveId != trusted)
            return null;
        return payload;
    }

    private static string? TexturePayload(string preparedPath, IReadOnlyList<string> files, PlatformId? id,
        string system)
    {
        if (files.Count == 0 || !files.Any(IsTextureFile)) return null;
        if (File.Exists(preparedPath)) return preparedPath;
        if (id is { } gameId)
        {
            var directories = Directory.EnumerateDirectories(preparedPath, "*", SearchOption.AllDirectories)
                .Prepend(preparedPath)
                .ToList();
            var candidates = directories
                .Where(path => Path.GetFileName(path).Equals(gameId.Value, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 1) return candidates[0];
            if (directories.Any(path =>
                    PlatformId.TryCreate(system, Path.GetFileName(path), out var archiveId) && archiveId != gameId))
                return null;
        }
        var entries = Directory.GetFileSystemEntries(preparedPath);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : preparedPath;
    }

    private static bool IsTextureFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".dds" or ".bmp" or ".tga" or ".jpg" or ".jpeg";

    private static string? SingleParentCheatPayload(string preparedPath, IReadOnlyList<string> files, string extension,
        Func<string, bool> hasValidName)
    {
        if (files.Count == 0 || !files.All(path => Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)) ||
            !files.All(hasValidName)) return null;
        if (File.Exists(preparedPath)) return preparedPath;
        var parents = files.Select(path => Path.GetDirectoryName(path) ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return parents.Count == 1 ? parents[0] : null;
    }
}
