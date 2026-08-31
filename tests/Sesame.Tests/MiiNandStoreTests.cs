using System.Text.Json;
using Sesame.Services;
using Sesame.Services.Mii;

namespace Sesame.Tests;

public sealed class MiiNandStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sesame-mii-test-" + Guid.NewGuid().ToString("N"));
    private const string Target = "/nand/RFL_DB.dat";
    private readonly MiiFormatWii _format = new();

    [Fact]
    public void PathsFollowStatelessEmulatorOverrides()
    {
        var previous = LibraryPaths.Current.EmulatorOverrides.ToDictionary(x => x.Key, x => x.Value);
        try
        {
            LibraryPaths.Current.EmulatorOverrides["dolphin"] = new EmulatorPathOverrides { UserRoot = "/custom/dolphin" };
            LibraryPaths.Current.EmulatorOverrides["eden"] = new EmulatorPathOverrides { UserRoot = "/custom/eden" };
            var fake = new FakeTransport();
            var service = new MiiService(fake, _root);
            Assert.Equal("/custom/dolphin/Wii/shared2/menu/FaceLib/RFL_DB.dat", service.Capture(MiiTargetKind.Wii).TargetPath);
            Assert.Equal("/custom/eden/nand/system/save/8000000000000030/MiiDatabase.dat", service.Capture(MiiTargetKind.Eden).TargetPath);
        }
        finally
        {
            LibraryPaths.Current.EmulatorOverrides.Clear();
            foreach (var pair in previous) LibraryPaths.Current.EmulatorOverrides[pair.Key] = pair.Value;
        }
    }

    [Fact]
    public void MissingDolphinDatabaseExplainsHowToInitializeItWithoutGuessing()
    {
        var previous = LibraryPaths.Current.EmulatorOverrides.ToDictionary(x => x.Key, x => x.Value);
        try
        {
            LibraryPaths.Current.EmulatorOverrides.Clear();
            var fake = new FakeTransport();
            var resolved = new MiiService(fake, _root).Resolve(MiiTargetKind.Wii);

            Assert.False(resolved.Exists);
            Assert.Contains("Wii System Menu or Mii Channel", resolved.Target.PathStatus, StringComparison.Ordinal);
            Assert.Contains("does not create RFL_DB.dat automatically", resolved.Target.PathStatus, StringComparison.Ordinal);
            Assert.Contains(resolved.Candidates, x => x.Path == "/home/deck/Emulation/tools/dolphin-emu/User/Wii/shared2/menu/FaceLib/RFL_DB.dat");
        }
        finally
        {
            LibraryPaths.Current.EmulatorOverrides.Clear();
            foreach (var pair in previous) LibraryPaths.Current.EmulatorOverrides[pair.Key] = pair.Value;
        }
    }

    [Fact]
    public void InvalidDolphinDatabaseIsReportedAsInvalidNotMissing()
    {
        var previous = LibraryPaths.Current.EmulatorOverrides.ToDictionary(x => x.Key, x => x.Value);
        try
        {
            LibraryPaths.Current.EmulatorOverrides.Clear();
            var fake = new FakeTransport();
            fake.Files["/home/deck/Emulation/storage/dolphin-emu/Wii/shared2/menu/FaceLib/RFL_DB.dat"] = [1, 2, 3];
            var resolved = new MiiService(fake, _root).Resolve(MiiTargetKind.Wii);

            Assert.False(resolved.Exists);
            Assert.Contains("was found but could not be verified", resolved.Target.PathStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("has not created", resolved.Target.PathStatus, StringComparison.Ordinal);
        }
        finally
        {
            LibraryPaths.Current.EmulatorOverrides.Clear();
            foreach (var pair in previous) LibraryPaths.Current.EmulatorOverrides[pair.Key] = pair.Value;
        }
    }

    [Fact]
    public void SyntheticValidationNeverClaimsWriteVerified()
    {
        var fake = NewLive();
        var service = new MiiService(fake, _root);
        var target = new MiiOperationSnapshot(MiiTargetKind.Wii, Target, fake.HostId, fake.Host);
        var state = service.Load(target);
        Assert.Equal(MiiCapability.ReadOnlyVerified, state.Capability);
        Assert.False(state.CanPush);
        Assert.False(MiiService.WiiWriteGateVerified);
        Assert.False(MiiService.EdenWriteGateVerified);
    }

    [Fact]
    public void WiiBasicTemplateReusesSystemIdAndCreatesSafeUniqueMiiId()
    {
        var fake = NewLive();
        var first = _format.CreateBasicRecord("Original", [1, 2, 3, 4, 0xA1, 0xB2, 0xC3, 0xD4]);
        fake.Files[Target] = _format.Insert(fake.Files[Target], first);
        var service = new MiiService(fake, _root);
        var state = service.Load(Snapshot(fake));
        var created = service.CreateBasicRecord(state, "New");
        Assert.Equal(first.AsSpan(28, 4).ToArray(), created.AsSpan(28, 4).ToArray());
        Assert.NotEqual(first.AsSpan(24, 4).ToArray(), created.AsSpan(24, 4).ToArray());
        Assert.Equal(0, created[24] & 0x20);
        Assert.True(_format.Validate(_format.Insert(fake.Files[Target], created)).IsValid);
    }

    [Fact]
    public void DraftEditorNeverMutatesLiveUntilExplicitExperimentalPush()
    {
        var fake = NewLive();
        var original = fake.Files[Target].ToArray();
        var service = new MiiService(fake, _root);
        var state = service.Load(Snapshot(fake));

        var draft = service.AddBasicDraft(state, "Alice");
        Assert.True(draft.IsDraft);
        Assert.Single(draft.Slots);
        Assert.Equal(original, fake.Files[Target]);
        Assert.False(draft.CanExperimentalPush(false));
        Assert.True(draft.CanExperimentalPush(true));

        var renamed = service.RenameDraft(draft, draft.Slots[0].Slot, "Bob");
        Assert.Equal("Bob", renamed.Slots[0].Name);
        Assert.Equal(original, fake.Files[Target]);
        var result = service.PushDatabase(renamed, allowUnavailableProcessCheck: false,
            experimentalAcknowledged: true);
        Assert.Equal(renamed.Database, fake.Files[Target]);
        Assert.Equal(MiiNandStore.Sha(renamed.Database!), result.PostWriteSha256);
    }

    [Fact]
    public void PushUsesVerifiedBackupsTempCasAtomicMoveAndAudit()
    {
        var fake = NewLive();
        var before = fake.Files[Target];
        var replacement = _format.Insert(before, _format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        var store = new MiiNandStore(fake, _root);
        var result = store.ReplaceTransactional(Snapshot(fake), _format, replacement, MiiNandStore.Sha(before), false, false);

        Assert.Equal(replacement, fake.Files[Target]);
        Assert.True(result.HadLiveSource);
        Assert.NotNull(result.BackupDirectory);
        Assert.Contains(fake.Events, x => x.StartsWith("write-new:/nand/RFL_DB.dat.sesame-backup-", StringComparison.Ordinal));
        Assert.Contains(fake.Events, x => x.StartsWith("write-new:/nand/RFL_DB.dat.sesame-tmp-", StringComparison.Ordinal));
        Assert.DoesNotContain("write-new:" + Target, fake.Events);
        Assert.Equal(3, fake.ProcessChecks);
        Assert.Contains("mv -f --", fake.LastMoveCommand);
        Assert.Contains("sync -f", fake.LastMoveCommand);
        Assert.Contains("test ! -L", fake.LastMoveCommand);
        Assert.True(fake.LastMoveCommand.IndexOf("sync -f", StringComparison.Ordinal) <
                    fake.LastMoveCommand.IndexOf("if test", StringComparison.Ordinal));
        Assert.True(fake.LastMoveCommand.IndexOf("if test", StringComparison.Ordinal) <
                    fake.LastMoveCommand.IndexOf("mv -f", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory!, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(_root, "mii.log")));
        var manifest = JsonSerializer.Deserialize<MiiBackupManifest>(File.ReadAllText(Path.Combine(result.BackupDirectory!, "manifest.json")))!;
        Assert.Equal(fake.HostId, manifest.HostId);
        Assert.Equal(Target, manifest.TargetPath);
        Assert.Equal(MiiNandStore.Sha(before), manifest.BackupSha256);
    }

    [Fact]
    public void LiveFileRaceFailsCasBeforeRename()
    {
        var fake = NewLive();
        var before = fake.Files[Target];
        var replacement = _format.Insert(before, _format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        fake.MutateLiveAfterThirdProcessCheck = true;
        var ex = Assert.Throws<MiiTransactionException>(() => new MiiNandStore(fake, _root)
            .ReplaceTransactional(Snapshot(fake), _format, replacement, MiiNandStore.Sha(before), false, false));
        Assert.Equal(MiiTransactionOutcome.NotCommitted, ex.Outcome);
        Assert.DoesNotContain(fake.Events, x => x == "move");
    }

    [Fact]
    public void LiveFileRaceInsideFinalShellCasNeverRenames()
    {
        var fake = NewLive();
        var before = fake.Files[Target];
        var replacement = _format.Insert(before, _format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        fake.MutateAtShellCas = true;
        var ex = Assert.Throws<MiiTransactionException>(() => new MiiNandStore(fake, _root)
            .ReplaceTransactional(Snapshot(fake), _format, replacement, MiiNandStore.Sha(before), false, false));
        Assert.Equal(MiiTransactionOutcome.NotCommitted, ex.Outcome);
        Assert.Contains("shell compare-and-swap", ex.Message);
        Assert.DoesNotContain("move", fake.Events);
    }

    [Fact]
    public void HostSwitchRejectsInventoryAndRestore()
    {
        var fake = NewLive();
        var store = new MiiNandStore(fake, _root);
        var backup = store.BackupNow(Snapshot(fake), _format);
        fake.HostId = "other-profile";
        fake.Host = "other-host";
        var other = Snapshot(fake);
        Assert.Empty(store.Inventory(other));
        Assert.Throws<InvalidDataException>(() => store.Restore(other, _format, backup, false));
    }

    [Fact]
    public void HostSwitchDuringPreflightStopsBeforeRemoteOrTempWrite()
    {
        var fake = NewLive();
        var originalSnapshot = Snapshot(fake);
        var before = fake.Files[Target];
        var replacement = _format.Insert(before, _format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        fake.SwitchHostAfterFirstLiveRead = true;
        var ex = Assert.Throws<MiiTransactionException>(() => new MiiNandStore(fake, _root)
            .ReplaceTransactional(originalSnapshot, _format, replacement, MiiNandStore.Sha(before), false, false));
        Assert.Equal(MiiTransactionOutcome.NotCommitted, ex.Outcome);
        Assert.DoesNotContain(fake.Events, x => x.StartsWith("write-new:", StringComparison.Ordinal));
        Assert.Equal(before, fake.Files[Target]);
    }

    [Fact]
    public void MissingTargetRestoreIsAllowedWithoutPretendPreBackup()
    {
        var fake = NewLive();
        var store = new MiiNandStore(fake, _root);
        var backup = store.BackupNow(Snapshot(fake), _format);
        fake.Files.Remove(Target);
        var result = store.Restore(Snapshot(fake), _format, backup, false);
        Assert.False(result.HadLiveSource);
        Assert.Null(result.BackupDirectory);
        Assert.True(fake.Files.ContainsKey(Target));
        Assert.DoesNotContain(fake.Events, x => x.StartsWith("write-new:/nand/RFL_DB.dat.sesame-backup-", StringComparison.Ordinal));
        Assert.Contains(backup.Directory, File.ReadAllText(Path.Combine(_root, "mii.log")));
    }

    [Fact]
    public void MissingTargetRenameFailureIsNotCommittedAndKeepsRestoreLocation()
    {
        var fake = NewLive();
        var store = new MiiNandStore(fake, _root);
        var backup = store.BackupNow(Snapshot(fake), _format);
        fake.Files.Remove(Target);
        fake.MoveBehavior = MoveBehavior.ThrowBeforeCommit;
        var ex = Assert.Throws<MiiTransactionException>(() => store.Restore(Snapshot(fake), _format, backup, false));
        Assert.Equal(MiiTransactionOutcome.NotCommitted, ex.Outcome);
        Assert.Equal(backup.Directory, ex.BackupDirectory);
        Assert.False(fake.Files.ContainsKey(Target));
    }

    [Fact]
    public void MissingTargetAppearingInsideFinalShellCasIsNeverOverwritten()
    {
        var fake = NewLive();
        var store = new MiiNandStore(fake, _root);
        var backup = store.BackupNow(Snapshot(fake), _format);
        fake.Files.Remove(Target);
        fake.CreateTargetAtShellCas = true;
        var ex = Assert.Throws<MiiTransactionException>(() => store.Restore(Snapshot(fake), _format, backup, false));
        Assert.Equal(MiiTransactionOutcome.NotCommitted, ex.Outcome);
        Assert.Equal(backup.Directory, ex.BackupDirectory);
        Assert.Equal([0xCA, 0xFE], fake.Files[Target]);
        Assert.DoesNotContain("move", fake.Events);
    }

    [Fact]
    public void RestoreOfCorruptLiveFileBacksUpItsExactBytesFirst()
    {
        var fake = NewLive();
        var store = new MiiNandStore(fake, _root);
        var good = store.BackupNow(Snapshot(fake), _format);
        var corrupt = (byte[])fake.Files[Target].Clone();
        corrupt[50] ^= 0x55;
        fake.Files[Target] = corrupt;
        var result = store.Restore(Snapshot(fake), _format, good, false);
        Assert.True(result.HadLiveSource);
        Assert.NotNull(result.BackupDirectory);
        Assert.Equal(corrupt, File.ReadAllBytes(Path.Combine(result.BackupDirectory!, "database.bin")));
        Assert.True(_format.Validate(fake.Files[Target]).IsValid);
    }

    [Fact]
    public void PartialUploadNeverMovesLiveFile()
    {
        var fake = NewLive();
        var before = fake.Files[Target];
        var replacement = _format.Insert(before, _format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        fake.CorruptTempUpload = true;
        var ex = Assert.Throws<MiiTransactionException>(() => new MiiNandStore(fake, _root)
            .ReplaceTransactional(Snapshot(fake), _format, replacement, MiiNandStore.Sha(before), false, false));
        Assert.Equal(MiiTransactionOutcome.NotCommitted, ex.Outcome);
        Assert.Equal(before, fake.Files[Target]);
        Assert.DoesNotContain("move", fake.Events);
    }

    [Fact]
    public void RenameTimeoutAfterCommitReconcilesToSuccess()
    {
        var fake = NewLive();
        var before = fake.Files[Target];
        var replacement = _format.Insert(before, _format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        fake.MoveBehavior = MoveBehavior.CommitThenThrow;
        var result = new MiiNandStore(fake, _root)
            .ReplaceTransactional(Snapshot(fake), _format, replacement, MiiNandStore.Sha(before), false, false);
        Assert.True(result.ReconciledAfterTransportFailure);
        Assert.Equal(replacement, fake.Files[Target]);
    }

    [Fact]
    public void RenameFailureWithOriginalLiveReportsNotCommitted()
    {
        var fake = NewLive();
        var before = fake.Files[Target];
        var replacement = _format.Insert(before, _format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        fake.MoveBehavior = MoveBehavior.ThrowBeforeCommit;
        var ex = Assert.Throws<MiiTransactionException>(() => new MiiNandStore(fake, _root)
            .ReplaceTransactional(Snapshot(fake), _format, replacement, MiiNandStore.Sha(before), false, false));
        Assert.Equal(MiiTransactionOutcome.NotCommitted, ex.Outcome);
        Assert.Contains("hash is unchanged", ex.Message);
    }

    [Fact]
    public void PostMoveDisconnectReportsIndeterminateNeverRefused()
    {
        var fake = NewLive();
        var before = fake.Files[Target];
        var replacement = _format.Insert(before, _format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        fake.MoveBehavior = MoveBehavior.CommitThenDisconnect;
        var ex = Assert.Throws<MiiTransactionException>(() => new MiiNandStore(fake, _root)
            .ReplaceTransactional(Snapshot(fake), _format, replacement, MiiNandStore.Sha(before), false, false));
        Assert.Equal(MiiTransactionOutcome.Indeterminate, ex.Outcome);
        Assert.DoesNotContain("refused", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(ex.BackupDirectory);
    }

    [Fact]
    public void PostWriteMismatchReportsIndeterminateWithBackup()
    {
        var fake = NewLive();
        var before = fake.Files[Target];
        var replacement = _format.Insert(before, _format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        fake.MoveBehavior = MoveBehavior.CommitCorrupt;
        var ex = Assert.Throws<MiiTransactionException>(() => new MiiNandStore(fake, _root)
            .ReplaceTransactional(Snapshot(fake), _format, replacement, MiiNandStore.Sha(before), false, false));
        Assert.Equal(MiiTransactionOutcome.Indeterminate, ex.Outcome);
        Assert.NotNull(ex.BackupDirectory);
    }

    [Fact]
    public void CorruptLocalBackupRereadStopsBeforeRemoteOrTempWrite()
    {
        var fake = NewLive();
        var before = fake.Files[Target];
        var replacement = _format.Insert(before, _format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        var store = new MiiNandStore(fake, _root, file => File.WriteAllBytes(file, [9, 9, 9]));
        Assert.Throws<IOException>(() => store.ReplaceTransactional(Snapshot(fake), _format, replacement,
            MiiNandStore.Sha(before), false, false));
        Assert.DoesNotContain(fake.Events, x => x.StartsWith("write-new:", StringComparison.Ordinal));
        Assert.Equal(before, fake.Files[Target]);
    }

    [Fact]
    public void CorruptLocalManifestRereadStopsBeforeRemoteOrTempWrite()
    {
        var fake = NewLive();
        var before = fake.Files[Target];
        var replacement = _format.Insert(before, _format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        var store = new MiiNandStore(fake, _root, afterLocalManifestWritten: file => File.WriteAllText(file, "{}"));
        Assert.Throws<IOException>(() => store.ReplaceTransactional(Snapshot(fake), _format, replacement,
            MiiNandStore.Sha(before), false, false));
        Assert.DoesNotContain(fake.Events, x => x.StartsWith("write-new:", StringComparison.Ordinal));
        Assert.Equal(before, fake.Files[Target]);
    }

    [Fact]
    public void TamperedBackupIsRejectedBeforeAnyRemoteMutation()
    {
        var fake = NewLive();
        var store = new MiiNandStore(fake, _root);
        var backup = store.BackupNow(Snapshot(fake), _format);
        File.WriteAllBytes(Path.Combine(backup.Directory, backup.Manifest.BackupFile), [1, 2, 3]);
        Assert.Empty(store.Inventory(Snapshot(fake)));
        fake.Events.Clear();
        Assert.Throws<InvalidDataException>(() => store.Restore(Snapshot(fake), _format, backup, false));
        Assert.Empty(fake.Events);
    }

    [Fact]
    public void TamperedManifestIsNotOfferedOrRestored()
    {
        var fake = NewLive();
        var store = new MiiNandStore(fake, _root);
        var backup = store.BackupNow(Snapshot(fake), _format);
        var manifestPath = Path.Combine(backup.Directory, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(backup.Manifest with
        {
            BackupFile = "..\\outside.bin"
        }));

        Assert.Empty(store.Inventory(Snapshot(fake)));
        fake.Events.Clear();
        Assert.Throws<InvalidDataException>(() => store.Restore(Snapshot(fake), _format, backup, false));
        Assert.Empty(fake.Events);
    }

    [Fact]
    public void RestoreRejectsBackupThatEscapesTheProtectedStore()
    {
        var fake = NewLive();
        var store = new MiiNandStore(fake, _root);
        var backup = store.BackupNow(Snapshot(fake), _format);
        var escaped = backup with { Directory = Path.GetTempPath() };

        fake.Events.Clear();
        Assert.Throws<InvalidDataException>(() => store.Restore(Snapshot(fake), _format, escaped, false));
        Assert.Empty(fake.Events);
    }

    [Fact]
    public void UnapprovedPathNeverStartsATransaction()
    {
        var fake = NewLive();
        var before = fake.Files[Target];
        var replacement = _format.Insert(before, _format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        var unapproved = Snapshot(fake) with { PathApproved = false, PathStatus = "Ambiguous path." };

        var ex = Assert.Throws<MiiTransactionException>(() => new MiiNandStore(fake, _root)
            .ReplaceTransactional(unapproved, _format, replacement, MiiNandStore.Sha(before), false, false));

        Assert.Equal(MiiTransactionOutcome.NotCommitted, ex.Outcome);
        Assert.Empty(fake.Events);
        Assert.Equal(before, fake.Files[Target]);
    }

    [Fact]
    public void ProcessRunningOrUnavailableFailsClosed()
    {
        var fake = NewLive();
        var before = fake.Files[Target];
        var replacement = _format.Insert(before, _format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        fake.ProcessExitCode = 0;
        Assert.Throws<MiiTransactionException>(() => new MiiNandStore(fake, _root)
            .ReplaceTransactional(Snapshot(fake), _format, replacement, MiiNandStore.Sha(before), false, false));
        fake.ProcessExitCode = 2;
        Assert.Throws<MiiTransactionException>(() => new MiiNandStore(fake, _root)
            .ReplaceTransactional(Snapshot(fake), _format, replacement, MiiNandStore.Sha(before), false, false));
        Assert.DoesNotContain(fake.Events, x => x.StartsWith("write-new:", StringComparison.Ordinal));
    }

    [Fact]
    public void OperationSnapshotDoesNotFollowLaterTargetSelection()
    {
        var fake = NewLive();
        var selected = new MutableSelection { Kind = MiiTargetKind.Wii, Path = Target };
        var captured = new MiiOperationSnapshot(selected.Kind, selected.Path, fake.HostId, fake.Host);
        selected.Kind = MiiTargetKind.Eden;
        selected.Path = "/other/MiiDatabase.dat";
        Assert.Equal(MiiTargetKind.Wii, captured.Kind);
        Assert.Equal(Target, captured.TargetPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private FakeTransport NewLive()
    {
        var fake = new FakeTransport();
        fake.Directories.Add("/nand");
        fake.Files[Target] = MiiFormatWii.CreateEmptyDatabase();
        return fake;
    }

    private static MiiOperationSnapshot Snapshot(FakeTransport fake) =>
        new(MiiTargetKind.Wii, Target, fake.HostId, fake.Host);

    private sealed class MutableSelection
    {
        public MiiTargetKind Kind { get; set; }
        public string Path { get; set; } = "";
    }

    private enum MoveBehavior { Normal, ThrowBeforeCommit, CommitThenThrow, CommitThenDisconnect, CommitCorrupt }

    private sealed class FakeTransport : IMiiNandTransport
    {
        public bool IsConnected { get; set; } = true;
        public string Host { get; set; } = "deck.example";
        public string HostId { get; set; } = "profile-stable-id";
        public string Home { get; set; } = "/home/deck";
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal) { "/" };
        public List<string> Events { get; } = [];
        public int ProcessExitCode { get; set; } = 1;
        public int ProcessChecks { get; private set; }
        public bool MutateLiveAfterThirdProcessCheck { get; set; }
        public bool MutateAtShellCas { get; set; }
        public bool CreateTargetAtShellCas { get; set; }
        public bool CorruptTempUpload { get; set; }
        public bool SwitchHostAfterFirstLiveRead { get; set; }
        public MoveBehavior MoveBehavior { get; set; }
        public string LastMoveCommand { get; private set; } = "";
        private bool _throwReads;
        private int _liveReads;

        public bool Exists(string path) => Files.ContainsKey(path) || Directories.Contains(path);
        public long FileLength(string path) => Files.TryGetValue(path, out var bytes) ? bytes.Length : -1;

        public byte[] ReadBytes(string path)
        {
            if (_throwReads) throw new IOException("disconnected after move");
            if (!Files.TryGetValue(path, out var bytes)) throw new FileNotFoundException(path);
            var result = (byte[])bytes.Clone();
            if (path == Target && SwitchHostAfterFirstLiveRead && ++_liveReads == 1)
            {
                HostId = "switched-profile";
                Host = "switched-host";
            }
            return result;
        }

        public void WriteNew(string path, byte[] data)
        {
            Events.Add("write-new:" + path);
            if (Files.ContainsKey(path)) throw new IOException("exists");
            var bytes = (byte[])data.Clone();
            if (CorruptTempUpload && path.Contains(".sesame-tmp-", StringComparison.Ordinal))
                bytes = bytes[..Math.Max(1, bytes.Length / 2)];
            Files[path] = bytes;
        }

        public void DeleteFile(string path)
        {
            Events.Add("delete:" + path);
            Files.Remove(path);
        }

        public IReadOnlyList<string> ListFiles(string directory, string prefix) => Files.Keys
            .Where(x => DeckClient.Parent(x) == directory && Path.GetFileName(x).StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(x => x, StringComparer.Ordinal).ToList();

        public MiiCommandResult Execute(string command, int timeoutSeconds = 20)
        {
            if (command.StartsWith("pgrep", StringComparison.Ordinal))
            {
                ProcessChecks++;
                Events.Add("process-check");
                if (MutateLiveAfterThirdProcessCheck && ProcessChecks == 3 && Files.TryGetValue(Target, out var live))
                {
                    live = (byte[])live.Clone();
                    live[10] ^= 1;
                    Files[Target] = live;
                }
                return new MiiCommandResult(ProcessExitCode, "");
            }
            if (!command.Contains("mv -f --", StringComparison.Ordinal)) return new MiiCommandResult(0, "");
            LastMoveCommand = command;
            if (CreateTargetAtShellCas)
            {
                Files[Target] = [0xCA, 0xFE];
                return new MiiCommandResult(1, "__SESAME_CAS_MISMATCH");
            }
            if (MutateAtShellCas && Files.TryGetValue(Target, out var shellLive))
            {
                shellLive = (byte[])shellLive.Clone();
                shellLive[11] ^= 1;
                Files[Target] = shellLive;
                return new MiiCommandResult(1, "__SESAME_CAS_MISMATCH");
            }
            if (MoveBehavior == MoveBehavior.ThrowBeforeCommit) throw new TimeoutException("before move");
            var temp = Files.Keys.Single(x => x.StartsWith(Target + ".sesame-tmp-", StringComparison.Ordinal));
            Files[Target] = Files[temp];
            Files.Remove(temp);
            Events.Add("move");
            if (MoveBehavior == MoveBehavior.CommitCorrupt) Files[Target][100] ^= 1;
            if (MoveBehavior == MoveBehavior.CommitThenDisconnect) _throwReads = true;
            if (MoveBehavior is MoveBehavior.CommitThenThrow or MoveBehavior.CommitThenDisconnect)
                throw new TimeoutException("after move");
            return new MiiCommandResult(0, "");
        }
    }
}
