using System.IO;
using Sesame.Services;

namespace Sesame.Services.N64;

public static class GenericN64Text
{
    private const int MinLen = 8;
    private const int MaxLen = 240;
    private const int MaxLines = 6000;
    private const int BootEnd = 0x1000;

    public static List<BkTextLine> Extract(byte[] rom, Action<string>? progress = null)
    {
        var lines = new List<BkTextLine>();
        var used = new bool[rom.Length];

        progress?.Invoke("Rare-dialoog in de ROM zoeken…");
        ExtractRareDialog(rom, lines, used, progress);

        progress?.Invoke("Engelse zinnen in de ROM zoeken…");
        ExtractAscii(rom, lines, used, progress);

        return lines;
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

        var changed = lines.Where(l => l.Changed && !string.IsNullOrWhiteSpace(l.Translation)).ToList();
        if (changed.Count == 0)
            throw new InvalidDataException("No changed texts to write into the ROM.");

        var output = (byte[])rom.Clone();
        var errors = 0;
        string? lastError = null;
        var written = 0;
        var skipped = 0;
        var crcTouched = false;

        var blobs = changed.Where(l => l.InPlaceBlob).GroupBy(l => l.RomOffset).ToList();
        var ascii = changed.Where(l => l.Generic && !l.InPlaceBlob).ToList();
        var total = Math.Max(1, blobs.Count + ascii.Count);
        var step = 0;

        foreach (var group in blobs)
        {
            step++;
            var pct = 10 + (int)(80.0 * step / total);
            try
            {
                var sample = group.First();
                if (!RareZip.TryUnzipAt(rom, sample.RomOffset, out var raw, out var consumed))
                    throw new InvalidDataException("blob niet meer te openen");
                byte[] next;
                if (BkTextCodec.TryParse(raw, out var parsed))
                {
                    var edits = group.ToList();
                    ApplyEdits(parsed.Bottom, edits, "body");
                    ApplyEdits(parsed.Top, edits, parsed.Kind == BkTextKind.Dialog ? "top" : "opties");
                    next = BkTextCodec.ToBytes(parsed);
                }
                else
                {
                    next = (byte[])raw.Clone();
                    foreach (var line in group)
                    {
                        if (line.InnerSlot <= 0 || line.InnerOffset < 0) continue;
                        var payload = BkTextCodec.Encode(line.Translation, line.AllCaps, 0x00);
                        var room = Math.Max(1, line.InnerSlot - 1);
                        if (payload.Length > room) payload = payload[..room];
                        if (line.InnerOffset + line.InnerSlot > next.Length) continue;
                        Array.Clear(next, line.InnerOffset, line.InnerSlot);
                        Buffer.BlockCopy(payload, 0, next, line.InnerOffset, payload.Length);
                    }
                }
                var blob = RareZip.Zip(next, sample.SlotBytes > 0 ? sample.SlotBytes : consumed);
                if (blob.Length > consumed)
                    throw new InvalidDataException("De nieuwe tekst past niet in het originele ROM-gat.");
                var at = sample.RomOffset;
                Array.Clear(output, at, consumed);
                Buffer.BlockCopy(blob, 0, output, at, blob.Length);
                if (at < 0x101000) crcTouched = true;
                written++;
                Report(pct, $"Dialoog gezet {written} (ROM {at:X6})", errors);
            }
            catch (Exception ex)
            {
                errors++;
                skipped++;
                lastError = $"ROM {group.Key:X6}: {ex.Message}";
                Report(pct, lastError, errors, lastError);
            }
        }

        foreach (var line in ascii)
        {
            step++;
            var pct = 10 + (int)(80.0 * step / total);
            try
            {
                var at = line.RomOffset;
                var slot = line.SlotBytes;
                if (at < 0 || slot <= 0 || at + slot > output.Length)
                    throw new InvalidDataException("ROM-adres buiten bereik");
                var payload = line.Codec == "sm64"
                    ? Sm64Charset.Encode(line.Translation)
                    : BkTextCodec.Encode(line.Translation, line.AllCaps, line.NewlineByte);
                var room = line.MaxChars;
                if (payload.Length > room)
                    payload = payload[..room];
                Array.Clear(output, at, slot);
                Buffer.BlockCopy(payload, 0, output, at, payload.Length);
                var term = line.Codec == "sm64" ? (byte)0xFF : line.Terminator;
                if (term is 0x00 or 0xFF && payload.Length < slot)
                    output[at + payload.Length] = term;
                if (at < 0x101000) crcTouched = true;
                written++;
                Report(pct, $"Tekst gezet {written}/{changed.Count} (ROM {at:X6})", errors);
            }
            catch (Exception ex)
            {
                errors++;
                skipped++;
                lastError = $"ROM {line.RomOffset:X6}: {ex.Message}";
                Report(pct, lastError, errors, lastError);
            }
        }

        if (written == 0)
            throw new InvalidDataException(
                "No text fitted in the ROM. Shorten a few lines or leave them English." +
                (lastError is null ? "" : " " + lastError));

        if (crcTouched && N64Rom.LooksLikeN64(output))
        {
            Report(96, "Boot-checksum bijwerken…", errors, lastError);
            N64Rom.RecalcCrc(output);
        }
        else
            Report(96, "Checksum ongewijzigd (tekst buiten boot-gebied).", errors, lastError);

        var summary = skipped == 0
            ? $"{written} teksten gezet in de ROM."
            : $"{written} teksten gezet, {skipped} pasten niet in het originele gat en blijven Engels.";
        Report(99, summary, errors, lastError);
        return new RomBuildResult
        {
            Rom = output,
            Changed = written,
            Relocated = 0,
            Errors = errors,
            Summary = summary,
            LastError = lastError
        };
    }

