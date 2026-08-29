using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sesame.Deck.Input;
using Sesame.Models;
using Sesame.Services;
using Sesame;

namespace Sesame.Deck.Views;

public partial class ShellWindow : Window
{
    private readonly DeckSession _session = DeckSession.Current;
    private readonly bool _gameMode;
    private readonly GamepadPump _pad;
    private readonly QuickAccessStore _pins = new();
    private int _tile;
    private bool _tabsReady;

    public ShellWindow()
    {
        InitializeComponent();
        _gameMode = HostEnvironment.UseGameModeUi;
        Title = AppBrand.WindowTitle + " " + AppVersion.Label;
        VersionLabel.Text = AppVersion.Label;
        try
        {
            Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://SESAME/Assets/sesame.ico")));
        }
        catch { /* ico is optioneel */ }
        DesktopRoot.IsVisible = !_gameMode;
        GameRoot.IsVisible = _gameMode;
        HintBar.Text = _gameMode
            ? "A confirm    B back    D-pad / stick navigate    L/R tabs"
            : "";
        if (_gameMode)
        {
            WindowState = WindowState.FullScreen;
            Width = 1280;
            Height = 800;
            QuickPane.IsVisible = false;
            GameBackBtn.IsVisible = false;
        }

        _pad = new GamepadPump(OnPad);
        _session.Changed += RefreshStatus;
        _pins.Load();
        BuildQuickAccess();
        WirePanels();
        Closed += (_, _) =>
        {
            _pad.Dispose();
            _session.Client.Dispose();
        };
        Opened += async (_, _) =>
        {
            try
            {
                await _session.EnsureConnectedAsync();
                AfterConnect();
            }
            catch (Exception ex)
            {
                FooterStatus.Text = ex.Message;
            }
            RefreshStatus();
            _tabsReady = true;
            if (_gameMode) FocusTile(0);
        };
        RefreshStatus();
    }

    private void WirePanels()
    {
        FilesPanel.PathChanged += path => FooterStatus.Text = path;
        AppsPanel.StatusChanged += text => FooterStatus.Text = text;
        AppsPanel.ManualChanged += ArtPanel.UpsertManual;
        AppsPanel.ManualRemoved += ArtPanel.RemoveManual;
        GamesPanel.StatusChanged += text => FooterStatus.Text = text;
        GamesPanel.ManualChanged += ArtPanel.UpsertManual;
        GamesPanel.ManualRemoved += ArtPanel.RemoveManual;
        ArtPanel.StatusChanged += text => FooterStatus.Text = text;
        ArtPanel.SetCompact(_gameMode);
        StorePanel.SetGames(_session.Catalog.StoreGames, []);
    }

    private void AfterConnect()
    {
        FilesPanel.OnConnected();
        AppsPanel.OnConnected();
        GamesPanel.OnConnected();
        ArtPanel.OnConnected();
        StorePanel.SetGames(_session.Catalog.StoreGames, GamesPanel.Items.Select(g => g.Identity));
        if (_gameMode)
            ArtPanel.SetCompact(true);
    }

    private void BuildQuickAccess()
    {
        QuickList.ItemsSource = _pins.Combined(_session.Catalog.QuickAccess).ToList();
    }

    private void Quick_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_tabsReady) return;
        if (QuickList.SelectedItem is not QuickPath path) return;
        MainTabs.SelectedIndex = 0;
        FilesPanel.OpenPath(path.Path);
    }

    private async void Tabs_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_tabsReady) return;
        if (MainTabs.SelectedIndex == 1)
            await AppsPanel.EnsureScannedAsync();
        if (MainTabs.SelectedIndex == 2 && GamesPanel.Items.Count == 0)
            await GamesPanel.ScanAsync();
    }

    private void RefreshStatus()
    {
        var status = _session.Status;
        StatusLabel.Text = status;
        StatusLabel.IsVisible = !string.IsNullOrWhiteSpace(status);
        GameStatus.Text = string.IsNullOrWhiteSpace(status)
            ? HostEnvironment.RuntimeLabel
            : status + " · " + HostEnvironment.RuntimeLabel;
        if (!string.IsNullOrWhiteSpace(status))
            FooterStatus.Text = status;
        RemoteBtn.IsVisible = true;
    }

    private void OpenTab(int index, bool compactArt)
    {
        ArtPanel.SetCompact(compactArt);
        MainTabs.SelectedIndex = index;
        if (!_gameMode) return;
        GameHome.IsVisible = false;
        DesktopRoot.IsVisible = true;
        GameBackBtn.IsVisible = true;
    }

    private void ShowHome()
    {
        if (!_gameMode) return;
        DesktopRoot.IsVisible = false;
        GameHome.IsVisible = true;
        GameBackBtn.IsVisible = false;
        FocusTile(_tile);
    }

    private void NavSettings_Click(object? sender, RoutedEventArgs e) => OpenTab(5, compactArt: false);

    private void Tile0_Click(object? sender, RoutedEventArgs e)
    {
        _tile = 0;
        OpenTab(3, compactArt: true);
        ArtPanel.OnConnected();
    }

    private async void Tile1_Click(object? sender, RoutedEventArgs e)
    {
        _tile = 1;
        OpenTab(2, compactArt: false);
        if (GamesPanel.Items.Count == 0)
            await GamesPanel.ScanAsync();
    }

    private void Tile2_Click(object? sender, RoutedEventArgs e)
    {
        _tile = 2;
        OpenTab(0, compactArt: false);
        FilesPanel.OnConnected();
    }

    private void Tile3_Click(object? sender, RoutedEventArgs e)
    {
        _tile = 3;
        OpenTab(5, compactArt: false);
    }

    private void GameBack_Click(object? sender, RoutedEventArgs e) => ShowHome();

    private async void Remote_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new RemoteDialog();
        await dlg.ShowDialog(this);
        if (dlg.Connected)
            AfterConnect();
        RefreshStatus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (GamepadPump.TryKey(e.Key, out var action))
        {
            OnPad(action);
            e.Handled = true;
        }
    }

    private void OnPad(PadAction action)
    {
        if (_gameMode && GameHome.IsVisible)
        {
            _tile = action switch
            {
                PadAction.Left => _tile is 1 or 3 ? _tile - 1 : _tile,
                PadAction.Right => _tile is 0 or 2 ? _tile + 1 : _tile,
                PadAction.Up => _tile >= 2 ? _tile - 2 : _tile,
                PadAction.Down => _tile <= 1 ? _tile + 2 : _tile,
                _ => _tile
            };
            FocusTile(_tile);
            if (action == PadAction.Confirm)
            {
                Button[] tiles = [Tile0, Tile1, Tile2, Tile3];
                tiles[_tile].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            return;
        }

        if (action == PadAction.Back && _gameMode && DesktopRoot.IsVisible)
        {
            ShowHome();
            return;
        }

        if (action is PadAction.PrevTab or PadAction.NextTab)
        {
            if (DesktopRoot.IsVisible)
            {
                var next = MainTabs.SelectedIndex + (action == PadAction.NextTab ? 1 : -1);
                MainTabs.SelectedIndex = Math.Clamp(next, 0, Math.Max(0, MainTabs.ItemCount - 1));
            }
        }
    }

    private void FocusTile(int index)
    {
        Button[] tiles = [Tile0, Tile1, Tile2, Tile3];
        tiles[Math.Clamp(index, 0, 3)].Focus();
    }
}
