using System.Buffers.Binary;
using System.IO;

namespace Sesame.Services.N64;

internal static class Mio0
{
    private const int Header = 16;

    public static bool TryDecode(byte[] rom, int offset, out byte[] raw, out int consumed)
    {
        raw = [];
        consumed = 0;
        if (offset < 0 || offset + Header > rom.Length) return false;
        if (rom[offset] != (byte)'M' || rom[offset + 1] != (byte)'I' ||
            rom[offset + 2] != (byte)'O' || rom[offset + 3] != (byte)'0')
            return false;

        var destSize = Be32(rom, offset + 4);
        var compOff = Be32(rom, offset + 8);
        var uncompOff = Be32(rom, offset + 12);
        if (destSize is < 16 or > 0x200000) return false;
        if (compOff < Header || uncompOff < Header) return false;
        if (offset + Math.Max(compOff, uncompOff) >= rom.Length) return false;

        try
        {
            var dest = new byte[destSize];
            var written = 0;
            var bitIdx = 0;
            var compIdx = 0;
            var uncompIdx = 0;
            while (written < destSize)
            {
                var bitAt = offset + Header + (bitIdx / 8);
                if (bitAt >= rom.Length) return false;
                var bit = (rom[bitAt] & (1 << (7 - (bitIdx % 8)))) != 0;
                bitIdx++;
                if (bit)
                {
                    var src = offset + uncompOff + uncompIdx;
                    if (src >= rom.Length) return false;
                    dest[written++] = rom[src];
                    uncompIdx++;
                }
                else
                {
                    var src = offset + compOff + compIdx;
                    if (src + 1 >= rom.Length) return false;
                    var length = ((rom[src] & 0xF0) >> 4) + 3;
                    var back = ((rom[src] & 0x0F) << 8) + rom[src + 1] + 1;
                    compIdx += 2;
                    if (back <= 0 || back > written) return false;
                    for (var i = 0; i < length && written < destSize; i++)
                    {
                        dest[written] = dest[written - back];
                        written++;
                    }
                }
            }

            raw = dest;
            consumed = Math.Max(Header + (bitIdx + 7) / 8, Math.Max(compOff + compIdx, uncompOff + uncompIdx));
            return consumed >= Header && offset + consumed <= rom.Length;
        }
        catch
        {
            return false;
        }
    }

    public static byte[] Encode(byte[] raw)
    {
        var look = new List<int>[256];
        for (var i = 0; i < 256; i++) look[i] = new List<int>();

        var bits = new List<bool>(raw.Length);
        var comp = new List<byte>(raw.Length / 2);
        var literals = new List<byte>(raw.Length);

        void Push(int pos)
        {
            look[raw[pos]].Add(pos);
        }

        if (raw.Length == 0) return [(byte)'M', (byte)'I', (byte)'O', (byte)'0', 0, 0, 0, 0, 0, 0, 0, 16, 0, 0, 0, 16];

        Push(0);
        literals.Add(raw[0]);
        bits.Add(true);
        var pos = 1;
        while (pos < raw.Length)
        {
            var maxLen = Math.Min(18, raw.Length - pos);
            var (matchLen, matchOff) = Longest(raw, pos, maxLen, look[raw[pos]]);
            if (matchLen > 2)
            {
                for (var i = 0; i < matchLen; i++) Push(pos + i);
                var encodedOff = matchOff - 1;
                comp.Add((byte)((((matchLen - 3) & 0x0F) << 4) | ((encodedOff >> 8) & 0x0F)));
                comp.Add((byte)(encodedOff & 0xFF));
                bits.Add(false);
                pos += matchLen;
            }
            else
            {
                Push(pos);
                literals.Add(raw[pos]);
                bits.Add(true);
                pos++;
            }
        }

        var bitBytes = (bits.Count + 7) / 8;
        var compOff = Align(Header + bitBytes, 4);
        var uncompOff = compOff + comp.Count;
        var output = new byte[uncompOff + literals.Count];
        output[0] = (byte)'M';
        output[1] = (byte)'I';
        output[2] = (byte)'O';
        output[3] = (byte)'0';
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), (uint)raw.Length);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(8), (uint)compOff);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(12), (uint)uncompOff);
        for (var i = 0; i < bits.Count; i++)
        {
            if (!bits[i]) continue;
            output[Header + i / 8] |= (byte)(1 << (7 - (i % 8)));
        }
        comp.CopyTo(output, compOff);
        literals.CopyTo(output, uncompOff);
        return output;
    }

    private static (int Length, int Offset) Longest(byte[] buf, int start, int maxLen, List<int> hits)
    {
        var bestLen = 0;
        var bestOff = 0;
        var farthest = Math.Max(0, start - 4096);
        for (var i = hits.Count - 1; i >= 0; i--)
        {
            var off = hits[i];
            if (off < farthest) break;
            if (off >= start) continue;
            var n = 0;
            var limit = Math.Min(maxLen, start - off);
            while (n < limit && buf[start + n] == buf[off + n]) n++;
            if (n == limit)
            {
                var extra = Math.Min(maxLen - n, buf.Length - start - n);
                var k = 0;
                while (k < extra && buf[start + n + k] == buf[off + k]) k++;
                n += k;
            }
            if (n > bestLen)
            {
                bestLen = n;
                bestOff = start - off;
                if (bestLen == maxLen) break;
            }
        }
        return (bestLen, bestOff);
    }

    private static int Align(int value, int align) => (value + align - 1) & ~(align - 1);

    private static int Be32(byte[] data, int offset) =>
        (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
}
