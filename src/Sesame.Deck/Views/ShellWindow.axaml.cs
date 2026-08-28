using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sesame.Deck.Input;
using Sesame;
using Sesame.Services;

namespace Sesame.Deck.Views;

public partial class ShellWindow : Window
{
    private readonly DeckSession _session = DeckSession.Current;
    private readonly bool _gameMode;
    private readonly GamepadPump _pad;
    private readonly OptimizerPage _optimizer = new();
    private readonly FilesPage _files = new();
    private readonly SettingsPage _settings = new();
    private Control? _current;
    private int _tile;

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
        if (_gameMode)
        {
            WindowState = WindowState.FullScreen;
            Width = 1280;
            Height = 800;
            HintBar.Text = "A bevestigen    B terug    D-pad / stick navigeren    L/R tabs    Start menu";
        }
        else
            HintBar.Text = "Muis, toetsenbord of Steam Deck-controls. Lokaal of SSH.";

        _pad = new GamepadPump(OnPad);
        _session.Changed += RefreshStatus;
        Closed += (_, _) =>
        {
            _pad.Dispose();
            _session.Client.Dispose();
        };
        if (!_gameMode)
            ShowPage(_optimizer, "Optimaliseren");
        Opened += async (_, _) =>
        {
            try
            {
                await _session.EnsureConnectedAsync();
                _optimizer.OnConnected();
                _files.OnConnected();
            }
            catch (Exception ex)
            {
                FooterStatus.Text = ex.Message;
            }
            RefreshStatus();
            if (_gameMode) FocusTile(0);
        };
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        StatusLabel.Text = _session.Status;
        GameStatus.Text = _session.Status + " · " + HostEnvironment.RuntimeLabel;
        FooterStatus.Text = _session.Status;
        LocalBtn.IsVisible = HostEnvironment.LocalAvailable;
    }

    private void ShowPage(Control page, string title)
    {
        _current = page;
        GamePageTitle.Text = title;
        if (_gameMode)
        {
            GameHome.IsVisible = false;
            GamePage.IsVisible = true;
            GameHost.Content = page;
        }
        else
            DesktopHost.Content = page;
    }

    private void ShowHome()
    {
        if (!_gameMode) return;
        GamePage.IsVisible = false;
        GameHome.IsVisible = true;
        FocusTile(_tile);
    }

    private void NavOpt_Click(object? sender, RoutedEventArgs e) => ShowPage(_optimizer, "Optimaliseren");
    private void NavFiles_Click(object? sender, RoutedEventArgs e) => ShowPage(_files, "Bestanden");
    private void NavSettings_Click(object? sender, RoutedEventArgs e) => ShowPage(_settings, "Instellingen");

    private void Tile0_Click(object? sender, RoutedEventArgs e) { _tile = 0; ShowPage(_optimizer, "Optimaliseren"); }
    private void Tile1_Click(object? sender, RoutedEventArgs e) { _tile = 1; ShowPage(_optimizer, "Bibliotheek"); }
    private void Tile2_Click(object? sender, RoutedEventArgs e) { _tile = 2; ShowPage(_files, "Bestanden"); }
    private void Tile3_Click(object? sender, RoutedEventArgs e) { _tile = 3; ShowPage(_settings, "Instellingen"); }
    private void GameBack_Click(object? sender, RoutedEventArgs e) => ShowHome();

    private async void Local_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _session.ConnectLocalAsync();
            _optimizer.OnConnected();
            _files.OnConnected();
        }
        catch (Exception ex)
        {
            FooterStatus.Text = ex.Message;
        }
        RefreshStatus();
    }

    private async void Remote_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new RemoteDialog();
        await dlg.ShowDialog(this);
        if (dlg.Connected)
        {
            _optimizer.OnConnected();
            _files.OnConnected();
        }
        RefreshStatus();
    }

    private void Disconnect_Click(object? sender, RoutedEventArgs e)
    {
        _session.Disconnect();
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

        if (action == PadAction.Back)
        {
            if (_gameMode && GamePage.IsVisible) ShowHome();
            return;
        }

        if (action is PadAction.PrevTab or PadAction.NextTab)
        {
            if (_current == _optimizer) ShowPage(_files, "Bestanden");
            else if (_current == _files) ShowPage(_settings, "Instellingen");
            else ShowPage(_optimizer, "Optimaliseren");
        }
    }

    private void FocusTile(int index)
    {
        Button[] tiles = [Tile0, Tile1, Tile2, Tile3];
        tiles[Math.Clamp(index, 0, 3)].Focus();
    }
}
