using System.IO;
using Sesame.Models;

namespace Sesame.Services.GameOptimizer;

public sealed class SteamShortcut
{
    public uint AppId { get; set; }
    public string AppName { get; set; } = "";
    public string Exe { get; set; } = "";
    public string StartDir { get; set; } = "";
    public string Icon { get; set; } = "";
    public string LaunchOptions { get; set; } = "";
    public string ShortcutPath { get; set; } = "";
    public string FlatpakAppId { get; set; } = "";
    public string RomPath { get; set; } = "";
    public List<string> Tags { get; } = new();
    public int AllowDesktopConfig { get; set; } = 1;
    public VdfNode? Extra { get; set; }
}

public static class SteamShortcuts
{
    public static IReadOnlyList<string> FindUserConfigs(DeckClient client)
    {
        var roots = new[]
        {
            "/home/deck/.local/share/Steam/userdata",
            "/home/deck/.steam/steam/userdata"
        };
        var found = new List<(string config, DateTime stamp)>();
        foreach (var root in roots)
        {
            if (!client.Exists(root)) continue;
            foreach (var dir in client.List(root).Where(i => i.IsDirectory && i.Name != "0"))
            {
                var config = DeckClient.Combine(dir.FullPath, "config");
                if (!client.Exists(config) ||
                    found.Any(f => string.Equals(f.config, config, StringComparison.Ordinal)))
                    continue;
                var stamp = DateTime.MinValue;
                try
                {
                    foreach (var item in client.List(config))
                    {
                        if (item.Name is "localconfig.vdf" or "shortcuts.vdf" && item.LastWrite > stamp)
                            stamp = item.LastWrite;
                    }
                }
                catch
                {
                    /* map blijft meedoen */
                }
                found.Add((config, stamp));
            }
        }
        return found
            .Select(f => (f.config, f.stamp, real: RealPath(client, f.config)))
            .GroupBy(f => f.real, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(x => x.stamp).First())
            .OrderByDescending(f => f.stamp)
            .Select(f => f.config)
            .ToList();
    }

    private static string RealPath(DeckClient client, string path)
    {
        try
        {
            var real = client.Execute("readlink -f " + DeckClient.ShQuote(path) + " 2>/dev/null || echo " +
                                      DeckClient.ShQuote(path), 6).Trim();
            return string.IsNullOrEmpty(real) ? path : real;
        }
        catch
        {
            return path;
        }
    }

    public static List<SteamShortcut> Load(DeckClient client, string configDir)
    {
        var path = DeckClient.Combine(configDir, "shortcuts.vdf");
        if (!client.Exists(path)) return new List<SteamShortcut>();
        var data = client.ReadBytes(path);
        if (data.Length == 0) return new List<SteamShortcut>();
        var root = BinaryVdf.Read(data);
        var map = root.Child("shortcuts") ?? root.Entries.FirstOrDefault().Value as VdfNode;
        if (map is null) return new List<SteamShortcut>();
        return map.Maps().Select(FromNode).ToList();
    }

    public static void Save(DeckClient client, string configDir, IReadOnlyList<SteamShortcut> shortcuts)
    {
        var path = DeckClient.Combine(configDir, "shortcuts.vdf");
        try
        {
            if (client.Exists(path))
                client.Execute("cp -f " + DeckClient.ShQuote(path) + " " +
                               DeckClient.ShQuote(path + ".sesame.bak") + " 2>/dev/null || true", 8);
        }
        catch
        {
            /* backup is extra zekerheid */
        }
        var map = new VdfNode();
        for (var i = 0; i < shortcuts.Count; i++)
            map.Set(i.ToString(), ToNode(shortcuts[i]));
        var root = new VdfNode();
        root.Set("shortcuts", map);
        client.WriteBytes(path, BinaryVdf.Write(root));
    }

    public const string OwnerTag = "SESAME";
    public const string LegacyOwnerTag = "VisualSSH";
    public const string AppShortcutPath = SteamSelfShortcut.ShortcutPath;

