using Avalonia.Controls;
using Avalonia.Interactivity;
using Sesame;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck.Views;

public partial class SettingsPage : UserControl
{
    private AppRelease? _release;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            KeyBox.Text = OptimizerSettings.HasSteamGridDb ? "••••••••••••••••" : "";
            KeyStatus.Text = OptimizerSettings.HasSteamGridDb ? "Key is saved." : "No key yet.";
            UpdateVersion.Text = "Installed: " + AppVersion.Label;
            BindLibrary();
        };
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

        if (!DeckSession.Current.Connected)
        {
            LibraryStatus.Text = "Saved. Folders are created after SESAME connects.";
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

    private void SaveKey_Click(object? sender, RoutedEventArgs e)
    {
        var value = KeyBox.Text?.Trim() ?? "";
        if (value.Contains('•', StringComparison.Ordinal)) return;
        OptimizerSettings.SaveKey(value);
        KeyStatus.Text = OptimizerSettings.HasSteamGridDb ? "Key saved." : "Key cleared.";
    }

    private void Dark_Click(object? sender, RoutedEventArgs e) => DeckTheme.Apply(DeckTheme.Dark);

    private void Light_Click(object? sender, RoutedEventArgs e) => DeckTheme.Apply(DeckTheme.Light);

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
            UpdateNotes.Text = string.IsNullOrWhiteSpace(_release.Notes)
                ? ""
                : _release.Notes;
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
            UpdateStatus.Text = "SteamOS is switching to Desktop Mode…";
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = ex.Message;
        }
    }
}
