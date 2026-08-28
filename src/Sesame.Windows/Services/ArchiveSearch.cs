using System.IO;
using System.Net.Http;
using System.Text.Json;
using Sesame.Models;

namespace Sesame.Services;

public static class ArchiveSearch
{
    public const string SourceName = "Internet Archive";
    private const string ScrapeUrl = "https://archive.org/services/search/v1/scrape";
    private const string MetaUrl = "https://archive.org/metadata/";
    private const long MaxItemBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxPatchBytes = 250L * 1024 * 1024;

    private static readonly string[] AllowedExt =
        [".zip", ".rar", ".7z", ".bps", ".ips", ".ups", ".xdelta", ".hts", ".htc", ".pak"];

    private static readonly string[] RomExt =
        [".z64", ".n64", ".v64", ".nes", ".unf", ".sfc", ".smc", ".gba", ".nds", ".iso", ".gcm",
         ".rvz", ".ciso", ".nsp", ".xci", ".cia", ".wbfs", ".wad", ".bin", ".cue", ".chd"];

    private static readonly string[] RejectHay =
    [
        "romset", "rom set", "rom collection", "complete rom", "patched rom", "beta rom",
        "rom leak", "rom dump", "iso dump", "game dump", "bios", "firmware", "blooper",
        "machinima", "speedrun", "soundtrack", " ost", "midi", "ntsc-u", "ntsc-j", "pal rom",
        "juegos", "full rom", "roms part", "classified remake"
    ];

    public static async Task<List<PackHit>> SearchAsync(HttpClient http, string query, StoreGame game,
        string kind, CancellationToken ct)
    {
        if (game.IsAll && query.Trim().Length < 3)
            return [];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var url = ScrapeUrl
                  + "?count=100&fields=" + Uri.EscapeDataString(
                      "identifier,title,mediatype,item_size,downloads,creator,date,subject")
                  + "&q=" + Uri.EscapeDataString(BuildQuery(game, query, kind));

        using var response = await http.GetAsync(url, timeout.Token);
        if ((int)response.StatusCode is 502 or 503)
            throw new InvalidOperationException(
                "Internet Archive is overbelast. Probeer het later of kies een andere bron.");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];

        var hits = new List<PackHit>();
        foreach (var item in items.EnumerateArray())
        {
            var hit = ToHit(item, game, kind);
            if (hit is not null) hits.Add(hit);
        }

        return hits
            .GroupBy(h => h.PageUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(24)
            .ToList();
    }

    public static async Task InspectAsync(HttpClient http, PackHit hit, CancellationToken ct)
    {
        if (!string.Equals(hit.Source, SourceName, StringComparison.OrdinalIgnoreCase)) return;
        var id = hit.ItemId;
        if (string.IsNullOrWhiteSpace(id)) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var doc = JsonDocument.Parse(await http.GetStringAsync(MetaUrl + Uri.EscapeDataString(id), timeout.Token));
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return;

        var shots = new List<string>();
        JsonElement? best = null;
        var bestScore = -1;
        long bestSize = 0;
        foreach (var file in files.EnumerateArray())
        {
            var name = file.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("__", StringComparison.Ordinal)) continue;
            var ext = Path.GetExtension(name).ToLowerInvariant();
            var size = ReadSize(file);
            if (IsImageName(name))
            {
                if (!name.Contains("_thumb", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("itemimage", StringComparison.OrdinalIgnoreCase))
                    shots.Add(ArchiveFileUrl(id, name));
                continue;
            }

            if (RomExt.Contains(ext)) continue;
            if (!AllowedExt.Contains(ext)) continue;
            var score = ScoreArchiveFile(name, hit.Kind);
            if (score > bestScore || (score == bestScore && size > bestSize))
            {
                best = file;
                bestScore = score;
                bestSize = size;
            }
        }

        if (best is JsonElement chosen)
        {
            var name = chosen.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            hit.FileName = name;
            hit.DownloadUrl = ArchiveFileUrl(id, name);
            if (bestSize > 0) hit.Size = bestSize;
        }

        if (shots.Count > 0)
        {
            hit.ScreenshotUrls = shots.Take(8).ToList();
            hit.ImageUrl ??= shots[0];
        }
        hit.ImageUrl ??= $"https://archive.org/services/img/{Uri.EscapeDataString(id)}";
    }

