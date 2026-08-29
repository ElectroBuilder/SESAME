using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sesame.Models;
using Sesame.Services;

namespace Sesame.Services.GameOptimizer;

public sealed class ManualShortcut
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
    public string StartDir { get; set; } = "";
    public string LaunchOptions { get; set; } = "";
    public string Kind { get; set; } = "App";
    public string ChosenLaunch { get; set; } = "";
    public bool AddedByUser { get; set; }
}

public static class ManualShortcutStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static List<ManualShortcut>? _cache;

    public static IReadOnlyList<ManualShortcut> Load()
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;
            try
            {
                var path = FilePath();
                if (File.Exists(path))
                    _cache = JsonSerializer.Deserialize<List<ManualShortcut>>(File.ReadAllText(path), Json)
                             ?? [];
            }
            catch
            {
                _cache = [];
            }

            _cache ??= [];
            return _cache;
        }
    }

    public static void Upsert(ManualShortcut item)
    {
        lock (Gate)
        {
            var list = Load().ToList();
            var i = list.FindIndex(x => x.Id == item.Id || NamesMatch(x, item));
            if (i >= 0)
            {
                item.Id = list[i].Id;
                list[i] = item;
            }
            else
                list.Add(item);
            Save(list);
        }
    }

    public static void RememberLaunch(string name, string exe, string options, string kind = "App")
    {
        lock (Gate)
        {
            var list = Load().ToList();
            var hit = list.FirstOrDefault(x =>
                x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                hit = new ManualShortcut
                {
                    Name = name,
                    Exe = exe,
                    LaunchOptions = options,
                    Kind = kind,
                    AddedByUser = false
                };
                list.Add(hit);
            }
            hit.ChosenLaunch = LaunchChoice.KeyOf(exe, options);
            hit.Exe = exe;
            hit.LaunchOptions = options;
            Save(list);
        }
    }

    public static void Delete(string id)
    {
        lock (Gate)
        {
            Save(Load().Where(x => x.Id != id).ToList());
        }
    }

    public static string? ChosenLaunch(string name) =>
        Load().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.ChosenLaunch;

    public static OptimizerGame ToGame(ManualShortcut item)
    {
        var steam = LaunchComposer.ForSteam(item.Exe, item.StartDir, item.LaunchOptions);
        var gameKind = item.Kind.Equals("Game", StringComparison.OrdinalIgnoreCase);
        return new OptimizerGame
        {
            DisplayName = item.Name,
            FileName = Path.GetFileName(item.Exe.Trim('"')),
            RomPath = item.Exe.Trim('"'),
            FolderName = gameKind ? "games" : "apps",
            SystemId = gameKind ? "game" : "app",
            SystemName = gameKind ? "Games" : "Apps",
            Category = gameKind ? "Games" : "Apps",
            Fps = 60,
            SearchQuery = item.Name,
            ShortcutKind = gameKind ? ShortcutKind.Game : ShortcutKind.App,
            EmulatorName = gameKind ? "Game" : "App",
            Target = steam.Exe,
            StartDir = steam.StartDir,
            LaunchOptions = steam.LaunchOptions,
            IsManual = item.AddedByUser,
            ManualId = item.Id,
            Status = item.AddedByUser ? "Manual" : "New",
            LaunchChoices =
            {
                new LaunchChoice
                {
                    Exe = steam.Exe,
                    StartDir = steam.StartDir,
                    Options = steam.LaunchOptions,
                    RomPath = item.Exe.Trim('"')
                }
            }
        };
    }

    public static GameEntry ToLibraryEntry(ManualShortcut item) =>
        new()
        {
            DisplayName = item.Name,
            FileName = Path.GetFileName(item.Exe.Trim('"')),
            System = "GAME",
            RomPath = item.Exe.Trim('"'),
            IsManual = item.AddedByUser,
            ManualId = item.Id,
            KindOverride = "Game",
            TagOverride = "Manual"
        };

    public static bool NamesMatch(ManualShortcut left, ManualShortcut right) =>
        left.Kind.Equals(right.Kind, StringComparison.OrdinalIgnoreCase) &&
        Normalize(left.Name) == Normalize(right.Name);

    public static string Normalize(string name)
    {
        var text = Regex.Replace(name ?? "", @"\s*[\(\[].*?[\)\]]", "").Trim().ToLowerInvariant();
        return Regex.Replace(text, @"\s+", " ");
    }

    private static void Save(List<ManualShortcut> list)
    {
        _cache = list;
        var path = FilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(list, Json));
        AppDataPaths.RestrictFile(path);
    }

    private static string FilePath() => AppDataPaths.Combine("manual-shortcuts.json");
}
