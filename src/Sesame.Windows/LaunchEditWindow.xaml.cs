using System.Windows;
using Sesame.Models;
using Sesame.Services.GameOptimizer;

namespace Sesame;

public partial class LaunchEditWindow : Window
{
    private readonly OptimizerGame _game;

    public LaunchEditWindow(OptimizerGame game)
    {
        InitializeComponent();
        _game = game;
        TargetBox.Text = game.Target;
        StartDirBox.Text = game.StartDir;
        OptionsBox.Text = game.LaunchOptions;
        LockBox.IsChecked = game.LaunchLocked;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var steam = LaunchComposer.ForSteam(
            TargetBox.Text?.Trim() ?? "",
            StartDirBox.Text?.Trim() ?? "",
            OptionsBox.Text?.Trim() ?? "");
        _game.Target = steam.Exe;
        _game.StartDir = steam.StartDir;
        _game.LaunchOptions = steam.LaunchOptions;
        _game.LaunchLocked = LockBox.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
