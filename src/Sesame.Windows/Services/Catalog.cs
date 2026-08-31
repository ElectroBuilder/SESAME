using System.IO;
using System.Text.Json;
using Sesame.Models;
using Sesame.Services.GameOptimizer;

namespace Sesame.Services;

public sealed class AppCatalog
{
    public IReadOnlyList<ConnectionProfile> Profiles { get; }
    public IReadOnlyList<QuickPath> QuickAccess { get; }
    public IReadOnlyDictionary<string, string> RomFolders { get; }
    public IReadOnlyDictionary<string, string> InstallRoutes { get; }
    public IReadOnlyDictionary<string, string> TitleIds { get; }
    public IReadOnlyDictionary<string, string> TextureRoots { get; }
    public IReadOnlyDictionary<string, string> TextureByGame { get; }
    public IReadOnlyDictionary<string, string> RetroarchSaves { get; }
    public IReadOnlyList<StoreGame> StoreGames { get; }
    private readonly string _edenMods;
    private readonly string _edenSaves;
    private readonly string _edenUsersRoot;
    private readonly string _edenProfiles;
    public string EdenMods => FirstPath(LibraryPaths.Current.PrimaryModsRoot, _edenMods);
    public string EdenSaves => FirstPath(LibraryPaths.Current.PrimarySavesRoot, _edenSaves);
    public string EdenUsersRoot
    {
        get
        {
            var saves = LibraryPaths.Current.PrimarySavesRoot;
            return string.IsNullOrEmpty(saves)
                ? _edenUsersRoot
                : DeckClient.Combine(saves, "0000000000000000");
        }
    }
    public string EdenProfiles => FirstPath(LibraryPaths.Current.SwitchProfiles(LibraryPaths.Current.PrimarySwitchId), _edenProfiles);

    public AppCatalog()
    {
        using var doc = OpenCatalog();
        var root = doc.RootElement;

        Profiles = root.TryGetProperty("profiles", out var sessionProfiles) && sessionProfiles.ValueKind == JsonValueKind.Array
            ? sessionProfiles.EnumerateArray().Select(p => new ConnectionProfile
            {
                Id = p.GetProperty("id").GetString() ?? "",
                Name = p.GetProperty("name").GetString() ?? "",
                Host = p.GetProperty("host").GetString() ?? "",
                Port = p.TryGetProperty("port", out var port) ? port.GetInt32() : 22,
                User = p.TryGetProperty("user", out var user) ? user.GetString() ?? "deck" : "deck"
            }).ToList()
            : [];

        QuickAccess = root.GetProperty("quickAccess").EnumerateArray()
            .Select(p => new QuickPath
            {
                Name = p.GetProperty("name").GetString() ?? "",
                Path = p.GetProperty("path").GetString() ?? "",
                Group = p.GetProperty("group").GetString() ?? ""
            }).ToList();

        RomFolders = ReadMap(root.GetProperty("romFolders"));
        InstallRoutes = ReadMap(root.GetProperty("installRoutes"));
        TitleIds = root.TryGetProperty("titleIds", out var ids) ? ReadMap(ids)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var eden = root.GetProperty("eden");
        _edenMods = eden.GetProperty("mods").GetString() ?? "";
        _edenSaves = eden.GetProperty("saves").GetString() ?? "";
        _edenUsersRoot = eden.TryGetProperty("usersRoot", out var users)
            ? users.GetString() ?? ""
            : DeckClient.Combine(_edenSaves, "0000000000000000");
        _edenProfiles = eden.TryGetProperty("profiles", out var profiles)
            ? profiles.GetString() ?? ""
            : "";

        TextureRoots = root.TryGetProperty("textureRoots", out var tr) ? ReadMap(tr) : new Dictionary<string, string>();
        TextureByGame = root.TryGetProperty("textureByGame", out var tg) ? ReadMap(tg) : new Dictionary<string, string>();
        RetroarchSaves = root.TryGetProperty("retroarchSaves", out var rs) ? ReadMap(rs) : new Dictionary<string, string>();
        StoreGames = root.TryGetProperty("storeGames", out var sg)
            ? sg.EnumerateArray().Select(ReadStoreGame).ToList()
            : new List<StoreGame>();
    }

    public string? RomFolderFor(string system)
    {
        var folded = StoreGame.FoldSystem(system);
        if (RomFolders.TryGetValue(folded, out var path) && !string.IsNullOrEmpty(path))
            return RelocateRomFolder(path, folded);
        if (RomFolders.TryGetValue(system, out path) && !string.IsNullOrEmpty(path))
            return RelocateRomFolder(path, system);
        var discovered = RomScan.Systems.FirstOrDefault(s =>
            s.SystemId.Equals(folded, StringComparison.OrdinalIgnoreCase) ||
            s.Key.Equals(system, StringComparison.OrdinalIgnoreCase) ||
            s.Key.Equals(folded, StringComparison.OrdinalIgnoreCase));
        if (discovered is not null) return discovered.Path;
        var profile = SystemCatalog.FromFolder(system);
        if (profile is null) return null;
        foreach (var folder in profile.Folders)
        {
            discovered = RomScan.Systems.FirstOrDefault(s =>
                s.Key.Equals(folder, StringComparison.OrdinalIgnoreCase));
            if (discovered is not null) return discovered.Path;
            if (RomFolders.TryGetValue(folder, out path) && !string.IsNullOrEmpty(path))
                return RelocateRomFolder(path, folder);
        }
        var name = profile.Folders.FirstOrDefault() ?? profile.Id;
        return LibraryPaths.Current.RomFolder(name);
    }

