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
public sealed record MiiAppearance(
    string Name,
    bool IsFemale,
    int FavoriteColor,
    int HairStyle,
    int HairColor,
    int EyeColor);

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
