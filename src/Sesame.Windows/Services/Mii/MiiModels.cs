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
    byte[] ExportRecord(byte[] database, int slot);
    byte[] CreateBasicRecord(string name, byte[]? identity = null);
}
