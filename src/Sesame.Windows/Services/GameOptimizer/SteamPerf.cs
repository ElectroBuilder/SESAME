using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sesame.Models;

namespace Sesame.Services.GameOptimizer;

public static class SteamPerf
{
    public static void Apply(DeckClient client, uint appId, int fps, int refresh)
    {
        var path = "/home/deck/.local/share/Steam/config/config.vdf";
        if (!client.Exists(path))
            path = "/home/deck/.steam/steam/config/config.vdf";
        if (!client.Exists(path)) return;

        string text;
        try { text = Encoding.UTF8.GetString(client.ReadBytes(path)); }
        catch { return; }

        var blob = BuildBlob(fps, refresh);
        var key = appId.ToString(CultureInfo.InvariantCulture);
        var block = $"\t\t\t\t\"{key}\"\n\t\t\t\t{{\n\t\t\t\t\t\"0\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"0\"\t\t\"{blob}\"\n\t\t\t\t\t}}\n\t\t\t\t}}";

        if (text.Contains($"\"{key}\"", StringComparison.Ordinal))
        {
            text = Regex.Replace(text,
                $@"""{key}""\s*\{{\s*""0""\s*\{{\s*""0""\s*""[0-9a-fA-F]+""\s*\}}\s*\}}",
                block.Trim(),
                RegexOptions.Multiline);
        }
        else if (text.Contains("\"GameProfiles\"", StringComparison.Ordinal))
        {
            var marker = "\"GameProfiles\"";
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            var brace = text.IndexOf('{', at);
            if (brace < 0) return;
            text = text.Insert(brace + 1, "\n" + block);
        }
        else if (text.Contains("\"Perf\"", StringComparison.Ordinal))
        {
            var at = text.IndexOf("\"Perf\"", StringComparison.Ordinal);
            var brace = text.IndexOf('{', at);
            if (brace < 0) return;
            text = text.Insert(brace + 1,
                "\n\t\t\t\"GameProfiles\"\n\t\t\t{\n\t\t\t\t\"App\"\n\t\t\t\t{\n" + block + "\n\t\t\t\t}\n\t\t\t}");
        }
        else
        {
            return;
        }

        client.WriteText(path, text);
    }

    public static void WriteRetroArchCfg(DeckClient client, OptimizerGame game)
    {
        if (!game.IsRetroArch || string.IsNullOrEmpty(game.RetroArchCoreName)) return;
        var dirs = new[]
        {
            "/home/deck/.config/retroarch/config",
            "/home/deck/.var/app/org.libretro.RetroArch/config/retroarch/config"
        };
        var cfg = $"video_vsync = \"true\"\n" +
                  $"video_refresh_rate = \"{game.Fps}\"\n" +
                  "video_max_swapchain_images = \"2\"\n";
        foreach (var root in dirs)
        {
            if (!client.Exists(root) && root.Contains(".config/retroarch", StringComparison.Ordinal))
                client.EnsureDirectory(root);
            if (!client.Exists(DeckClient.Parent(root)) && !client.Exists(root)) continue;
            var folder = DeckClient.Combine(root, game.RetroArchCoreName);
            client.EnsureDirectory(folder);
            var name = Sanitize(game.DisplayName) + ".cfg";
            client.WriteText(DeckClient.Combine(folder, name), cfg);
            return;
        }
    }

    private static string BuildBlob(int fps, int refresh)
    {
        fps = Math.Clamp(fps, 0, 90);
        refresh = Math.Clamp(refresh, 40, 90);
        // Profiel aan, FPS-limiet en refresh, zonder handmatige GPU-clock of TDP.
        return $"08c80110{fps:x2}180020000000300138dc0b4004480550005800600068{refresh:x2}70017801800105";
    }

    private static string Sanitize(string name)
    {
        var n = Regex.Replace(name, @"[<>:""/\\|?*]", "_");
        return n.Trim();
    }
}
