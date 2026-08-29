using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Sesame.Models;

namespace Sesame.Services.GameOptimizer;

public sealed class ArtworkSet
{
    public string Source { get; set; } = "";
    public int? GameId { get; set; }
    public string? GridUrl { get; set; }
    public string? WideUrl { get; set; }
    public string? HeroUrl { get; set; }
    public string? LogoUrl { get; set; }
    public string? IconUrl { get; set; }
    public byte[]? Grid { get; set; }
    public byte[]? Wide { get; set; }
    public byte[]? Hero { get; set; }
    public byte[]? Logo { get; set; }
    public byte[]? Icon { get; set; }
}

public static class ArtworkClient
{
    private const string Api = "https://www.steamgriddb.com/api/v2";
    private static readonly HttpClient Http = Create();

    public static string LastError { get; private set; } = "";
    public static bool LastKeyInvalid { get; private set; }

    public static async Task<ArtworkSet?> FindAsync(string title, SystemProfile system, CancellationToken ct)
    {
        LastError = "";
        LastKeyInvalid = false;
        if (OptimizerSettings.HasSteamGridDb)
        {
            var sgdb = await FromSteamGridDbAsync(title, system, ct);
            if (sgdb is not null) return sgdb;
            if (LastKeyInvalid) return null;
        }
        else
        {
            LastError = "No SteamGridDB key. Set it in Settings (top right).";
        }

        var libretro = await FromLibretroAsync(title, system, ct);
        if (libretro is not null) return libretro;
        if (string.IsNullOrEmpty(LastError))
            LastError = "No cover found for " + title + ".";
        return null;
    }

    public static async Task<List<ArtworkChoice>> ListGridsAsync(int gameId, CancellationToken ct) =>
        await ListKindAsync("grids", gameId, ct);

    public static async Task<List<ArtworkChoice>> ListAllAsync(int gameId, CancellationToken ct)
    {
        var list = new List<ArtworkChoice>();
        list.AddRange(await ListKindAsync("grids", gameId, ct));
        list.AddRange(await ListKindAsync("heroes", gameId, ct));
        list.AddRange(await ListKindAsync("logos", gameId, ct));
        list.AddRange(await ListKindAsync("icons", gameId, ct));
        return list;
    }

    public static async Task EnsureExtraAssetsAsync(ArtworkSet art, int? gameId, CancellationToken ct)
    {
        if (gameId is null) return;
        art.WideUrl ??= await FirstAssetAsync(
            $"grids/game/{gameId}?dimensions=920x430,460x215&nsfw=false&types=static", ct);
        art.HeroUrl ??= await FirstAssetAsync($"heroes/game/{gameId}?nsfw=false", ct);
        art.LogoUrl ??= await FirstAssetAsync($"logos/game/{gameId}?nsfw=false&types=static", ct);
        art.IconUrl ??= await FirstAssetAsync($"icons/game/{gameId}?nsfw=false", ct);
    }

