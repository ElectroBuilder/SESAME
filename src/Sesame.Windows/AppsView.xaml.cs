using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Sesame.Models;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame;

public partial class AppsView : UserControl
{
    private readonly ObservableCollection<OptimizerGame> _apps = new();
    private DeckClient? _client;
    private bool _busy;
    private bool _scanned;

    public event Action<string>? StatusChanged;
    public event Action<OptimizerGame>? ManualChanged;
    public event Action<string>? ManualRemoved;

    public AppsView()
    {
        InitializeComponent();
        ListColumns.Attach(AppList, _apps);
    }

    public int Count => _apps.Count;

    public void Attach(DeckClient client) => _client = client;

    public void OnConnected()
    {
        _scanned = false;
        ShowManuals();
    }

    public void Clear()
    {
        _scanned = false;
        _apps.Clear();
        HintText.Text = "Connect to the Deck, then scan.";
    }

    public Task EnsureScannedAsync()
    {
        if (_scanned || _client is not { IsConnected: true }) return Task.CompletedTask;
        return ScanAsync();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    public async Task ScanAsync(bool overlay = true)
    {
        if (_busy) return;
        if (_client is not { IsConnected: true })
        {
            HintText.Text = "Connect to the Steam Deck first.";
            return;
        }

        _busy = true;
        if (overlay)
            ShowBusy("Reading installed apps…",
                "Looking up desktop entries and Flatpaks on the Deck. The list stays usable afterwards.");
        HintText.Text = "Reading installed apps…";
        StatusChanged?.Invoke("Apps: reading installed apps…");
        try
        {
            var client = _client;
            var progress = new Progress<string>(text => Dispatcher.Invoke(() =>
            {
                OverlayDetail.Text = text;
                HintText.Text = text;
                StatusChanged?.Invoke("Apps: " + text);
            }));
            var found = await Task.Run(() => GameOptimizerService.ScanNativeApps(client, progress));
            _apps.Clear();
            foreach (var app in found)
                _apps.Add(app);
            ListColumns.Refresh(AppList);
            _scanned = true;
            var multi = found.Count(a => a.LaunchChoices.Count > 1);
            HintText.Text = found.Count == 0
                ? "No known native apps found. Use + App to add one yourself."
                : $"{found.Count} native apps." +
                  (multi > 0 ? $" {multi} have more than one launch — use Choose launch." : " Duplicates are merged.");
            StatusChanged?.Invoke($"Apps: {found.Count}");
        }
        catch (Exception ex)
        {
            HintText.Text = "Scan failed: " + ex.Message;
            StatusChanged?.Invoke("Apps: scan failed");
        }
        finally
        {
            _busy = false;
            if (overlay) ProgressOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var win = new ManualEntryWindow("App", _client?.IsLocal == true) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true) return;
        ManualShortcutStore.Upsert(win.Result);
        var game = ManualShortcutStore.ToGame(win.Result);
        UpsertLocal(game);
        ManualChanged?.Invoke(game);
        HintText.Text = game.DisplayName + " added. It stays until you remove it.";
        StatusChanged?.Invoke("Apps: added " + game.DisplayName);
    }

    private void PickLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not OptimizerGame game)
        {
            MessageBox.Show(Window.GetWindow(this), "Select an app first.", "Choose launch");
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
        ListColumns.Refresh(AppList);
        ManualChanged?.Invoke(game);
        HintText.Text = "Using " + win.Chosen.Label + " for " + game.DisplayName + ".";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not OptimizerGame game)
        {
            MessageBox.Show(Window.GetWindow(this), "Select an app first.", "Remove");
            return;
        }

        if (!game.IsManual)
        {
            MessageBox.Show(Window.GetWindow(this),
                "Only apps you added with + App can be removed. Scanned apps stay in the allow-list.",
                "Remove");
            return;
        }

        if (!string.IsNullOrEmpty(game.ManualId))
            ManualShortcutStore.Delete(game.ManualId);
        _apps.Remove(game);
        ManualRemoved?.Invoke(game.ManualId);
        ListColumns.Refresh(AppList);
        HintText.Text = game.DisplayName + " removed.";
    }

    private void ShowManuals()
    {
        _apps.Clear();
        foreach (var item in ManualShortcutStore.Load()
                     .Where(x => x.AddedByUser && x.Kind.Equals("App", StringComparison.OrdinalIgnoreCase)))
            _apps.Add(ManualShortcutStore.ToGame(item));
        ListColumns.Refresh(AppList);
        HintText.Text = _apps.Count == 0
            ? "Connect to the Deck, then scan. Use + App for anything the scanner misses."
            : $"{_apps.Count} manually added apps. Scan to also load Deck apps.";
    }

    private void UpsertLocal(OptimizerGame game)
    {
        var key = ExtraShortcuts.KeyOf(game);
        var existing = _apps.FirstOrDefault(a =>
            string.Equals(ExtraShortcuts.KeyOf(a), key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var i = _apps.IndexOf(existing);
            _apps[i] = game;
        }
        else
            _apps.Add(game);
        ListColumns.Refresh(AppList);
    }

    private void ShowBusy(string title, string detail)
    {
        OverlayTitle.Text = title;
        OverlayDetail.Text = detail;
        ProgressOverlay.Visibility = Visibility.Visible;
    }
}
