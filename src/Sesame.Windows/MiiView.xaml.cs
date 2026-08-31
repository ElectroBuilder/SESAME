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
    private MiiTargetState? _liveState;
    private readonly MiiOperationLock _operationLock = new();
    private bool _updatingPaths;
    private readonly Dictionary<MiiTargetKind, string> _selectedPaths = [];

    public MiiView()
    {
        InitializeComponent();
        _operationLock.StateChanged += busy => BusyChanged?.Invoke(busy);
        TargetBox.SelectedIndex = 0;
        ConfigureAppearanceControls(MiiTargetKind.Wii);
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
        _liveState = null;
        ConfigureAppearanceControls(SelectedKind);
        MiiList.ItemsSource = null;
        BackupBox.ItemsSource = null;
        DatabasePathBox.ItemsSource = null;
        ShowUnavailable("Not connected.");
    }

    private MiiTargetKind SelectedKind =>
        TargetBox.SelectedItem is ComboBoxItem { Tag: string value } &&
        Enum.TryParse<MiiTargetKind>(value, out var kind) ? kind : MiiTargetKind.Wii;

    private async Task RefreshAsync()
    {
        if (IsWorking || _service is null || _client is not { IsConnected: true }) return;
        var kind = SelectedKind;
        var profile = _client.ActiveProfile;
        if (profile is null) return;
        _selectedPaths.TryGetValue(kind, out var selectedPath);
        // Freeze the host and requested path before awaiting. Probing then runs while the whole
        // panel and app-wide disconnect/close mutations are blocked by the operation lock.
        var frozen = new MiiOperationSnapshot(kind, selectedPath ?? "automatic path detection",
            profile.Id, profile.Host, "Detecting exact known database paths…", PathApproved: false);
        await RunExclusiveAsync(frozen, async () =>
        {
            var resolution = await Task.Run(() => _service.Resolve(kind, selectedPath));
            var snapshot = resolution.Target;
            var loaded = await Task.Run(() => _service.Load(snapshot));
            var backups = await Task.Run(() => _service.Inventory(loaded.Target));
            _state = _liveState = loaded;
            if (resolution.Target.PathApproved && resolution.Exists)
                _selectedPaths[kind] = resolution.Target.TargetPath;
            MiiList.ItemsSource = loaded.Slots;
            MiiList.SelectedIndex = loaded.Slots.Count > 0 ? 0 : -1;
            BackupBox.ItemsSource = backups;
            BackupBox.SelectedIndex = backups.Count > 0 ? 0 : -1;
            _updatingPaths = true;
            try
            {
                DatabasePathBox.ItemsSource = resolution.ValidCandidates;
                DatabasePathBox.SelectedItem = resolution.ValidCandidates.FirstOrDefault(x =>
                    string.Equals(x.Path, loaded.Target.TargetPath, StringComparison.Ordinal));
                DatabasePathBox.Visibility = resolution.ValidCandidates.Count > 1 ||
                                             (!resolution.Target.PathApproved && resolution.ValidCandidates.Count > 0)
                    ? Visibility.Visible : Visibility.Collapsed;
                DatabasePathBox.IsEnabled = DatabasePathBox.Visibility == Visibility.Visible;
            }
            finally { _updatingPaths = false; }
            CapabilityText.Text = loaded.Capability switch
            {
                MiiCapability.WriteVerified => "Capability: Write verified",
                MiiCapability.ReadOnlyVerified => "Capability: Read-only verified",
                _ => "Capability: Unavailable"
            };
            IntegrityText.Text = loaded.Integrity;
            PathText.Text = loaded.Target.Host + " · " + loaded.Target.TargetPath +
                            (string.IsNullOrWhiteSpace(loaded.Target.PathStatus) ? "" : "\n" + loaded.Target.PathStatus);
            ApplyCapabilities();
            StatusChanged?.Invoke($"Mii: {loaded.Slots.Count} record(s), {loaded.Capability}");
        });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void TargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ConfigureAppearanceControls(SelectedKind);
        if (IsLoaded && _client is { IsConnected: true })
        {
            _updatingPaths = true;
            try { DatabasePathBox.ItemsSource = null; DatabasePathBox.Visibility = Visibility.Collapsed; }
            finally { _updatingPaths = false; }
            await RefreshAsync();
        }
    }

    private async void DatabasePathBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPaths || DatabasePathBox.SelectedItem is not MiiPathCandidate candidate) return;
        _selectedPaths[SelectedKind] = candidate.Path;
        if (IsLoaded && _client is { IsConnected: true }) await RefreshAsync();
    }

    private void MiiList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_service is not null && _state is not null && MiiList.SelectedItem is MiiSlot selected)
        {
            try { LoadAppearance(_service.GetAppearance(_state, selected.Slot)); }
            catch (Exception ex) { StatusChanged?.Invoke("Could not load Mii appearance: " + ex.Message); }
        }
        ApplyCapabilities();
    }
    private void BackupBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyCapabilities();

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _state is null || _state.Capability == MiiCapability.Unavailable) return;
        try
        {
            _state = _service.AddBasicDraft(_state, EditorAppearance());
            SelectSlot(_state.Slots.Last().Slot);
            StatusChanged?.Invoke("New Mii created in the draft. Choose Save to emulator when ready.");
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Mii maker"); }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _state is null || MiiList.SelectedItem is not MiiSlot selected) return;
        try
        {
            _state = _service.UpdateAppearanceDraft(_state, selected.Slot, EditorAppearance());
            SelectSlot(selected.Slot);
            StatusChanged?.Invoke("Mii changes are in the draft. Choose Save to emulator when ready.");
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Mii maker"); }
    }

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

    private void ExportDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _state is null) return;
        try
        {
            var bytes = _service.ExportDatabase(_state);
            var dialog = new SaveFileDialog
            {
                Title = "Export exact Mii database",
                FileName = _state.Target.Kind == MiiTargetKind.Wii ? "RFL_DB.dat" : "MiiDatabase.dat",
                Filter = "Mii database (*.*)|*.*"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            File.WriteAllBytes(dialog.FileName, bytes);
            StatusChanged?.Invoke("Exact Mii database exported without conversion.");
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Mii export"); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || BackupBox.SelectedItem is not MiiBackup backup) return;
        var snapshot = _state?.Target ?? _service.Capture(SelectedKind);
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

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _state is null || _state.Capability == MiiCapability.Unavailable) return;
        var dialog = new OpenFileDialog { Title = "Import exact Mii record", Filter = "Mii records (*.mii;*.miigx)|*.mii;*.miigx|All files (*.*)|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            _state = _service.ImportDraft(_state, File.ReadAllBytes(dialog.FileName));
            SelectSlot(_state.Slots.Last().Slot);
            StatusChanged?.Invoke("Record imported into offline draft. Live NAND is unchanged.");
            ApplyCapabilities();
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Mii editor"); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _state is not { IsDraft: true } state) return;
        var warning = "Save this Mii draft to the emulator?\n\n" +
                      "Host: " + state.Target.Host + " (" + state.Target.HostId + ")\nPath: " + state.Target.TargetPath +
                      "\n\nSESAME creates and verifies a local and remote backup before saving. The emulator must be closed. " +
                      "This remains experimental until this emulator path is manually certified.";
        if (MessageBox.Show(Window.GetWindow(this), warning, "Save Mii to emulator",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        const bool acknowledged = true;
        if (!state.CanExperimentalPush(acknowledged)) return;
        var allowUnknown = UnknownProcessBox.IsChecked == true;
        await RunExclusiveAsync(state.Target, async () =>
        {
            var result = await Task.Run(() => _service.PushDatabase(state, allowUnknown, acknowledged));
            StatusChanged?.Invoke(result.ReconciledAfterTransportFailure
                ? "Mii saved and reconciled after a transport failure."
                : "Mii saved to the emulator and verified.");
        });
        if (_client is { IsConnected: true }) await RefreshAsync();
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (_state is not { IsDraft: true }) return;
        if (MessageBox.Show(Window.GetWindow(this), "Discard the offline Mii draft? Live NAND is unchanged.",
                "Mii editor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _state = _liveState;
        if (_state is not null)
        {
            MiiList.ItemsSource = _state.Slots;
            MiiList.SelectedItem = _state.Slots.FirstOrDefault();
            IntegrityText.Text = _state.Integrity;
            ApplyCapabilities();
        }
    }

    private async Task ReloadBackupsAsync(MiiOperationSnapshot snapshot)
    {
        if (_service is null) return;
        var backups = await Task.Run(() => _service.Inventory(snapshot));
        BackupBox.ItemsSource = backups;
        BackupBox.SelectedIndex = backups.Count > 0 ? 0 : -1;
        ApplyCapabilities();
    }

    private void ConfigureAppearanceControls(MiiTargetKind kind)
    {
        HairStyleBox.ItemsSource = Enumerable.Range(0, kind == MiiTargetKind.Wii ? 72 : 132).ToArray();
        HairColorBox.ItemsSource = Enumerable.Range(0, kind == MiiTargetKind.Wii ? 8 : 100).ToArray();
        EyeColorBox.ItemsSource = Enumerable.Range(0, kind == MiiTargetKind.Wii ? 6 : 100).ToArray();
        FavoriteColorBox.ItemsSource = Enumerable.Range(0, 12).ToArray();
        GenderBox.SelectedIndex = 0;
        HairStyleBox.SelectedItem = 0;
        HairColorBox.SelectedItem = 0;
        EyeColorBox.SelectedItem = 0;
        FavoriteColorBox.SelectedItem = 0;
    }

    private MiiAppearance EditorAppearance() => new(
        NameBox.Text,
        GenderBox.SelectedIndex == 1,
        SelectedNumber(FavoriteColorBox),
        SelectedNumber(HairStyleBox),
        SelectedNumber(HairColorBox),
        SelectedNumber(EyeColorBox));

    private static int SelectedNumber(ComboBox box) => box.SelectedItem is int number ? number : 0;

    private void LoadAppearance(MiiAppearance appearance)
    {
        NameBox.Text = appearance.Name;
        GenderBox.SelectedIndex = appearance.IsFemale ? 1 : 0;
        HairStyleBox.SelectedItem = appearance.HairStyle;
        HairColorBox.SelectedItem = appearance.HairColor;
        EyeColorBox.SelectedItem = appearance.EyeColor;
        FavoriteColorBox.SelectedItem = appearance.FavoriteColor;
    }

    private void SelectSlot(int slot)
    {
        if (_state is null) return;
        MiiList.ItemsSource = _state.Slots;
        MiiList.SelectedItem = _state.Slots.FirstOrDefault(x => x.Slot == slot);
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
        DatabasePathBox.IsEnabled = DatabasePathBox.Visibility == Visibility.Visible && !IsWorking;
        ExportBtn.IsEnabled = valid && MiiList.SelectedItem is not null;
        ExportDatabaseBtn.IsEnabled = valid;
        CreateBtn.IsEnabled = valid;
        ApplyBtn.IsEnabled = valid && MiiList.SelectedItem is not null;
        ImportBtn.IsEnabled = valid;
        DiscardBtn.IsEnabled = _state?.IsDraft == true;
        SaveBtn.IsEnabled = _state is { IsDraft: true, Capability: not MiiCapability.Unavailable };
        RestoreBtn.IsEnabled = BackupBox.SelectedItem is MiiBackup;
        if (MiiList.SelectedItem is not MiiSlot) NameBox.Clear();
    }

    private void ShowUnavailable(string text)
    {
        CapabilityText.Text = "Capability: Unavailable";
        IntegrityText.Text = text;
        PathText.Text = "";
        ApplyCapabilities();
    }
}
