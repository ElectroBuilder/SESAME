using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Sesame.Models;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck.Views;

public partial class OptimizerPage : UserControl
{
    private readonly ObservableCollection<OptimizerGame> _games = new();
    private bool _busy;

    public OptimizerPage()
    {
        InitializeComponent();
        GameList.ItemsSource = _games;
    }

    public void OnConnected()
    {
        var id = DeckSession.Current.Client.ActiveProfile?.Id ?? "local";
        var host = DeckSession.Current.Client.ActiveProfile?.Host ?? "local";
        var cached = OptimizerLibraryCache.Load(id, host);
        if (cached.Count == 0) return;
        Replace(cached);
        Hint.Text = cached.Count + " games from cache. Scan for a fresh list.";
    }

    private async void Scan_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var session = DeckSession.Current;
        if (!session.Connected)
        {
            Hint.Text = "Connect first (This Deck or SSH).";
            return;
        }
        _busy = true;
        Hint.Text = "Scanning…";
        try
        {
            var games = await Task.Run(() => GameOptimizerService.Scan(session.Client, session.Catalog));
            Replace(games);
            Hint.Text = games.Count + " games found.";
        }
        catch (Exception ex)
        {
            Hint.Text = ex.Message;
        }
        finally
        {
            _busy = false;
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
        var selected = _games.Where(g => g.Selected).ToList();
        if (selected.Count == 0)
        {
            Hint.Text = "Select at least one game.";
            return;
        }
        _busy = true;
        Hint.Text = "Optimizing…";
        try
        {
            var report = await GameOptimizerService.ApplyAsync(
                session.Client, session.Catalog, _games,
                new Progress<OptimizeProgress>(p => Hint.Text = p.Title + " — " + p.Detail),
                CancellationToken.None);
            Hint.Text = report.Summary;
        }
        catch (Exception ex)
        {
            Hint.Text = ex.Message;
        }
        finally
        {
            _busy = false;
        }
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
            if (game.GridBytes is { Length: > 0 })
            {
                try
                {
                    using var ms = new MemoryStream(game.GridBytes);
                    game.Cover = new Bitmap(ms);
                }
                catch { /* cover is optioneel */ }
            }
            _games.Add(game);
        }
    }
}