    private static async Task<List<ArtworkChoice>> ListKindAsync(string endpoint, int gameId, CancellationToken ct)
    {
        var list = new List<ArtworkChoice>();
        var data = await ReadArrayAsync($"{endpoint}/game/{gameId}?nsfw=false&types=static", ct)
                   ?? (endpoint == "icons" ? await ReadArrayAsync($"{endpoint}/game/{gameId}?nsfw=false", ct) : null);
        if (data is null) return list;
        foreach (var item in data.Value.EnumerateArray())
        {
            var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) continue;
            var w = item.TryGetProperty("width", out var we) && we.TryGetInt32(out var wi) ? wi : 0;
            var h = item.TryGetProperty("height", out var he) && he.TryGetInt32(out var hi) ? hi : 0;
            var style = item.TryGetProperty("style", out var st) ? st.GetString() ?? "" : "";
            var author = ReadAuthor(item);
            var thumb = item.TryGetProperty("thumb", out var th) ? th.GetString() : url;
            var kind = endpoint switch
            {
                "heroes" => "hero",
                "logos" => "logo",
                "icons" => "icon",
                _ => h >= w && w > 0 ? "cover" : "wide"
            };
            var title = kind switch
            {
                "hero" => "Hero",
                "logo" => "Logo",
                "icon" => "Icon",
                "wide" => "Wide",
                _ => "Cover"
            };
            list.Add(new ArtworkChoice
            {
                Url = url,
                ThumbUrl = thumb ?? url,
                Width = w,
                Height = h,
                Kind = kind,
                Author = author,
                Style = style,
                Label = string.Join(" · ", new[] { title, style, author }.Where(s => !string.IsNullOrWhiteSpace(s)))
            });
        }
        return list;
    }

    public static async Task<int?> FindGameIdAsync(string title, SystemProfile system, CancellationToken ct) =>
        await SearchGameIdAsync(title, system, ct);

    public static async Task<(bool ok, string message)> ValidateKeyAsync(string key, CancellationToken ct)
    {
        var cleaned = OptimizerSettings.CleanKey(key);
        if (cleaned.Length < 16)
            return (false, "Paste the API key from steamgriddb.com/profile/preferences/api (not SGDBoop).");
        using var req = new HttpRequestMessage(HttpMethod.Get, Api + "/search/autocomplete/Mario");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cleaned);
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode)
            return (true, "SteamGridDB key works. Covers, heroes and logos will be fetched with it.");
        if ((int)resp.StatusCode is 401 or 403)
            return (false, "This key is rejected. Create a new one at steamgriddb.com/profile/preferences/api.");
        return (false, "SteamGridDB gaf HTTP " + (int)resp.StatusCode + ". " + Truncate(body));
    }

    public static async Task FillBytesAsync(ArtworkSet art, CancellationToken ct)
    {
        art.Grid = await DownloadAsync(art.GridUrl, ct) ?? art.Grid;
        art.Wide = await DownloadAsync(art.WideUrl, ct) ?? art.Wide;
        art.Hero = await DownloadAsync(art.HeroUrl, ct);
        art.Logo = await DownloadAsync(art.LogoUrl, ct);
        art.Icon = await DownloadAsync(art.IconUrl, ct) ?? art.Grid;
    }

    private static async Task<ArtworkSet?> FromSteamGridDbAsync(string title, SystemProfile system, CancellationToken ct)
    {
        var gameId = await SearchGameIdAsync(title, system, ct);
        if (gameId is null) return null;

        var grid = await FirstAssetAsync($"grids/game/{gameId}?dimensions=600x900,342x482&nsfw=false&types=static", ct)
                   ?? await FirstAssetAsync($"grids/game/{gameId}?nsfw=false&types=static", ct);
        var wide = await FirstAssetAsync($"grids/game/{gameId}?dimensions=920x430,460x215&nsfw=false&types=static", ct)
                   ?? await FirstAssetAsync($"grids/game/{gameId}?dimensions=920x430&nsfw=false", ct);
        var hero = await FirstAssetAsync($"heroes/game/{gameId}?dimensions=1920x620&nsfw=false&types=static", ct)
                   ?? await FirstAssetAsync($"heroes/game/{gameId}?nsfw=false", ct);
        var logo = await FirstAssetAsync($"logos/game/{gameId}?nsfw=false&types=static", ct);
        var icon = await FirstAssetAsync($"icons/game/{gameId}?nsfw=false", ct);

        if (grid is null && wide is null && hero is null && logo is null)
        {
            LastError = "SteamGridDB heeft deze game, maar geen bruikbare afbeeldingen.";
            return null;
        }

        return new ArtworkSet
        {
            Source = "SteamGridDB",
            GameId = gameId,
            GridUrl = grid ?? wide,
            WideUrl = wide ?? grid,
            HeroUrl = hero,
            LogoUrl = logo,
            IconUrl = icon
        };
    }

    private static async Task<int?> SearchGameIdAsync(string title, SystemProfile system, CancellationToken ct)
    {
        var clean = StoreGame.StripVariant(title);
        var terms = system.Id is "hydra" or "app" or "lutris" or "game"
            ? new[] { clean, title, StoreGame.FoldTitle(clean) }
            : new[]
            {
                clean,
                title,
                clean + " " + system.Name,
                StoreGame.FoldTitle(clean)
            };

        JsonElement? bestHits = null;
        foreach (var term in terms.Where(t => !string.IsNullOrWhiteSpace(t))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var data = await SearchAsync(term, ct);
            if (LastKeyInvalid) return null;
            if (data is null || data.Value.GetArrayLength() == 0) continue;
            bestHits = data;
            var id = PickGameId(data.Value, clean, system);
            if (id is not null) return id;
        }

        if (bestHits is { } fallback && fallback.GetArrayLength() > 0)
        {
            var first = fallback[0];
            if (first.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
                return id;
        }

        if (string.IsNullOrEmpty(LastError))
            LastError = "SteamGridDB vindt geen game voor \"" + title + "\".";
        return null;
    }

    private static async Task<JsonElement?> SearchAsync(string term, CancellationToken ct)
    {
        var url = Api + "/search/autocomplete/" + Uri.EscapeDataString(term.Trim());
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OptimizerSettings.SteamGridDbKey);
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if ((int)resp.StatusCode is 401 or 403)
        {
            LastKeyInvalid = true;
            LastError = "SteamGridDB key invalid. Create a new one at steamgriddb.com/profile/preferences/api.";
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            LastError = "SteamGridDB search failed (HTTP " + (int)resp.StatusCode + ").";
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            LastError = ReadErrors(root) ?? "SteamGridDB gaf geen resultaat.";
            return null;
        }
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;
        return data.Clone();
    }

    private static int? PickGameId(JsonElement data, string title, SystemProfile system)
    {
        var want = StoreGame.FoldTitle(title);
        JsonElement? best = null;
        var bestScore = 0;
        foreach (var item in data.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var types = item.TryGetProperty("types", out var t) ? t.ToString() : "";
            var folded = StoreGame.FoldTitle(name);
            var score = 0;
            if (folded == want) score += 12;
            else if (want.Length >= 4 && (folded.Contains(want) || want.Contains(folded))) score += 5;
            if (types.Contains(system.Name, StringComparison.OrdinalIgnoreCase) ||
                types.Contains(system.Id, StringComparison.OrdinalIgnoreCase) ||
                types.Contains(system.Category, StringComparison.OrdinalIgnoreCase))
                score += 3;
            if (item.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True)
                score += 2;
            if (item.TryGetProperty("release_date", out _))
                score += 1;
            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }
        if (best is null || bestScore < 5) return null;
        if (!best.Value.TryGetProperty("id", out var id)) return null;
        return id.TryGetInt32(out var nId) ? nId : null;
    }

    private static async Task<JsonElement?> ReadArrayAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Api + "/" + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OptimizerSettings.SteamGridDbKey);
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await Http.SendAsync(req, ct);
        if ((int)resp.StatusCode is 401 or 403)
        {
            LastKeyInvalid = true;
            LastError = "SteamGridDB key invalid.";
            return null;
        }
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;
        return data.Clone();
    }

    private static async Task<string?> FirstAssetAsync(string path, CancellationToken ct)
    {
        var data = await ReadArrayAsync(path, ct);
        if (data is null || data.Value.GetArrayLength() == 0) return null;
        foreach (var item in data.Value.EnumerateArray())
        {
            if (item.TryGetProperty("url", out var url) && url.GetString() is { Length: > 8 } href)
                return href;
            if (item.TryGetProperty("thumb", out var thumb) && thumb.GetString() is { Length: > 8 } th)
                return th;
        }
        return null;
    }

    private static async Task<ArtworkSet?> FromLibretroAsync(string title, SystemProfile system, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(system.LibretroThumbs)) return null;
        var names = new[] { title, StoreGame.StripVariant(title), title.Replace(" - ", " "), title.Replace(":", "") };
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var file = Uri.EscapeDataString(name.Trim()) + ".png";
            var box = $"https://thumbnails.libretro.com/{Uri.EscapeDataString(system.LibretroThumbs)}/Named_Boxarts/{file}";
            var bytes = await DownloadAsync(box, ct);
            if (bytes is { Length: > 800 })
            {
                return new ArtworkSet
                {
                    Source = "Libretro",
                    GridUrl = box,
                    Grid = bytes
                };
            }
        }
        return null;
    }

    public static Task<byte[]?> DownloadAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<byte[]?>(null);
        return ArtworkCache.GetOrFetchAsync(url, token => FetchAsync(url, token), ct);
    }

    private static async Task<byte[]?> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://www.steamgriddb.com/");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length > 200 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadAuthor(JsonElement item)
    {
        if (!item.TryGetProperty("author", out var author)) return "";
        if (author.ValueKind == JsonValueKind.String)
            return author.GetString()?.Trim() ?? "";
        if (author.ValueKind == JsonValueKind.Object &&
            author.TryGetProperty("name", out var name))
            return name.GetString()?.Trim() ?? "";
        return "";
    }

    private static string? ReadErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors)) return null;
        if (errors.ValueKind == JsonValueKind.Array)
            return string.Join(" ", errors.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)));
        return errors.ToString();
    }

    private static string Truncate(string text) =>
        string.IsNullOrWhiteSpace(text) ? "" : text.Length <= 180 ? text : text[..180] + "…";

    private static HttpClient Create()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SESAME-GameOptimizer/1.0");
        return http;
    }
}
