using System.IO;
using System.Text.Json;
using Sesame.Models;
using Sesame.Services;

namespace Sesame.Services.GameOptimizer;

public sealed class LaunchLayout
{
    public string RomsRoot { get; set; } = "/home/deck/Emulation/roms";
    public string ToolsRoot { get; set; } = "/home/deck/Emulation/tools";
    public string LaunchersRoot { get; set; } = "/home/deck/Emulation/tools/launchers";
    public string Flatpak { get; set; } = "/usr/bin/flatpak";
    public string RetroArchApp { get; set; } = "org.libretro.RetroArch";
    public string DefaultPreset { get; set; } = LaunchPresets.Flatpak;
    public List<SystemLaunchConfig> Systems { get; set; } = new();
}

public sealed class SystemLaunchConfig
{
    public string SystemId { get; set; } = "";
    public string Name { get; set; } = "";
    public string RomFolder { get; set; } = "";
    public string Preset { get; set; } = LaunchPresets.Flatpak;
    public string Emulator { get; set; } = "retroarch";
    public string Core { get; set; } = "";
    public string TargetTemplate { get; set; } = "";
    public string StartDirTemplate { get; set; } = "";
    public string OptionsTemplate { get; set; } = "";
}

public sealed class LaunchPreset
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Target { get; init; } = "";
    public string StartDir { get; init; } = "";
    public string Options { get; init; } = "";
    public override string ToString() => Name;
}

public static class EmulatorLaunch
{
    public const string RetroArchArgs = "run org.libretro.RetroArch -L";
    public const string RetroArchTarget = "{flatpak} {emulator} {core} \"{rom}\"";
    public const string StandaloneTarget = "{exe} \"{rom}\"";

    public static IReadOnlyList<string> Ids { get; } =
    [
        "retroarch", "dolphin", "eden", "yuzu", "ryujinx", "citron", "cemu",
        "ppsspp", "pcsx2", "duckstation", "rpcs3", "flycast", "xemu", "vita3k",
        "citra", "lime3ds", "azahar", "mame", "drastic"
    ];

    public static bool IsRetroArch(string? id) =>
        string.Equals(id, "retroarch", StringComparison.OrdinalIgnoreCase);

    public static string Args(string? id) =>
        IsRetroArch(id) ? RetroArchArgs : "";

    public static void Apply(SystemLaunchConfig cfg)
    {
        if (IsRetroArch(cfg.Emulator))
        {
            cfg.Preset = LaunchPresets.Flatpak;
            cfg.TargetTemplate = RetroArchTarget;
            cfg.StartDirTemplate = "/usr/bin/";
            cfg.OptionsTemplate = "";
        }
        else
        {
            cfg.Preset = LaunchPresets.Standalone;
            cfg.TargetTemplate = StandaloneTarget;
            cfg.StartDirTemplate = "{exeDir}/";
            cfg.OptionsTemplate = "";
        }
    }
}

public static class LaunchPresets
{
    public const string Flatpak = "retroarch-flatpak";
    public const string FlatpakLine = "retroarch-oneline";
    public const string EmuDeckLauncher = "emudeck-launcher";
    public const string EmuDeckScript = "emudeck-script";
    public const string Wrapper = "sesame-wrapper";
    public const string LegacyWrapper = "vssh-wrapper";
    public const string Standalone = "standalone";

    public static IReadOnlyList<LaunchPreset> All { get; } =
    [
        new()
        {
            Id = Flatpak,
            Name = "RetroArch",
            Target = EmulatorLaunch.RetroArchTarget,
            StartDir = "/usr/bin/",
            Options = ""
        },
        new()
        {
            Id = Standalone,
            Name = "Standalone (Eden, Dolphin, …)",
            Target = EmulatorLaunch.StandaloneTarget,
            StartDir = "{exeDir}/",
            Options = ""
        }
    ];

    public static LaunchPreset Get(string? id)
    {
        if (string.Equals(id, FlatpakLine, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, EmuDeckLauncher, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, EmuDeckScript, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, Wrapper, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, LegacyWrapper, StringComparison.OrdinalIgnoreCase))
            id = Flatpak;
        return All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }

    public static void Apply(SystemLaunchConfig cfg, string presetId)
    {
        if (presetId.Equals(Standalone, StringComparison.OrdinalIgnoreCase) &&
            !EmulatorLaunch.IsRetroArch(cfg.Emulator))
        {
            EmulatorLaunch.Apply(cfg);
            return;
        }
        var preset = Get(presetId);
        cfg.Preset = preset.Id;
        cfg.TargetTemplate = preset.Target;
        cfg.StartDirTemplate = preset.StartDir;
        cfg.OptionsTemplate = preset.Options;
    }
}

