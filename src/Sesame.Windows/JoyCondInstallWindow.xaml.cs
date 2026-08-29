using System.Windows;
using System.Windows.Controls;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame;

public partial class JoyCondInstallWindow : Window
{
    private readonly DeckClient _client;
    private bool _busy;

    public JoyCondInstallWindow(DeckClient client)
    {
        InitializeComponent();
        _client = client;
        RiskBox.Text = JoyCondInstall.RiskSummary;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (!_client.IsConnected)
        {
            StatusLine.Text = "Not connected to the Steam Deck.";
            InstallBtn.IsEnabled = false;
            return;
        }

        try
        {
            var st = JoyCondInstall.Query(_client);
            StatusLine.Text =
                "joycond: " + (st.JoyCondActive ? "active" : st.ActiveRaw) +
                " · cemuhook: " + (st.CemuhookOk ? "OK" : "not ready") +
                (string.IsNullOrEmpty(st.StatusFile) ? "" : " · status: " + st.StatusFile);
            if (!_busy)
                InstallBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusLine.Text = ex.Message;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshStatus();

    private void ViewScript_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window
        {
            Title = "install-joycond.sh",
            Owner = this,
            Width = 720,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox
            {
                Text = JoyCondInstall.ScriptText(),
                IsReadOnly = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                AcceptsReturn = true
            }
        };
        win.ShowDialog();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_client.IsConnected)
        {
            MessageBox.Show(this, "Connect to the Steam Deck first.", "Joy-Con install");
            return;
        }

        if (RiskCheck.IsChecked != true)
        {
            MessageBox.Show(this, "Confirm that you understand the risks first.", "Joy-Con install");
            return;
        }

        var pass = SudoBox.Password ?? "";
        if (string.IsNullOrEmpty(pass))
        {
            MessageBox.Show(this, "Enter the Deck sudo password.", "Joy-Con install");
            return;
        }

        if (MessageBox.Show(this,
                "Install joycond + cemuhook on the connected Deck now?\n\nThis can take several minutes.",
                "Joy-Con install", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _busy = true;
        InstallBtn.IsEnabled = false;
        ViewScriptBtn.IsEnabled = false;
        RefreshBtn.IsEnabled = false;
        SudoBox.IsEnabled = false;
        LogBox.Text = "";
        var progress = new Progress<string>(t =>
        {
            StatusLine.Text = t;
            if (!string.IsNullOrEmpty(LogBox.Text))
                LogBox.AppendText("\n");
            LogBox.AppendText(t);
            LogBox.ScrollToEnd();
        });

        try
        {
            var output = await Task.Run(() => JoyCondInstall.Run(_client, pass, progress));
            var tail = JoyCondInstall.ReadLogTail(_client);
            LogBox.Text = string.IsNullOrWhiteSpace(tail) ? output : tail;
            SudoBox.Clear();
            RefreshStatus();
            MessageBox.Show(this,
                "Install finished.\n\nPair Joy-Cons (SL+SR each), Steam Input Off on the Wii shortcut, Optimize, then launch via SESAME.",
                "Joy-Con install");
        }
        catch (Exception ex)
        {
            var tail = "";
            try { tail = JoyCondInstall.ReadLogTail(_client); } catch { /* ignore */ }
            LogBox.Text = (string.IsNullOrWhiteSpace(tail) ? "" : tail + "\n\n") + ex.Message;
            StatusLine.Text = "Install failed.";
            MessageBox.Show(this, ex.Message, "Joy-Con install");
        }
        finally
        {
            _busy = false;
            ViewScriptBtn.IsEnabled = true;
            RefreshBtn.IsEnabled = true;
            SudoBox.IsEnabled = true;
            RefreshStatus();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
