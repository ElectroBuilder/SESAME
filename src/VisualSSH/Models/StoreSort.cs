using System.ComponentModel;

namespace VisualSSH.Models;

public sealed class StoreSort
{
    public static readonly StoreSort Popular = new(
        "Populair", "likes", "Generic_MostLiked", nameof(PackHit.LikeCount), ListSortDirection.Descending);

    public static readonly StoreSort PopularWeek = new(
        "Populair deze week", "likes-week", "Generic_Newest", nameof(PackHit.LikeCount),
        ListSortDirection.Descending, thisWeek: true);

    public static readonly StoreSort Downloads = new(
        "Meest gedownload", "downloads", "Generic_MostDownloaded", nameof(PackHit.DownloadCount),
        ListSortDirection.Descending);

    public static readonly StoreSort Featured = new(
        "Uitgelicht", "featured", "Generic_MostLiked", nameof(PackHit.LikeCount),
        ListSortDirection.Descending, featuredOnly: true);

    public static readonly StoreSort Newest = new(
        "Nieuw", "new", "Generic_Newest", nameof(PackHit.AddedUtc), ListSortDirection.Descending);

    public static readonly StoreSort Updated = new(
        "Bijgewerkt", "updated", "Generic_LatestUpdated", nameof(PackHit.UpdatedUtc),
        ListSortDirection.Descending);

    public static readonly StoreSort Views = new(
        "Meest bekeken", "views", "Generic_MostViewed", nameof(PackHit.ViewCount),
        ListSortDirection.Descending);

    public static readonly StoreSort Discussed = new(
        "Meest besproken", "posts", "Generic_MostCommented", nameof(PackHit.PostCount),
        ListSortDirection.Descending);

    public static readonly StoreSort Title = new(
        "Titel", "title", null, nameof(PackHit.Title), ListSortDirection.Ascending);

    public static readonly StoreSort Author = new(
        "Auteur", "author", null, nameof(PackHit.Author), ListSortDirection.Ascending);

    public static readonly StoreSort Status = new(
        "Status", "status", null, nameof(PackHit.StatusText), ListSortDirection.Ascending);

    public static readonly StoreSort Size = new(
        "Grootte", "size", null, nameof(PackHit.Size), ListSortDirection.Descending);

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
        "Titel" => Title,
        "Auteur" => Author,
        "Toegevoegd" => Newest,
        "Bijgewerkt" => Updated,
        "Grootte" => Size,
        "Status" => Status,
        _ => null
    };

    public override string ToString() => Label;
}
