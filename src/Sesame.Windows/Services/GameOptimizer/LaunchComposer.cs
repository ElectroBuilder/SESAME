using System.IO;
using System.Text.RegularExpressions;
using Sesame.Models;

namespace Sesame.Services.GameOptimizer;

public static class LaunchComposer
{
    public static void Bind(OptimizerGame game, SystemProfile profile, EmulatorLayout layout)
    {
        if (game.LaunchLocked && ShouldKeepLaunch(game.Target, game.LaunchOptions) &&
            !DolphinInput.NeedsRebind(game, profile))
            return;
        game.LaunchLocked = false;

        var cfg = LaunchConfigStore.ForSystem(profile.Id);
        var retro = UsesRetroArch(cfg);
        EmulatorTarget? standalone = null;
        if (!retro)
        {
            standalone = EmulatorProbe.ResolveStandalone(cfg.Emulator, game.RomPath, layout)
                         ?? EmulatorProbe.ResolveStandalone(profile.Id, game.RomPath, layout)
                         ?? profile.Emulators
                             .Where(id => !id.Equals("retroarch", StringComparison.OrdinalIgnoreCase))
                             .Select(id => EmulatorProbe.ResolveStandalone(id, game.RomPath, layout))
                             .FirstOrDefault(t => t is not null);
        }

        var coreFile = CoreFileName(cfg.Core, profile);
        var corePath = ResolveCorePath(coreFile, layout);
        var exe = standalone?.Exe ?? "";
        var exeDir = string.IsNullOrEmpty(exe) ? "" : DeckClient.Parent(exe);
        var rom = SteamArg(game.RomPath);

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rom"] = rom,
            ["romDir"] = DeckClient.Parent(game.RomPath),
            ["file"] = game.FileName,
            ["system"] = profile.Id,
            ["folder"] = string.IsNullOrWhiteSpace(cfg.RomFolder) ? profile.Id : cfg.RomFolder,
            ["roms"] = TrimSlash(LaunchConfigStore.Current.RomsRoot),
            ["tools"] = TrimSlash(LaunchConfigStore.Current.ToolsRoot),
            ["launchers"] = TrimSlash(LaunchConfigStore.Current.LaunchersRoot),
            ["flatpak"] = LaunchConfigStore.Current.Flatpak,
            ["app"] = LaunchConfigStore.Current.RetroArchApp,
            ["core"] = corePath,
            ["coreFile"] = coreFile,
            ["corePath"] = corePath,
            ["coreHint"] = CoreHint(profile.Id),
            ["emulator"] = EmulatorLaunch.Args(cfg.Emulator),
            ["exe"] = exe,
            ["exeDir"] = string.IsNullOrEmpty(exeDir)
                ? TrimSlash(LaunchConfigStore.Current.LaunchersRoot)
                : exeDir
        };

        var target = Expand(cfg.TargetTemplate, tokens);
        var startDir = Expand(cfg.StartDirTemplate, tokens);
        var options = Expand(cfg.OptionsTemplate, tokens);

        if (string.IsNullOrWhiteSpace(target) && standalone is not null)
        {
            target = standalone.Exe;
            startDir = standalone.StartDir;
            options = SteamCrc.Quote(rom);
        }

        var steam = ForSteam(target, startDir, options);
        game.Target = steam.Exe;
        game.StartDir = steam.StartDir;
        game.LaunchOptions = steam.LaunchOptions;
        game.IsRetroArch = retro;
        game.CorePath = corePath;
        game.RetroArchCoreName = Path.GetFileNameWithoutExtension(coreFile)
            .Replace("_libretro", "", StringComparison.OrdinalIgnoreCase);
        game.EmulatorName = game.IsRetroArch
            ? "RetroArch · " + game.RetroArchCoreName.Replace('_', ' ')
            : string.IsNullOrEmpty(standalone?.Name) ? cfg.Emulator : standalone.Name;

