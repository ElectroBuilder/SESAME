using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Sesame.Services.N64;

namespace Sesame.Services;

public static class TranslationSheet
{
    private static readonly string[] Headers =
        ["ID", "Kind", "Speaker", "English", "Dutch", "Chars", "Asset", "Section", "Index"];

    public static void Export(string path, IReadOnlyList<BkTextLine> lines)
    {
        if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            WriteXlsx(path, lines);
        else
            WriteCsv(path, lines);
    }

    public static int Import(string path, IList<BkTextLine> lines)
    {
        var rows = path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? ReadXlsx(path)
            : ReadCsv(path);
        var byKey = lines.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        var byEnglish = lines
            .GroupBy(l => l.Original, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var n = 0;
        foreach (var row in rows)
        {
            if (!byKey.TryGetValue(row.Key, out var line) &&
                !byEnglish.TryGetValue(row.English, out line))
                continue;
            if (string.IsNullOrWhiteSpace(row.Dutch)) continue;
            if (string.Equals(line.Translation, row.Dutch, StringComparison.Ordinal)) continue;
            line.Translation = row.Dutch;
            line.UserEdited = true;
            DutchTranslator.Remember(line.Original, line.Translation, userEdit: true);
            n++;
        }
        return n;
    }

    private static string Key(BkTextLine line) => $"{line.AssetId:X8}|{line.Section}|{line.Index}";

    private static void WriteCsv(string path, IReadOnlyList<BkTextLine> lines)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF');
        sb.AppendLine(string.Join(';', Headers.Select(Csv)));
        foreach (var line in lines)
        {
            sb.Append(Csv(line.IdText)).Append(';');
            sb.Append(Csv(line.KindText)).Append(';');
            sb.Append(Csv(line.Speaker)).Append(';');
            sb.Append(Csv(line.Original)).Append(';');
            sb.Append(Csv(line.Translation)).Append(';');
            sb.Append(Csv(line.LengthText)).Append(';');
            sb.Append(Csv(line.AssetId.ToString("X8", CultureInfo.InvariantCulture))).Append(';');
            sb.Append(Csv(line.Section)).Append(';');
            sb.AppendLine(Csv(line.Index.ToString(CultureInfo.InvariantCulture)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Csv(string? value)
    {
        var t = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (t.Contains(';') || t.Contains('"') || t.Contains('\n'))
            return "\"" + t.Replace("\"", "\"\"") + "\"";
        return t;
    }

    private static List<(string Key, string English, string Dutch)> ReadCsv(string path)
    {
        var text = File.ReadAllText(path);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var rows = new List<(string, string, string)>();
        var sep = text.Contains(';') ? ';' : ',';
        using var reader = new StringReader(text);
        var header = reader.ReadLine();
        if (header is null) return rows;
        var cols = SplitCsv(header, sep);
        var iId = IndexOf(cols, "ID", "Asset");
        var iEn = IndexOf(cols, "English", "Engels");
        var iNl = IndexOf(cols, "Dutch", "Nederlands");
        var iSec = IndexOf(cols, "Section", "Sectie");
        var iIdx = IndexOf(cols, "Index");
        var iAsset = IndexOf(cols, "Asset");
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var c = SplitCsv(line, sep);
            var english = Get(c, iEn);
            var dutch = Get(c, iNl);
            var asset = Get(c, iAsset >= 0 ? iAsset : iId);
            var section = Get(c, iSec);
            var index = Get(c, iIdx);
            var key = $"{NormalizeAsset(asset)}|{section}|{index}";
            rows.Add((key, english, dutch));
        }
        return rows;
    }

    private static string NormalizeAsset(string asset)
    {
        if (int.TryParse(asset.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n))
            return n.ToString("X8", CultureInfo.InvariantCulture);
        return asset.Trim();
    }

    private static string Get(string[] cols, int index) =>
        index >= 0 && index < cols.Length ? cols[index] : "";

    private static int IndexOf(string[] cols, params string[] names)
    {
        for (var i = 0; i < cols.Length; i++)
            foreach (var name in names)
                if (string.Equals(cols[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return i;
        return -1;
    }

    private static string[] SplitCsv(string line, char sep)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == sep)
            {
                list.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(ch);
        }
        list.Add(sb.ToString());
        return list.ToArray();
    }

    private static readonly XNamespace Ss = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static void WriteXlsx(string path, IReadOnlyList<BkTextLine> lines)
    {
        var rows = new StringBuilder();
        rows.Append("<row r=\"1\">");
        for (var i = 0; i < Headers.Length; i++)
            rows.Append(InlineCell(1, i, Headers[i]));
        rows.Append("</row>");
        for (var r = 0; r < lines.Count; r++)
        {
            var line = lines[r];
            var row = r + 2;
            rows.Append("<row r=\"").Append(row).Append("\">");
            rows.Append(InlineCell(row, 0, line.IdText));
            rows.Append(InlineCell(row, 1, line.KindText));
            rows.Append(InlineCell(row, 2, line.Speaker));
            rows.Append(InlineCell(row, 3, line.Original));
            rows.Append(InlineCell(row, 4, line.Translation));
            rows.Append(InlineCell(row, 5, line.LengthText));
            rows.Append(InlineCell(row, 6, line.AssetId.ToString("X8", CultureInfo.InvariantCulture)));
            rows.Append(InlineCell(row, 7, line.Section));
            rows.Append(InlineCell(row, 8, line.Index.ToString(CultureInfo.InvariantCulture)));
            rows.Append("</row>");
        }

        var sheet = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
            <sheetData>
            """ + rows + "</sheetData></worksheet>";

        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteZip(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        WriteZip(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteZip(zip, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Teksten" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteZip(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        WriteZip(zip, "xl/worksheets/sheet1.xml", sheet);
    }

    private static string InlineCell(int row, int col, string value)
    {
        var refName = ColName(col) + row;
        return $"<c r=\"{refName}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEscape(value)}</t></is></c>";
    }

    private static string ColName(int index)
    {
        var n = index + 1;
        var s = "";
        while (n > 0)
        {
            n--;
            s = (char)('A' + n % 26) + s;
            n /= 26;
        }
        return s;
    }

    private static string XmlEscape(string value) =>
        (value ?? "")
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

    private static void WriteZip(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private static List<(string Key, string English, string Dutch)> ReadXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidDataException("No worksheet found in this Excel file.");
        using var stream = sheet.Open();
        var doc = XDocument.Load(stream);
        var rows = new List<(string, string, string)>();
        var header = true;
        int iEn = 3, iNl = 4, iAsset = 6, iSec = 7, iIdx = 8, iId = 0;
        foreach (var row in doc.Descendants(Ss + "row"))
        {
            var cells = row.Elements(Ss + "c")
                .Select(c => (Ref: (string?)c.Attribute("r") ?? "", Text: CellText(c)))
                .ToList();
            string CellAt(int col)
            {
                var name = ColName(col);
                return cells.FirstOrDefault(c => c.Ref.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                                                 && c.Ref.Length > name.Length
                                                 && char.IsDigit(c.Ref[name.Length])).Text ?? "";
            }

            if (header)
            {
                var labels = Enumerable.Range(0, 12).Select(CellAt).ToArray();
                iId = IndexOf(labels, "ID", "Asset");
                iEn = IndexOf(labels, "Engels", "English");
                iNl = IndexOf(labels, "Dutch", "Nederlands");
                iSec = IndexOf(labels, "Sectie", "Section");
                iIdx = IndexOf(labels, "Index");
                iAsset = IndexOf(labels, "Asset");
                header = false;
                continue;
            }

            var english = CellAt(iEn);
            var dutch = CellAt(iNl);
            var asset = CellAt(iAsset >= 0 ? iAsset : iId);
            var key = $"{NormalizeAsset(asset)}|{CellAt(iSec)}|{CellAt(iIdx)}";
            rows.Add((key, english, dutch));
        }
        return rows;
    }

    private static string CellText(XElement cell)
    {
        var t = cell.Element(Ss + "is")?.Element(Ss + "t")?.Value
                ?? cell.Element(Ss + "v")?.Value
                ?? "";
        return t;
    }
}
