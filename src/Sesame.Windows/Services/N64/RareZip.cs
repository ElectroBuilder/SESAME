using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Sesame.Services.N64;

public static class RareZip
{
    public static bool IsCompressed(ReadOnlySpan<byte> data) =>
        data.Length >= 6 && data[0] == 0x11 && data[1] == 0x72;

    public static bool TryUnzipAt(byte[] rom, int offset, out byte[] raw, out int consumed)
    {
        raw = [];
        consumed = 0;
        if (offset < 0 || offset + 8 > rom.Length) return false;
        if (rom[offset] != 0x11 || rom[offset + 1] != 0x72) return false;
        var size = BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(offset + 2));
        if (size is < 6 or > 0xC000) return false;
        var maxComp = Math.Min(rom.Length - offset - 6, (int)size + 512);
        if (maxComp < 2) return false;
        try
        {
            using var input = new MemoryStream(rom, offset + 6, maxComp, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var buf = new byte[size];
            var read = 0;
            while (read < size)
            {
                var n = deflate.Read(buf, read, (int)size - read);
                if (n <= 0) return false;
                read += n;
            }
            raw = buf;
            consumed = 6 + (int)input.Position;
            return consumed >= 8 && offset + consumed <= rom.Length;
        }
        catch
        {
            return false;
        }
    }

    public static byte[] Unzip(byte[] data)
    {
        if (!IsCompressed(data)) return data;
        var payload = data.AsSpan(6);
        using var input = new MemoryStream(payload.ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    public static byte[] Zip(byte[] raw, int maxBytes = int.MaxValue)
    {
        var levels = maxBytes < int.MaxValue
            ? new[] { CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize }
            : new[] { CompressionLevel.Fastest, CompressionLevel.Optimal };
        foreach (var level in levels)
        {
            var blob = ZipWith(raw, level);
            if (blob.Length > maxBytes) continue;
            try
            {
                var round = Unzip(blob);
                if (round.Length == raw.Length && round.AsSpan().SequenceEqual(raw))
                    return blob;
            }
            catch { /* volgende niveau */ }
        }
        throw new InvalidDataException("De nieuwe tekst past niet in het originele ROM-gat.");
    }

    private static byte[] ZipWith(byte[] raw, CompressionLevel level)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x11);
        output.WriteByte(0x72);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)raw.Length);
        output.Write(size);
        using (var deflate = new DeflateStream(output, level, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        return output.ToArray();
    }
}
