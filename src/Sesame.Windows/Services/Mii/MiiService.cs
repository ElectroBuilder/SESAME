using System.Security.Cryptography;

namespace Sesame.Services.Mii;

public sealed record MiiTargetState(
    MiiOperationSnapshot Target,
    MiiCapability Capability,
    string Integrity,
    byte[]? Database,
    string? SourceSha256,
    IReadOnlyList<MiiSlot> Slots)
{
    public bool CanExport => Database is not null && Capability != MiiCapability.Unavailable;
    public bool CanPush => Capability == MiiCapability.WriteVerified;
}

public sealed class MiiService
{
    // Synthetic fixtures prove parsing, CRCs and transaction mechanics, not emulator compatibility.
    // These gates may only become true after an explicit, documented manual validation campaign.
    public const bool WiiWriteGateVerified = false;
    public const bool EdenWriteGateVerified = false;

    private readonly IMiiNandTransport _transport;
    private readonly MiiNandStore _store;
    private readonly MiiFormatWii _wii = new();
    private readonly MiiFormatSwitch _eden = new();

    public MiiService(DeckClient client) : this(new DeckMiiNandTransport(client)) { }

    public MiiService(IMiiNandTransport transport, string? backupRoot = null)
    {
        _transport = transport;
        _store = new MiiNandStore(transport, backupRoot);
    }

    public MiiOperationSnapshot Capture(MiiTargetKind kind)
    {
        var root = EmulatorPaths.UserRoot(kind == MiiTargetKind.Wii ? "dolphin" : "eden");
        var path = kind == MiiTargetKind.Wii
            ? DeckClient.Combine(root, "Wii/shared2/menu/FaceLib/RFL_DB.dat")
            : DeckClient.Combine(root, "nand/system/save/8000000000000030/MiiDatabase.dat");
        return MiiOperationSnapshot.Capture(kind, path, _transport);
    }

    public MiiTargetState Load(MiiOperationSnapshot target)
    {
        if (!_transport.IsConnected)
            return Unavailable(target, "Not connected.");
        if (!string.Equals(target.HostId, _transport.HostId, StringComparison.Ordinal) ||
            !string.Equals(target.Host, _transport.Host, StringComparison.Ordinal))
            return Unavailable(target, "Connected host changed after target capture.");
        try
        {
            if (!_transport.Exists(target.TargetPath))
                return Unavailable(target, "Database missing. A host-bound verified backup can still be restored.");
            var bytes = _transport.ReadBytes(target.TargetPath);
            var validation = Format(target.Kind).Validate(bytes);
            if (!validation.IsValid)
                return Unavailable(target, "Integrity failed: " + validation.Error);
            var writeGate = target.Kind == MiiTargetKind.Wii ? WiiWriteGateVerified : EdenWriteGateVerified;
            var capability = writeGate ? MiiCapability.WriteVerified : MiiCapability.ReadOnlyVerified;
            var integrity = writeGate
                ? "Format and manual write gate verified."
                : "Format/CRC verified (synthetic fixtures); Push disabled until manual emulator validation.";
            return new MiiTargetState(target, capability, integrity, bytes, MiiNandStore.Sha(bytes), validation.Slots);
        }
        catch (Exception ex)
        {
            return Unavailable(target, "Read failed: " + ex.Message);
        }
    }

    public IReadOnlyList<MiiBackup> Inventory(MiiOperationSnapshot target) => _store.Inventory(target);

    public MiiBackup BackupNow(MiiTargetState state) =>
        _store.BackupNow(state.Target, Format(state.Target.Kind));

    public byte[] ExportRecord(MiiTargetState state, int slot)
    {
        if (!state.CanExport || state.Database is null)
            throw new InvalidOperationException("This target is not verified for exact record export.");
        return Format(state.Target.Kind).ExportRecord(state.Database, slot);
    }

    public MiiPushResult PushRecord(MiiTargetState state, byte[] record, bool allowUnavailableProcessCheck)
    {
        if (!state.CanPush || state.Database is null || string.IsNullOrWhiteSpace(state.SourceSha256))
            throw new InvalidOperationException("Push is read-only: manual emulator validation has not enabled this target.");
        var replacement = Format(state.Target.Kind).Insert(state.Database, record);
        return _store.ReplaceTransactional(state.Target, Format(state.Target.Kind), replacement,
            state.SourceSha256, allowMissingSource: false, allowUnavailableProcessCheck);
    }

    public MiiPushResult PushBasic(MiiTargetState state, string name, bool allowUnavailableProcessCheck)
    {
        if (!state.CanPush)
            throw new InvalidOperationException("Push is read-only: manual emulator validation has not enabled this target.");
        return PushRecord(state, CreateBasicRecord(state, name), allowUnavailableProcessCheck);
    }

    public byte[] CreateBasicRecord(MiiTargetState state, string name)
    {
        if (state.Target.Kind != MiiTargetKind.Wii)
            return Format(state.Target.Kind).CreateBasicRecord(name);

        var identity = RandomNumberGenerator.GetBytes(8);
        var usedIds = state.Slots.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        do
        {
            RandomNumberGenerator.Fill(identity.AsSpan(0, 4));
            identity[0] &= 0xDF;
            if (identity.AsSpan(0, 4).IndexOfAnyExcept((byte)0) < 0) identity[3] = 1;
        } while (usedIds.Contains(Convert.ToHexString(identity.AsSpan(0, 4))));

        if (state.Database is { } database && state.Slots.FirstOrDefault() is { } first)
        {
            var offset = MiiFormatWii.RecordsOffset + first.Slot * MiiFormatWii.RecordSize;
            database.AsSpan(offset + 28, 4).CopyTo(identity.AsSpan(4, 4));
        }
        return _wii.CreateBasicRecord(name, identity);
    }

    public MiiPushResult Restore(MiiOperationSnapshot target, MiiBackup backup, bool allowUnavailableProcessCheck) =>
        _store.Restore(target, Format(target.Kind), backup, allowUnavailableProcessCheck);

    private IMiiFormat Format(MiiTargetKind kind) => kind == MiiTargetKind.Wii ? _wii : _eden;

    private static MiiTargetState Unavailable(MiiOperationSnapshot target, string reason) =>
        new(target, MiiCapability.Unavailable, reason, null, null, []);
}
