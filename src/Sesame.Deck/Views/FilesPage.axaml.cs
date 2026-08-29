using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sesame.Models;
using Sesame.Services;

namespace Sesame.Deck.Views;

public partial class FilesPage : UserControl
{
    private string _cwd = "/home/deck";
    private readonly Stack<string> _back = new();

    public event Action<string>? PathChanged;

    public FilesPage() => InitializeComponent();

    public void OnConnected()
    {
        _cwd = DeckSession.Current.Client.Home;
        Navigate(_cwd, push: false);
    }

    public void OpenPath(string path) => Navigate(path);

    private void Refresh_Click(object? sender, RoutedEventArgs e) => Navigate(_cwd, push: false);
    private void Up_Click(object? sender, RoutedEventArgs e) => Navigate(DeckClient.Parent(_cwd));
    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (_back.Count == 0) return;
        Navigate(_back.Pop(), push: false);
    }
    private void Go_Click(object? sender, RoutedEventArgs e) => Navigate(PathBox.Text ?? _cwd);

    private void Path_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Navigate(PathBox.Text ?? _cwd);
    }

    private void Open_Double(object? sender, TappedEventArgs e)
    {
        if (FileList.SelectedItem is RemoteItem { IsDirectory: true } item)
            Navigate(item.FullPath);
    }

    private void Navigate(string path, bool push = true)
    {
        var session = DeckSession.Current;
        if (!session.Connected) return;
        try
        {
            if (push && !string.Equals(_cwd, path, StringComparison.Ordinal))
                _back.Push(_cwd);
            _cwd = string.IsNullOrWhiteSpace(path) ? session.Client.Home : path.Trim();
            PathBox.Text = _cwd;
            FileList.ItemsSource = session.Client.List(_cwd);
            PathChanged?.Invoke(_cwd);
        }
        catch (Exception ex)
        {
            PathBox.Text = ex.Message;
        }
    }
}
