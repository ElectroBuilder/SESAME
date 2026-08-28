using System.IO;
using System.Runtime.InteropServices;

namespace Sesame.Services;

/// <summary>
/// Detects Steam Deck / SteamOS and whether SESAME should talk to the machine
/// directly (local files) or over SSH.
/// </summary>
public static class HostEnvironment
{
    public static bool ForceLocal { get; set; }
    public static bool ForceRemote { get; set; }
    public static bool PreferGameModeUi { get; set; }

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static bool IsSteamOs
    {
        get
        {
            try
            {
                if (File.Exists("/usr/bin/steamos-session-select")) return true;
                if (!File.Exists("/etc/os-release")) return false;
                var text = File.ReadAllText("/etc/os-release");
                return text.Contains("steamos", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("ID=holo", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsSteamDeckHardware
    {
        get
        {
            if (string.Equals(Environment.GetEnvironmentVariable("SteamDeck"), "1",
                    StringComparison.OrdinalIgnoreCase))
                return true;
            if (Directory.Exists("/home/deck")) return true;
            return IsSteamOs;
        }
    }

    public static bool LocalAvailable =>
        !ForceRemote && (ForceLocal || (IsLinux && IsSteamDeckHardware));

    public static bool RunningInGamescope
    {
        get
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GAMESCOPE_WAYLAND_DISPLAY")))
                return true;
            var desktop = string.Join(' ',
                Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
                Environment.GetEnvironmentVariable("DESKTOP_SESSION"),
                Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"));
            return desktop.Contains("gamescope", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool UseGameModeUi =>
        PreferGameModeUi || RunningInGamescope;

    public static string Home
    {
        get
        {
            if (Directory.Exists("/home/deck"))
                return "/home/deck";
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                home = Environment.GetEnvironmentVariable("HOME") ?? "/home/deck";
            return home.Replace('\\', '/');
        }
    }

    public static void ApplyArgs(IReadOnlyList<string> args)
    {
        foreach (var raw in args)
        {
            var a = raw.Trim().ToLowerInvariant();
            if (a is "--local" or "-local") ForceLocal = true;
            else if (a is "--remote" or "-remote") ForceRemote = true;
            else if (a is "--gamemode" or "--game-mode" or "-g") PreferGameModeUi = true;
            else if (a is "--desktop" or "-d") PreferGameModeUi = false;
        }
    }

    public static string RuntimeLabel =>
        LocalAvailable
            ? (IsSteamOs ? "Steam Deck" : "Linux (lokaal)")
            : RuntimeInformation.OSDescription;
}
