using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Sesame.Services.N64;

public static class N64Rom
{
    public static bool LooksLikeN64(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 0x40) return false;
        if (rom[0] == 0x80 && rom[1] == 0x37) return true;
        if (rom[0] == 0x37 && rom[1] == 0x80) return true;
        return rom[0] == 0x40 && rom[1] == 0x12;
    }

    public static byte[] ToZ64(byte[] rom)
    {
        if (rom.Length < 0x40)
            throw new InvalidDataException("File is too small for an N64 ROM.");
        if (rom[0] == (byte)'P' && rom[1] == (byte)'K')
            throw new InvalidDataException(
                "No recognizable N64 ROM (.z64 / .v64 / .n64). These are still zip bytes, not a ROM.");
        if (rom[0] == 0x80 && rom[1] == 0x37) return rom;
        var copy = (byte[])rom.Clone();
        if (rom[0] == 0x37 && rom[1] == 0x80)
        {
            for (var i = 0; i + 1 < copy.Length; i += 2)
                (copy[i], copy[i + 1]) = (copy[i + 1], copy[i]);
            return copy;
        }
        if (rom[0] == 0x40 && rom[1] == 0x12)
        {
            for (var i = 0; i + 3 < copy.Length; i += 4)
            {
                (copy[i], copy[i + 3]) = (copy[i + 3], copy[i]);
                (copy[i + 1], copy[i + 2]) = (copy[i + 2], copy[i + 1]);
            }
            return copy;
        }
        throw new InvalidDataException("No recognizable N64 ROM (.z64 / .v64 / .n64).");
    }

    public static string InternalName(byte[] z64)
    {
        var n = Encoding.ASCII.GetString(z64, 0x20, 20).Trim('\0', ' ');
        return n;
    }

    public static string CartId(byte[] z64) =>
        z64.Length > 0x3F ? Encoding.ASCII.GetString(z64, 0x3B, 4) : "";

    public static bool LooksLikeBanjoKazooie(byte[] z64)
    {
        var id = CartId(z64);
        if (id.StartsWith("NBK", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.StartsWith("NB7", StringComparison.OrdinalIgnoreCase)) return false;
        var name = InternalName(z64);
        if (name.Contains("TOOIE", StringComparison.OrdinalIgnoreCase)) return false;
        return name.Contains("BANJO", StringComparison.OrdinalIgnoreCase);
    }

    public static void RecalcCrc(byte[] z64)
    {
        var cic = DetectCic(z64);
        if (LooksLikeBanjoKazooie(z64)) cic = 6103;
        var (crc1, crc2) = ComputeCrc(z64, cic);
        BinaryPrimitives.WriteUInt32BigEndian(z64.AsSpan(0x10), crc1);
        BinaryPrimitives.WriteUInt32BigEndian(z64.AsSpan(0x14), crc2);
    }

    public static (uint Crc1, uint Crc2) ComputeCrc(byte[] data) =>
        ComputeCrc(data, LooksLikeBanjoKazooie(data) ? 6103 : DetectCic(data));

    private static (uint Crc1, uint Crc2) ComputeCrc(byte[] data, int cic)
    {
        uint seed = cic switch
        {
            6103 => 0xA3886759,
            6105 => 0xDF26F436,
            6106 => 0x1FEA617A,
            _ => 0xF8CA4DDC
        };

        uint t1 = seed, t2 = seed, t3 = seed, t4 = seed, t5 = seed, t6 = seed;
        const int start = 0x1000;
        var end = Math.Min(data.Length, start + 0x100000);
        for (var i = start; i + 3 < end; i += 4)
        {
            var d = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
            if (t6 + d < t6) t4++;
            t6 += d;
            t3 ^= d;
            var r = RotateLeft(d, (int)(d & 0x1F));
            t5 += r;
            if (t2 > d) t2 ^= r;
            else t2 ^= t6 ^ d;

            if (cic == 6105)
            {
                var bootOff = 0x40 + 0x0710 + (i & 0xFF);
                var boot = bootOff + 3 < data.Length
                    ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bootOff))
                    : 0;
                t1 += boot ^ d;
            }
            else t1 += t5 ^ d;
        }

        return cic switch
        {
            6103 => ((t6 ^ t4) + t3, (t5 ^ t2) + t1),
            6106 => ((t6 * t4) + t3, (t5 * t2) + t1),
            _ => (t6 ^ t4 ^ t3, t5 ^ t2 ^ t1)
        };
    }

    private static int DetectCic(byte[] data)
    {
        if (data.Length < 0x1000) return 6102;
        var crc = Crc32(data.AsSpan(0x40, 0xFC0));
        return crc switch
        {
            0x6170A4A1 => 6101,
            0x90BB6CB5 => 6102,
            0x0B050EE0 => 6103,
            0x98BC2C86 => 6105,
            0xACC8580A => 6106,
            _ => 6102
        };
    }

    private static uint RotateLeft(uint value, int bits) =>
        bits == 0 ? value : (value << bits) | (value >> (32 - bits));

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
