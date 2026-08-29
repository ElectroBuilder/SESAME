using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Sesame.Models;
using Sesame.Services.GameOptimizer;

namespace Sesame.Services;

public sealed class GameLibrary
{
    private static readonly Regex TitleIdInName = new(@"0[1-9A-Fa-f][0-9A-Fa-f]{14}", RegexOptions.Compiled);
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata.txt", "systeminfo.txt", ".gitkeep"
    };

    public EdenLayout Eden { get; private set; } = new();
    public IReadOnlyList<RomSystemFolder> Systems { get; private set; } = [];

    public IReadOnlyList<GameEntry> Scan(DeckClient client, AppCatalog catalog)
    {
        Eden = DiscoverEden(client, catalog);
        var games = new List<GameEntry>();
        var primary = Eden.Primary;
        var files = RomScan.ListFiles(client, catalog);
        Systems = RomScan.Systems;

        foreach (var rom in RomScan.PickPrimary(files))
        {
            if (SkipNames.Contains(rom.FileName)) continue;
            var innerFile = rom.InnerFileName;
            var titleProbe = innerFile ?? rom.FileName;
            var titleId = ExtractTitleId(titleProbe) ?? GuessTitleId(titleProbe, catalog.TitleIds);
            var display = DisplayName(titleProbe, titleId, catalog);
            var system = rom.SystemLabel;
            var entry = BuildEntry(client, catalog, display, rom.FileName, system,
                rom.FullPath, titleId, primary);
            entry.InnerFileName = innerFile;
            if (RomHackLog.TryGet(rom.FullPath, out var loggedTitle, out var loggedKind))
            {
                if (string.Equals(loggedKind, "translation", StringComparison.OrdinalIgnoreCase))
                {
                    entry.IsTranslation = true;
                    entry.DisplayName = StoreGame.LooksLikeTranslation(display)
                        ? display
                        : StoreGame.StripVariant(display) + " (NL)";
                }
                else
                {
                    entry.IsRomHack = true;
                    entry.DisplayName = loggedTitle + " (ROM-hack)";
                }
            }
            else if (StoreGame.LooksLikeTranslation(rom.FileName))
            {
                entry.IsTranslation = true;
            }
            entry.Identity = catalog.ResolveStoreGame(entry.DisplayName, system,
                titleId, entry.IsTranslation);
            games.Add(entry);
        }

        return games.OrderBy(g => g.System).ThenBy(g => g.DisplayName).ToList();
    }

    public static string? ExtractTitleId(string fileName)
    {
        var m = TitleIdInName.Match(fileName);
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    private static bool LooksLikeTitleId(string name) =>
        name.Length == 16 && TitleIdInName.IsMatch(name);

    public static string? TexturePathFor(string displayName, string system, string? titleId, AppCatalog catalog)
    {
        foreach (var (name, path) in catalog.TextureByGame)
        {
            if (displayName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(displayName, StringComparison.OrdinalIgnoreCase))
                return path;
        }

        if (string.Equals(system, "SWITCH", StringComparison.OrdinalIgnoreCase) && titleId is not null)
            return DeckClient.Combine(catalog.EdenMods, titleId);
        if (string.Equals(system, "N64", StringComparison.OrdinalIgnoreCase) &&
            catalog.TextureRoots.TryGetValue("n64", out var n64))
            return n64;
        return catalog.TextureRoots.TryGetValue("hdpacks", out var hd) ? hd : null;
    }

    public static string? SavePathFor(string system, string? titleId, EdenUser? user, AppCatalog catalog)
    {
        if (string.Equals(system, "SWITCH", StringComparison.OrdinalIgnoreCase) &&
            titleId is not null && user is not null)
            return DeckClient.Combine(user.Folder, titleId);

        var key = StoreGame.FoldSystem(system);
        if (catalog.RetroarchSaves.TryGetValue(key, out var path)) return path;
        if (catalog.RetroarchSaves.TryGetValue(system.ToLowerInvariant(), out path)) return path;
        var profile = SystemCatalog.FromFolder(system);
        if (profile is not null)
        {
            foreach (var folder in profile.Folders)
                if (catalog.RetroarchSaves.TryGetValue(folder, out path)) return path;
            if (catalog.RetroarchSaves.TryGetValue(profile.Id, out path)) return path;
        }
        return null;
    }

    public static EdenLayout DiscoverEden(DeckClient client, AppCatalog catalog)
    {
        var root = catalog.EdenUsersRoot;
        if (!client.Exists(root))
        {
            var nested = DeckClient.Combine(catalog.EdenSaves, "0000000000000000");
            root = client.Exists(nested) ? nested : catalog.EdenSaves;
        }

        var profiles = ReadEdenProfiles(client, catalog.EdenProfiles);
        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var users = new List<EdenUser>();

        foreach (var profile in profiles)
        {
            var folder = ResolveSaveFolder(client, root, profile.Uuid) ??
                         DeckClient.Combine(root, NintendoSaveId(profile.Uuid));
            usedFolders.Add(Path.GetFileName(folder.TrimEnd('/')));
            users.Add(new EdenUser
            {
                Id = Path.GetFileName(folder.TrimEnd('/')),
                Name = profile.Name,
                Folder = folder
            });
        }

        if (client.Exists(root))
        {
            foreach (var item in client.List(root))
            {
                if (!item.IsDirectory || item.Name is "cache") continue;
                if (item.Name.Trim('0').Length == 0) continue;
                if (usedFolders.Contains(item.Name)) continue;
                if (LooksLikeTitleId(item.Name)) continue;
                users.Add(new EdenUser
                {
                    Id = item.Name,
                    Name = "Onbekend",
                    Folder = item.FullPath
                });
            }
        }

        return new EdenLayout { UsersRoot = root, Users = users };
    }

    private GameEntry BuildEntry(DeckClient client, AppCatalog catalog, string display, string fileName,
        string system, string romPath, string? titleId, EdenUser? primary)
    {
        var modPath = titleId is not null ? DeckClient.Combine(catalog.EdenMods, titleId) : null;
        var savePath = SavePathFor(system, titleId, primary, catalog);
        var texturePath = TexturePathFor(display, system, titleId, catalog);
        return new GameEntry
        {
            DisplayName = display,
            FileName = fileName,
            System = system,
            RomPath = romPath,
            TitleId = titleId,
            ModPath = modPath,
            SavePath = savePath,
            TexturePath = texturePath,
            SaveAccountName = primary?.Name,
            HasMods = DirectoryExistsWithChildren(client, modPath),
            HasSaves = !string.IsNullOrEmpty(savePath) && client.Exists(savePath),
            HasTextures = DirectoryExistsWithChildren(client, texturePath),
            Identity = catalog.ResolveStoreGame(display, system, titleId)
        };
    }

    private static bool DirectoryExistsWithChildren(DeckClient client, string? path)
    {
        if (string.IsNullOrEmpty(path) || !client.Exists(path)) return false;
        return client.List(path).Count > 0;
    }

    private static string? GuessTitleId(string fileName, IReadOnlyDictionary<string, string> titles)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        string? bestId = null;
        var bestLen = 0;
        foreach (var (id, name) in titles)
        {
            if (name.Length <= bestLen) continue;
            if (stem.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                bestId = id;
                bestLen = name.Length;
            }
        }
        return bestId;
    }

    private static string DisplayName(string fileName, string? titleId, AppCatalog catalog)
    {
        if (titleId is not null && catalog.TitleIds.TryGetValue(titleId, out var mapped))
            return mapped;
        var cleaned = RomNameCleaner.Clean(fileName);
        if (!string.IsNullOrWhiteSpace(cleaned)) return cleaned;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        stem = TitleIdInName.Replace(stem, "").Replace("[]", "").Replace("()", "");
        return Regex.Replace(stem, @"[_\-\[\]\(\)]+", " ").Trim();
    }

    private readonly record struct EdenProfile(byte[] Uuid, string Name);

    private const int ProfileHeaderSize = 0x10;
    private const int ProfileSlotSize = 0xC8;
    private const int MaxEdenUsers = 8;

    private static List<EdenProfile> ReadEdenProfiles(DeckClient client, string profilesPath)
    {
        var list = new List<EdenProfile>();
        if (string.IsNullOrEmpty(profilesPath) || !client.Exists(profilesPath))
            return list;

        byte[] data;
        try { data = client.ReadBytes(profilesPath); }
        catch { return list; }

        for (var i = 0; i < MaxEdenUsers; i++)
        {
            var offset = ProfileHeaderSize + i * ProfileSlotSize;
            if (offset + 0x38 > data.Length) break;
            var uuid = data.AsSpan(offset, 16).ToArray();
            if (uuid.All(b => b == 0)) continue;
            var name = DecodeProfileName(data.AsSpan(offset + 0x28, 32));
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new EdenProfile(uuid, name));
        }

        return list;
    }

    private static string? ResolveSaveFolder(DeckClient client, string root, byte[] uuid)
    {
        foreach (var id in UuidKeys(uuid))
        {
            var path = DeckClient.Combine(root, id);
            if (client.Exists(path))
                return path;
        }
        return null;
    }

    private static IEnumerable<string> UuidKeys(byte[] uuid)
    {
        yield return Convert.ToHexString(uuid);
        yield return NintendoSaveId(uuid);
        var lo = BitConverter.ToUInt64(uuid, 0);
        var hi = BitConverter.ToUInt64(uuid, 8);
        yield return $"{lo:X16}{hi:X16}";
        yield return Convert.ToHexString(uuid.Reverse().ToArray());
    }

    private static string NintendoSaveId(byte[] uuid)
    {
        var lo = BitConverter.ToUInt64(uuid, 0);
        var hi = BitConverter.ToUInt64(uuid, 8);
        return $"{hi:X16}{lo:X16}";
    }

    private static string DecodeProfileName(ReadOnlySpan<byte> raw)
    {
        var utf8 = Encoding.UTF8.GetString(raw).TrimEnd('\0', ' ', '\t');
        if (utf8.Length > 0 && utf8.All(c => !char.IsControl(c)))
            return utf8;
        var utf16 = Encoding.Unicode.GetString(raw).TrimEnd('\0', ' ', '\t');
        return utf16.All(c => !char.IsControl(c)) ? utf16 : "";
    }
}
