using System.Windows;
using System.Windows.Controls;
using Sesame.Services.GameOptimizer;

namespace Sesame;

public partial class LaunchersView : UserControl
{
    private bool _loading;
    private SystemLaunchConfig? _selected;

    public LaunchersView()
    {
        InitializeComponent();
        Reload();
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
        if (SystemList.Items.Count > 0)
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
    }

    private void System_Changed(object sender, SelectionChangedEventArgs e)
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
        GyroNote.Visibility = dolphin ? Visibility.Visible : Visibility.Collapsed;
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

    private void Layout_Changed(object sender, RoutedEventArgs e)
    {
        LaunchConfigStore.Current.RomsRoot = RomsBox.Text?.Trim() ?? "";
        LaunchConfigStore.Current.ToolsRoot = ToolsBox.Text?.Trim() ?? "";
        LaunchConfigStore.Current.LaunchersRoot = LaunchersBox.Text?.Trim() ?? "";
        LaunchConfigStore.Current.Flatpak = FlatpakBox.Text?.Trim() ?? "";
        if (_selected is not null)
            PreviewBox.Text = LaunchComposer.Preview(_selected);
    }

    private void Field_Changed(object sender, RoutedEventArgs e) => SaveFields();

    private void Emulator_Changed(object sender, SelectionChangedEventArgs e) => ApplyEmulatorChoice();

    private void Emulator_DropDownClosed(object? sender, EventArgs e) => ApplyEmulatorChoice();

    private void Core_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selected is null) return;
        SaveFields();
    }

    private void Core_DropDownClosed(object? sender, EventArgs e)
    {
        if (_loading || _selected is null) return;
        SaveFields();
    }

    private void ApplyEmulatorChoice()
    {
        if (_loading || _selected is null) return;
        var picked = ComboText(EmulatorBox);
        if (string.IsNullOrWhiteSpace(picked)) return;
        _selected.Emulator = picked;
        EmulatorLaunch.Apply(_selected);
        ShowSelected();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Reset all launch rules to {flatpak} {emulator} {core} \"{rom}\" for RetroArch?",
                "Emulators", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        LaunchConfigStore.Reset();
        RomsBox.Text = LaunchConfigStore.Current.RomsRoot;
        ToolsBox.Text = LaunchConfigStore.Current.ToolsRoot;
        LaunchersBox.Text = LaunchConfigStore.Current.LaunchersRoot;
        FlatpakBox.Text = LaunchConfigStore.Current.Flatpak;
        SystemList.ItemsSource = LaunchConfigStore.Current.Systems.OrderBy(s => s.Name).ToList();
        if (SystemList.Items.Count > 0)
            SystemList.SelectedIndex = 0;
    }

    private static string ComboText(ComboBox box)
    {
        if (box.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
            return selected.Trim();
        return box.Text?.Trim() ?? "";
    }

    private static void SetCombo(ComboBox box, string value)
    {
        value = (value ?? "").Trim();
        if (box.ItemsSource is IEnumerable<string> items)
        {
            var hit = items.FirstOrDefault(i => i.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                box.SelectedItem = hit;
                box.Text = hit;
                return;
            }
        }
        box.SelectedItem = null;
        box.Text = value;
    }
}
