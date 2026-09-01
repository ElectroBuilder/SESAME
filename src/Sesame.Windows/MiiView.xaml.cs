using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Sesame.Services;
using Sesame.Services.Mii;

namespace Sesame;

public sealed record MiiChoice(int Id, string Name, Brush Swatch, string Glyph);

public partial class MiiView : UserControl
{
    private DeckClient? _client;
    private MiiService? _service;
    private MiiTargetState? _state;
    private MiiTargetState? _liveState;
    private readonly MiiOperationLock _operationLock = new();
    private readonly FflRenderer _fflRenderer = new();
    private CancellationTokenSource? _previewCts;
    private int _previewGeneration;
    private bool _updatingPaths;
    private readonly Dictionary<MiiTargetKind, string> _selectedPaths = [];

    public MiiView()
    {
        InitializeComponent();
        _operationLock.StateChanged += busy => BusyChanged?.Invoke(busy);
        TargetBox.SelectedIndex = 0;
        ConfigureAppearanceControls(MiiTargetKind.Wii);
        ShowUnavailable("Connect to a Steam Deck to inspect Miis.");
        var app = Application.Current;
        if (app is not null) app.Exit += (_, _) => _fflRenderer.Dispose();
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
        _previewCts?.Cancel();
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

    private void EditorValueChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();
    private void EditorTextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void GenderChanged(object sender, RoutedEventArgs e) => UpdatePreview();
    private void BackupBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyCapabilities();

    private void ChooseFflResource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose FFLResHigh.dat or AFLResHigh_2_3.dat",
            Filter = "FFL resource (*.dat)|*.dat|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            FflRenderer.SaveResourcePath(dialog.FileName);
            StatusChanged?.Invoke("FFL resource selected. Rendering the Eden preview…");
            UpdatePreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "FFL renderer");
        }
    }

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
        HairStyleBox.ItemsSource = HairChoices(kind);
        HairColorBox.ItemsSource = ColourChoices(kind, hair: true);
        EyeColorBox.ItemsSource = ColourChoices(kind, hair: false);
        FavoriteColorBox.ItemsSource = FavoriteChoices();
        MaleRadio.IsChecked = true;
        FemaleRadio.IsChecked = false;
        HairStyleBox.SelectedIndex = 0;
        HairColorBox.SelectedIndex = 0;
        EyeColorBox.SelectedIndex = 0;
        FavoriteColorBox.SelectedIndex = 0;
        UpdatePreview();
    }

    private MiiAppearance EditorAppearance() => new(
        NameBox.Text,
        FemaleRadio.IsChecked == true,
        SelectedNumber(FavoriteColorBox),
        SelectedNumber(HairStyleBox),
        SelectedNumber(HairColorBox),
        SelectedNumber(EyeColorBox));

    private static int SelectedNumber(ComboBox box) => box.SelectedItem is MiiChoice choice ? choice.Id : 0;

    private void LoadAppearance(MiiAppearance appearance)
    {
        NameBox.Text = appearance.Name;
        FemaleRadio.IsChecked = appearance.IsFemale;
        MaleRadio.IsChecked = !appearance.IsFemale;
        SelectChoice(HairStyleBox, appearance.HairStyle);
        SelectChoice(HairColorBox, appearance.HairColor);
        SelectChoice(EyeColorBox, appearance.EyeColor);
        SelectChoice(FavoriteColorBox, appearance.FavoriteColor);
        UpdatePreview();
    }

    private static void SelectChoice(ComboBox box, int id)
    {
        box.SelectedItem = box.Items.OfType<MiiChoice>().FirstOrDefault(x => x.Id == id) ?? box.Items[0];
    }

    private void UpdatePreview()
    {
        // WPF raises Checked/SelectionChanged while InitializeComponent is still
        // materialising the editor. Do not read sibling controls until the full
        // visual tree exists; otherwise startup fails before the main window opens.
        if (AvatarPreview is null || NameBox is null || MaleRadio is null || FemaleRadio is null ||
            HairStyleBox is null || HairColorBox is null || EyeColorBox is null || FavoriteColorBox is null)
            return;
        var appearance = EditorAppearance();
        AvatarPreview.Appearance = appearance;
        AvatarPreview.RenderedImage = null;
        FflPreviewText.Text = "Live preview · emulator-safe fields";
        RequestRealPreview(appearance);
    }

    private void RequestRealPreview(MiiAppearance appearance)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        var cts = _previewCts = new CancellationTokenSource();
        var generation = ++_previewGeneration;
        _ = RenderRealPreviewAsync(appearance, generation, cts.Token);
    }

    private async Task RenderRealPreviewAsync(MiiAppearance appearance, int generation,
        CancellationToken cancellationToken)
    {
        if (SelectedKind != MiiTargetKind.Eden || _service is null || _state is null || !_state.CanExport)
            return;
        byte[] record;
        try
        {
            if (MiiList.SelectedItem is MiiSlot selected)
            {
                var previewState = _service.UpdateAppearanceDraft(_state, selected.Slot, appearance);
                record = _service.ExportRecord(previewState, selected.Slot);
            }
            else
            {
                var previewState = _service.AddBasicDraft(_state, appearance);
                record = _service.ExportRecord(previewState, previewState.Slots.Last().Slot);
            }
        }
        catch { return; }

        try
        {
            await Task.Delay(120, cancellationToken);
            var image = await _fflRenderer.RenderAsync(record, cancellationToken);
            if (image is null || cancellationToken.IsCancellationRequested || generation != _previewGeneration)
                return;
            AvatarPreview.RenderedImage = image;
            FflPreviewText.Text = "Live preview · Eden FFL renderer";
        }
        catch (OperationCanceledException) { }
        catch { /* the vector preview remains available when the optional helper is unavailable */ }
    }

    private static IReadOnlyList<MiiChoice> HairChoices(MiiTargetKind kind)
    {
        var max = kind == MiiTargetKind.Wii ? 72 : 132;
        var names = new[] { "Classic", "Side sweep", "Parted", "Bob", "Curls", "Spiky" };
        return Enumerable.Range(0, max).Select(i => new MiiChoice(i,
            $"{names[i % names.Length]} {i / names.Length + 1}",
            new SolidColorBrush(Color.FromRgb(64, 46, 39)), names[i % names.Length][0].ToString())).ToArray();
    }

    private static IReadOnlyList<MiiChoice> ColourChoices(MiiTargetKind kind, bool hair)
    {
        var max = kind == MiiTargetKind.Wii ? (hair ? 8 : 6) : 100;
        var palette = hair
            ? new[] { "Black", "Brown", "Auburn", "Blonde", "White", "Gray", "Red", "Blue" }
            : new[] { "Brown", "Dark brown", "Blue", "Green", "Violet", "Black" };
        return Enumerable.Range(0, max).Select(i => new MiiChoice(i,
            i < palette.Length ? palette[i] : $"Colour {i + 1}",
            new SolidColorBrush(hair ? HairPalette(i) : EyePalette(i)), "")).ToArray();
    }

    private static IReadOnlyList<MiiChoice> FavoriteChoices() =>
        new[] { "Blue", "Red", "Green", "Yellow", "Purple", "Orange", "Turquoise", "Pink", "Indigo", "Brown", "Lime", "Gray" }
            .Select((name, i) => new MiiChoice(i, name, new SolidColorBrush(FavoritePalette(i)), "")).ToArray();

    private static Color HairPalette(int i) => new[]
    {
        Color.FromRgb(40,29,24), Color.FromRgb(92,55,35), Color.FromRgb(173,111,57), Color.FromRgb(224,178,87),
        Color.FromRgb(220,220,220), Color.FromRgb(137,137,145), Color.FromRgb(194,69,62), Color.FromRgb(56,77,125)
    }[Math.Abs(i) % 8];

    private static Color EyePalette(int i) => new[]
    {
        Color.FromRgb(36,28,24), Color.FromRgb(73,48,32), Color.FromRgb(63,107,151),
        Color.FromRgb(74,133,82), Color.FromRgb(111,71,126), Color.FromRgb(45,45,52)
    }[Math.Abs(i) % 6];

    private static Color FavoritePalette(int i) => new[]
    {
        Color.FromRgb(77,145,205), Color.FromRgb(220,76,76), Color.FromRgb(92,175,105), Color.FromRgb(235,180,59),
        Color.FromRgb(157,102,194), Color.FromRgb(238,130,63), Color.FromRgb(47,166,164), Color.FromRgb(230,102,153),
        Color.FromRgb(92,107,192), Color.FromRgb(118,92,67), Color.FromRgb(101,172,87), Color.FromRgb(90,90,98)
    }[Math.Abs(i) % 12];

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