    private static void ApplyEdits(List<BkString> strings, List<BkTextLine> edits, string section)
    {
        foreach (var edit in edits.Where(e => e.Section == section))
        {
            if (edit.Index < 0 || edit.Index >= strings.Count) continue;
            if (!BkTextCodec.TryEncode(edit.Translation, out var bytes)) continue;
            strings[edit.Index].Bytes = bytes;
        }
    }

    private static void ExtractRareDialog(byte[] rom, List<BkTextLine> lines, bool[] used, Action<string>? progress)
    {
        for (var i = BootEnd; i + 8 < rom.Length; i++)
        {
            if (rom[i] != 0x11 || rom[i + 1] != 0x72) continue;
            if (used[i]) continue;
            if (!RareZip.TryUnzipAt(rom, i, out var raw, out var consumed)) continue;
            if (BkTextCodec.TryParse(raw, out var parsed))
            {
                Mark(used, i, consumed);
                AddParsed(lines, parsed, i, i, consumed);
                i += Math.Max(1, consumed - 1);
                continue;
            }
            var found = new List<(int Rel, string Text, int InnerSlot)>();
            CollectAsciiInBuffer(raw, (rel, text, innerSlot) => found.Add((rel, text, innerSlot)));
            if (found.Count < 2) continue;
            for (var n = 0; n < found.Count; n++)
            {
                var (rel, text, innerSlot) = found[n];
                lines.Add(new BkTextLine
                {
                    AssetId = i,
                    Kind = BkTextKind.Raw,
                    Section = "zip",
                    Index = n,
                    OriginalBytes = raw[rel..(rel + Math.Max(0, innerSlot - 1))],
                    Original = text,
                    Translation = text,
                    Generic = true,
                    InPlaceBlob = true,
                    RomOffset = i,
                    SlotBytes = consumed,
                    InnerOffset = rel,
                    InnerSlot = innerSlot,
                    Terminator = 0x00,
                    AllCaps = BkTextCodec.PreferAllCaps(text),
                    Codec = "ascii"
                });
            }
            Mark(used, i, consumed);
            if (lines.Count % 40 == 0)
                progress?.Invoke($"Rare-tekst… {lines.Count} regels, ROM {i:X6}");
            i += Math.Max(1, consumed - 1);
        }
    }

    private static void AddParsed(List<BkTextLine> lines, BkParsedText parsed, int id, int offset, int slot)
    {
        AddSection(lines, parsed.Bottom, parsed.Kind, "body", id, offset, slot);
        AddSection(lines, parsed.Top, parsed.Kind,
            parsed.Kind == BkTextKind.Dialog ? "top" : "opties", id, offset, slot);
    }

