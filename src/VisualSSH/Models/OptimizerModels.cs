using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VisualSSH.Models;

public sealed class OptimizerGame : INotifyPropertyChanged
{
    private bool _selected = true;
    private string _displayName = "";
    private string _status = "Nieuw";
    private string _artworkSource = "—";
    private string _emulatorName = "—";
    private string _target = "";
    private string _startDir = "";
    private string _launchOptions = "";
    private string _category = "";
    private int _fps;
    private object? _cover;
    private object? _coverWide;
    private string? _coverUrl;
    private uint _steamAppId;
    private bool _inSteam;
    private bool _hasArtwork;
    private string _note = "";

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string FileName { get; set; } = "";
    public string RomPath { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string SystemId { get; set; } = "";
    public string SystemName { get; set; } = "";
    public string Status { get => _status; set => Set(ref _status, value); }
    public string ArtworkSource { get => _artworkSource; set => Set(ref _artworkSource, value); }
    public string EmulatorName { get => _emulatorName; set => Set(ref _emulatorName, value); }
    public string Target { get => _target; set => Set(ref _target, value); }
    public string StartDir { get => _startDir; set => Set(ref _startDir, value); }
    public string LaunchOptions { get => _launchOptions; set => Set(ref _launchOptions, value); }
    public string Category { get => _category; set => Set(ref _category, value); }
    public int Fps { get => _fps; set => Set(ref _fps, value); }
    public object? Cover { get => _cover; set => Set(ref _cover, value); }
    public object? CoverWide { get => _coverWide; set => Set(ref _coverWide, value); }
    public string? CoverUrl { get => _coverUrl; set => Set(ref _coverUrl, value); }
    public uint SteamAppId { get => _steamAppId; set => Set(ref _steamAppId, value); }
    public bool InSteam { get => _inSteam; set => Set(ref _inSteam, value); }
    public bool HasArtwork { get => _hasArtwork; set => Set(ref _hasArtwork, value); }
    public string Note { get => _note; set => Set(ref _note, value); }
    public string SearchQuery { get; set; } = "";
    public string CorePath { get; set; } = "";
    public bool IsRetroArch { get; set; }
    public string RetroArchCoreName { get; set; } = "";
    public bool IsRomHack { get; set; }
    public bool IsTranslation { get; set; }
    public bool LaunchLocked { get; set; }
    public ShortcutKind ShortcutKind { get; set; } = ShortcutKind.Rom;
    public bool IsRom => ShortcutKind == ShortcutKind.Rom;
    public int? SteamGridDbId { get; set; }
    public string? SelectedGridUrl { get; set; }
    public string? SelectedWideUrl { get; set; }
    public string? SelectedHeroUrl { get; set; }
    public string? SelectedLogoUrl { get; set; }
    public string? SelectedIconUrl { get; set; }
    public byte[]? GridBytes { get; set; }
    public byte[]? WideBytes { get; set; }
    public byte[]? HeroBytes { get; set; }
    public byte[]? LogoBytes { get; set; }
    public byte[]? IconBytes { get; set; }
    public object? Hero { get; set; }
    public object? Logo { get; set; }
    public object? Icon { get; set; }
    public List<ArtworkChoice> ArtworkChoices { get; } = new();
    public string KindText => ShortcutKind switch
    {
        ShortcutKind.Hydra => "Hydra",
        ShortcutKind.App => "App",
        _ => IsTranslation ? "Vertaling" : IsRomHack ? "ROM-hack" : "—"
    };

    public string SteamText => InSteam ? "ja" : "nee";
    public string ArtworkText => HasArtwork ? ArtworkSource : "ontbreekt";
    public string FpsText => Fps > 0 ? Fps + " fps" : "—";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(InSteam))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SteamText)));
        if (name is nameof(HasArtwork) or nameof(ArtworkSource))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ArtworkText)));
        if (name is nameof(Fps))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FpsText)));
    }
}

public enum ShortcutKind
{
    Rom,
    Hydra,
    App
}

public sealed class ArtworkChoice : INotifyPropertyChanged
{
    private object? _preview;
    public string Url { get; set; } = "";
    public string ThumbUrl { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "cover";
    public string Author { get; set; } = "";
    public string Style { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double ThumbW => Kind switch
    {
        "hero" => 168,
        "wide" => 148,
        "logo" => 120,
        "icon" => 64,
        _ => 76
    };
    public double ThumbH => Kind switch
    {
        "hero" => 54,
        "wide" => 70,
        "logo" => 48,
        "icon" => 64,
        _ => 114
    };
    public object? Preview
    {
        get => _preview;
        set
        {
            if (Equals(_preview, value)) return;
            _preview = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ArtworkPack
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public ArtworkChoice? Cover { get; set; }
    public ArtworkChoice? Wide { get; set; }
    public ArtworkChoice? Hero { get; set; }
    public ArtworkChoice? Logo { get; set; }
    public ArtworkChoice? Icon { get; set; }

    public IEnumerable<ArtworkChoice> Pieces
    {
        get
        {
            if (Cover is not null) yield return Cover;
            if (Wide is not null) yield return Wide;
            if (Hero is not null) yield return Hero;
            if (Logo is not null) yield return Logo;
            if (Icon is not null) yield return Icon;
        }
    }

    public int PieceCount => Pieces.Count();
}

public sealed class EmulatorTarget
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public string Exe { get; init; } = "";
    public string StartDir { get; init; } = "";
    public string LaunchOptions { get; init; } = "";
    public bool IsRetroArch { get; set; }
    public string CorePath { get; init; } = "";
    public string CoreName { get; set; } = "";
}

public sealed class OptimizeProgress
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public double Percent { get; set; }
    public bool Indeterminate { get; set; }
    public int Current { get; set; }
    public int Total { get; set; }
}

public sealed class OptimizeReport
{
    public int Applied { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int ArtworkWritten { get; set; }
    public int ArtworkKept { get; set; }
    public string Summary { get; set; } = "";
    public List<string> Errors { get; } = new();
}
