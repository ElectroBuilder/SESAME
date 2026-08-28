using System.IO;
using System.IO.Compression;
using SharpCompress.Archives;
using Sesame.Services.N64;

namespace Sesame.Services;

public static class RomContainer
{
    private const int MaxNesting = 4;

    private static readonly HashSet<string> ArchiveExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar"
    };

    private static readonly HashSet<string> N64Ext = new(StringComparer.OrdinalIgnoreCase)
    {
        ".z64", ".n64", ".v64"
    };

    private static readonly HashSet<string> SkipExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".nfo", ".diz", ".url", ".jpg", ".png", ".gif", ".md",
        ".ips", ".bps", ".ups", ".xdelta", ".sav", ".srm", ".srm.bak"
    };

    public static bool IsArchivePath(string path) =>
        ArchiveExt.Contains(Path.GetExtension(path));

    public static bool IsArchiveBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;
        if (data[0] == (byte)'P' && data[1] == (byte)'K') return true;
        if (data[0] == (byte)'R' && data[1] == (byte)'a' && data[2] == (byte)'r' && data[3] == (byte)'!')
            return true;
        return data.Length >= 6 && data[0] == 0x37 && data[1] == 0x7A && data[2] == 0xBC && data[3] == 0xAF;
    }

    public static byte[] ReadRom(string path, string? preferName = null) =>
        ReadRom(path, preferName, 0);

    private static byte[] ReadRom(string path, string? preferName, int depth)
    {
        if (depth > MaxNesting)
            throw new InvalidDataException("Te veel geneste archieven in " + Path.GetFileName(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("ROM niet gevonden: " + path);

        var raw = File.ReadAllBytes(path);
        if (!IsArchivePath(path) && !IsArchiveBytes(raw))
            return raw;

        Exception? last = null;
        try { return PickRom(CollectZipArchive(path), preferName, depth); }
        catch (Exception ex) { last = ex; }
        try { return PickRom(CollectSharpCompress(path, raw), preferName, depth); }
        catch (Exception ex) { last = ex; }

        throw new InvalidDataException(
            "Kon de ROM niet uit " + Path.GetFileName(path) + " halen. Eerste bytes: " + FirstBytes(raw) +
            (last is null ? "" : " (" + last.Message + ")"));
    }

    public static byte[] ReadRomFromBytes(byte[] data, string? preferName = null) =>
        ReadRomFromBytes(data, preferName, 0);

    private static byte[] ReadRomFromBytes(byte[] data, string? preferName, int depth)
    {
        if (!IsArchiveBytes(data)) return data;
        var temp = Path.Combine(Path.GetTempPath(), "SESAME", "rompeek",
            Guid.NewGuid().ToString("N")[..8] + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.WriteAllBytes(temp, data);
        try { return ReadRom(temp, preferName, depth); }
        finally
        {
            try { File.Delete(temp); } catch { /* ignore */ }
        }
    }

    public static string? BestInnerName(IEnumerable<string> entries, string? preferName = null)
    {
        var names = entries
            .Select(NormalizeEntry)
            .Where(n => n.Length > 0 && !ShouldSkip(n))
            .ToList();
        if (names.Count == 0) return null;
        return names.OrderByDescending(n => Score(n, preferName, 0, n64: false)).First();
    }

    public static string RomExtension(byte[] data, string originalPath)
    {
        if (N64Rom.LooksLikeN64(data)) return ".z64";
        if (data.Length > 16 && data[0] == (byte)'N' && data[1] == (byte)'E' && data[2] == (byte)'S')
            return ".nes";
        if (RomPatcher.LooksGba(data)) return ".gba";
        if (RomPatcher.LooksNds(data)) return ".nds";
        if (RomPatcher.LooksGenesisBin(data) || RomPatcher.LooksSmd(data)) return ".md";
        var inner = Path.GetExtension(originalPath);
        if (!string.IsNullOrEmpty(inner) && !IsArchivePath(originalPath))
            return inner;
        return ".bin";
    }

    public static string InnerRomFileName(string? innerName, string fallbackPath, byte[] rom)
    {
        var name = !string.IsNullOrWhiteSpace(innerName)
            ? Path.GetFileName(innerName)
            : Path.GetFileName(fallbackPath);
        if (string.IsNullOrWhiteSpace(name) || IsArchivePath(name))
            name = Path.GetFileNameWithoutExtension(fallbackPath) + RomExtension(rom, fallbackPath);
        if (IsArchivePath(name))
            name = Path.GetFileNameWithoutExtension(name) + RomExtension(rom, name);
        return name;
    }

    public static bool PreferZipOutput(string originalName, string system)
    {
        if (IsArchivePath(originalName)) return true;
        var sys = system.Trim().ToUpperInvariant();
        return sys is "NES" or "SNES" or "N64" or "GB" or "GBC" or "GBA" or "NDS" or "DS"
            or "MD" or "GENESIS" or "MEGADRIVE" or "SMS";
    }

    public static void WriteOutput(string destPath, byte[] rom, string? innerName = null)
    {
        if (!IsArchivePath(destPath))
        {
            File.WriteAllBytes(destPath, rom);
            return;
        }

        if (File.Exists(destPath))
            File.Delete(destPath);
        var entryName = InnerRomFileName(innerName, destPath, rom);
        using var zip = ZipFile.Open(destPath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(rom, 0, rom.Length);
    }

    public static string FirstBytes(ReadOnlySpan<byte> data)
    {
        var n = Math.Min(8, data.Length);
        var parts = new string[n];
        for (var i = 0; i < n; i++) parts[i] = data[i].ToString("X2");
        return string.Join(" ", parts);
    }

    private static byte[] PickRom(List<(string Name, byte[] Data)> items, string? preferName, int depth)
    {
        if (items.Count == 0)
            throw new InvalidDataException("Het archief heeft geen uitpakbare bestanden.");

        var resolved = new List<(string Name, byte[] Data)>(items.Count);
        foreach (var (name, data) in items)
        {
            var blob = data;
            if (IsArchiveBytes(blob) && depth < MaxNesting)
            {
                try { blob = ReadRomFromBytes(blob, preferName, depth + 1); }
                catch { /* keep original blob; scored below */ }
            }
            resolved.Add((name, blob));
        }

        var n64Hits = resolved.Where(x => N64Rom.LooksLikeN64(x.Data)).ToList();
        if (n64Hits.Count > 0)
            return n64Hits.OrderByDescending(x => Score(x.Name, preferName, x.Data.Length, n64: true)).First().Data;

        var namedN64 = items.Any(x => N64Ext.Contains(Path.GetExtension(NormalizeEntry(x.Name))));
        if (namedN64)
            throw new InvalidDataException(
                "Zip bevat " + string.Join(", ", items.Select(x => NormalizeEntry(x.Name))) +
                " maar geen N64-magic (80 37 / 37 80 / 40 12).");

        var usable = resolved.Where(x => !IsArchiveBytes(x.Data)).ToList();
        if (usable.Count == 0)
            throw new InvalidDataException(
                "Geen herkenbare ROM in archief. Bestanden: " +
                string.Join(", ", items.Select(x => NormalizeEntry(x.Name))));

        return usable.OrderByDescending(x => Score(x.Name, preferName, x.Data.Length, n64: false)).First().Data;
    }

    private static List<(string Name, byte[] Data)> CollectZipArchive(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var items = new List<(string, byte[])>();
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || ShouldSkip(entry.FullName)) continue;
            using var stream = entry.Open();
            using var output = new MemoryStream();
            stream.CopyTo(output);
            var data = output.ToArray();
            if (data.Length < 0x40) continue;
            items.Add((entry.FullName, data));
        }
        return items;
    }

    private static List<(string Name, byte[] Data)> CollectSharpCompress(string path, byte[] raw)
    {
        var archivePath = path;
        var tempCopy = false;
        if (!IsArchivePath(path))
        {
            archivePath = path + ".zip";
            File.WriteAllBytes(archivePath, raw);
            tempCopy = true;
        }

        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var items = new List<(string, byte[])>();
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                var key = entry.Key ?? "";
                if (ShouldSkip(key)) continue;
                using var stream = entry.OpenEntryStream();
                using var output = new MemoryStream();
                stream.CopyTo(output);
                var data = output.ToArray();
                if (data.Length < 0x40) continue;
                items.Add((key, data));
            }
            return items;
        }
        finally
        {
            if (tempCopy)
            {
                try { File.Delete(archivePath); } catch { /* ignore */ }
            }
        }
    }

    private static string NormalizeEntry(string name)
    {
        name = name.Replace('\\', '/').Trim();
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    private static bool ShouldSkip(string name)
    {
        var file = NormalizeEntry(name);
        if (file.Length == 0 || file.StartsWith(".", StringComparison.Ordinal) ||
            name.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase))
            return true;
        return SkipExt.Contains(Path.GetExtension(file));
    }

    private static int Score(string name, string? preferName, long size, bool n64)
    {
        var file = NormalizeEntry(name);
        var ext = Path.GetExtension(file);
        var score = 0;
        if (n64) score += 1000;
        if (ext.Equals(".z64", StringComparison.OrdinalIgnoreCase)) score += 200;
        else if (ext.Equals(".n64", StringComparison.OrdinalIgnoreCase)) score += 190;
        else if (ext.Equals(".v64", StringComparison.OrdinalIgnoreCase)) score += 180;
        else if (ext is ".cue" or ".chd" or ".m3u" or ".pbp")
            score += 160;
        else if (ext is ".gba" or ".nds" or ".md" or ".gen" or ".smd" or ".sfc" or ".smc" or ".nes")
            score += 170;
        if (!string.IsNullOrWhiteSpace(preferName) &&
            file.Contains(preferName, StringComparison.OrdinalIgnoreCase))
            score += 300;
        if (file.Contains("banjo", StringComparison.OrdinalIgnoreCase) ||
            file.Contains("kazooie", StringComparison.OrdinalIgnoreCase))
            score += 80;
        if (size is >= 4 * 1024 * 1024 and <= 64L * 1024 * 1024) score += 40;
        return score;
    }
}
