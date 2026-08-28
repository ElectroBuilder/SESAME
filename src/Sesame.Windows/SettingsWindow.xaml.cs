using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame;

public partial class SettingsWindow : Window
{
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();
        _loading = true;
        DarkThemeBox.IsChecked = ThemeManager.Current == ThemeManager.Dark;
        LightThemeBox.IsChecked = ThemeManager.Current == ThemeManager.Light;
        TabsPlatformBox.IsChecked = OptimizerSettings.SteamTabScheme == SteamTabScheme.Platform;
        TabsBrandBox.IsChecked = OptimizerSettings.SteamTabScheme == SteamTabScheme.Brand;
        TabsEmulationBox.IsChecked = OptimizerSettings.SteamTabScheme == SteamTabScheme.Emulation;
        MasksMasterBox.IsChecked = OptimizerSettings.UseMasks;
        BindMasks();
        RefreshKeyStatus();
        _loading = false;
        Closing += Settings_Closing;
    }

    public bool KeyChanged { get; private set; }
    public bool LaunchersChanged { get; private set; }
    public bool MasksChanged { get; private set; }

    private void BindMasks() => MaskList.ItemsSource = OptimizerSettings.MaskOptions();

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ThemeManager.Apply(LightThemeBox.IsChecked == true ? ThemeManager.Light : ThemeManager.Dark);
    }

    private void SteamTabs_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (TabsBrandBox.IsChecked == true)
            OptimizerSettings.SteamTabScheme = SteamTabScheme.Brand;
        else if (TabsEmulationBox.IsChecked == true)
            OptimizerSettings.SteamTabScheme = SteamTabScheme.Emulation;
        else
            OptimizerSettings.SteamTabScheme = SteamTabScheme.Platform;
        OptimizerSettings.Save();
    }

    private void MasksMaster_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        OptimizerSettings.UseMasks = MasksMasterBox.IsChecked == true;
        OptimizerSettings.Save();
        MasksChanged = true;
    }

    private void MasksDefault_Click(object sender, RoutedEventArgs e)
    {
        OptimizerSettings.ResetMaskDefaults();
        BindMasks();
        MasksChanged = true;
    }

    private void MasksAllOn_Click(object sender, RoutedEventArgs e) => SetAllMasks(true);

    private void MasksAllOff_Click(object sender, RoutedEventArgs e) => SetAllMasks(false);

    private void SetAllMasks(bool enabled)
    {
        foreach (var row in OptimizerSettings.MaskOptions())
            OptimizerSettings.SetPlatformMask(row.Id, enabled, persist: false);
        OptimizerSettings.Save();
        BindMasks();
        MasksChanged = true;
    }

    private void ClearCaches_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Gedownloade covers, winkelminiaturen en de bibliotheekcache wissen?\n\nSSH- en API-sleutels en jouw coverkeuzes blijven bewaard.",
                "Caches wissen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var n = AppDataPaths.ClearCaches();
        CacheStatus.Text = n == 0
            ? "Geen cachebestanden gevonden."
            : n + " cachebestanden verwijderd. Bij de volgende verbinding scant SESAME de Deck opnieuw.";
    }

    private async void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var typed = KeyBox.Password ?? "";
        if (string.IsNullOrWhiteSpace(typed))
        {
            KeyStatus.Text = OptimizerSettings.HasSteamGridDb
                ? "Typ een nieuwe sleutel om de opgeslagen secret te vervangen."
                : "Plak eerst een SteamGridDB-sleutel.";
            return;
        }

        OptimizerSettings.SaveKey(typed);
        KeyBox.Clear();
        KeyChanged = true;
        KeyStatus.Text = "SteamGridDB-sleutel controleren…";
        try
        {
            var (ok, message) = await ArtworkClient.ValidateKeyAsync(OptimizerSettings.SteamGridDbKey, CancellationToken.None);
            KeyStatus.Text = message;
            if (!ok)
                MessageBox.Show(this, message, "SteamGridDB");
        }
        catch (Exception ex)
        {
            KeyStatus.Text = ex.Message;
        }
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        if (!OptimizerSettings.HasSteamGridDb && string.IsNullOrEmpty(KeyBox.Password))
            return;
        if (MessageBox.Show(this, "Opgeslagen SteamGridDB-sleutel verwijderen?", "Instellingen",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        OptimizerSettings.ClearKey();
        KeyBox.Clear();
        KeyChanged = true;
        RefreshKeyStatus();
    }

    private void RefreshKeyStatus()
    {
        KeyStatus.Text = OptimizerSettings.HasSteamGridDb
            ? "Er is een sleutel veilig opgeslagen. Typ alleen iets om die te vervangen."
            : "Nog geen sleutel. Maak er een op steamgriddb.com en plak die hier.";
    }

    private void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Settings_Closing(object? sender, CancelEventArgs e)
    {
        Launchers.Commit();
        LaunchersChanged = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
