using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sesame.Models;

namespace Sesame.Services.Mii;

public readonly record struct MiiCommandResult(int ExitCode, string Output);

public interface IMiiNandTransport
{
    bool IsConnected { get; }
    string Host { get; }
    string HostId { get; }
    bool Exists(string path);
    byte[] ReadBytes(string path);
    void WriteNew(string path, byte[] data);
    void DeleteFile(string path);
    IReadOnlyList<string> ListFiles(string directory, string prefix);
    MiiCommandResult Execute(string command, int timeoutSeconds = 20);
}

public sealed class DeckMiiNandTransport(DeckClient client) : IMiiNandTransport
{
    public bool IsConnected => client.IsConnected;
    public string Host => client.ActiveProfile?.Host ?? "unknown";
    public string HostId => client.ActiveProfile?.Id ?? "unknown";

    public bool Exists(string path) => client.Exists(path);
    public byte[] ReadBytes(string path) => client.ReadBytes(path);

    public void WriteNew(string path, byte[] data)
    {
        client.WriteNewBytes(path, data);
    }

    public void DeleteFile(string path)
    {
        if (client.Exists(path)) client.DeletePath(path, directory: false);
    }

    public IReadOnlyList<string> ListFiles(string directory, string prefix)
    {
        if (!client.Exists(directory)) return [];
        return client.List(directory)
            .Where(x => !x.IsDirectory && x.Name.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.FullPath)
            .ToList();
    }

    public MiiCommandResult Execute(string command, int timeoutSeconds = 20)
    {
        const string marker = "__SESAME_RC:";
        var wrapped = "{ " + command + "; }; rc=$?; printf '\\n" + marker + "%s\\n' \"$rc\"";
        var output = client.Execute(wrapped, timeoutSeconds);
        var index = output.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0) throw new IOException("Remote command returned no verifiable exit status.");
        var tail = output[(index + marker.Length)..].Trim();
        if (!int.TryParse(tail.Split('\n', 2)[0].Trim(), out var code))
            throw new IOException("Remote command returned an invalid exit status.");
        return new MiiCommandResult(code, output[..index].TrimEnd());
    }
}

public sealed record MiiOperationSnapshot(MiiTargetKind Kind, string TargetPath, string HostId, string Host)
{
    public static MiiOperationSnapshot Capture(MiiTargetKind kind, string targetPath, IMiiNandTransport transport) =>
        new(kind, targetPath, transport.HostId, transport.Host);
}

public enum MiiTransactionOutcome { NotCommitted, Indeterminate }

public sealed class MiiTransactionException : IOException
{
    public MiiTransactionException(MiiTransactionOutcome outcome, string message, string? backupDirectory = null,
        Exception? inner = null) : base(message, inner)
    {
        Outcome = outcome;
        BackupDirectory = backupDirectory;
    }

    public MiiTransactionOutcome Outcome { get; }
    public string? BackupDirectory { get; }
}

public sealed record MiiBackupManifest
{
    public string TargetPath { get; init; } = "";
    public MiiTargetKind TargetKind { get; init; }
    public string Host { get; init; } = "";
    public string HostId { get; init; } = "";
    public long Size { get; init; }
    public bool HadLiveSource { get; init; }
    public string PreWriteSha256 { get; init; } = "";
    public string PostWriteSha256 { get; init; } = "";
    public string BackupSha256 { get; init; } = "";
    public DateTimeOffset TimestampUtc { get; init; }
    public string SesameVersion { get; init; } = "";
    public string BackupFile { get; init; } = "database.bin";
}

public sealed record MiiBackup(string Directory, MiiBackupManifest Manifest);

public sealed record MiiPushResult(
    string PostWriteSha256,
    string? BackupDirectory,
    bool HadLiveSource,
    bool ReconciledAfterTransportFailure);

public sealed class MiiNandStore
{
    private const int RemoteRetention = 10;
    private readonly IMiiNandTransport _transport;
    private readonly string _backupRoot;
    private readonly Action<string>? _afterLocalBackupWritten;
    private readonly Action<string>? _afterLocalManifestWritten;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public MiiNandStore(IMiiNandTransport transport, string? backupRoot = null,
        Action<string>? afterLocalBackupWritten = null, Action<string>? afterLocalManifestWritten = null)
    {
        _transport = transport;
        _backupRoot = backupRoot ?? AppDataPaths.Combine("mii-backups");
        _afterLocalBackupWritten = afterLocalBackupWritten;
        _afterLocalManifestWritten = afterLocalManifestWritten;
    }

