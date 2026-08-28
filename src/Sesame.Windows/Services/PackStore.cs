using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using Sesame.Models;

namespace Sesame.Services;

public sealed class PackStore
{
    private static readonly HttpClient Http = CreateClient();
    private readonly HdPacksIndex _hdPacks = new();
    private int _bananaPage = 1;
    private bool _bananaHasMore;
    private string _lastQuery = "";
    private string _lastSource = "";
    private string _lastKind = "";
    private StoreGame _lastGame = StoreGame.All;
    private StoreSort _lastSort = StoreSort.Popular;
    private int _searchRank;

    public bool HasMore => _bananaHasMore;

    public const string LegalHackNl =
        "ROM hacks are community patches, not replacement ROMs. You must supply a legal dump of an original cartridge. SESAME does not ship copyrighted ROMs. The patch is fan work and contains no Nintendo code.";

    public async Task<IReadOnlyList<PackHit>> SearchAsync(string query, string source, string kind,
        StoreGame? game, StoreSort? sort = null, CancellationToken ct = default)
    {
        query = query.Trim();
        game ??= StoreGame.All;
        kind = string.IsNullOrWhiteSpace(kind) ? "All" : kind;
        _lastQuery = query;
        _lastSource = source;
        _lastKind = kind;
        _lastGame = game;
        _lastSort = sort ?? StoreSort.Popular;
        _bananaPage = 1;
        _bananaHasMore = false;
        _searchRank = 0;
        if (query.Length < 2 && game.IsAll) return [];
        return await SearchPageAsync(first: true, ct);
    }

    public Task<IReadOnlyList<PackHit>> SearchMoreAsync(CancellationToken ct = default) =>
        SearchPageAsync(first: false, ct);

    private async Task<IReadOnlyList<PackHit>> SearchPageAsync(bool first, CancellationToken ct)
    {
        var query = _lastQuery;
        var source = _lastSource;
        var kind = _lastKind;
        var game = _lastGame;
        if (!first && !_bananaHasMore) return [];
        if (!game.IsAll)
            await EnsureBananaIds(game, ct);

        var allSources = source.Equals("All sources", StringComparison.OrdinalIgnoreCase)
                         || source.Equals("Beide", StringComparison.OrdinalIgnoreCase);
        var wantMods = KindIs(kind, "All", "Mods", "ROM-hacks");
        var wantTex = KindIs(kind, "All", "Texture packs");
        var wantSaves = KindIs(kind, "All", "Saves");
        var wantBanana = allSources || source.Equals("GameBanana", StringComparison.OrdinalIgnoreCase);
        var wantArchive = source.Equals("Internet Archive", StringComparison.OrdinalIgnoreCase)
                          || (allSources && !game.IsAll);

        var jobs = new List<Task<List<PackHit>>>();
        if ((wantMods || wantTex || wantSaves) && wantBanana)
            jobs.Add(SafeSearch(() => SearchGameBanana(query, game, kind, _bananaPage, _lastSort, ct), ct));
        if (first && wantTex)
            jobs.Add(SafeSearch(() => SearchEmulationKing(query, game, ct), ct));
        if (first && (wantTex || wantSaves))
            jobs.Add(SafeSearch(() => _hdPacks.SearchAsync(Http, game, query, kind, ct), ct));
        if (first && wantTex)
            jobs.Add(SafeSearch(() => SearchGbaTemp(query, game, ct), ct));
        if (first && wantSaves && wantBanana)
            jobs.Add(SafeSearch(() => SearchGameBananaFiles(query, game, ct), ct));
        if (first && (wantMods || wantTex) && wantArchive)
        {
            if (allSources)
                jobs.Add(SafeSearch(() => ArchiveSearch.SearchAsync(Http, query, game, kind, ct), ct));
            else
                jobs.Add(ArchiveSearch.SearchAsync(Http, query, game, kind, ct));
        }

        var batches = jobs.Count == 0 ? [] : await Task.WhenAll(jobs);
        var results = batches.SelectMany(x => x).ToList();

        if (!game.IsAll)
        {
            results = results.Where(h => HitMatches(h, game)).ToList();
            foreach (var hit in results)
            {
                if (string.IsNullOrWhiteSpace(hit.GameName))
                    hit.GameName = game.Name;
                if (string.IsNullOrWhiteSpace(hit.Platform))
                    hit.Platform = game.System;
            }
        }

        if (_bananaHasMore)
            _bananaPage++;

        foreach (var hit in results)
            hit.SearchRank = _searchRank++;
        return results;
    }

    private static async Task<List<PackHit>> SafeSearch(Func<Task<List<PackHit>>> search, CancellationToken ct)
    {
        try { return await search(); }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    public async Task<StoreGameInfo> GetGameInfoAsync(StoreGame game, CancellationToken ct = default)
    {
        var info = new StoreGameInfo
        {
            Name = game.IsAll ? "All games" : game.Name,
            System = game.System,
            IdentityText = game.IdentityText,
            Meta = game.IsAll ? "Pick a game in the list" : BuildLocalMeta(game),
            Description = game.IsAll
                ? "Pick a game to search mods, texture packs and saves by platform + title + IDs."
                : BuildLocalDescription(game),
            PageUrl = game.GameBananaIds.Count > 0
                ? "https://gamebanana.com/games/" + game.GameBananaIds[0]
                : null
        };
        if (game.IsAll) return info;
        await EnsureBananaIds(game, ct);
        info.IdentityText = game.IdentityText;
        info.Meta = BuildLocalMeta(game);
        info.Description = BuildLocalDescription(game);
        info.PageUrl = game.GameBananaIds.Count > 0
            ? "https://gamebanana.com/games/" + game.GameBananaIds[0]
            : info.PageUrl;

        foreach (var id in game.GameBananaIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await FillBananaGame(info, id, game.Name, ct))
                    break;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* volgende id of lokale info */ }
        }

