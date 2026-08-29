using System.Globalization;

namespace Sesame.Services.GameOptimizer;

/// <summary>
/// Steam Input for non-Steam shortcuts lives in localconfig.vdf (not shortcuts.vdf).
/// UseSteamControllerConfig: 0 = Forced Off, 2 = Forced On.
/// Wii Joy-Cons need Off so Steam does not steal pads from joycond/cemuhook DSU.
/// </summary>
public static class SteamInputConfig
{
    /// <summary>Force Steam Input on (Deck gyro / virtual gamepad path).</summary>
    public static void ForceOn(DeckClient client, IReadOnlyList<string> configDirs,
        IEnumerable<uint> appIds) =>
        Apply(client, configDirs, appIds, forcedOn: true);

    /// <summary>Force Steam Input off (native HID / DSU Joy-Cons for Dolphin Wii).</summary>
    public static void ForceOff(DeckClient client, IReadOnlyList<string> configDirs,
        IEnumerable<uint> appIds) =>
        Apply(client, configDirs, appIds, forcedOn: false);

    private static void Apply(DeckClient client, IReadOnlyList<string> configDirs,
        IEnumerable<uint> appIds, bool forcedOn)
    {
        var keys = appIds
            .Where(id => id != 0)
            .Select(id => unchecked((int)id).ToString(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (keys.Count == 0) return;

        // argv[1] = "0" or "2", argv[2] = path, argv[3…] = app id keys
        var mode = forcedOn ? "2" : "0";
        foreach (var config in configDirs)
        {
            var path = DeckClient.Combine(config, "localconfig.vdf");
            try
            {
                if (!client.Exists(path)) continue;
                var args = DeckClient.ShQuote(mode) + " " + DeckClient.ShQuote(path) + " " +
                           string.Join(" ", keys.Select(DeckClient.ShQuote));
                client.Execute("python3 -c " + DeckClient.ShQuote(PatchPy) + " " + args, 20);
            }
            catch
            {
                /* Steam Input blijft handmatig zetbaar */
            }
        }
    }

    private const string PatchPy =
        "import re,sys\n" +
        "mode=sys.argv[1]\n" +
        "path=sys.argv[2]\n" +
        "keys=sys.argv[3:]\n" +
        "text=open(path,'r',encoding='utf-8',errors='ignore').read()\n" +
        "orig=text\n" +
        "flag='\"UseSteamControllerConfig\"\\t\\t\"%s\"'%mode\n" +
        "def patch_one(text,key):\n" +
        "    m=re.search(r'\"%s\"\\s*\\{'%re.escape(key),text)\n" +
        "    if m:\n" +
        "        i=m.end()\n" +
        "        head=text[i:i+3000]\n" +
        "        if re.search(r'\"UseSteamControllerConfig\"\\s+\"',head):\n" +
        "            head=re.sub(r'\"UseSteamControllerConfig\"\\s+\"[^\"]*\"',flag,head,count=1)\n" +
        "            return text[:i]+head+text[i+3000:]\n" +
        "        return text[:i]+'\\n\\t\\t'+flag+text[i:]\n" +
        "    m=re.search(r'\"UserLocalConfigStore\"\\s*\\{',text)\n" +
        "    if not m: return text\n" +
        "    rest=text[m.end():]\n" +
        "    apps=re.search(r'\"apps\"\\s*\\{',rest)\n" +
        "    block='\\n\\t\\t\"%s\"\\n\\t\\t{\\n\\t\\t\\t%s\\n\\t\\t}'%(key,flag)\n" +
        "    if apps:\n" +
        "        pos=m.end()+apps.end()\n" +
        "        return text[:pos]+block+text[pos:]\n" +
        "    return text[:m.end()]+'\\n\\t\"apps\"\\n\\t{' +block+'\\n\\t}'+text[m.end():]\n" +
        "for k in keys:\n" +
        "    text=patch_one(text,k)\n" +
        "if text!=orig:\n" +
        "    open(path,'w',encoding='utf-8',newline='\\n').write(text)\n";
}
