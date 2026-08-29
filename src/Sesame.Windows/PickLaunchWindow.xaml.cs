using System.Windows;
using System.Windows.Input;
using Sesame.Models;
using Sesame.Services.GameOptimizer;

namespace Sesame;

public partial class PickLaunchWindow : Window
{
    public LaunchChoice? Chosen { get; private set; }

    public PickLaunchWindow(OptimizerGame game)
    {
        InitializeComponent();
        HintText.Text = "Several launches were found for " + game.DisplayName +
                         ". Pick the shortcut SESAME should keep.";
        ChoiceList.ItemsSource = game.LaunchChoices;
        var current = game.LaunchChoices.FirstOrDefault(c =>
            string.Equals(c.Key, game.ChosenLaunch, StringComparison.OrdinalIgnoreCase))
                      ?? game.LaunchChoices.FirstOrDefault(c =>
                          LaunchChoice.KeyOf(game.Target, game.LaunchOptions) == c.Key);
        ChoiceList.SelectedItem = current ?? game.LaunchChoices.FirstOrDefault();
    }

    private void Choice_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChoiceList.SelectedItem is LaunchChoice)
            Save_Click(sender, e);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ChoiceList.SelectedItem is not LaunchChoice choice)
        {
            MessageBox.Show(this, "Select a launch first.", Title);
            return;
        }

        Chosen = choice;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
