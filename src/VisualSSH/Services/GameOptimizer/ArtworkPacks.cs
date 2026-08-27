using VisualSSH.Models;

namespace VisualSSH.Services.GameOptimizer;

public static class ArtworkPacks
{
    public static List<ArtworkPack> Build(IEnumerable<ArtworkChoice> choices)
    {
        var items = choices.Where(c => !string.IsNullOrWhiteSpace(c.Url)).ToList();
        var packs = new List<ArtworkPack>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in items
                     .Where(c => !string.IsNullOrWhiteSpace(c.Author))
                     .GroupBy(c => c.Author.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var pack = FromGroup(group.Key, group);
            if (pack.PieceCount < 2) continue;
            FillMissing(pack, items, used);
            Remember(pack, used);
            packs.Add(pack);
        }

        foreach (var group in items
                     .Where(c => !used.Contains(c.Url) && !string.IsNullOrWhiteSpace(c.Style))
                     .GroupBy(c => StyleKey(c.Style), StringComparer.OrdinalIgnoreCase))
        {
            var pack = FromGroup(StyleLabel(group.Key), group);
            if (pack.PieceCount < 2) continue;
            Remember(pack, used);
            packs.Add(pack);
        }

        return packs
            .OrderByDescending(p => p.PieceCount)
            .ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ArtworkPack FromGroup(string title, IEnumerable<ArtworkChoice> group)
    {
        var list = group.ToList();
        var pack = new ArtworkPack
        {
            Title = title,
            Cover = First(list, "cover"),
            Wide = First(list, "wide"),
            Hero = First(list, "hero"),
            Logo = First(list, "logo"),
            Icon = First(list, "icon")
        };
        pack.Subtitle = string.Join(" · ", pack.Pieces.Select(KindLabel));
        return pack;
    }

    private static void FillMissing(ArtworkPack pack, IReadOnlyList<ArtworkChoice> all, HashSet<string> used)
    {
        var styles = pack.Pieces
            .Select(p => StyleKey(p.Style))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (styles.Count == 0) return;
        foreach (var kind in new[] { "cover", "wide", "hero", "logo", "icon" })
        {
            if (Get(pack, kind) is not null) continue;
            var match = all.FirstOrDefault(c =>
                c.Kind == kind &&
                !used.Contains(c.Url) &&
                styles.Contains(StyleKey(c.Style), StringComparer.OrdinalIgnoreCase));
            if (match is not null)
                Set(pack, match);
        }
        pack.Subtitle = string.Join(" · ", pack.Pieces.Select(KindLabel));
    }

    private static ArtworkChoice? First(IEnumerable<ArtworkChoice> list, string kind) =>
        list.FirstOrDefault(c => c.Kind == kind);

    private static ArtworkChoice? Get(ArtworkPack pack, string kind) => kind switch
    {
        "cover" => pack.Cover,
        "wide" => pack.Wide,
        "hero" => pack.Hero,
        "logo" => pack.Logo,
        "icon" => pack.Icon,
        _ => null
    };

    private static void Set(ArtworkPack pack, ArtworkChoice choice)
    {
        switch (choice.Kind)
        {
            case "cover": pack.Cover = choice; break;
            case "wide": pack.Wide = choice; break;
            case "hero": pack.Hero = choice; break;
            case "logo": pack.Logo = choice; break;
            case "icon": pack.Icon = choice; break;
        }
    }

    private static void Remember(ArtworkPack pack, HashSet<string> used)
    {
        foreach (var piece in pack.Pieces)
            used.Add(piece.Url);
    }

    private static string StyleKey(string style) =>
        (style ?? "").Trim().ToLowerInvariant();

    private static string StyleLabel(string key) => key switch
    {
        "alternate" => "Alternate",
        "white_logo" => "White logo",
        "no_logo" => "Zonder logo",
        "material" => "Material",
        "blurred" => "Blurred",
        "minimal" => "Minimal",
        "" => "Set",
        _ => char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' ')
    };

    private static string KindLabel(ArtworkChoice choice) => choice.Kind switch
    {
        "hero" => "Hero",
        "wide" => "Wide",
        "logo" => "Logo",
        "icon" => "Icoon",
        _ => "Cover"
    };
}