    public IReadOnlyList<MiiBackup> Inventory(MiiOperationSnapshot target)
    {
        EnsureSnapshot(target);
        if (!Directory.Exists(_backupRoot)) return [];
        var result = new List<MiiBackup>();
        foreach (var manifestPath in Directory.EnumerateFiles(_backupRoot, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<MiiBackupManifest>(File.ReadAllText(manifestPath), Json);
                if (manifest is null || manifest.TargetKind != target.Kind ||
                    !string.Equals(manifest.HostId, target.HostId, StringComparison.Ordinal) ||
                    !string.Equals(manifest.TargetPath, target.TargetPath, StringComparison.Ordinal)) continue;
                result.Add(new MiiBackup(Path.GetDirectoryName(manifestPath)!, manifest));
            }
            catch { /* an unreadable manifest is never offered for restore */ }
        }
        return result.OrderByDescending(x => x.Manifest.TimestampUtc).ToList();
    }

    public MiiBackup BackupNow(MiiOperationSnapshot target, IMiiFormat format)
    {
        EnsureSnapshot(target);
        EnsureConnected();
        if (!_transport.Exists(target.TargetPath)) throw new FileNotFoundException("The live Mii database is missing.");
        var bytes = _transport.ReadBytes(target.TargetPath);
        EnsureValid(format, bytes, "The live Mii database is not structurally valid.");
        var hash = Sha(bytes);
        return CreateLocalBackup(target, bytes, hash, postHash: hash, hadLiveSource: true);
    }

    public MiiPushResult ReplaceTransactional(
        MiiOperationSnapshot target,
        IMiiFormat format,
        byte[] replacement,
        string? expectedSourceSha256,
        bool allowMissingSource,
        bool allowUnavailableProcessCheck,
        string? recoveryBackupDirectory = null)
    {
        EnsureSnapshot(target);
        EnsureConnected();
        EnsureValid(format, replacement, "Replacement Mii database failed validation.");
        var postHash = Sha(replacement);
        CheckProcesses(target.Kind, allowUnavailableProcessCheck);
        EnsureSnapshot(target);

        var hadLiveSource = _transport.Exists(target.TargetPath);
        if (!hadLiveSource && !allowMissingSource)
            throw NotCommitted("The live Mii database is missing; push was not started.");
        var parent = DeckClient.Parent(target.TargetPath);
        if (!hadLiveSource && !_transport.Exists(parent))
            throw NotCommitted("The target directory is missing; restore was not started.");

        byte[]? source = hadLiveSource ? _transport.ReadBytes(target.TargetPath) : null;
        var preHash = source is null ? "" : Sha(source);
        if (source is not null && !allowMissingSource)
            EnsureValid(format, source, "The live source database failed validation; push was not started.");
        if (!string.IsNullOrWhiteSpace(expectedSourceSha256) &&
            !FixedHashEquals(preHash, expectedSourceSha256))
            throw NotCommitted("The live database changed since it was loaded; no write was attempted.");

        MiiBackup? localBackup = null;
        string? remoteBackup = null;
        if (source is not null)
        {
            localBackup = CreateLocalBackup(target, source, preHash, postHash, hadLiveSource: true);
            EnsureSnapshot(target);
            remoteBackup = CreateRemoteBackup(target, source, preHash);
        }

        CheckProcesses(target.Kind, allowUnavailableProcessCheck);
        EnsureSnapshot(target);
        var temp = target.TargetPath + ".sesame-tmp-" + Guid.NewGuid().ToString("N");
        var moved = false;
        try
        {
            _transport.WriteNew(temp, replacement);
            var staged = _transport.ReadBytes(temp);
            if (!FixedHashEquals(Sha(staged), postHash))
                throw NotCommitted("Staged upload verification failed; live NAND was not changed.", localBackup?.Directory);
            EnsureValid(format, staged, "Staged database failed structural verification.");

            CheckProcesses(target.Kind, allowUnavailableProcessCheck);
            EnsureSnapshot(target);
            CompareAndSwapLive(target, hadLiveSource, preHash);

            try
            {
                AtomicMove(temp, target.TargetPath, hadLiveSource, preHash);
                moved = true;
            }
            catch (MiiTransactionException ex) when (ex.Outcome == MiiTransactionOutcome.NotCommitted)
            {
                if (ex.BackupDirectory is null && (localBackup?.Directory ?? recoveryBackupDirectory) is { } recovery)
                    throw NotCommitted(ex.Message, recovery, ex);
                throw;
            }
            catch (Exception ex)
            {
                var reconciled = ReconcileAfterMoveFailure(target, format, preHash, postHash, hadLiveSource,
                    localBackup?.Directory ?? recoveryBackupDirectory, ex);
                if (reconciled is not null)
                {
                    Audit("replace-reconciled", target, preHash, postHash, localBackup, remoteBackup);
                    return reconciled;
                }
                throw;
            }

            byte[] verified;
            try { verified = _transport.ReadBytes(target.TargetPath); }
            catch (Exception ex)
            {
                throw Indeterminate("The rename may have committed, but the live database could not be reread. " +
                                    "Connection and NAND state must be checked before retrying.",
                    localBackup?.Directory ?? recoveryBackupDirectory, ex);
            }
            if (!FixedHashEquals(Sha(verified), postHash) || !format.Validate(verified).IsValid)
                    throw Indeterminate("The live file after rename does not match the staged database. Restore from the retained backup before retrying.",
                    localBackup?.Directory ?? recoveryBackupDirectory);

            Audit("replace", target, preHash, postHash, localBackup, remoteBackup);
            return new MiiPushResult(postHash, localBackup?.Directory, hadLiveSource, false);
        }
        finally
        {
            if (!moved)
            {
                try { _transport.DeleteFile(temp); } catch { /* never hide the primary result */ }
            }
        }
    }

