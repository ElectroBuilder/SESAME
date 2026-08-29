using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Sesame.Services;

namespace Sesame.Deck;

public static class DeckTheme
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public static string Current { get; private set; } = Dark;

    public static void LoadSaved()
    {
        try
        {
            var path = AppDataPaths.Combine("theme.txt");
            if (File.Exists(path))
            {
                Apply(File.ReadAllText(path).Trim());
                return;
            }
        }
        catch { /* keep default */ }
        Apply(Dark);
    }

    public static void Apply(string theme)
    {
        var app = Application.Current;
        if (app is null) return;
        Current = theme.Equals(Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
        app.RequestedThemeVariant = Current == Light ? ThemeVariant.Light : ThemeVariant.Dark;

        var dict = (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri($"avares://SESAME/Themes/{Current}.axaml"));
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(dict);

        if (app.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
                window.RequestedThemeVariant = app.RequestedThemeVariant;
        }

        try
        {
            var path = AppDataPaths.Combine("theme.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Current);
        }
        catch { /* theme file is optional */ }
    }
}