    private static PackHit? ToHit(JsonElement item, StoreGame game, string kind)
    {
        var id = item.TryGetProperty("identifier", out var idEl) ? idEl.GetString() ?? "" : "";
        var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var media = item.TryGetProperty("mediatype", out var m) ? m.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;
        if (media is "movies" or "audio" or "collection") return null;

        var subject = JoinSubject(item);
        var hay = $"{title} {subject} {id}".ToLowerInvariant();
        if (RejectHay.Any(x => hay.Contains(x, StringComparison.Ordinal))) return null;
        if (LooksLikeDump(hay)) return null;

        var classified = Classify(hay);
        if (string.IsNullOrEmpty(classified)) return null;
        if (!KindWanted(kind, classified)) return null;
        if (!game.IsAll && !MatchesGame(game, title, subject, id)) return null;

        var size = item.TryGetProperty("item_size", out var s) && s.ValueKind == JsonValueKind.Number
            ? s.GetInt64() : 0;
        if (size > MaxItemBytes) return null;
        if (classified == "ROM-hack" && size > MaxPatchBytes) return null;

        var downloads = item.TryGetProperty("downloads", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetInt32() : 0;
        var creator = item.TryGetProperty("creator", out var c)
            ? (c.ValueKind == JsonValueKind.Array
                ? string.Join(", ", c.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)))
                : c.GetString() ?? "")
            : "";

        DateTime? added = null;
        if (item.TryGetProperty("date", out var dateEl) && dateEl.GetString() is { Length: >= 4 } dateText
            && DateTime.TryParse(dateText, out var parsed))
            added = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

