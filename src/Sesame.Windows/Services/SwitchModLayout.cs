using System.IO;
using System.Text.RegularExpressions;
using Sesame.Models;

namespace Sesame.Services;

public readonly record struct SwitchModJob(string LocalFolder, string FolderName);

public static class SwitchModLayout
{
    private static readonly Regex TitleIdRx = new(@"^0[1-9A-Fa-f][0-9A-Fa-f]{14}$", RegexOptions.Compiled);
    private static readonly HashSet<string> Markers = new(StringComparer.OrdinalIgnoreCase)
    {
        "romfs", "exefs", "exefs_patches", "romfs_patches", "cheats"
    };
    private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase)
    {
        "atmosphere", "contents", "load", "mods", "mod"
    };
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "__macosx", ".git", ".ds_store"
    };
    private static readonly HashSet<string> SkipFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ds_store", "thumbs.db", "desktop.ini"
    };

    public static bool IsSwitch(string? system) =>
        StoreGame.FoldSystem(system ?? "") == "switch";

    public static List<SwitchModJob> Prepare(string downloadedFile, string titleId, string preferredName)
    {
        var prepared = PackStore.PrepareUploadFolder(downloadedFile);
        if (File.Exists(prepared))
        {
            var wrap = prepared + ".modroot";
            if (Directory.Exists(wrap))
                Directory.Delete(wrap, true);
            Directory.CreateDirectory(wrap);
            File.Copy(prepared, Path.Combine(wrap, Path.GetFileName(prepared)), true);
            return [new SwitchModJob(wrap, FolderName(preferredName))];
        }

        var roots = FindModRoots(prepared, titleId, 0);
        if (roots.Count == 0)
            return [new SwitchModJob(prepared, FolderName(preferredName))];

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobs = new List<SwitchModJob>();
        foreach (var root in roots)
        {
            var name = root.Equals(prepared, StringComparison.OrdinalIgnoreCase)
                ? FolderName(preferredName)
                : FolderName(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            if (LooksLikeTitleId(name) || Containers.Contains(name))
                name = FolderName(preferredName);
            name = Unique(name, used);
            jobs.Add(new SwitchModJob(root, name));
        }
        return jobs;
    }

    public static string FolderName(string? title)
    {
        var name = title ?? "";
        foreach (var c in Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']))
            name = name.Replace(c, ' ');
        name = Regex.Replace(name, @"\s+", " ").Trim(' ', '.');
        if (name.Length > 80) name = name[..80].Trim();
        return string.IsNullOrWhiteSpace(name) ? "Mod" : name;
    }

    private static List<string> FindModRoots(string root, string titleId, int depth)
    {
        if (depth > 8) return [root];
        if (LooksLikeModRoot(root)) return [root];

        var dirs = MeaningfulDirs(root);
        var files = MeaningfulFiles(root);

        if (dirs.Count == 1 && files.Count == 0)
        {
            var name = Path.GetFileName(dirs[0]);
            if (IsContainer(name, titleId))
                return FindModRoots(dirs[0], titleId, depth + 1);
            if (LooksLikeModRoot(dirs[0]))
                return [dirs[0]];
            return FindModRoots(dirs[0], titleId, depth + 1);
        }

        var titleDir = dirs.FirstOrDefault(d => IsTitleIdFolder(Path.GetFileName(d), titleId));
        if (titleDir is not null && files.Count == 0)
            return FindModRoots(titleDir, titleId, depth + 1);

        var container = dirs.FirstOrDefault(d => IsContainer(Path.GetFileName(d), titleId));
        if (container is not null && files.Count == 0)
            return FindModRoots(container, titleId, depth + 1);

        var mods = dirs.Where(LooksLikeModRoot).ToList();
        if (mods.Count > 0) return mods;

        if (dirs.Count > 0 && files.Count == 0)
            return dirs;

        return [root];
    }

    private static bool LooksLikeModRoot(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir)
                .Select(Path.GetFileName)
                .Any(name => name is not null && Markers.Contains(name));
        }
        catch
        {
            return false;
        }
    }

    private static List<string> MeaningfulDirs(string root)
    {
        try
        {
            return Directory.GetDirectories(root)
                .Where(d => !SkipDirs.Contains(Path.GetFileName(d)))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<string> MeaningfulFiles(string root)
    {
        try
        {
            return Directory.GetFiles(root)
                .Where(f => !SkipFiles.Contains(Path.GetFileName(f)))
                .Where(f => !Path.GetFileName(f).StartsWith("readme", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).StartsWith("license", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsContainer(string name, string titleId) =>
        Containers.Contains(name) || IsTitleIdFolder(name, titleId);

    private static bool IsTitleIdFolder(string name, string titleId) =>
        !string.IsNullOrEmpty(titleId) && name.Equals(titleId, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeTitleId(string name) => TitleIdRx.IsMatch(name ?? "");

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        var n = 2;
        while (!used.Add(candidate))
            candidate = $"{name} ({n++})";
        return candidate;
    }
}
