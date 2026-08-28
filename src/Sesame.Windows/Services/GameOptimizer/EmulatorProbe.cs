using Sesame.Models;

namespace Sesame.Services.GameOptimizer;

public sealed class EmulatorLayout
{
    public Dictionary<string, string> Launchers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Cores { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CoreDirs { get; } = new();
    public List<string> Applications { get; } = new();
    public List<string> Flatpaks { get; } = new();
}

public static class EmulatorProbe
{
    public const string WrapperDir = "/home/deck/Emulation/tools/launchers";

    private static readonly Dictionary<string, string[]> LauncherNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["retroarch"] = ["retroarch.sh", "retroarch"],
        ["nes"] = ["nes.sh", "famicom.sh"],
        ["snes"] = ["snes.sh", "sfc.sh"],
        ["n64"] = ["n64.sh", "nintendo64.sh"],
        ["gba"] = ["gba.sh"],
        ["gb"] = ["gb.sh"],
        ["gbc"] = ["gbc.sh"],
        ["nds"] = ["nds.sh"],
        ["psx"] = ["psx.sh", "ps1.sh", "ps.sh"],
        ["ps1"] = ["psx.sh", "ps1.sh", "ps.sh", "duckstation.sh"],
        ["ps2"] = ["ps2.sh", "pcsx2-qt.sh", "pcsx2.sh"],
        ["ps3"] = ["ps3.sh", "rpcs3.sh"],
        ["dolphin"] = ["dolphin.sh", "dolphin-emu.sh", "gc.sh", "wii.sh", "primehack.sh"],
        ["gc"] = ["gc.sh", "dolphin.sh", "dolphin-emu.sh"],
        ["wii"] = ["wii.sh", "dolphin.sh", "dolphin-emu.sh"],
        ["eden"] = ["eden.sh"],
        ["yuzu"] = ["yuzu.sh"],
        ["ryujinx"] = ["ryujinx.sh"],
        ["citron"] = ["citron.sh"],
        ["cemu"] = ["cemu.sh"],
        ["ppsspp"] = ["ppsspp.sh"],
        ["pcsx2"] = ["pcsx2-qt.sh", "pcsx2.sh"],
        ["duckstation"] = ["duckstation.sh"],
        ["rpcs3"] = ["rpcs3.sh"],
        ["flycast"] = ["flycast.sh"],
        ["xemu"] = ["xemu.sh"],
        ["vita3k"] = ["vita3k.sh"],
        ["citra"] = ["citra.sh"],
        ["lime3ds"] = ["lime3ds.sh"],
        ["azahar"] = ["azahar.sh"],
        ["mame"] = ["mame.sh"],
        ["drastic"] = ["drastic.sh"]
    };

    public static EmulatorLayout Probe(DeckClient client)
    {
        const string py =
            "import os,glob\n" +
            "def emit(k,p):\n" +
            "    if os.path.isfile(p) or os.path.isdir(p): print(k+'\\t'+p)\n" +
            "for p in glob.glob('/home/deck/Emulation/tools/launchers/*')+" +
            "glob.glob('/home/deck/Emulation/tools/launchers/**/*', recursive=True):\n" +
            "    if os.path.isfile(p): emit('LAUNCHER',p)\n" +
            "for p in glob.glob('/home/deck/Applications/*'):\n" +
            "    emit('APP',p)\n" +
            "for d in ['/home/deck/.var/app/org.libretro.RetroArch/config/retroarch/cores',\n" +
            " '/home/deck/.config/retroarch/cores',\n" +
            " '/var/lib/flatpak/app/org.libretro.RetroArch/current/active/files/share/libretro/cores']:\n" +
            "    if os.path.isdir(d):\n" +
            "        emit('COREDIR',d)\n" +
            "        for c in os.listdir(d):\n" +
            "            if c.endswith('.so'): emit('CORE', os.path.join(d,c))\n";

        var layout = new EmulatorLayout();
        string output;
        try
        {
            output = client.Execute("python3 -c " + DeckClient.ShQuote(py), timeoutSeconds: 20);
        }
        catch
        {
            return layout;
        }

        foreach (var line in output.Split('\n'))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var kind = line[..tab].Trim();
            var path = line[(tab + 1)..].Trim();
            if (string.IsNullOrEmpty(path)) continue;
            switch (kind)
            {
                case "LAUNCHER":
                    layout.Launchers[PathName(path)] = path;
                    break;
                case "APP":
                    layout.Applications.Add(path);
                    break;
                case "COREDIR":
                    layout.CoreDirs.Add(path);
                    break;
                case "CORE":
                    layout.Cores[PathName(path)] = path;
                    break;
            }
        }

