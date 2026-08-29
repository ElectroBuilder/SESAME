using System.IO;
using System.Reflection;
using System.Text.Json;
using Sesame.Models;
using Sesame.Services;

namespace Sesame.Services.GameOptimizer;

public enum ExtraScanMode
{
    All,
    Apps,
    Hydra
}

public static class ExtraShortcuts
{
    public static IReadOnlyList<OptimizerGame> Scan(DeckClient client, IReadOnlyList<SteamShortcut> steam,
        ExtraScanMode mode = ExtraScanMode.All, IProgress<string>? progress = null)
    {
        var games = new List<OptimizerGame>();
        if (mode is ExtraScanMode.All or ExtraScanMode.Hydra)
        {
            progress?.Report("Reading Hydra library…");
            try
            {
                Parse(Run(client, "hydra", 90), games);
            }
            catch
            {
                /* Hydra is optional */
            }

            foreach (var shortcut in steam)
            {
                if (SteamShortcuts.IsOwned(shortcut)) continue;
                var hay = ((shortcut.Exe ?? "") + " " + (shortcut.LaunchOptions ?? "") + " " +
                           (shortcut.StartDir ?? "")).Replace('\\', '/');
                if (!hay.Contains("hydra", StringComparison.OrdinalIgnoreCase)) continue;
                if (games.Any(g => g.ShortcutKind == ShortcutKind.Hydra &&
                                   ManualShortcutStore.Normalize(g.DisplayName) ==
                                   ManualShortcutStore.Normalize(shortcut.AppName)))
                    continue;
                games.Add(FromShortcut(shortcut, ShortcutKind.Hydra, "Hydra"));
            }
        }

        if (mode is ExtraScanMode.All or ExtraScanMode.Apps)
        {
            progress?.Report("Reading installed apps…");
            try
            {
                Parse(Run(client, "apps", 35), games);
            }
            catch
            {
                /* apps are optional */
            }
        }

        progress?.Report("Merging duplicates…");
        AppendManuals(games, mode);
        return Sanitize(games);
    }

    public static IReadOnlyList<OptimizerGame> Sanitize(IEnumerable<OptimizerGame> games) =>
        games
            .Where(Keep)
            .GroupBy(KeyOf, StringComparer.OrdinalIgnoreCase)
            .Select(KeepPreferred)
            .OrderBy(g => g.SystemName)
            .ThenBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string KeyOf(OptimizerGame game)
    {
        if (game.ShortcutKind == ShortcutKind.Rom)
            return "rom|" + (game.RomPath ?? "").Replace('\\', '/').ToLowerInvariant();
        if (game.ShortcutKind == ShortcutKind.App)
        {
            if (DeckApps.TryMatch(game.DisplayName, game.RomPath, game.LaunchOptions, out var app))
                return "app|" + app.Id;
            return "app|" + ManualShortcutStore.Normalize(game.DisplayName);
        }

        if (game.ShortcutKind == ShortcutKind.Hydra)
            return "hydra|" + ManualShortcutStore.Normalize(game.DisplayName);
        return "game|" + ManualShortcutStore.Normalize(game.DisplayName);
    }

    public static void ApplyLaunch(OptimizerGame game, LaunchChoice choice, bool remember = true)
    {
        var steam = LaunchComposer.ForSteam(choice.Exe, choice.StartDir, choice.Options);
        game.Target = steam.Exe;
        game.StartDir = steam.StartDir;
        game.LaunchOptions = steam.LaunchOptions;
        game.RomPath = string.IsNullOrWhiteSpace(choice.RomPath) ? steam.Exe.Trim('"') : choice.RomPath;
        game.FileName = Path.GetFileName(game.RomPath.Trim('"'));
        game.ChosenLaunch = choice.Key;
        if (game.Status.StartsWith("Multiple launches", StringComparison.OrdinalIgnoreCase))
            game.Status = game.InSteam ? "In Steam" : "New";
        if (remember)
        {
            var kind = game.ShortcutKind == ShortcutKind.App ? "App" : "Game";
            ManualShortcutStore.RememberLaunch(game.DisplayName, steam.Exe, steam.LaunchOptions, kind);
        }
    }

