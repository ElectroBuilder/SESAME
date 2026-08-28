using System.IO;
using System.Windows;

namespace Sesame.Services;

public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    public static string Current { get; private set; } = Dark;

    public static void Apply(string theme)
    {
        var app = Application.Current;
        if (app is null) return;

        Current = theme == Light ? Light : Dark;
        var uri = new Uri($"Themes/{Current}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        var existing = app.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/Dark") == true
                              || d.Source?.OriginalString.Contains("Themes/Light") == true);
        if (existing is not null)
            app.Resources.MergedDictionaries.Remove(existing);
        app.Resources.MergedDictionaries.Insert(0, dict);
        Save();
    }

    public static void Toggle() => Apply(Current == Dark ? Light : Dark);

    public static void LoadSaved()
    {
        try
        {
            var path = SettingsPath();
            if (File.Exists(path))
            {
                var value = File.ReadAllText(path).Trim();
                Apply(value);
                return;
            }
        }
        catch
        {
            // keep default
        }
        Apply(Dark);
    }

    private static void Save()
    {
        try
        {
            var path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Current);
        }
        catch
        {
            // ignore
        }
    }

    private static string SettingsPath() =>
        AppDataPaths.Combine("theme.txt");
}
