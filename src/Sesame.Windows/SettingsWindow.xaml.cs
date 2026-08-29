using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame;

public partial class SettingsWindow : Window
{
    private bool _loading;

    public SettingsWindow(DeckClient? client = null)
    {
        InitializeComponent();
        _loading = true;
        DarkThemeBox.IsChecked = ThemeManager.Current == ThemeManager.Dark;
        LightThemeBox.IsChecked = ThemeManager.Current == ThemeManager.Light;
        TabsPlatformBox.IsChecked = OptimizerSettings.SteamTabScheme == SteamTabScheme.Platform;
        TabsBrandBox.IsChecked = OptimizerSettings.SteamTabScheme == SteamTabScheme.Brand;
        TabsEmulationBox.IsChecked = OptimizerSettings.SteamTabScheme == SteamTabScheme.Emulation;
        MasksMasterBox.IsChecked = OptimizerSettings.UseMasks;
        BindMasks();
        RefreshKeyStatus();
        BindLibrary();
        Launchers.Attach(client);
        UpdateVersion.Text = "Installed: " + AppVersion.Label;
        InstallUpdateBtn.IsEnabled = false;
        _loading = false;
        Closing += Settings_Closing;
    }

    private AppRelease? _release;

    public bool KeyChanged { get; private set; }
    public bool LaunchersChanged { get; private set; }
    public bool MasksChanged { get; private set; }

    private void BindMasks() => MaskList.ItemsSource = OptimizerSettings.MaskOptions();

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ThemeManager.Apply(LightThemeBox.IsChecked == true ? ThemeManager.Light : ThemeManager.Dark);
    }

    private void SteamTabs_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (TabsBrandBox.IsChecked == true)
            OptimizerSettings.SteamTabScheme = SteamTabScheme.Brand;
        else if (TabsEmulationBox.IsChecked == true)
            OptimizerSettings.SteamTabScheme = SteamTabScheme.Emulation;
        else
            OptimizerSettings.SteamTabScheme = SteamTabScheme.Platform;
        OptimizerSettings.Save();
    }

    private void MasksMaster_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        OptimizerSettings.UseMasks = MasksMasterBox.IsChecked == true;
        OptimizerSettings.Save();
        MasksChanged = true;
    }

    private void MasksDefault_Click(object sender, RoutedEventArgs e)
    {
        OptimizerSettings.ResetMaskDefaults();
        BindMasks();
        MasksChanged = true;
    }

    private void MasksAllOn_Click(object sender, RoutedEventArgs e) => SetAllMasks(true);

    private void MasksAllOff_Click(object sender, RoutedEventArgs e) => SetAllMasks(false);

    private void SetAllMasks(bool enabled)
    {
        foreach (var row in OptimizerSettings.MaskOptions())
            OptimizerSettings.SetPlatformMask(row.Id, enabled, persist: false);
        OptimizerSettings.Save();
        BindMasks();
        MasksChanged = true;
    }

    private void ClearCaches_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Clear downloaded covers, store thumbnails and the library cache?\n\nSSH and API keys and your cover picks stay.",
                "Clear caches", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var n = AppDataPaths.ClearCaches();
        CacheStatus.Text = n == 0
            ? "No cache files found."
            : n + " cache files removed. On the next connection SESAME scans the Deck again.";
    }

    private async void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var typed = KeyBox.Password ?? "";
        if (string.IsNullOrWhiteSpace(typed))
        {
            KeyStatus.Text = OptimizerSettings.HasSteamGridDb
                ? "Type a new key to replace the stored secret."
                : "Paste a SteamGridDB key first.";
            return;
        }

        OptimizerSettings.SaveKey(typed);
        KeyBox.Clear();
        KeyChanged = true;
        KeyStatus.Text = "Checking SteamGridDB key…";
        try
        {
            var (ok, message) = await ArtworkClient.ValidateKeyAsync(OptimizerSettings.SteamGridDbKey, CancellationToken.None);
            KeyStatus.Text = message;
            if (!ok)
                MessageBox.Show(this, message, "SteamGridDB");
        }
        catch (Exception ex)
        {
            KeyStatus.Text = ex.Message;
        }
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        if (!OptimizerSettings.HasSteamGridDb && string.IsNullOrEmpty(KeyBox.Password))
            return;
        if (MessageBox.Show(this, "Remove the saved SteamGridDB key?", "Settings",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        OptimizerSettings.ClearKey();
        KeyBox.Clear();
        KeyChanged = true;
        RefreshKeyStatus();
    }

    private void RefreshKeyStatus()
    {
        KeyStatus.Text = OptimizerSettings.HasSteamGridDb
            ? "A key is stored securely. Type something only to replace it."
            : "No key yet. Create one on steamgriddb.com and paste it here.";
    }

    private void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Settings_Closing(object? sender, CancelEventArgs e)
    {
        Launchers.Commit();
        LaunchersChanged = true;
    }

    private void BindLibrary()
    {
        var paths = LibraryPaths.Current;
        RomsRootBox.Text = paths.RomsRoot;
        HydraRootBox.Text = paths.HydraRoot;
        LutrisRootBox.Text = paths.LutrisRoot;
        OtherGamesBox.Text = paths.OtherGamesRoot;
        UseEdenBox.IsChecked = paths.UseEden;
        UseYuzuBox.IsChecked = paths.UseYuzu;
        UseRyujinxBox.IsChecked = paths.UseRyujinx;
        UseCitronBox.IsChecked = paths.UseCitron;
    }

    private void SaveLibrary_Click(object sender, RoutedEventArgs e)
    {
        LibraryPaths.Current.RomsRoot = RomsRootBox.Text?.Trim() ?? "";
        LibraryPaths.Current.HydraRoot = HydraRootBox.Text?.Trim() ?? "";
        LibraryPaths.Current.LutrisRoot = LutrisRootBox.Text?.Trim() ?? "";
        LibraryPaths.Current.OtherGamesRoot = OtherGamesBox.Text?.Trim() ?? "";
        LibraryPaths.Current.UseEden = UseEdenBox.IsChecked == true;
        LibraryPaths.Current.UseYuzu = UseYuzuBox.IsChecked == true;
        LibraryPaths.Current.UseRyujinx = UseRyujinxBox.IsChecked == true;
        LibraryPaths.Current.UseCitron = UseCitronBox.IsChecked == true;
        LibraryPaths.Save();
        Launchers.Reload();
        LibraryStatus.Text = "Saved. Empty folders are created the next time you connect to the Deck.";
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatus.Text = "Checking GitHub…";
        InstallUpdateBtn.IsEnabled = false;
        try
        {
            _release = await AppUpdate.CheckAsync();
            if (_release is null)
            {
                UpdateStatus.Text = "No versioned GitHub release found yet.";
                return;
            }

            UpdateVersion.Text = "Installed: " + AppVersion.Label + "   Latest: v" + _release.Version;
            UpdateNotes.Text = _release.Notes;
            if (_release.IsNewer)
            {
                InstallUpdateBtn.IsEnabled = true;
                UpdateStatus.Text = "SESAME " + _release.Version + " is ready to install.";
            }
            else
                UpdateStatus.Text = "You already have the latest version.";
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = ex.Message;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_release is not { IsNewer: true }) return;
        if (MessageBox.Show(this,
                "Download SESAME " + _release.Version + " from GitHub, replace this install and restart?",
                "Update SESAME", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        InstallUpdateBtn.IsEnabled = false;
        var progress = new Progress<string>(text => UpdateStatus.Text = text);
        try
        {
            await AppUpdate.ApplyAsync(_release, progress);
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = ex.Message;
            InstallUpdateBtn.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
