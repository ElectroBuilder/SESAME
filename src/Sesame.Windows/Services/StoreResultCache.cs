using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sesame.Models;

namespace Sesame.Services;

public sealed class CachedStoreSearch
{
    public DateTime SavedAt { get; set; }
    public bool HasMore { get; set; }
    public string IdentityText { get; set; } = "";
    public List<CachedHit> Hits { get; set; } = new();
    public bool IsComplete => !HasMore && Hits.Count > 0;
    public TimeSpan Age => DateTime.UtcNow - SavedAt;
    public bool IsFresh(TimeSpan maxAge) => IsComplete && Age <= maxAge;
}

public sealed class CachedHit
{
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string GameName { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public string? DownloadUrl { get; set; }
    public string? FileName { get; set; }
    public string? ItemId { get; set; }
    public string Kind { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Platform { get; set; } = "";
    public string OriginalGame { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTime? AddedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public int LikeCount { get; set; }
    public int DownloadCount { get; set; }
    public int ViewCount { get; set; }
    public int PostCount { get; set; }
    public bool WasFeatured { get; set; }
    public int SearchRank { get; set; }
    public int? SourceGameId { get; set; }
    public string? ImageUrl { get; set; }
    public List<string> ScreenshotUrls { get; set; } = new();
    public long Size { get; set; }
}

public static class StoreResultCache
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string Dir => AppDataPaths.Combine("store-cache", "results");

    public static string Key(StoreGame game, string query, string kind, string source, string? sort = null)
    {
        var raw = string.Join("|",
            StoreGame.FoldSystem(game.System),
            StoreGame.FoldTitle(game.Name),
            string.Join(",", game.GameBananaIds.OrderBy(id => id)),
            game.TitleId ?? "",
            kind.Trim().ToLowerInvariant(),
            source.Trim().ToLowerInvariant(),
            (sort ?? "").Trim().ToLowerInvariant(),
            StoreGame.Normalize(query));
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..20].ToLowerInvariant();
    }

    public static CachedStoreSearch? TryLoad(string key)
    {
        try
        {
            var path = Path.Combine(Dir, key + ".json");
            if (!File.Exists(path)) return null;
            var data = JsonSerializer.Deserialize<CachedStoreSearch>(File.ReadAllText(path), Json);
            if (data is null || data.Hits.Count == 0) return null;
            return data;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string key, IEnumerable<PackHit> hits, bool hasMore, string identityText)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var data = new CachedStoreSearch
            {
                SavedAt = DateTime.UtcNow,
                HasMore = hasMore,
                IdentityText = identityText,
                Hits = hits.Select(ToCached).ToList()
            };
            File.WriteAllText(Path.Combine(Dir, key + ".json"), JsonSerializer.Serialize(data, Json));
        }
        catch
        {
            // cache is optioneel
        }
    }

    public static PackHit ToHit(CachedHit c) => new()
    {
        Title = c.Title,
        Source = c.Source,
        GameName = c.GameName,
        PageUrl = c.PageUrl,
        DownloadUrl = c.DownloadUrl,
        FileName = c.FileName,
        ItemId = c.ItemId,
        Kind = PackStore.ClassifyKind(c.Title, c.Summary, c.Kind, c.FileName, c.Platform),
        Author = c.Author,
        Version = c.Version,
        Platform = c.Platform,
        OriginalGame = c.OriginalGame,
        Summary = c.Summary,
        AddedUtc = c.AddedUtc,
        UpdatedUtc = c.UpdatedUtc,
        LikeCount = c.LikeCount,
        DownloadCount = c.DownloadCount,
        ViewCount = c.ViewCount,
        PostCount = c.PostCount,
        WasFeatured = c.WasFeatured,
        SearchRank = c.SearchRank,
        SourceGameId = c.SourceGameId,
        ImageUrl = c.ImageUrl,
        ScreenshotUrls = c.ScreenshotUrls ?? [],
        Size = c.Size
    };
    // Kind wordt opnieuw bepaald bij gebruik (save-in-modnaam).

    public static string HitKey(PackHit hit) =>
        !string.IsNullOrWhiteSpace(hit.PageUrl) ? hit.PageUrl.ToLowerInvariant()
        : $"{hit.Source}|{hit.ItemId}|{hit.Title}".ToLowerInvariant();

    public static (int Added, int Updated, int Removed) Diff(IReadOnlyList<PackHit> previous, IReadOnlyList<PackHit> next)
    {
        var oldMap = previous.GroupBy(HitKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var newMap = next.GroupBy(HitKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var added = newMap.Keys.Count(k => !oldMap.ContainsKey(k));
        var removed = oldMap.Keys.Count(k => !newMap.ContainsKey(k));
        var updated = 0;
        foreach (var (key, neu) in newMap)
        {
            if (!oldMap.TryGetValue(key, out var old)) continue;
            if (!string.Equals(old.Version, neu.Version, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(old.Title, neu.Title, StringComparison.OrdinalIgnoreCase) ||
                old.UpdatedUtc != neu.UpdatedUtc)
                updated++;
        }
        return (added, updated, removed);
    }

    private static CachedHit ToCached(PackHit h) => new()
    {
        Title = h.Title,
        Source = h.Source,
        GameName = h.GameName,
        PageUrl = h.PageUrl,
        DownloadUrl = h.DownloadUrl,
        FileName = h.FileName,
        ItemId = h.ItemId,
        Kind = h.Kind,
        Author = h.Author,
        Version = h.Version,
        Platform = h.Platform,
        OriginalGame = h.OriginalGame,
        Summary = h.Summary,
        AddedUtc = h.AddedUtc,
        UpdatedUtc = h.UpdatedUtc,
        LikeCount = h.LikeCount,
        DownloadCount = h.DownloadCount,
        ViewCount = h.ViewCount,
        PostCount = h.PostCount,
        WasFeatured = h.WasFeatured,
        SearchRank = h.SearchRank,
        SourceGameId = h.SourceGameId,
        ImageUrl = h.ImageUrl,
        ScreenshotUrls = h.ScreenshotUrls,
        Size = h.Size
    };
}
