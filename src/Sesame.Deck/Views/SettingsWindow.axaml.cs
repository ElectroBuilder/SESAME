using Avalonia.Controls;

namespace Sesame.Deck.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = Avalonia.Application.Current?.RequestedThemeVariant;
        Closed += (_, _) => Page.CommitLaunchers();
    }
}
