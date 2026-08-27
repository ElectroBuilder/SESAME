using System.IO;
using System.Text.Json;
using VisualSSH.Models;
using VisualSSH.Services.GameOptimizer;

namespace VisualSSH.Services;

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
    public string EdenMods { get; }
    public string EdenSaves { get; }
    public string EdenUsersRoot { get; }
    public string EdenProfiles { get; }

    public AppCatalog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "catalog.json");
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        Profiles = root.GetProperty("profiles").EnumerateArray()
            .Select(p => new ConnectionProfile
            {
                Id = p.GetProperty("id").GetString() ?? "",
                Name = p.GetProperty("name").GetString() ?? "",
                Host = p.GetProperty("host").GetString() ?? "",
                Port = p.GetProperty("port").GetInt32(),
                User = p.GetProperty("user").GetString() ?? "deck"
            }).ToList();

        QuickAccess = root.GetProperty("quickAccess").EnumerateArray()
            .Select(p => new QuickPath
            {
                Name = p.GetProperty("name").GetString() ?? "",
                Path = p.GetProperty("path").GetString() ?? "",
                Group = p.GetProperty("group").GetString() ?? ""
            }).ToList();

        RomFolders = ReadMap(root.GetProperty("romFolders"));
        InstallRoutes = ReadMap(root.GetProperty("installRoutes"));
        TitleIds = ReadMap(root.GetProperty("titleIds"))
            .ToDictionary(kv => kv.Key.ToUpperInvariant(), kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        var eden = root.GetProperty("eden");
        EdenMods = eden.GetProperty("mods").GetString() ?? "";
        EdenSaves = eden.GetProperty("saves").GetString() ?? "";
        EdenUsersRoot = eden.TryGetProperty("usersRoot", out var users)
            ? users.GetString() ?? ""
            : DeckClient.Combine(EdenSaves, "0000000000000000");
        EdenProfiles = eden.TryGetProperty("profiles", out var profiles)
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
            return path;
        if (RomFolders.TryGetValue(system, out path) && !string.IsNullOrEmpty(path))
            return path;
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
                return path;
        }
        var name = profile.Folders.FirstOrDefault() ?? profile.Id;
        return "/home/deck/Emulation/roms/" + name;
    }

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
        var game = new StoreGame
        {
            Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            System = el.TryGetProperty("system", out var s) ? s.GetString() ?? "" : "",
            TitleId = el.TryGetProperty("titleId", out var t) ? t.GetString() : null
        };
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
}