    private static void AddSection(
        List<BkTextLine> lines, List<BkString> strings, BkTextKind kind, string section,
        int id, int offset, int slot)
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
                Translation = text,
                InPlaceBlob = true,
                RomOffset = offset,
                SlotBytes = slot,
                AllCaps = true
            });
        }
    }

    private static void ExtractAscii(byte[] rom, List<BkTextLine> lines, bool[] used, Action<string>? progress)
    {
        var added = 0;
        for (var i = BootEnd; i < rom.Length && lines.Count < MaxLines; i++)
        {
            if (used[i]) continue;
            if (!IsPrintable(rom[i])) continue;
            var start = i;
            var newlines = 0;
            while (i < rom.Length && !used[i])
            {
                var b = rom[i];
                if (IsPrintable(b))
                {
                    i++;
                    continue;
                }
                if (b == 0x0A && newlines < 3 && i + 1 < rom.Length && IsPrintable(rom[i + 1]))
                {
                    newlines++;
                    i++;
                    continue;
                }
                break;
            }

            var len = i - start;
            byte term = 0;
            var slot = len;
            if (i < rom.Length && rom[i] is 0x00 or 0xFF)
            {
                term = rom[i];
                slot = len + 1;
            }

            if (len < MinLen || len > MaxLen)
            {
                i = start;
                continue;
            }

            var text = ReadAscii(rom.AsSpan(start, len));
            if (!EnglishText.LooksLike(text))
            {
                i = start;
                continue;
            }

            Mark(used, start, slot);
            lines.Add(new BkTextLine
            {
                AssetId = start,
                Kind = BkTextKind.Raw,
                Section = "raw",
                Index = 0,
                OriginalBytes = rom[start..(start + len)],
                Original = text,
                Translation = text,
                Generic = true,
                RomOffset = start,
                SlotBytes = slot,
                Terminator = term,
                AllCaps = BkTextCodec.PreferAllCaps(text)
            });
            added++;
            if (added % 80 == 0)
                progress?.Invoke($"Tekst gezocht… {lines.Count} regels, ROM {start:X6}");
        }
    }

    private static string ReadAscii(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder(data.Length);
        foreach (var b in data)
        {
            if (b == 0x0A) sb.Append('\n');
            else sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static void ExtractSm64(byte[] rom, List<BkTextLine> lines, bool[] used, Action<string>? progress)
    {
        var added = 0;
        for (var i = BootEnd; i + 12 < rom.Length && lines.Count < MaxLines; i++)
        {
            if (used[i] || !Sm64Charset.IsTextByte(rom[i])) continue;
            var start = i;
            while (i < rom.Length && !used[i] && Sm64Charset.IsTextByte(rom[i]))
                i++;
            if (i >= rom.Length || rom[i] != 0xFF)
            {
                i = start;
                continue;
            }
            var len = i - start;
            if (len is < 10 or > MaxLen)
            {
                i = start;
                continue;
            }
            var text = Sm64Charset.TryDecode(rom.AsSpan(start, len));
            if (text is null || !EnglishText.LooksLike(text))
            {
                i = start;
                continue;
            }
            Mark(used, start, len + 1);
            lines.Add(new BkTextLine
            {
                AssetId = start,
                Kind = BkTextKind.Raw,
                Section = "sm64",
                Index = 0,
                OriginalBytes = rom[start..(start + len)],
                Original = text,
                Translation = text,
                Generic = true,
                RomOffset = start,
                SlotBytes = len + 1,
                Terminator = 0xFF,
                AllCaps = BkTextCodec.PreferAllCaps(text),
                Codec = "sm64"
            });
            added++;
            if (added % 20 == 0)
                progress?.Invoke($"Mario-tekst… {added} regels, ROM {start:X6}");
        }
    }

    private static void CollectAsciiInBuffer(byte[] data, Action<int, string, int> add)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] is < 0x20 or > 0x7E) continue;
            var start = i;
            while (i < data.Length && data[i] is >= 0x20 and <= 0x7E)
                i++;
            var len = i - start;
            var slot = len + (i < data.Length && data[i] == 0 ? 1 : 0);
            if (len < 8 || len > MaxLen) continue;
            var text = ReadAscii(data.AsSpan(start, len));
            if (!EnglishText.LooksLike(text)) continue;
            add(start, text, slot);
        }
    }

    private static bool IsPrintable(byte b) => b is >= 0x20 and <= 0x7E;

    private static void Mark(bool[] used, int start, int length)
    {
        var end = Math.Min(used.Length, start + Math.Max(1, length));
        for (var i = Math.Max(0, start); i < end; i++)
            used[i] = true;
    }
}