    public static bool IsOwned(SteamShortcut shortcut)
    {
        if (IsSesameLauncher(shortcut)) return false;
        if (shortcut.Tags.Any(IsOwnerTag))
            return true;
        if (IsOwnerTag(shortcut.ShortcutPath))
            return true;
        return LaunchComposer.IsVisualSshLaunch(shortcut.Exe, shortcut.LaunchOptions);
    }

    public static bool IsSesameLauncher(SteamShortcut shortcut)
    {
        if (string.Equals(shortcut.ShortcutPath, AppShortcutPath, StringComparison.OrdinalIgnoreCase))
            return true;
        var name = (shortcut.AppName ?? "").Trim();
        if (!name.Equals("SESAME", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("SESAME ", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("SESAME (Game Mode)", StringComparison.OrdinalIgnoreCase))
            return false;
        var exe = LaunchComposer.ExePath(shortcut.Exe ?? "").Replace('\\', '/');
        return exe.EndsWith("/SESAME", StringComparison.OrdinalIgnoreCase) ||
               exe.Contains("/Applications/SESAME", StringComparison.OrdinalIgnoreCase) ||
               Path.GetFileName(exe).Equals("SESAME", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOwnerTag(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Equals(OwnerTag, StringComparison.OrdinalIgnoreCase) ||
         value.Equals(LegacyOwnerTag, StringComparison.OrdinalIgnoreCase));

    public static SteamShortcut? FindByRom(IEnumerable<SteamShortcut> shortcuts, string romPath) =>
        shortcuts.FirstOrDefault(s => MentionsRom(s, romPath));

    public static SteamShortcut? FindOwnedByRom(IEnumerable<SteamShortcut> shortcuts, string romPath) =>
        shortcuts.FirstOrDefault(s => IsOwned(s) && MentionsRom(s, romPath));

    public static bool MentionsRom(SteamShortcut shortcut, string romPath)
    {
        var rom = NormalizeRom(romPath);
        if (rom.Length < 8) return false;
        // Flatpak apps all share /usr/bin/flatpak — never treat that as a unique ROM key.
        if (IsAmbiguousLauncher(rom)) return false;
        var hay = NormalizeRom(Hay(shortcut));
        if (hay.Contains(rom, StringComparison.OrdinalIgnoreCase)) return true;
        // Broken quotes: "/home/.../Black" Jacket/game.exe vs /home/.../Black Jacket/game.exe
        var looseHay = hay.Replace("\"", "");
        var looseRom = rom.Replace("\"", "");
        if (looseHay.Contains(looseRom, StringComparison.OrdinalIgnoreCase)) return true;
        var folder = GameFolderKey(rom);
        var other = GameFolderKey(LaunchComposer.ExePath(shortcut.Exe)) ??
                    GameFolderKey(ExtractRom(shortcut) ?? "");
        return !string.IsNullOrEmpty(folder) &&
               string.Equals(folder, other, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Match a catalog Flatpak/native app by launch target (not shared /usr/bin/flatpak).
    /// </summary>
    public static SteamShortcut? FindOwnedApp(IEnumerable<SteamShortcut> shortcuts, OptimizerGame game)
    {
        if (!DeckApps.TryMatch(game.DisplayName, game.RomPath, game.LaunchOptions, out var want))
            return null;
        return shortcuts.FirstOrDefault(s =>
            IsOwned(s) &&
            DeckApps.TryMatch(s.AppName, LaunchComposer.ExePath(s.Exe), s.LaunchOptions, out var hit) &&
            string.Equals(hit.Id, want.Id, StringComparison.OrdinalIgnoreCase));
    }

    public static SteamShortcut? FindApp(IEnumerable<SteamShortcut> shortcuts, OptimizerGame game)
    {
        if (!DeckApps.TryMatch(game.DisplayName, game.RomPath, game.LaunchOptions, out var want))
            return null;
        return shortcuts.FirstOrDefault(s =>
            DeckApps.TryMatch(s.AppName, LaunchComposer.ExePath(s.Exe), s.LaunchOptions, out var hit) &&
            string.Equals(hit.Id, want.Id, StringComparison.OrdinalIgnoreCase));
    }

    public static SteamShortcut Build(OptimizerGame game)
    {
        var steam = LaunchComposer.ForSteam(game.Target, game.StartDir, game.LaunchOptions);
        game.Target = steam.Exe;
        game.StartDir = steam.StartDir;
        game.LaunchOptions = steam.LaunchOptions;
        var appId = SteamCrc.ShortcutId(steam.Exe, game.DisplayName);
        game.SteamAppId = appId;
        var collection = SteamTabGrouping.TabName(game);
        return new SteamShortcut
        {
            AppId = appId,
            AppName = game.DisplayName,
            Exe = steam.Exe,
            StartDir = steam.StartDir,
            Icon = LaunchComposer.ExePath(steam.Exe),
            ShortcutPath = OwnerTag,
            LaunchOptions = steam.LaunchOptions,
            RomPath = game.RomPath,
            // Game Mode gebruikt de in-game layout; 1 laat Steam Input (gyro) ook in Desktop toe.
            AllowDesktopConfig = 1,
            Tags = { OwnerTag, collection }
        };
    }

    public static void Upsert(List<SteamShortcut> shortcuts, SteamShortcut item, bool overwrite)
    {
        var matches = FindReplaceCandidates(shortcuts, item).ToList();
        if (matches.Count == 0)
        {
            shortcuts.Add(item);
            return;
        }

        // Keep the oldest AppId so Steam artwork + CompatToolMapping stay attached
        // when we only fix Exe quoting / StartDir.
        var existing = matches
            .OrderByDescending(s => IsOwned(s))
            .ThenBy(s => s.AppId == 0 ? uint.MaxValue : s.AppId)
            .First();
        foreach (var extra in matches.Where(s => !ReferenceEquals(s, existing)))
            shortcuts.Remove(extra);

        if (existing.AppId != 0)
            item.AppId = existing.AppId;

        existing.Exe = item.Exe;
        existing.StartDir = item.StartDir;
        existing.LaunchOptions = item.LaunchOptions;
        existing.AppId = item.AppId;
        existing.RomPath = item.RomPath;
        existing.ShortcutPath = OwnerTag;
        existing.AllowDesktopConfig = item.AllowDesktopConfig;
        if (!existing.Tags.Any(t => t.Equals(OwnerTag, StringComparison.OrdinalIgnoreCase)))
            existing.Tags.Insert(0, OwnerTag);
        EnsureCollectionTag(existing, item.Tags);
        if (!overwrite) return;
        existing.AppName = item.AppName;
        existing.Icon = string.IsNullOrEmpty(item.Icon) ? existing.Icon : item.Icon;
        existing.Tags.Clear();
        existing.Tags.AddRange(item.Tags);
        if (!existing.Tags.Any(t => t.Equals(OwnerTag, StringComparison.OrdinalIgnoreCase)))
            existing.Tags.Insert(0, OwnerTag);
    }

    /// <summary>
    /// Match SESAME-owned rows by ROM path, and also orphan/broken Hydra duplicates
    /// by game folder or display name so Optimize never leaves two tiles.
    /// Catalog Flatpak apps share /usr/bin/flatpak — match those by DeckApps id only.
    /// </summary>
    private static IEnumerable<SteamShortcut> FindReplaceCandidates(
        IEnumerable<SteamShortcut> shortcuts, SteamShortcut item)
    {
        var rom = NormalizeRom(item.RomPath);
        var folder = GameFolderKey(rom);
        var name = NormalizeName(item.AppName);
        var pc = LooksPcExe(item.Exe) || LooksPcExe(rom);
        var itemApp = TryDeckAppId(item);

        foreach (var s in shortcuts)
        {
            if (IsSesameLauncher(s)) continue;

            var existingApp = TryDeckAppId(s);
            if (itemApp is not null || existingApp is not null)
            {
                if (itemApp is not null && existingApp is not null &&
                    string.Equals(itemApp, existingApp, StringComparison.OrdinalIgnoreCase))
                    yield return s;
                continue;
            }

            if (IsOwned(s) && !string.IsNullOrEmpty(rom) && MentionsRom(s, rom))
            {
                yield return s;
                continue;
            }

            if (!pc) continue;

            // Broken quoted paths still share the game folder name.
            if (!string.IsNullOrEmpty(folder) &&
                string.Equals(folder, GameFolderKey(LaunchComposer.ExePath(s.Exe)) ??
                                      GameFolderKey(ExtractRom(s) ?? "") ??
                                      GameFolderKey(s.StartDir), StringComparison.OrdinalIgnoreCase))
            {
                yield return s;
                continue;
            }

            if (!string.IsNullOrEmpty(name) &&
                string.Equals(name, NormalizeName(s.AppName), StringComparison.OrdinalIgnoreCase) &&
                (LooksPcExe(s.Exe) || Hay(s).Contains("/Hydra/", StringComparison.OrdinalIgnoreCase) ||
                 Hay(s).Contains("/hydra/", StringComparison.OrdinalIgnoreCase) ||
                 Hay(s).Contains("/Lutris/", StringComparison.OrdinalIgnoreCase) ||
                 IsOwned(s)))
                yield return s;
        }
    }

    private static string? TryDeckAppId(SteamShortcut s)
    {
        if (!DeckApps.TryMatch(s.AppName, LaunchComposer.ExePath(s.Exe), s.LaunchOptions, out var entry))
            return null;
        return entry.Id;
    }

    private static bool IsAmbiguousLauncher(string path)
    {
        var leaf = Path.GetFileName(NormalizeRom(path)).ToLowerInvariant();
        return leaf is "flatpak" or "python" or "python3" or "bash" or "sh" or "env" or "wine";
    }

    private static string NormalizeName(string? name) =>
        ManualShortcutStore.Normalize(name ?? "");

    private static string? GameFolderKey(string? path)
    {
        var p = NormalizeRom(path ?? "");
        if (p.Length < 3) return null;
        // .../Hydra/Black Jacket/game.exe → blackjacket
        var dir = p;
        if (Path.HasExtension(dir) &&
            Path.GetExtension(dir).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            dir = DeckClient.Parent(dir);
        var folder = Path.GetFileName(dir.TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(folder)) return null;
        if (folder.Equals("Hydra", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("Lutris", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("Other", StringComparison.OrdinalIgnoreCase) ||
            folder.Equals("Games", StringComparison.OrdinalIgnoreCase))
            return null;
        return ManualShortcutStore.Normalize(folder);
    }

    private static bool LooksPcExe(string? path)
    {
        var exe = LaunchComposer.ExePath(path ?? "");
        var ext = Path.GetExtension(exe).ToLowerInvariant();
        return ext is ".exe" or ".bat" or ".cmd" or ".msi";
    }

    private static void EnsureCollectionTag(SteamShortcut existing, IReadOnlyList<string> tags)
    {
        var collection = tags.FirstOrDefault(t =>
            !t.Equals(OwnerTag, StringComparison.OrdinalIgnoreCase) &&
            !t.Equals(LegacyOwnerTag, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(collection)) return;
        existing.Tags.RemoveAll(t =>
            !t.Equals(OwnerTag, StringComparison.OrdinalIgnoreCase) &&
            !t.Equals(LegacyOwnerTag, StringComparison.OrdinalIgnoreCase));
        existing.Tags.Add(collection);
    }

    public static int RemoveLegacyFor(List<SteamShortcut> shortcuts, IEnumerable<SteamShortcut> written)
    {
        var writtenList = written.ToList();
        var keepIds = writtenList.Select(w => w.AppId).ToHashSet();
        return shortcuts.RemoveAll(s =>
            IsOwned(s) &&
            LaunchComposer.IsLegacyScript(s.Exe, s.LaunchOptions) &&
            !keepIds.Contains(s.AppId) &&
            writtenList.Any(w => MentionsRom(s, w.RomPath)));
    }

    public static List<SteamShortcut> LoadAll(DeckClient client, IReadOnlyList<string> configs)
    {
        var list = new List<SteamShortcut>();
        foreach (var config in configs)
        {
            foreach (var item in Load(client, config))
            {
                if (list.Any(s => s.AppId != 0 && s.AppId == item.AppId))
                    continue;
                if (list.Any(s =>
                        string.Equals(s.AppName, item.AppName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.Exe, item.Exe, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.LaunchOptions ?? "", item.LaunchOptions ?? "",
                            StringComparison.OrdinalIgnoreCase)))
                    continue;
                list.Add(item);
            }
        }
        return list;
    }

    private static string NormalizeRom(string romPath)
    {
        var rom = (romPath ?? "").Replace('\\', '/').Trim().Trim('"');
        return rom;
    }

    private static string Hay(SteamShortcut s) =>
        ((s.Exe ?? "") + " " + (s.LaunchOptions ?? "") + " " + (s.StartDir ?? "")).Replace('\\', '/');

    private static string? ExtractRom(SteamShortcut item)
    {
        var hay = Hay(item);
        var q = hay.LastIndexOf('"');
        if (q <= 0) return null;
        var p = hay.LastIndexOf('"', q - 1);
        if (p >= 0 && q > p)
        {
            var inner = hay[(p + 1)..q];
            if (inner.Contains('/')) return inner;
        }
        return null;
    }

    private static SteamShortcut FromNode(VdfNode node)
    {
        var shortcut = new SteamShortcut
        {
            AppId = unchecked((uint)node.GetInt("appid")),
            AppName = node.GetString("AppName") ?? node.GetString("appname") ?? "",
            Exe = node.GetString("Exe") ?? "",
            StartDir = node.GetString("StartDir") ?? "",
            Icon = node.GetString("icon") ?? "",
            LaunchOptions = node.GetString("LaunchOptions") ?? "",
            ShortcutPath = node.GetString("ShortcutPath") ?? "",
            FlatpakAppId = node.GetString("FlatpakAppID") ?? "",
            AllowDesktopConfig = node.GetInt("AllowDesktopConfig", 1) == 0 ? 0 : 1,
            Extra = node
        };
        var tags = node.Child("tags");
        if (tags is not null)
        {
            foreach (var entry in tags.Entries)
            {
                if (entry.Value is string tag && !string.IsNullOrWhiteSpace(tag))
                    shortcut.Tags.Add(tag);
            }
        }
        return shortcut;
    }

    private static VdfNode ToNode(SteamShortcut s)
    {
        if (!IsOwned(s) && s.Extra is not null)
            return s.Extra;

        var node = s.Extra ?? new VdfNode();
        node.Set("appid", unchecked((int)s.AppId));
        node.Set("AppName", s.AppName);
        node.Set("Exe", s.Exe);
        node.Set("StartDir", s.StartDir);
        node.Set("icon", s.Icon ?? "");
        node.Set("ShortcutPath", s.ShortcutPath ?? "");
        node.Set("LaunchOptions", s.LaunchOptions ?? "");
        node.Set("IsHidden", node.GetInt("IsHidden"));
        node.Set("AllowDesktopConfig", s.AllowDesktopConfig);
        node.Set("AllowOverlay", 1);
        node.Set("OpenVR", node.GetInt("OpenVR"));
        node.Set("Devkit", node.GetInt("Devkit"));
        node.Set("DevkitGameID", node.GetString("DevkitGameID") ?? "");
        node.Set("DevkitOverrideAppID", node.GetInt("DevkitOverrideAppID"));
        node.Set("LastPlayTime", node.GetInt("LastPlayTime"));
        node.Set("FlatpakAppID", s.FlatpakAppId ?? "");
        var tags = new VdfNode();
        for (var i = 0; i < s.Tags.Count; i++)
            tags.Set(i.ToString(), s.Tags[i]);
        node.Set("tags", tags);
        return node;
    }
}
