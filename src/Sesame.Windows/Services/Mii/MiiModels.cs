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

    public string Name { get; init; }
    public bool IsFemale { get; init; }
    public int FavoriteColor { get; init; }
    public int HairStyle { get; init; }
    public int HairColor { get; init; }
    public int EyeColor { get; init; }

// Additional FFL parts. The six constructor fields above remain stable for
// existing callers; these properties cover the complete portable Mii editor
// surface and are encoded per emulator by the format adapters.
    public bool HasAdvancedParts { get; init; }
    public int Height { get; init; }
    public int Build { get; init; }
    public int HairFlip { get; init; }
    public int FaceType { get; init; }
    public int FaceColor { get; init; }
    public int FaceMakeup { get; init; }
    public int FaceWrinkle { get; init; }
    public int EyeType { get; init; }
    public int EyeScale { get; init; }
    public int EyeAspect { get; init; }
    public int EyeRotate { get; init; }
    public int EyeSpacing { get; init; }
    public int EyePosition { get; init; }
    public int EyebrowType { get; init; }
    public int EyebrowColor { get; init; }
    public int EyebrowScale { get; init; }
    public int EyebrowAspect { get; init; }
    public int EyebrowRotate { get; init; }
    public int EyebrowSpacing { get; init; }
    public int EyebrowPosition { get; init; }
    public int NoseType { get; init; }
    public int NoseScale { get; init; }
    public int NosePosition { get; init; }
    public int MouthType { get; init; }
    public int MouthColor { get; init; }
    public int MouthScale { get; init; }
    public int MouthAspect { get; init; }
    public int MouthPosition { get; init; }
    public int BeardType { get; init; }
    public int BeardColor { get; init; }
    public int MustacheType { get; init; }
    public int MustacheScale { get; init; }
    public int MustachePosition { get; init; }
    public int GlassesType { get; init; }
    public int GlassesColor { get; init; }
    public int GlassesScale { get; init; }
    public int GlassesPosition { get; init; }
    public int MoleType { get; init; }
    public int MoleScale { get; init; }
    public int MoleX { get; init; }
    public int MoleY { get; init; }

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
