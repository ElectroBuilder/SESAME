using System.IO;

namespace VisualSSH.Services.N64;

public static class Sm64Text
{
    private static readonly int[] KnownMio0 =
    [
        0x108A40, // USA
        0x108A40, // PAL often nearby; scan covers the rest
    ];

    private const int UnusedStart = 0x7C94;
    private const int DialogStart = 0x7D34;
    private const int DialogTable = 0x0FFC8;
    private const int DialogTableEnd = 0x10D14;
    private const int LevelStart = 0x10D14;
    private const int LevelTable = 0x10F68;
    private const int LevelTableEnd = 0x10FD4;
    private const int ActStart = 0x10FD4;
    private const int ActTable = 0x1192C;
    private const int ActTableEnd = 0x11AC0;
    private const int MaxString = 2048;

    public static bool LooksLike(byte[] rom)
    {
        var id = N64Rom.CartId(rom);
        if (id.StartsWith("NSM", StringComparison.OrdinalIgnoreCase)) return true;
        var name = N64Rom.InternalName(rom);
        return name.Contains("SUPER MARIO", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("MARIO 64", StringComparison.OrdinalIgnoreCase);
    }

    public static List<BkTextLine> Extract(byte[] rom, Action<string>? progress = null)
    {
        progress?.Invoke("Mario 64-dialoogblok zoeken…");
        if (!TryFindSegment(rom, out var mio0At, out var raw, out var consumed))
            return [];

        var lines = new List<BkTextLine>();
        AddTable(lines, raw, DialogTable, DialogTableEnd, 16, 12, DialogStart, DialogTable, mio0At, consumed, "dialog");
        AddRegion(lines, raw, UnusedStart, DialogStart, mio0At, consumed, "dialog");
        AddTable(lines, raw, LevelTable, LevelTableEnd, 4, 0, LevelStart, LevelTable, mio0At, consumed, "level");
        AddTable(lines, raw, ActTable, ActTableEnd, 4, 0, ActStart, ActTable, mio0At, consumed, "act");
        if (lines.Count < 20)
        {
            AddRegion(lines, raw, DialogStart, DialogTable, mio0At, consumed, "dialog");
            AddRegion(lines, raw, LevelStart, LevelTable, mio0At, consumed, "level");
            AddRegion(lines, raw, ActStart, ActTable, mio0At, consumed, "act");
        }
        progress?.Invoke($"Mario 64-dialoog… {lines.Count} regels");
        return lines.Count >= 20 ? lines : [];
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

        var changed = lines.Where(l => l.Codec == "sm64" && l.Changed && !string.IsNullOrWhiteSpace(l.Translation)).ToList();
        if (changed.Count == 0)
            throw new InvalidDataException("Geen gewijzigde Mario 64-teksten om in de ROM te zetten.");

        var sample = changed[0];
        if (!Mio0.TryDecode(rom, sample.RomOffset, out var raw, out var consumed))
            throw new InvalidDataException("Mario 64-tekstblok is niet meer te openen.");

        Report(20, "Mario-dialoog opnieuw inpakken…");
        var next = (byte[])raw.Clone();
        var all = lines.Where(l => l.Codec == "sm64").ToList();
        var written = 0;
        var skipped = 0;
        written += Repack(next, all, "dialog", DialogStart, DialogTable);
        written += Repack(next, all, "level", LevelStart, LevelTable);
        written += Repack(next, all, "act", ActStart, ActTable);
        foreach (var line in changed.Where(l => l.TablePtr <= 0))
        {
            if (line.InnerOffset < 0 || line.InnerSlot <= 0) continue;
            if (line.InnerOffset + line.InnerSlot > next.Length)
            {
                skipped++;
                continue;
            }
            var payload = Sm64Charset.Encode(line.Translation);
            var room = Math.Max(1, line.InnerSlot - 1);
            if (payload.Length > room) payload = payload[..room];
            Array.Clear(next, line.InnerOffset, line.InnerSlot);
            Buffer.BlockCopy(payload, 0, next, line.InnerOffset, payload.Length);
            next[line.InnerOffset + payload.Length] = 0xFF;
            written++;
        }

        Report(80, "Mario-tekstblok opnieuw inpakken…");
        var blob = Mio0.Encode(next);
        var oldStart = sample.RomOffset;
        var oldEnd = oldStart + consumed;
        var output = (byte[])rom.Clone();
        var relocated = 0;
        if (blob.Length <= consumed)
        {
            Array.Clear(output, oldStart, consumed);
            Buffer.BlockCopy(blob, 0, output, oldStart, blob.Length);
        }
        else
        {
            Report(86, "Mario-tekstblok verplaatsen naar extra ROM-ruimte…");
            output = RomSpace.Place(output, blob, 16, out var at);
            var newEnd = RomSpace.Align(at + blob.Length, 16);
            if (!PatchMio0Refs(output, (uint)oldStart, (uint)oldEnd, (uint)at, (uint)newEnd))
                throw new InvalidDataException(
                    "De Nederlandse Mario-tekst paste niet in het originele gat en kon niet verplaatst worden.");
            relocated = 1;
        }

        if (N64Rom.LooksLikeN64(output))
        {
            Report(96, "Boot-checksum bijwerken…");
            N64Rom.RecalcCrc(output);
        }

        var summary = skipped == 0
            ? $"{written} Mario 64-teksten gezet in de ROM."
            : $"{written} teksten gezet, {skipped} pasten niet en blijven Engels.";
        if (relocated > 0)
            summary += " Tekstblok verplaatst naar extra ROM-ruimte.";
        Report(99, summary);
        return new RomBuildResult
        {
            Rom = output,
            Changed = written,
            Relocated = relocated,
            Errors = 0,
            Summary = summary
        };
    }

    private static bool PatchMio0Refs(byte[] rom, uint oldStart, uint oldEnd, uint newStart, uint newEnd)
    {
        var patched = 0;
        var asmLimit = Math.Min(rom.Length - 20, 0x200000);
        for (var addr = 0x1000; addr + 20 <= asmLimit; addr += 4)
        {
            if (Opcode(rom, addr) != 0x3C || Opcode(rom, addr + 4) != 0x3C || Opcode(rom, addr + 8) != 0x24)
                continue;
            var a1Addiu = Opcode(rom, addr + 0xC) == 0x24 ? 0xC
                : Opcode(rom, addr + 0x10) == 0x24 ? 0x10 : 0;
            if (a1Addiu == 0) continue;
            if (Rt(rom, addr) != Rt(rom, addr + a1Addiu)) continue;
            if (Rt(rom, addr + 4) != Rt(rom, addr + 8)) continue;
            if (La2Int(rom, addr, addr + a1Addiu) != oldStart) continue;
            WriteLa(rom, addr, addr + a1Addiu, newStart);
            WriteLa(rom, addr + 4, addr + 8, newEnd);
            patched++;
        }

        var scriptLimit = Math.Min(rom.Length - 12, 0x800000);
        for (var addr = 0xD0000; addr + 12 <= scriptLimit; addr += 4)
        {
            var cmd = rom[addr];
            if (cmd is not (0x17 or 0x18 or 0x1A) || rom[addr + 1] != 0x0C || rom[addr + 2] >= 0x02)
                continue;
            if (RomSpace.Be32(rom, addr + 4) != oldStart) continue;
            RomSpace.WriteBe32(rom, addr + 4, newStart);
            RomSpace.WriteBe32(rom, addr + 8, newEnd);
            patched++;
        }

        return patched > 0;
    }

    private static int Opcode(byte[] rom, int at) => rom[at] & 0xFC;

    private static int Rt(byte[] rom, int at) => rom[at + 1] & 0x1F;

    private static uint La2Int(byte[] rom, int lui, int addiu)
    {
        var high = RomSpace.Be16(rom, lui + 2);
        var low = RomSpace.Be16(rom, addiu + 2);
        if ((low & 0x8000) != 0) high--;
        return ((uint)high << 16) | low;
    }

    private static void WriteLa(byte[] rom, int lui, int addiu, uint addr)
    {
        var low = (ushort)(addr & 0xFFFF);
        var high = (ushort)(addr >> 16);
        if ((low & 0x8000) != 0) high++;
        RomSpace.WriteBe16(rom, lui + 2, high);
        RomSpace.WriteBe16(rom, addiu + 2, low);
    }

    private static bool TryFindSegment(byte[] rom, out int offset, out byte[] raw, out int consumed)
    {
        foreach (var at in KnownMio0)
        {
            if (Mio0.TryDecode(rom, at, out raw, out consumed) && LooksLikeSegment(raw))
            {
                offset = at;
                return true;
            }
        }

        var limit = Math.Min(rom.Length - 16, 0x400000);
        for (var i = 0x100000; i + 16 < limit; i += 4)
        {
            if (rom[i] != (byte)'M' || rom[i + 1] != (byte)'I') continue;
            if (!Mio0.TryDecode(rom, i, out raw, out consumed)) continue;
            if (!LooksLikeSegment(raw)) continue;
            offset = i;
            return true;
        }

        offset = 0;
        raw = [];
        consumed = 0;
        return false;
    }

    private static bool LooksLikeSegment(byte[] raw)
    {
        if (raw.Length <= DialogStart + 12) return false;
        var n = 0;
        while (DialogStart + n < raw.Length && n < 48 && raw[DialogStart + n] != 0xFF) n++;
        if (n < 8) return false;
        var text = Sm64Charset.TryDecode(raw.AsSpan(DialogStart, n));
        return text is not null && text.Count(char.IsLetter) >= 6;
    }

    private static int Repack(byte[] raw, List<BkTextLine> lines, string section, int textStart, int textEnd)
    {
        var items = lines.Where(l => l.Section == section && l.TablePtr > 0).ToList();
        if (items.Count == 0) return 0;
        var blobs = items.Select(l =>
        {
            var text = string.IsNullOrWhiteSpace(l.Translation) ? l.Original : l.Translation;
            return (Line: l, Data: Sm64Charset.Encode(text));
        }).ToList();

        var room = textEnd - textStart;
        var total = blobs.Sum(b => b.Data.Length + 1);
        while (total > room)
        {
            var i = 0;
            var longest = 0;
            for (var n = 0; n < blobs.Count; n++)
            {
                if (blobs[n].Data.Length > longest)
                {
                    longest = blobs[n].Data.Length;
                    i = n;
                }
            }
            if (longest <= 8) break;
            var cut = blobs[i].Data[..^Math.Max(4, longest / 20)];
            total -= blobs[i].Data.Length - cut.Length;
            blobs[i] = (blobs[i].Line, cut);
        }

        Array.Clear(raw, textStart, Math.Max(0, textEnd - textStart));
        var cursor = textStart;
        var written = 0;
        foreach (var (line, data) in blobs)
        {
            if (cursor + data.Length + 1 > textEnd) break;
            Buffer.BlockCopy(data, 0, raw, cursor, data.Length);
            raw[cursor + data.Length] = 0xFF;
            RomSpace.WriteBe32(raw, line.TablePtr, 0x02000000u | (uint)cursor);
            cursor += data.Length + 1;
            if (line.Changed) written++;
        }
        return written;
    }

    private static void AddTable(
        List<BkTextLine> lines, byte[] raw, int table, int tableEnd, int stride, int ptrAt,
        int textMin, int textMax, int mio0At, int consumed, string section)
    {
        if (raw.Length < tableEnd) return;
        for (var i = table; i + stride <= tableEnd; i += stride)
        {
            var ptrLoc = i + ptrAt;
            var ptr = RomSpace.Be32(raw, ptrLoc);
            var hi = ptr >> 24;
            if (hi is not (0 or 2)) continue;
            var from = (int)(ptr & 0xFFFFFF);
            if (from < textMin || from >= textMax || from >= raw.Length) continue;
            var end = from;
            while (end < textMax && end < from + MaxString && raw[end] != 0xFF)
                end++;
            if (end >= raw.Length || raw[end] != 0xFF) continue;
            var len = end - from;
            if (len < 3) continue;
            var text = Sm64Charset.TryDecode(raw.AsSpan(from, len));
            if (string.IsNullOrWhiteSpace(text) || text.Count(char.IsLetter) < 3) continue;
            lines.Add(MakeLine(from, text, raw, len, Math.Max(len + 1, 16), mio0At, consumed, section, lines.Count, ptrLoc));
        }
    }

    private static void AddRegion(
        List<BkTextLine> lines, byte[] raw, int start, int end, int mio0At, int consumed, string section)
    {
        if (raw.Length < end) return;
        var seen = new HashSet<int>(lines.Select(l => l.InnerOffset));
        var i = start;
        while (i < end)
        {
            while (i < end && (raw[i] == 0xFF || raw[i] == 0x00))
                i++;
            if (i >= end) break;
            var from = i;
            while (i < end && raw[i] != 0xFF)
                i++;
            if (i >= end) break;
            var len = i - from;
            i++;
            if (len is < 3 or > MaxString) continue;
            if (!seen.Add(from)) continue;
            var text = Sm64Charset.TryDecode(raw.AsSpan(from, len));
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.Count(char.IsLetter) < 3) continue;
            lines.Add(MakeLine(from, text, raw, len, len + 1, mio0At, consumed, section, lines.Count, 0));
        }
    }

    private static BkTextLine MakeLine(
        int from, string text, byte[] raw, int len, int slot, int mio0At, int consumed, string section, int index, int tablePtr) =>
        new()
        {
            AssetId = from,
            Kind = BkTextKind.Dialog,
            Section = section,
            Index = index,
            OriginalBytes = raw[from..(from + len)],
            Original = text,
            Translation = text,
            Generic = true,
            InPlaceBlob = true,
            RomOffset = mio0At,
            SlotBytes = consumed,
            InnerOffset = from,
            InnerSlot = slot,
            TablePtr = tablePtr,
            Terminator = 0xFF,
            AllCaps = BkTextCodec.PreferAllCaps(text),
            Codec = "sm64"
        };
}