        return info;
    }

    private static string BuildLocalMeta(StoreGame game)
    {
        var parts = new List<string> { game.System, game.Name };
        if (game.GameBananaIds.Count > 0)
            parts.Add("GameBanana #" + string.Join("/", game.GameBananaIds));
        if (!string.IsNullOrEmpty(game.TitleId))
            parts.Add("Title ID " + game.TitleId);
        return string.Join(" · ", parts);
    }

    private static string BuildLocalDescription(StoreGame game)
    {
        var bits = new List<string> { $"{game.Name} for {game.System}." };
        if (game.GameBananaIds.Count > 0)
            bits.Add("Mods and files are searched via GameBanana game ID " +
                     string.Join(", ", game.GameBananaIds.Select(id => "#" + id)) + ".");
        if (!string.IsNullOrEmpty(game.TitleId))
            bits.Add("Switch Title ID " + game.TitleId + ".");
        if (game.KingSlugs.Count > 0)
            bits.Add("Emulation King-pad: " + game.KingSlugs[0] + ".");
        return string.Join(" ", bits);
    }

    private static async Task<bool> FillBananaGame(StoreGameInfo info, int id, string name, CancellationToken ct)
    {
        var page = "https://gamebanana.com/games/" + id;
        info.PageUrl = page;
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(
            $"https://gamebanana.com/apiv11/Game/{id}?_csvProperties=_sName,_sProfileUrl,_aPreviewMedia", ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("_sErrorCode", out _))
            return await FillBananaGameFromSearch(info, id, name, ct);

        if (root.TryGetProperty("_sName", out var n) && n.GetString() is { Length: > 0 } title &&
            StoreGame.FoldTitle(title) == StoreGame.FoldTitle(name))
            info.Name = title;
        var images = BananaImages(root, preferThumb: false);
        AssignGameArt(info, images);

        await FillFromPage(info, page, ct);
        return !string.IsNullOrEmpty(info.CoverUrl) || info.Description.Length > 40;
    }

    private static async Task<bool> FillBananaGameFromSearch(StoreGameInfo info, int id, string name,
        CancellationToken ct)
    {
        var url = "https://gamebanana.com/apiv11/Util/Search/Results?_sModelName=Game&_nPerpage=15&_sSearchString="
                  + Uri.EscapeDataString(name);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("_aRecords", out var records)) return false;
        foreach (var rec in records.EnumerateArray())
        {
            var recId = rec.TryGetProperty("_idRow", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt32() : 0;
            if (recId != id) continue;
            if (rec.TryGetProperty("_sName", out var n) && n.GetString() is { Length: > 0 } title &&
                StoreGame.FoldTitle(title) == StoreGame.FoldTitle(name))
                info.Name = title;
            var images = BananaImages(rec, preferThumb: false);
            AssignGameArt(info, images);
            if (rec.TryGetProperty("_sAbbreviation", out var ab) && ab.GetString() is { Length: > 0 } abbr)
                info.Meta = string.IsNullOrEmpty(info.Meta) ? abbr : info.Meta + " · " + abbr;
            break;
        }

        await FillFromPage(info, "https://gamebanana.com/games/" + id, ct);
        return !string.IsNullOrEmpty(info.CoverUrl);
    }

    private static async Task FillFromPage(StoreGameInfo info, string pageUrl, CancellationToken ct)
    {
        try
        {
            var html = await Http.GetStringAsync(pageUrl, ct);
            var desc = MetaContent(html, "og:description") ?? MetaContent(html, "description");
            if (!string.IsNullOrWhiteSpace(desc) && desc.Length > 24 &&
                !desc.Contains("GameBanana is", StringComparison.OrdinalIgnoreCase))
                info.Description = System.Net.WebUtility.HtmlDecode(desc).Trim();
            var img = MetaContent(html, "og:image");
            if (!string.IsNullOrWhiteSpace(img) && !StoreUrls.IsPlaceholder(img))
            {
                info.CoverUrl ??= img;
                info.BannerUrl ??= img;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* lokale beschrijving blijft staan */ }
    }

    private static string? MetaContent(string html, string name)
    {
        var m = Regex.Match(html,
            $@"<meta[^>]+(?:property|name)=""{Regex.Escape(name)}""[^>]+content=""([^""]+)""",
            RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(html,
                $@"<meta[^>]+content=""([^""]+)""[^>]+(?:property|name)=""{Regex.Escape(name)}""",
                RegexOptions.IgnoreCase);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
    }

    private static List<string> BananaImages(JsonElement rec, bool preferThumb)
    {
        var urls = new List<string>();
        void Add(string? url)
        {
            url = StoreUrls.NormalizeUrl(url);
            if (string.IsNullOrEmpty(url) || StoreUrls.IsPlaceholder(url)) return;
            if (!urls.Exists(u => u.Equals(url, StringComparison.OrdinalIgnoreCase)))
                urls.Add(url);
        }

        if (!rec.TryGetProperty("_aPreviewMedia", out var media) ||
            !media.TryGetProperty("_aImages", out var images) ||
            images.ValueKind != JsonValueKind.Array)
        {
            if (preferThumb && rec.TryGetProperty("_sIconUrl", out var icon))
                Add(icon.GetString());
            return urls;
        }

        foreach (var img in images.EnumerateArray())
        {
            var type = GetStr(img, "_sType") ?? "";
            if (type.Equals("video", StringComparison.OrdinalIgnoreCase)) continue;

            var baseUrl = GetStr(img, "_sBaseUrl");
            if (!string.IsNullOrEmpty(baseUrl))
            {
                string? file = preferThumb
                    ? GetStr(img, "_sFile220") ?? GetStr(img, "_sFile100") ?? GetStr(img, "_sFile")
                    : GetStr(img, "_sFile530") ?? GetStr(img, "_sFile") ?? GetStr(img, "_sFile220");
                if (!string.IsNullOrEmpty(file))
                    Add(baseUrl.TrimEnd('/') + "/" + file);
            }

            Add(GetStr(img, "_sUrl"));
        }

        if (urls.Count == 0 && preferThumb && rec.TryGetProperty("_sIconUrl", out var fallback))
            Add(fallback.GetString());
        return urls;
    }

    private static string? GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static string BananaCategory(JsonElement rec)
    {
        var parts = new List<string>();
        foreach (var key in new[] { "_aSubCategory", "_aCategory", "_aSuperCategory" })
        {
            if (rec.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Object &&
                el.TryGetProperty("_sName", out var n) && n.GetString() is { Length: > 0 } name)
                parts.Add(name);
        }
        return string.Join(" · ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? BananaFileName(JsonElement rec)
    {
        if (!rec.TryGetProperty("_aFiles", out var files) || files.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var file in files.EnumerateArray())
        {
            if (file.TryGetProperty("_sFile", out var n) && n.GetString() is { Length: > 0 } name)
                return name;
        }
        return null;
    }

    private static string BananaPlatform(JsonElement rec, string gameName, string category)
    {
        foreach (var probe in new[] { category, gameName })
        {
            if (TryKnownSystem(probe, out var label))
                return label;
        }
        if (rec.TryGetProperty("_aCategory", out var cat) && cat.TryGetProperty("_sName", out var cn) &&
            TryKnownSystem(cn.GetString(), out var fromCat))
            return fromCat;
        if (LooksLikeN64(gameName + " " + category)) return "N64";
        return "";
    }

    private static void AssignGameArt(StoreGameInfo info, List<string> images)
    {
        if (images.Count == 0) return;
        var icon = images.FirstOrDefault(u =>
            u.Contains("/ico/", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("/icon", StringComparison.OrdinalIgnoreCase));
        var banner = images.FirstOrDefault(u =>
            u.Contains("/banners/", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("/ss/", StringComparison.OrdinalIgnoreCase));
        info.CoverUrl = icon ?? banner ?? images[0];
        info.BannerUrl = banner ?? icon ?? images[0];
    }

    public async Task<string> DownloadAsync(PackHit hit, string localDir, CancellationToken ct = default,
        Action<double, string, bool>? progress = null)
    {
        Directory.CreateDirectory(localDir);
        var url = hit.DownloadUrl;
        if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(hit.ItemId))
        {
            progress?.Invoke(0, "Fetching download link…", true);
            var file = await ResolveGameBananaFile(hit.ItemId, ct);
            url = file.Url;
            hit.FileName ??= file.Name;
            hit.Size = file.Size;
        }
        if (string.IsNullOrEmpty(url) && PackUrl.CanResolve(hit.PageUrl))
            url = await ResolvePageDownload(hit.PageUrl, ct) ?? hit.PageUrl;
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException("No download link. Open the page in the browser.");

        var name = string.IsNullOrWhiteSpace(hit.FileName) ? GuessFileName(url) : hit.FileName;
        name = Sanitize(name);
        var dest = Path.Combine(localDir, name);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is 403 or 503)
            throw new InvalidOperationException(
                "De site blokkeert automatische download (Cloudflare). Open de pagina, sla de patch op en kies dat bestand.");
        response.EnsureSuccessStatusCode();
        var fromHeader = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (!string.IsNullOrWhiteSpace(fromHeader))
        {
            name = Sanitize(fromHeader);
            dest = Path.Combine(localDir, name);
            hit.FileName = name;
        }

        var total = response.Content.Headers.ContentLength ?? (hit.Size > 0 ? hit.Size : 0);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
            {
                var pct = read * 100.0 / total;
                progress?.Invoke(pct, $"Downloading {RemoteItem.FormatSize(read)} / {RemoteItem.FormatSize(total)}", false);
            }
            else
                progress?.Invoke(0, "Downloading " + RemoteItem.FormatSize(read), true);
        }
        if (hit.Size <= 0) hit.Size = read;
        if (LooksLikeRomHack(hit.Title, hit.Summary, hit.Kind, hit.FileName ?? name, hit.Platform))
            hit.Kind = "ROM-hack";
        progress?.Invoke(100, "Downloaded", false);
        return dest;
    }

    public async Task InspectHitAsync(PackHit hit, CancellationToken ct = default) =>
        await FillHitDetailsAsync(hit, ct);

    public async Task FillHitDetailsAsync(PackHit hit, CancellationToken ct = default)
    {
        if (string.Equals(hit.Source, ArchiveSearch.SourceName, StringComparison.OrdinalIgnoreCase))
        {
            await ArchiveSearch.InspectAsync(Http, hit, ct);
            return;
        }

        if (string.IsNullOrEmpty(hit.ItemId) ||
            !string.Equals(hit.Source, "GameBanana", StringComparison.OrdinalIgnoreCase))
            return;

        await FillBananaItem(hit, ct);
        if (LooksLikeRomHack(hit.Title, hit.Summary, hit.Kind, hit.FileName, hit.Platform))
            hit.Kind = "ROM-hack";
        if (string.IsNullOrWhiteSpace(hit.OriginalGame))
            hit.OriginalGame = hit.GameName;
    }

    public static string PrepareUploadFolder(string downloadedFile, bool unwrapSingleRoot = false)
    {
        var ext = Path.GetExtension(downloadedFile);
        if (ext is not (".zip" or ".rar" or ".7z"))
            return downloadedFile;
        var extract = downloadedFile + ".extracted";
        if (Directory.Exists(extract))
            Directory.Delete(extract, true);
        Directory.CreateDirectory(extract);
        using var archive = ArchiveFactory.OpenArchive(downloadedFile);
        archive.WriteToDirectory(extract, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
        if (!unwrapSingleRoot) return extract;
        var entries = Directory.GetFileSystemEntries(extract);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : extract;
    }

    public static string ClassifyKind(string title, string? summary = null, string? model = null,
        string? fileName = null, string? platform = null)
    {
        if (LooksLikeSave(title) || LooksLikeSave(summary))
            return "Save";
        if (LooksLikeTexture(title) || LooksLikeTexture(summary))
            return "Texture pack";
        if (string.Equals(model, "Gamefile", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(model, "Save", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(model, "Saves", StringComparison.OrdinalIgnoreCase))
            return "Save";
        if (string.Equals(model, "Sound", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(model, "Sounds", StringComparison.OrdinalIgnoreCase))
            return "Sound";
        if (string.Equals(model, "Texture pack", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(model, "Texture packs", StringComparison.OrdinalIgnoreCase))
            return "Texture pack";
        if (LooksLikeRomHack(title, summary, model, fileName, platform))
            return "ROM-hack";
        return "Mod";
    }

    public static bool LooksLikeRomHack(string? title, string? summary = null, string? model = null,
        string? fileName = null, string? platform = null)
    {
        if (string.Equals(model, "ROM-hack", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(model, "ROM hack", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(model, "ROM-hacks", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(fileName) && RomPatcher.IsPatch(fileName))
            return true;
        var hay = $"{title} {summary} {model} {fileName}".ToLowerInvariant();
        if (hay.Contains("texture") || hay.Contains("hires") || hay.Contains("hd pack") ||
            hay.Contains("romfs") || hay.Contains("exefs"))
            return false;
        if (hay.Contains("rom hack") || hay.Contains("rom-hack") || hay.Contains("romhack"))
            return true;
        if (Regex.IsMatch(hay, @"\b(bps|ips|ups)\b"))
            return true;
        return IsCartRomSystem(platform) && Regex.IsMatch(hay, @"\b(patch|patches|rom patch)\b");
    }

    public static bool IsCartRomSystem(string? system)
    {
        var folded = StoreGame.FoldSystem(system ?? "");
        return folded is "n64" or "nes" or "snes" or "gba" or "nds" or "genesis";
    }

    public static string ResolveSystem(PackHit hit, AppCatalog catalog, StoreGame? selected = null)
    {
        if (hit.SourceGameId is int gid)
        {
            var mapped = catalog.StoreGames.FirstOrDefault(g => g.GameBananaIds.Contains(gid));
            if (mapped is not null && !string.IsNullOrWhiteSpace(mapped.System))
                return mapped.System;
        }

        if (TryKnownSystem(hit.Platform, out var fromPlat))
            return fromPlat;

        if (!string.IsNullOrWhiteSpace(hit.GameName))
        {
            var mapped = catalog.StoreGames.FirstOrDefault(g => g.MatchesTitle(hit.GameName));
            if (mapped is not null && !string.IsNullOrWhiteSpace(mapped.System))
                return mapped.System;
            if (TryKnownSystem(hit.GameName, out var fromGame))
                return fromGame;
        }

        if (selected is { IsAll: false } && !string.IsNullOrWhiteSpace(selected.System))
            return selected.System;

        var hay = $"{hit.GameName} {hit.Title} {hit.Summary} {hit.Platform}";
        if (LooksLikeN64(hay)) return "N64";
        if (LooksLikeSnes(hay)) return "SNES";
        if (LooksLikeNes(hay)) return "NES";
        return hit.Platform ?? "";
    }

    public static string FoldRomFolderKey(string? system) => StoreGame.FoldSystem(system ?? "");

    private static bool TryKnownSystem(string? value, out string label)
    {
        label = SystemLabel(StoreGame.FoldSystem(value ?? ""));
        return label.Length > 0;
    }

    private static string SystemLabel(string folded) => folded switch
    {
        "n64" => "N64",
        "nes" => "NES",
        "snes" => "SNES",
        "switch" => "SWITCH",
        "gc" => "GC",
        "gba" => "GBA",
        "nds" => "NDS",
        "genesis" => "GENESIS",
        "ps1" or "psx" => "PSX",
        "ps2" => "PS2",
        _ => ""
    };

    private static bool LooksLikeN64(string text)
    {
        var hay = text.ToLowerInvariant();
        return hay.Contains("n64") || hay.Contains("nintendo 64") || hay.Contains("mario 64") ||
               hay.Contains("sm64") || hay.Contains("smash 64") || hay.Contains("ocarina") ||
               hay.Contains("majora") || hay.Contains("banjo") || hay.Contains("dk64") ||
               hay.Contains("star fox 64") || hay.Contains("starfox 64");
    }

    private static bool LooksLikeSnes(string text)
    {
        var hay = text.ToLowerInvariant();
        return hay.Contains("snes") || hay.Contains("super nintendo") || hay.Contains("super metroid");
    }

    private static bool LooksLikeNes(string text)
    {
        var hay = text.ToLowerInvariant();
        return hay.Contains(" nes") || hay.StartsWith("nes ") || hay.Contains("famicom");
    }

    public static string? FindPatchFile(string downloadedFile)
    {
        var prepared = PrepareUploadFolder(downloadedFile);
        if (File.Exists(prepared) && RomPatcher.IsPatch(prepared))
            return prepared;
        if (!Directory.Exists(prepared)) return null;
        return Directory.EnumerateFiles(prepared, "*", SearchOption.AllDirectories)
            .FirstOrDefault(RomPatcher.IsPatch);
    }

    private static readonly ConcurrentDictionary<string, int[]> BananaIdCache = new(StringComparer.OrdinalIgnoreCase);

    private static async Task EnsureBananaIds(StoreGame game, CancellationToken ct)
    {
        if (game.IsAll || game.GameBananaIds.Count > 0) return;
        var key = $"{StoreGame.FoldSystem(game.System)}|{StoreGame.FoldTitle(game.Name)}";
        if (BananaIdCache.TryGetValue(key, out var cached))
        {
            foreach (var id in cached)
                if (!game.GameBananaIds.Contains(id)) game.GameBananaIds.Add(id);
            return;
        }

        try
        {
            var url = "https://gamebanana.com/apiv11/Util/Search/Results?_sModelName=Game&_nPerpage=15&_sSearchString="
                      + Uri.EscapeDataString(game.Name);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            if (!doc.RootElement.TryGetProperty("_aRecords", out var records)) return;

            var bestId = 0;
            var bestScore = 0;
            foreach (var rec in records.EnumerateArray())
            {
                if (!rec.TryGetProperty("_idRow", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    continue;
                var name = rec.TryGetProperty("_sName", out var n) ? n.GetString() ?? "" : "";
                var score = ScoreBananaGame(game, name);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = idEl.GetInt32();
                }
            }

            var ids = bestId > 0 && bestScore >= 60 ? new[] { bestId } : [];
            BananaIdCache[key] = ids;
            foreach (var id in ids)
                game.GameBananaIds.Add(id);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            BananaIdCache.TryAdd(key, []);
        }
    }

    private static int ScoreBananaGame(StoreGame game, string recName)
    {
        if (string.IsNullOrWhiteSpace(recName) || game.Conflicts(recName)) return 0;
        var folded = StoreGame.FoldTitle(recName);
        var want = StoreGame.FoldTitle(game.Name);
        if (folded.Length == 0 || want.Length == 0) return 0;
        var sys = StoreGame.FoldSystem(game.System);
        var recHay = recName.ToLowerInvariant();
        if (sys == "n64" && (recHay.Contains("ultimate") || recHay.Contains("melee") ||
                             recHay.Contains("brawl") || recHay.Contains("3ds") || recHay.Contains("wii u")))
            return 0;
        if (folded == want) return 100;
        if (game.MatchesTitle(recName))
        {
            var score = 70;
            if (sys == "n64" && recName.Contains("64", StringComparison.OrdinalIgnoreCase)) score += 15;
            if (sys == "nes" && recName.Contains("NES", StringComparison.OrdinalIgnoreCase)) score += 10;
            if (sys == "switch" && recName.Contains("Deluxe", StringComparison.OrdinalIgnoreCase)) score += 5;
            return score;
        }
        return 0;
    }

    private async Task<List<PackHit>> SearchGameBanana(string query, StoreGame game, string kind, int page,
        StoreSort sort, CancellationToken ct)
    {
        var hits = new List<PackHit>();
        var more = false;
        if (game.GameBananaIds.Count > 0)
        {
            foreach (var id in game.GameBananaIds)
            {
                var (pageHits, hasMore) = await BananaIndexPage("Mod", id, page, sort, ct);
                hits.AddRange(pageHits);
                more = more || hasMore;
            }
        }
        else if (!game.IsAll)
        {
            var (pageHits, hasMore) = await BananaSearchPage(game.Name, null, "Mod", page, sort, ct);
            hits.AddRange(pageHits.Where(h => game.MatchesTitle(h.GameName)));
            more = hasMore;
        }
        else if (query.Length >= 2)
        {
            var (pageHits, hasMore) = await BananaSearchPage(query, null, "Mod", page, sort, ct);
            hits.AddRange(pageHits);
            more = hasMore;
        }
        _bananaHasMore = more;

        if (query.Length >= 2 && !game.IsAll)
        {
            var q = StoreGame.Normalize(query);
            hits = hits.Where(h =>
                StoreGame.Normalize(h.Title).Contains(q, StringComparison.Ordinal) ||
                StoreGame.Normalize(h.Author).Contains(q, StringComparison.Ordinal) ||
                StoreGame.Normalize(h.Summary).Contains(q, StringComparison.Ordinal)).ToList();
        }

        foreach (var hit in hits)
            hit.Kind = ClassifyKind(hit.Title, hit.Summary, hit.Kind, hit.FileName, hit.Platform);

        if (kind.Equals("Texture packs", StringComparison.OrdinalIgnoreCase))
            hits = hits.Where(h => h.Section == "Texture packs").ToList();
        else if (kind.Equals("Mods", StringComparison.OrdinalIgnoreCase))
            hits = hits.Where(h => h.Section is "Mods" or "ROM-hacks").ToList();
        else if (kind.Equals("ROM-hacks", StringComparison.OrdinalIgnoreCase))
            hits = hits.Where(h => h.Section == "ROM-hacks").ToList();
        else if (kind.Equals("Saves", StringComparison.OrdinalIgnoreCase))
            hits = hits.Where(h => h.Section == "Saves").ToList();
        return DedupHits(hits);
    }

    private static async Task<List<PackHit>> SearchGameBananaFiles(string query, StoreGame game, CancellationToken ct)
    {
        var hits = new List<PackHit>();
        foreach (var id in game.GameBananaIds)
        {
            for (var page = 1; page <= 20; page++)
            {
                var (pageHits, hasMore) = await BananaIndexPage("Gamefile", id, page, StoreSort.Newest, ct);
                hits.AddRange(pageHits);
                if (!hasMore) break;
            }
        }
        if (hits.Count == 0 && !game.IsAll && query.Length < 2)
            hits.AddRange((await BananaSearchPage(game.Name, null, "Gamefile", 1, StoreSort.Newest, ct)).Hits);
        else if (query.Length >= 2)
            hits.AddRange((await BananaSearchPage(query, game.GameBananaIds.FirstOrDefault() is 0
                ? null : game.GameBananaIds.FirstOrDefault(), "Gamefile", 1, StoreSort.Newest, ct)).Hits);
        foreach (var hit in hits)
        {
            hit.Kind = "Save";
            if (string.IsNullOrEmpty(hit.Summary))
                hit.Summary = "GameBanana bestand / save";
        }
        if (!game.IsAll)
            hits = hits.Where(h => h.SourceGameId is > 0 || game.MatchesTitle(h.GameName) ||
                                   game.GameBananaIds.Count > 0).ToList();
        return DedupHits(hits);
    }

    private static async Task<(List<PackHit> Hits, bool HasMore)> BananaIndexPage(string model, int gameId, int page,
        StoreSort sort, CancellationToken ct)
    {
        const int perPage = 24;
        var url = $"https://gamebanana.com/apiv11/{model}/Index?_nPerpage={perPage}&_nPage={page}&_aFilters[Generic_Game]={gameId}"
                  + BananaSortQuery(sort);
        var (hits, complete, total) = await ReadBananaPage(url, model, gameId, ct);
        var loaded = (page - 1) * perPage + hits.Count;
        var hasMore = !complete && hits.Count >= perPage && (total == 0 || loaded < total);
        return (hits, hasMore);
    }

    private static async Task<(List<PackHit> Hits, bool HasMore)> BananaSearchPage(string query, int? gameId,
        string model, int page, StoreSort? sort, CancellationToken ct)
    {
        const int perPage = 24;
        var url = "https://gamebanana.com/apiv11/Util/Search/Results"
                  + $"?_sModelName={Uri.EscapeDataString(model)}&_nPerpage={perPage}&_nPage={page}&_sSearchString="
                  + Uri.EscapeDataString(query);
        if (gameId is > 0)
            url += "&_idGameRow=" + gameId;
        url += BananaSortQuery(sort);
        var (hits, complete, total) = await ReadBananaPage(url, model, gameId, ct);
        var loaded = (page - 1) * perPage + hits.Count;
        var hasMore = !complete && hits.Count >= perPage && (total == 0 || loaded < total);
        return (hits, hasMore);
    }

    private static async Task<List<PackHit>> ReadBananaRecords(string url, string model, CancellationToken ct) =>
        (await ReadBananaPage(url, model, null, ct)).Hits;

    private static async Task<(List<PackHit> Hits, bool Complete, int Total)> ReadBananaPage(
        string url, string model, int? gameId, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        var hits = new List<PackHit>();
        var complete = false;
        var total = 0;
        if (doc.RootElement.TryGetProperty("_aMetadata", out var meta))
        {
            complete = meta.TryGetProperty("_bIsComplete", out var done) && done.ValueKind == JsonValueKind.True;
            total = meta.TryGetProperty("_nRecordCount", out var n) && n.ValueKind == JsonValueKind.Number
                ? n.GetInt32() : 0;
        }
        if (!doc.RootElement.TryGetProperty("_aRecords", out var records))
            return (hits, true, total);
        foreach (var rec in records.EnumerateArray())
        {
            var recModel = rec.TryGetProperty("_sModelName", out var m) ? m.GetString() : model;
            if (!string.Equals(recModel, model, StringComparison.OrdinalIgnoreCase)) continue;
            var id = rec.TryGetProperty("_idRow", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt32().ToString() : "";
            var gameEl = rec.TryGetProperty("_aGame", out var g) ? g : default;
            var game = gameEl.ValueKind == JsonValueKind.Object && gameEl.TryGetProperty("_sName", out var gn)
                ? gn.GetString() ?? "" : "";
            var recGameId = gameEl.ValueKind == JsonValueKind.Object &&
                            gameEl.TryGetProperty("_idRow", out var gidEl) &&
                            gidEl.ValueKind == JsonValueKind.Number
                ? gidEl.GetInt32()
                : gameId;
            var author = rec.TryGetProperty("_aSubmitter", out var sub) && sub.TryGetProperty("_sName", out var an)
                ? an.GetString() ?? "" : "";
            var version = rec.TryGetProperty("_sVersion", out var ver) ? ver.GetString() ?? "" : "";
            var cat = BananaCategory(rec);
            var images = BananaImages(rec, preferThumb: true);
            var shots = BananaImages(rec, preferThumb: false);
            var title = rec.TryGetProperty("_sName", out var n) ? n.GetString() ?? model : model;
            var added = UnixTime(rec, "_tsDateAdded");
            var updated = UnixTime(rec, "_tsDateUpdated") ?? UnixTime(rec, "_tsDateModified") ?? added;
            var fileName = BananaFileName(rec);
            var platform = BananaPlatform(rec, game, cat);
            var size = BananaTotalSize(rec);
            hits.Add(new PackHit
            {
                Title = title,
                Source = "GameBanana",
                GameName = game,
                Author = author,
                Version = version,
                PageUrl = rec.TryGetProperty("_sProfileUrl", out var p) ? p.GetString() ?? "" : "",
                ItemId = id,
                FileName = fileName,
                Platform = platform,
                Kind = ClassifyKind(title, cat, model, fileName, platform),
                SourceGameId = recGameId,
                ImageUrl = images.FirstOrDefault(),
                ScreenshotUrls = shots,
                Size = size,
                Summary = cat,
                OriginalGame = game,
                AddedUtc = added,
                UpdatedUtc = updated,
                LikeCount = JsonInt(rec, "_nLikeCount"),
                DownloadCount = JsonInt(rec, "_nDownloadCount"),
                ViewCount = JsonInt(rec, "_nViewCount"),
                PostCount = JsonInt(rec, "_nPostCount"),
                WasFeatured = rec.TryGetProperty("_bWasFeatured", out var feat) &&
                              feat.ValueKind == JsonValueKind.True
            });
        }
        if (hits.Count == 0) complete = true;
        return (hits, complete, total);
    }

    private static string BananaSortQuery(StoreSort? sort)
    {
        if (sort is null) return "";
        var q = "";
        if (!string.IsNullOrEmpty(sort.ApiSort))
            q += "&_sSort=" + Uri.EscapeDataString(sort.ApiSort);
        if (sort.FeaturedOnly)
            q += "&_aFilters[Generic_WasFeatured]=true";
        return q;
    }

    private static int JsonInt(JsonElement rec, string name) =>
        rec.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32()
            : 0;

    private static List<PackHit> DedupHits(List<PackHit> hits) =>
        hits.GroupBy(h => !string.IsNullOrEmpty(h.PageUrl) ? h.PageUrl : h.Title + "|" + h.Author,
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    private static async Task FillBananaItem(PackHit hit, CancellationToken ct)
    {
        foreach (var model in new[] { "Mod", "Gamefile" })
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                    $"https://gamebanana.com/apiv11/{model}/{hit.ItemId}?_csvProperties=_aFiles,_aPreviewMedia,_sName,_nDownloadCount",
                    ct));
                if (doc.RootElement.TryGetProperty("_sErrorCode", out _)) continue;
                ApplyBananaFiles(hit, doc.RootElement);
                var thumbs = BananaImages(doc.RootElement, preferThumb: true);
                var shots = BananaImages(doc.RootElement, preferThumb: false);
                if (thumbs.Count > 0) hit.ImageUrl ??= thumbs[0];
                if (shots.Count > 0) hit.ScreenshotUrls = shots;
                var downloads = JsonInt(doc.RootElement, "_nDownloadCount");
                if (downloads > 0) hit.DownloadCount = downloads;
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* andere modelnaam proberen */ }
        }
    }

    private static long BananaTotalSize(JsonElement rec)
    {
        if (!rec.TryGetProperty("_aFiles", out var files) || files.ValueKind != JsonValueKind.Array)
            return 0;
        long total = 0;
        foreach (var file in files.EnumerateArray())
            if (file.TryGetProperty("_nFilesize", out var s) && s.ValueKind == JsonValueKind.Number)
                total += s.GetInt64();
        return total;
    }

    private static void ApplyBananaFiles(PackHit hit, JsonElement rec)
    {
        var parsed = ParseBananaFiles(rec);
        if (!string.IsNullOrEmpty(parsed.Name))
            hit.FileName = parsed.Name;
        if (parsed.Size > 0)
            hit.Size = parsed.Size;
        if (!string.IsNullOrEmpty(parsed.Url))
            hit.DownloadUrl ??= parsed.Url;
    }

    private static async Task<(string? Url, string? Name, long Size)> ResolveGameBananaFile(string itemId,
        CancellationToken ct)
    {
        foreach (var model in new[] { "Mod", "Gamefile" })
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                    $"https://gamebanana.com/apiv11/{model}/{itemId}?_csvProperties=_aFiles,_sName", ct));
                var parsed = ParseBananaFiles(doc.RootElement);
                if (!string.IsNullOrEmpty(parsed.Url) || parsed.Size > 0)
                    return parsed;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* andere modelnaam proberen */ }
        }
        return (null, null, 0);
    }

    private static (string? Url, string? Name, long Size) ParseBananaFiles(JsonElement root)
    {
        if (root.TryGetProperty("_sErrorCode", out _))
            return (null, null, 0);
        if (!root.TryGetProperty("_aFiles", out var files) || files.ValueKind != JsonValueKind.Array ||
            files.GetArrayLength() == 0)
            return (null, null, 0);

        JsonElement best = files[0];
        var bestSize = -1L;
        foreach (var file in files.EnumerateArray())
        {
            var av = file.TryGetProperty("_sAvResult", out var avEl) ? avEl.GetString() : "clean";
            if (!string.Equals(av, "clean", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(av) && av != "ok")
                continue;
            var size = file.TryGetProperty("_nFilesize", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt64() : 0;
            if (size >= bestSize)
            {
                best = file;
                bestSize = size;
            }
        }

        var dl = best.TryGetProperty("_sDownloadUrl", out var d) ? d.GetString() : null;
        var name = best.TryGetProperty("_sFile", out var n) ? n.GetString() : null;
        return (dl, name, bestSize < 0 ? 0 : bestSize);
    }

    private static List<string>? _kingSitemap;
    private static DateTime _kingSitemapAt;

    private static async Task<List<PackHit>> SearchEmulationKing(string query, StoreGame game, CancellationToken ct)
    {
        var urls = await GetKingSitemap(ct);
        var slugs = game.IsAll
            ? []
            : game.KingSlugs.Count > 0
                ? game.KingSlugs
                : [StoreGame.Slug(game.Name)];
        var platform = game.IsAll ? "" : KingPlatform(game.System);
        var extra = Tokenize(query);

        var ranked = urls
            .Select(url => (url, score: ScoreKingUrl(url, slugs, platform, extra, game.IsAll)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(8)
            .ToList();

        var hits = new List<PackHit>();
        foreach (var (url, _) in ranked)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var html = await Http.GetStringAsync(url, ct);
                var title = ExtractHtmlTitle(html) ?? TitleFromUrl(url);
                var download = ExtractKingDownload(html);
                var name = GuessGameFromUrl(url);
                var images = ExtractPageImages(html, "emulationking.com", "files.emulationking.com");
                hits.Add(new PackHit
                {
                    Title = title,
                    Source = "Emulation King",
                    GameName = string.IsNullOrEmpty(name) ? game.Name : name,
                    Kind = "Texture pack",
                    PageUrl = url,
                    DownloadUrl = download,
                    FileName = string.IsNullOrEmpty(download) ? null : Path.GetFileName(new Uri(download).AbsolutePath),
                    ImageUrl = images.FirstOrDefault(),
                    ScreenshotUrls = images,
                    Summary = string.IsNullOrEmpty(download)
                        ? "Open the page for the download"
                        : "Direct download found"
                });
            }
            catch
            {
                hits.Add(new PackHit
                {
                    Title = TitleFromUrl(url),
                    Source = "Emulation King",
                    GameName = game.IsAll ? GuessGameFromUrl(url) : game.Name,
                    Kind = "Texture pack",
                    PageUrl = url,
                    Summary = "Open de pagina voor de download"
                });
            }
        }
        return hits;
    }

    private static async Task<List<string>> GetKingSitemap(CancellationToken ct)
    {
        if (_kingSitemap is { Count: > 0 } && DateTime.UtcNow - _kingSitemapAt < TimeSpan.FromMinutes(30))
            return _kingSitemap;

        var xml = await Http.GetStringAsync("https://emulationking.com/sitemap.xml", ct);
        var urls = Regex.Matches(xml, @"<loc>(https://emulationking\.com/[^<]+)</loc>", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _kingSitemap = urls;
        _kingSitemapAt = DateTime.UtcNow;
        return urls;
    }

    private static int ScoreKingUrl(string url, IReadOnlyList<string> slugs, string platform,
        List<string> extra, bool allowLoose)
    {
        var slug = url.ToLowerInvariant();
        if (slug.Contains("/emulator/") || slug.Contains("/homebrew/") || slug.Contains("/plugin/"))
            return 0;
        if (platform.Length > 0 && !slug.Contains(platform, StringComparison.Ordinal))
            return 0;

        if (slugs.Count > 0)
        {
            if (!slugs.Any(s => s.Length >= 3 && slug.Contains("/games/" + s.ToLowerInvariant() + "/",
                    StringComparison.Ordinal)))
                return 0;
        }
        else if (!allowLoose)
        {
            return 0;
        }
        else
        {
            if (extra.Count == 0) return 0;
            if (!extra.All(t => t.Length < 3 || slug.Contains(t, StringComparison.Ordinal)))
                return 0;
        }

        var score = slugs.Count > 0 ? 10 : 2;
        foreach (var token in extra)
            if (slug.Contains(token, StringComparison.Ordinal))
                score += token.Length >= 4 ? 4 : 2;
        if (slug.Contains("/texturepacks/", StringComparison.Ordinal)) score += 8;
        if (slug.Contains("/games/", StringComparison.Ordinal)) score += 3;
        return score;
    }

    private static string KingPlatform(string system) => StoreGame.FoldSystem(system) switch
    {
        "n64" => "/n64/",
        "switch" => "/switch/",
        "gc" => "/gamecube/",
        "nes" => "/nes/",
        "snes" => "/snes/",
        "wii" => "/wii/",
        _ => ""
    };

    private static async Task<List<PackHit>> SearchGbaTemp(string query, StoreGame game, CancellationToken ct)
    {
        var term = game.IsAll ? query : string.IsNullOrWhiteSpace(query) ? game.Name : $"{game.Name} {query}";
        if (term.Length < 2) return [];
        var url = "https://gbatemp.net/search/1/?q=" + Uri.EscapeDataString(term + " texture pack")
                  + "&c[nodes][0]=736&o=relevance";
        string html;
        try { html = await Http.GetStringAsync(url, ct); }
        catch
        {
            html = await Http.GetStringAsync("https://gbatemp.net/forums/retro-texture-packs.736/", ct);
        }

        var hits = new List<PackHit>();
        foreach (Match m in Regex.Matches(html,
                     @"href=""(?<u>/threads/[^""]+)""[^>]*>(?<t>[^<]{3,160})</a>", RegexOptions.IgnoreCase))
        {
            var title = System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups["t"].Value, @"\s+", " ")).Trim();
            if (title.Length < 3 || title.Contains("Post thread", StringComparison.OrdinalIgnoreCase)) continue;
            if (!game.IsAll && !game.MatchesTitle(title) && game.Conflicts(title)) continue;
            if (!game.IsAll && !game.MatchesTitle(title) &&
                !game.TitlePhrases().Any(p => StoreGame.ContainsPhrase(title, p)))
                continue;
            var page = m.Groups["u"].Value;
            if (!page.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                page = "https://gbatemp.net" + page;
            hits.Add(new PackHit
            {
                Title = title,
                Source = "GBAtemp",
                GameName = game.IsAll ? "" : game.Name,
                Kind = "Texture pack",
                PageUrl = page.Split('?')[0],
                Summary = "Texture-pack thread — open voor downloads"
            });
        }

        return hits
            .GroupBy(h => h.PageUrl, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(16)
            .ToList();
    }

    private static bool HitMatches(PackHit hit, StoreGame game)
    {
        if (hit.SourceGameId is int gid && game.GameBananaIds.Contains(gid))
            return true;
        if (!string.IsNullOrWhiteSpace(hit.GameName) && game.MatchesTitle(hit.GameName))
            return !game.Conflicts(hit.GameName) && !game.Conflicts(hit.Title);
        if (game.MatchesTitle(hit.Title) && !game.Conflicts(hit.Title))
            return true;
        return game.KingSlugs.Any(s =>
            (hit.PageUrl ?? "").Contains("/games/" + s + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string?> ResolvePageDownload(string pageUrl, CancellationToken ct)
    {
        if (pageUrl.Contains("emulationking.com", StringComparison.OrdinalIgnoreCase))
        {
            var html = await Http.GetStringAsync(pageUrl, ct);
            return ExtractKingDownload(html);
        }
        if (pageUrl.Contains("gamebanana.com/mods/", StringComparison.OrdinalIgnoreCase) ||
            pageUrl.Contains("gamebanana.com/gamefiles/", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(pageUrl, @"/(?:mods|gamefiles)/(\d+)");
            if (m.Success)
                return (await ResolveGameBananaFile(m.Groups[1].Value, ct)).Url;
        }
        return null;
    }

    private static bool KindIs(string kind, params string[] names) =>
        names.Any(n => kind.Equals(n, StringComparison.OrdinalIgnoreCase));

    private static DateTime? UnixTime(JsonElement rec, string name)
    {
        if (!rec.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return null;
        var sec = el.GetInt64();
        return sec <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime;
    }

    private static bool LooksLikeTexture(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var n = text.ToLowerInvariant();
        return n.Contains("texture") || n.Contains("hd pack") || n.Contains("hires") ||
               n.Contains("hi-res") || n.Contains("retexture");
    }

    private static bool LooksLikeSave(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var n = text.ToLowerInvariant();
        return n.Contains("save file") || n.Contains("savefile") || n.Contains("save game") ||
               n.Contains("savegame") || n.Contains("100% save") || n.Contains("everything unlocked") ||
               n.Contains("userdata") || n.Contains("save data") || n.Contains("completed save") ||
               (n.Contains("unlocked") && n.Contains("save"));
    }

    private static string? ExtractHtmlTitle(string html)
    {
        var m = Regex.Match(html, @"<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;
        var title = Regex.Replace(m.Groups[1].Value, "<.*?>", "");
        title = System.Net.WebUtility.HtmlDecode(title).Replace(" - Emulation King", "").Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static string? ExtractKingDownload(string html)
    {
        var m = Regex.Match(html, @"href=""(https://files\.emulationking\.com/[^""]+\.(?:zip|rar|7z|hts|htc))""",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<string> ExtractPageImages(string html, params string[] hosts)
    {
        var urls = new List<string>();
        void Add(string? raw)
        {
            raw = StoreUrls.NormalizeUrl(System.Net.WebUtility.HtmlDecode(raw ?? "").Trim());
            if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return;
            var path = uri.AbsolutePath.ToLowerInvariant();
            if (path.Contains("logo") || path.Contains("favicon") || path.Contains("icon") ||
                path.Contains("avatar") || StoreUrls.IsPlaceholder(raw))
                return;
            if (hosts.Length > 0 && !hosts.Any(h => uri.Host.Contains(h, StringComparison.OrdinalIgnoreCase)))
                return;
            if (!path.EndsWith(".png") && !path.EndsWith(".jpg") && !path.EndsWith(".jpeg") &&
                !path.EndsWith(".webp") && !path.EndsWith(".gif"))
                return;
            if (!urls.Exists(u => u.Equals(raw, StringComparison.OrdinalIgnoreCase)))
                urls.Add(raw);
        }

        Add(MetaContent(html, "og:image"));
        foreach (Match m in Regex.Matches(html, @"<img[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase))
            Add(m.Groups[1].Value);
        return urls.Take(6).ToList();
    }

    private static string TitleFromUrl(string url)
    {
        var parts = new Uri(url).AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = parts.LastOrDefault() ?? url;
        return Regex.Replace(last, @"[-_]+", " ");
    }

    private static string GuessGameFromUrl(string url)
    {
        var m = Regex.Match(url, @"/games/([^/]+)/", RegexOptions.IgnoreCase);
        return m.Success ? Regex.Replace(m.Groups[1].Value, @"[-_]+", " ") : "";
    }

    private static string GuessFileName(string url)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            return string.IsNullOrWhiteSpace(name) || name is "download" ? "pack.bin" : name;
        }
        catch { return "pack.bin"; }
    }

    private static List<string> Tokenize(string query) =>
        Regex.Split(query.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(t => t.Length >= 2 && t is not "the" and not "and" and not "pack" and not "packs")
            .ToList();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return client;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
