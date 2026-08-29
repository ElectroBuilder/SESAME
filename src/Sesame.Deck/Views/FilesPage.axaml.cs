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
    public string CurrentPath => _cwd;

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

    private void Open_Double(object? sender, TappedEventArgs e) => OpenSelected();

    private void Open_Click(object? sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (FileList.SelectedItem is RemoteItem { IsDirectory: true } item)
            Navigate(item.FullPath);
    }

    private async void NewFolder_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null || !DeckSession.Current.Connected) return;
        var dlg = new AskWindow("New folder", "Folder name");
        if (await dlg.ShowDialog<bool>(owner) != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        try
        {
            DeckSession.Current.Client.EnsureDirectory(DeckClient.Combine(_cwd, dlg.Value));
            Navigate(_cwd, push: false);
        }
        catch (Exception ex)
        {
            PathBox.Text = ex.Message;
        }
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not RemoteItem item) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        if (!await ConfirmWindow.Ask(owner, "Delete", "Delete " + item.Name + "?")) return;
        try
        {
            DeckSession.Current.Client.Delete(item);
            Navigate(_cwd, push: false);
        }
        catch (Exception ex)
        {
            PathBox.Text = ex.Message;
        }
    }

    private async void Download_Click(object? sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not RemoteItem item) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Download to"
        });
        if (folders.Count == 0) return;
        try
        {
            DeckSession.Current.Client.DownloadItem(item, folders[0].Path.LocalPath);
            PathChanged?.Invoke("Downloaded " + item.Name);
        }
        catch (Exception ex)
        {
            PathBox.Text = ex.Message;
        }
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
