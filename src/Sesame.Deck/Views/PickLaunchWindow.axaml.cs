using Avalonia.Controls;
using Avalonia.Interactivity;
using Sesame.Models;

namespace Sesame.Deck.Views;

public partial class PickLaunchWindow : Window
{
    public LaunchChoice? Chosen { get; private set; }

    public PickLaunchWindow() => InitializeComponent();

    public PickLaunchWindow(IReadOnlyList<LaunchChoice> choices) : this()
    {
        ChoiceList.ItemsSource = choices;
        ChoiceList.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(LaunchChoice.Label));
        if (choices.Count > 0)
            ChoiceList.SelectedIndex = 0;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        Chosen = ChoiceList.SelectedItem as LaunchChoice;
        Close(Chosen is not null);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
