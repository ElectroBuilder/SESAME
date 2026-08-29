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
        var cached = OptimizerLibraryCache.Load(id, host);
        if (cached.Count == 0) return;
        DeckCovers.Hydrate(session.Client, cached);
        Replace(cached);
        Hint.Text = cached.Count + " games from cache, including artwork already in Steam.";
        _ = DeckCovers.PrefetchAsync(_games.ToList(), CancellationToken.None);
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

    private async void Scan_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var session = DeckSession.Current;
        if (!session.Connected)
        {
            Hint.Text = "Connect first (This Deck).";
            return;
        }
        _busy = true;
        ShowBusy("Scanning library", "ROMs, Hydra games and apps…");
        try
        {
            var progress = new Progress<OptimizeProgress>(p =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowBusy(p.Title, p.Detail)));
            var games = await Task.Run(() => GameOptimizerService.Scan(session.Client, session.Catalog, progress));
            DeckCovers.Hydrate(session.Client, games);
            Replace(games);
            Persist();
            Hint.Text = games.Count + " items. Existing Steam covers are shown; Apply writes SESAME shortcuts.";
            StatusChanged?.Invoke($"Artwork: {games.Count}");
            _ = DeckCovers.PrefetchAsync(_games.ToList(), CancellationToken.None);
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

    private async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var session = DeckSession.Current;
        if (!session.Connected)
        {
            Hint.Text = "Connect first.";
            return;
        }
        if (!_games.Any(g => g.Selected))
        {
            Hint.Text = "Select at least one game.";
            return;
        }
        _busy = true;
        ShowBusy("Apply artwork", "Writing shortcuts and covers…");
        try
        {
            var report = await GameOptimizerService.ApplyAsync(
                session.Client, session.Catalog, _games,
                new Progress<OptimizeProgress>(p =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowBusy(p.Title, p.Detail))),
                CancellationToken.None);
            DeckCovers.Hydrate(session.Client, _games);
            Hint.Text = report.Summary;
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
    }

    private void AllOff_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var g in _games) g.Selected = false;
    }

    private void Replace(IReadOnlyList<OptimizerGame> games)
    {
        _games.Clear();
        foreach (var game in games)
        {
            DeckCovers.ApplyBytes(game);
            _games.Add(game);
        }
    }

    private void Persist()
    {
        var session = DeckSession.Current;
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
