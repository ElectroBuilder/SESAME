using Avalonia.Controls;
using Avalonia.Interactivity;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck.Views;

public partial class JoyCondInstallWindow : Window
{
    private bool _busy;

    public JoyCondInstallWindow()
    {
        InitializeComponent();
        RiskBox.Text = JoyCondInstall.RiskSummary;
        RefreshStatus();
    }

    private DeckClient? Client =>
        DeckSession.Current.Connected ? DeckSession.Current.Client : null;

    private void RefreshStatus()
    {
        var client = Client;
        if (client is null)
        {
            StatusLine.Text = "Not connected.";
            return;
        }

        try
        {
            var st = JoyCondInstall.Query(client);
            StatusLine.Text =
                "joycond: " + (st.JoyCondActive ? "active" : st.ActiveRaw) +
                " · cemuhook: " + (st.CemuhookOk ? "OK" : "not ready");
        }
        catch (Exception ex)
        {
            StatusLine.Text = ex.Message;
        }
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => RefreshStatus();

    private async void ViewScript_Click(object? sender, RoutedEventArgs e)
    {
        var box = new TextBox
        {
            Text = JoyCondInstall.ScriptText(),
            IsReadOnly = true,
            FontFamily = new Avalonia.Media.FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            AcceptsReturn = true
        };
        var win = new Window
        {
            Title = "install-joycond.sh",
            Width = 720,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = box
        };
        await win.ShowDialog(this);
    }

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var client = Client;
        if (client is null)
        {
            await ConfirmWindow.Ask(this, "Joy-Con install", "Connect first.");
            return;
        }

        if (RiskCheck.IsChecked != true)
        {
            await ConfirmWindow.Ask(this, "Joy-Con install", "Confirm that you understand the risks first.");
            return;
        }

        var pass = SudoBox.Text ?? "";
        if (string.IsNullOrEmpty(pass))
        {
            await ConfirmWindow.Ask(this, "Joy-Con install", "Enter the sudo password.");
            return;
        }

        if (!await ConfirmWindow.Ask(this, "Joy-Con install",
                "Install joycond + cemuhook now? This can take several minutes."))
            return;

        _busy = true;
        LogBox.Text = "";
        var progress = new Progress<string>(t =>
        {
            StatusLine.Text = t;
            LogBox.Text += (string.IsNullOrEmpty(LogBox.Text) ? "" : "\n") + t;
        });

        try
        {
            var output = await Task.Run(() => JoyCondInstall.Run(client, pass, progress));
            var tail = JoyCondInstall.ReadLogTail(client);
            LogBox.Text = string.IsNullOrWhiteSpace(tail) ? output : tail;
            SudoBox.Text = "";
            RefreshStatus();
            await ConfirmWindow.Ask(this, "Joy-Con install",
                "Install finished. Pair Joy-Cons (SL+SR), Steam Input Off on Wii, Optimize, launch via SESAME.");
        }
        catch (Exception ex)
        {
            try { LogBox.Text = JoyCondInstall.ReadLogTail(client) + "\n\n" + ex.Message; }
            catch { LogBox.Text = ex.Message; }
            StatusLine.Text = "Install failed.";
            await ConfirmWindow.Ask(this, "Joy-Con install", ex.Message);
        }
        finally
        {
            _busy = false;
            RefreshStatus();
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
