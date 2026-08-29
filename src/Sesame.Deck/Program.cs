using Avalonia;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        HostEnvironment.ApplyArgs(args);
        if (HostEnvironment.RegisterSteamOnly)
        {
            Environment.ExitCode = RegisterSteam();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int RegisterSteam()
    {
        try
        {
            HostEnvironment.ForceLocal = true;
            using var client = new DeckClient();
            client.ConnectLocal();
            SteamSelfShortcut.Ensure(client);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
