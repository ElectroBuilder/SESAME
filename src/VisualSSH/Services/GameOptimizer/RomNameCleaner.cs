using System.IO;
using System.Text.RegularExpressions;
using VisualSSH.Models;

namespace VisualSSH.Services.GameOptimizer;

public static class RomNameCleaner
{
    private static readonly Regex TitleId = new(@"0[1-9A-Fa-f][0-9A-Fa-f]{14}", RegexOptions.Compiled);
    private static readonly Regex BracketJunk = new(
        @"\s*[\(\[]\s*(USA|U|EUR|Europe|Japan|JPN|J|World|W|En|En,Fr|En,Es|Rev\s*\d+|Rev\s*[A-Z]|v\s*\d+(\.\d+)?|Unl|Proto|Beta|Sample|Demo|Disc\s*\d+|Disk\s*\d+)\s*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DumpTags = new(
        @"\s*[\(\[][^\)]*(nointro|no-intro|redump|goodset|tosec|trurip)[^\)]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LangList = new(
        @"\s*[\(\[]\s*[A-Za-z]{2}(?:\s*,\s*[A-Za-z]{2})+\s*[\)\]]",
        RegexOptions.Compiled);
    private static readonly Regex TrailingLangList = new(
        @"\s+[A-Za-z]{2}(?:\s*,\s*[A-Za-z]{2})+\s*$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> SkipExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".xml", ".json", ".md", ".nfo", ".jpg", ".jpeg", ".png", ".gif", ".webp",
        ".mp4", ".mkv", ".avi", ".srm", ".sav", ".state", ".auto", ".cht", ".bak", ".cfg",
        ".ini", ".log", ".dsv", ".mcr", ".eep", ".fla", ".mpk", ".sra", ".sbi", ".pnach",
        ".html", ".htm", ".pdf", ".url", ".desktop", ".exe", ".sh", ".bat", ".cmd",
        ".appimage", ".so", ".dll", ".py", ".ps1", ".doc", ".rtf", ".diz", ".sfv",
        ".md5", ".sha1", ".sha256", ".torrent"
    };

    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata.txt", "systeminfo.txt", "gamelist.xml", ".gitkeep", "downloaded_media",
        "readme", "license", "licence", "copying", "changelog", "authors", "credits",
        "notes", "todo", "dolphin", "pcsx2", "pcsx2-qt", "duckstation", "retroarch",
        "ppsspp", "cemu", "ryujinx", "yuzu", "eden", "citron", "xemu", "flycast",
        "mame", "rpcs3", "vita3k", "azahar", "lime3ds", "citra", "drastic", "primehack",
        "emulationstation", "es-de", "emudeck", "steamrommanager", "srm"
    };

    private static readonly string[] SkipNamePrefixes =
        ["readme", "license", "licence", "copying", "changelog"];

    public static bool IsRomFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.StartsWith('.')) return false;
        var name = Path.GetFileName(fileName);
        if (SkipNames.Contains(name)) return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        if (SkipNames.Contains(stem)) return false;
        if (SkipNamePrefixes.Any(p => stem.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return false;
        var ext = Path.GetExtension(name);
        if (ext.Length == 0) return false;
        return !SkipExt.Contains(ext);
    }

    public static bool IsRomFile(string fileName, SystemProfile? profile)
    {
        if (!IsRomFile(fileName)) return false;
        if (profile is null || profile.Extensions.Count == 0) return true;
        return profile.Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
    }

    public static int Rank(string ext) => ext.ToLowerInvariant() switch
    {
        ".m3u" => 0,
        ".cue" => 1,
        ".chd" => 2,
        ".rvz" => 3,
        ".gcz" => 4,
        ".iso" => 5,
        ".wbfs" => 6,
        ".nsp" or ".xci" => 7,
        ".cso" or ".pbp" => 8,
        ".zip" or ".7z" => 20,
        ".bin" or ".img" => 40,
        _ => 10
    };

    public static string Clean(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        stem = TitleId.Replace(stem, "");
        stem = stem.Replace('_', ' ').Replace('.', ' ');
        stem = DumpTags.Replace(stem, "");
        stem = BracketJunk.Replace(stem, "");
        stem = LangList.Replace(stem, "");
        stem = Regex.Replace(stem, @"\s*[\(\[][!bhtf][^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
        stem = Regex.Replace(stem, @"[!_\[\]\(\)]+", " ");
        stem = TrailingLangList.Replace(stem, "");
        stem = Regex.Replace(stem, @"\s+", " ").Trim(" -".ToCharArray());
        var cleaned = StoreGame.CleanTitle(stem);
        return string.IsNullOrWhiteSpace(cleaned) ? stem : cleaned;
    }
}