        if (DolphinInput.UsesDolphin(profile))
            DolphinInput.Bind(game);
    }

    public static (string Exe, string StartDir, string LaunchOptions) ForSteam(
        string target, string startDir, string options)
    {
        target = (target ?? "").Trim();
        options = (options ?? "").Trim();
        startDir = TrimSlash((startDir ?? "").Trim().Trim('"'));

        var (exePath, args) = SplitCommand(target);
        exePath = StripQuotes(exePath);
        // Repair targets split on spaces inside a path ("/home/.../Black" Jacket/game.exe).
        if (LooksLikePathContinuation(exePath, args))
        {
            exePath = (exePath + " " + args).Trim();
            args = "";
        }

        if (string.IsNullOrEmpty(exePath))
            return ("", string.IsNullOrEmpty(startDir) ? "" : SteamCrc.Quote(WithSlash(startDir)), "");

        if (string.IsNullOrEmpty(startDir))
            startDir = ParentOf(exePath);
        var start = SteamCrc.Quote(WithSlash(startDir));

        // Game Mode treats Exe as the binary and LaunchOptions as arguments.
        // Proton also needs a real .exe in Exe — extra flags belong in LaunchOptions.
        if (DolphinInput.IsBound(exePath) || DolphinInput.IsBound(target) ||
            DolphinInput.IsBound(args) || DolphinInput.IsBound(options) ||
            IsWindowsExe(exePath))
        {
            var extra = args;
            if (!string.IsNullOrEmpty(options))
                extra = string.IsNullOrEmpty(extra) ? options : extra + " " + options;
            return (SteamCrc.Quote(exePath), start, extra);
        }

        if (!string.IsNullOrEmpty(options))
            args = string.IsNullOrEmpty(args) ? options : args + " " + options;

        var exeField = SteamCrc.Quote(exePath);
        if (!string.IsNullOrEmpty(args))
            exeField += " " + args;
        return (exeField, start, "");
    }

    public static string ExePath(string target) => StripQuotes(FirstToken(target ?? ""));

    private static bool IsWindowsExe(string path)
    {
        var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
        return ext is ".exe" or ".bat" or ".cmd" or ".msi" or ".com";
    }

    public static string Preview(SystemLaunchConfig cfg, string? sampleRom = null)
    {
        var rom = sampleRom ?? TrimSlash(LaunchConfigStore.Current.RomsRoot) + "/" +
                  (string.IsNullOrWhiteSpace(cfg.RomFolder) ? cfg.SystemId : cfg.RomFolder) +
                  "/Game.rom";
        var tokens = BaseTokens(cfg, SteamArg(rom));
        var target = Expand(cfg.TargetTemplate, tokens);
        var start = Expand(cfg.StartDirTemplate, tokens);
        var options = Expand(cfg.OptionsTemplate, tokens);
        var steam = ForSteam(target, start, options);
        return "Target: " + steam.Exe +
               "\nStart in: " + steam.StartDir +
               "\nStartopties: " + (string.IsNullOrEmpty(steam.LaunchOptions) ? "(leeg)" : steam.LaunchOptions);
    }

    public static bool UsesRetroArch(SystemLaunchConfig cfg) =>
        EmulatorLaunch.IsRetroArch(cfg.Emulator) ||
        cfg.Preset is LaunchPresets.Flatpak or LaunchPresets.FlatpakLine
            or LaunchPresets.EmuDeckLauncher or LaunchPresets.EmuDeckScript
            or LaunchPresets.Wrapper or LaunchPresets.LegacyWrapper;

    public static bool NeedsWrapper(SystemLaunchConfig cfg) =>
        cfg.Preset.Equals(LaunchPresets.Wrapper, StringComparison.OrdinalIgnoreCase) ||
        cfg.Preset.Equals(LaunchPresets.LegacyWrapper, StringComparison.OrdinalIgnoreCase);

    public static bool ShouldKeepLaunch(string? target, string? options = null)
    {
        if (IsLegacyScript(target, options)) return false;
        var hay = ((target ?? "") + " " + (options ?? "")).Trim();
        if (string.IsNullOrEmpty(hay)) return false;
        if (hay.Contains("--command=retroarch", StringComparison.OrdinalIgnoreCase) ||
            hay.Contains("--branch=stable", StringComparison.OrdinalIgnoreCase))
            return false;
        if (hay.Contains("run org.libretro.RetroArch", StringComparison.OrdinalIgnoreCase))
            return true;
        return !hay.Contains("org.libretro.RetroArch", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVisualSshLaunch(string? target, string? options = null)
    {
        var hay = ((target ?? "") + " " + (options ?? "")).Replace('\\', '/');
        return hay.Contains("/sesame-", StringComparison.OrdinalIgnoreCase) ||
               hay.Contains("/vssh-", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLegacyScript(string? target, string? options = null)
    {
        var hay = ((target ?? "") + " " + (options ?? "")).Replace('\\', '/');
        if (hay.Contains("/sesame-", StringComparison.OrdinalIgnoreCase)) return false;
        if (!hay.Contains("/vssh-", StringComparison.OrdinalIgnoreCase)) return false;
        return !DolphinInput.IsBound(hay);
    }

    private static Dictionary<string, string> BaseTokens(SystemLaunchConfig cfg, string rom)
    {
        var core = string.IsNullOrWhiteSpace(cfg.Core) ? "snes9x_libretro.so" : cfg.Core;
        if (!core.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
            core += "_libretro.so";
        var corePath = "/home/deck/.var/app/org.libretro.RetroArch/config/retroarch/cores/" + core;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rom"] = rom,
            ["romDir"] = DeckClient.Parent(rom.Trim('"')),
            ["file"] = Path.GetFileName(rom.Trim('"')),
            ["system"] = cfg.SystemId,
            ["folder"] = cfg.RomFolder,
            ["roms"] = TrimSlash(LaunchConfigStore.Current.RomsRoot),
            ["tools"] = TrimSlash(LaunchConfigStore.Current.ToolsRoot),
            ["launchers"] = TrimSlash(LaunchConfigStore.Current.LaunchersRoot),
            ["flatpak"] = LaunchConfigStore.Current.Flatpak,
            ["app"] = LaunchConfigStore.Current.RetroArchApp,
            ["core"] = corePath,
            ["coreFile"] = core,
            ["corePath"] = corePath,
            ["coreHint"] = CoreHint(cfg.SystemId),
            ["emulator"] = EmulatorLaunch.Args(cfg.Emulator),
            ["exe"] = "{exe}",
            ["exeDir"] = TrimSlash(LaunchConfigStore.Current.LaunchersRoot)
        };
    }

    private static string Expand(string template, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var text = Regex.Replace(template, @"\{([A-Za-z]+)\}", m =>
            tokens.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);
        return Regex.Replace(text, @"[ \t]{2,}", " ").Trim();
    }

    private static string CoreFileName(string configured, SystemProfile profile)
    {
        var core = configured;
        if (string.IsNullOrWhiteSpace(core))
            core = profile.Cores.FirstOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(core))
            return "";
        if (!core.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
            core += "_libretro.so";
        return Path.GetFileName(core);
    }

    private static string ResolveCorePath(string coreFile, EmulatorLayout layout)
    {
        if (string.IsNullOrEmpty(coreFile)) return "";
        if (layout.Cores.TryGetValue(coreFile, out var path) && !string.IsNullOrEmpty(path))
            return path.Replace('\\', '/');
        var preferred = "/home/deck/.var/app/org.libretro.RetroArch/config/retroarch/cores/" + coreFile;
        foreach (var dir in layout.CoreDirs)
        {
            var candidate = DeckClient.Combine(dir, coreFile);
            if (candidate.Contains("/.var/app/org.libretro.RetroArch/", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return layout.CoreDirs.Count > 0
            ? DeckClient.Combine(layout.CoreDirs[0], coreFile)
            : preferred;
    }

    private static string CoreHint(string systemId) => systemId.ToLowerInvariant() switch
    {
        "gb" => "gameboy",
        "gbc" => "gbc",
        "gba" => "gba",
        "nes" => "nes",
        "snes" => "snes",
        "n64" => "n64",
        "nds" => "nds",
        "ps1" => "psx",
        "psp" => "psp",
        "genesis" => "genesis",
        "sms" => "mastersystem",
        "saturn" => "saturn",
        "dc" => "dreamcast",
        "arcade" => "mame",
        _ => systemId
    };

    private static (string exe, string rest) SplitCommand(string target)
    {
        target = (target ?? "").Trim();
        if (string.IsNullOrEmpty(target)) return ("", "");
        if (target.StartsWith('"'))
        {
            var end = target.IndexOf('"', 1);
            if (end > 0)
                return (target[1..end], target[(end + 1)..].Trim());
        }

        // Absolute path with spaces: /home/deck/Hydra/Black Jacket/BlackJacket.exe
        if (IsSingleAbsolutePath(target))
            return (target, "");

        var space = target.IndexOf(' ');
        if (space < 0) return (target, "");
        return (target[..space], target[(space + 1)..].Trim());
    }

    private static bool IsSingleAbsolutePath(string target)
    {
        if (string.IsNullOrEmpty(target)) return false;
        if (target.Contains(" -", StringComparison.Ordinal) ||
            target.Contains(" --", StringComparison.Ordinal))
            return false;
        var unix = target.StartsWith('/');
        var win = target.Length >= 3 && char.IsLetter(target[0]) && target[1] == ':' &&
                  (target[2] == '/' || target[2] == '\\');
        if (!unix && !win) return false;
        var ext = Path.GetExtension(target.Replace('\\', '/'));
        return ext.Length is >= 2 and <= 8;
    }

    private static bool LooksLikePathContinuation(string exe, string rest)
    {
        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(rest)) return false;
        if (rest.StartsWith('-')) return false;
        if (!exe.Contains('/') && !exe.Contains('\\')) return false;
        return IsWindowsExe(exe + " " + rest) || IsSingleAbsolutePath(exe + " " + rest);
    }

    private static string FirstToken(string target)
    {
        var (exe, _) = SplitCommand(target);
        return exe;
    }

    private static string StripQuotes(string value)
    {
        value = (value ?? "").Trim();
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
            return value[1..^1];
        return value;
    }

    private static string SteamArg(string path)
    {
        path = (path ?? "").Replace('\\', '/').Trim();
        if (path.StartsWith('"') && path.EndsWith('"') && path.Length >= 2)
            path = path[1..^1];
        return path.Replace("%", "%%", StringComparison.Ordinal);
    }

    private static string ParentOf(string path)
    {
        var parent = DeckClient.Parent(path);
        return string.IsNullOrEmpty(parent) ? "/usr/bin" : parent;
    }

    private static string WithSlash(string path)
    {
        path = TrimSlash(path);
        return string.IsNullOrEmpty(path) ? "/" : path + "/";
    }

    private static string TrimSlash(string path) => (path ?? "").Trim().TrimEnd('/');
}
