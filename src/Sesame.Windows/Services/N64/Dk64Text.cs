using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using Sesame.Services;

namespace Sesame.Services.N64;

public static class Dk64Text
{
    private const int TextTable = 12;
    private const int MaxFiles = 80;
    private const int MinUseful = 20;

    public static bool LooksLike(byte[] rom)
    {
        var id = N64Rom.CartId(rom);
        if (id.StartsWith("NDK", StringComparison.OrdinalIgnoreCase)) return true;
        var name = N64Rom.InternalName(rom);
        return name.Contains("DONKEY KONG", StringComparison.OrdinalIgnoreCase);
    }

    public static List<BkTextLine> Extract(byte[] rom, Action<string>? progress = null)
    {
        var lines = new List<BkTextLine>();
        if (!TryPointerBase(rom, out var tableBase)) return lines;
        if (!TryTable(rom, tableBase, TextTable, out var files, out _, out _)) return lines;

        progress?.Invoke("Donkey Kong 64-teksttabel gevonden…");
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (file.Size <= 4) continue;
            if (!TryReadFile(rom, file, out var raw, out var gzip)) continue;
            if (!TryParseFile(raw, out var strings)) continue;
            for (var n = 0; n < strings.Count; n++)
            {
                var s = strings[n];
                if (s.Text.Length < 3) continue;
                lines.Add(new BkTextLine
                {
                    AssetId = i,
                    Kind = BkTextKind.Dialog,
                    Section = "dk64",
                    Index = n,
                    OriginalBytes = Encoding.ASCII.GetBytes(s.Text),
                    Original = s.Text,
                    Translation = s.Text,
                    Generic = true,
                    InPlaceBlob = true,
                    RomOffset = file.Start,
                    SlotBytes = file.Size,
                    InnerOffset = s.Offset,
                    InnerSlot = s.Size,
                    Terminator = 0,
                    AllCaps = BkTextCodec.PreferAllCaps(s.Text),
                    Codec = "dk64"
                });
            }
            if (i % 4 == 0)
                progress?.Invoke($"DK64-tekst… {lines.Count} regels, bestand {i}");
        }

