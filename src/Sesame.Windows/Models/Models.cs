using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sesame.Models;

public sealed class ConnectionProfile : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N")[..12];
    private string _name = "";
    private string _host = "";
    private int _port = 22;
    private string _user = "deck";
    private string? _keyPath;
    private string _macAddress = "";

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public string User { get => _user; set => Set(ref _user, value); }
    /// <summary>Alleen voor migratie van oude sessions.json. Wordt niet meer weggeschreven.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KeyPath { get => _keyPath; set => Set(ref _keyPath, value); }
    public string MacAddress { get => _macAddress; set => Set(ref _macAddress, value); }

    /// <summary>Niet persistente lokale sessie (deze Steam Deck, zonder SSH).</summary>
    [JsonIgnore]
    public bool IsLocal { get; set; }

    public static ConnectionProfile LocalDeck() => new()
    {
        Id = "local",
        Name = "This Steam Deck",
        Host = "local",
        Port = 0,
        User = Environment.UserName,
        IsLocal = true
    };

    public ConnectionProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Host = Host,
        Port = Port,
        User = User,
        KeyPath = null,
        MacAddress = MacAddress,
        IsLocal = IsLocal
    };

    public void CopyFrom(ConnectionProfile other)
    {
        Id = other.Id;
        Name = other.Name;
        Host = other.Host;
        Port = other.Port;
        User = other.User;
        MacAddress = other.MacAddress;
        IsLocal = other.IsLocal;
    }

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(Name))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToString)));
    }
}

public sealed class QuickPath
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Group { get; set; } = "";
}

public sealed class RemoteItem
{
    public bool IsDirectory { get; set; }
    /// <summary>Echte map- of bestandsnaam op de Deck. Nooit herschrijven voor weergave.</summary>
    public string Name { get; set; } = "";
    /// <summary>Alleen UI. Lege of gelijke waarde betekent: toon Name.</summary>
    public string DisplayName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWrite { get; set; }
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public string SizeText => IsDirectory ? "" : FormatSize(Size);
    public string Kind => IsDirectory ? "Folder" : "File";
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    public bool HasAlias => !string.IsNullOrWhiteSpace(DisplayName) &&
                            !string.Equals(DisplayName, Name, StringComparison.Ordinal);

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";
    }

    public static long ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var m = Regex.Match(text.Trim(), @"([\d.,]+)\s*(bytes?|b|kb|kib|mb|mib|gb|gib|tb|tib)?",
            RegexOptions.IgnoreCase);
        if (!m.Success) return 0;
        var raw = m.Groups[1].Value;
        raw = raw.Contains(',') && raw.Contains('.') ? raw.Replace(",", "") : raw.Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) || val <= 0)
            return 0;
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "kb" or "kib" => (long)(val * 1024),
            "mb" or "mib" => (long)(val * 1024 * 1024),
            "gb" or "gib" => (long)(val * 1024 * 1024 * 1024),
            "tb" or "tib" => (long)(val * 1024L * 1024 * 1024 * 1024),
            "b" or "byte" or "bytes" => (long)val,
            _ => val >= 4096 ? (long)val : 0
        };
    }
}

