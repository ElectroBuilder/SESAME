using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sesame.Models;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck.Views;

public partial class AppsPage : UserControl
{
    private readonly ObservableCollection<OptimizerGame> _apps = new();
    private bool _busy;

    public event Action<OptimizerGame>? ManualChanged;
    public event Action<string?>? ManualRemoved;
    public event Action<string>? StatusChanged;

    public AppsPage()
    {
        InitializeComponent();
        AppList.ItemsSource = _apps;
    }

    public void OnConnected()
    {
        foreach (var item in ManualShortcutStore.Load()
                     .Where(x => x.AddedByUser && x.Kind.Equals("App", StringComparison.OrdinalIgnoreCase)))
        {
            var game = ManualShortcutStore.ToGame(item);
            if (_apps.All(a => !string.Equals(a.RomPath, game.RomPath, StringComparison.OrdinalIgnoreCase)))
                _apps.Add(game);
        }
    }

    public async Task EnsureScannedAsync()
    {
        if (_apps.Count > 0 && _apps.Any(a => !a.IsManual)) return;
        await ScanAsync();
    }

    private async void Scan_Click(object? sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        var session = DeckSession.Current;
        if (!session.Connected || _busy) return;
        _busy = true;
        BusyOverlay.IsVisible = true;
        BusyText.Text = "Reading installed apps…";
        HintText.Text = "Scanning native apps…";
        try
        {
            var found = await Task.Run(() =>
                GameOptimizerService.ScanNativeApps(session.Client, new Progress<string>(t =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => BusyText.Text = t))));
            _apps.Clear();
            foreach (var app in found)
                _apps.Add(app);
            HintText.Text = found.Count == 0
                ? "No known native apps found."
                : $"{found.Count} native apps. Duplicates are merged.";
            StatusChanged?.Invoke($"Apps: {found.Count}");
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

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var dlg = new PromptWindow("App");
        if (await dlg.ShowDialog<bool>(owner) != true) return;
        ManualShortcutStore.Upsert(dlg.Result);
        var game = ManualShortcutStore.ToGame(dlg.Result);
        _apps.Add(game);
        ManualChanged?.Invoke(game);
        HintText.Text = game.DisplayName + " added. It stays until you remove it.";
    }

    private async void Pick_Click(object? sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not OptimizerGame game) return;
        if (game.LaunchChoices.Count == 0)
            ExtraShortcuts.UnionChoices(game, [game]);
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var dlg = new PickLaunchWindow(game.LaunchChoices);
        if (await dlg.ShowDialog<bool>(owner) != true || dlg.Chosen is null) return;
        ExtraShortcuts.ApplyLaunch(game, dlg.Chosen);
        ManualChanged?.Invoke(game);
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not OptimizerGame game || !game.IsManual) return;
        ManualShortcutStore.Delete(game.ManualId);
        _apps.Remove(game);
        ManualRemoved?.Invoke(game.ManualId);
    }
}
