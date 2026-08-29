using System.IO;
using System.Text.Json;
using Sesame.Services.GameOptimizer;

namespace Sesame.Services;

/// <summary>
/// User-chosen library roots. Empty folders only — no game list.
/// </summary>
public sealed class LibraryPaths
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static LibraryPaths Current { get; private set; } = new();

    public string RomsRoot { get; set; } = "/home/deck/Emulation/roms";
    public string HydraRoot { get; set; } = "/home/deck/Games/Hydra";
    public string LutrisRoot { get; set; } = "/home/deck/Games/Lutris";
    public string OtherGamesRoot { get; set; } = "/home/deck/Games/Other";
    public bool UseEden { get; set; } = true;
    public bool UseYuzu { get; set; }
    public bool UseRyujinx { get; set; }
    public bool UseCitron { get; set; }

    public string EmulationRoot
    {
        get
        {
            var roms = Norm(RomsRoot);
            var name = Path.GetFileName(roms);
            if (name.Equals("roms", StringComparison.OrdinalIgnoreCase))
                return DeckClient.Parent(roms);
            var parent = DeckClient.Parent(roms);
            return string.IsNullOrEmpty(parent) ? roms : parent;
        }
    }

    public string StorageRoot => Join(EmulationRoot, "storage");
    public string SavesRoot => Join(EmulationRoot, "saves");

    public IEnumerable<string> EnabledSwitchIds
    {
        get
        {
            if (UseEden) yield return "eden";
            if (UseYuzu) yield return "yuzu";
            if (UseRyujinx) yield return "ryujinx";
            if (UseCitron) yield return "citron";
        }
    }

    public string PrimarySwitchId => EnabledSwitchIds.FirstOrDefault() ?? "eden";
    public string PrimaryModsRoot => SwitchMods(PrimarySwitchId);
    public string PrimarySavesRoot => SwitchSaves(PrimarySwitchId);

    public static void Load()
    {
        try
        {
            var path = FilePath();
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<LibraryPaths>(File.ReadAllText(path), Json);
                if (loaded is not null)
                    Current = loaded;
            }
        }
        catch
        {
            Current = new LibraryPaths();
        }

        if (!File.Exists(FilePath()) &&
            !string.IsNullOrWhiteSpace(LaunchConfigStore.Current.RomsRoot))
            Current.RomsRoot = LaunchConfigStore.Current.RomsRoot;

        Current.Normalize();
    }

    public static void Save(bool syncLaunchers = true)
    {
        Current.Normalize();
        var path = FilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(Current, Json));
        AppDataPaths.RestrictFile(path);
        if (syncLaunchers)
        {
            LaunchConfigStore.Current.RomsRoot = Current.RomsRoot;
            LaunchConfigStore.Save();
        }
    }

    public string RomFolder(string systemFolder) =>
        Join(Norm(RomsRoot), systemFolder.Trim().Trim('/'));

    public string SwitchMods(string emulator) =>
        emulator.Equals("ryujinx", StringComparison.OrdinalIgnoreCase)
            ? Join(StorageRoot, "ryujinx", "mods", "contents")
            : Join(StorageRoot, emulator, "load");

    public string SwitchSaves(string emulator) =>
        emulator.Equals("ryujinx", StringComparison.OrdinalIgnoreCase)
            ? Join(StorageRoot, "ryujinx", "bis", "user", "save")
            : Join(StorageRoot, emulator, "nand", "user", "save");

    public string SwitchProfiles(string emulator) =>
        emulator.Equals("ryujinx", StringComparison.OrdinalIgnoreCase)
            ? ""
            : Join(StorageRoot, emulator, "nand", "system", "save",
                "8000000000000010", "su", "avators", "profiles.dat");

    public IReadOnlyList<string> FolderPaths()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        Add(paths, Norm(RomsRoot));
        foreach (var profile in SystemCatalog.All)
        {
            var folder = profile.Folders.FirstOrDefault() ?? profile.Id;
            Add(paths, RomFolder(folder));
        }

        Add(paths, Norm(HydraRoot));
        Add(paths, Norm(LutrisRoot));
        Add(paths, Norm(OtherGamesRoot));
        Add(paths, Join(Home(), ".config", "hydra"));
        Add(paths, Join(Home(), ".config", "hydralauncher"));
        Add(paths, Join(Home(), ".local", "share", "hydra"));
        Add(paths, Join(Home(), ".local", "share", "hydralauncher"));
        Add(paths, Join(EmulationRoot, "hdpacks"));
        Add(paths, Join(EmulationRoot, "bios", "Mupen64plus", "hires_texture"));
        Add(paths, Join(EmulationRoot, "bios", "Mupen64plus", "cache"));
        Add(paths, Join(SavesRoot, "retroarch"));
        foreach (var profile in SystemCatalog.All)
        {
            var folder = profile.Folders.FirstOrDefault() ?? profile.Id;
            Add(paths, Join(SavesRoot, folder, "retroarch", "saves"));
        }

        foreach (var id in EnabledSwitchIds)
        {
            Add(paths, SwitchMods(id));
            Add(paths, SwitchSaves(id));
        }

        return paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    private void Normalize()
    {
        RomsRoot = Norm(RomsRoot);
        HydraRoot = Norm(HydraRoot);
        LutrisRoot = Norm(LutrisRoot);
        OtherGamesRoot = Norm(OtherGamesRoot);
        if (!UseEden && !UseYuzu && !UseRyujinx && !UseCitron)
            UseEden = true;
    }

    private static void Add(HashSet<string> paths, string path)
    {
        path = Norm(path);
        if (path.Length == 0) return;
        paths.Add(path);
    }

    private static string Join(params string[] parts)
    {
        var path = Norm(parts[0]);
        for (var i = 1; i < parts.Length; i++)
            path = DeckClient.Combine(path, parts[i]);
        return path;
    }

    private static string Norm(string path) =>
        (path ?? "").Trim().Replace('\\', '/').TrimEnd('/');

    private static string Home() =>
        Directory.Exists("/home/deck") ? "/home/deck" : (Environment.GetEnvironmentVariable("HOME") ?? "/home/deck");

    private static string FilePath() => AppDataPaths.Combine("library-paths.json");
}
