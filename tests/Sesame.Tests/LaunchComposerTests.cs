using Sesame.Services.GameOptimizer;

namespace Sesame.Tests;

public sealed class LaunchComposerTests
{
    [Fact]
    public void Eden_launcher_keeps_executable_and_rom_in_separate_steam_fields()
    {
        var launch = LaunchComposer.ForSteam(
            "/home/deck/Emulation/tools/launchers/eden.sh \"/home/deck/Emulation/roms/switch/Game.nsp\"",
            "/home/deck/Emulation/tools/launchers/",
            "");

        Assert.Equal("\"/home/deck/Emulation/tools/launchers/eden.sh\"", launch.Exe);
        Assert.Equal("\"/home/deck/Emulation/tools/launchers/\"", launch.StartDir);
        Assert.Equal("\"/home/deck/Emulation/roms/switch/Game.nsp\"", launch.LaunchOptions);
    }

    [Fact]
    public void Shortcut_builder_always_writes_the_canonical_steam_fields()
    {
        var game = new Sesame.Models.OptimizerGame
        {
            DisplayName = "Mario Kart",
            RomPath = "/home/deck/Emulation/roms/switch/Mario Kart.nsp",
            Target = "/home/deck/Emulation/tools/launchers/eden.sh \"/home/deck/Emulation/roms/switch/Mario Kart.nsp\"",
            StartDir = "/home/deck/Emulation/tools/launchers/",
            LaunchOptions = ""
        };

        var shortcut = SteamShortcuts.Build(game);

        Assert.Equal("\"/home/deck/Emulation/tools/launchers/eden.sh\"", shortcut.Exe);
        Assert.Equal("\"/home/deck/Emulation/roms/switch/Mario Kart.nsp\"", shortcut.LaunchOptions);
    }

    [Fact]
    public void Unquoted_launcher_path_with_spaces_is_repaired()
    {
        var launch = LaunchComposer.ForSteam(
            "/home/deck/Emulation Tools/launchers/eden.sh \"/home/deck/My Games/Game.nsp\"",
            "",
            "");

        Assert.Equal("\"/home/deck/Emulation Tools/launchers/eden.sh\"", launch.Exe);
        Assert.Equal("\"/home/deck/Emulation Tools/launchers/\"", launch.StartDir);
        Assert.Equal("\"/home/deck/My Games/Game.nsp\"", launch.LaunchOptions);
    }

    [Fact]
    public void RetroArch_command_is_also_written_as_executable_plus_launch_options()
    {
        var launch = LaunchComposer.ForSteam(
            "/usr/bin/flatpak run org.libretro.RetroArch -L \"/cores/snes.so\" \"/roms/Game.sfc\"",
            "/usr/bin/",
            "");

        Assert.Equal("\"/usr/bin/flatpak\"", launch.Exe);
        Assert.Equal("\"/usr/bin/\"", launch.StartDir);
        Assert.Equal("run org.libretro.RetroArch -L \"/cores/snes.so\" \"/roms/Game.sfc\"",
            launch.LaunchOptions);
    }

    [Fact]
    public void Duplicate_arguments_are_not_written_twice()
    {
        var launch = LaunchComposer.ForSteam(
            "/home/deck/Emulation/tools/launchers/eden.sh \"/roms/Game.nsp\"",
            "/home/deck/Emulation/tools/launchers",
            "\"/roms/Game.nsp\"");

        Assert.Equal("\"/roms/Game.nsp\"", launch.LaunchOptions);
    }

    [Fact]
    public void Duplicate_target_lines_are_flattened_before_writing_the_shortcut()
    {
        var launch = LaunchComposer.ForSteam(
            "/home/deck/Emulation/tools/launchers/eden.sh\r\n\"/roms/Game.nsp\"",
            "/home/deck/Emulation/tools/launchers",
            "");

        Assert.Equal("\"/home/deck/Emulation/tools/launchers/eden.sh\"", launch.Exe);
        Assert.Equal("\"/roms/Game.nsp\"", launch.LaunchOptions);
    }

    [Fact]
    public void Explicit_standalone_emulator_wins_over_a_stale_retroarch_preset()
    {
        var cfg = new SystemLaunchConfig
        {
            Emulator = "eden",
            Preset = LaunchPresets.Flatpak
        };

        Assert.False(LaunchComposer.UsesRetroArch(cfg));
    }
}