        try
        {
            var flats = client.Execute("flatpak list --app --columns=application 2>/dev/null", 15);
            foreach (var line in flats.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                layout.Flatpaks.Add(line.Trim());
        }
        catch
        {
            /* flatpak ontbreekt */
        }

        return layout;
    }

    public static EmulatorTarget? Resolve(SystemProfile system, string romPath, EmulatorLayout layout)
    {
        if (system.Emulators.Contains("retroarch", StringComparer.OrdinalIgnoreCase))
            return RetroArchWrapper(system, romPath, layout);

        var systemLauncher = ExactStandalone(system.Id, romPath, layout)
                             ?? system.Folders.Select(f => ExactStandalone(f, romPath, layout))
                                 .FirstOrDefault(t => t is not null);
        if (systemLauncher is not null)
        {
            systemLauncher.Name = system.Name;
            return systemLauncher;
        }

        foreach (var emu in system.Emulators)
        {
            if (emu.Equals("retroarch", StringComparison.OrdinalIgnoreCase))
                continue;
            var target = ExactStandalone(emu, romPath, layout);
            if (target is not null) return target;
        }

        return null;
    }

    public static EmulatorTarget? ResolveStandalone(string id, string romPath, EmulatorLayout layout) =>
        ExactStandalone(id, romPath, layout);

    public static string WrapperPath(string systemId) =>
        DeckClient.Combine(WrapperDir, "sesame-" + systemId.Trim().ToLowerInvariant() + ".sh");

