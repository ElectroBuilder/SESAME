using System.IO;
using System.Windows;
using Microsoft.Win32;
using Sesame.Models;
using Sesame.Services;

namespace Sesame;

public partial class SessionsWindow : Window
{
    private readonly ProfileStore _store;

    public ConnectionProfile? ProfileToOpen { get; private set; }

    public SessionsWindow(ProfileStore store)
    {
        InitializeComponent();
        _store = store;
        SessionList.ItemsSource = _store.Profiles;
        if (_store.Profiles.Count > 0)
            SessionList.SelectedIndex = 0;
        else
            ShowProfile(new ConnectionProfile());
    }

    private void SessionList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is ConnectionProfile profile)
            ShowProfile(profile);
    }

    private void SessionList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SessionList.SelectedItem is ConnectionProfile)
            Connect_Click(sender, e);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var created = _store.AddNew();
        SessionList.SelectedItem = created;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not ConnectionProfile profile) return;
        if (MessageBox.Show(this, $"Delete session '{profile.Name}'?", AppBrand.ShortName,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _store.Delete(profile);
        if (_store.Profiles.Count > 0)
            SessionList.SelectedIndex = 0;
        else
            ShowProfile(new ConnectionProfile());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var edited)) return;
        if (SessionList.SelectedItem is ConnectionProfile selected)
            edited.Id = selected.Id;
        SaveSecrets(edited.Id);
        _store.Upsert(edited);
        SessionList.SelectedItem = _store.Profiles.First(p => p.Id == edited.Id);
        ShowSecretStatus(edited.Id);
        MessageBox.Show(this, "Session saved.", AppBrand.ShortName, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not ConnectionProfile profile)
        {
            MessageBox.Show(this, "Select or create a session first.");
            return;
        }

        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var dlg = new OpenFileDialog
        {
            Title = "Import SSH private key",
            Filter = "All files (*.*)|*.*|OpenSSH / PuTTY (*.ppk;*.pem;*.key)|*.ppk;*.pem;*.key",
            FilterIndex = 1,
            CheckFileExists = true,
            ValidateNames = false,
            DereferenceLinks = true,
            InitialDirectory = Directory.Exists(sshDir) ? sshDir : ""
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var needsPass = SshSecrets.ImportFromFile(profile.Id, dlg.FileName);
            if (TryReadForm(out var edited))
            {
                edited.Id = profile.Id;
                _store.Upsert(edited);
            }
            ShowSecretStatus(profile.Id);
            if (needsPass)
            {
                KeyStatus.Text = "Encrypted key saved — enter the passphrase";
                PassphraseBox.Focus();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppBrand.ShortName);
        }
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not ConnectionProfile profile) return;
        if (!SshSecrets.HasKey(profile.Id)) return;
        if (MessageBox.Show(this, "Remove the saved SSH key (and passphrase)?", AppBrand.ShortName,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        SshSecrets.DeleteKey(profile.Id);
        ShowSecretStatus(profile.Id);
    }

    private void ClearPassphrase_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not ConnectionProfile profile) return;
        SshSecrets.SavePassphrase(profile.Id, "");
        PassphraseBox.Clear();
        ShowSecretStatus(profile.Id);
    }

    private void ClearPassword_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not ConnectionProfile profile) return;
        SshSecrets.SavePassword(profile.Id, "");
        LoginPasswordBox.Clear();
        ShowSecretStatus(profile.Id);
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var edited)) return;
        if (SessionList.SelectedItem is ConnectionProfile selected)
            edited.Id = selected.Id;
        SaveSecrets(edited.Id);
        _store.Upsert(edited);
        if (!SshSecrets.HasKey(edited.Id) && !SshSecrets.HasPassword(edited.Id))
        {
            MessageBox.Show(this, "Import a private key first, or save a login password.");
            return;
        }
        ProfileToOpen = _store.Profiles.First(p => p.Id == edited.Id).Clone();
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowProfile(ConnectionProfile profile)
    {
        NameBox.Text = profile.Name;
        HostBox.Text = profile.Host;
        PortBox.Text = profile.Port.ToString();
        UserBox.Text = profile.User;
        MacBox.Text = profile.MacAddress;
        PassphraseBox.Clear();
        LoginPasswordBox.Clear();
        ShowSecretStatus(profile.Id);
    }

    private void ShowSecretStatus(string profileId)
    {
        KeyStatus.Text = SshSecrets.HasKey(profileId) ? "Key saved" : "No key";
        PassphraseStatus.Text = SshSecrets.HasPassphrase(profileId)
            ? "Passphrase saved. Type a new one only if you want to replace it."
            : "Only needed if this private key is encrypted. Leave empty for a normal Steam Deck key.";
        PasswordStatus.Text = SshSecrets.HasPassword(profileId)
            ? "Password saved. Type a new one only if you want to replace it."
            : "Optional. Leave empty if you log in with a key.";
    }

    private void SaveSecrets(string profileId)
    {
        var pass = PassphraseBox.Password ?? "";
        if (pass.Length > 0)
            SshSecrets.SavePassphrase(profileId, pass);
        var password = LoginPasswordBox.Password ?? "";
        if (password.Length > 0)
            SshSecrets.SavePassword(profileId, password);
        PassphraseBox.Clear();
        LoginPasswordBox.Clear();
    }

    private bool TryReadForm(out ConnectionProfile profile)
    {
        profile = new ConnectionProfile();
        var name = NameBox.Text.Trim();
        var host = HostBox.Text.Trim();
        var user = UserBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a session name.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show(this, "Enter a host or IP address.");
            return false;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Port must be between 1 and 65535.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(user))
        {
            MessageBox.Show(this, "Enter a user name.");
            return false;
        }

        profile.Name = name;
        profile.Host = host;
        profile.Port = port;
        profile.User = user;
        profile.MacAddress = MacBox.Text.Trim();
        return true;
    }
}
