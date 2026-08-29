using Avalonia.Controls;
using Avalonia.Interactivity;
using Sesame.Services;
using Sesame.Services.GameOptimizer;

namespace Sesame.Deck.Views;

public partial class LaunchersPage : UserControl
{
    private bool _loading;
    private SystemLaunchConfig? _selected;

    public LaunchersPage()
    {
        InitializeComponent();
        Reload();
        _ = RefreshJoyCondStatusAsync();
    }

    public void RefreshJoyCondStatus() => _ = RefreshJoyCondStatusAsync();

    public async Task RefreshJoyCondStatusAsync()
    {
        if (JoyCondStatus is null) return;
        if (!DeckSession.Current.Connected)
        {
            JoyCondStatus.Text = "Connect to check or install.";
            return;
        }

        JoyCondStatus.Text = "Checking joycond…";
        try
        {
            var client = DeckSession.Current.Client;
            var st = await Task.Run(() => JoyCondInstall.Query(client));
            JoyCondStatus.Text =
                "Status: joycond " + (st.JoyCondActive ? "active" : st.ActiveRaw) +
                ", cemuhook " + (st.CemuhookOk ? "OK" : "not ready");
        }
        catch (Exception ex)
        {
            JoyCondStatus.Text = ex.Message;
        }
    }

    private async void JoyCondInstall_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        if (!DeckSession.Current.Connected)
        {
            await ConfirmWindow.Ask(owner, "Joy-Con install", "Connect first.");
            return;
        }

        var win = new JoyCondInstallWindow();
        await win.ShowDialog(owner);
        await RefreshJoyCondStatusAsync();
    }

    public void Reload()
    {
        LaunchConfigStore.Load();
        EmulatorBox.ItemsSource = EmulatorLaunch.Ids;
        RomsBox.Text = LaunchConfigStore.Current.RomsRoot;
        ToolsBox.Text = LaunchConfigStore.Current.ToolsRoot;
        LaunchersBox.Text = LaunchConfigStore.Current.LaunchersRoot;
        FlatpakBox.Text = LaunchConfigStore.Current.Flatpak;
        SystemList.ItemsSource = LaunchConfigStore.Current.Systems.OrderBy(s => s.Name).ToList();
        if (SystemList.ItemCount > 0)
            SystemList.SelectedIndex = 0;
    }

    public void Commit()
    {
        SaveFields();
        LaunchConfigStore.Current.RomsRoot = RomsBox.Text?.Trim() ?? "";
        LaunchConfigStore.Current.ToolsRoot = ToolsBox.Text?.Trim() ?? "";
        LaunchConfigStore.Current.LaunchersRoot = LaunchersBox.Text?.Trim() ?? "";
        LaunchConfigStore.Current.Flatpak = FlatpakBox.Text?.Trim() ?? "";
        LaunchConfigStore.Save();
        LibraryPaths.Current.RomsRoot = LaunchConfigStore.Current.RomsRoot;
        LibraryPaths.Save(syncLaunchers: false);
    }

    private void System_Changed(object? sender, SelectionChangedEventArgs e)
    {
        SaveFields();
        ShowSelected();
    }

    private void ShowSelected()
    {
        _selected = SystemList.SelectedItem as SystemLaunchConfig;
        if (_selected is null) return;
        _loading = true;
        FolderBox.Text = _selected.RomFolder;
        SetCombo(EmulatorBox, _selected.Emulator);
        var cores = SystemCatalog.All.FirstOrDefault(p =>
            p.Id.Equals(_selected.SystemId, StringComparison.OrdinalIgnoreCase))?.Cores;
        CoreBox.ItemsSource = cores ?? [];
        SetCombo(CoreBox, _selected.Core);
        TargetBox.Text = _selected.TargetTemplate;
        StartDirBox.Text = _selected.StartDirTemplate;
        OptionsBox.Text = _selected.OptionsTemplate;
        _loading = false;
        PreviewBox.Text = LaunchComposer.Preview(_selected);
        var dolphin = _selected.SystemId is "wii" or "gc" ||
                      string.Equals(_selected.Emulator, "dolphin", StringComparison.OrdinalIgnoreCase);
        GyroNote.Text = dolphin ? DolphinInput.DutchGyroHint : "";
        GyroNote.IsVisible = dolphin;
    }

    private void SaveFields()
    {
        if (_loading || _selected is null) return;
        _selected.RomFolder = FolderBox.Text?.Trim() ?? "";
        _selected.Emulator = ComboText(EmulatorBox);
        _selected.Core = ComboText(CoreBox);
        _selected.TargetTemplate = TargetBox.Text?.Trim() ?? "";
        _selected.StartDirTemplate = StartDirBox.Text?.Trim() ?? "";
        _selected.OptionsTemplate = OptionsBox.Text?.Trim() ?? "";
        PreviewBox.Text = LaunchComposer.Preview(_selected);
    }

    private void Layout_Changed(object? sender, RoutedEventArgs e)
    {
        LaunchConfigStore.Current.RomsRoot = RomsBox.Text?.Trim() ?? "";
        LaunchConfigStore.Current.ToolsRoot = ToolsBox.Text?.Trim() ?? "";
        LaunchConfigStore.Current.LaunchersRoot = LaunchersBox.Text?.Trim() ?? "";
        LaunchConfigStore.Current.Flatpak = FlatpakBox.Text?.Trim() ?? "";
        if (_selected is not null)
            PreviewBox.Text = LaunchComposer.Preview(_selected);
    }

    private void Field_Changed(object? sender, RoutedEventArgs e) => SaveFields();

    private void Emulator_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selected is null) return;
        var picked = ComboText(EmulatorBox);
        if (string.IsNullOrWhiteSpace(picked)) return;
        _selected.Emulator = picked;
        EmulatorLaunch.Apply(_selected);
        ShowSelected();
    }

    private void Core_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selected is null) return;
        SaveFields();
    }

    private async void Reset_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        if (!await ConfirmWindow.Ask(owner, "Emulators",
                "Reset all launch rules to {flatpak} {emulator} {core} \"{rom}\" for RetroArch?"))
            return;
        LaunchConfigStore.Reset();
        Reload();
    }

    private static string ComboText(ComboBox box) =>
        (box.SelectedItem as string)?.Trim()
        ?? box.SelectedItem?.ToString()?.Trim()
        ?? "";

    private static void SetCombo(ComboBox box, string value)
    {
        value = (value ?? "").Trim();
        if (box.ItemsSource is IEnumerable<string> items)
        {
            var hit = items.FirstOrDefault(i => i.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (hit is null && value.Length > 0)
            {
                var extra = items.Append(value).ToList();
                box.ItemsSource = extra;
                hit = value;
            }
            box.SelectedItem = hit;
            return;
        }
        box.SelectedItem = value.Length == 0 ? null : value;
    }
}