    public static void UnionChoices(OptimizerGame game, IEnumerable<OptimizerGame> others)
    {
        var map = game.LaunchChoices
            .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        void Add(LaunchChoice choice)
        {
            if (string.IsNullOrWhiteSpace(choice.Exe) || map.ContainsKey(choice.Key)) return;
            map[choice.Key] = choice;
            game.LaunchChoices.Add(choice);
        }

        Add(ToChoice(game));
        foreach (var other in others)
        {
            Add(ToChoice(other));
            foreach (var choice in other.LaunchChoices)
                Add(choice);
        }
    }

    private static bool Keep(OptimizerGame game)
    {
        if (game.ShortcutKind != ShortcutKind.App) return true;
        if (game.IsManual) return true;
        return DeckApps.TryMatch(game.DisplayName, game.RomPath, game.LaunchOptions, out _);
    }

    private static OptimizerGame KeepPreferred(IGrouping<string, OptimizerGame> group)
    {
        var list = group.ToList();
        var winner = list
            .OrderBy(g => g.IsManual ? 0 : 1)
            .ThenBy(g => DeckApps.LaunchRank(g.Target, g.LaunchOptions))
            .ThenByDescending(g => g.InSteam)
            .First();
        UnionChoices(winner, list.Where(g => !ReferenceEquals(g, winner)));
        winner.IsManual = list.Any(g => g.IsManual);
        if (string.IsNullOrEmpty(winner.ManualId))
            winner.ManualId = list.Select(g => g.ManualId).FirstOrDefault(id => !string.IsNullOrEmpty(id)) ?? "";

        var steamHit = list.FirstOrDefault(g => g.InSteam);
        if (steamHit is not null)
        {
            winner.InSteam = true;
            winner.SteamAppId = steamHit.SteamAppId != 0 ? steamHit.SteamAppId : winner.SteamAppId;
            if (string.IsNullOrEmpty(winner.Status) || winner.Status == "New")
                winner.Status = steamHit.Status;
        }

        var chosen = ManualShortcutStore.ChosenLaunch(winner.DisplayName);
        if (!string.IsNullOrEmpty(chosen))
        {
            var hit = winner.LaunchChoices.FirstOrDefault(c =>
                string.Equals(c.Key, chosen, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                ApplyLaunch(winner, hit, remember: false);
        }
        else if (winner.LaunchChoices.Count > 1 && !winner.InSteam)
            winner.Status = "Multiple launches — pick one";

        return winner;
    }

    private static void AppendManuals(List<OptimizerGame> games, ExtraScanMode mode)
    {
        foreach (var item in ManualShortcutStore.Load())
        {
            var app = item.Kind.Equals("App", StringComparison.OrdinalIgnoreCase);
            if (mode == ExtraScanMode.Apps && !app) continue;
            if (mode == ExtraScanMode.Hydra && app) continue;
            games.Add(ManualShortcutStore.ToGame(item));
        }
    }

    private static void Parse(string output, List<OptimizerGame> games)
    {
        foreach (var line in output.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 4) continue;
            var kind = parts[0].Trim();
            var title = parts[1].Trim();
            var exe = parts[2].Trim();
            var start = parts[3].Trim();
            var options = parts.Length > 4 ? parts[4].Trim() : "";
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(exe)) continue;
            if (kind.Equals("APP", StringComparison.OrdinalIgnoreCase))
            {
                if (!DeckApps.TryMatch(title, exe, options, out var app))
                    continue;
                // Catalog title wins — never keep a contaminated DisplayName/SearchQuery.
                AddParsed(games, app.Title, exe, start, options, ShortcutKind.App, "apps", "app", "Apps");
                continue;
            }

            if (kind.Equals("LUTRIS", StringComparison.OrdinalIgnoreCase))
                AddParsed(games, title, exe, start, options, ShortcutKind.Game, "lutris", "lutris", "Lutris");
            else if (kind.Equals("OTHER", StringComparison.OrdinalIgnoreCase) ||
                     kind.Equals("GAME", StringComparison.OrdinalIgnoreCase))
                AddParsed(games, title, exe, start, options, ShortcutKind.Game, "other", "game", "Games");
            else
                AddParsed(games, title, exe, start, options, ShortcutKind.Hydra, "hydra", "hydra", "Hydra");
        }
    }

