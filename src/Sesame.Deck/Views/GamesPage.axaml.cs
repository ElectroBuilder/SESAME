using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sesame.Models;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck.Views;

public partial class GamesPage : UserControl
{
    private readonly ObservableCollection<GameEntry> _games = new();
    private bool _busy;

    public event Action<OptimizerGame>? ManualChanged;
    public event Action<string?>? ManualRemoved;
    public event Action<string>? OpenFolder;
    public event Action<StoreGame?>? SearchPacks;
    public event Action<string>? StatusChanged;
    public IReadOnlyList<GameEntry> Items => _games;

    public GamesPage()
    {
        InitializeComponent();
        GameList.ItemsSource = _games;
    }

    public void OnConnected() => AddManuals();

    private async void Scan_Click(object? sender, RoutedEventArgs e) => await ScanAsync();

    public async Task ScanAsync()
    {
        var session = DeckSession.Current;
        if (!session.Connected || _busy) return;
        _busy = true;
        BusyOverlay.IsVisible = true;
        BusyText.Text = "Scanning games…";
        try
        {
            var found = await Task.Run(() => session.Library.Scan(session.Client, session.Catalog));
            _games.Clear();
            foreach (var game in found)
                _games.Add(game);
            AddManuals();
            HintText.Text = $"{_games.Count} games.";
            StatusChanged?.Invoke($"Games: {_games.Count}");
        }
        catch (Exception ex)
        {
            HintText.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            _busy = false;
            BusyOverlay.IsVisible = false;
        }
    }

    private void AddManuals()
    {
        foreach (var item in ManualShortcutStore.Load()
                     .Where(x => x.AddedByUser && x.Kind.Equals("Game", StringComparison.OrdinalIgnoreCase)))
        {
            var entry = ManualShortcutStore.ToLibraryEntry(item);
            if (_games.All(g => !string.Equals(g.RomPath, entry.RomPath, StringComparison.OrdinalIgnoreCase)))
                _games.Add(entry);
        }
    }

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null || !DeckSession.Current.Connected) return;
        var dlg = new PromptWindow("Game");
        if (await dlg.ShowDialog<bool>(owner) != true) return;
        ManualShortcutStore.Upsert(dlg.Result);
        var entry = ManualShortcutStore.ToLibraryEntry(dlg.Result);
        _games.Add(entry);
        ManualChanged?.Invoke(ManualShortcutStore.ToGame(dlg.Result));
        HintText.Text = entry.DisplayName + " added. It stays until you remove it.";
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry game || !game.IsManual) return;
        if (!string.IsNullOrEmpty(game.ManualId))
        {
            ManualShortcutStore.Delete(game.ManualId);
            ManualRemoved?.Invoke(game.ManualId);
        }
        _games.Remove(game);
    }

    private void OpenRom_Click(object? sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is GameEntry { RomPath: var p } && !string.IsNullOrEmpty(p))
            OpenFolder?.Invoke(DeckClient.Parent(p));
    }

    private void OpenMods_Click(object? sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is GameEntry g)
            OpenFolder?.Invoke(g.ModPath ?? DeckSession.Current.Catalog.EdenMods);
    }

    private void OpenSaves_Click(object? sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry g) return;
        OpenFolder?.Invoke(g.SavePath ?? DeckSession.Current.Library.Eden.Primary?.Folder
            ?? DeckSession.Current.Catalog.EdenUsersRoot);
    }

    private void OpenTextures_Click(object? sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is GameEntry g && !string.IsNullOrEmpty(g.TexturePath))
            OpenFolder?.Invoke(g.TexturePath);
    }

    private void SearchPacks_Click(object? sender, RoutedEventArgs e)
    {
        var game = GameList.SelectedItem as GameEntry;
        SearchPacks?.Invoke(game?.Identity);
    }
}
