using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Sesame.Services;
using Sesame.Services.Mii;

namespace Sesame;

public partial class MiiView : UserControl
{
    private DeckClient? _client;
    private MiiService? _service;
    private MiiTargetState? _state;
    private readonly MiiOperationLock _operationLock = new();

    public MiiView()
    {
        InitializeComponent();
        _operationLock.StateChanged += busy => BusyChanged?.Invoke(busy);
        TargetBox.SelectedIndex = 0;
        ShowUnavailable("Connect to a Steam Deck to inspect Miis.");
    }

    public bool IsWorking => _operationLock.IsActive;
    public event Action<string>? StatusChanged;
    public event Action<bool>? BusyChanged;
    public Func<bool>? CanStartOperation { get; set; }

    public void Attach(DeckClient client)
    {
        _client = client;
        _service = new MiiService(client);
    }

    public void OnConnected() => _ = RefreshAsync();

    public void OnDisconnected()
    {
        _state = null;
        MiiList.ItemsSource = null;
        BackupBox.ItemsSource = null;
        ShowUnavailable("Not connected.");
    }

    private MiiTargetKind SelectedKind =>
        TargetBox.SelectedItem is ComboBoxItem { Tag: string value } &&
        Enum.TryParse<MiiTargetKind>(value, out var kind) ? kind : MiiTargetKind.Wii;