    private static string RelocateRomFolder(string catalogPath, string systemKey)
    {
        if (systemKey.Equals("hydra", StringComparison.OrdinalIgnoreCase) ||
            catalogPath.Contains("/Games/Hydra", StringComparison.OrdinalIgnoreCase))
            return LibraryPaths.Current.HydraRoot;
        var name = Path.GetFileName(catalogPath.Trim().TrimEnd('/'));
        if (string.IsNullOrEmpty(name)) name = systemKey;
        return LibraryPaths.Current.RomFolder(name);
    }

    private static string FirstPath(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    /// <summary>Relocates legacy catalog defaults through the one persisted LibraryPaths contract.</summary>
    public static string RelocateKnownPath(string path)
    {
        var normalized = (path ?? "").Trim().Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0) return normalized;
        if (normalized.Equals("/home/deck/Emulation", StringComparison.OrdinalIgnoreCase))
            return LibraryPaths.Current.EmulationRoot;
        const string emulation = "/home/deck/Emulation/";
        if (normalized.StartsWith(emulation, StringComparison.OrdinalIgnoreCase))
            return DeckClient.Combine(LibraryPaths.Current.EmulationRoot, normalized[emulation.Length..]);
        if (normalized.Equals("/home/deck/Hydra", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("/home/deck/Games/Hydra", StringComparison.OrdinalIgnoreCase))
            return LibraryPaths.Current.HydraRoot;
        if (normalized.Equals("/home/deck/Games/Lutris", StringComparison.OrdinalIgnoreCase))
            return LibraryPaths.Current.LutrisRoot;
        if (normalized.Equals("/home/deck/Games/Other", StringComparison.OrdinalIgnoreCase))
            return LibraryPaths.Current.OtherGamesRoot;
        return normalized;
    }

    public IReadOnlyList<QuickPath> EffectiveQuickAccess() => QuickAccess.Select(path => new QuickPath
    {
        Name = path.Name,
        Group = path.Group,
        Path = RelocateKnownPath(path.Path)
    }).ToList();

    public StoreGame ResolveStoreGame(string name, string system, string? titleId,
        bool isTranslation = false)
    {
        isTranslation = isTranslation || StoreGame.LooksLikeTranslation(name);
        var search = StoreGame.StripVariant(name);
        if (string.IsNullOrWhiteSpace(search))
            search = name;

        if (!string.IsNullOrEmpty(titleId))
        {
            var byId = StoreGames.FirstOrDefault(g =>
                string.Equals(g.TitleId, titleId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                var hit = byId.Clone();
                hit.TitleId = titleId;
                if (!string.IsNullOrWhiteSpace(name) && !isTranslation) hit.Name = byId.Name;
                return ApplyVariant(hit, byId.Name, isTranslation);
            }
        }

        var byName = StoreGames.FirstOrDefault(g =>
            g.MatchesSystem(system) && g.MatchesTitle(search));
        if (byName is not null)
        {
            var hit = byName.Clone();
            hit.TitleId ??= titleId;
            return ApplyVariant(hit, byName.Name, isTranslation);
        }

        return ApplyVariant(new StoreGame
        {
            Name = StoreGame.CleanTitle(search),
            System = system.ToUpperInvariant(),
            TitleId = titleId
        }, StoreGame.CleanTitle(search), isTranslation);
    }

    private static StoreGame ApplyVariant(StoreGame game, string baseName, bool translation)
    {
        if (!translation) return game;
        game.Variant = "NL";
        var stem = string.IsNullOrWhiteSpace(baseName) ? game.Name : baseName;
        stem = StoreGame.StripVariant(stem);
        if (!StoreGame.LooksLikeTranslation(game.Name))
            game.Name = stem + " (NL)";
        return game;
    }

    private static StoreGame ReadStoreGame(JsonElement el)
    {
        var system = el.TryGetProperty("system", out var s) ? s.GetString() ?? "" : "";
        var game = new StoreGame
        {
            Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            System = system,
            TitleId = el.TryGetProperty("titleId", out var t) ? t.GetString() : null
        };
        if (el.TryGetProperty("platformId", out var platform) &&
            PlatformId.TryCreate(system, platform.GetString(), out var gameId))
            game.GameId = gameId;
        if (el.TryGetProperty("gameBananaId", out var one) && one.ValueKind == JsonValueKind.Number)
            game.GameBananaIds.Add(one.GetInt32());
        if (el.TryGetProperty("gameBananaIds", out var many) && many.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in many.EnumerateArray())
                if (id.ValueKind == JsonValueKind.Number && !game.GameBananaIds.Contains(id.GetInt32()))
                    game.GameBananaIds.Add(id.GetInt32());
        }
        if (el.TryGetProperty("kingSlugs", out var slugs) && slugs.ValueKind == JsonValueKind.Array)
            game.KingSlugs.AddRange(slugs.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
        if (el.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
            game.Aliases.AddRange(aliases.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
        return game;
    }

    private static Dictionary<string, string> ReadMap(JsonElement el) =>
        el.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);

    private static JsonDocument OpenCatalog()
    {
        var asm = typeof(AppCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream("Sesame.catalog.json");
        if (stream is not null)
            return JsonDocument.Parse(stream);

        var path = Path.Combine(AppContext.BaseDirectory, "Data", "catalog.json");
        if (File.Exists(path))
            return JsonDocument.Parse(File.ReadAllText(path));

        throw new InvalidOperationException("The game catalog is missing from this SESAME build.");
    }
}
