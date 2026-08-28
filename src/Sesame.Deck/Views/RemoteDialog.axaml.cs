using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sesame.Models;
using Sesame.Services;

namespace Sesame.Deck.Views;

public partial class RemoteDialog : Window
{
    public bool Connected { get; private set; }

    public RemoteDialog()
    {
        InitializeComponent();
        var existing = DeckSession.Current.Profiles.Profiles.FirstOrDefault();
        if (existing is null) return;
        HostBox.Text = existing.Host;
        PortBox.Text = existing.Port.ToString();
        UserBox.Text = existing.User;
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "SSH private key",
            AllowMultiple = false
        });
        if (files.Count > 0)
            KeyBox.Text = files[0].Path.LocalPath;
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(PortBox.Text, out var port)) port = 22;
            var profiles = DeckSession.Current.Profiles;
            var profile = profiles.Profiles.FirstOrDefault() ?? profiles.AddNew();
            profile.Name = string.IsNullOrWhiteSpace(HostBox.Text) ? "Steam Deck" : HostBox.Text.Trim();
            profile.Host = HostBox.Text?.Trim() ?? "";
            profile.Port = port;
            profile.User = string.IsNullOrWhiteSpace(UserBox.Text) ? "deck" : UserBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(KeyBox.Text))
                SshSecrets.ImportFromFile(profile.Id, KeyBox.Text.Trim());
            profiles.Save();
            await DeckSession.Current.ConnectRemoteAsync(profile);
            Connected = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
