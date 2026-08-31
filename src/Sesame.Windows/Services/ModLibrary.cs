using System.IO;
using System.Text.Json;
using Sesame.Models;

namespace Sesame.Services;

public enum PackOwnershipKind
{
    Unknown,
    IsolatedDirectory,
    ExactFiles
}

public sealed class ModRecord
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Author { get; set; } = "";
    public string? ItemId { get; set; }
    public string PageUrl { get; set; } = "";
    public string GameName { get; set; } = "";
    public string System { get; set; } = "";
    public string? TitleId { get; set; }
    public string? GameId { get; set; }
    public string? FileName { get; set; }
    public string? LocalFile { get; set; }
    public string? RemotePath { get; set; }
    public string? ModFolderName { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime? DownloadedAt { get; set; }
    public DateTime? InstalledAt { get; set; }
    public PackActivationState ActivationState { get; set; } = PackActivationState.Active;
    public PackOwnershipKind OwnershipKind { get; set; }
    public List<string> OwnedRemoteFiles { get; set; } = new();

    public bool HasLocalFile => !string.IsNullOrWhiteSpace(LocalFile) && File.Exists(LocalFile);
    public bool HasInstall => !string.IsNullOrWhiteSpace(RemotePath);
}

public sealed class ModLibrary
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private List<ModRecord> _items = new();

    public IReadOnlyList<ModRecord> Items
    {
        get
        {
            lock (_gate)
                return _items.ToList();
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            _items = new List<ModRecord>();
            var path = FilePath();
            if (!File.Exists(path)) return;
            try
            {
                var loaded = JsonSerializer.Deserialize<List<ModRecord>>(File.ReadAllText(path), Json);
                if (loaded is not null)
                    _items = loaded;
            }
            catch
            {
                _items = new List<ModRecord>();
            }
        }
    }

    public string CacheDir(PackHit hit)
    {
        var parts = new List<string>();
        var source = StoreGame.Slug(hit.Source);
        if (source.Length > 0) parts.Add(source);
        if (!string.IsNullOrWhiteSpace(hit.ItemId))
            parts.Add(StoreGame.Slug(hit.ItemId));
        var title = StoreGame.Slug(hit.Title);
        if (title.Length > 28) title = title[..28];
        if (title.Length > 0) parts.Add(title);
        var folder = parts.Count > 0 ? string.Join("-", parts) : SafeKey(StoreResultCache.HitKey(hit));
        if (folder.Length > 72) folder = folder[..72];
        var dir = Path.Combine(RootDir(), "mods", folder);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public ModRecord? Find(PackHit hit)
    {
        lock (_gate)
            return FindLocked(hit);
    }

    public IReadOnlyList<ModRecord> ForGame(StoreGame game)
    {
        lock (_gate)
        {
            return _items.Where(r => MatchesGame(r, game)).ToList();
        }
    }

    public void Apply(PackHit hit)
    {
        var rec = Find(hit);
        if (hit.IsBusy || hit.IsQueued) return;
        if (rec is null)
        {
            if (!string.IsNullOrWhiteSpace(hit.LocalFile) && File.Exists(hit.LocalFile))
            {
                hit.SetDownloaded(hit.LocalFile);
                if (hit.Size <= 0)
                    hit.Size = new FileInfo(hit.LocalFile).Length;
            }
            return;
        }

        if (rec.HasLocalFile)
        {
            hit.SetDownloaded(rec.LocalFile!);
            if (hit.Size <= 0)
                hit.Size = new FileInfo(rec.LocalFile!).Length;
        }
        if (PlatformId.TryCreate(rec.System, rec.GameId, out var gameId)) hit.GameId = gameId;
        if (rec.HasInstall)
        {
            if (rec.ActivationState == PackActivationState.Staged)
                hit.SetStaged(rec.RemotePath!, "Staged (not active)", rec.LocalFile);
            else
            {
                hit.SetInstalled(rec.RemotePath!, rec.LocalFile);
                hit.SetEnabled(rec.Enabled);
            }
        }
    }

    public PackHit ToHit(ModRecord rec)
    {
        var hit = new PackHit
        {
            Title = rec.Title,
            Source = rec.Source,
            GameName = rec.GameName,
            PageUrl = rec.PageUrl,
            ItemId = rec.ItemId,
            Kind = string.IsNullOrWhiteSpace(rec.Kind) ? "Mod" : rec.Kind,
            Author = rec.Author,
            FileName = rec.FileName,
            Platform = rec.System,
            GameId = PlatformId.TryCreate(rec.System, rec.GameId, out var gameId) ? gameId : null,
            TargetPath = rec.RemotePath
        };
        Apply(hit);
        return hit;
    }

    public ModRecord RecordDownload(PackHit hit, string localFile, StoreGame game, string? titleId)
    {
        lock (_gate)
        {
            var rec = UpsertLocked(hit, game, titleId);
            rec.LocalFile = localFile;
            rec.FileName = Path.GetFileName(localFile);
            rec.DownloadedAt = DateTime.UtcNow;
            if (hit.Size <= 0 && File.Exists(localFile))
                hit.Size = new FileInfo(localFile).Length;
            SaveLocked();
            return rec;
        }
    }

    public ModRecord RecordInstall(PackHit hit, string remotePath, StoreGame game, string? titleId,
        string? localFile = null, string? folderName = null,
        PackActivationState activationState = PackActivationState.Active,
        PackOwnershipKind ownershipKind = PackOwnershipKind.Unknown,
        IEnumerable<string>? ownedRemoteFiles = null)
    {
        lock (_gate)
        {
            var rec = UpsertLocked(hit, game, titleId);
            if (!string.IsNullOrWhiteSpace(localFile))
            {
                rec.LocalFile = localFile;
                rec.FileName = Path.GetFileName(localFile);
                rec.DownloadedAt ??= DateTime.UtcNow;
            }
            rec.RemotePath = remotePath;
            rec.ModFolderName = SwitchModFolders.BaseName(
                folderName ?? Path.GetFileName(remotePath.TrimEnd('/')));
            rec.Enabled = !SwitchModFolders.IsDisabled(rec.ModFolderName) &&
                          !SwitchModFolders.IsDisabled(Path.GetFileName(remotePath.TrimEnd('/')));
            rec.InstalledAt = DateTime.UtcNow;
            rec.ActivationState = activationState;
            rec.OwnershipKind = ownershipKind;
            if (ownedRemoteFiles is not null)
            {
                rec.OwnedRemoteFiles = ownedRemoteFiles
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(NormalizeRemotePath)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList();
            }
            SaveLocked();
            return rec;
        }
    }

    public void RecordToggle(PackHit hit, string remotePath, bool enabled)
    {
        lock (_gate)
        {
            var rec = FindLocked(hit);
            if (rec is null) return;
            rec.RemotePath = remotePath;
            rec.ModFolderName = SwitchModFolders.BaseName(Path.GetFileName(remotePath.TrimEnd('/')));
            rec.Enabled = enabled;
            SaveLocked();
        }
    }

    public static PackOwnershipKind EffectiveOwnership(ModRecord? record)
    {
        if (record is null) return PackOwnershipKind.Unknown;
        if (record.OwnershipKind != PackOwnershipKind.Unknown) return record.OwnershipKind;

        // Backward compatibility for an old, individually-owned Switch folder. Never infer
        // ownership for a title root or for shared disc/N64 roots.
        if (StoreGame.FoldSystem(record.System) == "switch" &&
            !string.IsNullOrWhiteSpace(record.TitleId) &&
            !string.IsNullOrWhiteSpace(record.RemotePath))
        {
            var remote = NormalizeRemotePath(record.RemotePath);
            var leaf = Path.GetFileName(remote);
            var parentLeaf = Path.GetFileName(DeckClient.Parent(remote));
            if (!leaf.Equals(record.TitleId, StringComparison.OrdinalIgnoreCase) &&
                parentLeaf.Equals(record.TitleId, StringComparison.OrdinalIgnoreCase))
                return PackOwnershipKind.IsolatedDirectory;
        }

        return PackOwnershipKind.Unknown;
    }

    public void Remove(PackHit hit)
    {
        lock (_gate)
        {
            var rec = FindLocked(hit);
            if (rec is null) return;
            _items.Remove(rec);
            SaveLocked();
        }
    }

    public void DeleteLocalFiles(PackHit hit)
    {
        var rec = Find(hit);
        TryDeleteFile(rec?.LocalFile);
        TryDeleteFile(hit.LocalFile);
        try
        {
            var dir = CacheDir(hit);
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch
        {
            // cache opruimen is optioneel
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { File.Delete(path); }
        catch { /* lokaal bestand kan in gebruik zijn */ }
    }

    public void MarkInstalledFolders(IEnumerable<PackHit> hits, string? titleId, IReadOnlyList<string> folders)
    {
        var names = folders.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        foreach (var hit in hits)
        {
            if (hit.IsBusy || hit.IsQueued) continue;
            var rec = Find(hit);
            if (!BelongsToTitle(rec, hit, titleId)) continue;
            var match = MatchFolder(names, hit, rec);
            if (match is not null)
            {
                var remote = rec?.RemotePath;
                if (string.IsNullOrWhiteSpace(remote) ||
                    !Path.GetFileName(remote.TrimEnd('/')).Equals(match, StringComparison.OrdinalIgnoreCase))
                    remote = RemoteForFolder(hit.TargetPath, match);
                hit.SetInstalled(remote, rec?.LocalFile ?? hit.LocalFile);
                hit.SetEnabled(!SwitchModFolders.IsDisabled(match));
                continue;
            }

            if (rec is { HasInstall: true } && rec.HasLocalFile)
                hit.SetDownloaded(rec.LocalFile!);
        }
    }

    private static bool BelongsToTitle(ModRecord? rec, PackHit hit, string? titleId)
    {
        if (string.IsNullOrEmpty(titleId)) return true;
        if (rec?.TitleId is string id)
            return id.Equals(titleId, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(hit.TargetPath))
            return hit.TargetPath.Contains(titleId, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static string? MatchFolder(List<string> names, PackHit hit, ModRecord? rec)
    {
        if (rec is not null && !string.IsNullOrWhiteSpace(rec.ModFolderName))
        {
            var recorded = names.FirstOrDefault(n =>
                SwitchModFolders.BaseName(n).Equals(rec.ModFolderName, StringComparison.OrdinalIgnoreCase));
            if (recorded is not null) return recorded;
        }

        return names.FirstOrDefault(n => FolderMatches(SwitchModFolders.BaseName(n), hit));
    }

    private static string RemoteForFolder(string? targetPath, string folder)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return folder;
        var trimmed = targetPath.TrimEnd('/');
        var parent = DeckClient.Parent(trimmed);
        var leaf = Path.GetFileName(trimmed.Replace('\\', '/'));
        if (leaf.Equals(folder, StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (LooksLikeTitleIdLeaf(leaf))
            return DeckClient.Combine(trimmed, folder);
        return DeckClient.Combine(parent, folder);
    }

    private static bool LooksLikeTitleIdLeaf(string name) =>
        name.Length == 16 && name.StartsWith("01", StringComparison.OrdinalIgnoreCase);

    public static bool MatchesGame(ModRecord rec, StoreGame game)
    {
        if (game.IsAll) return true;
        if (!string.IsNullOrEmpty(game.TitleId) &&
            string.Equals(rec.TitleId, game.TitleId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(rec.System) && !game.MatchesSystem(rec.System))
            return false;
        return game.MatchesTitle(rec.GameName) || game.MatchesTitle(rec.Title);
    }

    public static bool FolderMatches(string folderName, PackHit hit)
    {
        var folder = StoreGame.Slug(folderName);
        var title = StoreGame.Slug(hit.Title);
        if (folder.Length == 0 || title.Length == 0) return false;
        if (folder.Equals(title, StringComparison.OrdinalIgnoreCase)) return true;
        return folder.Length >= 10 && title.Length >= 10 &&
               (folder.Contains(title, StringComparison.OrdinalIgnoreCase) ||
                title.Contains(folder, StringComparison.OrdinalIgnoreCase));
    }

    private ModRecord? FindLocked(PackHit hit)
    {
        var key = StoreResultCache.HitKey(hit);
        return _items.FirstOrDefault(r =>
                   string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))
               ?? _items.FirstOrDefault(r =>
                   !string.IsNullOrEmpty(hit.ItemId) &&
                   string.Equals(r.ItemId, hit.ItemId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(r.Source, hit.Source, StringComparison.OrdinalIgnoreCase))
               ?? _items.FirstOrDefault(r =>
                   string.Equals(r.Title, hit.Title, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(r.Source, hit.Source, StringComparison.OrdinalIgnoreCase));
    }

    private ModRecord UpsertLocked(PackHit hit, StoreGame game, string? titleId)
    {
        var rec = FindLocked(hit);
        if (rec is null)
        {
            rec = new ModRecord { Key = StoreResultCache.HitKey(hit) };
            _items.Add(rec);
        }

        rec.Title = hit.Title;
        rec.Source = hit.Source;
        rec.Kind = hit.Kind;
        rec.Author = hit.Author;
        rec.ItemId = hit.ItemId;
        rec.PageUrl = hit.PageUrl;
        rec.GameName = !string.IsNullOrWhiteSpace(hit.GameName) ? hit.GameName
            : game.IsAll ? rec.GameName : game.Name;
        rec.System = !string.IsNullOrWhiteSpace(game.System) ? game.System : hit.Platform;
        rec.TitleId = titleId ?? game.TitleId ?? rec.TitleId;
        rec.GameId = hit.GameId?.Value ?? game.GameId?.Value ?? rec.GameId;
        rec.FileName = hit.FileName ?? rec.FileName;
        return rec;
    }

    private void SaveLocked()
    {
        try
        {
            var path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_items, Json));
        }
        catch
        {
            // lokale status is optioneel
        }
    }

    private static string FilePath() => Path.Combine(RootDir(), "mod-library.json");

    private static string NormalizeRemotePath(string path) =>
        (path ?? "").Trim().Replace('\\', '/').TrimEnd('/');

    private static string RootDir()
    {
        AppDataPaths.EnsureProtected();
        return AppDataPaths.Root;
    }

    private static string SafeKey(string key)
    {
        var slug = StoreGame.Slug(key);
        if (slug.Length > 40) slug = slug[..40];
        return string.IsNullOrEmpty(slug) ? "mod" : slug;
    }
}
