namespace Sesame.Services.Mii;

public enum MiiTargetKind
{
    Wii,
    Eden
}

public enum MiiCapability
{
    Unavailable,
    ReadOnlyVerified,
    WriteVerified
}

public sealed record MiiSlot(int Slot, string Name, string Id);

// These are the portable, directly encoded appearance controls shared by the
// Wii RFL and SwitchDB records. Style and colour values are deliberately kept
// as emulator IDs: their visual ordering differs between platforms.
public sealed class MiiAppearance : IEquatable<MiiAppearance>
{
    public MiiAppearance(string name, bool isFemale, int favoriteColor, int hairStyle,
        int hairColor, int eyeColor)
    {
        Name = name;
        IsFemale = isFemale;
        FavoriteColor = favoriteColor;
        HairStyle = hairStyle;
        HairColor = hairColor;
        EyeColor = eyeColor;
    }

    public string Name { get; set; }
    public bool IsFemale { get; set; }
    public int FavoriteColor { get; set; }
    public int HairStyle { get; set; }
    public int HairColor { get; set; }
    public int EyeColor { get; set; }

// Additional FFL parts. The six constructor fields above remain stable for
// existing callers; these properties cover the complete portable Mii editor
// surface and are encoded per emulator by the format adapters.
    public bool HasAdvancedParts { get; set; }
    public int Height { get; set; }
    public int Build { get; set; }
    public int HairFlip { get; set; }
    public int FaceType { get; set; }
    public int FaceColor { get; set; }
    public int FaceMakeup { get; set; }
    public int FaceWrinkle { get; set; }
    public int EyeType { get; set; }
    public int EyeScale { get; set; }
    public int EyeAspect { get; set; }
    public int EyeRotate { get; set; }
    public int EyeSpacing { get; set; }
    public int EyePosition { get; set; }
    public int EyebrowType { get; set; }
    public int EyebrowColor { get; set; }
    public int EyebrowScale { get; set; }
    public int EyebrowAspect { get; set; }
    public int EyebrowRotate { get; set; }
    public int EyebrowSpacing { get; set; }
    public int EyebrowPosition { get; set; }
    public int NoseType { get; set; }
    public int NoseScale { get; set; }
    public int NosePosition { get; set; }
    public int MouthType { get; set; }
    public int MouthColor { get; set; }
    public int MouthScale { get; set; }
    public int MouthAspect { get; set; }
    public int MouthPosition { get; set; }
    public int BeardType { get; set; }
    public int BeardColor { get; set; }
    public int MustacheType { get; set; }
    public int MustacheScale { get; set; }
    public int MustachePosition { get; set; }
    public int GlassesType { get; set; }
    public int GlassesColor { get; set; }
    public int GlassesScale { get; set; }
    public int GlassesPosition { get; set; }
    public int MoleType { get; set; }
    public int MoleScale { get; set; }
    public int MoleX { get; set; }
    public int MoleY { get; set; }

    public MiiAppearance Clone() => (MiiAppearance)MemberwiseClone();

    // Keep the original six-field value semantics for compatibility. The
    // advanced fields are editor state, not identity of the legacy value.
    public bool Equals(MiiAppearance? other) => other is not null &&
        string.Equals(Name, other.Name, StringComparison.Ordinal) &&
        IsFemale == other.IsFemale && FavoriteColor == other.FavoriteColor &&
        HairStyle == other.HairStyle && HairColor == other.HairColor && EyeColor == other.EyeColor;

    public override bool Equals(object? obj) => Equals(obj as MiiAppearance);
    public override int GetHashCode() => HashCode.Combine(Name, IsFemale, FavoriteColor, HairStyle, HairColor, EyeColor);
}

public sealed record MiiValidation(bool IsValid, string Error, IReadOnlyList<MiiSlot> Slots)
{
    public static MiiValidation Valid(IReadOnlyList<MiiSlot> slots) => new(true, "", slots);

    public static MiiValidation Invalid(string error) => new(false, error, Array.Empty<MiiSlot>());
}

public interface IMiiFormat
{
    MiiTargetKind Kind { get; }
    int DatabaseSize { get; }
    string ExportExtension { get; }
    MiiValidation Validate(byte[] database);
    byte[] Insert(byte[] database, byte[] record);
    MiiAppearance ReadAppearance(byte[] database, int slot);
    byte[] UpdateAppearance(byte[] database, int slot, MiiAppearance appearance);
    byte[] UpdateName(byte[] database, int slot, string name);
    byte[] ExportRecord(byte[] database, int slot);
    byte[] CreateBasicRecord(string name, byte[]? identity = null);
}
