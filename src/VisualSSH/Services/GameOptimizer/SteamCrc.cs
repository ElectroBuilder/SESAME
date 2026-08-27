using System.Text;

namespace VisualSSH.Services.GameOptimizer;

public static class SteamCrc
{
    private static readonly uint[] Table = Build();

    public static uint ShortcutId(string exe, string appName)
    {
        var crc = Compute(Encoding.UTF8.GetBytes(exe + appName));
        return crc | 0x80000000u;
    }

    public static string Quote(string path)
    {
        path = (path ?? "").Trim();
        if (path.StartsWith('"') && path.EndsWith('"')) return path;
        return "\"" + path + "\"";
    }

    public static uint Compute(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var j = 0; j < 8; j++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }
}
