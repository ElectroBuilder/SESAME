using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sesame.Models;
using Sesame.Services;

namespace Sesame;

public partial class StoreView : UserControl
{
    private readonly PackStore _store = new();
    private readonly ModLibrary _mods = new();
    private readonly ObservableCollection<PackHit> _hits = new();
    private readonly ListCollectionView _hitView;
    private readonly ObservableCollection<StoreGame> _games = new() { StoreGame.All };
    private readonly ObservableCollection<ImageSource> _shots = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _installedFolders = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _infoCts;
    private CancellationTokenSource? _shotCts;
    private StoreGameInfo? _gameInfo;
    private PackHit? _detailHit;
    private StoreSort _sort = StoreSort.Popular;
    private string _sortProperty = nameof(PackHit.LikeCount);
    private ListSortDirection _sortDir = ListSortDirection.Descending;

    public event Action<PackHit>? InstallRequested;
    public event Action<PackHit>? DeleteRequested;
    public event Action<PackHit, bool>? ToggleRequested;
    public Func<PackHit, string?>? TargetResolver { get; set; }
    public StoreGame SelectedStoreGame => GameBox.SelectedItem as StoreGame ?? StoreGame.All;
    public ModLibrary Mods => _mods;

    public StoreView()
    {
        InitializeComponent();
        _hitView = new ListCollectionView(_hits);
        _hitView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PackHit.Section)));
        _hitView.IsLiveSorting = true;
        _hitView.IsLiveFiltering = true;
        ResultList.ItemsSource = _hitView;
        ShotList.ItemsSource = _shots;
        GameBox.ItemsSource = _games;
        GameBox.SelectedIndex = 0;
        SortBox.ItemsSource = StoreSort.All;
        SortBox.SelectedItem = StoreSort.Popular;
        _mods.Load();
        _hitView.Filter = HitPassesFilter;
        ListColumns.Attach(ResultList, _hitView, sort: false,
            properties: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Added"] = nameof(PackHit.AddedUtc),
                ["Updated"] = nameof(PackHit.UpdatedUtc),
                ["Size"] = nameof(PackHit.Size),
                ["Status"] = nameof(PackHit.StatusText)
            });
        ApplySort();
    }

    public void RefreshLocalState(IReadOnlyDictionary<string, IReadOnlyList<string>>? installedByTitleId = null)
    {
        if (installedByTitleId is not null)
        {
            _installedFolders.Clear();
            foreach (var (id, folders) in installedByTitleId)
                _installedFolders[id] = folders;
        }
        ApplyLocalState();
        ApplyViewFilter();
    }

    public void SetGames(IEnumerable<StoreGame> catalogGames, IEnumerable<StoreGame>? libraryGames = null)
    {
        var selected = GameBox.SelectedItem as StoreGame;
        var catalog = catalogGames.Where(g => !g.IsAll).ToList();
        var merged = new List<StoreGame> { StoreGame.All };
        foreach (var game in (libraryGames ?? []).Where(g => !g.IsAll))
        {
            var item = game.Clone();
            var known = catalog.FirstOrDefault(c => c.SameIdentity(item) || Namesake(c, item));
            if (known is not null)
                item.MergeFrom(known);
            var existing = merged.FirstOrDefault(g => !g.IsAll && (g.SameIdentity(item) || Namesake(g, item)));
            if (existing is null)
                merged.Add(item);
            else
                existing.MergeFrom(item);
        }

        merged.Sort((a, b) =>
        {
            if (a.IsAll) return -1;
            if (b.IsAll) return 1;
            var sys = string.Compare(a.System, b.System, StringComparison.OrdinalIgnoreCase);
            return sys != 0 ? sys : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        _games.Clear();
        foreach (var game in merged) _games.Add(game);
        GameBox.SelectedItem = selected is null
            ? _games[0]
            : _games.FirstOrDefault(g => g.SameIdentity(selected) || Namesake(g, selected)) ?? _games[0];
    }

    public void Prefill(StoreGame? game, string query = "", string? kind = null)
    {
        if (game is { IsAll: false })
        {
            var match = _games.FirstOrDefault(g => !g.IsAll && (g.SameIdentity(game) || Namesake(g, game)));
            if (match is null)
            {
                _games.Add(game.Clone());
                match = _games[^1];
            }
            else
            {
                match.MergeFrom(game);
            }
            GameBox.SelectedItem = match;
            QueryBox.Text = string.IsNullOrWhiteSpace(query) ||
                            query.Contains(game.Name, StringComparison.OrdinalIgnoreCase)
                ? ""
                : query;
        }
        else
        {
            QueryBox.Text = query;
        }

        if (!string.IsNullOrWhiteSpace(kind))
            SelectCombo(KindBox, kind);
        _ = SearchAsync();
    }

    private static bool Namesake(StoreGame a, StoreGame b) =>
        !a.IsAll && !b.IsAll &&
        a.SameVariant(b) &&
        a.MatchesSystem(b.System) &&
        a.MatchesTitle(StoreGame.StripVariant(b.Name)) &&
        b.MatchesTitle(StoreGame.StripVariant(a.Name));

    private static void SelectCombo(ComboBox box, string content)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private void Game_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var game = GameBox.SelectedItem as StoreGame ?? StoreGame.All;
        _ = LoadGameInfoAsync(game);
        var safety = DiscInstallHint(game);
        if (safety.Length > 0) HintText.Text = safety;
        if (!game.IsAll)
            _ = SearchAsync(forceRefresh: false);
    }

    private void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SearchAsync(forceRefresh: false);
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(forceRefresh: true);

    private static readonly TimeSpan CacheFreshFor = TimeSpan.FromHours(12);

    private async Task SearchAsync(bool forceRefresh = false)
    {
        var query = QueryBox.Text.Trim();
        var game = GameBox.SelectedItem as StoreGame ?? StoreGame.All;
        if (query.Length < 2 && game.IsAll)
        {
            if (StatusFilter() != "Any status")
            {
                _hits.Clear();
                MergeLibraryHits(game);
                ApplyLocalState();
                HintText.Text = $"{_hits.Count} lokale mods · {StatusFilter().ToLowerInvariant()}";
                if (_hits.Count > 0) ResultList.SelectedIndex = 0;
                return;
            }
            HintText.Text = "Pick a game or type at least 2 characters.";
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var source = (SourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All sources";
        var kind = (KindBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        HintText.Text = "Searching… " + game.IdentityText;
        _hits.Clear();
        ClearDetail();
        _ = LoadGameInfoAsync(game);

        var cacheKey = StoreResultCache.Key(game, query, kind, source, _sort.Tag);
        var cached = StoreResultCache.TryLoad(cacheKey);
        if (cached is not null)
        {
            var loaded = cached.Hits.Select(StoreResultCache.ToHit).ToList();
            foreach (var hit in loaded)
                _hits.Add(hit);
            MergeLibraryHits(game);
            ApplyLocalState();
            HintText.Text = CacheStatus(cached, game.IdentityText);
            if (_hits.Count > 0)
                ResultList.SelectedIndex = 0;
            _ = PrefetchThumbsAsync(_hits.ToList(), ct);
            _ = PrefetchDetailsAsync(loaded, cacheKey, game, cached.HasMore, ct);
            if (!forceRefresh && cached.IsFresh(CacheFreshFor))
                return;
            if (!forceRefresh && cached.IsComplete)
            {
                HintText.Text = CacheStatus(cached, game.IdentityText) + " · stil bijwerken…";
                _ = RefreshCacheQuietAsync(cacheKey, query, source, kind, game, cached, ct);
                return;
            }
        }

        try
        {
            await LoadLiveAsync(cacheKey, query, source, kind, game, cached, ct);
        }
        catch (OperationCanceledException)
        {
            // nieuwere zoekopdracht
        }
        catch (Exception ex)
        {
            if (_hits.Count > 0)
                HintText.Text = $"{_hits.Count} results from cache (refresh failed)";
            else
            {
                ClearDetail();
                HintText.Text = "Search failed: " + ex.Message;
            }
        }
    }

    private static string CacheStatus(CachedStoreSearch cached, string identity)
    {
        var age = cached.Age;
        var when = age.TotalMinutes < 2 ? "zojuist"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min geleden"
            : age.TotalHours < 24 ? $"{(int)age.TotalHours} uur geleden"
            : $"{(int)age.TotalDays} d geleden";
        return $"{cached.Hits.Count} resultaten uit cache · {identity} · {when}";
    }

    private async Task RefreshCacheQuietAsync(string cacheKey, string query, string source, string kind,
        StoreGame game, CachedStoreSearch cached, CancellationToken ct)
    {
        try
        {
            var previous = _hits.ToList();
            var first = await _store.SearchAsync(query, source, kind, game, _sort, ct);
            if (ct.IsCancellationRequested) return;
            var old = previous
                .GroupBy(StoreResultCache.HitKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var changed = first.Any(hit =>
            {
                var key = StoreResultCache.HitKey(hit);
                if (!old.TryGetValue(key, out var known)) return true;
                return !string.Equals(known.Version, hit.Version, StringComparison.OrdinalIgnoreCase) ||
                       known.UpdatedUtc != hit.UpdatedUtc;
            });
            if (!changed)
            {
                StoreResultCache.Save(cacheKey, previous, false, game.IdentityText);
                HintText.Text = CacheStatus(StoreResultCache.TryLoad(cacheKey) ?? cached, game.IdentityText);
                return;
            }

            await LoadLiveAsync(cacheKey, query, source, kind, game, cached, ct, seed: first);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            HintText.Text = CacheStatus(cached, game.IdentityText);
        }
    }

    private async Task LoadLiveAsync(string cacheKey, string query, string source, string kind,
        StoreGame game, CachedStoreSearch? cached, CancellationToken ct, IReadOnlyList<PackHit>? seed = null)
    {
        var previous = _hits.ToList();
        var live = new List<PackHit>();
        if (seed is { Count: > 0 })
        {
            live.AddRange(seed);
            MergeHits(seed, replaceMissing: false);
        }
        else
        {
            var first = await _store.SearchAsync(query, source, kind, game, _sort, ct);
            live.AddRange(first);
            MergeHits(first, replaceMissing: false);
            _ = PrefetchThumbsAsync(first, ct);
        }

        if (_hits.Count > 0 && ResultList.SelectedIndex < 0)
            ResultList.SelectedIndex = 0;
        if (cached is null)
            HintText.Text = live.Count + " resultaten · rest op de achtergrond laden…";

        while (_store.HasMore && !ct.IsCancellationRequested)
        {
            var extra = await _store.SearchMoreAsync(ct);
            if (extra.Count == 0) break;
            live.AddRange(extra);
            MergeHits(extra, replaceMissing: false);
            if (cached is null)
                HintText.Text = $"{_hits.Count} resultaten · verder laden…";
            StoreResultCache.Save(cacheKey, DistinctHits(live), true, game.IdentityText);
            _ = PrefetchThumbsAsync(extra, ct);
        }

        var complete = DistinctHits(live);
        ReplaceHits(complete);
        MergeLibraryHits(game);
        ApplyLocalState();
        StoreResultCache.Save(cacheKey, complete, false, game.IdentityText);
        HintText.Text = AppendDiscHint(
            BuildSearchStatus(_hits.Count, game, previous, complete, cached is not null, _sort.Label), game);
        if (_hits.Count > 0 && ResultList.SelectedIndex < 0)
            ResultList.SelectedIndex = 0;
        else if (_hits.Count == 0)
            ClearDetail();
        _ = PrefetchDetailsAsync(complete, cacheKey, game, false, ct);
    }

    private static string BuildSearchStatus(int count, StoreGame game, IReadOnlyList<PackHit> previous,
        IReadOnlyList<PackHit> next, bool hadCache, string sortLabel)
    {
        var identity = game.IdentityText;
        if (count == 0)
            return $"No results for {identity}.";
        if (!hadCache || previous.Count == 0)
            return $"{count} resultaten · {identity} · {sortLabel}";
        var (added, updated, removed) = StoreResultCache.Diff(previous, next);
        var changes = added + updated + removed;
        if (changes == 0)
            return $"{count} resultaten · bijgewerkt · {identity} · {sortLabel}";
        if (updated > 0 && added == 0 && removed == 0)
            return $"{count} results · {updated} update{(updated == 1 ? "" : "s")} found · {sortLabel}";
        return $"{count} resultaten · bijgewerkt (+{added} / ~{updated} / -{removed}) · {sortLabel}";
    }

    private static string DiscInstallHint(StoreGame game) =>
        DiscPackRouting.IsDiscSystem(game.System)
            ? "Close the emulator before install. Unknown IDs/layouts are staged (not active); saves require a format-specific importer."
            : "";

    private static string AppendDiscHint(string status, StoreGame game)
    {
        var hint = DiscInstallHint(game);
        return hint.Length == 0 ? status : status + " · " + hint;
    }

    private async Task LoadGameInfoAsync(StoreGame game)
    {
        _infoCts?.Cancel();
        _infoCts = new CancellationTokenSource();
        var ct = _infoCts.Token;
        if (game.IsAll)
        {
            GameHeader.Visibility = Visibility.Collapsed;
            return;
        }

        GameHeader.Visibility = Visibility.Visible;
        GameTitle.Text = game.Name;
        GameMeta.Text = game.IdentityText;
        GameDesc.Text = "Game-info laden…";
        GameCover.Source = null;
        try
        {
            var info = await _store.GetGameInfoAsync(game, ct);
            if (ct.IsCancellationRequested) return;
            _gameInfo = info;
            GameTitle.Text = info.Name;
            GameMeta.Text = string.IsNullOrWhiteSpace(info.Meta) ? info.IdentityText : info.Meta;
            GameDesc.Text = info.Description;
            var cover = await StoreImageCache.LoadAsync(info.CoverUrl ?? info.BannerUrl, 160, ct);
            if (cover is not null && !ct.IsCancellationRequested)
            {
                info.Cover = cover;
                GameCover.Source = cover;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            GameDesc.Text = game.IdentityText;
        }
    }

    private void MergeHits(IEnumerable<PackHit> incoming, bool replaceMissing)
    {
        var incomingList = incoming.ToList();
        var map = _hits
            .GroupBy(StoreResultCache.HitKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var hit in incomingList)
        {
            var key = StoreResultCache.HitKey(hit);
            if (map.TryGetValue(key, out var existing))
                CopyLive(existing, hit);
            else
            {
                _hits.Add(hit);
                map[key] = hit;
            }
        }
        if (replaceMissing)
        {
            var keep = incomingList.Select(StoreResultCache.HitKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = _hits.Count - 1; i >= 0; i--)
                if (!keep.Contains(StoreResultCache.HitKey(_hits[i])))
                    _hits.RemoveAt(i);
        }
        _hitView.Refresh();
    }

    private void ReplaceHits(IReadOnlyList<PackHit> live)
    {
        var selected = ResultList.SelectedItem is PackHit hit ? StoreResultCache.HitKey(hit) : null;
        MergeHits(live, replaceMissing: true);
        if (selected is null) return;
        var match = _hits.FirstOrDefault(h =>
            string.Equals(StoreResultCache.HitKey(h), selected, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            ResultList.SelectedItem = match;
    }

    private static void CopyLive(PackHit dest, PackHit src)
    {
        dest.Title = src.Title;
        dest.Kind = src.Kind;
        dest.Author = src.Author;
        dest.Version = src.Version;
        dest.Summary = src.Summary;
        dest.AddedUtc = src.AddedUtc;
        dest.UpdatedUtc = src.UpdatedUtc;
        dest.LikeCount = src.LikeCount;
        dest.DownloadCount = src.DownloadCount;
        dest.ViewCount = src.ViewCount;
        dest.PostCount = src.PostCount;
        dest.WasFeatured = src.WasFeatured;
        dest.SearchRank = src.SearchRank;
        dest.DownloadUrl = src.DownloadUrl ?? dest.DownloadUrl;
        dest.FileName = src.FileName ?? dest.FileName;
        dest.ItemId = src.ItemId ?? dest.ItemId;
        dest.ImageUrl = src.ImageUrl ?? dest.ImageUrl;
        if (src.ScreenshotUrls.Count > dest.ScreenshotUrls.Count)
            dest.ScreenshotUrls = src.ScreenshotUrls;
        else if (src.ScreenshotUrls.Count > 0 && dest.ScreenshotUrls.Count == 0)
            dest.ScreenshotUrls = src.ScreenshotUrls;
        if (src.Size > 0) dest.Size = src.Size;
        dest.SourceGameId = src.SourceGameId ?? dest.SourceGameId;
        dest.GameName = string.IsNullOrWhiteSpace(src.GameName) ? dest.GameName : src.GameName;
    }

    private void MergeLibraryHits(StoreGame game)
    {
        var known = _hits
            .Select(StoreResultCache.HitKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in _mods.ForGame(game))
        {
            var hit = _mods.ToHit(rec);
            var key = StoreResultCache.HitKey(hit);
            if (known.Contains(key)) continue;
            _hits.Add(hit);
            known.Add(key);
        }
        ApplyViewFilter();
    }

    private void ApplyLocalState()
    {
        foreach (var hit in _hits)
        {
            if (hit.IsBusy || hit.IsQueued) continue;
            if (TargetResolver is not null)
                hit.TargetPath = TargetResolver(hit);
            _mods.Apply(hit);
        }

        var game = SelectedStoreGame;
        var titleId = game.TitleId;
        if (!string.IsNullOrEmpty(titleId) && _installedFolders.TryGetValue(titleId, out var folders))
            _mods.MarkInstalledFolders(_hits, titleId, folders);
        else if (game.IsAll)
        {
            foreach (var (id, found) in _installedFolders)
                _mods.MarkInstalledFolders(_hits, id, found);
        }
        ApplyViewFilter();
    }

    private void StatusFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyViewFilter();
        if (_hits.Count == 0 && StatusFilter() != "Any status")
            _ = SearchAsync();
    }

    private string StatusFilter() =>
        (StatusFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Any status";

    private bool HitPassesFilter(object obj)
    {
        if (obj is not PackHit hit) return false;
        var statusOk = StatusFilter() switch
        {
            "Downloaded" => hit.IsDownloaded || hit.IsInstalled,
            "Installed" => hit.IsInstalled,
            _ => true
        };
        if (!statusOk) return false;
        if (_sort.ThisWeek && !hit.IsInstalled && !hit.IsDownloaded && !hit.IsQueued && !hit.IsBusy
            && !IsThisWeek(hit))
            return false;
        return true;
    }

    private static bool IsThisWeek(PackHit hit)
    {
        var since = DateTime.UtcNow.AddDays(-7);
        return (hit.AddedUtc is DateTime added && added >= since)
               || (hit.UpdatedUtc is DateTime updated && updated >= since);
    }

    private void ApplyViewFilter()
    {
        _hitView.Refresh();
    }

    private static List<PackHit> DistinctHits(IEnumerable<PackHit> hits) =>
        hits.GroupBy(StoreResultCache.HitKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var next = SortBox.SelectedItem as StoreSort ?? StoreSort.Popular;
        var apiChanged = next.UsesApi != _sort.UsesApi
                         || next.ApiSort != _sort.ApiSort
                         || next.FeaturedOnly != _sort.FeaturedOnly
                         || next.ThisWeek != _sort.ThisWeek;
        _sort = next;
        _sortProperty = next.ClientProperty;
        _sortDir = next.Direction;
        ApplySort();
        if (apiChanged)
            _ = SearchAsync();
        else
            ApplyViewFilter();
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        var header = HeaderOf(e.OriginalSource as DependencyObject);
        if (header?.Column is null) return;
        var key = ListColumns.TryInfo(header.Column, out var title, out var property)
            ? title
            : header.Column.Header as string;
        if (string.IsNullOrEmpty(key) && property.Length > 0) key = property;
        var mapped = StoreSort.FromHeader(key);
        if (mapped is not null)
        {
            SortBox.SelectedItem = mapped;
            return;
        }

        var prop = key switch
        {
            nameof(PackHit.Kind) or "Kind" => nameof(PackHit.Kind),
            nameof(PackHit.Source) or "Source" => nameof(PackHit.Source),
            _ => nameof(PackHit.Title)
        };
        if (prop == _sortProperty)
        {
            _sortDir = _sortDir == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            _sortProperty = prop;
            _sortDir = ListSortDirection.Ascending;
        }
        ApplySort();
    }

    private static GridViewColumnHeader? HeaderOf(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is GridViewColumnHeader header) return header;
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private void ApplySort()
    {
        using (_hitView.DeferRefresh())
        {
            _hitView.SortDescriptions.Clear();
            _hitView.SortDescriptions.Add(new SortDescription(
                nameof(PackHit.PinRank), ListSortDirection.Descending));
            if (_sort.UsesApi && !_sort.ThisWeek)
                _hitView.SortDescriptions.Add(new SortDescription(
                    nameof(PackHit.SearchRank), ListSortDirection.Ascending));
            else
                _hitView.SortDescriptions.Add(new SortDescription(_sortProperty, _sortDir));
            if (_sortProperty != nameof(PackHit.Title))
                _hitView.SortDescriptions.Add(new SortDescription(
                    nameof(PackHit.Title), ListSortDirection.Ascending));
        }
    }

    private async Task PrefetchThumbsAsync(IReadOnlyList<PackHit> hits, CancellationToken ct)
    {
        try
        {
            await Parallel.ForEachAsync(hits.Take(24),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (hit, token) =>
                {
                    var bmp = await StoreImageCache.LoadAsync(hit.BestImageUrl, 220, token);
                    if (bmp is null) return;
                    await Dispatcher.InvokeAsync(() => hit.Thumbnail = bmp);
                });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PrefetchDetailsAsync(IReadOnlyList<PackHit> hits, string cacheKey, StoreGame game,
        bool hasMore, CancellationToken ct)
    {
        try
        {
            var need = hits.Where(h =>
                    h.Size <= 0 || h.ScreenshotUrls.Count == 0 || string.IsNullOrWhiteSpace(h.ImageUrl))
                .Take(40)
                .ToList();
            if (need.Count == 0) return;
            await Parallel.ForEachAsync(need,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (hit, token) =>
                {
                    try { await _store.FillHitDetailsAsync(hit, token); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* grootte/screenshots zijn optioneel */ }
                });
            if (ct.IsCancellationRequested) return;
            StoreResultCache.Save(cacheKey, DistinctHits(hits), hasMore, game.IdentityText);
            if (_detailHit is PackHit selected)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_detailHit, selected))
                        BindDetailMeta(selected);
                });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Result_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!IsLoaded || _hits.Count == 0 || _searchCts is null) return;
        var visible = new List<PackHit>();
        for (var i = 0; i < ResultList.Items.Count && visible.Count < 16; i++)
        {
            if (ResultList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement { IsVisible: true })
                continue;
            if (ResultList.Items[i] is PackHit { HasThumbnail: false } hit)
                visible.Add(hit);
        }
        if (visible.Count > 0)
            _ = PrefetchThumbsAsync(visible, _searchCts.Token);
    }

    private async void Result_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is not PackHit hit)
        {
            ClearDetail();
            return;
        }

        BindDetail(hit);
        BindDetailMeta(hit);
        ShowRomHackDetail(hit);
        UpdateDetailStatus(hit);
        _ = InspectSelectedAsync(hit);
        await LoadScreenshotsAsync(hit);
    }

    private void BindDetailMeta(PackHit hit)
    {
        DetailTitle.Text = hit.Title;
        var dates = new List<string>();
        if (!string.IsNullOrWhiteSpace(hit.SizeText)) dates.Add(hit.SizeText);
        if (hit.LikeCount > 0) dates.Add(hit.LikeCount + " likes");
        if (hit.DownloadCount > 0) dates.Add(hit.DownloadCount + " downloads");
        if (hit.ViewCount > 0) dates.Add(hit.ViewCount + " views");
        if (hit.WasFeatured) dates.Add("uitgelicht");
        if (!string.IsNullOrEmpty(hit.AddedText)) dates.Add("toegevoegd " + hit.AddedText);
        if (!string.IsNullOrEmpty(hit.UpdatedText)) dates.Add("bijgewerkt " + hit.UpdatedText);
        var meta = string.IsNullOrWhiteSpace(hit.GameLabel)
            ? hit.CardMeta
            : hit.GameLabel + " · " + hit.CardMeta;
        if (dates.Count > 0)
            meta = string.IsNullOrWhiteSpace(meta) ? string.Join(" · ", dates) : meta + " · " + string.Join(" · ", dates);
        DetailMeta.Text = meta;
        DetailSummary.Text = hit.Summary;
    }

    private async Task LoadScreenshotsAsync(PackHit hit)
    {
        _shotCts?.Cancel();
        _shotCts = new CancellationTokenSource();
        var ct = _shotCts.Token;
        PreviewImage.Source = (hit.Preview as ImageSource) ?? (hit.Thumbnail as ImageSource);
        PreviewPlaceholder.Visibility = PreviewImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        _shots.Clear();

        var urls = hit.ScreenshotUrls.Count > 0
            ? hit.ScreenshotUrls
            : string.IsNullOrEmpty(hit.ImageUrl) ? [] : [hit.ImageUrl];
        foreach (var url in urls.Take(8))
        {
            BitmapImage? bmp;
            try { bmp = await StoreImageCache.LoadAsync(url, 480, ct); }
            catch (OperationCanceledException) { return; }
            if (bmp is null || ct.IsCancellationRequested || !ReferenceEquals(_detailHit, hit)) return;
            if (!hit.HasPreview)
            {
                hit.Preview = bmp;
                PreviewImage.Source = bmp;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            if (!hit.HasThumbnail)
                hit.Thumbnail = bmp;
            _shots.Add(bmp);
        }
    }

    private void Shot_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ShotList.SelectedItem is ImageSource img)
        {
            PreviewImage.Source = img;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowRomHackDetail(PackHit hit)
    {
        if (hit.IsRomHack)
        {
            DetailRom.Visibility = Visibility.Visible;
            var rom = hit.RequiredRomName ?? hit.OriginalGame ?? hit.GameName;
            var hashes = new List<string>();
            if (!string.IsNullOrWhiteSpace(hit.FileSha1)) hashes.Add("SHA-1 " + hit.FileSha1);
            if (!string.IsNullOrWhiteSpace(hit.FileCrc32)) hashes.Add("CRC32 " + hit.FileCrc32);
            DetailRom.Text = PackStore.LegalHackNl + Environment.NewLine + Environment.NewLine +
                             (string.IsNullOrWhiteSpace(rom) ? "Basis-ROM: zie de hack-pagina." : "Basis-ROM: " + rom) +
                             (hashes.Count == 0 ? "" : Environment.NewLine + string.Join(" · ", hashes));
        }
        else
        {
            DetailRom.Visibility = Visibility.Collapsed;
        }
    }

    private async Task InspectSelectedAsync(PackHit hit)
    {
        try
        {
            await _store.InspectHitAsync(hit);
            if (!ReferenceEquals(_detailHit, hit)) return;
            BindDetailMeta(hit);
            ShowRomHackDetail(hit);
            if (TargetResolver is not null)
                hit.TargetPath = TargetResolver(hit);
            UpdateDetailStatus(hit);
            if (hit.ScreenshotUrls.Count > 0 && _shots.Count == 0)
                await LoadScreenshotsAsync(hit);
        }
        catch
        {
            // inspect is optioneel
        }
    }

    private void BindDetail(PackHit hit)
    {
        if (_detailHit is not null)
            _detailHit.PropertyChanged -= Hit_PropertyChanged;
        _detailHit = hit;
        hit.PropertyChanged += Hit_PropertyChanged;
    }

    private void Hit_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is PackHit hit &&
            e.PropertyName is nameof(PackHit.StatusText) or nameof(PackHit.StatusDetail)
                or nameof(PackHit.RemotePath) or nameof(PackHit.TargetPath) or nameof(PackHit.LocalFile)
                or nameof(PackHit.IsInstalled) or nameof(PackHit.IsDownloaded) or nameof(PackHit.IsEnabled)
                or nameof(PackHit.IsBusy) or nameof(PackHit.IsQueued)
                or nameof(PackHit.SizeText) or nameof(PackHit.CardMeta))
        {
            UpdateDetailStatus(hit);
            UpdateDetailActions(hit);
            BindDetailMeta(hit);
        }
    }

    private void UpdateDetailStatus(PackHit hit)
    {
        var text = hit.StatusDetail;
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(hit.TargetPath))
        {
            DetailStatus.Visibility = Visibility.Collapsed;
            return;
        }
        DetailStatus.Visibility = Visibility.Visible;
        DetailStatus.Text = string.IsNullOrWhiteSpace(text)
            ? "Doel: " + hit.TargetPath
            : text;
        DetailStatus.Foreground = hit.StatusKind switch
        {
            "ok" => (Brush)FindResource("Ok"),
            "err" => (Brush)FindResource("Danger"),
            "busy" => (Brush)FindResource("Accent"),
            "local" or "off" => (Brush)FindResource("Warn"),
            _ => (Brush)FindResource("Muted")
        };
        UpdateDetailActions(hit);
    }

    private void UpdateDetailActions(PackHit hit)
    {
        var canToggle = hit.IsInstalled && !hit.IsBusy && !hit.IsQueued;
        var canDelete = (hit.IsInstalled || hit.IsDownloaded) && !hit.IsBusy && !hit.IsQueued;
        EnableBtn.Visibility = canToggle && !hit.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        DisableBtn.Visibility = canToggle && hit.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        DeleteBtn.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
        DetailActions.Visibility = canToggle || canDelete ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearDetail()
    {
        if (_detailHit is not null)
            _detailHit.PropertyChanged -= Hit_PropertyChanged;
        _detailHit = null;
        DetailTitle.Text = "";
        DetailMeta.Text = "";
        DetailSummary.Text = "";
        DetailStatus.Text = "";
        DetailStatus.Visibility = Visibility.Collapsed;
        DetailActions.Visibility = Visibility.Collapsed;
        DetailRom.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        _shots.Clear();
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (ResultList.SelectedItem is not PackHit hit)
        {
            MessageBox.Show("Select a result first to download.", "Store");
            return;
        }
        if (hit.IsRomHack)
        {
            var ok = MessageBox.Show(
                PackStore.LegalHackNl + Environment.NewLine + Environment.NewLine +
                "Continue and download the patch only? The original is never overwritten; a separate ROM is made.",
                "ROM-hack", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (ok != MessageBoxResult.Yes) return;
        }
        if (!hit.CanDownload && !hit.IsRomHack)
        {
            MessageBox.Show(
                "No automatic download for this item. The page will open so you can save the file yourself.",
                "Store");
            OpenPage(hit);
            return;
        }
        InstallRequested?.Invoke(hit);
    }

    private void Enable_Click(object sender, RoutedEventArgs e)
    {
        if (ContextHit() is PackHit hit)
            ToggleRequested?.Invoke(hit, true);
    }

    private void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (ContextHit() is PackHit hit)
            ToggleRequested?.Invoke(hit, false);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ContextHit() is PackHit hit)
            DeleteRequested?.Invoke(hit);
    }

    private void ResultMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (Mouse.DirectlyOver is DependencyObject src &&
            ItemsControl.ContainerFromElement(ResultList, src) is ListViewItem item &&
            item.DataContext is PackHit hit)
            ResultList.SelectedItem = hit;

        var selected = ResultList.SelectedItem as PackHit;
        var canToggle = selected is { IsInstalled: true, IsBusy: false, IsQueued: false };
        var canDelete = selected is { IsBusy: false, IsQueued: false } &&
                        (selected.IsInstalled || selected.IsDownloaded);
        EnableMenu.Visibility = canToggle && selected is { IsEnabled: false }
            ? Visibility.Visible : Visibility.Collapsed;
        DisableMenu.Visibility = canToggle && selected is { IsEnabled: true }
            ? Visibility.Visible : Visibility.Collapsed;
        DeleteMenu.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
        if (selected is null)
            e.Handled = true;
    }

    private PackHit? ContextHit() =>
        ResultList.SelectedItem as PackHit ?? _detailHit;

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (ResultList.SelectedItem is PackHit hit)
            OpenPage(hit);
        else if (!string.IsNullOrWhiteSpace(_gameInfo?.PageUrl))
            Process.Start(new ProcessStartInfo(_gameInfo.PageUrl) { UseShellExecute = true });
    }

    private static void OpenPage(PackHit hit)
    {
        if (string.IsNullOrWhiteSpace(hit.PageUrl)) return;
        Process.Start(new ProcessStartInfo(hit.PageUrl) { UseShellExecute = true });
    }
}