public static class LaunchConfigStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static LaunchLayout Current { get; private set; } = Defaults();

    public static void Load()
    {
        try
        {
            var path = FilePath();
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<LaunchLayout>(File.ReadAllText(path), Json);
                if (loaded is not null)
                    Current = loaded;
            }
        }
        catch
        {
            Current = Defaults();
        }
        MergeCatalog();
        if (MigrateTemplates())
            Save();
    }

    public static void Save()
    {
        MergeCatalog();
        var path = FilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(Current, Json));
        AppDataPaths.RestrictFile(path);
    }

    public static SystemLaunchConfig ForSystem(string systemId)
    {
        MergeCatalog();
        var hit = Current.Systems.FirstOrDefault(s =>
            s.SystemId.Equals(systemId, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit;
        var profile = SystemCatalog.All.FirstOrDefault(p =>
            p.Id.Equals(systemId, StringComparison.OrdinalIgnoreCase));
        var created = profile is null
            ? new SystemLaunchConfig { SystemId = systemId, Name = systemId, Preset = Current.DefaultPreset }
            : FromProfile(profile, Current.DefaultPreset);
        if (string.IsNullOrWhiteSpace(created.TargetTemplate))
            EmulatorLaunch.Apply(created);
        Current.Systems.Add(created);
        return created;
    }

    public static LaunchLayout Defaults()
    {
        var layout = new LaunchLayout();
        foreach (var profile in SystemCatalog.All)
            layout.Systems.Add(FromProfile(profile, layout.DefaultPreset));
        return layout;
    }

    public static void Reset()
    {
        Current = Defaults();
        Save();
    }

    public static void ApplyDefaultPreset(string presetId)
    {
        Current.DefaultPreset = presetId;
        foreach (var cfg in Current.Systems)
        {
            if (EmulatorLaunch.IsRetroArch(cfg.Emulator))
                LaunchPresets.Apply(cfg, presetId);
        }
    }

    private static bool MigrateTemplates()
    {
        var changed = false;
        if (Current.DefaultPreset is LaunchPresets.EmuDeckScript or LaunchPresets.EmuDeckLauncher
            or LaunchPresets.Wrapper or LaunchPresets.LegacyWrapper or LaunchPresets.FlatpakLine)
        {
            Current.DefaultPreset = LaunchPresets.Flatpak;
            changed = true;
        }

        foreach (var cfg in Current.Systems)
        {
            if (EmulatorLaunch.IsRetroArch(cfg.Emulator) || LaunchComposer.UsesRetroArch(cfg))
            {
                if (IsSimpleRetroArch(cfg)) continue;
                cfg.Emulator = "retroarch";
                EmulatorLaunch.Apply(cfg);
                changed = true;
            }
            else if (string.IsNullOrWhiteSpace(cfg.TargetTemplate))
            {
                EmulatorLaunch.Apply(cfg);
                changed = true;
            }
            else if (LaunchComposer.IsRetroArchTemplate(cfg.TargetTemplate))
            {
                // The emulator selector is authoritative. Repair old configs that
                // kept a RetroArch target after switching to Eden/Yuzu/etc.
                EmulatorLaunch.Apply(cfg);
                changed = true;
            }
        }
        return changed;
    }

    private static bool IsSimpleRetroArch(SystemLaunchConfig cfg)
    {
        var target = (cfg.TargetTemplate ?? "").Trim();
        return target.Equals(EmulatorLaunch.RetroArchTarget, StringComparison.Ordinal) &&
               string.IsNullOrWhiteSpace(cfg.OptionsTemplate);
    }

    private static void MergeCatalog()
    {
        foreach (var profile in SystemCatalog.All)
        {
            var existing = Current.Systems.FirstOrDefault(s =>
                s.SystemId.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Current.Systems.Add(FromProfile(profile, Current.DefaultPreset));
                continue;
            }
            if (string.IsNullOrWhiteSpace(existing.Name))
                existing.Name = profile.Name;
            if (string.IsNullOrWhiteSpace(existing.RomFolder))
                existing.RomFolder = profile.Folders.FirstOrDefault() ?? profile.Id;
            if (string.IsNullOrWhiteSpace(existing.Core))
                existing.Core = profile.Cores.FirstOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(existing.TargetTemplate))
                EmulatorLaunch.Apply(existing);
        }
    }

    private static SystemLaunchConfig FromProfile(SystemProfile profile, string defaultPreset)
    {
        var cfg = new SystemLaunchConfig
        {
            SystemId = profile.Id,
            Name = profile.Name,
            RomFolder = profile.Folders.FirstOrDefault() ?? profile.Id,
            Emulator = profile.Emulators.FirstOrDefault() ?? "retroarch",
            Core = profile.Cores.FirstOrDefault() ?? "",
            Preset = defaultPreset
        };
        EmulatorLaunch.Apply(cfg);
        return cfg;
    }

    private static string FilePath() => AppDataPaths.Combine("launchers.json");
}
