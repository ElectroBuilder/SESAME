using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Sesame.Deck.Views;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppDataPaths.EnsureProtected();
        OptimizerSettings.Load();
        LaunchConfigStore.Load();
        LibraryPaths.Load();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new ShellWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
