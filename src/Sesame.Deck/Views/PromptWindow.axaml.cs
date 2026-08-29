using Avalonia.Controls;
using Avalonia.Interactivity;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck.Views;

public partial class PromptWindow : Window
{
    public ManualShortcut Result { get; } = new();

    public PromptWindow() => InitializeComponent();

    public PromptWindow(string kind) : this()
    {
        var app = kind.Equals("App", StringComparison.OrdinalIgnoreCase);
        Title = app ? "Add app" : "Add game";
        HintText.Text = app
            ? "Name and a launch path are enough. The app stays in Apps and Artwork until you remove it."
            : "Name and a launch path are enough. The game stays in Games and Artwork until you remove it.";
        Result.Kind = app ? "App" : "Game";
        Result.AddedByUser = true;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        var exe = ExeBox.Text?.Trim().Trim('"') ?? "";
        if (name.Length == 0 || exe.Length == 0)
            return;
        Result.Name = name;
        Result.Exe = exe;
        Result.StartDir = Path.GetDirectoryName(exe.Replace('\\', '/').TrimEnd('/')) ?? "";
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
