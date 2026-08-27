using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VisualSSH.Models;
using VisualSSH.Services;

namespace Sesame.Deck.Views;

public partial class FilesPage : UserControl
{
    private string _cwd = "/home/deck";

    public FilesPage()
    {
        InitializeComponent();
    }

    public void OnConnected()
    {
        _cwd = DeckSession.Current.Client.Home;
        Navigate(_cwd);
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => Navigate(_cwd);
    private void Up_Click(object? sender, RoutedEventArgs e) => Navigate(DeckClient.Parent(_cwd));

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

    private void Navigate(string path)
    {
        var session = DeckSession.Current;
        if (!session.Connected) return;
        try
        {
            _cwd = string.IsNullOrWhiteSpace(path) ? session.Client.Home : path.Trim();
            PathBox.Text = _cwd;
            var items = session.Client.List(_cwd);
            FileList.ItemsSource = items.Select(i =>
            {
                i.DisplayName = (i.IsDirectory ? "📁 " : "   ") + i.Name;
                return i;
            }).ToList();
            FileList.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(RemoteItem.DisplayName));
        }
        catch (Exception ex)
        {
            PathBox.Text = ex.Message;
        }
    }
}
