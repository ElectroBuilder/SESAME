using System.ComponentModel;

namespace Sesame.Models;

public sealed class StoreSort
{
    public static readonly StoreSort Popular = new(
        "Popular", "likes", "Generic_MostLiked", nameof(PackHit.LikeCount), ListSortDirection.Descending);

    public static readonly StoreSort PopularWeek = new(
        "Popular this week", "likes-week", "Generic_Newest", nameof(PackHit.LikeCount),
        ListSortDirection.Descending, thisWeek: true);

    public static readonly StoreSort Downloads = new(
        "Most downloaded", "downloads", "Generic_MostDownloaded", nameof(PackHit.DownloadCount),
        ListSortDirection.Descending);

    public static readonly StoreSort Featured = new(
        "Featured", "featured", "Generic_MostLiked", nameof(PackHit.LikeCount),
        ListSortDirection.Descending, featuredOnly: true);

    public static readonly StoreSort Newest = new(
        "New", "new", "Generic_Newest", nameof(PackHit.AddedUtc), ListSortDirection.Descending);

    public static readonly StoreSort Updated = new(
        "Updated", "updated", "Generic_LatestUpdated", nameof(PackHit.UpdatedUtc),
        ListSortDirection.Descending);

    public static readonly StoreSort Views = new(
        "Most viewed", "views", "Generic_MostViewed", nameof(PackHit.ViewCount),
        ListSortDirection.Descending);

    public static readonly StoreSort Discussed = new(
        "Most discussed", "posts", "Generic_MostCommented", nameof(PackHit.PostCount),
        ListSortDirection.Descending);

    public static readonly StoreSort Title = new(
        "Title", "title", null, nameof(PackHit.Title), ListSortDirection.Ascending);

    public static readonly StoreSort Author = new(
        "Author", "author", null, nameof(PackHit.Author), ListSortDirection.Ascending);

    public static readonly StoreSort Status = new(
        "Status", "status", null, nameof(PackHit.StatusText), ListSortDirection.Ascending);

    public static readonly StoreSort Size = new(
        "Size", "size", null, nameof(PackHit.Size), ListSortDirection.Descending);

    public static IReadOnlyList<StoreSort> All { get; } =
    [
        Popular, PopularWeek, Downloads, Featured, Newest, Updated, Views, Discussed, Title, Author, Size, Status
    ];

    public string Label { get; }
    public string Tag { get; }
    public string? ApiSort { get; }
    public string ClientProperty { get; }
    public ListSortDirection Direction { get; }
    public bool FeaturedOnly { get; }
    public bool ThisWeek { get; }
    public bool UsesApi => !string.IsNullOrEmpty(ApiSort) || FeaturedOnly;

    private StoreSort(string label, string tag, string? apiSort, string clientProperty,
        ListSortDirection direction, bool featuredOnly = false, bool thisWeek = false)
    {
        Label = label;
        Tag = tag;
        ApiSort = apiSort;
        ClientProperty = clientProperty;
        Direction = direction;
        FeaturedOnly = featuredOnly;
        ThisWeek = thisWeek;
    }

    public static StoreSort FromTag(string? tag) =>
        All.FirstOrDefault(s => string.Equals(s.Tag, tag, StringComparison.OrdinalIgnoreCase))
        ?? Popular;

    public static StoreSort? FromHeader(string? header) => header switch
    {
        "Title" => Title,
        "Author" => Author,
        "Added" or "AddedUtc" => Newest,
        "Updated" or "UpdatedUtc" => Updated,
        "Size" => Size,
        "Status" or "StatusText" => Status,
        _ => null
    };

    public override string ToString() => Label;
}
