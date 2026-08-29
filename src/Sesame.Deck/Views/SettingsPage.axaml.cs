using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sesame;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck.Views;

public partial class SettingsPage : UserControl
{
    private bool _loading;
    private AppRelease? _release;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => BindAll();
    }

    public void BindAll()
    {
        _loading = true;
        DarkThemeBox.IsChecked = DeckTheme.Current == DeckTheme.Dark;
        LightThemeBox.IsChecked = DeckTheme.Current == DeckTheme.Light;
        TabsPlatformBox.IsChecked = OptimizerSettings.SteamTabScheme == SteamTabScheme.Platform;
        TabsBrandBox.IsChecked = OptimizerSettings.SteamTabScheme == SteamTabScheme.Brand;
        TabsEmulationBox.IsChecked = OptimizerSettings.SteamTabScheme == SteamTabScheme.Emulation;
        MasksMasterBox.IsChecked = OptimizerSettings.UseMasks;
        MaskList.ItemsSource = OptimizerSettings.MaskOptions();
        KeyBox.Text = "";
        KeyStatus.Text = OptimizerSettings.HasSteamGridDb
            ? "A key is stored securely. Type something only to replace it."
            : "No key yet. Create one on steamgriddb.com and paste it here.";
        DataIntro.Text =
            "Everything lives in " + AppDataPaths.Root +
            ", readable only for this user. SSH and API keys are stored in secrets/ and never go in JSON. The library cache is per Steam Deck: a scan reads this machine live (ROMs, Hydra, apps).";
        BindLibrary();
        Launchers.Reload();
        UpdateVersion.Text = "Installed: " + AppVersion.Label;
        InstallUpdateBtn.IsEnabled = false;
        _loading = false;
    }

    public void CommitLaunchers() => Launchers.Commit();

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

    private void Theme_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        DeckTheme.Apply(LightThemeBox.IsChecked == true ? DeckTheme.Light : DeckTheme.Dark);
    }

    private void SteamTabs_Changed(object? sender, RoutedEventArgs e)
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

    private void MasksMaster_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        OptimizerSettings.UseMasks = MasksMasterBox.IsChecked == true;
        OptimizerSettings.Save();
    }

    private void MasksDefault_Click(object? sender, RoutedEventArgs e)
    {
        OptimizerSettings.ResetMaskDefaults();
        MaskList.ItemsSource = OptimizerSettings.MaskOptions();
    }

    private void MasksAllOn_Click(object? sender, RoutedEventArgs e) => SetAllMasks(true);

    private void MasksAllOff_Click(object? sender, RoutedEventArgs e) => SetAllMasks(false);

    private void SetAllMasks(bool enabled)
    {
        foreach (var row in OptimizerSettings.MaskOptions())
            OptimizerSettings.SetPlatformMask(row.Id, enabled, persist: false);
        OptimizerSettings.Save();
        MaskList.ItemsSource = OptimizerSettings.MaskOptions();
    }

    private async void ClearCaches_Click(object? sender, RoutedEventArgs e)
    {
        var owner = Owner();
        if (owner is null) return;
        if (!await ConfirmWindow.Ask(owner, "Clear caches",
                "Clear downloaded covers, store thumbnails and the library cache?\n\nSSH and API keys and your cover picks stay."))
            return;
        var n = AppDataPaths.ClearCaches();
        CacheStatus.Text = n == 0
            ? "No cache files found."
            : n + " cache files removed. On the next scan SESAME reads the Deck again.";
    }

    private async void SaveKey_Click(object? sender, RoutedEventArgs e)
    {
        var typed = KeyBox.Text?.Trim() ?? "";
        if (typed.Contains('•', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(typed))
        {
            KeyStatus.Text = OptimizerSettings.HasSteamGridDb
                ? "Type a new key to replace the stored secret."
                : "Paste a SteamGridDB key first.";
            return;
        }

        OptimizerSettings.SaveKey(typed);
        KeyBox.Text = "";
        KeyStatus.Text = "Checking SteamGridDB key…";
        try
        {
            var (ok, message) = await ArtworkClient.ValidateKeyAsync(OptimizerSettings.SteamGridDbKey, CancellationToken.None);
            KeyStatus.Text = message;
        }
        catch (Exception ex)
        {
            KeyStatus.Text = ex.Message;
        }
    }

    private async void ClearKey_Click(object? sender, RoutedEventArgs e)
    {
        var owner = Owner();
        if (owner is null) return;
        if (!OptimizerSettings.HasSteamGridDb && string.IsNullOrWhiteSpace(KeyBox.Text))
            return;
        if (!await ConfirmWindow.Ask(owner, "Settings", "Remove the saved SteamGridDB key?"))
            return;
        OptimizerSettings.ClearKey();
        KeyBox.Text = "";
        KeyStatus.Text = "No key yet. Create one on steamgriddb.com and paste it here.";
    }

    private void OpenKeyLink_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.steamgriddb.com/profile/preferences/api")
            {
                UseShellExecute = true
            });
        }
        catch { /* browser is optional */ }
    }

    private void SaveLibrary_Click(object? sender, RoutedEventArgs e)
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

        if (!DeckSession.Current.Connected)
        {
            LibraryStatus.Text = "Saved. Empty folders are created the next time SESAME connects.";
            return;
        }

        try
        {
            LibraryLayout.Ensure(DeckSession.Current.Client, DeckSession.Current.Catalog);
            LibraryStatus.Text = "Saved. Empty ROM, Hydra and Switch folders are ready.";
        }
        catch (Exception ex)
        {
            LibraryStatus.Text = "Saved, but folder create failed: " + ex.Message;
        }
    }

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
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

    private async void InstallUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_release is not { IsNewer: true }) return;
        var owner = Owner();
        if (owner is not null &&
            !await ConfirmWindow.Ask(owner, "Update SESAME",
                "Download SESAME " + _release.Version + " from GitHub, replace this install and restart?"))
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

    private void Desktop_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DeckSession.Current.Connected)
                DeckSession.Current.Client.Execute("steamos-session-select plasma", 15);
            KeyStatus.Text = "SteamOS is switching to Desktop Mode…";
        }
        catch (Exception ex)
        {
            KeyStatus.Text = ex.Message;
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        CommitLaunchers();
        if (TopLevel.GetTopLevel(this) is Window window)
            window.Close();
    }

    private Window? Owner() => TopLevel.GetTopLevel(this) as Window;
}
