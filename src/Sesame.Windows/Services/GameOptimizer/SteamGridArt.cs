using Sesame.Models;

namespace Sesame.Services.GameOptimizer;

/// <summary>
/// Reads artwork already written to Steam's grid folder on the Deck.
/// </summary>
public static class SteamGridArt
{
    public static void Attach(DeckClient client, OptimizerGame game) =>
        Attach(client, game, SteamShortcuts.FindUserConfigs(client));

    public static void Attach(DeckClient client, OptimizerGame game, IReadOnlyList<string> configs)
    {
        if (game.SteamAppId == 0) return;
        if (game.GridBytes is { Length: > 0 })
        {
            game.HasArtwork = true;
            if (string.IsNullOrEmpty(game.ArtworkSource) || game.ArtworkSource == "—")
                game.ArtworkSource = "Steam";
            return;
        }

        var bytes = ReadPortrait(client, game.SteamAppId, configs);
        if (bytes is not { Length: > 0 }) return;
        game.GridBytes = bytes;
        game.HasArtwork = true;
        if (string.IsNullOrEmpty(game.ArtworkSource) || game.ArtworkSource == "—")
            game.ArtworkSource = "Steam";
    }

    public static void AttachAll(DeckClient client, IEnumerable<OptimizerGame> games)
    {
        IReadOnlyList<string> configs;
        try { configs = SteamShortcuts.FindUserConfigs(client); }
        catch { return; }
        foreach (var game in games)
        {
            try { Attach(client, game, configs); }
            catch { /* cover is optional */ }
        }
    }

    public static byte[]? ReadPortrait(DeckClient client, uint appId) =>
        ReadPortrait(client, appId, SteamShortcuts.FindUserConfigs(client));

    public static byte[]? ReadPortrait(DeckClient client, uint appId, IReadOnlyList<string> configs)
    {
        if (appId == 0) return null;
        foreach (var config in configs)
        {
            var grid = DeckClient.Combine(config, "grid");
            foreach (var name in new[] { appId + "p.png", appId + "_p.png", appId + ".png", appId + "_hero.png" })
            {
                var path = DeckClient.Combine(grid, name);
                try
                {
                    if (client.FileLength(path) > 200)
                        return client.ReadBytes(path);
                }
                catch
                {
                    /* next candidate */
                }
            }
        }

        return null;
    }
}