    private static OptimizerGame FromShortcut(SteamShortcut shortcut, ShortcutKind kind, string system) =>
        new()
        {
            DisplayName = shortcut.AppName,
            FileName = Path.GetFileName((shortcut.Exe ?? "").Trim('"')),
            RomPath = LaunchComposer.ExePath(shortcut.Exe ?? ""),
            FolderName = kind == ShortcutKind.Hydra ? "hydra" : "apps",
            SystemId = kind == ShortcutKind.Hydra ? "hydra" : "app",
            SystemName = system,
            Category = system,
            Fps = 60,
            SearchQuery = shortcut.AppName,
            ShortcutKind = kind,
            EmulatorName = kind == ShortcutKind.Hydra ? "Game" : system,
            Target = shortcut.Exe ?? "",
            StartDir = shortcut.StartDir ?? "",
            LaunchOptions = shortcut.LaunchOptions ?? "",
            InSteam = true,
            SteamAppId = shortcut.AppId,
            Status = "In Steam (extern)",
            LaunchChoices = { ToChoice(shortcut.Exe ?? "", shortcut.StartDir ?? "", shortcut.LaunchOptions ?? "") }
        };

    private static LaunchChoice ToChoice(OptimizerGame game) =>
        ToChoice(game.Target, game.StartDir, game.LaunchOptions, game.RomPath);

    private static LaunchChoice ToChoice(string exe, string start, string options, string? rom = null) =>
        new()
        {
            Exe = exe ?? "",
            StartDir = start ?? "",
            Options = options ?? "",
            RomPath = string.IsNullOrWhiteSpace(rom) ? (exe ?? "").Trim('"') : rom
        };

    private static string Run(DeckClient client, string mode, int timeout)
    {
        var code =
            "MODE = " + JsonText(mode) + "\n" +
            "LUTRIS_ROOT = " + JsonText(LibraryPaths.Current.LutrisRoot) + "\n" +
            "OTHER_ROOT = " + JsonText(LibraryPaths.Current.OtherGamesRoot) + "\n" +
            "HYDRA_GAMES = " + JsonText(LibraryPaths.Current.HydraRoot) + "\n" +
            Script();
        return client.Execute("python3 -u -c " + DeckClient.ShQuote(code), timeout);
    }

    private static string JsonText(string? value) =>
        JsonSerializer.Serialize(value ?? "");

    private static string? _script;

    private static string Script()
    {
        if (_script is not null) return _script;
        var asm = typeof(ExtraShortcuts).Assembly;
        using var stream = asm.GetManifestResourceStream("Sesame.extra-scan.py")
                           ?? asm.GetManifestResourceStream("VisualSSH.extra-scan.py");
        if (stream is null)
            throw new InvalidOperationException("extra-scan.py ontbreekt in de build.");
        using var reader = new StreamReader(stream);
        _script = reader.ReadToEnd();
        return _script;
    }

    private static void AddParsed(List<OptimizerGame> games, string title, string exe, string start,
        string options, ShortcutKind kind, string folder, string systemId, string systemName)
    {
        var steam = LaunchComposer.ForSteam(exe, start, options);
        games.Add(new OptimizerGame
        {
            DisplayName = title,
            FileName = Path.GetFileName(exe.Trim('"')),
            RomPath = exe.Trim('"'),
            FolderName = folder,
            SystemId = systemId,
            SystemName = systemName,
            Category = systemName,
            Fps = 60,
            SearchQuery = title,
            ShortcutKind = kind,
            EmulatorName = kind == ShortcutKind.App ? "App" : "Game",
            Target = steam.Exe,
            StartDir = steam.StartDir,
            LaunchOptions = steam.LaunchOptions,
            LaunchChoices = { ToChoice(exe, start, options) }
        });
    }
}
