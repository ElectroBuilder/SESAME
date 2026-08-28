using System.Buffers.Binary;
using System.IO;

namespace Sesame.Services.N64;

internal sealed class BkAssetEntry
{
    public int Id;
    public uint Offset;
    public bool Compressed;
    public ushort Flags;
    public byte[] Blob = [];
}

public sealed class RomBuildProgress
{
    public int Percent { get; init; }
    public string Message { get; init; } = "";
    public int Errors { get; init; }
    public string? LastError { get; init; }
}

public sealed class RomBuildResult
{
    public required byte[] Rom { get; init; }
    public int Changed { get; init; }
    public int Relocated { get; init; }
    public int Shortened { get; init; }
    public int Errors { get; init; }
    public string Summary { get; init; } = "";
    public string? LastError { get; init; }
    public List<(int AssetId, string Section, int Index, string Text)> Fitted { get; init; } = [];
}

public static class BkAssetTable
{
    public const int TableHeader = 0x5E90;

    public static bool LooksValid(byte[] rom)
    {
        if (rom.Length < TableHeader + 16) return false;
        var count = BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(TableHeader));
        return count is > 0x100 and < 0x4000 && DataStart(count) < rom.Length;
    }

    public static List<BkTextLine> ExtractText(byte[] rom, Action<string>? progress = null)
    {
        var entries = Read(rom);
        var lines = new List<BkTextLine>();
        foreach (var entry in entries)
        {
            if (entry.Flags == 4 || entry.Blob.Length == 0) continue;
            if (entry.Id is < 0xA0B or > 0x1469) continue;
            byte[] raw;
            try { raw = entry.Compressed ? RareZip.Unzip(entry.Blob) : entry.Blob; }
            catch { continue; }
            if (!BkTextCodec.TryParse(raw, out var parsed)) continue;
            AddLines(lines, entry.Id, parsed.Kind, "body", parsed.Bottom);
            AddLines(lines, entry.Id, parsed.Kind, parsed.Kind == BkTextKind.Dialog ? "top" : "opties", parsed.Top);
            if (lines.Count % 80 == 0)
                progress?.Invoke($"Tekst gezocht… {lines.Count} regels, asset {entry.Id:X4}");
        }
        return lines;
    }

    public static RomBuildResult ApplyText(byte[] rom, IReadOnlyList<BkTextLine> lines, Action<RomBuildProgress>? progress = null)
    {
        void Report(int pct, string msg, int errors = 0, string? err = null) =>
            progress?.Invoke(new RomBuildProgress
            {
                Percent = Math.Clamp(pct, 0, 99),
                Message = msg,
                Errors = errors,
                LastError = err
            });

        Report(3, "Vertalingen verzamelen…");
        var byAsset = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Translation) &&
                        !string.Equals(l.Translation, l.Original, StringComparison.Ordinal))
            .GroupBy(l => l.AssetId)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (byAsset.Count == 0)
            throw new InvalidDataException("No changed texts to write into the ROM.");

        Report(8, "Asset-tabel lezen…");
        var entries = Read(rom);
        var dataStart = DataStart((uint)entries.Count);
        var prepared = new Dictionary<int, byte[]>();
        var fitted = new List<(int AssetId, string Section, int Index, string Text)>();
        var shortenedAssets = 0;
        var errors = 0;
        string? lastError = null;
        var targets = entries.Where(e => byAsset.ContainsKey(e.Id) && e.Blob.Length > 0).ToList();

        for (var i = 0; i < targets.Count; i++)
        {
            var entry = targets[i];
            var pct = 10 + (int)(70.0 * (i + 1) / Math.Max(1, targets.Count));
            try
            {
                var slot = SlotLength(entries, entry.Id, dataStart, rom.Length);
                var blob = BuildBlob(entry, byAsset[entry.Id], slot, out var assetFitted);
                prepared[entry.Id] = blob;
                if (assetFitted.Count > 0)
                {
                    shortenedAssets++;
                    fitted.AddRange(assetFitted);
                }
                Report(pct, $"Text done {i + 1}/{targets.Count} (asset {entry.Id:X4})", errors);
            }
            catch (Exception ex)
            {
                errors++;
                lastError = $"Asset {entry.Id:X4}: {ex.Message}";
                Report(pct, lastError, errors, lastError);
            }
        }

        if (prepared.Count == 0)
            throw new InvalidDataException(
                "No text file could be built." +
                (lastError is null ? "" : " " + lastError));

        Report(85, "Writing in-place into the original ROM slots…", errors, lastError);
        var output = PatchInPlace(rom, entries, prepared, dataStart, out var crcTouched);
        if (crcTouched)
        {
            Report(96, "Updating boot checksum (CIC-6103)…", errors, lastError);
            N64Rom.RecalcCrc(output);
        }
        else
            Report(96, "Checksum unchanged (dialogue outside boot area).", errors, lastError);

        var summary = shortenedAssets == 0
            ? $"{prepared.Count} texts written into the original slots."
            : $"{prepared.Count} texts written. {shortenedAssets} files were too long for the slot; " +
              "those lines were shortened so the ROM still starts (no table rebuild).";
        Report(99, summary, errors, lastError);
        return new RomBuildResult
        {
            Rom = output,
            Changed = prepared.Count,
            Relocated = 0,
            Shortened = fitted.Count,
            Errors = errors,
            Summary = summary,
            LastError = lastError,
            Fitted = fitted
        };
    }

    private static byte[] PatchInPlace(
        byte[] rom, List<BkAssetEntry> entries, Dictionary<int, byte[]> prepared, int dataStart, out bool crcTouched)
    {
        var output = (byte[])rom.Clone();
        crcTouched = false;
        foreach (var (id, blob) in prepared)
        {
            var entry = entries[id];
            var slot = SlotLength(entries, id, dataStart, output.Length);
            if (blob.Length > slot)
                throw new InvalidDataException($"Asset {id:X4} past niet in-place.");
            var at = dataStart + (int)entry.Offset;
            if (at < 0 || at + slot > output.Length)
                throw new InvalidDataException("ROM-adres buiten bereik");
            Array.Clear(output, at, slot);
            Buffer.BlockCopy(blob, 0, output, at, blob.Length);
            if (at < 0x101000) crcTouched = true;
        }
        return output;
    }

    private static byte[] BuildBlob(
        BkAssetEntry entry, List<BkTextLine> edits, int slot,
        out List<(int AssetId, string Section, int Index, string Text)> fitted)
    {
        fitted = [];
        var texts = edits.ToDictionary(e => (e.Section, e.Index), e => e.Translation);

        if (TryCompress(entry, edits, texts, slot, out var blob))
            return blob;

        for (var n = 0; n < 48; n++)
        {
            var key = texts
                .Where(kv => !string.Equals(kv.Value, OriginalOf(edits, kv.Key), StringComparison.Ordinal))
                .OrderByDescending(kv => BkTextCodec.EncodedLength(kv.Value))
                .Select(kv => kv.Key)
                .DefaultIfEmpty(default)
                .First();
            if (key == default && texts.Count > 0)
                key = texts.OrderByDescending(kv => BkTextCodec.EncodedLength(kv.Value)).First().Key;
            if (!texts.TryGetValue(key, out var current) || current.Length <= 2)
                break;
            var budget = Math.Max(2, BkTextCodec.EncodedLength(current) - Math.Max(3, current.Length / 12));
            texts[key] = TrimTo(current, budget);
            if (!TryCompress(entry, edits, texts, slot, out blob))
                continue;
            foreach (var (k, v) in texts)
            {
                var line = edits.FirstOrDefault(e => e.Section == k.Section && e.Index == k.Index);
                if (line is null || string.Equals(line.Translation, v, StringComparison.Ordinal)) continue;
                fitted.Add((entry.Id, k.Section, k.Index, v));
            }
            return blob;
        }

        throw new InvalidDataException("De nieuwe tekst past niet in het originele ROM-gat.");
    }

    private static bool TryCompress(
        BkAssetEntry entry, List<BkTextLine> edits,
        Dictionary<(string Section, int Index), string> texts, int slot, out byte[] blob)
    {
        blob = [];
        var raw = entry.Compressed ? RareZip.Unzip(entry.Blob) : entry.Blob;
        if (!BkTextCodec.TryParse(raw, out var parsed))
            return false;
        ApplyEdits(parsed.Bottom, edits, "body", texts);
        ApplyEdits(parsed.Top, edits, parsed.Kind == BkTextKind.Dialog ? "top" : "opties", texts);
        var next = BkTextCodec.ToBytes(parsed);
        try
        {
            blob = entry.Compressed ? RareZip.Zip(next, slot) : FitUncompressed(next, slot);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string OriginalOf(List<BkTextLine> edits, (string Section, int Index) key)
    {
        var line = edits.FirstOrDefault(e => e.Section == key.Section && e.Index == key.Index);
        return line?.Original ?? "";
    }

    private static string TrimTo(string text, int maxBytes)
    {
        var game = BkTextCodec.ToGameText(text);
        if (BkTextCodec.EncodedLength(game) <= maxBytes) return game;
        var cut = game.TrimEnd();
        while (cut.Length > 1 && BkTextCodec.EncodedLength(cut) > maxBytes)
            cut = cut[..^1];
        cut = cut.TrimEnd(' ', ',', ';', '-', '\'', '"');
        var space = cut.LastIndexOf(' ');
        if (space > cut.Length / 2) cut = cut[..space];
        return cut.Length > 0 ? cut : game[..1];
    }

    private static int SlotLength(List<BkAssetEntry> entries, int id, int dataStart, int romLen)
    {
        var start = (int)entries[id].Offset;
        var end = id + 1 < entries.Count ? (int)entries[id + 1].Offset : romLen - dataStart;
        if (end < start) throw new InvalidDataException("ongeldige asset-lengte");
        return end - start;
    }

    private static byte[] FitUncompressed(byte[] next, int slot)
    {
        if (next.Length > slot)
            throw new InvalidDataException("De nieuwe tekst past niet in het originele ROM-gat.");
        return next;
    }

    private static void ApplyEdits(
        List<BkString> strings, List<BkTextLine> edits, string section,
        Dictionary<(string Section, int Index), string>? texts = null)
    {
        foreach (var edit in edits.Where(e => e.Section == section))
        {
            if (edit.Index < 0 || edit.Index >= strings.Count) continue;
            var text = texts is not null && texts.TryGetValue((edit.Section, edit.Index), out var t)
                ? t
                : edit.Translation;
            if (!BkTextCodec.TryEncode(text, out var bytes)) continue;
            strings[edit.Index].Bytes = bytes;
        }
    }

    private static void AddLines(List<BkTextLine> lines, int id, BkTextKind kind, string section, List<BkString> strings)
    {
        for (var i = 0; i < strings.Count; i++)
        {
            var text = BkTextCodec.ToReadable(strings[i].Bytes);
            if (string.IsNullOrWhiteSpace(text)) continue;
            lines.Add(new BkTextLine
            {
                AssetId = id,
                Kind = kind,
                Section = section,
                Index = i,
                Cmd = strings[i].Cmd,
                OriginalBytes = strings[i].Bytes,
                Original = text,
                Translation = text
            });
        }
    }

    private static List<BkAssetEntry> Read(byte[] rom)
    {
        if (!LooksValid(rom))
            throw new InvalidDataException("Dit lijkt geen Banjo-Kazooie-ROM met een herkenbare asset-tabel.");

        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(TableHeader));
        var table = TableHeader + 8;
        var data = DataStart((uint)count);
        var entries = new List<BkAssetEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var at = table + i * 8;
            entries.Add(new BkAssetEntry
            {
                Id = i,
                Offset = BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(at)),
                Compressed = BinaryPrimitives.ReadUInt16BigEndian(rom.AsSpan(at + 4)) != 0,
                Flags = BinaryPrimitives.ReadUInt16BigEndian(rom.AsSpan(at + 6))
            });
        }

        for (var i = 0; i < count - 1; i++)
        {
            var cur = entries[i];
            if (cur.Flags == 4) continue;
            var start = data + (int)cur.Offset;
            var end = data + (int)entries[i + 1].Offset;
            if (start < 0 || end > rom.Length || end < start) continue;
            cur.Blob = rom[start..end];
        }
        return entries;
    }

    private static int DataStart(uint count) => TableHeader + 8 + (int)count * 8;
}
