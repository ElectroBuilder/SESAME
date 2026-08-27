using System.IO;
using System.Text;
using VisualSSH.Services.N64;

namespace VisualSSH.Services;

public static class CartRom
{
    public static bool LooksLikeNes(ReadOnlySpan<byte> data) =>
        data.Length > 16 && data[0] == (byte)'N' && data[1] == (byte)'E' &&
        data[2] == (byte)'S' && data[3] == 0x1A;

    public static bool LooksLikeSnes(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 0x8000 or > 0x800000) return false;
        return HasSnesTitle(data, 0x7FC0) || HasSnesTitle(data, 0xFFC0) ||
               (data.Length > 0x200 + 0x7FC0 && HasSnesTitle(data, 0x200 + 0x7FC0));
    }

    public static string Extension(byte[] rom)
    {
        if (N64Rom.LooksLikeN64(rom)) return ".z64";
        if (LooksLikeNes(rom)) return ".nes";
        if (LooksLikeSnes(rom)) return ".sfc";
        return ".bin";
    }

    public static bool IsSupportedSystem(string? system) =>
        string.Equals(system, "N64", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(system, "NES", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(system, "SNES", StringComparison.OrdinalIgnoreCase);

    private static bool HasSnesTitle(ReadOnlySpan<byte> data, int at)
    {
        if (at + 21 > data.Length) return false;
        var letters = 0;
        for (var i = 0; i < 21; i++)
        {
            var b = data[at + i];
            if (b is >= 0x20 and <= 0x7E) letters++;
            else if (b != 0) return false;
        }
        return letters >= 8;
    }

    public static string Describe(byte[] rom)
    {
        if (N64Rom.LooksLikeN64(rom))
            return N64Rom.InternalName(rom);
        if (LooksLikeNes(rom))
            return "NES";
        if (LooksLikeSnes(rom))
            return "SNES";
        return Encoding.ASCII.GetString(rom, 0, Math.Min(16, rom.Length));
    }
}
