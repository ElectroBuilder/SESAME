using System.Windows;
using Microsoft.Win32;
using VisualSSH.Models;
using VisualSSH.Services;

namespace VisualSSH;

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
        if (MessageBox.Show(this, $"Sessie '{profile.Name}' verwijderen?", AppBrand.ShortName,
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
        MessageBox.Show(this, "Sessie opgeslagen.", AppBrand.ShortName, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not ConnectionProfile profile)
        {
            MessageBox.Show(this, "Kies of maak eerst een sessie.");
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Private key importeren",
            Filter = "SSH keys (*.*;steam_deck;id_rsa;*.ppk)|*.*|Alle bestanden (*.*)|*.*"
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
                KeyStatus.Text = "Versleutelde sleutel opgeslagen — vul de wachtwoordzin in";
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
        if (MessageBox.Show(this, "Opgeslagen SSH-sleutel (en wachtwoordzin) verwijderen?", AppBrand.ShortName,
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
            MessageBox.Show(this, "Importeer eerst een private key of sla een wachtwoord op.");
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
        KeyStatus.Text = SshSecrets.HasKey(profileId) ? "Sleutel opgeslagen" : "Geen sleutel";
        PassphraseStatus.Text = SshSecrets.HasPassphrase(profileId)
            ? "Wachtwoordzin opgeslagen. Typ alleen iets om die te vervangen."
            : "Alleen nodig bij een versleutelde sleutel.";
        PasswordStatus.Text = SshSecrets.HasPassword(profileId)
            ? "Wachtwoord opgeslagen. Typ alleen iets om dat te vervangen."
            : "Optioneel. Laat leeg bij login met alleen een sleutel.";
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
            MessageBox.Show(this, "Vul een sessienaam in.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show(this, "Vul een host of IP-adres in.");
            return false;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Poort moet tussen 1 en 65535 liggen.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(user))
        {
            MessageBox.Show(this, "Vul een gebruikersnaam in.");
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