public sealed class GameEntry
{
    /// <summary>Leesbare naam in de UI. Wijzigt geen bestanden of mappen.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Echte ROM-bestandsnaam op schijf.</summary>
    public string FileName { get; set; } = "";
    public string System { get; set; } = "";
    public string RomPath { get; set; } = "";
    public string? TitleId { get; set; }
    public string? ModPath { get; set; }
    public string? SavePath { get; set; }
    public string? TexturePath { get; set; }
    public string? SaveAccountName { get; set; }
    public bool HasMods { get; set; }
    public bool HasSaves { get; set; }
    public bool HasTextures { get; set; }
    public bool IsRomHack { get; set; }
    public bool IsTranslation { get; set; }
    public bool IsManual { get; set; }
    public string ManualId { get; set; } = "";
    public string KindOverride { get; set; } = "";
    public string TagOverride { get; set; } = "";
    public string? InnerFileName { get; set; }
    public StoreGame Identity { get; set; } = StoreGame.All;
    public string TitleIdText => TitleId ?? "—";
    public string ModsText => HasMods ? "ja" : "nee";
    public string SavesText => HasSaves ? "ja" : "nee";
    public string FileNameText
    {
        get
        {
            if (string.IsNullOrEmpty(FileName)) return "—";
            return string.IsNullOrEmpty(InnerFileName) ? FileName : FileName + " → " + InnerFileName;
        }
    }
    public string KindText =>
        !string.IsNullOrEmpty(KindOverride) ? KindOverride : "Rom";
    public string TagsText
    {
        get
        {
            if (!string.IsNullOrEmpty(TagOverride)) return TagOverride;
            var tags = new List<string>();
            if (IsRomHack) tags.Add("Hack");
            if (IsTranslation) tags.Add("Translation");
            return tags.Count == 0 ? "—" : string.Join(", ", tags);
        }
    }
}

public sealed class StoreGame
{
    public static StoreGame All { get; } = new() { Name = "All games" };

    public string Name { get; set; } = "";
    public string System { get; set; } = "";
    public string? TitleId { get; set; }
    /// <summary>Onderscheidt een Nederlandse dump van het origineel, bv. "NL".</summary>
    public string? Variant { get; set; }
    public List<int> GameBananaIds { get; set; } = new();
    public List<string> KingSlugs { get; set; } = new();
    public List<string> Aliases { get; set; } = new();
    public bool IsTranslation =>
        string.Equals(Variant, "NL", StringComparison.OrdinalIgnoreCase);

    public bool IsAll =>
        string.IsNullOrWhiteSpace(System) &&
        (string.IsNullOrWhiteSpace(Name) || Name.Equals("All games", StringComparison.OrdinalIgnoreCase));

    public string Label => IsAll ? "All games" : $"{System} · {Name}";

    public string IdentityText
    {
        get
        {
            if (IsAll) return "Alle games";
            var parts = new List<string> { System, Name };
            if (GameBananaIds.Count > 0)
                parts.Add("GameBanana #" + string.Join("/", GameBananaIds));
            if (!string.IsNullOrEmpty(TitleId))
                parts.Add("Program ID " + TitleId);
            if (KingSlugs.Count > 0)
                parts.Add(KingSlugs[0]);
            return string.Join(" · ", parts);
        }
    }

    public string Key => $"{System}|{Name}|{TitleId}|{Variant}".ToLowerInvariant();

    public override string ToString() => Label;

    public StoreGame Clone() => new()
    {
        Name = Name,
        System = System,
        TitleId = TitleId,
        Variant = Variant,
        GameBananaIds = [.. GameBananaIds],
        KingSlugs = [.. KingSlugs],
        Aliases = [.. Aliases]
    };

