using System.IO;
using System.Security.Cryptography;
using System.Text;
using Sesame.Models;

namespace Sesame.Services;

public sealed class RomHackInstaller
{
    public string Install(PackHit hit, string patchOrZip, DeckClient client, AppCatalog catalog,
        Action<string>? progress = null)
    {
        if (!client.IsConnected)
            throw new InvalidOperationException("Eerst verbinden met de Steam Deck.");

        var patch = PackStore.FindPatchFile(patchOrZip)
                    ?? throw new InvalidOperationException(
                        "No .bps/.ips/.ups patch found. SESAME downloads the patch only, never a full ROM.");

        var system = PackStore.FoldRomFolderKey(PackStore.ResolveSystem(hit, catalog));
        var romFolder = catalog.RomFolderFor(system);
        if (string.IsNullOrEmpty(system) || string.IsNullOrEmpty(romFolder))
            throw new InvalidOperationException(
                "No ROM folder known for this system. In the Store pick the N64, NES or SNES game.");

        progress?.Invoke("Basis-ROM zoeken op de Deck…");
        var match = FindBaseRom(client, romFolder, hit);
        if (match is null)
            throw new InvalidOperationException(MissingDumpMessage(hit, romFolder));

        var temp = Path.Combine(Path.GetTempPath(), "SESAME", "romhack", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var localBase = Path.Combine(temp, match.Name);
        progress?.Invoke("Copying your dump (original stays untouched)…");
        client.DownloadFile(match.FullPath, localBase);

        progress?.Invoke("ROM uit archief lezen…");
        var source = RomContainer.ReadRom(localBase, hit.OriginalGame ?? hit.GameName);
        progress?.Invoke("Patch toepassen…");
        var patched = RomPatcher.ApplyWithHeaderVariants(source, patch, match.Name);
        if (!string.IsNullOrWhiteSpace(hit.OutputSha1))
        {
            var sha = Sha1(patched);
            if (!sha.Equals(hit.OutputSha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The patched ROM has SHA-1 {sha}, expected {hit.OutputSha1}. The original was not overwritten.");
        }

        var (localOut, newName) = WritePatchedRom(temp, patched, match.Name, hit.Title,
            hit.OriginalGame ?? hit.GameName, system, hit.Title);
        progress?.Invoke("Nieuwe ROM uploaden…");
        client.EnsureDirectory(romFolder);
        var remote = DeckClient.Combine(romFolder, newName);
        if (client.Exists(remote))
        {
            var stamped = Path.GetFileNameWithoutExtension(newName) + " " + DateTime.Now.ToString("HHmmss")
                          + Path.GetExtension(newName);
            localOut = Path.Combine(temp, stamped);
            RomContainer.WriteOutput(localOut, patched, RomContainer.InnerRomFileName(
                hit.RequiredRomName, match.Name, patched));
            newName = stamped;
            remote = DeckClient.Combine(romFolder, newName);
        }
        client.UploadFile(localOut, romFolder, progress);
        RomHackLog.Remember(remote, hit.Title, match.Name);
        return remote;
    }

    public string InstallFromGame(GameEntry game, string patchOrZip, DeckClient client,
        Action<string>? progress = null)
    {
        if (!client.IsConnected)
            throw new InvalidOperationException("Eerst verbinden met de Steam Deck.");
        if (string.IsNullOrWhiteSpace(game.RomPath))
            throw new InvalidOperationException(
                "This game has no ROM file. Only your own legal dump can be patched.");

        var patch = PackStore.FindPatchFile(patchOrZip)
                    ?? throw new InvalidOperationException(
                        "No .bps/.ips/.ups patch found in that file.");

        var originalName = string.IsNullOrWhiteSpace(game.FileName)
            ? Path.GetFileName(game.RomPath)
            : game.FileName;
        var romFolder = DeckClient.Parent(game.RomPath);
        var temp = Path.Combine(Path.GetTempPath(), "SESAME", "romhack", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var localBase = Path.Combine(temp, originalName);
        progress?.Invoke("Copying your dump (original stays untouched)…");
        client.DownloadFile(game.RomPath, localBase);

        progress?.Invoke("ROM uit archief lezen…");
        var source = RomContainer.ReadRom(localBase, game.InnerFileName ?? game.DisplayName);
        progress?.Invoke("Patch toepassen…");
        var patched = RomPatcher.ApplyWithHeaderVariants(source, patch, originalName);

        var hackTitle = Path.GetFileNameWithoutExtension(patch);
        var (localOut, newName) = WritePatchedRom(temp, patched, originalName, hackTitle,
            game.InnerFileName ?? game.DisplayName, game.System, hackTitle);
        if (string.Equals(newName, originalName, StringComparison.OrdinalIgnoreCase))
        {
            (localOut, newName) = WritePatchedRom(temp, patched,
                Path.GetFileNameWithoutExtension(originalName) + " (ROM-hack)" + Path.GetExtension(originalName),
                hackTitle, game.InnerFileName ?? game.DisplayName, game.System, hackTitle);
        }

        progress?.Invoke("Nieuwe ROM uploaden…");
        client.EnsureDirectory(romFolder);
        var remote = DeckClient.Combine(romFolder, newName);
        if (client.Exists(remote) || string.Equals(remote, game.RomPath, StringComparison.OrdinalIgnoreCase))
        {
            var stamped = Path.GetFileNameWithoutExtension(newName) + " " + DateTime.Now.ToString("HHmmss")
                          + Path.GetExtension(newName);
            localOut = Path.Combine(temp, stamped);
            RomContainer.WriteOutput(localOut, patched,
                RomContainer.InnerRomFileName(game.InnerFileName, originalName, patched));
            newName = stamped;
            remote = DeckClient.Combine(romFolder, newName);
        }
        if (string.Equals(remote, game.RomPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The new ROM would overwrite the original. Aborted.");

        client.UploadFile(localOut, romFolder, progress);
        RomHackLog.Remember(remote, hackTitle, originalName);
        return remote;
    }

    public static string MissingDumpMessage(PackHit hit, string romFolder)
    {
        var name = hit.RequiredRomName ?? hit.OriginalGame ?? hit.GameName;
        var hashes = new List<string>();
        if (!string.IsNullOrWhiteSpace(hit.FileSha1)) hashes.Add("File SHA-1 " + hit.FileSha1);
        if (!string.IsNullOrWhiteSpace(hit.RomSha1)) hashes.Add("ROM SHA-1 " + hit.RomSha1);
        if (!string.IsNullOrWhiteSpace(hit.FileCrc32)) hashes.Add("CRC32 " + hit.FileCrc32);
        var hashText = hashes.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, hashes);
        return
            "No matching original dump found in " + romFolder + "." + Environment.NewLine + Environment.NewLine +
            "You must make a legal dump of an original cartridge yourself and put that ROM in that folder." +
            Environment.NewLine + "SESAME levert geen auteursrechtelijk beschermde ROMs." +
            Environment.NewLine + Environment.NewLine +
            "Verwachte basis: " + (string.IsNullOrWhiteSpace(name) ? "(zie de hack-pagina)" : name) +
            hashText;
    }

    private static RemoteItem? FindBaseRom(DeckClient client, string folder, PackHit hit)
    {
        if (!client.Exists(folder)) return null;
        var items = client.List(folder).Where(i => !i.IsDirectory).ToList();
        if (items.Count == 0) return null;

        var wantHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(hit.FileSha1)) wantHashes.Add(hit.FileSha1);
        if (!string.IsNullOrWhiteSpace(hit.RomSha1)) wantHashes.Add(hit.RomSha1);
        if (!string.IsNullOrWhiteSpace(hit.FileCrc32)) wantHashes.Add(hit.FileCrc32);

        var named = items.Where(i => NameLooksLikeBase(i.Name, hit)).ToList();
        var candidates = named.Count > 0 ? named : items;

        foreach (var item in candidates)
        {
            if (item.Size > 512L * 1024 * 1024) continue;
            byte[] data;
            try { data = client.ReadBytes(item.FullPath); }
            catch { continue; }

            if (wantHashes.Count > 0)
            {
                byte[] rom;
                try { rom = RomContainer.ReadRomFromBytes(data, hit.OriginalGame ?? hit.GameName); }
                catch { rom = data; }
                var fileSha = Sha1(rom);
                var fileCrc = RomPatcher.Crc32(rom).ToString("X8");
                if (wantHashes.Contains(fileSha) || wantHashes.Contains(fileCrc))
                    return item;
                var stripped = RomPatcher.WithoutHeader(rom);
                if (stripped is not null && wantHashes.Contains(Sha1(stripped)))
                    return item;
                if (!ReferenceEquals(rom, data) && wantHashes.Contains(Sha1(data)))
                    return item;
            }
        }

        return named.Count == 1 ? named[0] : named.FirstOrDefault();
    }

    private static bool NameLooksLikeBase(string fileName, PackHit hit)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (hit.IsRomHack && !string.IsNullOrWhiteSpace(hit.Title) &&
            stem.Contains(hit.Title, StringComparison.OrdinalIgnoreCase))
            return false;
        if (StoreGame.LooksLikeTranslation(fileName) &&
            !StoreGame.LooksLikeTranslation(hit.OriginalGame) &&
            !StoreGame.LooksLikeTranslation(hit.RequiredRomName) &&
            !StoreGame.LooksLikeTranslation(hit.GameName))
            return false;
        var probes = new[] { hit.RequiredRomName, hit.OriginalGame, hit.GameName }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        foreach (var probe in probes)
        {
            if (StoreGame.ContainsPhrase(stem, probe!) || StoreGame.FoldTitle(stem) == StoreGame.FoldTitle(probe!))
                return true;
            var core = StoreGame.FoldTitle(probe!).Replace("legend of ", "");
            if (core.Length >= 5 && StoreGame.FoldTitle(stem).Contains(core, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static (string Path, string Name) WritePatchedRom(string temp, byte[] patched,
        string originalName, string hackTitle, string? innerHint, string system, string fallbackTitle)
    {
        var zip = RomContainer.PreferZipOutput(originalName, system);
        var inner = RomContainer.InnerRomFileName(innerHint, originalName, patched);
        var innerExt = Path.GetExtension(inner);
        if (string.IsNullOrEmpty(innerExt))
            innerExt = RomContainer.RomExtension(patched, originalName);
        var stem = Sanitize(Path.GetFileNameWithoutExtension(originalName));
        var hack = Sanitize(hackTitle);
        if (string.IsNullOrWhiteSpace(hack))
            hack = Sanitize(fallbackTitle);
        var baseName = stem.Length > 0 && !stem.Equals(hack, StringComparison.OrdinalIgnoreCase)
            ? $"{stem} - {hack}"
            : hack;
        if (baseName.Length > 100)
            baseName = hack;
        var fileName = baseName + (zip ? ".zip" : innerExt);
        var innerName = baseName + innerExt;
        var local = Path.Combine(temp, fileName);
        RomContainer.WriteOutput(local, patched, innerName);
        return (local, fileName);
    }

    private static string Sha1(byte[] data)
    {
        var hash = SHA1.HashData(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, ' ');
        name = string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return name.Trim();
    }
}