    public static void InstallWrapper(DeckClient client, string systemId, string coreFileName)
    {
        if (string.IsNullOrWhiteSpace(systemId)) return;
        var name = PathName(coreFileName);
        if (string.IsNullOrWhiteSpace(name))
            name = DefaultCore(systemId);
        if (!name.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
            name += "_libretro.so";
        var path = WrapperPath(systemId);
        client.EnsureDirectory(WrapperDir);
        client.WriteText(path, WrapperScript(name));
        try
        {
            client.Execute("chmod +x " + DeckClient.ShQuote(path) + " ; sed -i 's/\\r$//' " +
                           DeckClient.ShQuote(path), 8);
        }
        catch
        {
            /* Steam kan /bin/bash als fallback gebruiken */
        }
    }

    private static EmulatorTarget RetroArchWrapper(SystemProfile system, string romPath, EmulatorLayout layout)
    {
        var core = ResolveCoreFileName(system, layout);
        if (core.Length == 0)
            core = system.Cores.FirstOrDefault() ?? DefaultCore(system.Id);

        return new EmulatorTarget
        {
            Id = "retroarch",
            Name = "RetroArch · " + CoreLabel(core),
            Exe = WrapperPath(system.Id),
            StartDir = WrapperDir,
            LaunchOptions = SteamCrc.Quote(romPath),
            IsRetroArch = true,
            CorePath = core,
            CoreName = CoreFileName(system)
        };
    }

    private static string ResolveCoreFileName(SystemProfile system, EmulatorLayout layout)
    {
        foreach (var name in system.Cores)
        {
            if (layout.Cores.ContainsKey(name)) return name;
        }
        return system.Cores.FirstOrDefault() ?? DefaultCore(system.Id);
    }

    private static string CoreFileName(SystemProfile system)
    {
        var core = system.Cores.FirstOrDefault() ?? DefaultCore(system.Id);
        return core.Replace("_libretro.so", "", StringComparison.OrdinalIgnoreCase);
    }

    private static EmulatorTarget? ExactStandalone(string id, string romPath, EmulatorLayout layout)
    {
        var exe = FindLauncher(layout, id);
        if (exe is null) return null;
        if (PathName(exe).StartsWith("sesame-", StringComparison.OrdinalIgnoreCase) ||
            PathName(exe).StartsWith("vssh-", StringComparison.OrdinalIgnoreCase))
            return null;
        return new EmulatorTarget
        {
            Id = id,
            Name = Label(id),
            Exe = exe,
            StartDir = DeckClient.Parent(romPath),
            LaunchOptions = SteamCrc.Quote(romPath)
        };
    }

    private static string? FindLauncher(EmulatorLayout layout, string id)
    {
        if (!LauncherNames.TryGetValue(id, out var names))
            names = [id + ".sh", id];
        foreach (var name in names)
        {
            if (layout.Launchers.TryGetValue(name, out var path) && IsExactLauncher(path, name))
                return path;
        }
        return null;
    }

    private static bool IsExactLauncher(string path, string name)
    {
        var file = PathName(path);
        return file.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static string DefaultCore(string systemId) => systemId.ToLowerInvariant() switch
    {
        "nes" => "nestopia_libretro.so",
        "snes" => "snes9x_libretro.so",
        "n64" => "mupen64plus_next_libretro.so",
        "gba" => "mgba_libretro.so",
        "gb" => "gambatte_libretro.so",
        "gbc" => "gambatte_libretro.so",
        "nds" => "melonds_libretro.so",
        "ps1" or "psx" => "pcsx_rearmed_libretro.so",
        _ => "nestopia_libretro.so"
    };

    private static string WrapperScript(string coreFileName) =>
        "#!/bin/bash\n" +
        "stripq() { local s=\"$1\"; s=\"${s#\\\"}\"; s=\"${s%\\\"}\"; s=\"${s#\\'}\"; s=\"${s%\\'}\"; printf '%s' \"$s\"; }\n" +
        "ROM=\"\"\n" +
        "HINT=\"\"\n" +
        "prev=\"\"\n" +
        "for a in \"$@\"; do\n" +
        "  a=$(stripq \"$a\")\n" +
        "  case \"$a\" in\n" +
        "    -L|--libretro) prev=L; continue ;;\n" +
        "  esac\n" +
        "  if [ \"$prev\" = L ]; then HINT=\"$a\"; prev=\"\"; continue; fi\n" +
        "  ROM=\"$a\"\n" +
        "done\n" +
        "if [ -z \"$ROM\" ]; then echo \"SESAME: geen ROM\" >&2; exit 1; fi\n" +
        "CORE_NAME=" + DeckClient.ShQuote(coreFileName) + "\n" +
        "if [ -n \"$HINT\" ]; then\n" +
        "  case \"$HINT\" in\n" +
        "    *.so) CORE_NAME=$(basename \"$HINT\") ;;\n" +
        "    *) CORE_NAME=\"${HINT}_libretro.so\" ;;\n" +
        "  esac\n" +
        "fi\n" +
        "CORE=\"\"\n" +
        "for d in \\\n" +
        "  \"$HOME/.var/app/org.libretro.RetroArch/config/retroarch/cores\" \\\n" +
        "  \"$HOME/.config/retroarch/cores\" \\\n" +
        "  \"/var/lib/flatpak/app/org.libretro.RetroArch/current/active/files/share/libretro/cores\"\n" +
        "do\n" +
        "  if [ -f \"$d/$CORE_NAME\" ]; then CORE=\"$d/$CORE_NAME\"; break; fi\n" +
        "done\n" +
        "if [ -z \"$CORE\" ] && [ -f \"$HINT\" ]; then CORE=\"$HINT\"; fi\n" +
        "if [ -z \"$CORE\" ]; then echo \"SESAME: core $CORE_NAME niet gevonden\" >&2; exit 1; fi\n" +
        "export HOME=\"${HOME:-/home/deck}\"\n" +
        "exec /usr/bin/flatpak run --filesystem=host ${CONTROLLER_FLATPAK_ARGS:-} org.libretro.RetroArch -L \"$CORE\" \"$ROM\"\n";

    private static string PathName(string path)
    {
        var i = path.Replace('\\', '/').LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    private static string CoreLabel(string core) =>
        PathName(core).Replace("_libretro.so", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ');

    private static string Label(string id) => id.ToLowerInvariant() switch
    {
        "eden" => "Eden",
        "yuzu" => "Yuzu",
        "ryujinx" => "Ryujinx",
        "dolphin" => "Dolphin",
        "cemu" => "Cemu",
        "ppsspp" => "PPSSPP",
        "pcsx2" => "PCSX2",
        "duckstation" => "DuckStation",
        "flycast" => "Flycast",
        "azahar" => "Azahar",
        "lime3ds" => "Lime3DS",
        "citra" => "Citra",
        "xemu" => "Xemu",
        "vita3k" => "Vita3K",
        "mame" => "MAME",
        _ => id
    };
}
