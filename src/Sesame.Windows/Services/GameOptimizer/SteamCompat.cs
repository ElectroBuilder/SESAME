using System.Globalization;
using System.IO;
using Sesame.Models;

namespace Sesame.Services.GameOptimizer;

/// <summary>
/// Non-ROM Windows games (Hydra, Lutris, Other) need a Steam Play tool.
/// That mapping lives in config.vdf CompatToolMapping, not in shortcuts.vdf.
/// </summary>
public static class SteamCompat
{
    public static void Apply(DeckClient client, IEnumerable<OptimizerGame> games)
    {
        var ids = games
            .Where(NeedsProton)
            .Select(g => g.SteamAppId)
            .Where(id => id != 0)
            .Distinct()
            .ToList();
        if (ids.Count == 0) return;

        var tool = ResolveTool(client);
        if (string.IsNullOrEmpty(tool)) return;

        var path = ConfigPath(client);
        if (string.IsNullOrEmpty(path)) return;

        // Shortcut appids always have the high bit set; Steam stores them as signed ints
        // in CompatToolMapping (same as Steam Input / collections). Unsigned keys miss.
        var keys = ids
            .Select(id => unchecked((int)id).ToString(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        try
        {
            var args = DeckClient.ShQuote(path) + " " + DeckClient.ShQuote(tool) + " " +
                       string.Join(" ", keys.Select(DeckClient.ShQuote));
            client.Execute("python3 -c " + DeckClient.ShQuote(PatchPy) + " " + args, 25);
        }
        catch
        {
            /* Proton remains settable by hand in Steam */
        }
    }

    public static bool NeedsProton(OptimizerGame game)
    {
        if (game.IsRom) return false;
        if (game.ShortcutKind == ShortcutKind.App) return false;
        if (LooksWindows(game.Target) || LooksWindows(game.RomPath)) return true;
        if (LooksNativeLinux(game.Target, game.LaunchOptions)) return false;
        return game.ShortcutKind is ShortcutKind.Hydra or ShortcutKind.Game;
    }

    public static string ResolveTool(DeckClient client)
    {
        var installed = ListTools(client);
        var umu = Pick(installed, n => n.Contains("umu", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(umu)) return umu;
        var ge = Pick(installed, n =>
            n.Contains("ge-proton", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("proton-ge", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(ge)) return ge;
        return "proton_experimental";
    }

    private static string Pick(IReadOnlyList<string> names, Func<string, bool> match)
    {
        var hits = names.Where(match).OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return hits.Count == 0 ? "" : hits[0];
    }

    private static List<string> ListTools(DeckClient client)
    {
        try
        {
            var text = client.Execute("python3 -c " + DeckClient.ShQuote(ListPy), 12);
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? ConfigPath(DeckClient client)
    {
        foreach (var path in new[]
                 {
                     "/home/deck/.local/share/Steam/config/config.vdf",
                     "/home/deck/.steam/steam/config/config.vdf",
                     "/home/deck/.steam/root/config/config.vdf"
                 })
        {
            if (client.Exists(path)) return path;
        }

        return null;
    }

    private static bool LooksWindows(string? path)
    {
        var ext = Path.GetExtension(LaunchComposer.ExePath(path ?? "")).ToLowerInvariant();
        return ext is ".exe" or ".bat" or ".cmd" or ".msi" or ".com";
    }

    private static bool LooksNativeLinux(string? target, string? options)
    {
        var hay = ((target ?? "") + " " + (options ?? "")).Replace('\\', '/').ToLowerInvariant();
        if (hay.Contains("flatpak") || hay.Contains(".appimage") || hay.Contains("lutris:rungame"))
            return true;
        var exe = LaunchComposer.ExePath(target ?? "").Replace('\\', '/');
        var name = Path.GetFileName(exe);
        if (name.Equals("lutris", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("umu-run", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("wine", StringComparison.OrdinalIgnoreCase))
            return true;
        var ext = Path.GetExtension(exe).ToLowerInvariant();
        return ext is ".sh" or ".desktop";
    }

    private const string ListPy =
        "import os,re,glob\n" +
        "home=os.path.expanduser('~')\n" +
        "roots=[os.path.join(home,'.local','share','Steam','compatibilitytools.d')," +
        "os.path.join(home,'.steam','root','compatibilitytools.d')," +
        "os.path.join(home,'.steam','steam','compatibilitytools.d')]\n" +
        "seen=set()\n" +
        "for root in roots:\n" +
        "    if not os.path.isdir(root): continue\n" +
        "    for name in os.listdir(root):\n" +
        "        p=os.path.join(root,name,'compatibilitytool.vdf')\n" +
        "        if not os.path.isfile(p): continue\n" +
        "        try: text=open(p,encoding='utf-8',errors='ignore').read()\n" +
        "        except Exception: continue\n" +
        "        ids=re.findall(r'\"compat_tools\"\\s*\\{[^{]*\"([^\"]+)\"\\s*\\{',text)\n" +
        "        if not ids: ids=[name]\n" +
        "        for i in ids:\n" +
        "            if i.lower() in ('compat_tools','compatibilitytools'): continue\n" +
        "            if i not in seen:\n" +
        "                seen.add(i); print(i)\n";

    private const string PatchPy =
        "import re,sys\n" +
        "path,tool=sys.argv[1],sys.argv[2]\n" +
        "keys=sys.argv[3:]\n" +
        "text=open(path,'r',encoding='utf-8',errors='ignore').read()\n" +
        "orig=text\n" +
        "def find_block(text,key,start=0):\n" +
        "    m=re.search(r'\"%s\"\\s*\\{'%re.escape(key),text[start:])\n" +
        "    if not m: return None\n" +
        "    i=start+m.end()-1\n" +
        "    depth=0\n" +
        "    for j in range(i,len(text)):\n" +
        "        if text[j]=='{': depth+=1\n" +
        "        elif text[j]=='}':\n" +
        "            depth-=1\n" +
        "            if depth==0: return (start+m.start(), j+1, text[i+1:j])\n" +
        "    return None\n" +
        "def upsert(inner,key,tool):\n" +
        "    block='\\n\\t\\t\\t\\t\"%s\"\\n\\t\\t\\t\\t{\\n\\t\\t\\t\\t\\t\"name\"\\t\\t\"%s\"\\n\\t\\t\\t\\t\\t\"config\"\\t\\t\"\"\\n\\t\\t\\t\\t\\t\"priority\"\\t\\t\"250\"\\n\\t\\t\\t\\t}'%(key,tool)\n" +
        "    m=re.search(r'\"%s\"\\s*\\{'%re.escape(key),inner)\n" +
        "    if not m: return inner+block\n" +
        "    i=m.end()-1\n" +
        "    depth=0\n" +
        "    for j in range(i,len(inner)):\n" +
        "        if inner[j]=='{': depth+=1\n" +
        "        elif inner[j]=='}':\n" +
        "            depth-=1\n" +
        "            if depth==0:\n" +
        "                return inner[:m.start()]+block.strip()+'\\n'+inner[j+1:]\n" +
        "    return inner+block\n" +
        "hit=find_block(text,'CompatToolMapping')\n" +
        "if hit is None:\n" +
        "    steam=find_block(text,'Steam')\n" +
        "    if steam is None: sys.exit(0)\n" +
        "    inner='\\n\\t\\t\\t\"CompatToolMapping\"\\n\\t\\t\\t{\\n\\t\\t\\t}\\n'\n" +
        "    text=text[:steam[1]-1]+inner+text[steam[1]-1:]\n" +
        "    hit=find_block(text,'CompatToolMapping')\n" +
        "    if hit is None: sys.exit(0)\n" +
        "s,e,inner=hit\n" +
        "for k in keys:\n" +
        "    inner=upsert(inner,k,tool)\n" +
        "text=text[:s]+'\"CompatToolMapping\"\\n\\t\\t\\t{'+inner+'\\n\\t\\t\\t}'+text[e:]\n" +
        "if text!=orig:\n" +
        "    open(path,'w',encoding='utf-8',newline='\\n').write(text)\n";
}