    public void MergeFrom(StoreGame other)
    {
        if (LooksMessy(Name) && !LooksMessy(other.Name) && !string.IsNullOrWhiteSpace(other.Name))
            Name = other.Name;
        TitleId ??= other.TitleId;
        Variant ??= other.Variant;
        foreach (var id in other.GameBananaIds)
            if (!GameBananaIds.Contains(id)) GameBananaIds.Add(id);
        foreach (var slug in other.KingSlugs)
            if (!KingSlugs.Exists(s => s.Equals(slug, StringComparison.OrdinalIgnoreCase)))
                KingSlugs.Add(slug);
        foreach (var alias in other.Aliases)
            if (!Aliases.Exists(a => a.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                Aliases.Add(alias);
    }

    public bool SameAs(StoreGame other) =>
        string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);

    public bool SameIdentity(StoreGame other)
    {
        if (other.IsAll || IsAll) return false;
        if (!MatchesSystem(other.System)) return false;
        if (!SameVariant(other)) return false;
        if (!string.IsNullOrEmpty(TitleId) && !string.IsNullOrEmpty(other.TitleId))
            return string.Equals(TitleId, other.TitleId, StringComparison.OrdinalIgnoreCase);
        if (SameAs(other)) return true;
        return MatchesTitle(StripVariant(other.Name)) || other.MatchesTitle(StripVariant(Name));
    }

    public bool SameVariant(StoreGame other) =>
        string.Equals(Variant ?? "", other.Variant ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Alleen de tag die SESAME zelf zet, bv. "Banjo-Kazooie (NL)".
    /// Dump-talen zoals (En,Fr) of (En,Fr,Nl) zijn geen vertaling.
    /// </summary>
    public static bool LooksLikeTranslation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Regex.IsMatch(text, @"[\(\[]\s*(nl|dutch|nederlands)\s*[\)\]]",
            RegexOptions.IgnoreCase);
    }

    public static string StripVariant(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = Regex.Replace(name, @"\s*[\(\[]\s*(nl|dutch|nederlands)\s*[\)\]]", "",
            RegexOptions.IgnoreCase);
        return CleanTitle(n);
    }

    private static bool LooksMessy(string name) =>
        Regex.IsMatch(name ?? "", @"\b(usa|eur|japan|rev\s*\d|unl|proto)\b", RegexOptions.IgnoreCase);

    public IEnumerable<string> TitlePhrases()
    {
        yield return Name;
        foreach (var alias in Aliases)
            yield return alias;
    }

    public bool MatchesTitle(string? title)
    {
        if (IsAll) return true;
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (Conflicts(title)) return false;
        var folded = FoldTitle(title);
        foreach (var phrase in TitlePhrases())
        {
            var want = FoldTitle(phrase);
            if (want.Length == 0) continue;
            if (folded == want) return true;
            if (IsRegionOrDumpExtra(folded, want)) return true;
            // Alleen langere titels mogen als zin in de kandidaat zitten.
            // "Metroid" mag niet matchen met "Super Metroid".
            if (want.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2 &&
                (ContainsPhrase(folded, want) || ContainsPhrase(title, phrase)))
                return true;
        }
        return false;
    }

    public bool MatchesSystem(string? value)
    {
        if (IsAll || string.IsNullOrWhiteSpace(value)) return true;
        var a = FoldSystem(System);
        var b = FoldSystem(value);
        return a.Length > 0 && a == b;
    }

    public bool Conflicts(string text)
    {
        var hay = text.ToLowerInvariant();
        var slug = Slug(Name);
        if (slug is "supermario64" or "sm64")
            return ContainsAny(hay, "kart", "wonder", "party", "strikers", "odyssey", "galaxy",
                "sunshine", "3d world", "bros.", "bros ", "vs. donkey", "vs donkey");
        if (slug.Contains("mariokart64"))
            return hay.Contains("kart 8") || hay.Contains("kart8") || ContainsPhrase(hay, "super mario 64");
        if (slug.Contains("mariokart8"))
            return hay.Contains("kart 64") || hay.Contains("kart64");
        if (slug.Contains("supermariobroswonder"))
            return !hay.Contains("wonder") || hay.Contains("jamboree") ||
                   ContainsPhrase(hay, "mario party");
        if (slug.Contains("legendofzelda") && !slug.Contains("zelda2") && !slug.Contains("adventureoflink"))
            return ContainsAny(hay, "zelda ii", "zelda 2", "adventure of link", "ocarina", "majora",
                "awakening", "minish", "twilight", "wind waker", "breath of the wild",
                "tears of the kingdom", "links awakening");
        if (slug is "supersmashbros" or "smash64" or "ssb64")
            return ContainsAny(hay, "ultimate", "melee", "brawl", "3ds", "wii u", "crusade");
        if (slug.Contains("supermariopartyjamboree") || slug.Contains("mariopartyjamboree"))
            return hay.Contains("wonder") ||
                   (ContainsPhrase(hay, "super mario party") && !hay.Contains("jamboree"));
        if (slug is "metroid")
            return ContainsAny(hay, "super metroid", "metroid prime", "metroid dread",
                "metroid fusion", "other m", "samus returns");
        if (slug.Contains("supermetroid"))
            return ContainsAny(hay, "metroid prime", "metroid dread", "metroid fusion") &&
                   !hay.Contains("super metroid");
        return false;
    }

    public static string Slug(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", "");

    public static string Normalize(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    public static string FoldTitle(string value)
    {
        var n = Normalize(value);
        if (n.StartsWith("the ")) n = n[4..];
        if (n.EndsWith(" the")) n = n[..^4];
        n = n.Replace(" the ", " ");
        return Regex.Replace(n, @"\s+", " ").Trim();
    }

    public static bool ContainsPhrase(string hay, string phrase)
    {
        var h = $" {Normalize(hay)} ";
        var p = $" {Normalize(phrase)} ";
        return p.Length > 2 && h.Contains(p, StringComparison.Ordinal);
    }

    public static string CleanTitle(string name)
    {
        var n = Regex.Replace(name ?? "", @"\s*[\(\[][^\)\]]*(USA|EUR|Europe|Japan|Rev\s*\d+)[^\)\]]*[\)\]]",
            "", RegexOptions.IgnoreCase);
        n = Regex.Replace(n, @"\b(USA|EUR|Europe|Japan|Rev\s*\d+|Unl|Proto)\b", "", RegexOptions.IgnoreCase);
        return Regex.Replace(n, @"\s+", " ").Trim();
    }

    private static bool IsRegionOrDumpExtra(string folded, string want)
    {
        if (!folded.StartsWith(want + " ", StringComparison.Ordinal)) return false;
        var extra = folded[(want.Length + 1)..];
        return extra.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t =>
            t is "usa" or "eur" or "europe" or "japan" or "jap" or "unl" or "proto" or "en" or
                "prg0" or "prg1" or "sample" ||
            Regex.IsMatch(t, @"^(v|rev)?\d+$", RegexOptions.IgnoreCase));
    }

    public static string FoldSystem(string value)
    {
        var s = Slug(value);
        return s switch
        {
            "nintendo64" or "n64" => "n64",
            "nintendoswitch" or "switch" or "nsw" => "switch",
            "gamecube" or "nintendogamecube" or "gc" or "ngc" => "gc",
            "snes" or "supernintendo" or "supernintendosnes" => "snes",
            "nes" or "famicom" or "nintendones" => "nes",
            "wii" or "nintendowii" => "wii",
            "3ds" or "nintendo3ds" or "n3ds" => "3ds",
            "psone" or "ps1" or "psx" or "playstation" or "sonyplaystation" => "ps1",
            "ps2" or "playstation2" or "sonyplaystation2" => "ps2",
            "psp" or "sonyplaystationportable" or "playstationportable" => "psp",
            "psvita" or "vita" or "sonyplaystationvita" or "playstationvita" => "vita",
            "dreamcast" or "segadreamcast" or "dc" => "dc",
            "gba" or "gameboyadvance" or "agb" => "gba",
            "nds" or "nintendods" or "ds" => "nds",
            "genesis" or "megadrive" or "segagenesis" or "segamegadrive" or "md" or "smd" => "genesis",
            _ => s
        };
    }

    private static bool ContainsAny(string hay, params string[] parts) =>
        parts.Any(p => hay.Contains(p, StringComparison.Ordinal));
}

public sealed class EdenUser
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
}

public sealed class EdenLayout
{
    public string UsersRoot { get; set; } = "";
    public List<EdenUser> Users { get; set; } = new();
    public EdenUser? Primary => Users.Count > 0 ? Users[0] : null;
}

public sealed class StoreGameInfo : INotifyPropertyChanged
{
    private object? _cover;

