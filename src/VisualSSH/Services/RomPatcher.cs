using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace VisualSSH.Services;

public static class RomPatcher
{
    private static readonly string[] PatchExt = [".bps", ".ips", ".ups"];

    public static bool IsPatch(string path)
    {
        var ext = Path.GetExtension(path);
        return PatchExt.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    public static byte[] Apply(byte[] source, string patchPath)
    {
        var patch = File.ReadAllBytes(patchPath);
        var ext = Path.GetExtension(patchPath).ToLowerInvariant();
        return ext switch
        {
            ".bps" => ApplyBps(source, patch),
            ".ips" => ApplyIps(source, patch),
            ".ups" => ApplyUps(source, patch),
            _ => throw new InvalidOperationException("Onbekend patchformaat: " + ext + ". Ondersteund: BPS, IPS, UPS.")
        };
    }

    public static byte[] ApplyWithHeaderVariants(byte[] source, string patchPath, string? dumpName = null)
    {
        InvalidDataException? last = null;
        var attempts = new List<string>();
        var index = 0;
        foreach (var candidate in HeaderVariants(source, PeekSourceSize(patchPath)))
        {
            index++;
            var label = index == 1 ? "origineel" : "variant " + index;
            var n64 = N64Layout(candidate);
            if (n64.Length > 0) label += " (" + n64 + ")";
            try { return Apply(candidate, patchPath); }
            catch (InvalidDataException ex)
            {
                last = ex;
                attempts.Add(
                    $"{label}: {candidate.Length} bytes, CRC32 {Crc32(candidate):X8} — {ShortReason(ex)}");
            }
        }

        throw new InvalidDataException(FormatMismatch(patchPath, dumpName, source, attempts, last));
    }

    private static string ShortReason(Exception ex)
    {
        var text = ex.Message.ReplaceLineEndings(" ").Trim();
        if (text.Contains("past niet", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("checksum", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("CRC", StringComparison.OrdinalIgnoreCase))
            return "CRC komt niet overeen";
        if (text.Contains("bytes", StringComparison.OrdinalIgnoreCase))
            return "andere bestandsgrootte";
        return text.Length > 80 ? text[..80] + "…" : text;
    }

    private static string N64Layout(byte[] data)
    {
        if (data.Length < 2) return "";
        if (data[0] == 0x80 && data[1] == 0x37) return "z64";
        if (data[0] == 0x37 && data[1] == 0x80) return "v64";
        if (data[0] == 0x40 && data[1] == 0x12) return "n64";
        return "";
    }

    private static string FormatMismatch(string patchPath, string? dumpName, byte[] source,
        List<string> attempts, InvalidDataException? last)
    {
        var lines = new List<string>();
        lines.Add("Deze dump past niet bij de patch.");
        lines.Add("");
        lines.Add("Patch: " + Path.GetFileName(patchPath));
        var expect = TryReadBpsExpect(patchPath, out var info) ? info : (PatchExpect?)null;
        if (expect is { } wanted)
        {
            lines.Add($"Patch verwacht: {wanted.SourceSize} bytes, CRC32 {wanted.SourceCrc:X8}");
            if (!string.IsNullOrWhiteSpace(wanted.Metadata))
                lines.Add("Patch-metadata: " + wanted.Metadata);
        }
        else if (last is not null)
            lines.Add(last.Message);

        lines.Add("");
        lines.Add("Gebruikte dump: " + (string.IsNullOrWhiteSpace(dumpName) ? "(onbekend)" : dumpName));
        lines.Add($"Gevonden: {source.Length} bytes, CRC32 {Crc32(source):X8}");
        var n64 = DescribeN64(source);
        if (n64.Length > 0)
            lines.Add(n64);

        if (attempts.Count > 0)
        {
            lines.Add("");
            lines.Add("Geprobeerd:");
            foreach (var attempt in attempts.Take(8))
                lines.Add("• " + attempt);
        }

        var hint = MismatchHint(dumpName, source, expect);
        if (hint.Length > 0)
        {
            lines.Add("");
            lines.Add(hint);
        }

        lines.Add("");
        lines.Add("Het origineel is niet overschreven.");
        return string.Join(Environment.NewLine, lines);
    }

    private readonly record struct PatchExpect(int SourceSize, uint SourceCrc, string Metadata);

    private static bool TryReadBpsExpect(string patchPath, out PatchExpect info)
    {
        info = default;
        try
        {
            var patch = File.ReadAllBytes(patchPath);
            var ext = Path.GetExtension(patchPath).ToLowerInvariant();
            if (patch.Length < 19) return false;
            var pos = 4;
            if (ext == ".bps" && Encoding.ASCII.GetString(patch, 0, 4) == "BPS1")
            {
                var sourceSize = (int)ReadVlq(patch, ref pos);
                ReadVlq(patch, ref pos);
                var metaSize = (int)ReadVlq(patch, ref pos);
                var meta = "";
                if (metaSize > 0 && pos + metaSize <= patch.Length - 12)
                    meta = Encoding.UTF8.GetString(patch, pos, metaSize).Trim('\0', ' ', '\n', '\r');
                var crc = BinaryPrimitives.ReadUInt32LittleEndian(patch.AsSpan(patch.Length - 12));
                info = new PatchExpect(sourceSize, crc, meta);
                return true;
            }
            if (ext == ".ups" && Encoding.ASCII.GetString(patch, 0, 4) == "UPS1")
            {
                var sourceSize = (int)ReadVlq(patch, ref pos);
                var crc = BinaryPrimitives.ReadUInt32LittleEndian(patch.AsSpan(patch.Length - 12));
                info = new PatchExpect(sourceSize, crc, "");
                return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static string DescribeN64(byte[] source)
    {
        if (!N64.N64Rom.LooksLikeN64(source)) return "";
        try
        {
            var z64 = N64.N64Rom.ToZ64(source);
            var layout = N64Layout(source);
            var name = N64.N64Rom.InternalName(z64);
            var cart = N64.N64Rom.CartId(z64);
            var parts = new List<string>();
            if (layout.Length > 0) parts.Add(layout);
            if (!string.IsNullOrWhiteSpace(name)) parts.Add("intern \"" + name + "\"");
            if (!string.IsNullOrWhiteSpace(cart)) parts.Add("cart " + cart);
            return parts.Count == 0 ? "" : "N64: " + string.Join(", ", parts);
        }
        catch
        {
            return "";
        }
    }

    private static string MismatchHint(string? dumpName, byte[] source, PatchExpect? expect)
    {
        var hay = dumpName ?? "";
        if (hay.Contains("lodgenet", StringComparison.OrdinalIgnoreCase))
            return "LodgeNet is een kiosk-versie van Super Mario 64, niet de winkel-USA-dump. De meeste patches verwachten Super Mario 64 (USA), cart NSME.";
        if (expect is { } want && source.Length == want.SourceSize)
            return "De bestandsgrootte klopt, maar de inhoud niet. Dit is waarschijnlijk een andere regio of revisie dan de patch verwacht.";
        if (expect is { } sized && source.Length != sized.SourceSize)
            return "De dump heeft een andere grootte dan de patch verwacht. Controleer of dit dezelfde game-revisie is.";
        return "";
    }

    private static IEnumerable<byte[]> HeaderVariants(byte[] source, int? expected)
    {
        yield return source;
        if (LooksNes(source))
            yield return source.AsSpan(16).ToArray();
        if (LooksSnesHeadered(source))
            yield return source.AsSpan(512).ToArray();

        var nesSized = source.Length is >= 16384 and <= 2 * 1024 * 1024;
        var wantNesHeader = expected == source.Length + 16 || (expected is null && nesSized);
        if (wantNesHeader && !LooksNes(source) && nesSized)
        {
            foreach (var headed in BuildNesHeaders(source))
                yield return headed;
        }

        if ((expected is null || expected == source.Length + 512) && !LooksSnesHeadered(source)
            && source.Length <= 8 * 1024 * 1024)
            yield return Prepend(source, new byte[512]);

        foreach (var n64 in N64ByteOrders(source))
            yield return n64;
        foreach (var genesis in GenesisVariants(source, expected))
            yield return genesis;
        foreach (var gba in GbaVariants(source, expected))
            yield return gba;
        if (expected is int want && want > 0 && want < source.Length && IsPadding(source.AsSpan(want)))
            yield return source.AsSpan(0, want).ToArray();
    }

    private static IEnumerable<byte[]> BuildNesHeaders(byte[] rom)
    {
        if (rom.Length < 16384 || rom.Length % 8192 != 0)
            yield break;
        var prg = (byte)Math.Clamp(rom.Length / 16384, 1, 255);
        var chr = (byte)((rom.Length % 16384) == 8192 ? 1 : 0);
        byte[] common = [0x12, 0x10, 0x11, 0x18, 0x01, 0x00, 0x02, 0x08];
        foreach (var flags in common)
            yield return WithINes(rom, prg, chr, flags, 0);

        for (var flags = 0; flags < 256; flags++)
        {
            if (common.Contains((byte)flags)) continue;
            yield return WithINes(rom, prg, chr, (byte)flags, 0);
        }
        yield return WithINes(rom, prg, chr, 0x10, 0x08);
    }

    private static byte[] WithINes(byte[] rom, byte prg, byte chr, byte flags6, byte flags7)
    {
        var headed = new byte[16 + rom.Length];
        headed[0] = (byte)'N';
        headed[1] = (byte)'E';
        headed[2] = (byte)'S';
        headed[3] = 0x1A;
        headed[4] = prg;
        headed[5] = chr;
        headed[6] = flags6;
        headed[7] = flags7;
        Buffer.BlockCopy(rom, 0, headed, 16, rom.Length);
        return headed;
    }

    private static byte[] Prepend(byte[] rom, byte[] prefix)
    {
        var data = new byte[prefix.Length + rom.Length];
        Buffer.BlockCopy(prefix, 0, data, 0, prefix.Length);
        Buffer.BlockCopy(rom, 0, data, prefix.Length, rom.Length);
        return data;
    }

    private static int? PeekSourceSize(string patchPath)
    {
        var patch = File.ReadAllBytes(patchPath);
        var ext = Path.GetExtension(patchPath).ToLowerInvariant();
        if (patch.Length < 8) return null;
        var pos = 4;
        if (ext == ".bps" && Encoding.ASCII.GetString(patch, 0, 4) == "BPS1")
            return (int)ReadVlq(patch, ref pos);
        if (ext == ".ups" && Encoding.ASCII.GetString(patch, 0, 4) == "UPS1")
            return (int)ReadVlq(patch, ref pos);
        return null;
    }

    public static bool LooksNes(byte[] data) =>
        data.Length > 16 && data[0] == (byte)'N' && data[1] == (byte)'E' && data[2] == (byte)'S' && data[3] == 0x1A;

    public static bool LooksSnesHeadered(byte[] data) =>
        data.Length > 512 && data.Length % 1024 == 512;

    public static byte[]? WithoutHeader(byte[] data)
    {
        if (LooksNes(data)) return data.AsSpan(16).ToArray();
        if (LooksSnesHeadered(data)) return data.AsSpan(512).ToArray();
        return null;
    }

    private static IEnumerable<byte[]> N64ByteOrders(byte[] source)
    {
        if (!N64.N64Rom.LooksLikeN64(source)) yield break;
        byte[] z64;
        try { z64 = N64.N64Rom.ToZ64(source); }
        catch { yield break; }
        if (!SameBytes(z64, source)) yield return z64;
        var v64 = SwapPairs(z64, 2);
        if (!SameBytes(v64, source)) yield return v64;
        var n64 = SwapPairs(z64, 4);
        if (!SameBytes(n64, source)) yield return n64;
    }

    private static IEnumerable<byte[]> GenesisVariants(byte[] source, int? expected)
    {
        if (LooksGenesisBin(source))
        {
            var smd = BinToSmd(source, header: false);
            if (expected is null || expected == smd.Length) yield return smd;
            var smdH = BinToSmd(source, header: true);
            if (expected is null || expected == smdH.Length) yield return smdH;
            yield break;
        }

        if (!LooksSmd(source)) yield break;
        byte[] bin;
        try { bin = SmdToBin(source); }
        catch { yield break; }
        if (expected is null || expected == bin.Length) yield return bin;
        if (source.Length % 16384 == 512)
        {
            var noHead = source.AsSpan(512).ToArray();
            if (expected is null || expected == noHead.Length) yield return noHead;
        }
    }

    private static IEnumerable<byte[]> GbaVariants(byte[] source, int? expected)
    {
        const int copier = 0xC0;
        if (expected == source.Length + copier)
            yield return Prepend(source, new byte[copier]);
        if (expected == source.Length - copier && source.Length > copier)
            yield return source.AsSpan(copier).ToArray();
    }

    public static bool LooksGenesisBin(byte[] data)
    {
        if (data.Length < 0x200) return false;
        var mark = Encoding.ASCII.GetString(data, 0x100, 4);
        return mark is "SEGA" or " SEG";
    }

    public static bool LooksSmd(byte[] data)
    {
        if (data.Length < 0x4000 || LooksGenesisBin(data)) return false;
        var rem = data.Length % 16384;
        return rem is 0 or 512;
    }

    public static bool LooksGba(byte[] data) =>
        data.Length >= 0xC0 && data[0xB2] == 0x96;

    public static bool LooksNds(byte[] data) =>
        data.Length >= 0x160 && data[0x15C] == 0x56 && data[0x15D] == 0xCF;

    private static byte[] SmdToBin(byte[] smd)
    {
        var offset = smd.Length % 16384 == 512 ? 512 : 0;
        var len = smd.Length - offset;
        if (len <= 0 || len % 16384 != 0)
            throw new InvalidDataException("Geen geldige Genesis SMD-dump.");
        var bin = new byte[len];
        for (var block = 0; block < len; block += 16384)
        {
            for (var i = 0; i < 8192; i++)
            {
                bin[block + i * 2] = smd[offset + block + i + 8192];
                bin[block + i * 2 + 1] = smd[offset + block + i];
            }
        }
        return bin;
    }

    private static byte[] BinToSmd(byte[] bin, bool header)
    {
        var aligned = bin.Length % 16384 == 0 ? bin : PadTo(bin, ((bin.Length + 16383) / 16384) * 16384);
        var smd = new byte[(header ? 512 : 0) + aligned.Length];
        var offset = header ? 512 : 0;
        for (var block = 0; block < aligned.Length; block += 16384)
        {
            for (var i = 0; i < 8192; i++)
            {
                smd[offset + block + i] = aligned[block + i * 2 + 1];
                smd[offset + block + i + 8192] = aligned[block + i * 2];
            }
        }
        return smd;
    }

    private static byte[] PadTo(byte[] data, int size)
    {
        var copy = new byte[size];
        Buffer.BlockCopy(data, 0, copy, 0, data.Length);
        return copy;
    }

    private static byte[] SwapPairs(byte[] data, int width)
    {
        var copy = (byte[])data.Clone();
        if (width == 2)
        {
            for (var i = 0; i + 1 < copy.Length; i += 2)
                (copy[i], copy[i + 1]) = (copy[i + 1], copy[i]);
        }
        else
        {
            for (var i = 0; i + 3 < copy.Length; i += 4)
            {
                (copy[i], copy[i + 3]) = (copy[i + 3], copy[i]);
                (copy[i + 1], copy[i + 2]) = (copy[i + 2], copy[i + 1]);
            }
        }
        return copy;
    }

    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static bool IsPadding(ReadOnlySpan<byte> tail)
    {
        if (tail.Length == 0) return false;
        var fill = tail[0];
        if (fill is not 0x00 and not 0xFF) return false;
        for (var i = 1; i < tail.Length; i++)
            if (tail[i] != fill) return false;
        return true;
    }

    private static byte[] ApplyBps(byte[] source, byte[] patch)
    {
        if (patch.Length < 19 || Encoding.ASCII.GetString(patch, 0, 4) != "BPS1")
            throw new InvalidDataException("Geen geldige BPS-patch.");

        var crcPatch = Crc32(patch.AsSpan(0, patch.Length - 4));
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(patch.AsSpan(patch.Length - 4));
        if (crcPatch != stored)
            throw new InvalidDataException("BPS-patch is beschadigd (checksum).");

        var pos = 4;
        var sourceSize = (int)ReadVlq(patch, ref pos);
        var targetSize = (int)ReadVlq(patch, ref pos);
        var metaSize = (int)ReadVlq(patch, ref pos);
        pos += metaSize;
        if (source.Length != sourceSize)
            throw new InvalidDataException(
                $"BPS verwacht een basis-ROM van {sourceSize} bytes, deze dump is {source.Length} bytes.");

        var sourceCrc = BinaryPrimitives.ReadUInt32LittleEndian(patch.AsSpan(patch.Length - 12));
        if (Crc32(source) != sourceCrc)
            throw new InvalidDataException("Deze dump past niet bij de patch (SHA/CRC van de basis-ROM klopt niet).");

        var target = new byte[targetSize];
        long outputOffset = 0;
        long sourceRelative = 0;
        long targetRelative = 0;
        var end = patch.Length - 12;
        while (pos < end)
        {
            var data = ReadVlq(patch, ref pos);
            var command = (int)(data & 3);
            var length = (int)((data >> 2) + 1);
            switch (command)
            {
                case 0:
                    for (var i = 0; i < length; i++, outputOffset++)
                        target[outputOffset] = source[outputOffset];
                    break;
                case 1:
                    for (var i = 0; i < length; i++, outputOffset++)
                        target[outputOffset] = patch[pos++];
                    break;
                case 2:
                    var rel = ReadVlq(patch, ref pos);
                    sourceRelative += ((rel & 1) != 0 ? -1 : 1) * (long)(rel >> 1);
                    for (var i = 0; i < length; i++)
                        target[outputOffset++] = source[sourceRelative++];
                    break;
                case 3:
                    rel = ReadVlq(patch, ref pos);
                    targetRelative += ((rel & 1) != 0 ? -1 : 1) * (long)(rel >> 1);
                    for (var i = 0; i < length; i++)
                        target[outputOffset++] = target[targetRelative++];
                    break;
            }
        }

        var targetCrc = BinaryPrimitives.ReadUInt32LittleEndian(patch.AsSpan(patch.Length - 8));
        if (Crc32(target) != targetCrc)
            throw new InvalidDataException("Patch toegepast, maar de uitvoer-checksum klopt niet.");
        return target;
    }

    private static byte[] ApplyIps(byte[] source, byte[] patch)
    {
        if (patch.Length < 8 || Encoding.ASCII.GetString(patch, 0, 5) != "PATCH")
            throw new InvalidDataException("Geen geldige IPS-patch.");

        var output = source.ToArray();
        var pos = 5;
        while (pos + 3 <= patch.Length)
        {
            if (pos + 3 <= patch.Length &&
                patch[pos] == (byte)'E' && patch[pos + 1] == (byte)'O' && patch[pos + 2] == (byte)'F')
            {
                pos += 3;
                if (pos + 3 <= patch.Length)
                {
                    var truncate = (patch[pos] << 16) | (patch[pos + 1] << 8) | patch[pos + 2];
                    if (truncate > 0 && truncate < output.Length)
                        Array.Resize(ref output, truncate);
                }
                break;
            }

            var offset = (patch[pos] << 16) | (patch[pos + 1] << 8) | patch[pos + 2];
            pos += 3;
            if (pos + 2 > patch.Length) break;
            var size = (patch[pos] << 8) | patch[pos + 1];
            pos += 2;
            if (size == 0)
            {
                if (pos + 3 > patch.Length) break;
                var rle = (patch[pos] << 8) | patch[pos + 1];
                var fill = patch[pos + 2];
                pos += 3;
                Ensure(ref output, offset + rle);
                for (var i = 0; i < rle; i++)
                    output[offset + i] = fill;
            }
            else
            {
                if (pos + size > patch.Length) break;
                Ensure(ref output, offset + size);
                Buffer.BlockCopy(patch, pos, output, offset, size);
                pos += size;
            }
        }
        return output;
    }

    private static byte[] ApplyUps(byte[] source, byte[] patch)
    {
        if (patch.Length < 16 || Encoding.ASCII.GetString(patch, 0, 4) != "UPS1")
            throw new InvalidDataException("Geen geldige UPS-patch.");

        var pos = 4;
        var inputSize = (int)ReadVlq(patch, ref pos);
        var outputSize = (int)ReadVlq(patch, ref pos);
        if (source.Length != inputSize)
            throw new InvalidDataException(
                $"UPS verwacht een basis-ROM van {inputSize} bytes, deze dump is {source.Length} bytes.");

        var inputCrc = BinaryPrimitives.ReadUInt32LittleEndian(patch.AsSpan(patch.Length - 12));
        if (Crc32(source) != inputCrc)
            throw new InvalidDataException("Deze dump past niet bij de UPS-patch.");

        var output = new byte[outputSize];
        Buffer.BlockCopy(source, 0, output, 0, Math.Min(source.Length, output.Length));
        long offset = 0;
        var end = patch.Length - 12;
        while (pos < end)
        {
            offset += (long)ReadVlq(patch, ref pos);
            while (pos < end)
            {
                var x = patch[pos++];
                if (x == 0) break;
                if (offset < output.Length)
                    output[offset] ^= x;
                offset++;
            }
            offset++;
        }

        var outputCrc = BinaryPrimitives.ReadUInt32LittleEndian(patch.AsSpan(patch.Length - 8));
        if (Crc32(output) != outputCrc)
            throw new InvalidDataException("UPS toegepast, maar de uitvoer-checksum klopt niet.");
        return output;
    }

    private static void Ensure(ref byte[] data, int size)
    {
        if (size <= data.Length) return;
        Array.Resize(ref data, size);
    }

    private static ulong ReadVlq(byte[] data, ref int pos)
    {
        ulong value = 0;
        ulong shift = 1;
        while (pos < data.Length)
        {
            var x = data[pos++];
            value += (ulong)(x & 0x7F) * shift;
            if ((x & 0x80) != 0) break;
            shift <<= 7;
            value += shift;
        }
        return value;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
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