    private async Task RefreshAsync()
    {
        if (IsWorking || _service is null || _client is not { IsConnected: true }) return;
        // Capture kind, host id and resolved path synchronously before any await. The whole panel is
        // disabled until the operation ends, so a selection/settings change cannot redirect it.
        var snapshot = _service.Capture(SelectedKind);
        await RunExclusiveAsync(snapshot, async () =>
        {
            var loaded = await Task.Run(() => _service.Load(snapshot));
            var backups = await Task.Run(() => _service.Inventory(snapshot));
            _state = loaded;
            MiiList.ItemsSource = loaded.Slots;
            BackupBox.ItemsSource = backups;
            BackupBox.SelectedIndex = backups.Count > 0 ? 0 : -1;
            CapabilityText.Text = loaded.Capability switch
            {
                MiiCapability.WriteVerified => "Capability: Write verified",
                MiiCapability.ReadOnlyVerified => "Capability: Read-only verified",
                _ => "Capability: Unavailable"
            };
            IntegrityText.Text = loaded.Integrity;
            PathText.Text = snapshot.Host + " · " + snapshot.TargetPath;
            ApplyCapabilities();
            StatusChanged?.Invoke($"Mii: {loaded.Slots.Count} record(s), {loaded.Capability}");
        });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void TargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && _client is { IsConnected: true }) await RefreshAsync();
    }

    private void MiiList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyCapabilities();
    private void BackupBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyCapabilities();

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _state is null || _state.Capability == MiiCapability.Unavailable) return;
        var state = _state;
        var snapshot = state.Target;
        await RunExclusiveAsync(snapshot, async () =>
        {
            var backup = await Task.Run(() => _service.BackupNow(state));
            StatusChanged?.Invoke("Verified Mii backup created: " + backup.Directory);
            await ReloadBackupsAsync(snapshot);
        });
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _state is null || MiiList.SelectedItem is not MiiSlot selected) return;
        try
        {
            var bytes = _service.ExportRecord(_state, selected.Slot);
            var dialog = new SaveFileDialog
            {
                Title = "Export exact Mii record",
                FileName = $"{selected.Name}-{selected.Slot}.mii",
                Filter = "Mii record (*.mii)|*.mii|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            File.WriteAllBytes(dialog.FileName, bytes);
            StatusChanged?.Invoke("Mii record exported without conversion.");
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Mii export"); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || BackupBox.SelectedItem is not MiiBackup backup) return;
        var snapshot = _service.Capture(SelectedKind);
        if (!string.Equals(snapshot.HostId, backup.Manifest.HostId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.TargetPath, backup.Manifest.TargetPath, StringComparison.Ordinal))
        {
            MessageBox.Show(Window.GetWindow(this), "This backup does not belong to the selected host and path.", "Mii restore");
            return;
        }
        bool liveMissing;
        try { liveMissing = _client is not null && !_client.Exists(snapshot.TargetPath); }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Could not verify the current live target: " + ex.Message, "Mii restore");
            return;
        }
        var warning = "Restore this verified backup?\n\nHost: " + backup.Manifest.Host + " (" + backup.Manifest.HostId + ")\n" +
                      "Path: " + backup.Manifest.TargetPath + "\nHash: " + backup.Manifest.BackupSha256 +
                      (liveMissing ? "\n\nThe live target is missing, so no pre-restore backup can be made." :
                          "\n\nThe current live bytes will first be backed up locally and beside the NAND file.");
        if (MessageBox.Show(Window.GetWindow(this), warning, "Mii restore",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        var allowUnknownProcessCheck = UnknownProcessBox.IsChecked == true;
        await RunExclusiveAsync(snapshot, async () =>
        {
            var result = await Task.Run(() => _service.Restore(snapshot, backup, allowUnknownProcessCheck));
            StatusChanged?.Invoke(result.ReconciledAfterTransportFailure
                ? "Restore committed and reconciled after a transport failure."
                : "Restore committed and verified.");
        });
        if (_client is { IsConnected: true }) await RefreshAsync();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _state is not { CanPush: true } state) return;
        var dialog = new OpenFileDialog { Title = "Import exact Mii record", Filter = "Mii records (*.mii;*.miigx)|*.mii;*.miigx|All files (*.*)|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var record = File.ReadAllBytes(dialog.FileName);
        var snapshot = state.Target;
        var allowUnknown = UnknownProcessBox.IsChecked == true;
        await RunExclusiveAsync(snapshot, () => Task.Run(() => _service.PushRecord(state, record, allowUnknown)));
        if (_client is { IsConnected: true }) await RefreshAsync();
    }

    private async void Basic_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _state is not { CanPush: true } state) return;
        var name = PromptName();
        if (name is null) return;
        var snapshot = state.Target;
        var allowUnknown = UnknownProcessBox.IsChecked == true;
        await RunExclusiveAsync(snapshot, () => Task.Run(() => _service.PushBasic(state, name, allowUnknown)));
        if (_client is { IsConnected: true }) await RefreshAsync();
    }

    private string? PromptName()
    {
        var box = new TextBox { Margin = new Thickness(12), MaxLength = 10 };
        var ok = new Button { Content = "Create", Width = 90, IsDefault = true, Margin = new Thickness(6) };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true, Margin = new Thickness(6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Mii name (1–10 characters)", Margin = new Thickness(12, 12, 12, 0) });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Owner = Window.GetWindow(this), Title = "New basic Mii", Width = 360, Height = 165,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Content = panel, Background = Background
        };
        ok.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? box.Text : null;
    }

    private async Task ReloadBackupsAsync(MiiOperationSnapshot snapshot)
    {
        if (_service is null) return;
        var backups = await Task.Run(() => _service.Inventory(snapshot));
        BackupBox.ItemsSource = backups;
        BackupBox.SelectedIndex = backups.Count > 0 ? 0 : -1;
        ApplyCapabilities();
    }

    private async Task RunExclusiveAsync(MiiOperationSnapshot snapshot, Func<Task> operation)
    {
        if (CanStartOperation?.Invoke() == false)
        {
            MessageBox.Show(Window.GetWindow(this), "Wait until the current SESAME mutation has finished.", "Mii operation");
            return;
        }
        if (!_operationLock.TryBegin()) return;
        Root.IsEnabled = false;
        try
        {
            StatusChanged?.Invoke("Mii operation on " + snapshot.HostId + " · " + snapshot.TargetPath);
            await operation();
        }
        catch (MiiTransactionException ex)
        {
            var title = ex.Outcome == MiiTransactionOutcome.Indeterminate
                ? "Mii outcome indeterminate"
                : "Mii operation not committed";
            MessageBox.Show(Window.GetWindow(this), ex.Message +
                (string.IsNullOrWhiteSpace(ex.BackupDirectory) ? "" : "\n\nBackup: " + ex.BackupDirectory),
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Mii operation");
        }
        finally
        {
            Root.IsEnabled = true;
            _operationLock.End();
            ApplyCapabilities();
        }
    }

    private void ApplyCapabilities()
    {
        var valid = _state is { Capability: not MiiCapability.Unavailable };
        BackupBtn.IsEnabled = valid;
        ExportBtn.IsEnabled = valid && MiiList.SelectedItem is not null;
        ImportBtn.IsEnabled = _state?.CanPush == true;
        BasicBtn.IsEnabled = _state?.CanPush == true;
        RestoreBtn.IsEnabled = BackupBox.SelectedItem is MiiBackup;
    }

    private void ShowUnavailable(string text)
    {
        CapabilityText.Text = "Capability: Unavailable";
        IntegrityText.Text = text;
        PathText.Text = "";
        ApplyCapabilities();
    }
}