    public MiiPushResult Restore(MiiOperationSnapshot target, IMiiFormat format, MiiBackup backup,
        bool allowUnavailableProcessCheck)
    {
        EnsureSnapshot(target);
        ValidateBackupBinding(target, backup);
        var file = Path.Combine(backup.Directory, backup.Manifest.BackupFile);
        var bytes = File.ReadAllBytes(file);
        if (!FixedHashEquals(Sha(bytes), backup.Manifest.BackupSha256))
            throw new InvalidDataException("Backup hash verification failed; restore was not started.");
        EnsureValid(format, bytes, "Backup format validation failed; restore was not started.");
        var result = ReplaceTransactional(target, format, bytes, expectedSourceSha256: null,
            allowMissingSource: true, allowUnavailableProcessCheck, recoveryBackupDirectory: backup.Directory);
        Audit("restore-source", target, "", result.PostWriteSha256,
            localBackup: null, remoteBackup: null, restoreSource: backup);
        return result;
    }

    private MiiBackup CreateLocalBackup(MiiOperationSnapshot target, byte[] source, string sourceHash,
        string postHash, bool hadLiveSource)
    {
        Directory.CreateDirectory(_backupRoot);
        AppDataPaths.RestrictDirectory(_backupRoot);
        var stamp = DateTimeOffset.UtcNow;
        var dir = Path.Combine(_backupRoot, AppDataPaths.SafeFileName(target.HostId), target.Kind.ToString(),
            stamp.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        AppDataPaths.RestrictDirectory(dir);
        var file = Path.Combine(dir, "database.bin");
        var temp = file + ".tmp";
        File.WriteAllBytes(temp, source);
        using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            stream.Flush(flushToDisk: true);
        File.Move(temp, file);
        _afterLocalBackupWritten?.Invoke(file);
        var rereadHash = Sha(File.ReadAllBytes(file));
        if (!FixedHashEquals(rereadHash, sourceHash))
            throw new IOException("Local backup reread/hash verification failed; live NAND was not changed.");
        AppDataPaths.RestrictFile(file);

        var manifest = new MiiBackupManifest
        {
            TargetPath = target.TargetPath,
            TargetKind = target.Kind,
            Host = target.Host,
            HostId = target.HostId,
            Size = source.Length,
            HadLiveSource = hadLiveSource,
            PreWriteSha256 = hadLiveSource ? sourceHash : "",
            PostWriteSha256 = postHash,
            BackupSha256 = sourceHash,
            TimestampUtc = stamp,
            SesameVersion = AppVersion.Current,
            BackupFile = "database.bin"
        };
        var manifestPath = Path.Combine(dir, "manifest.json");
        var manifestTemp = manifestPath + ".tmp";
        var manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Json));
        var manifestHash = Sha(manifestBytes);
        File.WriteAllBytes(manifestTemp, manifestBytes);
        using (var stream = new FileStream(manifestTemp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            stream.Flush(flushToDisk: true);
        File.Move(manifestTemp, manifestPath);
        _afterLocalManifestWritten?.Invoke(manifestPath);
        var verifiedManifestBytes = File.ReadAllBytes(manifestPath);
        MiiBackupManifest? verifiedManifest = null;
        try { verifiedManifest = JsonSerializer.Deserialize<MiiBackupManifest>(verifiedManifestBytes, Json); }
        catch (JsonException) { /* handled by the common fail-closed check */ }
        if (!FixedHashEquals(Sha(verifiedManifestBytes), manifestHash) || verifiedManifest is null ||
            verifiedManifest.BackupSha256 != sourceHash ||
            verifiedManifest.HostId != target.HostId || verifiedManifest.TargetPath != target.TargetPath)
            throw new IOException("Local backup manifest reread verification failed; live NAND was not changed.");
        AppDataPaths.RestrictFile(manifestPath);
        return new MiiBackup(dir, manifest);
    }

    private string CreateRemoteBackup(MiiOperationSnapshot target, byte[] source, string sourceHash)
    {
        var parent = DeckClient.Parent(target.TargetPath);
        var name = Path.GetFileName(target.TargetPath) + ".sesame-backup-" +
                   DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + sourceHash[..12];
        var path = DeckClient.Combine(parent, name);
        _transport.WriteNew(path, source);
        if (!FixedHashEquals(Sha(_transport.ReadBytes(path)), sourceHash))
            throw new IOException("Remote backup reread/hash verification failed; live NAND was not changed.");
        foreach (var old in _transport.ListFiles(parent, Path.GetFileName(target.TargetPath) + ".sesame-backup-")
                     .Skip(RemoteRetention))
        {
            try { _transport.DeleteFile(old); } catch { /* retention is best-effort */ }
        }
        return path;
    }

    private void CompareAndSwapLive(MiiOperationSnapshot target, bool existed, string preHash)
    {
        var existsNow = _transport.Exists(target.TargetPath);
        if (existsNow != existed)
            throw NotCommitted("The live target existence changed immediately before rename; no commit was attempted.");
        if (existed && !FixedHashEquals(Sha(_transport.ReadBytes(target.TargetPath)), preHash))
            throw NotCommitted("The live database changed immediately before rename; no commit was attempted.");
    }

    private void AtomicMove(string temp, string target, bool expectedLiveSource, string expectedSourceSha256)
    {
        var parent = DeckClient.Parent(target);
        var targetQuote = DeckClient.ShQuote(target);
        var cas = expectedLiveSource
            ? "test -f " + targetQuote + " && test ! -L " + targetQuote +
              " && actual=$(sha256sum -- " + targetQuote +
              " 2>/dev/null | cut -d ' ' -f1 | tr '[:lower:]' '[:upper:]') && test \"$actual\" = " +
              DeckClient.ShQuote(expectedSourceSha256)
            : "test ! -e " + targetQuote + " && test ! -L " + targetQuote;
        var move = "mv -f -- " + DeckClient.ShQuote(temp) + " " + targetQuote + " && " +
                   "(sync -f " + targetQuote + " 2>/dev/null || sync) && " +
                   "(sync -f " + DeckClient.ShQuote(parent) + " 2>/dev/null || sync)";
        var command = "(sync -f " + DeckClient.ShQuote(temp) + " 2>/dev/null || sync) && " +
                      "if " + cas + "; then " + move +
                      "; else printf '__SESAME_CAS_MISMATCH\\n'; false; fi";
        var result = _transport.Execute(command, 30);
        if (result.ExitCode != 0 && result.Output.Contains("__SESAME_CAS_MISMATCH", StringComparison.Ordinal))
            throw NotCommitted("The live database changed at the final shell compare-and-swap; rename was not committed.");
        if (result.ExitCode != 0) throw new IOException("Atomic rename command failed: " + result.Output);
    }

    private MiiPushResult? ReconcileAfterMoveFailure(MiiOperationSnapshot target, IMiiFormat format,
        string preHash, string postHash, bool hadLiveSource, string? backupDirectory, Exception moveError)
    {
        try
        {
            if (!_transport.Exists(target.TargetPath))
            {
                if (!hadLiveSource)
                    throw NotCommitted("Rename did not create the missing live target; the verified restore backup is unchanged.",
                        backupDirectory, moveError);
                throw Indeterminate("Rename failed and the previously existing live target is now missing; restore from backup before retrying.",
                    backupDirectory, moveError);
            }
            var live = _transport.ReadBytes(target.TargetPath);
            var liveHash = Sha(live);
            if (FixedHashEquals(liveHash, postHash) && format.Validate(live).IsValid)
                return new MiiPushResult(postHash, backupDirectory, hadLiveSource, true);
            if (hadLiveSource && FixedHashEquals(liveHash, preHash))
                throw NotCommitted("Rename did not commit; the live hash is unchanged. The staging failure can be retried.",
                    backupDirectory, moveError);
            throw Indeterminate("Rename outcome is indeterminate: the live hash is neither the pre-write nor the staged hash. " +
                                "Restore from backup before retrying.", backupDirectory, moveError);
        }
        catch (MiiTransactionException) { throw; }
        catch (Exception ex)
        {
            throw Indeterminate("Rename may have committed, but disconnect/timeout prevented reconciliation. " +
                                "Do not retry until the live file is reread and compared with the audit hashes.",
                backupDirectory, ex);
        }
    }

    private void CheckProcesses(MiiTargetKind kind, bool allowUnavailable)
    {
        var names = kind == MiiTargetKind.Wii
            ? new[] { "dolphin-emu", "dolphin-emu-qt2", "Dolphin" }
            : new[] { "eden", "eden-emu", "eden-qt", "eden-bin", "Eden" };
        var command = string.Join(" || ", names.Select(name =>
            "pgrep -x -- " + DeckClient.ShQuote(name) + " >/dev/null"));
        MiiCommandResult result;
        try { result = _transport.Execute(command, 8); }
        catch (Exception ex)
        {
            if (allowUnavailable) return;
            throw NotCommitted("Emulator process state could not be checked. Explicit confirmation is required before continuing.", inner: ex);
        }
        if (result.ExitCode == 0)
            throw NotCommitted("The emulator is running. Close it before changing its Mii database.");
        if (result.ExitCode != 1 && !allowUnavailable)
            throw NotCommitted("Emulator process state is unavailable. Explicit confirmation is required before continuing.");
    }

    private void EnsureSnapshot(MiiOperationSnapshot target)
    {
        if (!string.Equals(target.HostId, _transport.HostId, StringComparison.Ordinal) ||
            !string.Equals(target.Host, _transport.Host, StringComparison.Ordinal))
            throw NotCommitted("The connected host changed after the operation target was captured.");
        if (string.IsNullOrWhiteSpace(target.TargetPath)) throw new ArgumentException("Target path is required.");
    }

    private void EnsureConnected()
    {
        if (!_transport.IsConnected) throw NotCommitted("No Steam Deck session is connected.");
    }

    private static void ValidateBackupBinding(MiiOperationSnapshot target, MiiBackup backup)
    {
        var manifest = backup.Manifest;
        if (manifest.TargetKind != target.Kind ||
            !string.Equals(manifest.HostId, target.HostId, StringComparison.Ordinal) ||
            !string.Equals(manifest.TargetPath, target.TargetPath, StringComparison.Ordinal))
            throw new InvalidDataException("Backup belongs to a different host, target, or path.");
    }

    private static void EnsureValid(IMiiFormat format, byte[] bytes, string message)
    {
        var validation = format.Validate(bytes);
        if (!validation.IsValid) throw new InvalidDataException(message + " " + validation.Error);
    }

    private void Audit(string action, MiiOperationSnapshot target, string preHash, string postHash,
        MiiBackup? localBackup, string? remoteBackup, MiiBackup? restoreSource = null)
    {
        try
        {
            Directory.CreateDirectory(_backupRoot);
            AppDataPaths.RestrictDirectory(_backupRoot);
            var line = string.Join('\t', DateTimeOffset.UtcNow.ToString("O"), action,
                target.HostId, target.Host, target.Kind, target.TargetPath,
                string.IsNullOrEmpty(preHash) ? "NONE" : preHash, postHash,
                restoreSource?.Manifest.BackupSha256 ?? localBackup?.Manifest.BackupSha256 ?? "NONE",
                restoreSource?.Directory ?? localBackup?.Directory ?? "NONE", remoteBackup ?? "NONE") + Environment.NewLine;
            var log = Path.Combine(_backupRoot, "mii.log");
            File.AppendAllText(log, line, Encoding.UTF8);
            AppDataPaths.RestrictFile(log);
        }
        catch { /* audit persistence is best-effort after a verified commit */ }
    }

    private static bool FixedHashEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch { return false; }
    }

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static MiiTransactionException NotCommitted(string message, string? backup = null, Exception? inner = null) =>
        new(MiiTransactionOutcome.NotCommitted, message, backup, inner);

    private static MiiTransactionException Indeterminate(string message, string? backup = null, Exception? inner = null) =>
        new(MiiTransactionOutcome.Indeterminate, message, backup, inner);
}
