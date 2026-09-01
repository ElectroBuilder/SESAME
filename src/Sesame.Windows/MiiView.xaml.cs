using System.IO;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Microsoft.Win32;
using Sesame.Services;
using Sesame.Services.Mii;

namespace Sesame;

public sealed class MiiChoice : INotifyPropertyChanged
{
    public MiiChoice(int id, string name, Brush swatch, string glyph)
    {
        Id = id;
        Name = name;
        Swatch = swatch;
        Glyph = glyph;
    }

    public int Id { get; }
    public string Name { get; }
    public Brush Swatch { get; }
    public string Glyph { get; }

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MiiCard : INotifyPropertyChanged
{
    public MiiCard(MiiSlot slot) => Slot = slot;
    public MiiSlot Slot { get; private set; }
    public int SlotIndex => Slot.Slot;
    public string Name => Slot.Name;
    public string Id => Slot.Id;
    private ImageSource? _image;
    public ImageSource? Image
    {
        get => _image;
        set { _image = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateSlot(MiiSlot slot)
    {
        Slot = slot;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SlotIndex)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Id)));
    }
}

public partial class MiiView : UserControl
{
    private DeckClient? _client;
    private MiiService? _service;
    private MiiTargetState? _state;
    private MiiTargetState? _liveState;
    private readonly MiiOperationLock _operationLock = new();
    private readonly FflRenderer _fflRenderer = new();
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _cardRenderCts;
    private CancellationTokenSource? _choiceRenderCts;
    private int _previewGeneration;
    private int _cardRenderGeneration;
    private int _choiceRenderGeneration;
    private bool _updatingPaths;
    private bool _suppressEditorEvents;
    private MiiAppearance? _loadedAppearance;
    private MiiAppearance? _editorAppearance;
    private int? _selectedSlot;
    private readonly Random _random = new();
    private readonly ObservableCollection<MiiCard> _miiCards = [];
    private readonly Dictionary<MiiTargetKind, string> _selectedPaths = [];

    public MiiView()
    {
        InitializeComponent();
        _operationLock.StateChanged += busy => BusyChanged?.Invoke(busy);
        TargetBox.SelectedIndex = 0;
        ConfigureAppearanceControls(MiiTargetKind.Wii);
        ShowUnavailable("Connect to a Steam Deck to inspect Miis.");
        Loaded += (_, _) =>
        {
            UpdatePreview();
            StartChoiceThumbnails();
        };
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
        _cardRenderCts?.Cancel();
        _choiceRenderCts?.Cancel();
        _state = null;
        _liveState = null;
        _loadedAppearance = null;
        _editorAppearance = null;
        _selectedSlot = null;
        AvatarPreview.RenderedImage = null;
        FflPreviewText.Text = "Connect to a Steam Deck to render a Mii.";
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
            BindMiiCards(loaded);
            MiiList.SelectedIndex = _miiCards.Count > 0 ? 0 : -1;
            _ = RenderMiiCardsAsync(loaded);
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
        if (_suppressEditorEvents) return;
        if (_service is not null && _state is not null && MiiList.SelectedItem is MiiCard card)
        {
            var selected = _state.Slots.FirstOrDefault(x => x.Slot == card.SlotIndex) ?? card.Slot;
            _selectedSlot = selected.Slot;
            try { LoadAppearance(_service.GetAppearance(_state, selected.Slot)); }
            catch (Exception ex) { StatusChanged?.Invoke("Could not load Mii appearance: " + ex.Message); }
        }
        ApplyCapabilities();
    }

    private MiiSlot? SelectedMii =>
        _selectedSlot is { } slot && _state?.Slots.FirstOrDefault(x => x.Slot == slot) is { } current
            ? current
            : MiiList.SelectedItem is MiiCard card ? card.Slot : null;

