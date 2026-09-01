using System.Security.Cryptography;

namespace Sesame.Services.Mii;

public sealed record MiiTargetState(
    MiiOperationSnapshot Target,
    MiiCapability Capability,
    string Integrity,
    byte[]? Database,
    string? SourceSha256,
    IReadOnlyList<MiiSlot> Slots,
    bool IsDraft = false)
{
    public bool CanExport => Database is not null && Capability != MiiCapability.Unavailable;
    public bool CanPush => Capability == MiiCapability.WriteVerified;
    public bool CanExperimentalPush(bool acknowledged) =>
        IsDraft && Database is not null && !string.IsNullOrWhiteSpace(SourceSha256) &&
        (Capability == MiiCapability.WriteVerified ||
         (Capability == MiiCapability.ReadOnlyVerified && acknowledged));
}

public sealed class MiiService
{
    // Synthetic fixtures prove parsing, CRCs and transaction mechanics, not emulator compatibility.
    // These gates may only become true after an explicit, documented manual validation campaign.
    public const bool WiiWriteGateVerified = false;
    public const bool EdenWriteGateVerified = false;

    private readonly IMiiNandTransport _transport;
    private readonly MiiNandStore _store;
    private readonly MiiDatabaseLocator _locator;
    private readonly MiiFormatWii _wii = new();
    private readonly MiiFormatSwitch _eden = new();

    public MiiService(DeckClient client) : this(new DeckMiiNandTransport(client)) { }

    public MiiService(IMiiNandTransport transport, string? backupRoot = null)
    {
        _transport = transport;
        _store = new MiiNandStore(transport, backupRoot);
        _locator = new MiiDatabaseLocator(transport);
    }

    public MiiOperationSnapshot Capture(MiiTargetKind kind) => Resolve(kind).Target;
    public MiiOperationSnapshot Preferred(MiiTargetKind kind) => _locator.Preferred(kind);

    public MiiPathResolution Resolve(MiiTargetKind kind, string? selectedPath = null) =>
        _locator.Resolve(kind, selectedPath);

