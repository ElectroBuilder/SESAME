using System.IO;
using System.Reflection;
using VisualSSH.Models;
using VisualSSH.Services;

namespace VisualSSH.Services.GameOptimizer;

public static class ExtraShortcuts
{
    public static IReadOnlyList<OptimizerGame> Scan(DeckClient client, IReadOnlyList<SteamShortcut> steam)
    {
        var games = new List<OptimizerGame>();
        try
        {
            Parse(client.Execute("python3 -c " + DeckClient.ShQuote(Script()), 45), games);
        }
        catch
        {
            /* Hydra/apps zijn optioneel: Steam-shortcuts hieronder blijven een fallback */
        }

        foreach (var shortcut in steam)
        {
            if (SteamShortcuts.IsOwned(shortcut)) continue;
            var hay = ((shortcut.Exe ?? "") + " " + (shortcut.LaunchOptions ?? "") + " " +
                       (shortcut.StartDir ?? "")).Replace('\\', '/');
            if (hay.Contains("hydra", StringComparison.OrdinalIgnoreCase) &&
                games.All(g => !g.RomPath.Equals(LaunchComposer.ExePath(shortcut.Exe ?? ""),
                    StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(g.DisplayName, shortcut.AppName, StringComparison.OrdinalIgnoreCase)))
            {
                games.Add(FromShortcut(shortcut, ShortcutKind.Hydra, "Hydra"));
            }
        }

        return games
            .GroupBy(g => g.DisplayName + "|" + g.Target, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(g => g.SystemName)
            .ThenBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            var hydra = kind.Equals("HYDRA", StringComparison.OrdinalIgnoreCase);
            var steam = LaunchComposer.ForSteam(exe, start, options);
            games.Add(new OptimizerGame
            {
                DisplayName = title,
                FileName = Path.GetFileName(exe.Trim('"')),
                RomPath = exe.Trim('"'),
                FolderName = hydra ? "hydra" : "apps",
                SystemId = hydra ? "hydra" : "app",
                SystemName = hydra ? "Hydra" : "Apps",
                Category = hydra ? "Hydra" : "Apps",
                Fps = 60,
                SearchQuery = title,
                ShortcutKind = hydra ? ShortcutKind.Hydra : ShortcutKind.App,
                EmulatorName = hydra ? "Hydra" : "App",
                Target = steam.Exe,
                StartDir = steam.StartDir,
                LaunchOptions = steam.LaunchOptions
            });
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
            EmulatorName = system,
            Target = shortcut.Exe ?? "",
            StartDir = shortcut.StartDir ?? "",
            LaunchOptions = shortcut.LaunchOptions ?? "",
            InSteam = true,
            SteamAppId = shortcut.AppId,
            Status = "In Steam (extern)"
        };

    private static string? _script;

    private static string Script()
    {
        if (_script is not null) return _script;
        var asm = typeof(ExtraShortcuts).Assembly;
        using var stream = asm.GetManifestResourceStream("VisualSSH.extra-scan.py");
        if (stream is null)
            throw new InvalidOperationException("extra-scan.py ontbreekt in de build.");
        using var reader = new StreamReader(stream);
        _script = reader.ReadToEnd();
        return _script;
    }
}