    private void EditorValueChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressEditorEvents) UpdatePreview();
    }

    private void EditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressEditorEvents) UpdatePreview();
    }

    private void GenderChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents || HairGallery is null) return;
        var currentHair = SelectedNumber(HairGallery);
        HairGallery.ItemsSource = HairChoices(SelectedKind, FemaleRadio.IsChecked == true);
        SelectChoice(HairGallery, currentHair);
        UpdatePreview();
    }

    private void HairGallery_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)) return;
        // ListBox handles these keys, but keeping focus here makes keyboard
        // navigation deterministic even when the pointer is over a thumbnail.
        if (sender is ListBox gallery) gallery.Focus();
    }

    private void BackupBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyCapabilities();

    private void CreateMiiCard_Click(object sender, RoutedEventArgs e) => Create_Click(sender, e);

    private void Randomize_Click(object sender, RoutedEventArgs e)
    {
        _suppressEditorEvents = true;
        try
        {
            MaleRadio.IsChecked = _random.Next(2) == 0;
            FemaleRadio.IsChecked = !MaleRadio.IsChecked;
            HairGallery.ItemsSource = HairChoices(SelectedKind, FemaleRadio.IsChecked == true);
            SetRandom(HairGallery);
            SetRandom(HairColorGallery);
            SetRandom(EyeColorGallery);
            SetRandom(FaceColorGallery);
            SetRandom(EyebrowColorGallery);
            SetRandom(MouthColorGallery);
            SetRandom(BeardColorGallery);
            SetRandom(GlassesColorGallery);
            SetRandom(FavoriteColorGallery);
            SetRandom(FaceGallery);
            SetRandom(EyeGallery);
            SetRandom(EyebrowGallery);
            SetRandom(NoseGallery);
            SetRandom(MouthGallery);
            SetRandom(GlassesGallery);
            SetRandom(MoleGallery);
        }
        finally { _suppressEditorEvents = false; }
        UpdatePreview();
        StartChoiceThumbnails();
    }

    private void SetRandom(ListBox gallery)
    {
        if (gallery.Items.Count > 0) gallery.SelectedIndex = _random.Next(gallery.Items.Count);
    }

    private void ChoiceGallery_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox gallery || gallery.Items.Count == 0) return;
        var columns = int.TryParse(gallery.Tag?.ToString(), out var value) ? value : 4;
        var index = gallery.SelectedIndex < 0 ? 0 : gallery.SelectedIndex;
        var next = e.Key switch
        {
            Key.Left => index - 1,
            Key.Right => index + 1,
            Key.Up => index - columns,
            Key.Down => index + columns,
            Key.Home => 0,
            Key.End => gallery.Items.Count - 1,
            _ => index
        };
        if (next == index && e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)) return;
        if (next < 0) next = gallery.Items.Count - 1;
        if (next >= gallery.Items.Count) next = 0;
        gallery.SelectedIndex = next;
        gallery.ScrollIntoView(gallery.SelectedItem);
        e.Handled = true;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _state is null || _state.Capability == MiiCapability.Unavailable) return;
        try
        {
            _state = _service.AddBasicDraft(_state, EditorAppearance());
            SelectSlot(_state.Slots.Last().Slot);
            _ = RenderMiiCardsAsync(_state);
            StatusChanged?.Invoke("New Mii created in the draft. Choose Save to emulator when ready.");
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Mii maker"); }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _state is null || SelectedMii is not { } selected) return;
        try
        {
            _state = _service.UpdateAppearanceDraft(_state, selected.Slot, EditorAppearance());
            SelectSlot(selected.Slot);
            _ = RenderMiiCardsAsync(_state);
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
        if (_service is null || _state is null || SelectedMii is not { } selected) return;
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
            _ = RenderMiiCardsAsync(_state);
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
            BindMiiCards(_state);
            MiiList.SelectedItem = _miiCards.FirstOrDefault();
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
        _loadedAppearance = null;
        _suppressEditorEvents = true;
        try
        {
            HairGallery.ItemsSource = HairChoices(kind, female: false);
            var commonColors = kind == MiiTargetKind.Wii ? 8 : 100;
            HairColorGallery.ItemsSource = ColourChoices(kind, commonColors, hair: true);
            EyeColorGallery.ItemsSource = ColourChoices(kind, kind == MiiTargetKind.Wii ? 6 : 100, hair: false);
            FaceColorGallery.ItemsSource = ColourChoices(kind, kind == MiiTargetKind.Wii ? 6 : 10, hair: false, skin: true);
            EyebrowColorGallery.ItemsSource = ColourChoices(kind, commonColors, hair: true);
            MouthColorGallery.ItemsSource = ColourChoices(kind, 4, hair: false);
            BeardColorGallery.ItemsSource = ColourChoices(kind, commonColors, hair: true);
            GlassesColorGallery.ItemsSource = ColourChoices(kind, commonColors, hair: false);
            FavoriteColorGallery.ItemsSource = FavoriteChoices();
            FaceGallery.ItemsSource = PartChoices(kind, "Face", kind == MiiTargetKind.Wii ? 6 : 12);
            EyeGallery.ItemsSource = PartChoices(kind, "Eye", kind == MiiTargetKind.Wii ? 48 : 60);
            EyebrowGallery.ItemsSource = PartChoices(kind, "Brow", kind == MiiTargetKind.Wii ? 24 : 32);
            NoseGallery.ItemsSource = PartChoices(kind, "Nose", kind == MiiTargetKind.Wii ? 12 : 18);
            MouthGallery.ItemsSource = PartChoices(kind, "Mouth", kind == MiiTargetKind.Wii ? 24 : 36);
            GlassesGallery.ItemsSource = PartChoices(kind, "Glasses", kind == MiiTargetKind.Wii ? 9 : 20);
            MoleGallery.ItemsSource = PartChoices(kind, "Mole", kind == MiiTargetKind.Wii ? 2 : 2);
            MaleRadio.IsChecked = true;
            FemaleRadio.IsChecked = false;
            HairGallery.SelectedIndex = 0;
            foreach (var gallery in AllGalleries()) gallery.SelectedIndex = 0;
        }
        finally { _suppressEditorEvents = false; }
        UpdatePreview();
    }

    private MiiAppearance EditorAppearance()
    {
        var old = _editorAppearance ?? _loadedAppearance;
        return new MiiAppearance(
            NameBox.Text,
            FemaleRadio.IsChecked == true,
            SelectedNumber(FavoriteColorGallery),
            SelectedNumber(HairGallery),
            SelectedNumber(HairColorGallery),
            SelectedNumber(EyeColorGallery))
        {
            HasAdvancedParts = true,
            Height = old?.Height ?? 0, Build = old?.Build ?? 0, HairFlip = old?.HairFlip ?? 0,
            FaceType = SelectedNumber(FaceGallery), FaceColor = SelectedNumber(FaceColorGallery),
            FaceMakeup = old?.FaceMakeup ?? 0, FaceWrinkle = old?.FaceWrinkle ?? 0,
            EyeType = SelectedNumber(EyeGallery), EyeScale = old?.EyeScale ?? 0,
            EyeAspect = old?.EyeAspect ?? 0, EyeRotate = old?.EyeRotate ?? 0,
            EyeSpacing = old?.EyeSpacing ?? 0, EyePosition = old?.EyePosition ?? 0,
            EyebrowType = SelectedNumber(EyebrowGallery), EyebrowColor = SelectedNumber(EyebrowColorGallery),
            EyebrowScale = old?.EyebrowScale ?? 0, EyebrowAspect = old?.EyebrowAspect ?? 0,
            EyebrowRotate = old?.EyebrowRotate ?? 0, EyebrowSpacing = old?.EyebrowSpacing ?? 0,
            EyebrowPosition = old?.EyebrowPosition ?? 0, NoseType = SelectedNumber(NoseGallery),
            NoseScale = old?.NoseScale ?? 0, NosePosition = old?.NosePosition ?? 0,
            MouthType = SelectedNumber(MouthGallery), MouthColor = SelectedNumber(MouthColorGallery),
            MouthScale = old?.MouthScale ?? 0, MouthAspect = old?.MouthAspect ?? 0,
            MouthPosition = old?.MouthPosition ?? 0, BeardType = old?.BeardType ?? 0,
            BeardColor = SelectedNumber(BeardColorGallery), MustacheType = old?.MustacheType ?? 0,
            MustacheScale = old?.MustacheScale ?? 0, MustachePosition = old?.MustachePosition ?? 0,
            GlassesType = SelectedNumber(GlassesGallery), GlassesColor = SelectedNumber(GlassesColorGallery),
            GlassesScale = old?.GlassesScale ?? 0, GlassesPosition = old?.GlassesPosition ?? 0,
            MoleType = SelectedNumber(MoleGallery), MoleScale = old?.MoleScale ?? 0,
            MoleX = old?.MoleX ?? 0, MoleY = old?.MoleY ?? 0
        };
    }

    private static int SelectedNumber(Selector control) => control.SelectedItem is MiiChoice choice ? choice.Id : 0;

    private void LoadAppearance(MiiAppearance appearance)
    {
        _loadedAppearance = appearance;
        _editorAppearance = appearance.Clone();
        _suppressEditorEvents = true;
        try
        {
            NameBox.Text = appearance.Name;
            FemaleRadio.IsChecked = appearance.IsFemale;
            MaleRadio.IsChecked = !appearance.IsFemale;
            HairGallery.ItemsSource = HairChoices(SelectedKind, appearance.IsFemale);
            SelectChoice(HairGallery, appearance.HairStyle);
            SelectChoice(HairColorGallery, appearance.HairColor);
            SelectChoice(EyeColorGallery, appearance.EyeColor);
            SelectChoice(FavoriteColorGallery, appearance.FavoriteColor);
            SelectChoice(FaceGallery, appearance.FaceType);
            SelectChoice(FaceColorGallery, appearance.FaceColor);
            SelectChoice(EyeGallery, appearance.EyeType);
            SelectChoice(EyebrowGallery, appearance.EyebrowType);
            SelectChoice(NoseGallery, appearance.NoseType);
            SelectChoice(MouthGallery, appearance.MouthType);
            SelectChoice(GlassesGallery, appearance.GlassesType);
            SelectChoice(MoleGallery, appearance.MoleType);
            SelectChoice(EyebrowColorGallery, appearance.EyebrowColor);
            SelectChoice(MouthColorGallery, appearance.MouthColor);
            SelectChoice(BeardColorGallery, appearance.BeardColor);
            SelectChoice(GlassesColorGallery, appearance.GlassesColor);
        }
        finally { _suppressEditorEvents = false; }
        UpdatePreview();
        StartChoiceThumbnails();
    }

    private static void SelectChoice(Selector box, int id)
    {
        if (box.Items.Count == 0) return;
        box.SelectedItem = box.Items.OfType<MiiChoice>().FirstOrDefault(x => x.Id == id) ?? box.Items[0];
    }

    private void StartChoiceThumbnails()
    {
        if (!IsLoaded || HairGallery is null) return;
        _choiceRenderCts?.Cancel();
        _choiceRenderCts?.Dispose();
        var cts = _choiceRenderCts = new CancellationTokenSource();
        var generation = ++_choiceRenderGeneration;
        _ = RenderChoiceThumbnailsAsync(SelectedKind, EditorAppearance(), generation, cts.Token);
    }

    private async Task RenderChoiceThumbnailsAsync(MiiTargetKind kind, MiiAppearance baseAppearance,
        int generation, CancellationToken cancellationToken)
    {
        // These are the high-value galleries: each tile is real FFL output,
        // so hair and eyes are recognisable instead of abstract labels.
        var galleries = new[]
        {
            (Gallery: HairGallery, Part: "HairStyle"),
            (Gallery: HairColorGallery, Part: "HairColor"),
            (Gallery: EyeColorGallery, Part: "EyeColor"),
            (Gallery: EyeGallery, Part: "EyeType"),
            (Gallery: NoseGallery, Part: "NoseType")
        };
        try
        {
            foreach (var (gallery, part) in galleries)
            {
                foreach (var choice in gallery.Items.OfType<MiiChoice>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = WithChoice(baseAppearance, part, choice.Id);
                    var record = await BuildPreviewRecordAsync(kind, candidate, cancellationToken);
                    var image = await _fflRenderer.RenderAsync(record, cancellationToken, resolution: 96);
                    if (image is not null && generation == _choiceRenderGeneration &&
                        !cancellationToken.IsCancellationRequested)
                        choice.Thumbnail = part is "EyeType" or "EyeColor" or "NoseType"
                            ? CropFace(image)
                            : image;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusChanged?.Invoke("Mii-keuzethumbnails niet beschikbaar: " + ex.Message); }
    }

    private async Task<byte[]> BuildPreviewRecordAsync(MiiTargetKind kind, MiiAppearance appearance,
        CancellationToken cancellationToken)
    {
        var service = _service;
        var state = _state;
        var selected = SelectedMii;
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (service is not null && state is { CanExport: true } && selected is not null)
            {
                var previewState = service.UpdateAppearanceDraft(state, selected.Slot, appearance);
                return service.ExportRecord(previewState, selected.Slot);
            }
            return CreatePreviewRecord(kind, appearance);
        }, cancellationToken);
    }

    private static MiiAppearance WithChoice(MiiAppearance source, string part, int value)
    {
        var result = source.Clone();
        switch (part)
        {
            case "HairStyle": result.HairStyle = value; break;
            case "HairColor": result.HairColor = value; break;
            case "EyeColor": result.EyeColor = value; break;
            case "EyeType": result.EyeType = value; break;
            case "NoseType": result.NoseType = value; break;
        }
        return result;
    }

    private static ImageSource CropFace(ImageSource source)
    {
        if (source is not BitmapSource bitmap) return source;
        var width = Math.Max(1, Math.Min(bitmap.PixelWidth, bitmap.PixelWidth * 2 / 3));
        var height = Math.Max(1, Math.Min(bitmap.PixelHeight, bitmap.PixelHeight * 2 / 3));
        var x = Math.Max(0, (bitmap.PixelWidth - width) / 2);
        var y = Math.Max(0, bitmap.PixelHeight / 10);
        if (y + height > bitmap.PixelHeight) y = bitmap.PixelHeight - height;
        var crop = new CroppedBitmap(bitmap, new Int32Rect(x, y, width, height));
        crop.Freeze();
        return crop;
    }

    private void UpdatePreview()
    {
        // WPF raises Checked/SelectionChanged while InitializeComponent is still
        // materialising the editor. Do not read sibling controls until the full
        // visual tree exists; otherwise startup fails before the main window opens.
        if (!IsLoaded || AvatarPreview is null || NameBox is null || MaleRadio is null || FemaleRadio is null ||
            HairGallery is null || HairColorGallery is null || EyeColorGallery is null || FavoriteColorGallery is null)
            return;
        var appearance = EditorAppearance();
        CommitEditorDraft(appearance);
        FflPreviewText.Text = AvatarPreview.RenderedImage is null
            ? "Rendering real Mii preview…"
            : "Updating real Mii preview…";
        RequestRealPreview(appearance);
    }

    private void CommitEditorDraft(MiiAppearance appearance)
    {
        if (_suppressEditorEvents || _service is null || _state is not { CanExport: true } state ||
            SelectedMii is not { } selected)
        {
            _editorAppearance = appearance.Clone();
            _loadedAppearance = appearance;
            return;
        }

        try
        {
            _state = _service.UpdateAppearanceDraft(state, selected.Slot, appearance);
            if (_miiCards.FirstOrDefault(x => x.SlotIndex == selected.Slot) is { } card &&
                _state.Slots.FirstOrDefault(x => x.Slot == selected.Slot) is { } updatedSlot)
                card.UpdateSlot(updatedSlot);
            _editorAppearance = appearance.Clone();
            _loadedAppearance = appearance;
        }
        catch (Exception ex)
        {
            FflPreviewText.Text = "Wijziging niet opgeslagen · " + ex.Message;
            StatusChanged?.Invoke("Mii wijziging kon niet in de draft worden gezet: " + ex.Message);
        }
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
        var kind = SelectedKind;
        var state = _state;
        var selected = SelectedMii;
        byte[] record;
        try
        {
            if (_service is not null && state is { CanExport: true })
            {
                if (selected is not null)
                {
                    var previewState = _service.UpdateAppearanceDraft(state, selected.Slot, appearance);
                    record = _service.ExportRecord(previewState, selected.Slot);
                }
                else
                {
                    var previewState = _service.AddBasicDraft(state, appearance);
                    record = _service.ExportRecord(previewState, previewState.Slots.Last().Slot);
                }
            }
            else
            {
                // A real preview should also work before a database exists.
                // The editor remains read-only in that state, but FFL can
                // still render a valid basic record for the selected platform.
                record = CreatePreviewRecord(kind, appearance);
            }
        }
        catch (Exception ex)
        {
            FflPreviewText.Text = "FFL preview unavailable · " + ex.Message;
            return;
        }

        try
        {
            await Task.Delay(120, cancellationToken);
            var image = await _fflRenderer.RenderAsync(record, cancellationToken);
            if (image is null || cancellationToken.IsCancellationRequested || generation != _previewGeneration)
            {
                if (!cancellationToken.IsCancellationRequested && generation == _previewGeneration &&
                    !string.IsNullOrWhiteSpace(_fflRenderer.LastError))
                    FflPreviewText.Text = "FFL preview unavailable · " + _fflRenderer.LastError;
                return;
            }
            AvatarPreview.RenderedImage = image;
            AvatarPreview.PlayRevealAnimation();
            FflPreviewText.Text = $"Live preview · FFL renderer ({kind})";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            FflPreviewText.Text = "FFL preview unavailable · " + ex.Message;
        }
    }

    private static byte[] CreatePreviewRecord(MiiTargetKind kind, MiiAppearance appearance)
    {
        var name = string.IsNullOrWhiteSpace(appearance.Name) ? "Mii" : appearance.Name;
        if (kind == MiiTargetKind.Wii)
        {
            var format = new MiiFormatWii();
            var database = format.Insert(MiiFormatWii.CreateEmptyDatabase(), format.CreateBasicRecord(name));
            return format.ExportRecord(format.UpdateAppearance(database, 0, appearance), 0);
        }

        var switchFormat = new MiiFormatSwitch();
        var switchDatabase = switchFormat.Insert(MiiFormatSwitch.CreateEmptyDatabase(),
            switchFormat.CreateBasicRecord(name));
        return switchFormat.ExportRecord(switchFormat.UpdateAppearance(switchDatabase, 0, appearance), 0);
    }

    private static IReadOnlyList<MiiChoice> HairChoices(MiiTargetKind kind, bool female)
    {
        var max = kind == MiiTargetKind.Wii ? 72 : 132;
        var names = female
            ? new[] { "Bob", "Side sweep", "Parted", "Curls", "Long", "Ponytail" }
            : new[] { "Classic", "Side sweep", "Parted", "Short", "Curls", "Spiky" };
        return Enumerable.Range(0, max).Select(i => new MiiChoice(i,
            $"{names[i % names.Length]} {i / names.Length + 1}",
            new SolidColorBrush(Color.FromRgb(64, 46, 39)), names[i % names.Length][0].ToString())).ToArray();
    }

    private IEnumerable<ListBox> AllGalleries() =>
    [
        HairGallery, HairColorGallery, EyeColorGallery, FaceColorGallery, EyebrowColorGallery,
        MouthColorGallery, BeardColorGallery, GlassesColorGallery, FavoriteColorGallery,
        FaceGallery, EyeGallery, EyebrowGallery, NoseGallery, MouthGallery,
        GlassesGallery, MoleGallery
    ];

    private static IReadOnlyList<MiiChoice> PartChoices(MiiTargetKind kind, string label, int count) =>
        Enumerable.Range(0, count).Select(i => new MiiChoice(i, $"{label} {i + 1}",
            new SolidColorBrush(PartPalette(i, label)), $"{i + 1:00}")).ToArray();

    private static IReadOnlyList<MiiChoice> ColourChoices(MiiTargetKind kind, int count, bool hair, bool skin = false)
    {
        var palette = skin
            ? new[] { "Porcelain", "Fair", "Peach", "Warm", "Tan", "Brown", "Deep", "Custom" }
            : hair
            ? new[] { "Black", "Brown", "Auburn", "Blonde", "White", "Gray", "Red", "Blue" }
            : new[] { "Brown", "Dark brown", "Blue", "Green", "Violet", "Black" };
        return Enumerable.Range(0, count).Select(i => new MiiChoice(i,
            i < palette.Length ? palette[i] : $"Colour {i + 1}",
            new SolidColorBrush(skin ? SkinPalette(i) : hair ? HairPalette(i) : EyePalette(i)), "")).ToArray();
    }

    private static Color PartPalette(int i, string label)
    {
        var palettes = new[]
        {
            Color.FromRgb(54, 84, 105), Color.FromRgb(91, 74, 126), Color.FromRgb(65, 128, 116),
            Color.FromRgb(133, 91, 68), Color.FromRgb(99, 111, 128), Color.FromRgb(128, 79, 101)
        };
        return palettes[(Math.Abs(i) + label.Length) % palettes.Length];
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

    private static Color SkinPalette(int i) => new[]
    {
        Color.FromRgb(255, 229, 196), Color.FromRgb(248, 204, 163), Color.FromRgb(232, 174, 126),
        Color.FromRgb(198, 135, 92), Color.FromRgb(150, 91, 61), Color.FromRgb(103, 61, 46),
        Color.FromRgb(74, 45, 38), Color.FromRgb(224, 157, 116)
    }[Math.Abs(i) % 8];

    private static Color FavoritePalette(int i) => new[]
    {
        Color.FromRgb(77,145,205), Color.FromRgb(220,76,76), Color.FromRgb(92,175,105), Color.FromRgb(235,180,59),
        Color.FromRgb(157,102,194), Color.FromRgb(238,130,63), Color.FromRgb(47,166,164), Color.FromRgb(230,102,153),
        Color.FromRgb(92,107,192), Color.FromRgb(118,92,67), Color.FromRgb(101,172,87), Color.FromRgb(90,90,98)
    }[Math.Abs(i) % 12];

    private void BindMiiCards(MiiTargetState state)
    {
        _cardRenderCts?.Cancel();
        _miiCards.Clear();
        foreach (var slot in state.Slots) _miiCards.Add(new MiiCard(slot));
        MiiList.ItemsSource = _miiCards;
    }

    private async Task RenderMiiCardsAsync(MiiTargetState state)
    {
        if (_service is not { } service || !state.CanExport) return;
        _cardRenderCts?.Cancel();
        _cardRenderCts?.Dispose();
        var cts = _cardRenderCts = new CancellationTokenSource();
        var generation = ++_cardRenderGeneration;
        var cards = _miiCards.ToArray();
        try
        {
            foreach (var card in cards)
            {
                cts.Token.ThrowIfCancellationRequested();
                var record = await Task.Run(() => service.ExportRecord(state, card.SlotIndex), cts.Token);
                var image = await _fflRenderer.RenderAsync(record, cts.Token, resolution: 112);
                if (image is not null && generation == _cardRenderGeneration && !cts.IsCancellationRequested)
                    card.Image = image;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusChanged?.Invoke("Mii thumbnails unavailable: " + ex.Message); }
    }

    private void SelectSlot(int slot)
    {
        if (_state is null) return;
        BindMiiCards(_state);
        MiiList.SelectedItem = _miiCards.FirstOrDefault(x => x.SlotIndex == slot);
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
        ExportBtn.IsEnabled = valid && SelectedMii is not null;
        ExportDatabaseBtn.IsEnabled = valid;
        CreateBtn.IsEnabled = valid;
        ApplyBtn.IsEnabled = valid && SelectedMii is not null;
        ImportBtn.IsEnabled = valid;
        DiscardBtn.IsEnabled = _state?.IsDraft == true;
        SaveBtn.IsEnabled = _state is { IsDraft: true, Capability: not MiiCapability.Unavailable };
        RestoreBtn.IsEnabled = BackupBox.SelectedItem is MiiBackup;
        if (SelectedMii is null) NameBox.Clear();
    }

    private void ShowUnavailable(string text)
    {
        CapabilityText.Text = "Capability: Unavailable";
        IntegrityText.Text = text;
        PathText.Text = "";
        ApplyCapabilities();
    }
}
