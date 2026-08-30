using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sesame.Models;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame;

public partial class GameOptimizerView : UserControl
{
    private readonly ObservableCollection<OptimizerGame> _games = new();
    private readonly ListCollectionView _view;
    private DeckClient? _client;
    private AppCatalog? _catalog;
    private bool _busy;
    private bool _scanning;
    private bool _bindingArt;
    private bool _loadingMask;
    private CancellationTokenSource? _coverCts;
    private CancellationTokenSource? _scanCts;
    private Task? _scanTask;
    private DateTime _progressStarted;

    public event Action<string>? StatusChanged;

    public int Count => _games.Count;
    public int InSteamCount => _games.Count(g => g.InSteam);
    public int SelectedCount => _games.Count(g => g.Selected);

    public GameOptimizerView()
    {
        InitializeComponent();
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_games);
        _view.Filter = FilterGame;
        GameList.ItemsSource = _view;
        ListColumns.Attach(GameList, _view, properties: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FPS"] = nameof(OptimizerGame.Fps)
        });
        SystemFilter.Items.Add("All systems");
        SystemFilter.SelectedIndex = 0;
        SearchBox.TextChanged += (_, _) => _view.Refresh();
        _loadingMask = true;
        MaskBox.IsChecked = OptimizerSettings.UseMasks;
        _loadingMask = false;
        UpdateHint();
    }

    public void Attach(DeckClient client, AppCatalog catalog)
    {
        _client = client;
        _catalog = catalog;
    }

    public void OnConnected()
    {
        OptimizerPicks.CurrentKey = CacheKey();
        CancelBackgroundScan();
        ClearGames();
        HintText.Text = "Loading cache…";
        var key = CacheKey();
        var host = CacheHost();
        var client = _client;
        _ = Task.Run(() =>
        {
            var cached = OptimizerLibraryCache.Load(key, host);
            Dispatcher.BeginInvoke(() =>
            {
                if (!string.Equals(CacheKey(), key, StringComparison.OrdinalIgnoreCase)) return;
                if (cached.Count == 0)
                {
                    HintText.Text = "Scan to load ROMs, Hydra games and apps from this Deck.";
                    StatusChanged?.Invoke("Optimize: not scanned yet");
                    return;
                }
                ApplyScanResults(cached, prefetch: true);
                HintText.Text = $"{_games.Count} games from cache. Scan to refresh from the Deck.";
                StatusChanged?.Invoke($"Optimize: {_games.Count} games (cache)");
                if (client is not { IsConnected: true }) return;
                var snapshot = _games.ToList();
                _ = Task.Run(() =>
                {
                    try { SteamGridArt.AttachAll(client, snapshot); }
                    catch { /* covers are optional */ }
                    Dispatcher.BeginInvoke(() => _ = PrefetchCoversAsync());
                });
            });
        });
    }

    public void LoadCachedLibrary()
    {
        OnConnected();
    }

    public void StartBackgroundScan() => _ = ScanLibraryAsync();

    public void CancelBackgroundScan() => _scanCts?.Cancel();

    public void OnSettingsClosed(bool keyChanged, bool launchersChanged)
    {
        UpdateHint();
        _loadingMask = true;
        MaskBox.IsChecked = OptimizerSettings.UseMasks;
        _loadingMask = false;
        RefreshMasks();
        if (keyChanged)
        {
            foreach (var game in _games)
            {
                if (string.IsNullOrEmpty(game.SelectedGridUrl))
                {
                    game.Cover = null;
                    game.CoverWide = null;
                }
            }
            if (GameList.SelectedItem is OptimizerGame selected)
                ShowGame(selected);
            _ = PrefetchCoversAsync();
        }
        if (launchersChanged)
            HintText.Text = "Emulator settings changed. Scan again to refresh launchers.";
    }

    private void UpdateHint()
    {
        HintText.Text = OptimizerSettings.HasSteamGridDb
            ? "Connect, then Scan. SESAME writes its own shortcuts only; Hydra and Steam ROM Manager stay."
            : "Set a SteamGridDB key in Settings to get covers.";
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanLibraryAsync();

    public Task ScanLibraryAsync(bool overlay = true)
    {
        if (_busy) return Task.CompletedTask;
        if (_scanning && _scanTask is { IsCompleted: false }) return _scanTask;
        _scanTask = ScanLibraryCoreAsync(overlay);
        return _scanTask;
    }

    private async Task ScanLibraryCoreAsync(bool overlay)
    {
        if (_client is not { IsConnected: true } || _catalog is null) return;
        if (_busy || _scanning) return;
        _scanning = true;
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        if (overlay)
        {
            ShowProgress(new OptimizeProgress
            {
                Title = "Scanning library",
                Detail = "Reading ROMs, Hydra games and apps on the Deck…",
                Indeterminate = true
            });
        }
        HintText.Text = "Scanning library…";
        StatusChanged?.Invoke("Optimize: scanning…");
        try
        {
            var catalog = _catalog;
            var client = _client;
            var scanKey = CacheKey();
            OptimizerPicks.CurrentKey = scanKey;
            var progress = overlay
                ? new Progress<OptimizeProgress>(p => Dispatcher.Invoke(() => ShowProgress(p)))
                : new Progress<OptimizeProgress>(p => Dispatcher.Invoke(() =>
                    StatusChanged?.Invoke(string.IsNullOrEmpty(p.Detail) ? p.Title : p.Title + " — " + p.Detail)));
            var games = await Task.Run(() => GameOptimizerService.Scan(client, catalog, progress), cts.Token);
            if (cts.IsCancellationRequested) return;
            if (!string.Equals(CacheKey(), scanKey, StringComparison.OrdinalIgnoreCase)) return;
            ApplyScanResults(games, prefetch: true);
            Persist();
            var ready = games.Count(g => !string.IsNullOrEmpty(g.Target));
            HintText.Text = $"{games.Count} games found, {ready} with a launcher. Changes are saved.";
            StatusChanged?.Invoke($"Optimize: {games.Count} games");
        }
        catch (OperationCanceledException)
        {
            /* disconnected */
        }
        catch (Exception ex)
        {
            HintText.Text = "Scan failed: " + ex.Message;
            StatusChanged?.Invoke("Optimize: scan failed");
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                _scanning = false;
                if (overlay) HideProgress();
            }
        }
    }

    private void ApplyScanResults(IReadOnlyList<OptimizerGame> games, bool prefetch)
    {
        var previous = _games.ToList();
        var previousByKey = previous
            .GroupBy(ExtraShortcuts.KeyOf, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var selectedKey = GameList.SelectedItem is OptimizerGame selected
            ? ExtraShortcuts.KeyOf(selected)
            : "";
        var filter = SystemFilter.SelectedItem as string;
        foreach (var old in _games)
            old.PropertyChanged -= Game_PropertyChanged;
        _games.Clear();
        SystemFilter.Items.Clear();
        SystemFilter.Items.Add("All systems");
        foreach (var system in games.Select(g => g.SystemName).Distinct().OrderBy(s => s))
            SystemFilter.Items.Add(system);
        if (filter is not null && SystemFilter.Items.Contains(filter))
            SystemFilter.SelectedItem = filter;
        else
            SystemFilter.SelectedIndex = 0;
        foreach (var game in games)
        {
            OptimizerPicks.Apply(game);
            if (previousByKey.TryGetValue(ExtraShortcuts.KeyOf(game), out var olds))
            {
                ExtraShortcuts.UnionChoices(game, olds);
                var old = olds[0];
                // In-session checkbox wins over disk when both exist.
                game.Selected = old.Selected;
                CopyPreview(old, game);
            }
            game.PropertyChanged += Game_PropertyChanged;
            _games.Add(game);
        }
        _view.Refresh();
        if (selectedKey.Length > 0)
        {
            var match = _games.FirstOrDefault(g =>
                string.Equals(ExtraShortcuts.KeyOf(g), selectedKey, StringComparison.OrdinalIgnoreCase));
            if (match is not null) GameList.SelectedItem = match;
            else if (_games.Count > 0) GameList.SelectedIndex = 0;
        }
        else if (_games.Count > 0 && GameList.SelectedItem is null)
            GameList.SelectedIndex = 0;
        if (prefetch)
            _ = PrefetchCoversAsync();
    }

    private static void CopyPreview(OptimizerGame from, OptimizerGame to)
    {
        if (from.Cover is null) return;
        if (to.SelectedGridUrl is not null && from.SelectedGridUrl is not null &&
            !string.Equals(from.SelectedGridUrl, to.SelectedGridUrl, StringComparison.Ordinal))
            return;
        to.Cover = from.Cover;
        to.CoverWide = from.CoverWide;
        to.Hero = from.Hero;
        to.Logo = from.Logo;
        to.Icon = from.Icon;
        to.GridBytes ??= from.GridBytes;
        to.WideBytes ??= from.WideBytes;
        to.HeroBytes ??= from.HeroBytes;
        to.LogoBytes ??= from.LogoBytes;
        to.IconBytes ??= from.IconBytes;
        to.ArtworkChoices.Clear();
        to.ArtworkChoices.AddRange(from.ArtworkChoices);
    }

    private async void OptimizeSelected_Click(object sender, RoutedEventArgs e) =>
        await RunOptimizeInteractiveAsync();

    public async Task RunOptimizeInteractiveAsync(bool selectAllIfEmpty = false)
    {
        if (selectAllIfEmpty && !_games.Any(g => g.Selected))
        {
            foreach (var game in _games)
                game.Selected = true;
        }

        var selected = _games.Where(g => g.Selected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Select at least one game.", "Optimize");
            return;
        }
        if (_client is not { IsConnected: true })
        {
            MessageBox.Show("Connect to the Steam Deck first.", "Optimize");
            return;
        }

        // Do not Detect() on the UI thread here — that used to freeze for many seconds
        // before the options dialog (or progress UI) appeared.
        const string steamNote =
            "Steam is always paused briefly so shortcuts, artwork and collections can be written. " +
            "If the Deck is in Game Mode, SESAME switches to Desktop Mode, writes, then turns Game Mode back on.";

        var dlg = new OptimizeOptionsWindow { Owner = Window.GetWindow(this) };
        dlg.Bind(selected.Count,
            OptimizerSettings.OverwriteShortcuts,
            OptimizerSettings.OverwriteArtwork,
            OptimizerSettings.UseMasks,
            steamNote,
            selected.Any(DolphinInput.UsesDolphin) ? DolphinInput.DutchGyroHint : null);
        if (dlg.ShowDialog() != true) return;

        OptimizerSettings.OverwriteShortcuts = dlg.OverwriteShortcuts;
        OptimizerSettings.OverwriteArtwork = dlg.OverwriteArtwork;
        OptimizerSettings.UseMasks = dlg.UseMasks;
        OptimizerSettings.Save();
        _loadingMask = true;
        MaskBox.IsChecked = OptimizerSettings.UseMasks;
        _loadingMask = false;
        RefreshMasks();
        await OptimizeAsync(_games.ToList());
    }

    private void Mask_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingMask) return;
        OptimizerSettings.UseMasks = MaskBox.IsChecked == true;
        OptimizerSettings.Save();
        RefreshMasks();
    }

    private void RefreshMasks()
    {
        foreach (var game in _games)
        {
            if (game.GridBytes is null && game.WideBytes is null) continue;
            var profile = ProfileOf(game);
            if (profile is not null)
                ApplyPreview(game, profile);
        }
    }

    private async Task OptimizeAsync(IReadOnlyList<OptimizerGame> games)
    {
        if (_client is not { IsConnected: true } || _catalog is null)
        {
            MessageBox.Show("Connect to the Steam Deck first.", "Optimize");
            return;
        }
        if (_busy) return;
        if (!_games.Any(g => g.Selected))
        {
            MessageBox.Show("Select at least one game.", "Optimize");
            return;
        }

        if (_scanTask is { IsCompleted: false })
            await _scanTask;

        _busy = true;
        var catalog = _catalog;
        var client = _client;
        var progress = new Progress<OptimizeProgress>(p =>
        {
            ShowProgress(p);
            StatusChanged?.Invoke(string.IsNullOrEmpty(p.Detail) ? p.Title : p.Title + " — " + p.Detail);
        });
        ShowProgress(new OptimizeProgress
        {
            Title = "Prepare Steam",
            Detail = "Starting… checking Deck session and pausing Steam.",
            Indeterminate = true
        });
        OptimizeReport? report = null;
        try
        {
            report = await Task.Run(() =>
                GameOptimizerService.ApplyAsync(client, catalog, games, progress, CancellationToken.None));
            HintText.Text = report.Summary;
            StatusChanged?.Invoke(report.Summary);
            Persist();
            ShowGame(GameList.SelectedItem as OptimizerGame);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Optimize");
        }
        finally
        {
            _busy = false;
            HideProgress();
        }
        if (report is { Errors.Count: > 0 })
            MessageBox.Show(string.Join(Environment.NewLine, report.Errors.Take(8)), "Optimize");
    }

    private void ShowProgress(OptimizeProgress p)
    {
        if (ProgressOverlay.Visibility != Visibility.Visible)
        {
            ProgressOverlay.Visibility = Visibility.Visible;
            _progressStarted = DateTime.UtcNow;
        }
        OverlayTitle.Text = string.IsNullOrWhiteSpace(p.Title) ? "Working…" : p.Title;
        OverlayDetail.Text = p.Detail ?? "";
        OverlayCount.Text = p.Total > 0
            ? (p.Current > 0 ? $"Game {p.Current}/{p.Total}" : $"{p.Total} games")
            : "";
        OverlayCount.Visibility = string.IsNullOrEmpty(OverlayCount.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        if (p.Indeterminate)
        {
            OverlayBar.IsIndeterminate = true;
            OverlayPercent.Text = "";
            OverlayEta.Text = "";
            return;
        }
        OverlayBar.IsIndeterminate = false;
        var pct = Math.Clamp(p.Percent, 0, 100);
        OverlayBar.Value = pct;
        OverlayPercent.Text = Math.Round(pct).ToString("0") + "%";
        OverlayEta.Text = EtaText(pct);
    }

    private void HideProgress()
    {
        OverlayBar.IsIndeterminate = false;
        ProgressOverlay.Visibility = Visibility.Collapsed;
    }

    private string EtaText(double percent)
    {
        if (percent < 8) return "";
        var elapsed = DateTime.UtcNow - _progressStarted;
        if (elapsed.TotalSeconds < 3) return "";
        var remain = TimeSpan.FromSeconds(elapsed.TotalSeconds * (100 - percent) / percent);
        if (remain.TotalSeconds < 8) return "Almost done…";
        if (remain.TotalHours >= 1)
            return $"About {remain.Hours} h {remain.Minutes} min left";
        if (remain.TotalMinutes >= 1.5)
            return $"About {Math.Ceiling(remain.TotalMinutes)} min left";
        return $"About {Math.Max(10, Math.Ceiling(remain.TotalSeconds / 5) * 5)} s left";
    }

    public void UpsertManual(OptimizerGame game)
    {
        var key = ExtraShortcuts.KeyOf(game);
        var existing = _games.FirstOrDefault(g =>
            string.Equals(ExtraShortcuts.KeyOf(g), key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ExtraShortcuts.UnionChoices(game, [existing]);
            game.Selected = existing.Selected;
            CopyPreview(existing, game);
            existing.PropertyChanged -= Game_PropertyChanged;
            var i = _games.IndexOf(existing);
            game.PropertyChanged += Game_PropertyChanged;
            _games[i] = game;
        }
        else
        {
            game.PropertyChanged += Game_PropertyChanged;
            _games.Add(game);
            if (!SystemFilter.Items.Contains(game.SystemName))
                SystemFilter.Items.Add(game.SystemName);
        }
        Persist();
        _view.Refresh();
    }

    public void RemoveManual(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var hit = _games.FirstOrDefault(g => g.ManualId == id);
        if (hit is null) return;
        hit.PropertyChanged -= Game_PropertyChanged;
        _games.Remove(hit);
        Persist();
        _view.Refresh();
    }

    private void PickLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not OptimizerGame game)
        {
            MessageBox.Show(Window.GetWindow(this), "Select a row first.", "Choose launch");
            return;
        }

        if (game.LaunchChoices.Count == 0)
            game.LaunchChoices.Add(new LaunchChoice
            {
                Exe = game.Target,
                StartDir = game.StartDir,
                Options = game.LaunchOptions,
                RomPath = game.RomPath
            });
        var win = new PickLaunchWindow(game) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true || win.Chosen is null) return;
        ExtraShortcuts.ApplyLaunch(game, win.Chosen);
        Persist();
        ShowGame(game);
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => _view.Refresh();

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _view.Refresh();
    }

    private bool FilterGame(object obj)
    {
        if (obj is not OptimizerGame game) return false;
        if (SystemFilter.SelectedItem is string system &&
            system != "All systems" &&
            !string.Equals(game.SystemName, system, StringComparison.OrdinalIgnoreCase))
            return false;
        var q = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return game.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               game.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               game.SystemName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               game.KindText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               game.TagsText.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Game_Changed(object sender, SelectionChangedEventArgs e) =>
        ShowGame(GameList.SelectedItem as OptimizerGame);

    private void Name_LostFocus(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not OptimizerGame game) return;
        var name = NameBox.Text?.Trim();
        if (!string.IsNullOrEmpty(name))
            game.DisplayName = name;
        game.SearchQuery = QueryBox.Text?.Trim() ?? game.SearchQuery;
        Remember(game);
    }

    private async void ShowGame(OptimizerGame? game)
    {
        _coverCts?.Cancel();
        CoverImage.Source = null;
        WideImage.Source = null;
        HeroImage.Source = null;
        LogoImage.Source = null;
        IconImage.Source = null;
        if (game is null)
        {
            NameBox.Text = "";
            QueryBox.Text = "";
            MetaText.Text = "";
            NoteText.Text = "";
            ClearArtLists();
            return;
        }

        NameBox.Text = game.DisplayName;
        QueryBox.Text = string.IsNullOrWhiteSpace(game.SearchQuery) ? game.DisplayName : game.SearchQuery;
        MetaText.Text = string.IsNullOrEmpty(game.Target)
            ? $"{game.SystemName} · {game.FpsText} · geen emulator"
            : $"{game.SystemName} · {game.FpsText} · {game.EmulatorName}\n{game.Target}";
        NoteText.Text = game.Note;

        var cts = new CancellationTokenSource();
        _coverCts = cts;
        if (game.Cover is not null)
        {
            CoverImage.Source = game.Cover as ImageSource;
            WideImage.Source = game.CoverWide as ImageSource;
            HeroImage.Source = game.Hero as ImageSource;
            LogoImage.Source = game.Logo as ImageSource;
            IconImage.Source = game.Icon as ImageSource;
            BindArtTabs(game);
            if (game.ArtworkChoices.Count == 0)
                _ = LoadChoicesAsync(game, cts.Token);
            if (game.Hero is null)
                _ = LoadExtrasAsync(game, cts.Token);
            return;
        }

        await LoadCoverAsync(game, cts.Token, extras: true);
        if (!cts.IsCancellationRequested && ReferenceEquals(GameList.SelectedItem, game))
            BindArtTabs(game);
        _ = LoadChoicesAsync(game, cts.Token);
    }

    private async Task PrefetchCoversAsync()
    {
        var games = _games.ToList();
        var needsCover = games.Any(g =>
            g.Cover is null &&
            (g.GridBytes is { Length: > 0 } ||
             g.SteamAppId != 0 ||
             !string.IsNullOrEmpty(g.SelectedGridUrl) ||
             OptimizerSettings.HasSteamGridDb));
        if (!needsCover) return;
        using var gate = new SemaphoreSlim(3);
        await Task.WhenAll(games.Select(async game =>
        {
            await gate.WaitAsync();
            try
            {
                if (game.Cover is not null) return;
                await LoadCoverAsync(game, CancellationToken.None, extras: false);
            }
            catch
            {
                /* cover is optioneel */
            }
            finally
            {
                gate.Release();
            }
        }));
        Persist();
    }

    private async Task LoadCoverAsync(OptimizerGame game, CancellationToken ct, bool extras)
    {
        var profile = ProfileOf(game);
        if (profile is null) return;
        // Apps always search by catalog title — never a polluted SearchQuery / rename.
        var query = ArtworkClient.ArtworkSearchQuery(game);
        byte[]? grid = game.GridBytes;
        byte[]? wide = game.WideBytes;
        if (grid is null && game.SteamAppId != 0 && _client is { IsConnected: true })
        {
            try { grid = SteamGridArt.ReadPortrait(_client, game.SteamAppId); }
            catch { /* cover is optioneel */ }
        }
        if (grid is null && !string.IsNullOrEmpty(game.SelectedGridUrl))
            grid = await ArtworkClient.DownloadAsync(game.SelectedGridUrl, ct);
        if (wide is null && !string.IsNullOrEmpty(game.SelectedWideUrl))
            wide = await ArtworkClient.DownloadAsync(game.SelectedWideUrl, ct);
        if (grid is null)
        {
            var art = await ArtworkClient.FindAsync(query, profile, ct);
            if (art?.GameId is int id) game.SteamGridDbId = id;
            if (!string.IsNullOrEmpty(ArtworkClient.LastError) && art is null)
                game.Note = ArtworkClient.LastError;
            if (grid is null && art?.GridUrl is not null)
                grid = await ArtworkClient.DownloadAsync(art.GridUrl, ct);
            if (wide is null && art?.WideUrl is not null)
                wide = await ArtworkClient.DownloadAsync(art.WideUrl, ct);
            game.SelectedGridUrl ??= art?.GridUrl;
            game.SelectedWideUrl ??= art?.WideUrl;
            game.SelectedHeroUrl ??= art?.HeroUrl;
            game.SelectedLogoUrl ??= art?.LogoUrl;
            game.SelectedIconUrl ??= art?.IconUrl;
            game.ArtworkSource = art?.Source ?? game.ArtworkSource;
        }
        if (ct.IsCancellationRequested) return;
        game.GridBytes = grid ?? game.GridBytes;
        game.WideBytes = wide ?? game.WideBytes;
        ApplyPreview(game, profile);
        if (extras)
            await LoadExtrasAsync(game, ct);
    }

    private async Task LoadExtrasAsync(OptimizerGame game, CancellationToken ct)
    {
        if (game.HeroBytes is null && !string.IsNullOrEmpty(game.SelectedHeroUrl))
            game.HeroBytes = await ArtworkClient.DownloadAsync(game.SelectedHeroUrl, ct);
        if (game.LogoBytes is null && !string.IsNullOrEmpty(game.SelectedLogoUrl))
            game.LogoBytes = await ArtworkClient.DownloadAsync(game.SelectedLogoUrl, ct);
        if (game.IconBytes is null && !string.IsNullOrEmpty(game.SelectedIconUrl))
            game.IconBytes = await ArtworkClient.DownloadAsync(game.SelectedIconUrl, ct);
        if (game.SteamGridDbId is int gid &&
            (game.SelectedHeroUrl is null || game.SelectedLogoUrl is null || game.SelectedIconUrl is null))
        {
            var extra = new ArtworkSet
            {
                HeroUrl = game.SelectedHeroUrl,
                LogoUrl = game.SelectedLogoUrl,
                IconUrl = game.SelectedIconUrl
            };
            await ArtworkClient.EnsureExtraAssetsAsync(extra, gid, ct);
            game.SelectedHeroUrl ??= extra.HeroUrl;
            game.SelectedLogoUrl ??= extra.LogoUrl;
            game.SelectedIconUrl ??= extra.IconUrl;
            game.HeroBytes ??= await ArtworkClient.DownloadAsync(game.SelectedHeroUrl, ct);
            game.LogoBytes ??= await ArtworkClient.DownloadAsync(game.SelectedLogoUrl, ct);
            game.IconBytes ??= await ArtworkClient.DownloadAsync(game.SelectedIconUrl, ct);
        }
        if (ct.IsCancellationRequested) return;
        if (game.HeroBytes is { Length: > 0 }) game.Hero = ToBitmap(game.HeroBytes);
        if (game.LogoBytes is { Length: > 0 }) game.Logo = ToBitmap(game.LogoBytes);
        if (game.IconBytes is { Length: > 0 }) game.Icon = ToBitmap(game.IconBytes);
        Dispatcher.Invoke(() =>
        {
            if (!ReferenceEquals(GameList.SelectedItem, game)) return;
            HeroImage.Source = game.Hero as ImageSource;
            LogoImage.Source = game.Logo as ImageSource;
            IconImage.Source = game.Icon as ImageSource;
        });
    }

    private void ApplyPreview(OptimizerGame game, SystemProfile profile)
    {
        // Unmasked platforms (Hydra/apps): show the chosen grid as-is so the preview
        // matches the thumbnail — CoverMask fill was cropping titles off.
        byte[] portrait;
        byte[] landscape;
        if (!OptimizerSettings.UseMaskFor(profile.Id) && game.GridBytes is { Length: > 0 })
        {
            portrait = CoverMask.FitOnlyPublic(game.GridBytes, CoverMask.PortraitWidth, CoverMask.PortraitHeight);
            landscape = CoverMask.FitOnlyPublic(
                game.WideBytes ?? game.GridBytes, CoverMask.LandscapeWidth, CoverMask.LandscapeHeight);
        }
        else
        {
            portrait = CoverMask.Portrait(game.GridBytes ?? game.WideBytes, profile, game.IsRomHack, game.IsTranslation);
            landscape = CoverMask.Landscape(game.WideBytes ?? game.GridBytes, profile, game.IsRomHack, game.IsTranslation);
        }
        var portraitBmp = ToBitmap(portrait);
        var wideBmp = ToBitmap(landscape);
        game.Cover = portraitBmp;
        game.CoverWide = wideBmp;
        game.HasArtwork = game.GridBytes is { Length: > 0 } || game.WideBytes is { Length: > 0 };
        if (game.HasArtwork && OptimizerSettings.UseMaskFor(profile.Id) &&
            !game.ArtworkSource.Contains("mask", StringComparison.OrdinalIgnoreCase))
            game.ArtworkSource = (string.IsNullOrEmpty(game.ArtworkSource) || game.ArtworkSource == "—"
                ? "SteamGridDB" : game.ArtworkSource) + " + mask";
        Dispatcher.Invoke(() =>
        {
            if (!ReferenceEquals(GameList.SelectedItem, game)) return;
            CoverImage.Source = portraitBmp;
            WideImage.Source = wideBmp;
            HeroImage.Source = game.Hero as ImageSource;
            LogoImage.Source = game.Logo as ImageSource;
            IconImage.Source = game.Icon as ImageSource;
            NoteText.Text = game.Note;
        });
    }

    private async void SearchArt_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not OptimizerGame game) return;
        game.SearchQuery = QueryBox.Text?.Trim() ?? game.DisplayName;
        game.SteamGridDbId = null;
        game.SelectedGridUrl = null;
        game.SelectedWideUrl = null;
        game.SelectedHeroUrl = null;
        game.SelectedLogoUrl = null;
        game.SelectedIconUrl = null;
        game.GridBytes = null;
        game.WideBytes = null;
        game.HeroBytes = null;
        game.LogoBytes = null;
        game.IconBytes = null;
        game.Cover = null;
        game.Hero = null;
        game.Logo = null;
        game.Icon = null;
        game.ArtworkChoices.Clear();
        ClearArtLists();
        await LoadCoverAsync(game, CancellationToken.None, extras: true);
        await LoadChoicesAsync(game, CancellationToken.None);
        Remember(game);
    }

    private async Task LoadChoicesAsync(OptimizerGame game, CancellationToken ct)
    {
        var profile = ProfileOf(game);
        if (profile is null || !OptimizerSettings.HasSteamGridDb) return;
        try
        {
            var id = game.SteamGridDbId ??
                     await ArtworkClient.FindGameIdAsync(
                         string.IsNullOrWhiteSpace(game.SearchQuery) ? game.DisplayName : game.SearchQuery,
                         profile, ct);
            if (id is null || ct.IsCancellationRequested) return;
            game.SteamGridDbId = id;
            var choices = await ArtworkClient.ListAllAsync(id.Value, ct);
            if (ct.IsCancellationRequested) return;
            game.ArtworkChoices.Clear();
            game.ArtworkChoices.AddRange(choices.Take(80));
            Dispatcher.Invoke(() => BindArtTabs(game));
            foreach (var choice in game.ArtworkChoices)
            {
                var bytes = await ArtworkClient.DownloadAsync(choice.ThumbUrl, ct);
                if (bytes is null || ct.IsCancellationRequested) continue;
                var bmp = ToBitmap(bytes);
                choice.Preview = bmp;
            }
        }
        catch (OperationCanceledException)
        {
            /* volgende selectie */
        }
        catch
        {
            /* galerij is optioneel */
        }
    }

    private void BindArtTabs(OptimizerGame game)
    {
        _bindingArt = true;
        CoverList.ItemsSource = game.ArtworkChoices.Where(c => c.Kind == "cover").ToList();
        WideList.ItemsSource = game.ArtworkChoices.Where(c => c.Kind == "wide").ToList();
        HeroList.ItemsSource = game.ArtworkChoices.Where(c => c.Kind == "hero").ToList();
        LogoList.ItemsSource = game.ArtworkChoices.Where(c => c.Kind == "logo").ToList();
        IconList.ItemsSource = game.ArtworkChoices.Where(c => c.Kind == "icon").ToList();
        SetList.ItemsSource = ArtworkPacks.Build(game.ArtworkChoices);
        SelectUrl(CoverList, game.SelectedGridUrl);
        SelectUrl(WideList, game.SelectedWideUrl);
        SelectUrl(HeroList, game.SelectedHeroUrl);
        SelectUrl(LogoList, game.SelectedLogoUrl);
        SelectUrl(IconList, game.SelectedIconUrl);
        _bindingArt = false;
    }

    private static void SelectUrl(ListBox list, string? url)
    {
        if (string.IsNullOrEmpty(url) || list.ItemsSource is not IEnumerable<ArtworkChoice> items)
            return;
        list.SelectedItem = items.FirstOrDefault(c => c.Url == url);
    }

    private void ClearArtLists()
    {
        _bindingArt = true;
        CoverList.ItemsSource = null;
        WideList.ItemsSource = null;
        HeroList.ItemsSource = null;
        LogoList.ItemsSource = null;
        IconList.ItemsSource = null;
        SetList.ItemsSource = null;
        _bindingArt = false;
    }

    private async void UseSet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not ArtworkPack pack) return;
        await ApplyPackAsync(pack);
    }

    private async void Set_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingArt) return;
        if (SetList.SelectedItem is not ArtworkPack pack) return;
        await ApplyPackAsync(pack);
    }

    private async Task ApplyPackAsync(ArtworkPack pack)
    {
        if (GameList.SelectedItem is not OptimizerGame game) return;
        foreach (var piece in pack.Pieces)
            await ApplyChoiceAsync(game, piece);
    }

    private async void Art_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingArt) return;
        if (sender is not ListBox box || box.SelectedItem is not ArtworkChoice choice) return;
        if (GameList.SelectedItem is not OptimizerGame game) return;
        await ApplyChoiceAsync(game, choice);
    }

    private async Task ApplyChoiceAsync(OptimizerGame game, ArtworkChoice choice)
    {
        var bytes = await ArtworkClient.DownloadAsync(choice.Url, CancellationToken.None);
        switch (choice.Kind)
        {
            case "hero":
                if (string.Equals(game.SelectedHeroUrl, choice.Url, StringComparison.Ordinal)) return;
                game.SelectedHeroUrl = choice.Url;
                game.HeroBytes = bytes;
                game.Hero = bytes is { Length: > 0 } ? ToBitmap(bytes) : game.Hero;
                HeroImage.Source = game.Hero as ImageSource;
                Remember(game);
                return;
            case "logo":
                if (string.Equals(game.SelectedLogoUrl, choice.Url, StringComparison.Ordinal)) return;
                game.SelectedLogoUrl = choice.Url;
                game.LogoBytes = bytes;
                game.Logo = bytes is { Length: > 0 } ? ToBitmap(bytes) : game.Logo;
                LogoImage.Source = game.Logo as ImageSource;
                Remember(game);
                return;
            case "icon":
                if (string.Equals(game.SelectedIconUrl, choice.Url, StringComparison.Ordinal)) return;
                game.SelectedIconUrl = choice.Url;
                game.IconBytes = bytes;
                game.Icon = bytes is { Length: > 0 } ? ToBitmap(bytes) : game.Icon;
                IconImage.Source = game.Icon as ImageSource;
                Remember(game);
                return;
            case "wide":
                if (string.Equals(game.SelectedWideUrl, choice.Url, StringComparison.Ordinal)) return;
                game.SelectedWideUrl = choice.Url;
                game.WideBytes = bytes;
                break;
            default:
                if (string.Equals(game.SelectedGridUrl, choice.Url, StringComparison.Ordinal)) return;
                game.SelectedGridUrl = choice.Url;
                game.GridBytes = bytes;
                break;
        }
        var profile = ProfileOf(game);
        if (profile is not null)
            ApplyPreview(game, profile);
        Remember(game);
    }

    private void ArtPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ListBox box) return;
        if (FindChild<WrapPanel>(box) is { } wrap)
            wrap.Width = Math.Max(80, box.ActualWidth - 16);
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void Game_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OptimizerGame.Selected) or nameof(OptimizerGame.DisplayName))
        {
            if (sender is OptimizerGame game)
                OptimizerPicks.Remember(game);
            Persist();
        }
    }

    private void Remember(OptimizerGame game)
    {
        OptimizerPicks.Remember(game);
        Persist();
    }

    private void Persist()
    {
        OptimizerPicks.RememberAll(_games);
        OptimizerLibraryCache.Save(CacheKey(), CacheHost(), _games);
    }

    private void ClearGames()
    {
        foreach (var old in _games)
            old.PropertyChanged -= Game_PropertyChanged;
        _games.Clear();
        SystemFilter.Items.Clear();
        SystemFilter.Items.Add("All systems");
        SystemFilter.SelectedIndex = 0;
    }

    private string CacheKey() => _client?.ActiveProfile?.Id ?? "";

    private string CacheHost() => _client?.ActiveProfile?.Host ?? "";

    private static SystemProfile ProfileOf(OptimizerGame game) =>
        SystemCatalog.FromFolder(game.FolderName) ??
        SystemCatalog.Extra.FirstOrDefault(p =>
            p.Id.Equals(game.SystemId, StringComparison.OrdinalIgnoreCase)) ??
        SystemCatalog.All.FirstOrDefault(p => p.Id == game.SystemId) ??
        SystemCatalog.Unknown(game.FolderName);

    private static BitmapImage ToBitmap(byte[] png)
    {
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(png);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