    public string Name { get; set; } = "";
    public string System { get; set; } = "";
    public string Description { get; set; } = "";
    public string Meta { get; set; } = "";
    public string IdentityText { get; set; } = "";
    public string? CoverUrl { get; set; }
    public string? BannerUrl { get; set; }
    public string? PageUrl { get; set; }

    public object? Cover
    {
        get => _cover;
        set
        {
            _cover = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCover)));
        }
    }

    public bool HasCover => Cover is not null;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PackHit : INotifyPropertyChanged
{
    private object? _thumbnail;
    private object? _preview;
    private string _statusText = "";
    private string _statusKind = "";
    private double _progress;
    private bool _isBusy;
    private bool _progressUnknown;
    private bool _isDownloaded;
    private bool _isInstalled;
    private bool _isQueued;
    private bool _isEnabled = true;
    private string? _localFile;
    private string? _remotePath;
    private string? _targetPath;
    private long _size;

    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string GameName { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public string? DownloadUrl { get; set; }
    public string? FileName { get; set; }
    public string? ItemId { get; set; }
    public string Kind { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Platform { get; set; } = "";
    public string OriginalGame { get; set; } = "";
    public string? RequiredRomName { get; set; }
    public string? FileSha1 { get; set; }
    public string? RomSha1 { get; set; }
    public string? FileCrc32 { get; set; }
    public string? OutputSha1 { get; set; }
    public string Summary { get; set; } = "";
    public DateTime? AddedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public int LikeCount { get; set; }
    public int DownloadCount { get; set; }
    public int ViewCount { get; set; }
    public int PostCount { get; set; }
    public bool WasFeatured { get; set; }
    public int SearchRank { get; set; }
    public int? SourceGameId { get; set; }
    public string? ImageUrl { get; set; }
    public List<string> ScreenshotUrls { get; set; } = new();
    public long Size
    {
        get => _size;
        set
        {
            if (_size == value) return;
            _size = value;
            Notify(nameof(Size));
            Notify(nameof(SizeText));
            Notify(nameof(CardMeta));
        }
    }
    public string SizeText => Size > 0 ? RemoteItem.FormatSize(Size) : "";
    public bool IsRomHack => Kind.Equals("ROM-hack", StringComparison.OrdinalIgnoreCase);
    public bool CanDownload =>
        !string.IsNullOrEmpty(ItemId) || PackUrl.CanResolve(DownloadUrl) || PackUrl.CanResolve(PageUrl);

    public string Section => Kind.ToLowerInvariant() switch
    {
        "texture pack" or "texture packs" => "Texture packs",
        "save" or "saves" => "Saves",
        "rom-hack" or "rom hack" or "rom hacks" => "ROM-hacks",
        "sound" or "sounds" => "Sounds",
        _ => "Mods"
    };

    public string GameLabel
    {
        get
        {
            var game = !string.IsNullOrWhiteSpace(GameName) ? GameName
                : !string.IsNullOrWhiteSpace(OriginalGame) ? OriginalGame : "";
            if (string.IsNullOrWhiteSpace(game) && string.IsNullOrWhiteSpace(Platform)) return "";
            if (string.IsNullOrWhiteSpace(Platform)) return game;
            if (string.IsNullOrWhiteSpace(game)) return Platform;
            if (game.Contains(Platform, StringComparison.OrdinalIgnoreCase)) return game;
            return Platform + " · " + game;
        }
    }

    public bool HasGameLabel => !string.IsNullOrWhiteSpace(GameLabel);

    public string AddedText => FormatDate(AddedUtc);
    public string UpdatedText => FormatDate(UpdatedUtc);

    public string CardMeta
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Kind)) parts.Add(Kind);
            if (!string.IsNullOrWhiteSpace(Source)) parts.Add(Source);
            if (!string.IsNullOrWhiteSpace(Author)) parts.Add(Author);
            if (!string.IsNullOrWhiteSpace(Version)) parts.Add("v" + Version.TrimStart('v', 'V'));
            if (!string.IsNullOrWhiteSpace(SizeText)) parts.Add(SizeText);
            if (LikeCount > 0) parts.Add(LikeCount + " likes");
            if (DownloadCount > 0) parts.Add(DownloadCount + " downloads");
            if (WasFeatured) parts.Add("uitgelicht");
            if (UpdatedUtc is not null) parts.Add("upd " + UpdatedText);
            else if (AddedUtc is not null) parts.Add(AddedText);
            return string.Join(" · ", parts);
        }
    }

    private static string FormatDate(DateTime? utc) =>
        utc is null ? "" : utc.Value.ToLocalTime().ToString("yyyy-MM-dd");

    public string? BestImageUrl => ImageUrl ?? ScreenshotUrls.FirstOrDefault();

    public object? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            Notify(nameof(Thumbnail));
            Notify(nameof(HasThumbnail));
        }
    }

    public object? Preview
    {
        get => _preview;
        set
        {
            _preview = value;
            Notify(nameof(Preview));
            Notify(nameof(HasPreview));
        }
    }

    public bool HasThumbnail => Thumbnail is not null;
    public bool HasPreview => Preview is not null;

    public string StatusText
    {
        get => string.IsNullOrWhiteSpace(_statusText) ? "—" : _statusText;
        private set
        {
            _statusText = value;
            Notify(nameof(StatusText));
            Notify(nameof(StatusDetail));
        }
    }

    public string StatusKind
    {
        get => _statusKind;
        private set
        {
            if (_statusKind == value) return;
            _statusKind = value;
            Notify(nameof(StatusKind));
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) < 0.05) return;
            _progress = value;
            Notify(nameof(Progress));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            Notify(nameof(IsBusy));
            Notify(nameof(PinRank));
        }
    }

    public bool ProgressUnknown
    {
        get => _progressUnknown;
        set
        {
            if (_progressUnknown == value) return;
            _progressUnknown = value;
            Notify(nameof(ProgressUnknown));
        }
    }

    public bool IsDownloaded
    {
        get => _isDownloaded;
        private set
        {
            if (_isDownloaded == value) return;
            _isDownloaded = value;
            Notify(nameof(IsDownloaded));
            Notify(nameof(PinRank));
        }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        private set
        {
            if (_isInstalled == value) return;
            _isInstalled = value;
            Notify(nameof(IsInstalled));
            Notify(nameof(PinRank));
        }
    }

    public bool IsQueued
    {
        get => _isQueued;
        private set
        {
            if (_isQueued == value) return;
            _isQueued = value;
            Notify(nameof(IsQueued));
            Notify(nameof(PinRank));
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            Notify(nameof(IsEnabled));
        }
    }

    public int PinRank =>
        IsBusy || IsQueued ? 4 :
        IsInstalled ? 3 :
        IsDownloaded ? 2 : 0;

    public string? LocalFile
    {
        get => _localFile;
        private set
        {
            if (_localFile == value) return;
            _localFile = value;
            Notify(nameof(LocalFile));
            Notify(nameof(StatusDetail));
        }
    }

    public string? RemotePath
    {
        get => _remotePath;
        private set
        {
            if (_remotePath == value) return;
            _remotePath = value;
            Notify(nameof(RemotePath));
            Notify(nameof(StatusDetail));
        }
    }

    public string? TargetPath
    {
        get => _targetPath;
        set
        {
            if (_targetPath == value) return;
            _targetPath = value;
            Notify(nameof(TargetPath));
            Notify(nameof(StatusDetail));
        }
    }

    public string StatusDetail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_statusText) && _statusText != "—")
                parts.Add(_statusText);
            if (!string.IsNullOrWhiteSpace(RemotePath))
                parts.Add("Op Deck: " + RemotePath);
            else if (!string.IsNullOrWhiteSpace(TargetPath))
                parts.Add("Doel: " + TargetPath);
            if (!string.IsNullOrWhiteSpace(LocalFile))
                parts.Add("Local: " + LocalFile);
            return string.Join(Environment.NewLine, parts);
        }
    }

    public void SetQueued(int position)
    {
        IsQueued = true;
        IsBusy = false;
        ProgressUnknown = false;
        Progress = 0;
        StatusKind = "busy";
        StatusText = position <= 1 ? "Queued…" : "Queued (" + position + ")";
    }

    public void SetWork(string text, double progress, bool unknown = false)
    {
        IsQueued = false;
        IsBusy = true;
        ProgressUnknown = unknown;
        Progress = Math.Clamp(progress, 0, 100);
        StatusKind = "busy";
        StatusText = text;
    }

    public void SetDownloaded(string localFile)
    {
        LocalFile = localFile;
        IsDownloaded = File.Exists(localFile);
        IsQueued = false;
        IsBusy = false;
        ProgressUnknown = false;
        Progress = IsInstalled ? 100 : 0;
        if (IsInstalled)
        {
            StatusKind = "ok";
            StatusText = "Installed";
        }
        else if (IsDownloaded)
        {
            StatusKind = "local";
            StatusText = "Downloaded";
        }
        else
        {
            StatusKind = "";
            StatusText = "";
        }
    }

    public void SetInstalled(string remotePath, string? localFile = null)
    {
        if (!string.IsNullOrWhiteSpace(localFile))
            LocalFile = localFile;
        RemotePath = remotePath;
        IsInstalled = true;
        IsDownloaded = IsDownloaded || File.Exists(LocalFile ?? "");
        IsBusy = false;
        ProgressUnknown = false;
        Progress = 100;
        StatusKind = IsEnabled ? "ok" : "off";
        StatusText = IsEnabled ? "Installed" : "Disabled";
    }

    public void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        if (!IsInstalled) return;
        IsBusy = false;
        IsQueued = false;
        StatusKind = enabled ? "ok" : "off";
        StatusText = enabled ? "Installed" : "Disabled";
    }

    public void ClearLocal()
    {
        IsQueued = false;
        IsBusy = false;
        IsDownloaded = false;
        IsInstalled = false;
        IsEnabled = true;
        LocalFile = null;
        RemotePath = null;
        Progress = 0;
        ProgressUnknown = false;
        StatusKind = "";
        StatusText = "";
    }

    public void SetFailed(string message)
    {
        IsQueued = false;
        IsBusy = false;
        ProgressUnknown = false;
        StatusKind = "err";
        StatusText = string.IsNullOrWhiteSpace(message) ? "Mislukt" : message;
    }

    public void ClearWork()
    {
        if (IsInstalled)
            SetInstalled(RemotePath ?? TargetPath ?? "", LocalFile);
        else if (IsDownloaded && !string.IsNullOrWhiteSpace(LocalFile))
            SetDownloaded(LocalFile);
        else
        {
            IsBusy = false;
            ProgressUnknown = false;
            Progress = 0;
            StatusKind = "";
            StatusText = "";
        }
    }

    private void Notify(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class PackUrl
{
    private static readonly string[] DirectExt =
        [".zip", ".rar", ".7z", ".hts", ".htc", ".pak", ".tar", ".gz", ".bps", ".ips", ".ups", ".xdelta"];

    public static bool CanResolve(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;
        if (host.Contains("gamebanana.com") &&
            (path.Contains("/mods/", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("/gamefiles/", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (host.Contains("emulationking.com"))
            return true;
        if (host.Contains("github.com") && path.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase))
            return true;
        if (host.Contains("githubusercontent.com"))
            return true;
        if (host.Contains("archive.org") && path.Contains("/download/", StringComparison.OrdinalIgnoreCase))
            return true;
        return DirectExt.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsDirectFile(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return DirectExt.Any(ext => uri.AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class TransferJob
{
    public string Name { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Status { get; set; } = "Working";
}