    public MiiTargetState Load(MiiOperationSnapshot target)
    {
        if (!_transport.IsConnected)
            return Unavailable(target, "Not connected.");
        if (!string.Equals(target.HostId, _transport.HostId, StringComparison.Ordinal) ||
            !string.Equals(target.Host, _transport.Host, StringComparison.Ordinal))
            return Unavailable(target, "Connected host changed after target capture.");
        if (!target.PathApproved)
            return Unavailable(target, target.PathStatus);
        try
        {
            if (!_transport.Exists(target.TargetPath))
                return Unavailable(target, target.PathStatus.Length > 0
                    ? target.PathStatus + " A host-bound verified backup can still be restored."
                    : "Database missing. A host-bound verified backup can still be restored.");
            var bytes = _transport.ReadBytes(target.TargetPath);
            var validation = Format(target.Kind).Validate(bytes);
            if (!validation.IsValid)
                return Unavailable(target, "Integrity failed: " + validation.Error);
            var writeGate = target.Kind == MiiTargetKind.Wii ? WiiWriteGateVerified : EdenWriteGateVerified;
            var capability = writeGate ? MiiCapability.WriteVerified : MiiCapability.ReadOnlyVerified;
            var integrity = writeGate
                ? "Format and manual write gate verified."
                : "Format/CRC verified (synthetic fixtures); Push requires explicit experimental opt-in until manual validation.";
            if (validation.Slots.Count == 0)
                integrity += " Valid database contains 0 Mii records; the emulator has not created one yet.";
            if (target.PathStatus.Length > 0) integrity = target.PathStatus + " " + integrity;
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

    public byte[] ExportDatabase(MiiTargetState state)
    {
        if (!state.CanExport || state.Database is null)
            throw new InvalidOperationException("This database is not structurally verified for export.");
        return (byte[])state.Database.Clone();
    }

    public MiiTargetState ImportDraft(MiiTargetState state, byte[] record) =>
        Draft(state, Format(state.Target.Kind).Insert(RequireDatabase(state), record), "Record imported into draft.");

    public MiiTargetState RenameDraft(MiiTargetState state, int slot, string name) =>
        Draft(state, Format(state.Target.Kind).UpdateName(RequireDatabase(state), slot, name), "Name edited in draft.");

    public MiiAppearance GetAppearance(MiiTargetState state, int slot) =>
        Format(state.Target.Kind).ReadAppearance(RequireDatabase(state), slot);

    public MiiTargetState UpdateAppearanceDraft(MiiTargetState state, int slot, MiiAppearance appearance) =>
        Draft(state, Format(state.Target.Kind).UpdateAppearance(RequireDatabase(state), slot, appearance),
            "Mii appearance edited in draft.");

    public MiiTargetState RemoveDraft(MiiTargetState state, int slot) =>
        Draft(state, Format(state.Target.Kind).Remove(RequireDatabase(state), slot),
            "Mii removed from an offline draft. Live NAND is unchanged.");

    public MiiTargetState AddBasicDraft(MiiTargetState state, string name) =>
        ImportDraft(state, CreateBasicRecord(state, name)) with
        {
            Integrity = "Basic Mii added to an offline draft. Live NAND is unchanged."
        };

    public MiiTargetState AddBasicDraft(MiiTargetState state, MiiAppearance appearance)
    {
        var added = AddBasicDraft(state, appearance.Name);
        var slot = added.Slots.Last().Slot;
        return UpdateAppearanceDraft(added, slot, appearance) with
        {
            Integrity = "Mii created in an offline draft. Live NAND is unchanged."
        };
    }

    public MiiPushResult PushRecord(MiiTargetState state, byte[] record, bool allowUnavailableProcessCheck,
        bool experimentalAcknowledged = false)
    {
        var draft = ImportDraft(state, record);
        return PushDatabase(draft, allowUnavailableProcessCheck, experimentalAcknowledged);
    }

    public MiiPushResult PushDatabase(MiiTargetState state, bool allowUnavailableProcessCheck,
        bool experimentalAcknowledged)
    {
        if (!state.CanExperimentalPush(experimentalAcknowledged) || state.Database is null ||
            string.IsNullOrWhiteSpace(state.SourceSha256))
            throw new InvalidOperationException(
                "Push is locked. Create a verified draft and explicitly acknowledge experimental Push for this exact host, target and path.");
        return _store.ReplaceTransactional(state.Target, Format(state.Target.Kind), state.Database,
            state.SourceSha256, allowMissingSource: false, allowUnavailableProcessCheck);
    }

    public MiiPushResult PushBasic(MiiTargetState state, string name, bool allowUnavailableProcessCheck,
        bool experimentalAcknowledged = false)
    {
        return PushDatabase(AddBasicDraft(state, name), allowUnavailableProcessCheck, experimentalAcknowledged);
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

    private MiiTargetState Draft(MiiTargetState state, byte[] database, string message)
    {
        if (state.Capability == MiiCapability.Unavailable)
            throw new InvalidOperationException("A valid live database must be loaded before editing a draft.");
        var validation = Format(state.Target.Kind).Validate(database);
        if (!validation.IsValid) throw new InvalidDataException("Draft failed validation: " + validation.Error);
        return state with
        {
            Database = database,
            Slots = validation.Slots,
            IsDraft = true,
            Integrity = message + " Exact format and CRCs verify; live NAND is unchanged."
        };
    }

    private static byte[] RequireDatabase(MiiTargetState state) => state.Database is { } bytes
        ? bytes
        : throw new InvalidOperationException("A valid database must be loaded before editing.");

    private IMiiFormat Format(MiiTargetKind kind) => kind == MiiTargetKind.Wii ? _wii : _eden;

    private static MiiTargetState Unavailable(MiiOperationSnapshot target, string reason) =>
        new(target, MiiCapability.Unavailable, reason, null, null, []);
}