        return lines.Count >= MinUseful ? lines : [];
    }

    public static RomBuildResult Apply(byte[] rom, IReadOnlyList<BkTextLine> lines, Action<RomBuildProgress>? progress = null)
    {
        void Report(int pct, string msg, int errors = 0, string? err = null) =>
            progress?.Invoke(new RomBuildProgress
            {
                Percent = Math.Clamp(pct, 0, 99),
                Message = msg,
                Errors = errors,
                LastError = err
            });

        var changed = lines.Where(l => l.Codec == "dk64" && l.Changed && !string.IsNullOrWhiteSpace(l.Translation)).ToList();
        if (changed.Count == 0)
            throw new InvalidDataException("Geen gewijzigde DK64-teksten om in de ROM te zetten.");
        if (!TryPointerBase(rom, out var tableBase) ||
            !TryTable(rom, tableBase, TextTable, out var files, out _, out _))
            throw new InvalidDataException("DK64-teksttabel is niet meer te openen.");

        var byFile = changed.ToLookup(l => l.RomOffset);
        var written = 0;
        var skipped = 0;
        var errors = 0;
        string? lastError = null;
        var output = (byte[])rom.Clone();
        Report(15, "DK64-tekst in de originele gaten zetten…");
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var group = byFile[file.Start].ToList();
            if (group.Count == 0) continue;
            try
            {
                var blob = BuildBlob(rom, file, group);
                if (blob.Length == 0 || blob.Length > file.Size)
                {
                    skipped += group.Count;
                    continue;
                }
                Array.Clear(output, file.Start, file.Size);
                Buffer.BlockCopy(blob, 0, output, file.Start, blob.Length);
                written += group.Count;
            }
            catch (Exception ex)
            {
                errors++;
                skipped += group.Count;
                lastError = $"ROM {file.Start:X6}: {ex.Message}";
            }
            if (i % 4 == 0)
                Report(15 + (int)(70.0 * (i + 1) / files.Count), $"DK64-tekst gezet {written}", errors, lastError);
        }

        if (written == 0)
            throw new InvalidDataException(
                "Geen enkele DK64-tekst paste in de ROM." + (lastError is null ? "" : " " + lastError));

        if (N64Rom.LooksLikeN64(output))
        {
            Report(96, "Boot-checksum bijwerken…", errors, lastError);
            N64Rom.RecalcCrc(output);
        }

        var summary = skipped == 0
            ? $"{written} DK64-teksten gezet in de ROM."
            : $"{written} teksten gezet, {skipped} pasten niet en blijven Engels.";
        Report(99, summary, 0, lastError);
        return new RomBuildResult
        {
            Rom = output,
            Changed = written,
            Relocated = 0,
            Errors = 0,
            Summary = summary,
            LastError = lastError
        };
    }

    private static byte[] BuildBlob(byte[] rom, TableFile file, List<BkTextLine> group)
    {
        if (!TryReadFile(rom, file, out var raw, out var gzip))
            throw new InvalidDataException("tekstbestand niet te openen");
        var next = (byte[])raw.Clone();
        foreach (var line in group)
        {
            if (line.InnerOffset < 0 || line.InnerSlot <= 0) continue;
            if (line.InnerOffset + line.InnerSlot > next.Length) continue;
            var payload = BkTextCodec.Encode(line.Translation, line.AllCaps, 0x00);
            if (payload.Length > line.InnerSlot)
                payload = payload[..line.InnerSlot];
            Array.Clear(next, line.InnerOffset, line.InnerSlot);
            Buffer.BlockCopy(payload, 0, next, line.InnerOffset, payload.Length);
        }

        if (!gzip) return next;
        var blob = Gzip(next);
        if (blob.Length > file.Size && next.Length <= file.Size)
            return next;
        return blob;
    }

    private readonly record struct TableFile(int Start, int Size, bool Compressed);

    private readonly record struct TextSpan(int Offset, int Size, string Text);

    private static bool TryPointerBase(byte[] rom, out int tableBase)
    {
        tableBase = 0;
        if (rom.Length < 0x40) return false;
        var region = rom[0x3E];
        var kiosk = rom[0x3D] == 0x50;
        tableBase = kiosk ? 0x1A7C20 : region switch
        {
            0x45 => 0x101C50,
            0x50 => 0x1038D0,
            0x4A => 0x1039C0,
            _ => 0x101C50
        };
        return tableBase + 33 * 4 < rom.Length;
    }

    private static bool TryTable(
        byte[] rom, int tableBase, int table, out List<TableFile> files, out int tableStart, out int count)
    {
        files = new List<TableFile>();
        tableStart = tableBase + Be32(rom, tableBase + table * 4);
        count = Be32(rom, tableBase + 32 * 4 + table * 4);
        if (count is < 1 or > MaxFiles) return false;
        if (tableStart < 0 || tableStart + (count + 1) * 4 > rom.Length) return false;
        for (var i = 0; i < count; i++)
        {
            var raw = (uint)Be32(rom, tableStart + i * 4);
            var start = tableBase + (int)(raw & 0x7FFFFFFF);
            var finish = tableBase + (Be32(rom, tableStart + (i + 1) * 4) & 0x7FFFFFFF);
            var size = start >= 0 && finish > start && finish <= rom.Length ? finish - start : 0;
            files.Add(new TableFile(start, size, (raw & 0x80000000u) != 0));
        }
        return files.Count > 0;
    }

    private static bool TryReadFile(byte[] rom, TableFile file, out byte[] raw, out bool gzip)
    {
        raw = [];
        gzip = false;
        if (file.Start < 0 || file.Size < 2 || file.Start + file.Size > rom.Length) return false;
        gzip = rom[file.Start] == 0x1F && rom[file.Start + 1] == 0x8B;
        if (!gzip)
        {
            raw = rom[file.Start..(file.Start + file.Size)];
            return raw.Length > 0;
        }
        try
        {
            using var input = new MemoryStream(rom, file.Start, file.Size, writable: false);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            raw = output.ToArray();
            return raw.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Gzip(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gz.Write(raw);
        return output.ToArray();
    }

    private static bool TryParseFile(byte[] file, out List<TextSpan> strings)
    {
        strings = new List<TextSpan>();
        if (file.Length < 8) return false;
        var count = file[0];
        if (count is 0 or > 80) return false;
        var dataStart = 1;
        var boxes = new List<List<(int Rel, int Size, bool Sprite)>>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                if (dataStart + 3 >= file.Length) return false;
                var section1 = file[dataStart];
                if (section1 is 0 or > 16) return false;
                var blockStart = 1;
                var parts = new List<(int Rel, int Size, bool Sprite)>();
                for (var k = 0; k < section1; k++)
                {
                    if (dataStart + blockStart >= file.Length) return false;
                    var sec2 = file[dataStart + blockStart];
                    var offset = (sec2 & 4) != 0 ? 4 : 0;
                    if (dataStart + blockStart + offset + 1 >= file.Length) return false;
                    var sec3 = file[dataStart + blockStart + offset + 1];
                    if (sec3 > 32) return false;
                    var sprite = (sec2 & 1) == 0 && (sec2 & 2) != 0;
                    var stride = sprite ? 4 : 8;
                    for (var j = 0; j < sec3; j++)
                    {
                        var block = blockStart + 2 + offset + stride * j - 1;
                        var at = dataStart + block;
                        if (sprite)
                        {
                            if (at + 4 > file.Length) return false;
                            parts.Add((0, 0, true));
                        }
                        else
                        {
                            if (at + 7 > file.Length) return false;
                            var rel = (file[at + 3] << 8) | file[at + 4];
                            var size = (file[at + 5] << 8) | file[at + 6];
                            if (size is < 1 or > 240) continue;
                            parts.Add((rel, size, false));
                        }
                    }
                    blockStart = blockStart + 2 + offset + stride * sec3 + 4;
                }
                boxes.Add(parts);
                dataStart += blockStart;
            }
        }
        catch
        {
            return false;
        }

        var pool = dataStart + 2;
        foreach (var box in boxes)
        {
            foreach (var (rel, size, sprite) in box)
            {
                if (sprite || size <= 0) continue;
                var at = rel + pool;
                if (at < 0 || at + size > file.Length) continue;
                var text = ReadAscii(file.AsSpan(at, size));
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!LooksLikeDialog(text)) continue;
                strings.Add(new TextSpan(at, size, text));
            }
        }
        return strings.Count > 0;
    }

    private static bool LooksLikeDialog(string text)
    {
        var letters = text.Count(char.IsLetter);
        if (letters < 3) return false;
        if (text.IndexOfAny(['~', '@']) >= 0) return true;
        return EnglishText.LooksLike(text) || (letters >= 8 && text.Count(char.IsWhiteSpace) >= 1);
    }

    private static string ReadAscii(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length);
        foreach (var b in data)
        {
            if (b is >= 0x20 and <= 0x7E) sb.Append((char)b);
            else if (b == 0x0A) sb.Append('\n');
            else sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    private static int Be32(byte[] data, int offset) =>
        (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
}
