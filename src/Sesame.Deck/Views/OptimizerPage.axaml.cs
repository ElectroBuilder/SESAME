using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sesame.Models;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck.Views;

public partial class OptimizerPage : UserControl
{
    private readonly ObservableCollection<OptimizerGame> _games = new();
    private bool _busy;
    private bool _compact;

    public event Action<string>? StatusChanged;

    public int Count => _games.Count;
    public int InSteamCount => _games.Count(g => g.InSteam);
    public int SelectedCount => _games.Count(g => g.Selected);

    public OptimizerPage()
    {
        InitializeComponent();
        GameList.ItemsSource = _games;
        TileList.ItemsSource = _games;
    }

    public void SetCompact(bool compact)
    {
        _compact = compact;
        GameList.IsVisible = !compact;
        TileHost.IsVisible = compact;
    }

    public void OnConnected()
    {
        var session = DeckSession.Current;
        if (!session.Connected) return;
        var id = session.Client.ActiveProfile?.Id ?? "local";
        var host = session.Client.ActiveProfile?.Host ?? "local";
        OptimizerPicks.CurrentKey = host;
        var cached = OptimizerLibraryCache.Load(id, host);
        if (cached.Count == 0)
        {
            Hint.Text = "Scan to load ROMs, Hydra games and apps from this Deck.";
            StatusChanged?.Invoke("Optimize: not scanned yet");
            return;
        }

        var client = session.Client;
        _ = Task.Run(() =>
        {
            try { SteamGridArt.AttachAll(client, cached); }
            catch { /* covers are optional */ }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Replace(cached);
                Hint.Text = cached.Count + " games from cache, including artwork already in Steam.";
                StatusChanged?.Invoke($"Optimize: {cached.Count} games (cache)");
                _ = DeckCovers.PrefetchAsync(_games.ToList(), CancellationToken.None);
            });
        });
    }

    public void UpsertManual(OptimizerGame game)
    {
        var existing = _games.FirstOrDefault(g =>
            string.Equals(ExtraShortcuts.KeyOf(g), ExtraShortcuts.KeyOf(game), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var i = _games.IndexOf(existing);
            _games[i] = game;
        }
        else
            _games.Add(game);
        Persist();
    }

    public void RemoveManual(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var hit = _games.FirstOrDefault(g => g.ManualId == id);
        if (hit is not null) _games.Remove(hit);
        Persist();
    }

    private async void Scan_Click(object? sender, RoutedEventArgs e) => await ScanLibraryAsync();

    public async Task ScanLibraryAsync(bool overlay = true)
    {
        if (_busy) return;
        var session = DeckSession.Current;
        if (!session.Connected)
        {
            Hint.Text = "Connect first.";
            return;
        }
        _busy = true;
        if (overlay)
            ShowBusy("Scanning library", "ROMs, Hydra games and apps…");
        StatusChanged?.Invoke("Optimize: scanning…");
        try
        {
            var progress = new Progress<OptimizeProgress>(p =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (overlay) ShowBusy(p.Title, p.Detail);
                    else StatusChanged?.Invoke(p.Detail);
                }));
            OptimizerPicks.CurrentKey = session.Client.ActiveProfile?.Host ?? "local";
            var games = await Task.Run(() =>
            {
                var found = GameOptimizerService.Scan(session.Client, session.Catalog, progress);
                SteamGridArt.AttachAll(session.Client, found);
                return found;
            });
            Replace(games);
            Persist();
            Hint.Text = games.Count + " items. Existing Steam covers are shown; Optimize writes SESAME shortcuts.";
            StatusChanged?.Invoke($"Optimize: {games.Count}");
            _ = DeckCovers.PrefetchAsync(_games.ToList(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Hint.Text = ex.Message;
            StatusChanged?.Invoke("Optimize: scan failed");
        }
        finally
        {
            _busy = false;
            if (overlay) HideBusy();
        }
    }

    private async void Apply_Click(object? sender, RoutedEventArgs e) =>
        await RunOptimizeInteractiveAsync();

    public async Task RunOptimizeInteractiveAsync(bool selectAllIfEmpty = false)
    {
        if (_busy) return;
        var session = DeckSession.Current;
        if (!session.Connected)
        {
            Hint.Text = "Connect first.";
            return;
        }

        if (selectAllIfEmpty && !_games.Any(g => g.Selected))
        {
            foreach (var g in _games)
                g.Selected = true;
        }

        if (!_games.Any(g => g.Selected))
        {
            Hint.Text = "Select at least one game.";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is not null)
        {
            var count = _games.Count(g => g.Selected);
            var ok = await ConfirmWindow.Ask(owner, "Optimize",
                "Write Steam shortcuts, artwork and collections for " + count +
                " selected title(s)? Steam may pause briefly while SESAME writes.");
            if (!ok) return;
        }

        _busy = true;
        ShowBusy("Optimize", "Writing shortcuts and covers…");
        try
        {
            var report = await GameOptimizerService.ApplyAsync(
                session.Client, session.Catalog, _games,
                new Progress<OptimizeProgress>(p =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowBusy(p.Title, p.Detail))),
                CancellationToken.None);
            await Task.Run(() => SteamGridArt.AttachAll(session.Client, _games));
            foreach (var game in _games)
                DeckCovers.ApplyBytes(game);
            Hint.Text = report.Summary;
            StatusChanged?.Invoke(report.Summary);
            Persist();
        }
        catch (Exception ex)
        {
            Hint.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            HideBusy();
        }
    }

    private async void Pick_Click(object? sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not OptimizerGame game) return;
        if (game.LaunchChoices.Count == 0)
            ExtraShortcuts.UnionChoices(game, [game]);
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var dlg = new PickLaunchWindow(game.LaunchChoices);
        if (await dlg.ShowDialog<bool>(owner) != true || dlg.Chosen is null) return;
        ExtraShortcuts.ApplyLaunch(game, dlg.Chosen);
        Persist();
    }

    private void AllOn_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var g in _games) g.Selected = true;
        Persist();
    }

    private void AllOff_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var g in _games) g.Selected = false;
        Persist();
    }

    private void Replace(IReadOnlyList<OptimizerGame> games)
    {
        _games.Clear();
        foreach (var game in games)
        {
            OptimizerPicks.Apply(game);
            DeckCovers.ApplyBytes(game);
            _games.Add(game);
        }
    }

    private void Persist()
    {
        var session = DeckSession.Current;
        OptimizerPicks.RememberAll(_games);
        OptimizerLibraryCache.Save(
            session.Client.ActiveProfile?.Id ?? "local",
            session.Client.ActiveProfile?.Host ?? "local",
            _games);
    }

    private void ShowBusy(string title, string detail)
    {
        BusyOverlay.IsVisible = true;
        BusyTitle.Text = string.IsNullOrWhiteSpace(title) ? "Working…" : title;
        BusyDetail.Text = detail ?? "";
        BusyBar.IsIndeterminate = true;
    }

    private void HideBusy() => BusyOverlay.IsVisible = false;
}