        return new PackHit
        {
            Title = title.Trim(),
            Source = SourceName,
            GameName = game.IsAll ? "" : game.Name,
            Author = creator,
            Kind = classified,
            Platform = game.IsAll ? "" : game.System,
            OriginalGame = game.IsAll ? "" : game.Name,
            ItemId = id,
            PageUrl = "https://archive.org/details/" + id,
            ImageUrl = "https://archive.org/services/img/" + id,
            ScreenshotUrls = ["https://archive.org/services/img/" + id],
            Size = size,
            DownloadCount = downloads,
            AddedUtc = added,
            Summary = classified == "ROM-hack"
                ? "Patch-archief — SESAME downloadt geen volledige ROMs."
                : "Internet Archive-item, gefilterd op texture packs en patches."
        };
    }

    private static string BuildQuery(StoreGame game, string query, string kind)
    {
        var gameClause = game.IsAll
            ? Quote(query.Trim())
            : "(" + string.Join(" OR ", GameTerms(game).Select(Quote)) + ")";
        var extra = query.Trim();
        if (!game.IsAll && extra.Length >= 3 &&
            !game.MatchesTitle(extra) && !GameTerms(game).Contains(extra, StringComparer.OrdinalIgnoreCase))
            gameClause += " AND " + Quote(extra);

        return $"{gameClause} AND ({KindQuery(kind)}) AND mediatype:(software OR data OR image) AND NOT mediatype:(movies OR audio)";
    }

    private static string KindQuery(string kind)
    {
        if (kind.Equals("Texture packs", StringComparison.OrdinalIgnoreCase))
            return "title:(\"texture pack\" OR \"hd texture\" OR hires OR \"hi-res\" OR retexture OR texturepack) OR subject:(\"texture pack\" OR \"hd textures\")";
        if (kind.Equals("ROM-hacks", StringComparison.OrdinalIgnoreCase))
            return "title:(patch OR ips OR bps OR ups OR \"romhack patch\" OR \"rom hack patch\") OR subject:(patch OR ips OR bps)";
        if (kind.Equals("Mods", StringComparison.OrdinalIgnoreCase))
            return "title:(mod OR homebrew OR texture OR patch) OR subject:(mod OR homebrew OR texture OR patch)";
        return "title:(\"texture pack\" OR \"hd texture\" OR hires OR patch OR ips OR bps OR ups OR homebrew OR \"rom hack patch\") OR subject:(\"texture pack\" OR patch OR homebrew)";
    }

    private static IEnumerable<string> GameTerms(StoreGame game)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in new[] { game.Name }.Concat(game.Aliases).Concat(game.KingSlugs))
        {
            var clean = term.Trim();
            if (clean.Length < 3 || !seen.Add(clean)) continue;
            yield return clean;
        }
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "") + "\"";

    private static bool MatchesGame(StoreGame game, string title, string subject, string id)
    {
        if (game.MatchesTitle(title) || game.MatchesTitle(subject)) return true;
        var hay = $"{title} {subject} {id}";
        return game.TitlePhrases().Any(p => StoreGame.ContainsPhrase(hay, p)) ||
               game.Aliases.Any(a => hay.Contains(a, StringComparison.OrdinalIgnoreCase)) ||
               game.KingSlugs.Any(s => id.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                                       hay.Contains(s.Replace("-", " "), StringComparison.OrdinalIgnoreCase));
    }

    private static string Classify(string hay)
    {
        if (hay.Contains("texture") || hay.Contains("hires") || hay.Contains("hi-res") ||
            hay.Contains("hd pack") || hay.Contains("retexture") || hay.Contains("hd texture"))
            return "Texture pack";
        if (hay.Contains("ips") || hay.Contains("bps") || hay.Contains("ups") ||
            hay.Contains("romhack patch") || hay.Contains("rom hack patch") ||
            (hay.Contains("patch") && (hay.Contains("hack") || hay.Contains("rom"))))
            return "ROM-hack";
        if (hay.Contains("homebrew") || hay.Contains(" mod") || hay.StartsWith("mod "))
            return "Mod";
        return "";
    }

    private static bool KindWanted(string kind, string classified)
    {
        if (kind.Equals("Alles", StringComparison.OrdinalIgnoreCase)) return true;
        if (kind.Equals("Texture packs", StringComparison.OrdinalIgnoreCase)) return classified == "Texture pack";
        if (kind.Equals("ROM-hacks", StringComparison.OrdinalIgnoreCase)) return classified == "ROM-hack";
        if (kind.Equals("Mods", StringComparison.OrdinalIgnoreCase)) return classified is "Mod" or "ROM-hack" or "Texture pack";
        if (kind.Equals("Saves", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool LooksLikeDump(string hay) =>
        hay.Contains(" roms") || hay.Contains("roms ") || hay.Contains("-roms") ||
        hay.Contains("iso") && hay.Contains("dump") ||
        hay.Contains("nsp") || hay.Contains("xci") || hay.Contains("cia dump");

    private static string JoinSubject(JsonElement item)
    {
        if (!item.TryGetProperty("subject", out var el)) return "";
        if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? "";
        if (el.ValueKind != JsonValueKind.Array) return "";
        return string.Join(" ", el.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static int ScoreArchiveFile(string name, string kind)
    {
        var n = name.ToLowerInvariant();
        var score = 1;
        if (n.Contains("texture") || n.Contains("hires") || n.Contains("hd")) score += 8;
        if (n.Contains("patch") || n.EndsWith(".bps") || n.EndsWith(".ips") || n.EndsWith(".ups")) score += 6;
        if (kind.Equals("Texture pack", StringComparison.OrdinalIgnoreCase) &&
            (n.Contains("texture") || n.EndsWith(".hts") || n.EndsWith(".htc")))
            score += 6;
        if (n.EndsWith(".zip") || n.EndsWith(".7z")) score += 2;
        return score;
    }

    private static bool IsImageName(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp";
    }

    private static string ArchiveFileUrl(string id, string name) =>
        $"https://archive.org/download/{Uri.EscapeDataString(id)}/{string.Join("/", name.Split('/').Select(Uri.EscapeDataString))}";

    private static long ReadSize(JsonElement file)
    {
        if (!file.TryGetProperty("size", out var el)) return 0;
        if (el.ValueKind == JsonValueKind.Number) return el.GetInt64();
        return long.TryParse(el.GetString(), out var n) ? n : 0;
    }
}
