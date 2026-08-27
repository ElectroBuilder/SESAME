using Avalonia;
using VisualSSH.Services;

namespace Sesame.Deck;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        HostEnvironment.ApplyArgs(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
