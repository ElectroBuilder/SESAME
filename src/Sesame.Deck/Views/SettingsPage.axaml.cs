using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            KeyBox.Text = OptimizerSettings.HasSteamGridDb ? "••••••••••••••••" : "";
            KeyStatus.Text = OptimizerSettings.HasSteamGridDb ? "Key is saved." : "No key yet.";
            HostInfo.Text = DeckSession.Current.Status + Environment.NewLine + HostEnvironment.RuntimeLabel;
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
            LibraryStatus.Text = "Saved. Connect to this Deck to create the empty folders.";
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

    private void Dark_Click(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = ThemeVariant.Dark;
        HostInfo.Text = "Dark theme.";
    }

    private void Light_Click(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = ThemeVariant.Light;
        HostInfo.Text = "Light theme. Chrome colors stay SESAME teal; content follows the system light variant.";
    }

    private void Desktop_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DeckSession.Current.Connected)
                DeckSession.Current.Client.Execute("steamos-session-select plasma", 15);
            HostInfo.Text = "SteamOS is switching to Desktop Mode…";
        }
        catch (Exception ex)
        {
            HostInfo.Text = ex.Message;
        }
    }
}
