using Avalonia.Media.Imaging;
using Sesame.Models;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck;

public static class DeckCovers
{
    public static void ApplyBytes(OptimizerGame game)
    {
        if (game.Cover is Bitmap) return;
        if (game.GridBytes is not { Length: > 0 } bytes) return;
        try
        {
            using var ms = new MemoryStream(bytes);
            game.Cover = new Bitmap(ms);
            game.HasArtwork = true;
        }
        catch
        {
            /* cover is optional */
        }
    }

    public static void Hydrate(DeckClient client, IEnumerable<OptimizerGame> games)
    {
        SteamGridArt.AttachAll(client, games);
        foreach (var game in games)
            ApplyBytes(game);
    }

    public static async Task PrefetchAsync(IReadOnlyList<OptimizerGame> games, CancellationToken ct)
    {
        if (!OptimizerSettings.HasSteamGridDb) return;
        using var gate = new SemaphoreSlim(3);
        await Task.WhenAll(games.Select(async game =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (game.Cover is Bitmap) return;
                var profile = SystemCatalog.FromFolder(game.FolderName)
                              ?? SystemCatalog.All.FirstOrDefault(p => p.Id == game.SystemId);
                if (profile is null) return;
                var query = string.IsNullOrWhiteSpace(game.SearchQuery) ? game.DisplayName : game.SearchQuery;
                var art = await ArtworkClient.FindAsync(query, profile, ct);
                if (art?.GridUrl is null) return;
                var bytes = await ArtworkClient.DownloadAsync(art.GridUrl, ct);
                if (bytes is not { Length: > 0 }) return;
                game.GridBytes = bytes;
                game.SelectedGridUrl ??= art.GridUrl;
                game.ArtworkSource = art.Source ?? "SteamGridDB";
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyBytes(game));
            }
            catch
            {
                /* cover is optional */
            }
            finally
            {
                gate.Release();
            }
        }));
    }
}
