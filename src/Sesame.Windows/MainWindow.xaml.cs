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

namespace Sesame;

public partial class MainWindow : Window
{
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
        _profiles.Load(_catalog.Profiles);
        _pins.Load();
        RebuildTargets();
        BuildQuickAccess();
        LoadTerminalPref();
        ResetTerminal("Verbind met de Steam Deck om commando's en scripts uit te voeren.");
        StorePanel.SetGames(_catalog.StoreGames, []);
        OptimizerPanel.Attach(_client, _catalog);
        OptimizerPanel.StatusChanged += text => FooterText.Text = text;
        StorePanel.InstallRequested += EnqueueStoreInstall;
        StorePanel.DeleteRequested += hit => _ = DeletePackAsync(hit);
        StorePanel.ToggleRequested += (hit, enabled) => _ = TogglePackAsync(hit, enabled);
        StorePanel.TargetResolver = PreviewPackPath;
        _client.ShellOutput += text => Dispatcher.BeginInvoke(() => AppendTerminal(text));
        Closed += (_, _) => _client.Dispose();
        Loaded += (_, _) =>
        {
            if (HostEnvironment.LocalAvailable && !HostEnvironment.ForceRemote)
                _ = ConnectToAsync(ConnectionProfile.LocalDeck());
        };
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
        TermToggleBtn.Content = show ? "Terminal verbergen" : "Terminal tonen";
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
            MessageBox.Show(this, "Maak of kies eerst een sessie via Sessies…");
            return;
        }
        await ConnectToAsync(selected);
    }

    private async Task ConnectToAsync(ConnectionProfile selected)
    {
        if (_busy) return;
        var chosen = selected.Clone();
        var fallback = _profiles.Profiles
            .Where(p => p.Id != chosen.Id)
            .Select(p => p.Clone())
            .ToList();
        TermHint.Text = "  ·  verbinden…";
        ResetTerminal("");
        _busy = true;
        FooterText.Text = chosen.IsLocal ? "Lokaal verbinden…" : "Verbinden…";
        try
        {
            if (chosen.IsLocal)
                await Task.Run(() => _client.ConnectLocal());
            else
                await Task.Run(() => ConnectOrWake(chosen, fallback));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppBrand.ShortName);
        }
        finally
        {
            _busy = false;
        }

        if (!_client.IsConnected)
        {
            TermHint.Text = "  ·  klik hier en typ een commando";
            return;
        }

        RememberMac();
        DisconnectBtn.IsEnabled = true;
        ConnectBtn.IsEnabled = false;
        var p = _client.ActiveProfile!;
        StatusText.Text = _client.IsLocal
            ? "Lokaal op deze Steam Deck"
            : $"Verbonden met {p.Name} ({p.Host})";
        StatusText.Foreground = (Brush)FindResource("Ok");
        _client.ResizeShell(_termCols, _termRows);
        TermHint.Text = "  ·  klik in het venster en typ  ·  Enter voert uit  ·  Ctrl+C stopt";
        Navigate(_client.Home, push: false);
        await ScanSilent();
        OptimizerPanel.OnConnected();
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
            "Geen SSH-verbinding met de Steam Deck. Controleer host, poort en sleutel. Als de Deck echt slaapt, vul het MAC-adres in bij Sessies…");
    }

    private bool TryConnectOrWake(ConnectionProfile profile, out Exception? error)
    {
        error = null;
        Dispatcher.Invoke(() => FooterText.Text = "Verbinden met " + profile.Host + "…");
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

        Dispatcher.Invoke(() => FooterText.Text = "Geen SSH-antwoord — Deck wekken…");
        try { WakeOnLan.Send(mac, profile.Host); }
        catch { return false; }

        for (var i = 0; i < 20; i++)
        {
            if (i > 0) Thread.Sleep(1000);
            var n = i + 1;
            Dispatcher.Invoke(() => FooterText.Text = "Opnieuw verbinden… (" + n + ")");
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
        _client.Disconnect();
        Files.Clear();
        Games.Clear();
        _library.Eden.Users.Clear();
        BuildQuickAccess();
        DisconnectBtn.IsEnabled = false;
        ConnectBtn.IsEnabled = true;
        StatusText.Text = "Niet verbonden";
        StatusText.Foreground = (Brush)FindResource("Muted");
        TermHint.Text = "  ·  klik hier en typ een commando";
        ResetTerminal("Verbinding verbroken.");
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
            MessageBox.Show(this, "Alleen zelf toegevoegde mappen kunnen losgemaakt worden.");
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
        var custom = Prompt("Snelle toegang", "Naam in de zijbalk:", label);
        if (string.IsNullOrWhiteSpace(custom)) return;
        _pins.Add(custom.Trim(), path);
        BuildQuickAccess();
        FooterText.Text = $"Vastgemaakt: {custom}";
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
            if (MainTabs.SelectedIndex == 1) ScanGames_Click(sender, e);
            else if (MainTabs.SelectedIndex == 2) OptimizerPanel.StartBackgroundScan();
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
            MainTabs.SelectedIndex = 0;
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
            ? $"Uitvoeren in terminal ({scripts.Count})"
            : "Uitvoeren in terminal";
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
                    $"{names} is groter dan 80 MB. Downloaden naar Downloads in plaats van lokaal openen?",
                    AppBrand.ShortName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await RunBusy("Downloaden…", () =>
                {
                    var dest = DownloadsFolder();
                    foreach (var item in large)
                        _client.DownloadItem(item, dest, msg => Dispatcher.Invoke(() => FooterText.Text = msg));
                });
            }
        }

        if (openable.Count == 0) return;
        await RunBusy("Openen op deze pc…", () =>
        {
            foreach (var item in openable)
                LocalOpen.DownloadAndOpen(_client, item);
        });
        FooterText.Text = openable.Count == 1
            ? $"Geopend: {openable[0].Name}"
            : $"{openable.Count} bestanden lokaal geopend";
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("Nieuwe map", "Naam van de map:");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunBusy("Map maken…", () => _client.CreateDirectory(DeckClient.Combine(_cwd, name.Trim())));
        Navigate(_cwd, push: false);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not RemoteItem item) return;
        var name = Prompt("Hernoemen", "Nieuwe naam:", item.Name);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        await RunBusy("Hernoemen…", () => _client.Rename(item.FullPath, DeckClient.Combine(_cwd, name.Trim())));
        Navigate(_cwd, push: false);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var items = FileList.SelectedItems.Cast<RemoteItem>().ToList();
        if (items.Count == 0) return;
        var label = items.Count == 1 ? items[0].Name : $"{items.Count} items";
        if (MessageBox.Show($"Verwijderen: {label}?", AppBrand.ShortName, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
            return;
        await RunBusy("Verwijderen…", () =>
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
        await RunBusy("Downloaden…", () =>
        {
            foreach (var item in items)
                _client.DownloadItem(item, dest, msg => Dispatcher.Invoke(() => FooterText.Text = msg));
        });
        FooterText.Text = $"Gedownload naar {dest}";
    }

    private async void ScanGames_Click(object sender, RoutedEventArgs e) => await ScanSilent();

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

    private void SearchPacks_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 2;
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
        AddMenu(GameMenu, "Open ROM-map", (_, _) =>
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
                var header = user == _library.Eden.Primary ? $"{user.Name} (standaard)" : user.Name;
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
        AddMenu(GameMenu, "Mod installeren…", InstallMod_Click);
        AddMenu(GameMenu, "ROM-hack toepassen…", ApplyRomHack_Click,
            enabled: !string.IsNullOrEmpty(game.RomPath));
        AddMenu(GameMenu, "Naar Nederlands vertalen…", TranslateDutch_Click,
            enabled: !string.IsNullOrEmpty(game.RomPath) && CartRom.IsSupportedSystem(game.System));
        AddMenu(GameMenu, "Packs zoeken…", (_, _) =>
        {
            MainTabs.SelectedIndex = 2;
            StorePanel.Prefill(game.Identity);
        });
        GameMenu.Items.Add(new Separator());
        AddMenu(GameMenu, "ROM-map vastmaken", (_, _) =>
        {
            if (!string.IsNullOrEmpty(game.RomPath))
                PinPath(DeckClient.Parent(game.RomPath), game.DisplayName);
        }, enabled: !string.IsNullOrEmpty(game.RomPath));
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
            MessageBox.Show(this, "Geen map bekend voor dit item.");
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
            MessageBox.Show(this, ex.Message, "Map openen mislukt");
        }
    }

    private async void InstallMod_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry game || string.IsNullOrEmpty(game.TitleId))
        {
            MessageBox.Show("Selecteer een Switch-game met Title ID.");
            return;
        }
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Mod-bestanden of map-zip kiezen" };
        if (dlg.ShowDialog() != true) return;
        var dest = game.ModPath ?? DeckClient.Combine(_catalog.EdenMods, game.TitleId);
        var remotes = new List<string>();
        await RunBusy("Mod installeren…", () =>
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
            ? "Mod geplaatst in " + remotes[0]
            : $"{remotes.Count} mods geplaatst in {dest}";
        await ScanSilent();
    }

    private async void TranslateDutch_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry game || string.IsNullOrWhiteSpace(game.RomPath))
        {
            MessageBox.Show(this,
                "Selecteer een N64-, NES- of SNES-game in de Games-tab. SESAME patched alleen een kopie van uw eigen legale dump.",
                "Taalpatch");
            return;
        }

        if (!LanguagePatcher.Supports(game))
        {
            MessageBox.Show(this,
                "De Nederlandse tekstpatch werkt voor N64-, NES- en SNES-ROMs. Selecteer een game met een ROM-bestand.",
                "Taalpatch");
            return;
        }

        var ok = MessageBox.Show(this,
            "Er wordt een kopie van " + game.FileName + " gehaald. Het origineel blijft staan." +
            Environment.NewLine + Environment.NewLine +
            "Daarna worden de in-game teksten uitgepakt en automatisch naar het Nederlands vertaald. " +
            "U kunt de vertaling nog controleren voordat de nieuwe ROM op de Deck wordt gezet." +
            Environment.NewLine + Environment.NewLine +
            "Banjo-Kazooie gebruikt de Rare-dialoogtabel. Donkey Kong 64 de eigen tekstbestanden. " +
            "Mario 64 het dialoogblok met eigen lettertype. " +
            "Andere dumps alleen via echte Engelse zinnen. NES/SNES hetzelfde, als de tekst als ASCII in de ROM staat." +
            Environment.NewLine + Environment.NewLine +
            "SESAME levert geen ROMs; u gebruikt uw eigen dump.",
            "Naar Nederlands vertalen", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (ok != MessageBoxResult.Yes) return;

        var temp = Path.Combine(Path.GetTempPath(), AppBrand.ShortName, "lang", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var local = Path.Combine(temp, Path.GetFileName(game.RomPath));
        try
        {
            await RunBusy("ROM ophalen…", () =>
                _client.DownloadFile(game.RomPath, local, msg => Dispatcher.Invoke(() => FooterText.Text = msg)));

            byte[] rom;
            try { rom = LanguagePatcher.LoadRom(local, game.InnerFileName ?? game.DisplayName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Taalpatch");
                return;
            }

            var win = new LanguagePatchWindow(rom, game.DisplayName) { Owner = this };
            if (win.ShowDialog() != true || string.IsNullOrWhiteSpace(win.OutputPath))
                return;

            await RunBusy("Nederlandse ROM plaatsen op de Deck…", () =>
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
                Dispatcher.Invoke(() => FooterText.Text = "Nederlandse ROM klaar: " + name);
            });
            await ScanSilent();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Taalpatch");
        }
    }

    private async void ApplyRomHack_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry game || string.IsNullOrWhiteSpace(game.RomPath))
        {
            MessageBox.Show(this,
                "Selecteer een game met een ROM-bestand. SESAME patched alleen een kopie van uw eigen legale dump.",
                "ROM-hack");
            return;
        }

        var ok = MessageBox.Show(this,
            PackStore.LegalHackNl + Environment.NewLine + Environment.NewLine +
            "Kies daarna de patch (.bps / .ips / .ups of een zip). Er wordt een kopie van " +
            game.FileName + " gemaakt; het origineel blijft staan.",
            "ROM-hack toepassen", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (ok != MessageBoxResult.Yes) return;

        var dlg = new OpenFileDialog
        {
            Title = "ROM-hack patch kiezen (geen ROM)",
            Filter = "Patches|*.bps;*.ips;*.ups;*.zip|Alle bestanden|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            await RunBusy("ROM-hack toepassen…", () =>
            {
                var installer = new RomHackInstaller();
                var remote = installer.InstallFromGame(game, dlg.FileName, _client,
                    msg => Dispatcher.Invoke(() => FooterText.Text = msg));
                Dispatcher.Invoke(() => FooterText.Text = "ROM-hack geplaatst als " + Path.GetFileName(remote));
            });
            await ScanSilent();
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
            MessageBox.Show(this, "Verbind eerst met de Steam Deck.", "Store");
            return;
        }

        _storeQueue.Add(hit);
        RefreshStoreQueue();
        FooterText.Text = _storeQueue.Count == 1
            ? "In wachtrij: " + hit.Title
            : "In wachtrij: " + _storeQueue.Count + " mods";
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
            MessageBox.Show(this, "Verbind eerst met de Steam Deck.", "Store");
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
            await RunBusy("Pack downloaden…", () =>
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
                        Report(12, "Lokaal bestand gebruiken…");
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

                    Report(72, "Voorbereiden…", true);
                    var jobs = PlanInstall(hit, file, dest, system, titleId);
                    if (jobs.Count == 0)
                        throw new InvalidOperationException("Geen bestanden om te installeren.");

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
            FooterText.Text = $"{hit.Kind} geïnstalleerd in {hit.RemotePath ?? dest}";
            if (scanAfter)
                await ScanSilent();
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
            await RunBusy("Pack downloaden…", () =>
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
                    Report(20, "Lokaal bestand gebruiken…");
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
                throw new InvalidOperationException("Download mislukt.");

            if (PackStore.FindPatchFile(file) is not null)
            {
                if (!hit.IsRomHack)
                {
                    var ok = MessageBox.Show(this,
                        PackStore.LegalHackNl + Environment.NewLine + Environment.NewLine +
                        "Dit is een ROM-patch (.bps/.ips/.ups). Het origineel blijft staan; er wordt een kopie gemaakt met de patch erin.",
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

                Report(72, "Voorbereiden…", true);
                var jobs = PlanInstall(hit, file, dest, system, null);
                if (jobs.Count == 0)
                    throw new InvalidOperationException("Geen bestanden om te installeren.");
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
            FooterText.Text = $"{hit.Kind} geïnstalleerd in {hit.RemotePath ?? dest}";
            if (scanAfter)
                await ScanSilent();
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
            MessageBox.Show(this, "Verbind eerst met de Steam Deck.", "Store");
            return;
        }
        if (hit.IsBusy || hit.IsQueued)
        {
            MessageBox.Show(this, "Deze mod staat in de wachtrij of wordt geïnstalleerd.", "Store");
            return;
        }

        try
        {
            string? next = null;
            await RunBusy(enabled ? "Mod inschakelen…" : "Mod uitschakelen…", () =>
            {
                var current = ResolveInstalledModPath(hit)
                              ?? throw new InvalidOperationException("Geen geïnstalleerde map gevonden.");
                var leaf = Path.GetFileName(current.TrimEnd('/'));
                var newName = enabled
                    ? SwitchModFolders.EnabledName(leaf)
                    : SwitchModFolders.DisabledName(leaf);
                next = SwitchModFolders.Sibling(current, newName);
                if (!string.Equals(current, next, StringComparison.OrdinalIgnoreCase))
                {
                    if (_client.Exists(next))
                        throw new InvalidOperationException("Doelmap bestaat al: " + newName);
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
            MessageBox.Show(this, "Wacht tot de installatie klaar is voordat je deze mod verwijdert.", "Store");
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
            ? $"'{hit.Title}' van de Deck en uit de bibliotheek verwijderen?"
            : $"Lokale download van '{hit.Title}' verwijderen?";
        if (MessageBox.Show(this, question, "Mod verwijderen",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            if (remote)
            {
                if (!_client.IsConnected)
                {
                    MessageBox.Show(this, "Verbind eerst met de Steam Deck om de geïnstalleerde map te verwijderen.",
                        "Store");
                    return;
                }

                await RunBusy("Mod verwijderen…", () =>
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
                await ScanSilent();
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
                    "Switch-mods moeten in load/<Title ID>/<modnaam> staan. Kies een Switch-game met Title ID.");
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
                    "Wilt u zelf een gedownloade patch (.bps/.ips/.ups of zip) kiezen?",
                    "ROM-hack", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (pick != MessageBoxResult.Yes) return;
                var dlg = new OpenFileDialog
                {
                    Title = "Patch kiezen (geen ROM)",
                    Filter = "Patches|*.bps;*.ips;*.ups;*.zip|Alle bestanden|*.*"
                };
                if (dlg.ShowDialog() != true) return;
                patch = dlg.FileName;
            }

            if (string.IsNullOrEmpty(patch)) return;
            var storeGame = StorePanel.SelectedStoreGame;
            var lib = MatchLibraryGame(storeGame, hit);
            await RunBusy("ROM-hack installeren…", () =>
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
                    FooterText.Text = "ROM-hack geplaatst als " + Path.GetFileName(remote);
                });
            });
            await ScanSilent();
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
                "Geen save-map bekend. Verbind met de Deck en kies een game met Title ID of RetroArch-pad.");
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
                    "Geen mod-map bekend voor " + system +
                    ". ROM-patches (.bps/.ips/.ups) worden als nieuwe ROM in de ROM-map gezet.");
            throw new InvalidOperationException("Geen texture-map bekend voor deze game.");
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
                ? "Geen mod-map bekend. Kies in de Store een Switch-game met Program ID."
                : "Geen mod-map bekend voor " + (string.IsNullOrWhiteSpace(system) ? "deze game" : system) + ".");
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
        var dlg = new OpenFileDialog { Multiselect = true, Title = "ROM, texture pack of mod kiezen" };
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
        await RunBusy("Uploaden…", () =>
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
        FooterText.Text = "Upload klaar";
        Navigate(_cwd, push: false);
        await ScanSilent();
    }

    private string RouteDestination(string localPath, string fallbackDir)
    {
        if (GameList.SelectedItem is GameEntry { TitleId: not null } game && MainTabs.SelectedIndex == 1)
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
            MessageBox.Show(this, "Verbind eerst met de Steam Deck.");
            return;
        }
        var dlg = new OpenFileDialog
        {
            Title = "Script uitvoeren op de Steam Deck",
            Filter = "Scripts (*.sh;*.bash;*.py;*.ps1;*.zsh)|*.sh;*.bash;*.py;*.ps1;*.zsh;*.fish|Alle bestanden (*.*)|*.*"
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
            MessageBox.Show(this, "Verbind eerst met de Steam Deck.");
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

    private void Navigate(string path, bool push = true)
    {
        if (!_client.IsConnected || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var items = _client.List(path);
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
            MainTabs.SelectedIndex = 0;
            FooterText.Text = $"{items.Count} items in {path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Map openen mislukt");
        }
    }

    private async Task ScanSilent()
    {
        if (!_client.IsConnected) return;
        await RunBusy("Games scannen…", () =>
        {
            var games = _library.Scan(_client, _catalog);
            var installed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in games.Where(g =>
                         !string.IsNullOrEmpty(g.TitleId) && !string.IsNullOrEmpty(g.ModPath)))
            {
                try
                {
                    installed[game.TitleId!] = _client.Exists(game.ModPath!)
                        ? _client.List(game.ModPath!)
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
            Dispatcher.Invoke(() =>
            {
                Games.Clear();
                foreach (var g in games) Games.Add(g);
                BuildQuickAccess();
                StorePanel.SetGames(_catalog.StoreGames, games.Select(x => x.Identity));
                StorePanel.RefreshLocalState(installed);
            });
        });
    }

    private async Task RunBusy(string? status, Action work)
    {
        if (_busy)
        {
            MessageBox.Show(this, "Even wachten, er loopt al een taak.", AppBrand.ShortName);
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
        var cancel = new Button { Content = "Annuleren", Width = 90, IsCancel = true };
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
