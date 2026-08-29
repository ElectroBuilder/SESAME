using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sesame.Deck.Input;
using Sesame.Models;
using Sesame.Services;
using Sesame.Services.GameOptimizer;
using Sesame;

namespace Sesame.Deck.Views;

public partial class ShellWindow : Window
{
    private const int TabDash = 0;
    private const int TabFiles = 1;
    private const int TabApps = 2;
    private const int TabGames = 3;
    private const int TabOptimize = 4;
    private const int TabStore = 5;

    private readonly DeckSession _session = DeckSession.Current;
    private readonly bool _gameMode;
    private readonly GamepadPump _pad;
    private readonly QuickAccessStore _pins = new();
    private int _tile;
    private bool _tabsReady;
    private bool _workOpen;

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
            : "Drop files here to upload them to the current folder or a game.";
        if (_gameMode)
        {
            WindowState = WindowState.FullScreen;
            Width = 1280;
            Height = 800;
            QuickPane.IsVisible = false;
            GameBackBtn.IsVisible = false;
            SessionsBtn.IsVisible = false;
            ConnectBtn.IsVisible = false;
            DisconnectBtn.IsVisible = false;
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
                if (!_gameMode && HostEnvironment.LocalAvailable && _session.Connected)
                {
                    var client = _session.Client;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            if (SteamSession.IsSteamRunning(client))
                                SteamSelfShortcut.TryEnsure(client);
                            else
                                SteamSelfShortcut.Ensure(client);
                        }
                        catch
                        {
                            /* Game Mode tile is optional until the next optimize */
                        }
                    });
                }
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
        FilesPanel.PathChanged += path =>
        {
            FooterStatus.Text = path;
            RefreshDashboard();
        };
        AppsPanel.StatusChanged += text =>
        {
            SetStatus(text);
            RefreshDashboard();
        };
        AppsPanel.ManualChanged += ArtPanel.UpsertManual;
        AppsPanel.ManualRemoved += ArtPanel.RemoveManual;
        GamesPanel.StatusChanged += text =>
        {
            SetStatus(text);
            RefreshDashboard();
        };
        GamesPanel.ManualChanged += ArtPanel.UpsertManual;
        GamesPanel.ManualRemoved += ArtPanel.RemoveManual;
        GamesPanel.OpenFolder += path =>
        {
            MainTabs.SelectedIndex = TabFiles;
            FilesPanel.OpenPath(path);
        };
        GamesPanel.SearchPacks += game =>
        {
            MainTabs.SelectedIndex = TabStore;
            StorePanel.Prefill(game);
        };
        ArtPanel.StatusChanged += text =>
        {
            SetStatus(text);
            RefreshDashboard();
        };
        ArtPanel.SetCompact(_gameMode);
        StorePanel.SetGames(_session.Catalog.StoreGames, []);
        DashPanel.OpenTab += OpenDashboardTab;
        DashPanel.ScanRequested += () => _ = DashboardScanAsync();
        DashPanel.OptimizeRequested += () => _ = DashboardOptimizeAsync();
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
        RefreshDashboard();
    }

    private void BuildQuickAccess()
    {
        QuickList.ItemsSource = _pins.Combined(_session.Catalog.QuickAccess).ToList();
    }

    private void Quick_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_tabsReady) return;
        if (QuickList.SelectedItem is not QuickPath path) return;
        MainTabs.SelectedIndex = TabFiles;
        FilesPanel.OpenPath(path.Path);
    }

    private void Tabs_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_tabsReady) return;
        if (MainTabs.SelectedIndex == TabDash)
            RefreshDashboard();
    }

    private void RefreshStatus()
    {
        var status = _session.Status;
        if (string.IsNullOrWhiteSpace(status))
            status = _session.Connected ? "" : "Not connected";
        StatusLabel.Text = string.IsNullOrWhiteSpace(status) ? "Connected" : status;
        StatusLabel.IsVisible = true;
        GameStatus.Text = string.IsNullOrWhiteSpace(_session.Status)
            ? HostEnvironment.RuntimeLabel
            : _session.Status + " · " + HostEnvironment.RuntimeLabel;
        if (!string.IsNullOrWhiteSpace(_session.Status))
            FooterStatus.Text = _session.Status;
        ConnectBtn.IsEnabled = !_session.Connected;
        DisconnectBtn.IsEnabled = _session.Connected;
        RefreshDashboard();
    }

    private void OpenDashboardTab(string id)
    {
        MainTabs.SelectedIndex = id switch
        {
            "files" => TabFiles,
            "apps" => TabApps,
            "games" => TabGames,
            "optimize" => TabOptimize,
            "store" => TabStore,
            _ => TabDash
        };
    }

    private async Task DashboardScanAsync()
    {
        if (!_session.Connected)
        {
            FooterStatus.Text = "Connect to the Steam Deck first.";
            return;
        }
        if (_workOpen) return;
        ShowWork("Scanning apps…", "Looking up desktop entries and Flatpaks on the Deck.");
        try
        {
            await AppsPanel.ScanAsync(overlay: false);
            ShowWork("Scanning games…", "Reading ROM folders and mods on the Deck.");
            await GamesPanel.ScanAsync(overlay: false);
            ShowWork("Scanning library…", "Reading ROMs, Hydra games and apps for Optimize.");
            await ArtPanel.ScanLibraryAsync(overlay: false);
            FooterStatus.Text =
                $"Scan done · {AppsPanel.Count} apps · {GamesPanel.Items.Count} games · {ArtPanel.Count} for Optimize";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = ex.Message;
        }
        finally
        {
            HideWork();
            RefreshDashboard();
        }
    }

    private async Task DashboardOptimizeAsync()
    {
        if (!_session.Connected)
        {
            FooterStatus.Text = "Connect to the Steam Deck first.";
            return;
        }
        if (ArtPanel.Count == 0)
        {
            ShowWork("Scanning library…", "Reading ROMs, Hydra games and apps on the Deck.");
            try { await ArtPanel.ScanLibraryAsync(overlay: false); }
            finally { HideWork(); }
            RefreshDashboard();
            if (ArtPanel.Count == 0)
            {
                FooterStatus.Text = "No games found. Scan first.";
                return;
            }
        }
        MainTabs.SelectedIndex = TabOptimize;
        await ArtPanel.RunOptimizeInteractiveAsync(selectAllIfEmpty: true);
        RefreshDashboard();
    }

    private void RefreshDashboard()
    {
        DashPanel.UpdateStats(new DashboardStats
        {
            Connected = _session.Connected,
            FileCount = FilesPanel.ItemCount,
            Folder = FilesPanel.CurrentPath,
            AppCount = AppsPanel.Count,
            GameCount = GamesPanel.Items.Count,
            OptimizeCount = ArtPanel.Count,
            InSteamCount = ArtPanel.InSteamCount,
            SelectedCount = ArtPanel.SelectedCount,
            StoreCount = _session.Catalog.StoreGames.Count
        });
    }

    private void SetStatus(string text)
    {
        FooterStatus.Text = text;
        if (_workOpen)
            WorkDetail.Text = text;
    }

    private void ShowWork(string title, string detail)
    {
        _workOpen = true;
        WorkOverlay.IsVisible = true;
        WorkTitle.Text = title;
        WorkDetail.Text = detail;
    }

    private void HideWork()
    {
        _workOpen = false;
        WorkOverlay.IsVisible = false;
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

    private async void NavSettings_Click(object? sender, RoutedEventArgs e) => await OpenSettingsAsync();

    private async Task OpenSettingsAsync()
    {
        var win = new SettingsWindow();
        await win.ShowDialog(this);
        ArtPanel.OnConnected();
    }

    private void PinCurrent_Click(object? sender, RoutedEventArgs e)
    {
        var path = FilesPanel.CurrentPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        _pins.Add(System.IO.Path.GetFileName(path.TrimEnd('/')), path, "Pinned");
        BuildQuickAccess();
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (HostEnvironment.LocalAvailable)
                await _session.ConnectLocalAsync();
            else
            {
                var dlg = new RemoteDialog();
                await dlg.ShowDialog(this);
                if (!dlg.Connected) return;
            }
            AfterConnect();
        }
        catch (Exception ex)
        {
            FooterStatus.Text = ex.Message;
        }
        RefreshStatus();
    }

    private void Disconnect_Click(object? sender, RoutedEventArgs e)
    {
        _session.Disconnect();
        RefreshStatus();
        RefreshDashboard();
    }

    private void Tile0_Click(object? sender, RoutedEventArgs e)
    {
        _tile = 0;
        OpenTab(TabOptimize, compactArt: true);
        ArtPanel.OnConnected();
    }

    private void Tile1_Click(object? sender, RoutedEventArgs e)
    {
        _tile = 1;
        OpenTab(TabGames, compactArt: false);
    }

    private void Tile2_Click(object? sender, RoutedEventArgs e)
    {
        _tile = 2;
        OpenTab(TabFiles, compactArt: false);
        FilesPanel.OnConnected();
    }

    private async void Tile3_Click(object? sender, RoutedEventArgs e)
    {
        _tile = 3;
        await OpenSettingsAsync();
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
