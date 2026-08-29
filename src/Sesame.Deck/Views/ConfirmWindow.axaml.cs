using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sesame.Deck.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow() => InitializeComponent();

    public ConfirmWindow(string title, string message) : this()
    {
        Title = title;
        MessageText.Text = message;
    }

    public static async Task<bool> Ask(Window owner, string title, string message)
    {
        var dlg = new ConfirmWindow(title, message);
        return await dlg.ShowDialog<bool>(owner);
    }

    private void Yes_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void No_Click(object? sender, RoutedEventArgs e) => Close(false);
}
