using System.IO;
using System.Text.RegularExpressions;
using VisualSSH.Models;
using VisualSSH.Services.GameOptimizer;

namespace VisualSSH.Services;

public sealed class RomSystemFolder
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Path { get; init; } = "";
    public string SystemId { get; init; } = "";
}

public sealed class RomFileHit
{
    public string SystemFolder { get; init; } = "";
    public string SystemLabel { get; init; } = "";
    public string FileName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string? InnerFileName { get; init; }
}

public static class RomScan
{
    private static readonly Regex TitleId = new(@"0[1-9A-Fa-f][0-9A-Fa-f]{14}", RegexOptions.Compiled);
    private static readonly Regex TrackName = new(
        @"\s*\(\s*Track\s*\d+(\s+of\s+\d+)?\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "downloaded_media", "media", "images", "videos", "screenshots", "manuals",
        "covers", "boxart", "fanart", "marquees", "cache", ".git", "sys", "files",
        "bios", "keys", "cheats", "codes", "saves", "savestates",
        "dolphin", "pcsx2", "duckstation", "retroarch", "ppsspp", "cemu", "ryujinx",
        "yuzu", "eden", "citron", "xemu", "flycast", "mame", "rpcs3", "primehack"
    };

    private static readonly HashSet<string> DiscCompanions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bin", ".img", ".iso", ".wav", ".ccd", ".sub", ".mds", ".mdf", ".raw"
    };

    public static IReadOnlyList<RomSystemFolder> Systems { get; private set; } = [];

    public static IReadOnlyList<RomFileHit> ListFiles(DeckClient client, AppCatalog catalog)
    {
        var folders = new Dictionary<string, RomSystemFolder>(StringComparer.OrdinalIgnoreCase);
        var files = new List<RomFileHit>();

        try
        {
            ParseOutput(WalkPython(client, catalog), folders, files);
        }
        catch
        {
            WalkSftp(client, catalog, folders, files);
        }

        var kept = DropCompanions(files)
            .GroupBy(f => f.FullPath.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        Systems = folders.Values
            .Where(s => kept.Any(f =>
                f.SystemFolder.Equals(s.Key, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return kept;
    }

    public static IEnumerable<RomFileHit> PickPrimary(IEnumerable<RomFileHit> files) =>
        files.GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(f => RomNameCleaner.Rank(Path.GetExtension(f.FileName))).First());

    public static string DisplayCode(string folder)
    {
        var profile = SystemCatalog.FromFolder(folder);
        if (profile is null)
            return string.IsNullOrWhiteSpace(folder) ? "?" : folder.ToUpperInvariant();
        return profile.Id switch
        {
            "ps1" => "PSX",
            "gc" => "GC",
            "genesis" => "GENESIS",
            "switch" => "SWITCH",
            _ => profile.Id.ToUpperInvariant()
        };
    }

    public static string SidebarName(string folder)
    {
        var profile = SystemCatalog.FromFolder(folder);
        if (profile is not null) return profile.Name;
        if (string.IsNullOrWhiteSpace(folder)) return folder;
        return folder.Length <= 5 ? folder.ToUpperInvariant() : char.ToUpper(folder[0]) + folder[1..];
    }

    private static void RememberFolder(Dictionary<string, RomSystemFolder> folders, string key, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = path.Replace('\\', '/').TrimEnd('/');
        var folderName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(folderName)) return;
        if (SkipDirs.Contains(folderName)) return;
        var profile = SystemCatalog.FromFolder(folderName) ?? SystemCatalog.FromFolder(key);
        var id = profile?.Id ?? StoreGame.FoldSystem(folderName);
        if (string.IsNullOrEmpty(id)) id = folderName.ToLowerInvariant();
        if (folders.Values.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;
        folders[id + "|" + path] = new RomSystemFolder
        {
            Key = folderName,
            Label = SidebarName(folderName),
            Path = path,
            SystemId = id
        };
    }

    private static string WalkPython(DeckClient client, AppCatalog catalog)
    {
        const string py =
            "import os,glob,sys,zipfile\n" +
            "skip_dir={'downloaded_media','media','images','videos','screenshots','manuals','covers','boxart','fanart','marquees','cache','.git','sys','files','bios','keys','cheats','codes','saves','savestates','dolphin','pcsx2','duckstation','retroarch','ppsspp','cemu','ryujinx','yuzu','eden','citron','xemu','flycast','mame','rpcs3','primehack'}\n" +
            "skip_ext={'.txt','.xml','.json','.md','.nfo','.jpg','.jpeg','.png','.gif','.webp','.mp4','.mkv','.avi','.srm','.sav','.state','.cfg','.ini','.log','.html','.htm','.pdf','.url','.desktop','.exe','.sh','.bat','.cmd','.appimage','.so','.dll'}\n" +
            "roots=sys.argv[1:]\n" +
            "extra=glob.glob('/run/media/deck/*/Emulation/roms')+glob.glob('/run/media/*/Emulation/roms')\n" +
            "seen=set()\n" +
            "seen_files=set()\n" +
            "def walk(root, sysname, depth):\n" +
            "    root=os.path.realpath(root)\n" +
            "    if not os.path.isdir(root) or root in seen or depth>5: return\n" +
            "    seen.add(root)\n" +
            "    try: names=os.listdir(root)\n" +
            "    except: return\n" +
            "    files=[n for n in names if os.path.isfile(os.path.join(root,n))]\n" +
            "    dirs=[n for n in names if os.path.isdir(os.path.join(root,n))]\n" +
            "    for n in files:\n" +
            "        if n.startswith('.'): continue\n" +
            "        ext=os.path.splitext(n)[1].lower()\n" +
            "        if ext in skip_ext: continue\n" +
            "        full=os.path.realpath(os.path.join(root,n))\n" +
            "        if full in seen_files: continue\n" +
            "        seen_files.add(full)\n" +
            "        inner=''\n" +
            "        if ext=='.zip':\n" +
            "            try:\n" +
            "                z=zipfile.ZipFile(full)\n" +
            "                inner='|'.join(i.filename for i in z.infolist() if not i.is_dir())\n" +
            "            except Exception:\n" +
            "                inner=''\n" +
            "        print('FILE\\t'+sysname+'\\t'+n+'\\t'+full+'\\t'+inner)\n" +
            "    for n in dirs:\n" +
            "        if n.startswith('.') or n.lower() in skip_dir: continue\n" +
            "        walk(os.path.join(root,n), sysname, depth+1)\n" +
            "for r in list(roots)+extra:\n" +
            "    if not os.path.isdir(r): continue\n" +
            "    if os.path.basename(r.rstrip('/'))=='roms':\n" +
            "        for n in os.listdir(r):\n" +
            "            if n.startswith('.') or n.lower() in skip_dir: continue\n" +
            "            p=os.path.realpath(os.path.join(r,n))\n" +
            "            if os.path.isdir(p):\n" +
            "                print('DIR\\t'+n+'\\t'+p)\n" +
            "                walk(p, n, 0)\n" +
            "    else:\n" +
            "        sysname=os.path.basename(r.rstrip('/'))\n" +
            "        print('DIR\\t'+sysname+'\\t'+r)\n" +
            "        walk(r, sysname, 0)\n";

        var roots = RomRoots(catalog);
        var args = string.Join(" ", roots.Select(DeckClient.ShQuote));
        return client.Execute("python3 -c " + DeckClient.ShQuote(py) + " " + args, timeoutSeconds: 60);
    }

    private static IEnumerable<string> RomRoots(AppCatalog catalog)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal)
        {
            "/home/deck/Emulation/roms",
            LaunchConfigStore.Current.RomsRoot
        };
        foreach (var folder in catalog.RomFolders.Values)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var parent = DeckClient.Parent(folder);
            if (!string.IsNullOrEmpty(parent) &&
                Path.GetFileName(parent.TrimEnd('/')).Equals("roms", StringComparison.OrdinalIgnoreCase))
                roots.Add(parent);
            else
                roots.Add(folder);
        }
        return roots.Where(r => !string.IsNullOrWhiteSpace(r));
    }

    private static void ParseOutput(string output, Dictionary<string, RomSystemFolder> folders,
        List<RomFileHit> files)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            if (parts[0].Equals("DIR", StringComparison.OrdinalIgnoreCase))
            {
                RememberFolder(folders, parts[1].Trim(), parts[2].Trim());
                continue;
            }
            if (!parts[0].Equals("FILE", StringComparison.OrdinalIgnoreCase) || parts.Length < 4)
                continue;
            var sys = parts[1].Trim();
            var name = parts[2].Trim();
            var path = parts[3].Trim();
            var profile = SystemCatalog.FromFolder(sys);
            if (!RomNameCleaner.IsRomFile(name, profile)) continue;
            var inner = parts.Length >= 5 ? parts[4].Trim() : "";
            var innerFile = string.IsNullOrEmpty(inner)
                ? null
                : RomContainer.BestInnerName(inner.Split('|', StringSplitOptions.RemoveEmptyEntries),
                    Path.GetFileNameWithoutExtension(name));
            files.Add(new RomFileHit
            {
                SystemFolder = sys,
                SystemLabel = DisplayCode(sys),
                FileName = name,
                FullPath = path.Replace('\\', '/'),
                InnerFileName = string.IsNullOrEmpty(innerFile) ? null : Path.GetFileName(innerFile.Replace('\\', '/'))
            });
        }
    }

    private static void WalkSftp(DeckClient client, AppCatalog catalog,
        Dictionary<string, RomSystemFolder> folders, List<RomFileHit> files)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in RomRoots(catalog))
        {
            if (!client.Exists(root) || !seen.Add(root)) continue;
            var baseName = Path.GetFileName(root.TrimEnd('/'));
            if (string.Equals(baseName, "roms", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in client.List(root))
                {
                    if (!item.IsDirectory || SkipDirs.Contains(item.Name) || item.Name.StartsWith('.'))
                        continue;
                    RememberFolder(folders, item.Name, item.FullPath);
                    WalkSftpFolder(client, item.FullPath, item.Name, 0, seen, files);
                }
            }
            else
            {
                RememberFolder(folders, baseName, root);
                WalkSftpFolder(client, root, baseName, 0, seen, files);
            }
        }
    }

    private static void WalkSftpFolder(DeckClient client, string folder, string sysname, int depth,
        HashSet<string> seen, List<RomFileHit> files)
    {
        if (depth > 5 || !seen.Add(folder) || !client.Exists(folder)) return;
        foreach (var item in client.List(folder))
        {
            if (item.IsDirectory)
            {
                if (SkipDirs.Contains(item.Name) || item.Name.StartsWith('.')) continue;
                WalkSftpFolder(client, item.FullPath, sysname, depth + 1, seen, files);
                continue;
            }
            if (!RomNameCleaner.IsRomFile(item.Name, SystemCatalog.FromFolder(sysname))) continue;
            files.Add(new RomFileHit
            {
                SystemFolder = sysname,
                SystemLabel = DisplayCode(sysname),
                FileName = item.Name,
                FullPath = item.FullPath.Replace('\\', '/')
            });
        }
    }

    private static List<RomFileHit> DropCompanions(List<RomFileHit> files)
    {
        var byDir = files.GroupBy(f => DeckClient.Parent(f.FullPath), StringComparer.OrdinalIgnoreCase);
        var kept = new List<RomFileHit>();
        foreach (var dir in byDir)
        {
            var names = new HashSet<string>(dir.Select(f => f.FileName), StringComparer.OrdinalIgnoreCase);
            var hasM3u = names.Any(n => n.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase));
            var hasCue = names.Any(n => n.EndsWith(".cue", StringComparison.OrdinalIgnoreCase));
            foreach (var file in dir)
            {
                var ext = Path.GetExtension(file.FileName);
                var stem = Path.GetFileNameWithoutExtension(file.FileName);
                if (hasM3u && !ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) &&
                    (DiscCompanions.Contains(ext) ||
                     ext.Equals(".chd", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals(".cue", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (DiscCompanions.Contains(ext) &&
                    (names.Contains(stem + ".cue") || names.Contains(stem + ".m3u") ||
                     (hasCue && TrackName.IsMatch(stem))))
                    continue;
                if (ext.Equals(".chd", StringComparison.OrdinalIgnoreCase) && names.Contains(stem + ".m3u"))
                    continue;
                if (TrackName.IsMatch(stem) && (hasCue || hasM3u))
                    continue;
                kept.Add(file);
            }
        }
        return kept;
    }

    private static string GroupKey(RomFileHit f)
    {
        if (KeepSeparate(f))
            return "solo|" + f.FullPath.Replace('\\', '/');
        return SystemId(f.SystemFolder) + "|" + StemKey(f.FileName);
    }

    private static bool KeepSeparate(RomFileHit f) =>
        StoreGame.LooksLikeTranslation(f.FileName) || RomHackLog.TryGet(f.FullPath, out _);

    private static string SystemId(string folder) =>
        SystemCatalog.FromFolder(folder)?.Id ?? folder.Trim().ToLowerInvariant();

    private static string StemKey(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        stem = TitleId.Replace(stem, "");
        return StoreGame.FoldTitle(RomNameCleaner.Clean(stem));
    }
}
