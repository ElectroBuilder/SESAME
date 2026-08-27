using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VisualSSH.Models;

namespace VisualSSH.Services;

public sealed class HdPackRow
{
    public string Sheet { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Size { get; set; } = "";
    public string Status { get; set; } = "";
    public string Type { get; set; } = "";
    public string Site { get; set; } = "";
    public string Kind { get; set; } = "Texture pack";
    public string? DownloadUrl { get; set; }
    public string? BackupUrl { get; set; }
    public string? PageUrl { get; set; }
}

public sealed class HdPacksIndex
{
    public const string SourceName = "HD Packs List";
    public const string SheetUrl = "https://docs.google.com/spreadsheets/d/1sif8FeRGJRbytK8wFRXgF6Hke9V6GUFs/edit";
    public const string ExportUrl = "https://docs.google.com/spreadsheets/d/1sif8FeRGJRbytK8wFRXgF6Hke9V6GUFs/export?format=xlsx";
    public const string ForumUrl = "https://gbatemp.net/forums/retro-texture-packs.736/";

    private static readonly XNamespace Ss = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly HashSet<string> SkipSheets = new(StringComparer.OrdinalIgnoreCase)
    {
        "Overview", "Requests", "Unsupported", "Controller Layout", "Tools", "Blacklist"
    };

    private static readonly HashSet<string> DumpHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "vimm.net", "www.vimm.net", "romsfun.com", "romspure.cc", "cdromance.com",
        "nsw2u.com", "nxbrew.com", "ziperto.com", "romulation.org", "coolrom.com",
        "emuparadise.me", "romhustler.org", "loveemu.com", "roms-megathread"
    };

    private List<HdPackRow>? _rows;
    private DateTime _loadedAt;

    public async Task<IReadOnlyList<HdPackRow>> GetRowsAsync(HttpClient http, CancellationToken ct)
    {
        if (_rows is { Count: > 0 } && DateTime.UtcNow - _loadedAt < TimeSpan.FromHours(6))
            return _rows;

        var path = await EnsureWorkbookAsync(http, ct);
        _rows = ParseWorkbook(path);
        _loadedAt = DateTime.UtcNow;
        return _rows;
    }

