using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sesame.Models;
using Sesame.Services;

namespace Sesame.Deck.Views;

public partial class StorePage : UserControl
{
    private readonly ObservableCollection<PackHit> _hits = new();
    private readonly PackStore _store = new();
    private CancellationTokenSource? _cts;

    public StorePage()
    {
        InitializeComponent();
        ResultList.ItemsSource = _hits;
        GameBox.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(StoreGame.Label));
    }

    public void SetGames(IEnumerable<StoreGame> catalog, IEnumerable<StoreGame>? library)
    {
        var items = new List<StoreGame> { StoreGame.All };
        items.AddRange(catalog.Where(g => !g.IsAll));
        foreach (var game in library ?? [])
        {
            if (game.IsAll) continue;
            if (items.All(g => !g.MatchesTitle(game.Name) || !g.MatchesSystem(game.System)))
                items.Add(game);
        }
        GameBox.ItemsSource = items;
        if (GameBox.SelectedItem is null && items.Count > 0)
            GameBox.SelectedIndex = 0;
    }

    private void Query_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SearchAsync();
    }

    private async void Search_Click(object? sender, RoutedEventArgs e) => await SearchAsync();

    private async Task SearchAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var query = QueryBox.Text?.Trim() ?? "";
        var kind = (KindBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        var source = (SourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All sources";
        var game = GameBox.SelectedItem as StoreGame ?? StoreGame.All;
        HintText.Text = "Searching…";
        try
        {
            var hits = await _store.SearchAsync(query, source, kind, game, ct: ct);
            _hits.Clear();
            foreach (var hit in hits)
                _hits.Add(hit);
            HintText.Text = _hits.Count == 0 ? "No packs found." : _hits.Count + " packs.";
        }
        catch (OperationCanceledException)
        {
            /* new search */
        }
        catch (Exception ex)
        {
            HintText.Text = ex.Message;
        }
    }
}
