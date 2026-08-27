using Avalonia.Controls;
using Avalonia.Interactivity;
using VisualSSH.Services;
using VisualSSH.Services.GameOptimizer;

namespace Sesame.Deck.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            KeyBox.Text = OptimizerSettings.HasSteamGridDb ? "••••••••••••••••" : "";
            KeyStatus.Text = OptimizerSettings.HasSteamGridDb ? "Sleutel is opgeslagen." : "Nog geen sleutel.";
            HostInfo.Text = DeckSession.Current.Status + Environment.NewLine + HostEnvironment.RuntimeLabel;
        };
    }

    private void SaveKey_Click(object? sender, RoutedEventArgs e)
    {
        var value = KeyBox.Text?.Trim() ?? "";
        if (value.Contains('•', StringComparison.Ordinal)) return;
        OptimizerSettings.SaveKey(value);
        KeyStatus.Text = OptimizerSettings.HasSteamGridDb ? "Sleutel opgeslagen." : "Sleutel gewist.";
    }

    private void Desktop_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DeckSession.Current.Connected)
                DeckSession.Current.Client.Execute("steamos-session-select plasma", 15);
            HostInfo.Text = "SteamOS schakelt naar Desktop Mode…";
        }
        catch (Exception ex)
        {
            HostInfo.Text = ex.Message;
        }
    }
}
