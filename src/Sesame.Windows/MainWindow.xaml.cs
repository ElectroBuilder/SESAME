using System.Collections.ObjectModel;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Renci.SshNet.Common;
using Sesame.Models;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame;

public partial class MainWindow : Window
{
    private const int TabDash = 0;
    private const int TabFiles = 1;
    private const int TabApps = 2;
    private const int TabGames = 3;
    private const int TabOptimize = 4;
    private const int TabStore = 5;
    private readonly AppCatalog _catalog = new();
    private readonly ProfileStore _profiles = new();
    private readonly QuickAccessStore _pins = new();
    private readonly DeckClient _client = new();
    private readonly GameLibrary _library = new();
    private readonly PackStore _store = new();
    private readonly ObservableCollection<ConnectionProfile> _targets = new();
    private readonly Stack<string> _back = new();
    private string _cwd = "/home/deck";
    private readonly TerminalDisplay _term = new();
    private bool _busy;
    private int _navGen;
    private bool _workOpen;
    private readonly List<PackHit> _storeQueue = new();
    private bool _drainingStoreQueue;
    private uint _termCols = 120;
    private uint _termRows = 32;
    private bool _termUi;

    public ObservableCollection<RemoteItem> Files { get; } = new();
    public ObservableCollection<GameEntry> Games { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = AppBrand.WindowTitle + " " + AppVersion.Label;
        VersionText.Text = AppVersion.Label;
        FileList.ItemsSource = Files;
        GameList.ItemsSource = Games;
        ListColumns.Attach(FileList, Files, properties: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = nameof(RemoteItem.Label),
            ["Size"] = nameof(RemoteItem.Size),
            ["Modified"] = nameof(RemoteItem.LastWrite)
        });
        ListColumns.Attach(GameList, Games);
        _profiles.Load(_catalog.Profiles);
        _pins.Load();
        RebuildTargets();
        BuildQuickAccess();
        LoadTerminalPref();
        ResetTerminal("Connect to the Steam Deck to run commands and scripts.");
        StorePanel.SetGames(_catalog.StoreGames, []);
        OptimizerPanel.Attach(_client, _catalog);
        OptimizerPanel.StatusChanged += SetStatus;
        AppsPanel.Attach(_client);
        AppsPanel.StatusChanged += SetStatus;
        AppsPanel.ManualChanged += OptimizerPanel.UpsertManual;
        AppsPanel.ManualRemoved += OptimizerPanel.RemoveManual;
        DashPanel.OpenTab += OpenDashboardTab;
        DashPanel.ScanRequested += () => _ = DashboardScanAsync();
        DashPanel.OptimizeRequested += () => _ = DashboardOptimizeAsync();
        StorePanel.InstallRequested += EnqueueStoreInstall;
        StorePanel.DeleteRequested += hit => _ = DeletePackAsync(hit);
        StorePanel.ToggleRequested += (hit, enabled) => _ = TogglePackAsync(hit, enabled);
        StorePanel.TargetResolver = PreviewPackPath;
        _client.ShellOutput += text => Dispatcher.BeginInvoke(() => AppendTerminal(text));
        Closed += (_, _) => _client.Dispose();
        Loaded += (_, _) => _ = StartupConnectAsync();
    }

    private async Task StartupConnectAsync()
    {
        if (HostEnvironment.LocalAvailable && !HostEnvironment.ForceRemote)
        {
            await ConnectToAsync(ConnectionProfile.LocalDeck(), quiet: true, trySiblings: false);
            return;
        }

        var ordered = new List<ConnectionProfile>();
        if (ProfileBox.SelectedItem is ConnectionProfile selected && !selected.IsLocal)
            ordered.Add(selected.Clone());
        foreach (var p in _profiles.Profiles.Where(p => !p.IsLocal))
        {
            if (ordered.Any(o => o.Id == p.Id)) continue;
            ordered.Add(p.Clone());
        }

        if (ordered.Count == 0) return;

        foreach (var profile in ordered)
        {
            FooterText.Text = "Trying " + profile.Name + " (" + profile.Host + ")…";
            ProfileBox.SelectedItem = _targets.FirstOrDefault(t => t.Id == profile.Id) ?? ProfileBox.SelectedItem;
            await ConnectToAsync(profile, quiet: true, trySiblings: false);
            if (_client.IsConnected) return;
        }

        FooterText.Text = "No session connected. Pick one and click Connect.";
        StatusText.Text = "Not connected";
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow { Owner = this };
        win.ShowDialog();
        OptimizerPanel.OnSettingsClosed(win.KeyChanged, win.LaunchersChanged);
    }

    private bool _terminalVisible = true;

    private void TermToggle_Click(object sender, RoutedEventArgs e) =>
        ApplyTerminalVisible(!_terminalVisible);

    private void LoadTerminalPref()
    {
        try
        {
            var path = TerminalPrefPath();
            if (File.Exists(path) &&
                string.Equals(File.ReadAllText(path).Trim(), "hidden", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTerminalVisible(false);
                return;
            }
        }
        catch
        {
            // standaard zichtbaar
        }
        ApplyTerminalVisible(true);
    }

    private void ApplyTerminalVisible(bool show)
    {
        _terminalVisible = show;
        TermRow.Height = show ? new GridLength(220) : new GridLength(0);
        TermSplitRow.Height = show ? new GridLength(6) : new GridLength(0);
        TermSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TermPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TermToggleBtn.Content = show ? "Hide terminal" : "Show terminal";
        if (show)
            Dispatcher.BeginInvoke(() => TermBox.Focus());
        try
        {
            var path = TerminalPrefPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, show ? "visible" : "hidden");
        }
        catch
        {
            // voorkeur is optioneel
        }
    }

    private static string TerminalPrefPath() => AppDataPaths.Combine("terminal.txt");

    private void BuildQuickAccess()
    {
        QuickTree.Items.Clear();
        var items = _pins.Combined(_catalog.QuickAccess).ToList();
        foreach (var sys in _library.Systems)
        {
            if (items.Any(p =>
                    string.Equals(p.Path.TrimEnd('/'), sys.Path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(p.Group, "ROMs", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(p.Name, sys.Label, StringComparison.OrdinalIgnoreCase))))
                continue;
            items.Add(new QuickPath { Name = sys.Label, Path = sys.Path, Group = "ROMs" });
        }
        foreach (var group in items.GroupBy(p => p.Group))
        {
            var node = new TreeViewItem { Header = group.Key, IsExpanded = true };
            foreach (var item in group)
            {
                var child = new TreeViewItem { Header = item.Name, Tag = item };
                if (IsEdenUsersEntry(item))
                {
                    foreach (var user in _library.Eden.Users)
                    {
                        child.Items.Add(new TreeViewItem
                        {
                            Header = user.Name,
                            Tag = new QuickPath { Name = user.Name, Path = user.Folder, Group = "Eden" }
                        });
                    }
                    child.IsExpanded = true;
                }
                node.Items.Add(child);
            }
            QuickTree.Items.Add(node);
        }
    }

    private bool IsEdenUsersEntry(QuickPath item) =>
        string.Equals(item.Path, _catalog.EdenUsersRoot, StringComparison.OrdinalIgnoreCase)
        || item.Name.Contains("Eden account", StringComparison.OrdinalIgnoreCase);

    private void RebuildTargets()
    {
        var selectedId = (ProfileBox.SelectedItem as ConnectionProfile)?.Id;
        _targets.Clear();
        if (HostEnvironment.LocalAvailable)
            _targets.Add(ConnectionProfile.LocalDeck());
        foreach (var p in _profiles.Profiles)
            _targets.Add(p);
        ProfileBox.ItemsSource = _targets;
        var match = _targets.FirstOrDefault(p => p.Id == selectedId);
        if (match is not null)
            ProfileBox.SelectedItem = match;
        else if (_targets.Count > 0)
            ProfileBox.SelectedIndex = 0;
    }

    private void Sessions_Click(object sender, RoutedEventArgs e)
    {
        var win = new SessionsWindow(_profiles) { Owner = this };
        var ok = win.ShowDialog() == true;
        RebuildTargets();
        if (win.ProfileToOpen is not null)
        {
            var match = _profiles.Profiles.FirstOrDefault(p => p.Id == win.ProfileToOpen.Id);
            if (match is not null)
                ProfileBox.SelectedItem = match;
        }
        if (ok && win.ProfileToOpen is not null)
            _ = ConnectToAsync(win.ProfileToOpen);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ConnectionProfile selected)
        {
            MessageBox.Show(this, "Create or pick a session first via Sessions…");
            return;
        }
        await ConnectToAsync(selected);
    }

    private async Task ConnectToAsync(ConnectionProfile selected, bool quiet = false, bool trySiblings = true)
    {
        if (_busy) return;
        var chosen = selected.Clone();
        var fallback = trySiblings
            ? _profiles.Profiles
                .Where(p => p.Id != chosen.Id && !p.IsLocal)
                .Select(p => p.Clone())
                .ToList()
            : [];
        TermHint.Text = "  ·  connecting…";
        ResetTerminal("");
        _busy = true;
        FooterText.Text = chosen.IsLocal ? "Connecting locally…" : "Connecting to " + chosen.Host + "…";
        try
        {
            if (chosen.IsLocal)
                await Task.Run(() => _client.ConnectLocal());
            else
                await Task.Run(() => ConnectOrWake(chosen, fallback));
        }
        catch (Exception ex)
        {
            if (!quiet)
                MessageBox.Show(ex.Message, AppBrand.ShortName);
            else
                FooterText.Text = chosen.Name + ": " + ex.Message;
        }
        finally
        {
            _busy = false;
        }

        if (!_client.IsConnected)
        {
            TermHint.Text = "  ·  click here and type a command";
            return;
        }

        DisconnectBtn.IsEnabled = true;
        ConnectBtn.IsEnabled = false;
        var p = _client.ActiveProfile!;
        StatusText.Text = _client.IsLocal
            ? "Local on this Steam Deck"
            : $"Connected to {p.Name} ({p.Host})";
        StatusText.Foreground = (Brush)FindResource("Ok");
        TermHint.Text = "  ·  click in the window and type  ·  Enter runs  ·  Ctrl+C stops";
        FooterText.Text = "Connected — opening folders…";

        // Keep SSH follow-up off the UI thread (RememberMac used to freeze for ~12–24s).
        var cols = _termCols;
        var rows = _termRows;
        _ = Task.Run(() =>
        {
            try { _client.ResizeShell(cols, rows); } catch { /* shell size is optional */ }
            try { RememberMac(); } catch { /* MAC learn is optional */ }
            try { LibraryLayout.Ensure(_client, _catalog); }
            catch { /* folders are created on first scan if this fails */ }
        });

        Navigate(_client.Home, push: false, showFilesTab: false);
        // Cache load can be heavy — do not block the connect handshake.
        _ = Dispatcher.BeginInvoke(() =>
        {
            OptimizerPanel.OnConnected();
            AppsPanel.OnConnected();
            RefreshDashboard();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ConnectOrWake(ConnectionProfile chosen, List<ConnectionProfile> fallback)
    {
        Exception? last = null;
        if (TryConnectOrWake(chosen, out last)) return;
        if (last is not null && !LooksLikeNetworkFailure(last))
            throw last;
        foreach (var other in fallback)
        {
            if (TryConnectOrWake(other, out last)) return;
            if (last is not null && !LooksLikeNetworkFailure(last))
                throw last;
        }
        throw last ?? new InvalidOperationException(
            "No SSH connection to the Steam Deck. Check host, port and key. If the Deck is asleep, fill in the MAC address under Sessions…");
    }

    private bool TryConnectOrWake(ConnectionProfile profile, out Exception? error)
    {
        error = null;
        Dispatcher.Invoke(() => FooterText.Text = "Connecting to " + profile.Host + "…");
        try
        {
            _client.Connect(profile);
            return true;
        }
        catch (Exception ex) when (!LooksLikeNetworkFailure(ex))
        {
            error = ex;
            return false;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        var mac = WakeOnLan.ResolveMac(profile);
        if (mac is null) return false;

        Dispatcher.Invoke(() => FooterText.Text = "No SSH reply — waking the Deck…");
        try { WakeOnLan.Send(mac, profile.Host); }
        catch { return false; }

        for (var i = 0; i < 20; i++)
        {
            if (i > 0) Thread.Sleep(1000);
            var n = i + 1;
            Dispatcher.Invoke(() => FooterText.Text = "Reconnecting… (" + n + ")");
            try
            {
                _client.Connect(profile);
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                if (!LooksLikeNetworkFailure(ex)) return false;
                if (n is 6 or 12)
                {
                    try { WakeOnLan.Send(mac, profile.Host); } catch { /* extra magic packet */ }
                }
            }
        }
        return false;
    }

    private static bool LooksLikeNetworkFailure(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SocketException or SshConnectionException or TimeoutException or SshOperationTimeoutException)
                return true;
            var msg = cur.Message ?? "";
            if (msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Connection reset", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("host is down", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("could not connect", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void RememberMac()
    {
        if (_client.IsLocal) return;
        try
        {
            var listing = _client.Execute(WakeOnLan.LearnMacScript(), 12);
            var mac = WakeOnLan.PickMac(listing);
            if (string.IsNullOrEmpty(mac) || _client.ActiveProfile is null) return;
            _client.ActiveProfile.MacAddress = mac;
            var stored = _profiles.Profiles.FirstOrDefault(p => p.Id == _client.ActiveProfile.Id) ??
                         _profiles.Profiles.FirstOrDefault(p =>
                             string.Equals(p.Host, _client.ActiveProfile.Host, StringComparison.OrdinalIgnoreCase));
            if (stored is not null)
            {
                stored.MacAddress = mac;
                _profiles.Save();
            }
            try { _client.Execute(WakeOnLan.EnableWowlanScript(), 12); }
            catch { /* WoL inschakelen is best-effort */ }
        }
        catch
        {
            /* MAC onthouden is optioneel */
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        OptimizerPanel.CancelBackgroundScan();
        AppsPanel.Clear();
        _client.Disconnect();
        Files.Clear();
        Games.Clear();
        _library.Eden.Users.Clear();
        BuildQuickAccess();
        DisconnectBtn.IsEnabled = false;
        ConnectBtn.IsEnabled = true;
        StatusText.Text = "Not connected";
        StatusText.Foreground = (Brush)FindResource("Muted");
        TermHint.Text = "  ·  click here and type a command";
        ResetTerminal("Disconnected.");
        RefreshDashboard();
    }

    private void QuickTree_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: QuickPath path })
            Navigate(path.Path);
    }

    private void QuickTree_RightDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item is not null) item.IsSelected = true;
    }

    private void QuickOpen_Click(object sender, RoutedEventArgs e)
    {
        if (QuickTree.SelectedItem is TreeViewItem { Tag: QuickPath path })
            Navigate(path.Path);
    }

    private void QuickUnpin_Click(object sender, RoutedEventArgs e)
    {
        if (QuickTree.SelectedItem is not TreeViewItem { Tag: QuickPath path }) return;
        if (!_pins.Contains(path.Path))
        {
            MessageBox.Show(this, "Only folders you added yourself can be unpinned.");
            return;
        }
        _pins.Remove(path.Path);
        BuildQuickAccess();
    }

    private void PinCurrent_Click(object sender, RoutedEventArgs e) => PinPath(_cwd, null);

    private void PinSelected_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is RemoteItem item)
            PinPath(item.IsDirectory ? item.FullPath : _cwd, item.IsDirectory ? item.Name : null);
        else
            PinPath(_cwd, null);
    }

    private void PinPath(string path, string? name)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var label = name ?? path.TrimEnd('/').Split('/').LastOrDefault() ?? path;
        var custom = Prompt("Quick access", "Name in the sidebar:", label);
        if (string.IsNullOrWhiteSpace(custom)) return;
        _pins.Add(custom.Trim(), path);
        BuildQuickAccess();
        FooterText.Text = $"Pinned: {custom}";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_back.Count == 0) return;
        Navigate(_back.Pop(), push: false);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Navigate(DeckClient.Parent(_cwd));
    private void Refresh_Click(object sender, RoutedEventArgs e) => Navigate(_cwd, push: false);
    private void Go_Click(object sender, RoutedEventArgs e) => Navigate(PathBox.Text);
    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Navigate(PathBox.Text);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or PasswordBox) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        if (e.Key == Key.F5)
        {
            if (MainTabs.SelectedIndex == TabGames) ScanGames_Click(sender, e);
            else if (MainTabs.SelectedIndex == TabOptimize) OptimizerPanel.StartBackgroundScan();
            else if (MainTabs.SelectedIndex == TabApps) _ = AppsPanel.ScanAsync();
            else if (MainTabs.SelectedIndex == TabDash) _ = DashboardScanAsync();
            else Refresh_Click(sender, e);
            e.Handled = true;
        }
        else if (alt && e.Key == Key.Left)
        {
            Back_Click(sender, e);
            e.Handled = true;
        }
        else if (alt && e.Key == Key.Up)
        {
            Up_Click(sender, e);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.L)
        {
            MainTabs.SelectedIndex = TabFiles;
            PathBox.Focus();
            PathBox.SelectAll();
            e.Handled = true;
        }
    }

    private void FileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or PasswordBox) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Enter:
                Open_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Back:
                Back_Click(sender, e);
                e.Handled = true;
                break;
            case Key.F2:
                Rename_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Delete:
                Delete_Click(sender, e);
                e.Handled = true;
                break;
            case Key.N when ctrl:
                NewFolder_Click(sender, e);
                e.Handled = true;
                break;
            case Key.D when ctrl:
                PinSelected_Click(sender, e);
                e.Handled = true;
                break;
            case Key.C when ctrl:
                CopyPath_Click(sender, e);
                e.Handled = true;
                break;
            case Key.S when ctrl:
                Download_Click(sender, e);
                e.Handled = true;
                break;
            case Key.A when ctrl:
                FileList.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void FileList_RightDown(object sender, MouseButtonEventArgs e) =>
        SelectRow<ListViewItem>(e.OriginalSource as DependencyObject);

    private void GameList_RightDown(object sender, MouseButtonEventArgs e) =>
        SelectRow<ListViewItem>(e.OriginalSource as DependencyObject);

    private static void SelectRow<T>(DependencyObject? source) where T : ListBoxItem
    {
        var row = FindParent<T>(source);
        if (row is not null) row.IsSelected = true;
    }

    private void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var scripts = FileList.SelectedItems.Cast<RemoteItem>().Where(IsDeckScript).ToList();
        RunOnDeckItem.IsEnabled = scripts.Count > 0;
        RunOnDeckItem.Header = scripts.Count > 1
            ? $"Run in terminal ({scripts.Count})"
            : "Run in terminal";
    }

    private void FileList_DoubleClick(object sender, RoutedEventArgs e) => Open_Click(sender, e);

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var items = FileList.SelectedItems.Cast<RemoteItem>().ToList();
        if (items.Count == 0) return;
        var folder = items.FirstOrDefault(i => i.IsDirectory);
        if (folder is not null)
        {
            Navigate(folder.FullPath);
            return;
        }

        var large = items.Where(i => i.Size > LocalOpen.OpenLimitBytes).ToList();
        var openable = items.Except(large).ToList();
        if (large.Count > 0)
        {
            var names = string.Join(", ", large.Select(i => i.Name));
            if (MessageBox.Show(this,
                    $"{names} is larger than 80 MB. Download to Downloads instead of opening locally?",
                    AppBrand.ShortName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await RunBusy("Downloading…", () =>
                {
                    var dest = DownloadsFolder();
                    foreach (var item in large)
                        _client.DownloadItem(item, dest, msg => Dispatcher.Invoke(() => FooterText.Text = msg));
                });
            }
        }

        if (openable.Count == 0) return;
        await RunBusy("Opening on this PC…", () =>
        {
            foreach (var item in openable)
                LocalOpen.DownloadAndOpen(_client, item);
        });
        FooterText.Text = openable.Count == 1
            ? $"Opened: {openable[0].Name}"
            : $"{openable.Count} files opened locally";
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("New folder", "Folder name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunBusy("Creating folder…", () => _client.CreateDirectory(DeckClient.Combine(_cwd, name.Trim())));
        Navigate(_cwd, push: false);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not RemoteItem item) return;
        var name = Prompt("Rename", "New name:", item.Name);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        await RunBusy("Renaming…", () => _client.Rename(item.FullPath, DeckClient.Combine(_cwd, name.Trim())));
        Navigate(_cwd, push: false);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var items = FileList.SelectedItems.Cast<RemoteItem>().ToList();
        if (items.Count == 0) return;
        var label = items.Count == 1 ? items[0].Name : $"{items.Count} items";
        if (MessageBox.Show($"Delete: {label}?", AppBrand.ShortName, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
            return;
        await RunBusy("Deleting…", () =>
        {
            foreach (var item in items)
                _client.Delete(item);
        });
        Navigate(_cwd, push: false);
    }

    private void Download_Click(object sender, RoutedEventArgs e) => _ = DownloadSelectedAsync();

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var paths = FileList.SelectedItems.Cast<RemoteItem>().Select(i => i.FullPath).ToList();
        if (paths.Count > 0)
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    private async Task DownloadSelectedAsync()
    {
        var items = FileList.SelectedItems.Cast<RemoteItem>().ToList();
        if (items.Count == 0) return;
        var dest = DownloadsFolder();
        await RunBusy("Downloading…", () =>
        {
            foreach (var item in items)
                _client.DownloadItem(item, dest, msg => Dispatcher.Invoke(() => FooterText.Text = msg));
        });
        FooterText.Text = $"Downloaded to {dest}";
    }

    private async void ScanGames_Click(object sender, RoutedEventArgs e) => await ScanGamesLibraryAsync();

    private void OpenRom_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is GameEntry { RomPath: var p } && !string.IsNullOrEmpty(p))
            Navigate(DeckClient.Parent(p));
    }

    private void OpenMods_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is GameEntry g)
            OpenGameFolder(g.ModPath ?? _catalog.EdenMods, create: true);
    }

    private void OpenSaves_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry g) return;
        OpenGameFolder(g.SavePath ?? _library.Eden.Primary?.Folder ?? _catalog.EdenUsersRoot, create: true);
    }

    private void OpenTextures_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is GameEntry g)
            OpenGameFolder(g.TexturePath, create: true);
    }

    private void MainTabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs.SelectedIndex == TabDash)
            RefreshDashboard();
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected)
        {
            MessageBox.Show(this, "Connect to the Steam Deck first.", "Add game");
            return;
        }

        var win = new ManualEntryWindow("Game", _client.IsLocal) { Owner = this };
        if (win.ShowDialog() != true) return;
        ManualShortcutStore.Upsert(win.Result);
        var game = ManualShortcutStore.ToGame(win.Result);
        var entry = ManualShortcutStore.ToLibraryEntry(win.Result);
        if (!Games.Any(g => string.Equals(g.RomPath, entry.RomPath, StringComparison.OrdinalIgnoreCase)))
            Games.Add(entry);
        OptimizerPanel.UpsertManual(game);
        FooterText.Text = entry.DisplayName + " added. It stays until you remove it.";
    }

    private void RemoveManualGame(GameEntry game)
    {
        if (!game.IsManual) return;
        if (!string.IsNullOrEmpty(game.ManualId))
        {
            ManualShortcutStore.Delete(game.ManualId);
            OptimizerPanel.RemoveManual(game.ManualId);
        }
        Games.Remove(game);
        FooterText.Text = game.DisplayName + " removed.";
    }

    private void SearchPacks_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = TabStore;
        if (GameList.SelectedItem is GameEntry g)
            StorePanel.Prefill(g.Identity);
    }

    private void GameList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry game)
        {
            e.Handled = true;
            return;
        }

        GameMenu.Items.Clear();
        AddMenu(GameMenu, "Open ROM folder", (_, _) =>
        {
            if (!string.IsNullOrEmpty(game.RomPath))
                Navigate(DeckClient.Parent(game.RomPath));
        }, enabled: !string.IsNullOrEmpty(game.RomPath));

        AddMenu(GameMenu, "Mods", (_, _) => OpenGameFolder(game.ModPath, create: true),
            enabled: !string.IsNullOrEmpty(game.ModPath));

        var saves = new MenuItem
        {
            Header = string.IsNullOrEmpty(game.SaveAccountName) ? "Saves" : $"Saves ({game.SaveAccountName})"
        };
        if (_library.Eden.Users.Count > 0 && string.Equals(game.System, "SWITCH", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var user in _library.Eden.Users)
            {
                var path = game.TitleId is null ? user.Folder : DeckClient.Combine(user.Folder, game.TitleId);
                var header = user == _library.Eden.Primary ? $"{user.Name} (default)" : user.Name;
                AddMenu(saves, header, (_, _) => OpenGameFolder(path, create: true));
            }
        }
        else
        {
            saves.Click += (_, _) => OpenGameFolder(game.SavePath, create: true);
            saves.IsEnabled = !string.IsNullOrEmpty(game.SavePath);
        }
        GameMenu.Items.Add(saves);

        AddMenu(GameMenu, "Texture packs", (_, _) => OpenGameFolder(game.TexturePath, create: true),
            enabled: !string.IsNullOrEmpty(game.TexturePath));
        GameMenu.Items.Add(new Separator());
        AddMenu(GameMenu, "Install mod…", InstallMod_Click);
        AddMenu(GameMenu, "Apply ROM hack…", ApplyRomHack_Click,
            enabled: !string.IsNullOrEmpty(game.RomPath));
        AddMenu(GameMenu, "To Dutch…", TranslateDutch_Click,
            enabled: !string.IsNullOrEmpty(game.RomPath) && CartRom.IsSupportedSystem(game.System));
        AddMenu(GameMenu, "Search packs…", (_, _) =>
        {
            MainTabs.SelectedIndex = TabStore;
            StorePanel.Prefill(game.Identity);
        });
        GameMenu.Items.Add(new Separator());
        AddMenu(GameMenu, "Pin ROM folder", (_, _) =>
        {
            if (!string.IsNullOrEmpty(game.RomPath))
                PinPath(DeckClient.Parent(game.RomPath), game.DisplayName);
        }, enabled: !string.IsNullOrEmpty(game.RomPath));
        if (game.IsManual)
        {
            GameMenu.Items.Add(new Separator());
            AddMenu(GameMenu, "Remove", (_, _) => RemoveManualGame(game));
        }
    }

    private static void AddMenu(ItemsControl parent, string header, RoutedEventHandler click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += click;
        parent.Items.Add(item);
    }

    private void OpenGameFolder(string? path, bool create = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "No folder known for this item.");
            return;
        }
        try
        {
            if (create && !_client.Exists(path))
                _client.EnsureDirectory(path);
            Navigate(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open folder");
        }
    }

    private async void InstallMod_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry game || string.IsNullOrEmpty(game.TitleId))
        {
            MessageBox.Show("Select a Switch game with a Title ID.");
            return;
        }
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Choose mod files or a folder zip" };
        if (dlg.ShowDialog() != true) return;
        var dest = game.ModPath ?? DeckClient.Combine(_catalog.EdenMods, game.TitleId);
        var remotes = new List<string>();
        await RunBusy("Installing mod…", () =>
        {
            _client.EnsureDirectory(dest);
            foreach (var file in dlg.FileNames)
            {
                var jobs = SwitchModLayout.Prepare(file, game.TitleId, Path.GetFileNameWithoutExtension(file));
                foreach (var job in jobs)
                {
                    var remote = DeckClient.Combine(dest, job.FolderName);
                    _client.EnsureDirectory(remote);
                    _client.UploadContents(job.LocalFolder, remote, (pct, msg) =>
                        Dispatcher.Invoke(() => FooterText.Text = $"{msg} ({pct:0}%)"));
                    remotes.Add(remote);
                }
            }
        });
        FooterText.Text = remotes.Count == 1
            ? "Mod placed in " + remotes[0]
            : $"{remotes.Count} mods geplaatst in {dest}";
        await ScanGamesLibraryAsync(overlay: false);
    }

    private async void TranslateDutch_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry game || string.IsNullOrWhiteSpace(game.RomPath))
        {
            MessageBox.Show(this,
                "Select an N64, NES or SNES game in the Games tab. SESAME only patches a copy of your own legal dump.",
                "Language patch");
            return;
        }

        if (!LanguagePatcher.Supports(game))
        {
            MessageBox.Show(this,
                "The Dutch text patch works for N64, NES and SNES ROMs. Select a game with a ROM file.",
                "Language patch");
            return;
        }

        var ok = MessageBox.Show(this,
            "A copy of " + game.FileName + " is fetched. The original file stays." +
            Environment.NewLine + Environment.NewLine +
            "In-game texts are then extracted and automatically translated to Dutch. " +
            "You can still review the translation before the new ROM is put on the Deck." +
            Environment.NewLine + Environment.NewLine +
            "Banjo-Kazooie uses the Rare dialogue table. Donkey Kong 64 uses its own text files. " +
            "Mario 64 uses the dialogue block with its own font. " +
            "Other dumps only via real English sentences. NES/SNES the same, if the text is ASCII in the ROM." +
            Environment.NewLine + Environment.NewLine +
            "SESAME does not ship ROMs; you use your own dump.",
            "To Dutch", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (ok != MessageBoxResult.Yes) return;

        var temp = Path.Combine(Path.GetTempPath(), AppBrand.ShortName, "lang", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var local = Path.Combine(temp, Path.GetFileName(game.RomPath));
        try
        {
            await RunBusy("Fetching ROM…", () =>
                _client.DownloadFile(game.RomPath, local, msg => Dispatcher.Invoke(() => FooterText.Text = msg)));

            byte[] rom;
            try { rom = LanguagePatcher.LoadRom(local, game.InnerFileName ?? game.DisplayName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Language patch");
                return;
            }

            var win = new LanguagePatchWindow(rom, game.DisplayName) { Owner = this };
            if (win.ShowDialog() != true || string.IsNullOrWhiteSpace(win.OutputPath))
                return;

            await RunBusy("Placing Dutch ROM on the Deck…", () =>
            {
                var folder = DeckClient.Parent(game.RomPath);
                var name = Path.GetFileName(win.OutputPath);
                var remote = DeckClient.Combine(folder, name);
                if (_client.Exists(remote) || string.Equals(remote, game.RomPath, StringComparison.OrdinalIgnoreCase))
                {
                    name = Path.GetFileNameWithoutExtension(name) + " " + DateTime.Now.ToString("HHmmss") +
                           Path.GetExtension(name);
                    remote = DeckClient.Combine(folder, name);
                }
                _client.EnsureDirectory(folder);
                _client.UploadFile(win.OutputPath, folder, msg => Dispatcher.Invoke(() => FooterText.Text = msg), name);
                RomHackLog.Remember(remote, game.DisplayName + " (NL)", game.FileName, "translation");
                Dispatcher.Invoke(() => FooterText.Text = "Dutch ROM ready: " + name);
            });
            await ScanGamesLibraryAsync(overlay: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Language patch");
        }
    }

    private async void ApplyRomHack_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry game || string.IsNullOrWhiteSpace(game.RomPath))
        {
            MessageBox.Show(this,
                "Select a game with a ROM file. SESAME only patches a copy of your own legal dump.",
                "ROM-hack");
            return;
        }

        var ok = MessageBox.Show(this,
            PackStore.LegalHackNl + Environment.NewLine + Environment.NewLine +
            "Then pick the patch (.bps / .ips / .ups or a zip). A copy of " +
            game.FileName + " is made; the original file stays.",
            "Apply ROM hack", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (ok != MessageBoxResult.Yes) return;

        var dlg = new OpenFileDialog
        {
            Title = "Choose ROM-hack patch (no ROM)",
            Filter = "Patches|*.bps;*.ips;*.ups;*.zip|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            await RunBusy("Applying ROM hack…", () =>
            {
                var installer = new RomHackInstaller();
                var remote = installer.InstallFromGame(game, dlg.FileName, _client,
                    msg => Dispatcher.Invoke(() => FooterText.Text = msg));
                Dispatcher.Invoke(() => FooterText.Text = "ROM hack placed as " + Path.GetFileName(remote));
            });
            await ScanGamesLibraryAsync(overlay: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "ROM-hack");
        }
    }

    private void EnqueueStoreInstall(PackHit hit)
    {
        if (hit.IsBusy || hit.IsQueued) return;
        if (!_client.IsConnected)
        {
            MessageBox.Show(this, "Connect to the Steam Deck first.", "Store");
            return;
        }

        _storeQueue.Add(hit);
        RefreshStoreQueue();
        FooterText.Text = _storeQueue.Count == 1
            ? "Queued: " + hit.Title
            : "Queued: " + _storeQueue.Count + " mods";
        _ = DrainStoreQueueAsync();
    }

    private void RefreshStoreQueue()
    {
        for (var i = 0; i < _storeQueue.Count; i++)
            _storeQueue[i].SetQueued(i + 1);
    }

    private async Task DrainStoreQueueAsync()
    {
        if (_drainingStoreQueue) return;
        _drainingStoreQueue = true;
        try
        {
            while (_storeQueue.Count > 0)
            {
                while (_busy)
                    await Task.Delay(200);
                var hit = _storeQueue[0];
                _storeQueue.RemoveAt(0);
                RefreshStoreQueue();
                await InstallPackAsync(hit, scanAfter: _storeQueue.Count == 0);
            }
        }
        finally
        {
            _drainingStoreQueue = false;
        }
    }

    private async Task InstallPackAsync(PackHit hit, bool scanAfter = true)
    {
        if (!_client.IsConnected)
        {
            MessageBox.Show(this, "Connect to the Steam Deck first.", "Store");
            return;
        }

        var storeGame = StorePanel.SelectedStoreGame;
        var system = ResolveSystem(storeGame, hit);
        if (string.IsNullOrWhiteSpace(hit.Platform))
            hit.Platform = system;
        if (string.IsNullOrWhiteSpace(hit.OriginalGame))
            hit.OriginalGame = hit.GameName;

        if (hit.IsRomHack)
        {
            await InstallRomHackAsync(hit);
            return;
        }

        if (PackStore.IsCartRomSystem(system) && hit.Section is not ("Saves" or "Texture packs"))
        {
            await InstallCartPackAsync(hit, storeGame, system, scanAfter);
            return;
        }

        string dest;
        try
        {
            dest = PackDestination(hit);
        }
        catch (Exception ex)
        {
            hit.SetFailed(ex.Message);
            MessageBox.Show(this, ex.Message, "Store");
            return;
        }

        var titleId = ResolveTitleId(storeGame, hit);
        var localDir = StorePanel.Mods.CacheDir(hit);
        var existing = StorePanel.Mods.Find(hit)?.LocalFile;

        try
        {
            await RunBusy("Downloading pack…", () =>
            {
                void Report(double pct, string msg, bool unknown = false) =>
                    Dispatcher.Invoke(() =>
                    {
                        hit.SetWork(msg, pct, unknown);
                        FooterText.Text = msg;
                    });

                try
                {
                    string file;
                    if (!string.IsNullOrWhiteSpace(existing) && File.Exists(existing) &&
                        new FileInfo(existing).Length > 0)
                    {
                        file = existing;
                        Report(12, "Using local file…");
                    }
                    else
                    {
                        file = _store.DownloadAsync(hit, localDir, default, (pct, msg, unk) =>
                            Report(Math.Min(70, pct * 0.7), msg, unk)).GetAwaiter().GetResult();
                        Dispatcher.Invoke(() =>
                        {
                            StorePanel.Mods.RecordDownload(hit, file, storeGame, titleId);
                            hit.SetDownloaded(file);
                        });
                    }

                    Report(72, "Preparing…", true);
                    var jobs = PlanInstall(hit, file, dest, system, titleId);
                    if (jobs.Count == 0)
                        throw new InvalidOperationException("No files to install.");

                    var remotes = new List<string>();
                    foreach (var job in jobs)
                    {
                        _client.EnsureDirectory(job.RemoteDir);
                        if (job.IsDirectory)
                            _client.UploadContents(job.LocalPath, job.RemoteDir, (pct, msg) =>
                                Report(72 + pct * 0.28, msg));
                        else
                            _client.UploadFile(job.LocalPath, job.RemoteDir, msg => Report(90, msg));
                        remotes.Add(job.RemoteDir);
                    }

                    var remote = remotes.Count == 1 ? remotes[0] : dest;
                    var folder = jobs.Count == 1 ? jobs[0].FolderName : null;
                    Dispatcher.Invoke(() =>
                    {
                        StorePanel.Mods.RecordInstall(hit, remote, storeGame, titleId, file, folder);
                        hit.SetInstalled(remote, file);
                        hit.TargetPath = remote;
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => hit.SetFailed(ex.Message));
                    throw;
                }
            });
            FooterText.Text = $"{hit.Kind} installed in {hit.RemotePath ?? dest}";
            if (scanAfter)
                await ScanGamesLibraryAsync(overlay: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Store");
        }
    }

    private async Task InstallCartPackAsync(PackHit hit, StoreGame storeGame, string system, bool scanAfter)
    {
        var localDir = StorePanel.Mods.CacheDir(hit);
        var existing = StorePanel.Mods.Find(hit)?.LocalFile;
        try
        {
            var file = "";
            await RunBusy("Downloading pack…", () =>
            {
                void Report(double pct, string msg, bool unknown = false) =>
                    Dispatcher.Invoke(() =>
                    {
                        hit.SetWork(msg, pct, unknown);
                        FooterText.Text = msg;
                    });

                if (!string.IsNullOrWhiteSpace(existing) && File.Exists(existing) &&
                    new FileInfo(existing).Length > 0)
                {
                    file = existing;
                    Report(20, "Using local file…");
                    return;
                }

                file = _store.DownloadAsync(hit, localDir, default, (pct, msg, unk) =>
                    Report(Math.Min(70, pct * 0.7), msg, unk)).GetAwaiter().GetResult();
                Dispatcher.Invoke(() =>
                {
                    StorePanel.Mods.RecordDownload(hit, file, storeGame, null);
                    hit.SetDownloaded(file);
                });
            });
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
                throw new InvalidOperationException("Download failed.");

            if (PackStore.FindPatchFile(file) is not null)
            {
                if (!hit.IsRomHack)
                {
                    var ok = MessageBox.Show(this,
                        PackStore.LegalHackNl + Environment.NewLine + Environment.NewLine +
                        "This is a ROM patch (.bps/.ips/.ups). The original stays; a copy is made with the patch applied.",
                        "ROM-hack", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (ok != MessageBoxResult.Yes)
                    {
                        hit.ClearWork();
                        return;
                    }
                    hit.Kind = "ROM-hack";
                    var roms = _catalog.RomFolderFor(system);
                    if (!string.IsNullOrEmpty(roms))
                        hit.TargetPath = roms;
                }
                await InstallRomHackAsync(hit, file);
                return;
            }

            string dest;
            try { dest = PackDestination(hit); }
            catch (Exception ex)
            {
                hit.SetFailed(ex.Message);
                MessageBox.Show(this, ex.Message, "Store");
                return;
            }

            await RunBusy("Mod plaatsen…", () =>
            {
                void Report(double pct, string msg, bool unknown = false) =>
                    Dispatcher.Invoke(() =>
                    {
                        hit.SetWork(msg, pct, unknown);
                        FooterText.Text = msg;
                    });

                Report(72, "Preparing…", true);
                var jobs = PlanInstall(hit, file, dest, system, null);
                if (jobs.Count == 0)
                    throw new InvalidOperationException("No files to install.");
                var remotes = new List<string>();
                foreach (var job in jobs)
                {
                    _client.EnsureDirectory(job.RemoteDir);
                    if (job.IsDirectory)
                        _client.UploadContents(job.LocalPath, job.RemoteDir, (pct, msg) =>
                            Report(72 + pct * 0.28, msg));
                    else
                        _client.UploadFile(job.LocalPath, job.RemoteDir, msg => Report(90, msg));
                    remotes.Add(job.RemoteDir);
                }
                var remote = remotes.Count == 1 ? remotes[0] : dest;
                Dispatcher.Invoke(() =>
                {
                    StorePanel.Mods.RecordInstall(hit, remote, storeGame, null, file, null);
                    hit.SetInstalled(remote, file);
                    hit.TargetPath = remote;
                });
            });
            FooterText.Text = $"{hit.Kind} installed in {hit.RemotePath ?? dest}";
            if (scanAfter)
                await ScanGamesLibraryAsync(overlay: false);
        }
        catch (Exception ex)
        {
            hit.SetFailed(ex.Message);
            MessageBox.Show(this, ex.Message, "Store");
        }
    }

    private async Task TogglePackAsync(PackHit hit, bool enabled)
    {
        if (!_client.IsConnected)
        {
            MessageBox.Show(this, "Connect to the Steam Deck first.", "Store");
            return;
        }
        if (hit.IsBusy || hit.IsQueued)
        {
            MessageBox.Show(this, "This mod is queued or currently installing.", "Store");
            return;
        }

        try
        {
            string? next = null;
            await RunBusy(enabled ? "Mod inschakelen…" : "Mod uitschakelen…", () =>
            {
                var current = ResolveInstalledModPath(hit)
                              ?? throw new InvalidOperationException("No installed folder found.");
                var leaf = Path.GetFileName(current.TrimEnd('/'));
                var newName = enabled
                    ? SwitchModFolders.EnabledName(leaf)
                    : SwitchModFolders.DisabledName(leaf);
                next = SwitchModFolders.Sibling(current, newName);
                if (!string.Equals(current, next, StringComparison.OrdinalIgnoreCase))
                {
                    if (_client.Exists(next))
                        throw new InvalidOperationException("Target folder already exists: " + newName);
                    _client.Rename(current, next);
                }
            });
            if (string.IsNullOrWhiteSpace(next)) return;
            StorePanel.Mods.RecordToggle(hit, next, enabled);
            hit.SetEnabled(enabled);
            hit.SetInstalled(next, hit.LocalFile);
            FooterText.Text = enabled
                ? hit.Title + " ingeschakeld"
                : hit.Title + " uitgeschakeld (disabled)";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Store");
        }
    }

    private async Task DeletePackAsync(PackHit hit)
    {
        if (hit.IsBusy)
        {
            MessageBox.Show(this, "Wait until the install finishes before deleting this mod.", "Store");
            return;
        }

        if (hit.IsQueued)
        {
            _storeQueue.Remove(hit);
            hit.ClearWork();
            RefreshStoreQueue();
        }

        var remote = hit.IsInstalled;
        var question = remote
            ? $"Delete '{hit.Title}' from the Deck and from the library?"
            : $"Delete the local download of '{hit.Title}'?";
        if (MessageBox.Show(this, question, "Delete mod",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            if (remote)
            {
                if (!_client.IsConnected)
                {
                    MessageBox.Show(this, "Connect to the Steam Deck first to delete the installed folder.",
                        "Store");
                    return;
                }

                await RunBusy("Deleting mod…", () =>
                {
                    var path = ResolveInstalledModPath(hit);
                    if (!string.IsNullOrWhiteSpace(path))
                        _client.DeletePath(path);
                });
            }

            StorePanel.Mods.DeleteLocalFiles(hit);
            StorePanel.Mods.Remove(hit);
            hit.ClearLocal();
            FooterText.Text = hit.Title + " verwijderd";
            if (remote)
                await ScanGamesLibraryAsync(overlay: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Store");
        }
    }

    private string? ResolveInstalledModPath(PackHit hit)
    {
        var path = hit.RemotePath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        var leaf = Path.GetFileName(path.TrimEnd('/').Replace('\\', '/'));
        if (!LooksLikeProgramId(leaf))
            return _client.Exists(path) ? path : null;

        var rec = StorePanel.Mods.Find(hit);
        var folder = !string.IsNullOrWhiteSpace(rec?.ModFolderName)
            ? rec!.ModFolderName
            : SwitchModLayout.FolderName(hit.Title);
        var enabled = DeckClient.Combine(path, SwitchModFolders.EnabledName(folder));
        var disabled = DeckClient.Combine(path, SwitchModFolders.DisabledName(folder));
        if (_client.Exists(enabled)) return enabled;
        if (_client.Exists(disabled)) return disabled;
        return null;
    }

    private static bool LooksLikeProgramId(string name) =>
        name.Length == 16 && name.StartsWith("01", StringComparison.OrdinalIgnoreCase);

    private readonly record struct PackInstallJob(string LocalPath, string RemoteDir, bool IsDirectory, string? FolderName);

    private List<PackInstallJob> PlanInstall(PackHit hit, string file, string destRoot, string system, string? titleId)
    {
        if (SwitchModLayout.IsSwitch(system) && hit.Section != "Saves")
        {
            if (string.IsNullOrWhiteSpace(titleId))
                throw new InvalidOperationException(
                    "Switch mods must live in load/<Title ID>/<modname>. Pick a Switch game with a Title ID.");
            return SwitchModLayout.Prepare(file, titleId, hit.Title)
                .Select(job => new PackInstallJob(
                    job.LocalFolder,
                    DeckClient.Combine(destRoot, job.FolderName),
                    true,
                    job.FolderName))
                .ToList();
        }

        var prepared = PackStore.PrepareUploadFolder(file, unwrapSingleRoot: hit.Section == "Saves");
        if (Directory.Exists(prepared))
            return [new PackInstallJob(prepared, destRoot, true, null)];
        return [new PackInstallJob(prepared, destRoot, false, null)];
    }

    private string? PreviewPackPath(PackHit hit)
    {
        try
        {
            var dest = PackDestination(hit);
            var storeGame = StorePanel.SelectedStoreGame;
            var system = ResolveSystem(storeGame, hit);
            if (string.IsNullOrEmpty(system))
                system = MatchLibraryGame(storeGame, hit)?.System ?? "";
            if (hit.IsRomHack)
                return dest;
            if (SwitchModLayout.IsSwitch(system) && hit.Section != "Saves")
                return DeckClient.Combine(dest, SwitchModLayout.FolderName(hit.Title));
            return dest;
        }
        catch
        {
            return null;
        }
    }

    private async Task InstallRomHackAsync(PackHit hit, string? existingPatch = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), AppBrand.ShortName, "romhack", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            if (string.IsNullOrWhiteSpace(hit.Platform))
                hit.Platform = ResolveSystem(StorePanel.SelectedStoreGame, hit);
            if (string.IsNullOrWhiteSpace(hit.OriginalGame))
                hit.OriginalGame = hit.GameName;
            if (string.IsNullOrWhiteSpace(hit.RequiredRomName))
                hit.RequiredRomName = hit.OriginalGame;
            hit.Kind = "ROM-hack";
            var roms = _catalog.RomFolderFor(hit.Platform);
            if (!string.IsNullOrEmpty(roms))
                hit.TargetPath = roms;

            string? patch = existingPatch;
            if (!string.IsNullOrWhiteSpace(patch) && File.Exists(patch))
            {
                // al gedownload
            }
            else
            try
            {
                await RunBusy("Patch downloaden…", () =>
                {
                    patch = _store.DownloadAsync(hit, temp, default, (pct, msg, unk) =>
                        Dispatcher.Invoke(() =>
                        {
                            hit.SetWork(msg, pct, unk);
                            FooterText.Text = msg;
                        })).GetAwaiter().GetResult();
                });
            }
            catch (Exception ex)
            {
                var pick = MessageBox.Show(this,
                    ex.Message + Environment.NewLine + Environment.NewLine +
                    "Do you want to pick a downloaded patch yourself (.bps/.ips/.ups or zip)?",
                    "ROM-hack", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (pick != MessageBoxResult.Yes) return;
                var dlg = new OpenFileDialog
                {
                    Title = "Choose patch (no ROM)",
                    Filter = "Patches|*.bps;*.ips;*.ups;*.zip|All files|*.*"
                };
                if (dlg.ShowDialog() != true) return;
                patch = dlg.FileName;
            }

            if (string.IsNullOrEmpty(patch)) return;
            var storeGame = StorePanel.SelectedStoreGame;
            var lib = MatchLibraryGame(storeGame, hit);
            await RunBusy("Installing ROM hack…", () =>
            {
                var installer = new RomHackInstaller();
                var remote = lib is not null && !string.IsNullOrWhiteSpace(lib.RomPath)
                    ? installer.InstallFromGame(lib, patch, _client,
                        msg => Dispatcher.Invoke(() =>
                        {
                            hit.SetWork(msg, 90, true);
                            FooterText.Text = msg;
                        }))
                    : installer.Install(hit, patch, _client, _catalog,
                        msg => Dispatcher.Invoke(() =>
                        {
                            hit.SetWork(msg, 90, true);
                            FooterText.Text = msg;
                        }));
                Dispatcher.Invoke(() =>
                {
                    StorePanel.Mods.RecordInstall(hit, remote, storeGame, null, patch, null);
                    hit.SetInstalled(remote, patch);
                    hit.TargetPath = remote;
                    FooterText.Text = "ROM hack placed as " + Path.GetFileName(remote);
                });
            });
            await ScanGamesLibraryAsync(overlay: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "ROM-hack");
        }
    }

    private string PackDestination(PackHit hit)
    {
        var storeGame = StorePanel.SelectedStoreGame;
        var game = MatchLibraryGame(storeGame, hit);
        var system = ResolveSystem(storeGame, hit);
        var titleId = ResolveTitleId(storeGame, hit);
        var name = !string.IsNullOrWhiteSpace(hit.GameName) ? hit.GameName
            : !string.IsNullOrWhiteSpace(storeGame.Name) && !storeGame.IsAll ? storeGame.Name
            : game?.DisplayName ?? "";

        if (hit.IsRomHack)
        {
            var roms = _catalog.RomFolderFor(system);
            if (!string.IsNullOrEmpty(roms))
                return roms;
        }

        if (hit.Section == "Saves")
        {
            var path = game?.SavePath ?? GameLibrary.SavePathFor(system, titleId, _library.Eden.Primary, _catalog);
            return path ?? throw new InvalidOperationException(
                "No save folder known. Connect to the Deck and pick a game with a Title ID or RetroArch path.");
        }

        if (hit.Section == "Texture packs" || PackStore.IsCartRomSystem(system))
        {
            var path = game?.TexturePath ?? GameLibrary.TexturePathFor(name, system, titleId, _catalog);
            if (!string.IsNullOrEmpty(path))
                return path;
            var key = PackStore.FoldRomFolderKey(system);
            if (_catalog.TextureRoots.TryGetValue(key, out var root) && !string.IsNullOrEmpty(root))
                return root;
            if (PackStore.IsCartRomSystem(system))
                throw new InvalidOperationException(
                    "No mod folder known for " + system +
                    ". ROM patches (.bps/.ips/.ups) are written as a new ROM in the ROM folder.");
            throw new InvalidOperationException("No texture folder known for this game.");
        }

        if (SwitchModLayout.IsSwitch(system) && !string.IsNullOrEmpty(titleId))
            return DeckClient.Combine(_catalog.EdenMods, titleId);
        if (!string.IsNullOrEmpty(game?.ModPath) &&
            (string.IsNullOrEmpty(titleId) ||
             game.ModPath.EndsWith(titleId, StringComparison.OrdinalIgnoreCase)))
            return game.ModPath;
        if (SwitchModLayout.IsSwitch(system) && !string.IsNullOrEmpty(titleId))
            return DeckClient.Combine(_catalog.EdenMods, titleId);
        throw new InvalidOperationException(
            SwitchModLayout.IsSwitch(system)
                ? "No mod folder known. In the Store pick a Switch game with a Program ID."
                : "No mod folder known for " + (string.IsNullOrWhiteSpace(system) ? "this game" : system) + ".");
    }

    private string? ResolveTitleId(StoreGame storeGame, PackHit hit)
    {
        var system = ResolveSystem(storeGame, hit);
        if (!SwitchModLayout.IsSwitch(system))
            return null;

        var lib = MatchLibraryGame(storeGame, hit);
        if (!string.IsNullOrWhiteSpace(lib?.TitleId))
            return lib.TitleId;
        if (!storeGame.IsAll && SwitchModLayout.IsSwitch(storeGame.System) &&
            !string.IsNullOrWhiteSpace(storeGame.TitleId))
            return storeGame.TitleId;
        var catalog = _catalog.StoreGames.FirstOrDefault(g =>
            !string.IsNullOrEmpty(g.TitleId) &&
            (g.SameIdentity(storeGame) || (!storeGame.IsAll && g.MatchesTitle(storeGame.Name))));
        return catalog?.TitleId;
    }

    private string ResolveSystem(StoreGame storeGame, PackHit hit) =>
        PackStore.ResolveSystem(hit, _catalog, storeGame);

    private GameEntry? MatchLibraryGame(StoreGame storeGame, PackHit hit)
    {
        var games = Games.ToList();
        if (!storeGame.IsAll)
        {
            var exact = games.FirstOrDefault(g => g.Identity.SameIdentity(storeGame));
            if (exact is not null) return exact;
        }

        var matches = new List<GameEntry>();
        if (hit.SourceGameId is int gid)
        {
            var catalog = _catalog.StoreGames.FirstOrDefault(g => g.GameBananaIds.Contains(gid));
            if (catalog is not null)
            {
                matches.AddRange(games.Where(g =>
                    catalog.MatchesTitle(StoreGame.StripVariant(g.DisplayName)) &&
                    catalog.MatchesSystem(g.System)));
            }
        }

        if (matches.Count == 0 && !string.IsNullOrWhiteSpace(hit.GameName))
        {
            matches.AddRange(games.Where(g =>
                g.Identity.MatchesTitle(hit.GameName) &&
                (string.IsNullOrWhiteSpace(hit.Platform) ||
                 string.Equals(g.System, hit.Platform, StringComparison.OrdinalIgnoreCase) ||
                 StoreGame.FoldSystem(g.System) == StoreGame.FoldSystem(hit.Platform))));
        }

        if (matches.Count == 0 && !storeGame.IsAll)
        {
            if (!string.IsNullOrEmpty(storeGame.TitleId))
            {
                matches.AddRange(games.Where(g =>
                    string.Equals(g.TitleId, storeGame.TitleId, StringComparison.OrdinalIgnoreCase)));
            }

            if (matches.Count == 0)
            {
                matches.AddRange(games.Where(g =>
                    storeGame.MatchesTitle(StoreGame.StripVariant(g.DisplayName)) &&
                    (string.IsNullOrEmpty(storeGame.System) ||
                     string.Equals(g.System, storeGame.System, StringComparison.OrdinalIgnoreCase))));
            }
        }

        if (!storeGame.IsAll && storeGame.IsTranslation)
            return PreferRom(matches.Where(g => g.IsTranslation));
        if (!storeGame.IsAll)
            return PreferRom(matches.Where(g => !g.IsTranslation));
        return PreferRom(matches);
    }

    private static GameEntry? PreferRom(IEnumerable<GameEntry> games)
    {
        var list = games.ToList();
        return list.FirstOrDefault(g => !g.IsTranslation && !g.IsRomHack)
               ?? list.FirstOrDefault(g => !g.IsTranslation)
               ?? list.FirstOrDefault();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Choose ROM, texture pack or mod" };
        if (dlg.ShowDialog() != true) return;
        await InstallLocalFiles(dlg.FileNames, _cwd);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = ((string[])e.Data.GetData(DataFormats.FileDrop)!)
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (paths.Length == 0) return;
        var dest = FolderAtDrop(e);
        await UploadLocalPaths(paths.Select(p => (p, dest)));
    }

    private string FolderAtDrop(DragEventArgs e)
    {
        try
        {
            var pos = e.GetPosition(FileList);
            if (pos.X >= 0 && pos.Y >= 0 && pos.X <= FileList.ActualWidth && pos.Y <= FileList.ActualHeight)
            {
                var hit = VisualTreeHelper.HitTest(FileList, pos);
                var row = FindParent<ListViewItem>(hit?.VisualHit);
                if (row?.DataContext is RemoteItem { IsDirectory: true } folder &&
                    !string.IsNullOrWhiteSpace(folder.FullPath))
                    return folder.FullPath;
            }
        }
        catch
        {
            // val terug op de geopende map
        }
        return _cwd;
    }

    private async Task InstallLocalFiles(IEnumerable<string> paths, string fallbackDir)
    {
        var jobs = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => (p, RouteDestination(p, fallbackDir)))
            .ToList();
        await UploadLocalPaths(jobs);
    }

    private async Task UploadLocalPaths(IEnumerable<(string path, string dest)> jobs)
    {
        var list = jobs.ToList();
        if (list.Count == 0) return;
        await RunBusy("Uploading…", () =>
        {
            foreach (var (path, dest) in list)
            {
                if (!_client.Exists(dest))
                {
                    try { _client.EnsureDirectory(dest); } catch { /* exists or parent missing */ }
                }
                if (Directory.Exists(path))
                    _client.UploadFolder(path, dest, msg => Dispatcher.Invoke(() => FooterText.Text = msg));
                else
                    _client.UploadFile(path, dest, msg => Dispatcher.Invoke(() => FooterText.Text = msg));
            }
        });
        FooterText.Text = "Upload done";
        Navigate(_cwd, push: false);
        await ScanGamesLibraryAsync(overlay: false);
    }

    private string RouteDestination(string localPath, string fallbackDir)
    {
        if (GameList.SelectedItem is GameEntry { TitleId: not null } game && MainTabs.SelectedIndex == TabGames)
            return game.ModPath ?? DeckClient.Combine(_catalog.EdenMods, game.TitleId);

        var ext = Path.GetExtension(localPath);
        if (!string.IsNullOrEmpty(ext) && _catalog.InstallRoutes.TryGetValue(ext, out var routed))
            return routed;

        if (Directory.Exists(localPath) && Path.GetFileName(localPath).Contains("SUPER MARIO 64", StringComparison.OrdinalIgnoreCase))
            return DeckClient.Combine("/home/deck/Emulation/bios/Mupen64plus/hires_texture", "SUPER MARIO 64");

        return fallbackDir;
    }

    private void TermBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            return;

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        if (ctrl && !alt && e.Key == Key.C && TermBox.SelectionLength > 0)
            return;
        if (ctrl && !alt && e.Key == Key.A)
            return;
        if (ctrl && !alt && e.Key == Key.V)
        {
            PasteToShell();
            e.Handled = true;
            return;
        }

        var seq = MapTerminalKey(e.Key, ctrl, alt);
        if (seq is null) return;
        _client.SendShell(seq);
        e.Handled = true;
    }

    private void TermBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        _client.SendShell(ToLinuxInput(e.Text));
        e.Handled = true;
    }

    private void TermBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        PasteToShell();
        e.CancelCommand();
    }

    private void TermBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var cols = (uint)Math.Max(20, (int)(TermBox.ActualWidth / 7.4));
        var rows = (uint)Math.Max(8, (int)(TermBox.ActualHeight / 17.5));
        if (cols == _termCols && rows == _termRows) return;
        _termCols = cols;
        _termRows = rows;
        if (_client.HasShell)
            _client.ResizeShell(cols, rows);
    }

    private void TermClear_Click(object sender, RoutedEventArgs e) => ResetTerminal("");

    private void TermInterrupt_Click(object sender, RoutedEventArgs e) => _client.SendShell("\u0003");

    private async void TermScript_Click(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected)
        {
            MessageBox.Show(this, "Connect to the Steam Deck first.");
            return;
        }
        var dlg = new OpenFileDialog
        {
            Title = "Script uitvoeren op de Steam Deck",
            Filter = "Scripts (*.sh;*.bash;*.py;*.ps1;*.zsh)|*.sh;*.bash;*.py;*.ps1;*.zsh;*.fish|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        var name = Path.GetFileName(dlg.FileName);
        var remote = "/tmp/sesame-" + DateTime.Now.ToString("HHmmss") + "-" + name;
        await RunBusy("Script uploaden…", () =>
            _client.UploadFile(dlg.FileName, "/tmp", remoteName: Path.GetFileName(remote)));
        if (!_client.IsConnected) return;

        ApplyTerminalVisible(true);
        TermBox.Focus();
        var quoted = DeckClient.ShQuote(remote);
        _client.SendShell(ScriptCommand(quoted, Path.GetExtension(name)) + "\r");
        FooterText.Text = "Script gestart in de terminal";
    }

    private void PasteToShell()
    {
        if (!Clipboard.ContainsText()) return;
        var text = ToLinuxInput(Clipboard.GetText().Replace("\r\n", "\n").Replace('\n', '\r'));
        if (text.Length > 0)
            _client.SendShell(text);
    }

    private void RunOnDeck_Click(object sender, RoutedEventArgs e)
    {
        var scripts = FileList.SelectedItems.Cast<RemoteItem>().Where(IsDeckScript).ToList();
        if (scripts.Count == 0) return;
        if (!_client.IsConnected)
        {
            MessageBox.Show(this, "Connect to the Steam Deck first.");
            return;
        }

        ApplyTerminalVisible(true);
        TermBox.Focus();
        foreach (var item in scripts)
        {
            var dir = DeckClient.ShQuote(DeckClient.Parent(item.FullPath));
            var file = DeckClient.ShQuote(item.FullPath);
            _client.SendShell("cd " + dir + " && " + ScriptCommand(file, Path.GetExtension(item.Name)) + "\r");
        }
        FooterText.Text = scripts.Count == 1
            ? "Script gestart: " + scripts[0].Name
            : scripts.Count + " scripts gestart in de terminal";
    }

    private static bool IsDeckScript(RemoteItem item)
    {
        if (item.IsDirectory) return false;
        return Path.GetExtension(item.Name).ToLowerInvariant() is
            ".sh" or ".bash" or ".zsh" or ".fish" or ".py" or ".ps1";
    }

    private static string ScriptCommand(string quotedPath, string? extension)
    {
        return extension?.ToLowerInvariant() switch
        {
            ".py" => "python3 " + quotedPath,
            ".ps1" => "(command -v pwsh >/dev/null && pwsh -NoLogo -File " + quotedPath +
                      ") || (command -v powershell >/dev/null && powershell -File " + quotedPath +
                      ") || echo 'PowerShell (pwsh) staat niet op de Deck.'",
            _ => "bash " + quotedPath
        };
    }

    private static string ToLinuxInput(string text) => text.Replace('\\', '/');

    private static string? MapTerminalKey(Key key, bool ctrl, bool alt)
    {
        if (alt) return null;
        if (ctrl)
        {
            return key switch
            {
                Key.C => "\u0003",
                Key.D => "\u0004",
                Key.Z => "\u001a",
                Key.L => "\u000c",
                Key.U => "\u0015",
                Key.W => "\u0017",
                Key.R => "\u0012",
                Key.A => "\u0001",
                Key.E => "\u0005",
                Key.K => "\u000b",
                Key.H => "\u0008",
                _ when key is >= Key.A and <= Key.Z =>
                    ((char)(key - Key.A + 1)).ToString(),
                _ => null
            };
        }

        return key switch
        {
            Key.Return or Key.Enter => "\r",
            Key.Back => "\u007f",
            Key.Tab => "\t",
            Key.Escape => "\u001b",
            Key.Up => "\u001b[A",
            Key.Down => "\u001b[B",
            Key.Right => "\u001b[C",
            Key.Left => "\u001b[D",
            Key.Home => "\u001b[H",
            Key.End => "\u001b[F",
            Key.Delete => "\u001b[3~",
            Key.Insert => "\u001b[2~",
            Key.PageUp => "\u001b[5~",
            Key.PageDown => "\u001b[6~",
            _ => null
        };
    }

    private void ResetTerminal(string message)
    {
        _term.Clear();
        if (!string.IsNullOrEmpty(message))
            _term.Write(message + "\n");
        RefreshTerminalBox();
    }

    private void AppendTerminal(string text)
    {
        _term.Write(text);
        RefreshTerminalBox();
    }

    private void TermBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_termUi) return;
        RefreshTerminalBox();
    }

    private void RefreshTerminalBox()
    {
        _termUi = true;
        try
        {
            TermBox.Text = _term.Text;
            TermBox.CaretIndex = TermBox.Text.Length;
            TermBox.ScrollToEnd();
        }
        finally
        {
            _termUi = false;
        }
    }

    private void ApplyDisplayAlias(RemoteItem item)
    {
        var id = GameLibrary.ExtractTitleId(item.Name);
        if (id is not null && _catalog.TitleIds.TryGetValue(id, out var friendly))
            item.DisplayName = friendly;
        else if (_library.Eden.Users.FirstOrDefault(u =>
                     string.Equals(u.Id, item.Name, StringComparison.OrdinalIgnoreCase)) is { } user)
            item.DisplayName = user.Name;
        else
            item.DisplayName = item.Name;
    }

    private void Navigate(string path, bool push = true, bool showFilesTab = true)
    {
        if (!_client.IsConnected || string.IsNullOrWhiteSpace(path)) return;
        var gen = ++_navGen;
        if (showFilesTab)
            MainTabs.SelectedIndex = TabFiles;
        FooterText.Text = "Opening " + path + "…";
        _ = NavigateAsync(path, push, gen);
    }

    private async Task NavigateAsync(string path, bool push, int gen)
    {
        try
        {
            var items = await Task.Run(() => _client.List(path));
            if (gen != _navGen) return;
            if (push && !string.Equals(_cwd, path, StringComparison.Ordinal))
                _back.Push(_cwd);
            _cwd = path;
            PathBox.Text = path;
            Files.Clear();
            foreach (var item in items)
            {
                ApplyDisplayAlias(item);
                Files.Add(item);
            }
            FooterText.Text = $"{items.Count} items in {path}";
            RefreshDashboard();
        }
        catch (Exception ex)
        {
            if (gen != _navGen) return;
            MessageBox.Show(ex.Message, "Could not open folder");
        }
    }

    private async Task ScanGamesLibraryAsync(bool overlay = true)
    {
        if (!_client.IsConnected) return;
        if (overlay) ShowWork("Scanning games…", "Reading ROM folders and mods on the Deck. This can take a moment.");
        else FooterText.Text = "Scanning games…";
        try
        {
            var catalog = _catalog;
            var client = _client;
            var (games, installed) = await Task.Run(() =>
            {
                var found = _library.Scan(client, catalog);
                var mods = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var game in found.Where(g =>
                             !string.IsNullOrEmpty(g.TitleId) && !string.IsNullOrEmpty(g.ModPath)))
                {
                    try
                    {
                        mods[game.TitleId!] = client.Exists(game.ModPath!)
                            ? client.List(game.ModPath!)
                                .Where(i => i.IsDirectory)
                                .Select(i => i.Name)
                                .ToList()
                            : [];
                    }
                    catch
                    {
                        // map blijft leeg tot de volgende scan
                    }
                }
                return (found, mods);
            });
            Games.Clear();
            foreach (var g in games) Games.Add(g);
            foreach (var item in ManualShortcutStore.Load()
                         .Where(x => x.AddedByUser &&
                                     x.Kind.Equals("Game", StringComparison.OrdinalIgnoreCase)))
            {
                var entry = ManualShortcutStore.ToLibraryEntry(item);
                if (!Games.Any(g => string.Equals(g.RomPath, entry.RomPath, StringComparison.OrdinalIgnoreCase)))
                    Games.Add(entry);
            }
            BuildQuickAccess();
            StorePanel.SetGames(_catalog.StoreGames, games.Select(x => x.Identity));
            StorePanel.RefreshLocalState(installed);
            FooterText.Text = $"Games: {Games.Count}";
            RefreshDashboard();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppBrand.ShortName);
        }
        finally
        {
            if (overlay) HideWork();
        }
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
        if (!_client.IsConnected)
        {
            MessageBox.Show(this, "Connect to the Steam Deck first.", "Scan");
            return;
        }
        if (_workOpen) return;
        ShowWork("Scanning apps…", "Looking up desktop entries and Flatpaks on the Deck.");
        try
        {
            await AppsPanel.ScanAsync(overlay: false);
            ShowWork("Scanning games…", "Reading ROM folders and mods on the Deck.");
            await ScanGamesLibraryAsync(overlay: false);
            ShowWork("Scanning library…", "Reading ROMs, Hydra games and apps for Optimize.");
            await OptimizerPanel.ScanLibraryAsync(overlay: false);
            FooterText.Text = $"Scan done · {AppsPanel.Count} apps · {Games.Count} games · {OptimizerPanel.Count} for Optimize";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Scan");
        }
        finally
        {
            HideWork();
            RefreshDashboard();
        }
    }

    private async Task DashboardOptimizeAsync()
    {
        if (!_client.IsConnected)
        {
            MessageBox.Show(this, "Connect to the Steam Deck first.", "Optimize");
            return;
        }
        if (OptimizerPanel.Count == 0)
        {
            ShowWork("Scanning library…", "Reading ROMs, Hydra games and apps on the Deck.");
            try { await OptimizerPanel.ScanLibraryAsync(overlay: false); }
            finally { HideWork(); }
            RefreshDashboard();
            if (OptimizerPanel.Count == 0)
            {
                MessageBox.Show(this, "No games found. Scan first.", "Optimize");
                return;
            }
        }
        MainTabs.SelectedIndex = TabOptimize;
        await OptimizerPanel.RunOptimizeInteractiveAsync(selectAllIfEmpty: true);
        RefreshDashboard();
    }

    private void RefreshDashboard()
    {
        DashPanel.UpdateStats(new DashboardStats
        {
            Connected = _client.IsConnected,
            FileCount = Files.Count,
            Folder = _cwd,
            AppCount = AppsPanel.Count,
            GameCount = Games.Count,
            OptimizeCount = OptimizerPanel.Count,
            InSteamCount = OptimizerPanel.InSteamCount,
            SelectedCount = OptimizerPanel.SelectedCount,
            StoreCount = _catalog.StoreGames.Count
        });
    }

    private void SetStatus(string text)
    {
        FooterText.Text = text;
        if (_workOpen)
            WorkDetail.Text = text;
    }

    private void ShowWork(string title, string detail)
    {
        _workOpen = true;
        WorkOverlay.Visibility = Visibility.Visible;
        WorkTitle.Text = title;
        WorkDetail.Text = detail;
        WorkBar.IsIndeterminate = true;
        FooterText.Text = title;
    }

    private void HideWork()
    {
        _workOpen = false;
        WorkOverlay.Visibility = Visibility.Collapsed;
    }

    private async Task RunBusy(string? status, Action work)
    {
        if (_busy)
        {
            MessageBox.Show(this, "Wait a moment, a task is already running.", AppBrand.ShortName);
            return;
        }
        _busy = true;
        if (status is not null) FooterText.Text = status;
        try
        {
            await Task.Run(work);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppBrand.ShortName);
        }
        finally
        {
            _busy = false;
        }
    }

    private static string DownloadsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static T? FindParent<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private string? Prompt(string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(12, 4, 12, 12) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
            Children = { ok, cancel }
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(12, 12, 12, 0) });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var win = new Window
        {
            Title = title,
            Width = 420,
            Height = 170,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Content = panel,
            Background = Background
        };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; win.DialogResult = true; };
        return win.ShowDialog() == true ? result : null;
    }
}