    public async Task<List<PackHit>> SearchAsync(HttpClient http, StoreGame game, string query, string kind, CancellationToken ct)
    {
        var rows = await GetRowsAsync(http, ct);
        var extra = StoreGame.Normalize(query);
        var hits = new List<PackHit>();
        foreach (var row in rows)
        {
            if (!KindMatches(kind, row.Kind)) continue;
            if (!game.IsAll && !RowMatches(row, game)) continue;
            if (extra.Length >= 2)
            {
                var hay = StoreGame.Normalize($"{row.Title} {row.Author} {row.Type} {row.Site}");
                if (!hay.Contains(extra, StringComparison.Ordinal)) continue;
            }

            var page = FirstUrl(row.PageUrl, row.DownloadUrl, row.BackupUrl, ForumUrl);
            var download = FirstDirect(row.DownloadUrl, row.BackupUrl);
            hits.Add(new PackHit
            {
                Title = string.IsNullOrWhiteSpace(row.Author) || row.Kind == "Save"
                    ? (string.IsNullOrWhiteSpace(row.Site) ? row.Title : $"{row.Site} — {row.Title}")
                    : $"{row.Title} — {row.Author}",
                Source = SourceName,
                GameName = row.Title,
                Author = row.Author,
                Kind = row.Kind,
                PageUrl = page,
                DownloadUrl = download,
                    FileName = download is null ? null : Path.GetFileName(new Uri(download).AbsolutePath),
                    Size = RemoteItem.ParseSize(row.Size),
                    Summary = BuildSummary(row, download)
            });
        }

        return hits
            .GroupBy(h => h.PageUrl + "|" + h.Title, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(40)
            .ToList();
    }

    private static bool RowMatches(HdPackRow row, StoreGame game)
    {
        if (row.Kind == "Save")
            return game.MatchesSystem(row.Title) || game.MatchesSystem(row.Sheet);
        if (!game.MatchesSystem(row.Sheet)) return false;
        return game.MatchesTitle(row.Title);
    }

    private static bool KindMatches(string kind, string rowKind)
    {
        if (kind.Equals("Alles", StringComparison.OrdinalIgnoreCase)) return true;
        if (kind.Equals("Saves", StringComparison.OrdinalIgnoreCase)) return rowKind == "Save";
        if (kind.Equals("Mods", StringComparison.OrdinalIgnoreCase)) return false;
        return rowKind != "Save";
    }

    private static string BuildSummary(HdPackRow row, string? download)
    {
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.Size)) bits.Add(row.Size);
        if (!string.IsNullOrWhiteSpace(row.Status)) bits.Add(row.Status);
        if (!string.IsNullOrWhiteSpace(row.Type)) bits.Add(row.Type);
        bits.Add(download is null ? "Open de pagina voor de download" : "Directe download");
        return string.Join(" · ", bits);
    }

    private static async Task<string> EnsureWorkbookAsync(HttpClient http, CancellationToken ct)
    {
        var dir = AppDataPaths.Root;
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "hdpacks.xlsx");
        var fresh = File.Exists(dest) && DateTime.UtcNow - File.GetLastWriteTimeUtc(dest) < TimeSpan.FromDays(7);
        if (fresh) return dest;

        var temp = Path.Combine(Path.GetTempPath(), "hdpacks.xlsx");
        if (File.Exists(temp) && !File.Exists(dest))
        {
            File.Copy(temp, dest, overwrite: true);
            return dest;
        }

        try
        {
            using var response = await http.GetAsync(ExportUrl, ct);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(dest);
            await input.CopyToAsync(output, ct);
            return dest;
        }
        catch when (File.Exists(dest))
        {
            return dest;
        }
        catch when (File.Exists(temp))
        {
            File.Copy(temp, dest, overwrite: true);
            return dest;
        }
    }

    private static List<HdPackRow> ParseWorkbook(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = ReadSharedStrings(zip);
        var sheets = ReadSheetMap(zip);
        var rows = new List<HdPackRow>();
        foreach (var (name, part) in sheets)
        {
            if (SkipSheets.Contains(name)) continue;
            var entry = zip.GetEntry(part) ?? zip.GetEntry(part.Replace('\\', '/'));
            if (entry is null) continue;
            var rels = ReadRels(zip, part);
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            rows.AddRange(ParseSheet(name, doc, shared, rels));
        }
        return rows;
    }

    private static Dictionary<string, string> ReadSheetMap(ZipArchive zip)
    {
        var workbook = Load(zip, "xl/workbook.xml");
        var rels = ReadRels(zip, "xl/workbook.xml");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in workbook.Descendants(Ss + "sheet"))
        {
            var name = (string?)sheet.Attribute("name");
            var rid = (string?)sheet.Attribute(R + "id");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(rid)) continue;
            if (!rels.TryGetValue(rid, out var target)) continue;
            var part = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target.TrimStart('/');
            if (part.Contains("worksheets/", StringComparison.OrdinalIgnoreCase))
                map[name] = part.Replace('\\', '/');
        }
        return map;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var doc = Load(zip, "xl/sharedStrings.xml");
        var list = new List<string>();
        foreach (var si in doc.Descendants(Ss + "si"))
        {
            var text = string.Concat(si.Descendants(Ss + "t").Select(t => t.Value));
            list.Add(text.Replace('\n', ' ').Trim());
        }
        return list;
    }

    private static Dictionary<string, string> ReadRels(ZipArchive zip, string part)
    {
        var file = Path.GetFileName(part);
        var dir = Path.GetDirectoryName(part)?.Replace('\\', '/') ?? "xl";
        var relPath = $"{dir}/_rels/{file}.rels";
        var entry = zip.GetEntry(relPath) ?? zip.GetEntry(relPath.Replace('/', '\\'));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (entry is null) return map;
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var rel in doc.Descendants(Rel + "Relationship"))
        {
            var id = (string?)rel.Attribute("Id");
            var target = (string?)rel.Attribute("Target");
            var type = (string?)rel.Attribute("Type") ?? "";
            if (id is null || target is null) continue;
            if (type.Contains("hyperlink", StringComparison.OrdinalIgnoreCase) ||
                part.EndsWith("workbook.xml", StringComparison.OrdinalIgnoreCase))
                map[id] = target;
        }
        return map;
    }

    private static IEnumerable<HdPackRow> ParseSheet(string sheet, XDocument doc, List<string> shared,
        Dictionary<string, string> rels)
    {
        var cells = new Dictionary<(int Row, string Col), string>();
        foreach (var cell in doc.Descendants(Ss + "c"))
        {
            var refer = (string?)cell.Attribute("r");
            if (string.IsNullOrEmpty(refer) || !SplitRef(refer, out var col, out var row)) continue;
            cells[(row, col)] = CellText(cell, shared);
        }

        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hl in doc.Descendants(Ss + "hyperlink"))
        {
            var refer = (string?)hl.Attribute("ref");
            var rid = (string?)hl.Attribute(R + "id");
            if (refer is null || rid is null || !rels.TryGetValue(rid, out var url)) continue;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            links[refer] = url;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((row, col), text) in cells)
        {
            if (row != 1 || string.IsNullOrWhiteSpace(text)) continue;
            headers[col] = text.Trim();
        }

        var titleCol = FindCol(headers, "TITLE", "NAME");
        var authorCol = FindCol(headers, "AUTHOR");
        var sizeCol = FindCol(headers, "SIZE");
        var statusCol = FindCol(headers, "STATUS");
        var typeCol = FindCol(headers, "TYPE", "CATEGORY");
        var siteCol = FindCol(headers, "SITE", "CONSOLE");
        var downloadCols = headers.Where(kv => IsDownloadHeader(kv.Value)).Select(kv => kv.Key).ToList();
        var dumpCols = new HashSet<string>(headers.Where(kv => IsDumpHeader(kv.Value)).Select(kv => kv.Key),
            StringComparer.OrdinalIgnoreCase);
        var isSaves = sheet.Equals("Savefiles", StringComparison.OrdinalIgnoreCase);

        var maxRow = cells.Keys.Select(k => k.Row).DefaultIfEmpty(1).Max();
        var lastTitle = "";
        for (var row = 2; row <= maxRow; row++)
        {
            var title = Get(cells, row, titleCol);
            if (string.IsNullOrWhiteSpace(title)) title = lastTitle;
            else lastTitle = title;
            if (string.IsNullOrWhiteSpace(title) || title.Length <= 1) continue;

            string? download = null, backup = null, page = null;
            foreach (var col in downloadCols)
            {
                if (dumpCols.Contains(col)) continue;
                var url = LinkAt(links, col, row);
                if (url is null || IsBlocked(url)) continue;
                var header = headers.GetValueOrDefault(col, "");
                if (header.Contains("BACKUP", StringComparison.OrdinalIgnoreCase))
                    backup ??= url;
                else
                    download ??= url;
                page ??= url;
            }

            if (isSaves)
            {
                foreach (var (refer, url) in links)
                {
                    if (!SplitRef(refer, out _, out var r) || r != row) continue;
                    if (IsBlocked(url)) continue;
                    page ??= url;
                    download ??= PackUrl.IsDirectFile(url) ? url : null;
                }
            }

            if (page is null && download is null && backup is null && !isSaves)
                continue;

            yield return new HdPackRow
            {
                Sheet = sheet,
                Title = title,
                Author = Get(cells, row, authorCol),
                Size = Get(cells, row, sizeCol),
                Status = Get(cells, row, statusCol),
                Type = Get(cells, row, typeCol),
                Site = Get(cells, row, siteCol),
                Kind = isSaves ? "Save" : "Texture pack",
                DownloadUrl = download,
                BackupUrl = backup,
                PageUrl = page ?? backup
            };
        }
    }

    private static string? LinkAt(Dictionary<string, string> links, string col, int row)
    {
        if (links.TryGetValue(col + row, out var url)) return url;
        return null;
    }

    private static bool IsDownloadHeader(string header)
    {
        var h = header.ToUpperInvariant();
        if (IsDumpHeader(h)) return false;
        return h.Contains("DOWNLOAD") || h.Contains("TEXTURE") || h.Contains("BACKUP") ||
               h.Contains("PACK") || h.Contains("SAVE");
    }

    private static bool IsDumpHeader(string header)
    {
        var h = header.ToUpperInvariant();
        return h.Contains("DUMP") || h is "ISO" or "ROM" or "NSP" or "XCI";
    }

    private static bool IsBlocked(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;
        var host = uri.Host.ToLowerInvariant();
        if (DumpHosts.Contains(host)) return true;
        if (host.Contains("roms", StringComparison.Ordinal) && !host.Contains("forums", StringComparison.Ordinal))
            return true;
        return false;
    }

    private static string? FindCol(Dictionary<string, string> headers, params string[] names)
    {
        foreach (var name in names)
        {
            var hit = headers.FirstOrDefault(kv => kv.Value.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(hit.Key)) return hit.Key;
        }
        return null;
    }

    private static string Get(Dictionary<(int Row, string Col), string> cells, int row, string? col) =>
        col is not null && cells.TryGetValue((row, col), out var text) ? text : "";

    private static string CellText(XElement cell, List<string> shared)
    {
        var type = (string?)cell.Attribute("t");
        var value = cell.Element(Ss + "v")?.Value ?? "";
        if (type == "s" && int.TryParse(value, out var idx) && idx >= 0 && idx < shared.Count)
            return shared[idx];
        if (type == "inlineStr")
            return string.Concat(cell.Descendants(Ss + "t").Select(t => t.Value)).Trim();
        return value.Trim();
    }

    private static bool SplitRef(string refer, out string col, out int row)
    {
        var m = Regex.Match(refer, @"^([A-Z]+)(\d+)$", RegexOptions.IgnoreCase);
        col = m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
        row = m.Success ? int.Parse(m.Groups[2].Value) : 0;
        return m.Success;
    }

    private static XDocument Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? throw new InvalidOperationException("xlsx mist " + path);
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static string FirstUrl(params string?[] urls) =>
        urls.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)) ?? "";

    private static string? FirstDirect(params string?[] urls) =>
        urls.FirstOrDefault(PackUrl.IsDirectFile);
}
