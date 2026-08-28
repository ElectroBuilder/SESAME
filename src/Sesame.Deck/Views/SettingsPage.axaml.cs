using Avalonia.Controls;
using Avalonia.Interactivity;
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
        };
    }

    private void SaveKey_Click(object? sender, RoutedEventArgs e)
    {
        var value = KeyBox.Text?.Trim() ?? "";
        if (value.Contains('•', StringComparison.Ordinal)) return;
        OptimizerSettings.SaveKey(value);
        KeyStatus.Text = OptimizerSettings.HasSteamGridDb ? "Key saved." : "Key cleared.";
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
