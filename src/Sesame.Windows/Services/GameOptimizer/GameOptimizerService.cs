using System.IO;
using System.Text.RegularExpressions;
using Sesame.Models;
using Sesame.Services;

namespace Sesame.Services.GameOptimizer;

public static class GameOptimizerService
{
    private static readonly Regex TitleId = new(@"0[1-9A-Fa-f][0-9A-Fa-f]{14}", RegexOptions.Compiled);

    public static List<OptimizerGame> Scan(DeckClient client, AppCatalog catalog,
        IProgress<OptimizeProgress>? progress = null)
    {
        progress?.Report(new OptimizeProgress
        {
            Title = "Scanning ROMs",
            Detail = "Reading emulators and Steam index…",
            Indeterminate = true
        });
        var layout = EmulatorProbe.Probe(client);
        var steam = LoadSteamIndex(client);
        progress?.Report(new OptimizeProgress
        {
            Title = "Scanning ROMs",
            Detail = "Looking for ROM files…",
            Indeterminate = true
        });
        var files = ListRoms(client, catalog);
        progress?.Report(new OptimizeProgress
        {
            Title = "Scanning ROMs",
            Detail = "Grouping " + files.Count + " files…",
            Indeterminate = true
        });
        var games = new List<OptimizerGame>();

        foreach (var group in files.GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var chosen = group.OrderBy(f => RomNameCleaner.Rank(Path.GetExtension(f.FileName))).First();
            var profile = SystemCatalog.Resolve(chosen.SystemFolder, chosen.FileName);
            if (profile.Extensions.Count > 0 &&
                !profile.Extensions.Contains(Path.GetExtension(chosen.FileName), StringComparer.OrdinalIgnoreCase))
                continue;

            var display = DisplayName(chosen, catalog, profile, out var romHack, out var translation);
            var game = new OptimizerGame
            {
                DisplayName = display,
                FileName = chosen.FileName,
                RomPath = chosen.FullPath,
                FolderName = chosen.SystemFolder,
                SystemId = profile.Id,
                SystemName = profile.Name,
                Category = profile.Category,
                Fps = profile.Fps,
                SearchQuery = display + " " + profile.Name,
                IsRomHack = romHack,
                IsTranslation = translation
            };
            BindEmulator(game, profile, layout);
            OptimizerPicks.Apply(game);
            if (!game.LaunchLocked)
                BindEmulator(game, profile, layout);
            var existing = SteamShortcuts.FindOwnedByRom(steam, chosen.FullPath)
                           ?? SteamShortcuts.FindByRom(steam, chosen.FullPath);
            if (existing is not null)
            {
                game.InSteam = SteamShortcuts.IsOwned(existing);
                game.SteamAppId = game.InSteam ? existing.AppId : 0;
                game.Status = !SteamShortcuts.IsOwned(existing)
                    ? "In Steam (extern)"
                    : LaunchComposer.IsLegacyScript(existing.Exe, existing.LaunchOptions)
                        ? "In Steam (oud script)"
                        : "In Steam";
            }
            else
                game.Status = string.IsNullOrEmpty(game.Target) ? "No emulator" : "New";
            if (string.IsNullOrEmpty(game.Target))
                game.Note = "No launcher found for " + profile.Name;
            SteamGridArt.Attach(client, game);
            games.Add(game);
        }

        progress?.Report(new OptimizeProgress
        {
            Title = "Hydra and apps",
            Detail = "Looking for Hydra games and native apps…",
            Indeterminate = true
        });
        var extraProgress = progress is null
            ? null
            : new Progress<string>(detail => progress.Report(new OptimizeProgress
            {
                Title = "Hydra and apps",
                Detail = detail,
                Indeterminate = true
            }));
        foreach (var extra in ExtraShortcuts.Scan(client, steam, ExtraScanMode.All, extraProgress))
        {
            var existing = ResolveExistingShortcut(steam, extra);
            if (existing is not null)
            {
                extra.InSteam = SteamShortcuts.IsOwned(existing);
                extra.SteamAppId = extra.InSteam ? existing.AppId : extra.SteamAppId;
                extra.Status = extra.InSteam ? "In Steam" : "In Steam (extern)";
            }
            else if (string.IsNullOrEmpty(extra.Status) || extra.Status == "New")
                extra.Status = extra.IsManual ? "Manual" :
                    extra.LaunchChoices.Count > 1 ? "Multiple launches — pick one" : "New";
            SteamGridArt.Attach(client, extra);
            games.Add(extra);
        }

        return games
            .OrderBy(g => g.SystemName)
            .ThenBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<OptimizerGame> ScanNativeApps(DeckClient client,
        IProgress<string>? progress = null)
    {
        progress?.Report("Reading Steam shortcuts…");
        var steam = LoadSteamIndex(client);
        var apps = ExtraShortcuts.Scan(client, steam, ExtraScanMode.Apps, progress)
            .Where(g => g.ShortcutKind == ShortcutKind.App)
            .ToList();
        foreach (var extra in apps)
        {
            var existing = ResolveExistingShortcut(steam, extra);
            if (existing is not null)
            {
                extra.InSteam = SteamShortcuts.IsOwned(existing);
                extra.SteamAppId = extra.InSteam ? existing.AppId : extra.SteamAppId;
                extra.Status = extra.InSteam ? "In Steam" : "In Steam (external)";
            }
            else if (string.IsNullOrEmpty(extra.Status) || extra.Status == "New")
                extra.Status = extra.IsManual ? "Manual" :
                    extra.LaunchChoices.Count > 1 ? "Multiple launches — pick one" : "New";
            SteamGridArt.Attach(client, extra);
        }

        return apps;
    }

    private static SteamShortcut? ResolveExistingShortcut(IReadOnlyList<SteamShortcut> steam,
        OptimizerGame extra)
    {
        if (extra.ShortcutKind == ShortcutKind.App)
            return SteamShortcuts.FindOwnedApp(steam, extra) ?? SteamShortcuts.FindApp(steam, extra);
        if (extra.SteamAppId != 0)
        {
            var byId = steam.FirstOrDefault(s => s.AppId == extra.SteamAppId);
            if (byId is not null) return byId;
        }
        return SteamShortcuts.FindOwnedByRom(steam, extra.RomPath)
               ?? SteamShortcuts.FindByRom(steam, extra.RomPath);
    }

    public static async Task<OptimizeReport> ApplyAsync(DeckClient client, AppCatalog catalog,
        IReadOnlyList<OptimizerGame> games, IProgress<OptimizeProgress>? progress, CancellationToken ct)
    {
        var report = new OptimizeReport();
        var selected = games.Where(g => g.Selected && !g.OptimizeLocked).ToList();
        if (selected.Count == 0)
        {
            report.Summary = "No games selected.";
            return report;
        }

        Report(progress, "Prepare Steam",
            "Steam must pause briefly so shortcut files can be written…", 2, indeterminate: true);

        var restoreGameMode = SteamSession.PrepareForWrite(client, SteamProgress(progress, 5));

        try
        {
        Report(progress, "Resolve emulators", "Pick the right launcher per system…", 10);

        var layout = EmulatorProbe.Probe(client);
        var wrapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in selected)
        {
            if (!game.IsRom) continue;
            var profile = SystemCatalog.FromFolder(game.FolderName)
                          ?? SystemCatalog.All.FirstOrDefault(p => p.Id == game.SystemId)
                          ?? SystemCatalog.Unknown(game.FolderName);
            if (profile is null) continue;
            BindEmulator(game, profile, layout);
            var cfg = LaunchConfigStore.ForSystem(profile.Id);
            if (LaunchComposer.NeedsWrapper(cfg) && wrapped.Add(game.SystemId))
            {
                Report(progress, "Write wrappers",
                    "Wrapper voor " + profile.Name + " installeren…", 12);
                EmulatorProbe.InstallWrapper(client, game.SystemId, game.CorePath);
            }
        }
        if (selected.Any(DolphinInput.IsBound))
        {
            Report(progress, "Dolphin controls",
                "Controllerkoppeling voor GameCube/Wii instellen…", 13);
            DolphinInput.Ensure(client);
        }
        selected = selected.Where(g => !string.IsNullOrEmpty(g.Target)).ToList();
        if (selected.Count == 0)
        {
            report.Summary = "No games with an emulator selected.";
            return report;
        }

        var configs = SteamShortcuts.FindUserConfigs(client);
        if (configs.Count == 0)
            throw new InvalidOperationException("No Steam user folder found on the Deck.");

        var primary = configs[0];
        var shortcuts = SteamShortcuts.LoadAll(client, configs);
        var written = new List<SteamShortcut>();
        var gridDir = DeckClient.Combine(primary, "grid");
        client.EnsureDirectory(gridDir);

        var n = selected.Count;
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var game = selected[i];
            var pct = 12 + 75.0 * i / n;
            Report(progress, "Write shortcut", game.DisplayName, pct, i + 1, n);
            try
            {
                RepairPcGameLaunch(game);
                EnsureExecutable(client, LaunchComposer.ExePath(game.Target));
                var shortcut = SteamShortcuts.Build(game);
                SteamShortcuts.Upsert(shortcuts, shortcut, overwrite: OptimizerSettings.OverwriteShortcuts);
                // Upsert may keep a prior AppId so Proton/artwork stay linked.
                game.SteamAppId = shortcut.AppId;
                written.Add(shortcut);
                game.Note = string.IsNullOrWhiteSpace(game.Note) ? game.Target : game.Note;

                var profile = SystemCatalog.FromFolder(game.FolderName)
                    ?? SystemCatalog.All.FirstOrDefault(p => p.Id == game.SystemId)
                    ?? SystemCatalog.Unknown(game.FolderName);
                var query = ArtworkClient.ArtworkSearchQuery(game);
                ArtworkSet? art = null;
                var keptExisting = !OptimizerSettings.OverwriteArtwork &&
                                   ArtworkAlreadyOnDeck(client, gridDir, game.SteamAppId);
                if (keptExisting)
                {
                    Report(progress, "Keep artwork", game.DisplayName,
                        12 + 75.0 * (i + 0.75) / n, i + 1, n);
                    report.ArtworkKept++;
                    game.HasArtwork = true;
                    if (string.IsNullOrEmpty(game.ArtworkSource) || game.ArtworkSource == "—")
                        game.ArtworkSource = "Steam (kept)";
                }
                else
                {
                Report(progress, "Fetch cover", game.DisplayName,
                    12 + 75.0 * (i + 0.4) / n, i + 1, n);
                if (!string.IsNullOrEmpty(game.SelectedGridUrl) || game.GridBytes is { Length: > 0 })
                {
                    art = new ArtworkSet
                    {
                        Source = "SteamGridDB",
                        GameId = game.SteamGridDbId,
                        GridUrl = game.SelectedGridUrl,
                        WideUrl = game.SelectedWideUrl ?? game.SelectedGridUrl,
                        HeroUrl = game.SelectedHeroUrl,
                        LogoUrl = game.SelectedLogoUrl,
                        IconUrl = game.SelectedIconUrl,
                        Grid = game.GridBytes,
                        Wide = game.WideBytes,
                        Hero = game.HeroBytes,
                        Logo = game.LogoBytes,
                        Icon = game.IconBytes
                    };
                    await ArtworkClient.EnsureExtraAssetsAsync(art, game.SteamGridDbId, ct);
                    await ArtworkClient.FillBytesAsync(art, ct);
                }
                else
                {
                    art = await ArtworkClient.FindAsync(query, profile, ct);
                    if (art is not null)
                    {
                        await ArtworkClient.EnsureExtraAssetsAsync(art, art.GameId, ct);
                        await ArtworkClient.FillBytesAsync(art, ct);
                        game.SelectedGridUrl ??= art.GridUrl;
                        game.SelectedWideUrl ??= art.WideUrl;
                        game.SelectedHeroUrl ??= art.HeroUrl;
                        game.SelectedLogoUrl ??= art.LogoUrl;
                        game.SelectedIconUrl ??= art.IconUrl;
                    }
                }
                Report(progress, "Apply artwork", game.DisplayName,
                    12 + 75.0 * (i + 0.75) / n, i + 1, n);
                if (art?.GameId is int gid) game.SteamGridDbId = gid;
                var artWrite = WriteArtwork(client, gridDir, game, profile, art);
                report.ArtworkWritten += artWrite.Written;
                report.ArtworkKept += artWrite.Skipped;
                var masked = OptimizerSettings.UseMaskFor(profile.Id);
                game.HasArtwork = art?.Grid is { Length: > 0 } || art?.Wide is { Length: > 0 } || masked;
                var src = art?.Source ?? "";
                game.ArtworkSource = string.IsNullOrEmpty(src)
                    ? (masked ? "Categoriemask" : "ontbreekt")
                    : masked ? src + " + mask" : src;
                if (!string.IsNullOrEmpty(ArtworkClient.LastError) && art is null)
                    game.Note = ArtworkClient.LastError;
                game.CoverUrl = art?.GridUrl;
                }

                shortcut.Icon = DeckClient.Combine(gridDir, game.SteamAppId + "_icon.png");

                SteamPerf.Apply(client, game.SteamAppId, game.Fps, game.Fps >= 60 ? 60 : game.Fps);
                if (game.IsRom)
                    SteamPerf.WriteRetroArchCfg(client, game);
                game.InSteam = true;
                game.Status = "Geoptimaliseerd";
                OptimizerPicks.Remember(game);
                report.Applied++;
            }
            catch (Exception ex)
            {
                game.Status = "Mislukt";
                game.Note = ex.Message;
                report.Failed++;
                report.Errors.Add(game.DisplayName + ": " + ex.Message);
            }
        }

        var stripped = SteamShortcuts.RemoveLegacyFor(shortcuts, written);
        Report(progress, "Save shortcuts", "Writing Steam shortcuts to the Deck…", 90);
        foreach (var config in configs)
            SteamShortcuts.Save(client, config, shortcuts);
        try { SteamSelfShortcut.Ensure(client); }
        catch { /* Game Mode SESAME tile is extra */ }
        shortcuts = SteamShortcuts.LoadAll(client, configs);

        var leftover = shortcuts.Count(s => SteamShortcuts.IsOwned(s) &&
                                            LaunchComposer.IsLegacyScript(s.Exe, s.LaunchOptions) &&
                                            written.Any(w => SteamShortcuts.MentionsRom(s, w.RomPath) ||
                                                             string.Equals(s.AppName, w.AppName, StringComparison.OrdinalIgnoreCase)));
        if (leftover > 0)
            report.Errors.Add(leftover + " oude script-shortcuts konden niet worden vervangen.");

        // Wii Joy-Cons need Steam Input Off so Steam does not steal pads from cemuhook DSU.
        // GC uses the same Off setting for consistency (native HID / no virtual gamepad).
        var dolphinIds = written
            .Where(s => DolphinInput.IsBound(s.Exe) || DolphinInput.IsBound(s.LaunchOptions))
            .Select(s => s.AppId)
            .ToList();
        if (dolphinIds.Count > 0)
        {
            Report(progress, "Steam Input", "Disable Steam Input for Dolphin (Joy-Con DSU)…", 92);
            SteamInputConfig.ForceOff(client, configs, dolphinIds);
        }

        Report(progress, "Update collections", "Set Steam tabs…", 94);
        var inSteam = games.Where(g => g.SteamAppId != 0).ToList();
        var collectionError = SteamCollections.Apply(client, configs, inSteam, shortcuts);
        if (!string.IsNullOrEmpty(collectionError))
            report.Errors.Add("Collecties: " + collectionError);

        string? protonTool = null;
        var protonGames = written
            .Select(s =>
            {
                var g = selected.FirstOrDefault(x => x.SteamAppId == s.AppId) ??
                        selected.FirstOrDefault(x =>
                            string.Equals(x.DisplayName, s.AppName, StringComparison.OrdinalIgnoreCase));
                if (g is null) return null;
                g.SteamAppId = s.AppId;
                return g;
            })
            .Where(g => g is not null)
            .Cast<OptimizerGame>()
            .Where(SteamCompat.NeedsProton)
            .ToList();
        if (protonGames.Count > 0)
        {
            Report(progress, "UMU Proton", "Force UMU / Proton for Windows games…", 95);
            protonTool = SteamCompat.Apply(client, protonGames);
            if (string.IsNullOrEmpty(protonTool))
                report.Errors.Add("UMU/Proton could not be set — enable it once in Steam for a Hydra game, then re-Optimize.");
        }

        if (configs.Count > 1)
        {
            foreach (var extra in configs.Skip(1))
            {
                var extraGrid = DeckClient.Combine(extra, "grid");
                client.EnsureDirectory(extraGrid);
                try
                {
                    client.Execute(
                        "cp -f " + DeckClient.ShQuote(gridDir) + "/* " + DeckClient.ShQuote(extraGrid) + " 2>/dev/null || true",
                        20);
                }
                catch { /* extra account is optioneel */ }
            }
        }

        report.Skipped = games.Count - selected.Count;
        report.Summary = $"{report.Applied} optimized" +
                         (report.Failed > 0 ? $", {report.Failed} failed" : "") +
                         (report.ArtworkKept > 0 ? $", {report.ArtworkKept} covers ongewijzigd overgeslagen" : "") +
                         (stripped > 0 ? $", {stripped} oude scripts vervangen" : "") +
                         (string.IsNullOrEmpty(collectionError) ? "" : ", collections partly failed") +
                         (dolphinIds.Count > 0
                             ? ". Wii Joy-Con: L+R to pair · Left off + SL+SR for solo Wiimote. See the Optimize hint."
                             : "") +
                         (string.IsNullOrEmpty(protonTool) ? "" : $". Proton: {protonTool}") +
                         (restoreGameMode
                             ? " Game Mode wordt weer gestart."
                             : " Open daarna Game Mode opnieuw.");

        return report;
        }
        finally
        {
            if (restoreGameMode)
            {
                Report(progress, "Restore Game Mode", "Starting Steam Game Mode again…", 98);
                SteamSession.RestoreGameMode(client, true, SteamProgress(progress, 98));
            }
            Report(progress, "Done", report.Summary, 100);
        }
    }

    private static void BindEmulator(OptimizerGame game, SystemProfile profile, EmulatorLayout layout) =>
        LaunchComposer.Bind(game, profile, layout);

    /// <summary>
    /// Hydra/Lutris paths often contain spaces; rebuild Target/StartDir from RomPath so
    /// Steam gets "/home/deck/Hydra/Black Jacket/game.exe" with StartDir = that folder.
    /// </summary>
    private static void RepairPcGameLaunch(OptimizerGame game)
    {
        if (game.ShortcutKind is not (ShortcutKind.Hydra or ShortcutKind.Game)) return;
        var exe = (game.RomPath ?? "").Trim().Trim('"').Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(exe) ||
            !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exe = LaunchComposer.ExePath(game.Target);
        if (string.IsNullOrWhiteSpace(exe)) return;
        exe = exe.Trim().Trim('"').Replace('\\', '/');
        var start = DeckClient.Parent(exe);
        var steam = LaunchComposer.ForSteam(exe, start, "");
        game.RomPath = exe;
        game.Target = steam.Exe;
        game.StartDir = steam.StartDir;
        game.LaunchOptions = steam.LaunchOptions;
        game.FileName = Path.GetFileName(exe);
    }

    private static bool ArtworkAlreadyOnDeck(DeckClient client, string gridDir, uint appId)
    {
        if (appId == 0) return false;
        var id = appId.ToString();
        var hasPortrait = false;
        foreach (var name in new[] { id + "p.png", id + "_p.png" })
        {
            try
            {
                if (client.FileLength(DeckClient.Combine(gridDir, name)) > 200)
                {
                    hasPortrait = true;
                    break;
                }
            }
            catch
            {
                /* try next */
            }
        }
        if (!hasPortrait) return false;
        // Missing hero is why Hydra capsules look blown-up — rewrite until hero exists.
        try
        {
            return client.FileLength(DeckClient.Combine(gridDir, id + "_hero.png")) > 200;
        }
        catch
        {
            return false;
        }
    }

    private static (int Written, int Skipped) WriteArtwork(DeckClient client, string gridDir, OptimizerGame game,
        SystemProfile profile, ArtworkSet? art)
    {
        var id = game.SteamAppId.ToString();
        // Portrait capsule must stay a vertical cover — never force a wide banner into it.
        // Unmasked platforms letterbox (contain) so titles are not cropped off.
        var portraitSrc = art?.Grid;
        var landscapeSrc = art?.Wide ?? art?.Hero ?? art?.Grid;
        byte[] portrait;
        byte[] landscape;
        if (!OptimizerSettings.UseMaskFor(profile.Id))
        {
            portrait = CoverMask.FitOnlyPublic(portraitSrc, CoverMask.PortraitWidth, CoverMask.PortraitHeight);
            landscape = CoverMask.FitOnlyPublic(landscapeSrc, CoverMask.LandscapeWidth, CoverMask.LandscapeHeight);
        }
        else
        {
            portrait = CoverMask.Portrait(portraitSrc, profile, game.IsRomHack, game.IsTranslation);
            landscape = CoverMask.Landscape(landscapeSrc, profile, game.IsRomHack, game.IsTranslation);
        }
        var files = new List<(string Path, byte[] Data)>
        {
            (DeckClient.Combine(gridDir, id + "p.png"), portrait),
            (DeckClient.Combine(gridDir, id + "_p.png"), portrait),
            (DeckClient.Combine(gridDir, id + ".png"), landscape)
        };
        if (art?.Hero is { Length: > 0 })
            files.Add((DeckClient.Combine(gridDir, id + "_hero.png"), art.Hero));
        else if (landscape is { Length: > 0 })
            // Steam zooms the capsule when hero is missing — reuse the fitted landscape.
            files.Add((DeckClient.Combine(gridDir, id + "_hero.png"), landscape));
        if (art?.Logo is { Length: > 0 })
            files.Add((DeckClient.Combine(gridDir, id + "_logo.png"), art.Logo));
        var icon = art?.Icon ?? portrait;
        if (icon is { Length: > 0 })
            files.Add((DeckClient.Combine(gridDir, id + "_icon.png"), icon));

        var hashes = OptimizerSettings.OverwriteArtwork
            ? RemoteHashes(client, files.Select(f => f.Path))
            : null;

        var written = 0;
        var skipped = 0;
        foreach (var (path, data) in files)
        {
            if (data is not { Length: > 0 }) continue;
            if (ArtworkUnchanged(client, path, data, hashes))
            {
                skipped++;
                continue;
            }
            client.WriteBytes(path, data);
            written++;
        }
        return (written, skipped);
    }

    private static bool ArtworkUnchanged(DeckClient client, string path, byte[] data,
        Dictionary<string, string>? hashes)
    {
        if (!OptimizerSettings.OverwriteArtwork)
            return client.FileLength(path) >= 0;

        if (hashes is not null &&
            hashes.TryGetValue(path, out var remote) &&
            string.Equals(remote, Sha256Hex(data), StringComparison.OrdinalIgnoreCase))
            return true;

        var size = client.FileLength(path);
        if (size < 0) return false;
        if (size != data.Length) return false;
        try
        {
            return client.ReadBytes(path).AsSpan().SequenceEqual(data);
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> RemoteHashes(DeckClient client, IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var quoted = string.Join(" ", list.Select(DeckClient.ShQuote));
            var output = client.Execute("sha256sum -b " + quoted + " 2>/dev/null", 20);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n'))
            {
                var text = line.TrimEnd('\r').Trim();
                if (text.Length < 66) continue;
                var hash = text[..64].Trim();
                var rest = text[64..].Trim().TrimStart('*').Trim();
                if (hash.Length == 64 && rest.Length > 0)
                    map[rest] = hash;
            }
            return map;
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    private static List<SteamShortcut> LoadSteamIndex(DeckClient client)
    {
        var configs = SteamShortcuts.FindUserConfigs(client);
        return configs.Count == 0 ? [] : SteamShortcuts.Load(client, configs[0]);
    }

    private readonly record struct RomHit(string SystemFolder, string FileName, string FullPath);

    private static List<RomHit> ListRoms(DeckClient client, AppCatalog catalog)
    {
        return RomScan.ListFiles(client, catalog)
            .Select(f => new RomHit(f.SystemFolder, f.FileName, f.FullPath))
            .ToList();
    }

    private static string DisplayName(RomHit rom, AppCatalog catalog, SystemProfile profile,
        out bool romHack, out bool translation)
    {
        romHack = false;
        translation = false;
        if (RomHackLog.TryGet(rom.FullPath, out var logged, out var kind))
        {
            if (string.Equals(kind, "translation", StringComparison.OrdinalIgnoreCase))
            {
                translation = true;
                return StoreGame.LooksLikeTranslation(logged) ? logged : logged + " (NL)";
            }
            romHack = true;
            return logged;
        }

        var raw = rom.FileName;
        var titleId = GameLibrary.ExtractTitleId(raw);
        if (titleId is not null && catalog.TitleIds.TryGetValue(titleId, out var mapped))
            return mapped;

        var cleaned = RomNameCleaner.Clean(raw);
        var fromThisApp = StoreGame.LooksLikeTranslation(raw);
        if (fromThisApp && !StoreGame.LooksLikeTranslation(cleaned))
            cleaned += " (NL)";
        var identity = catalog.ResolveStoreGame(cleaned, profile.Id, titleId, fromThisApp);
        translation = fromThisApp;
        return string.IsNullOrWhiteSpace(identity.Name) ? cleaned : identity.Name;
    }

    private static string GroupKey(RomHit f)
    {
        if (KeepSeparate(f))
            return "solo|" + f.FullPath.Replace('\\', '/');
        return SystemId(f.SystemFolder) + "|" + StemKey(f.FileName);
    }

    private static bool KeepSeparate(RomHit f) =>
        StoreGame.LooksLikeTranslation(f.FileName) ||
        RomHackLog.TryGet(f.FullPath, out _) ||
        LooksLikeHackName(f.FileName);

    private static bool LooksLikeHackName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (PackStore.LooksLikeRomHack(fileName, fileName: fileName)) return true;
        return Regex.IsMatch(fileName, @"[\(\[]\s*(h(ack)?s?|t[\+\-])", RegexOptions.IgnoreCase);
    }

    private static string SystemId(string folder) =>
        SystemCatalog.FromFolder(folder)?.Id ?? folder.Trim().ToLowerInvariant();

    private static string StemKey(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        stem = TitleId.Replace(stem, "");
        return StoreGame.FoldTitle(RomNameCleaner.Clean(stem));
    }

    private static void Report(IProgress<OptimizeProgress>? progress, string title, string detail,
        double percent, int current = 0, int total = 0, bool indeterminate = false)
    {
        progress?.Report(new OptimizeProgress
        {
            Title = title,
            Detail = detail,
            Percent = percent,
            Current = current,
            Total = total,
            Indeterminate = indeterminate
        });
    }

    private static IProgress<string>? SteamProgress(IProgress<OptimizeProgress>? progress, double percent) =>
        progress is null ? null : new SteamProgressAdapter(progress, percent);

    private sealed class SteamProgressAdapter : IProgress<string>
    {
        private readonly IProgress<OptimizeProgress> _inner;
        private readonly double _percent;

        public SteamProgressAdapter(IProgress<OptimizeProgress> inner, double percent)
        {
            _inner = inner;
            _percent = percent;
        }

        public void Report(string value) =>
            _inner.Report(new OptimizeProgress
            {
                Title = "Prepare Steam",
                Detail = value,
                Percent = _percent,
                Indeterminate = true
            });
    }

    private static void EnsureExecutable(DeckClient client, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            client.Execute("chmod +x " + DeckClient.ShQuote(target) + " 2>/dev/null || true", 8);
        }
        catch
        {
            /* launcher blijft zoals die is */
        }
    }

}
