using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sesame.Deck.Views;

public partial class AskWindow : Window
{
    public string? Value { get; private set; }

    public AskWindow() => InitializeComponent();

    public AskWindow(string title, string hint, string? initial = null) : this()
    {
        Title = title;
        HintText.Text = hint;
        ValueBox.Text = initial ?? "";
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        Value = ValueBox.Text?.Trim();
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
