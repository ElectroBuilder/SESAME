using System.Text;

namespace Sesame.Services.Mii;

public sealed record MiiPathCandidate(string Path, string Source, bool Authoritative = false);
public sealed record MiiPathResolution(MiiOperationSnapshot Target, IReadOnlyList<MiiPathCandidate> Candidates,
    IReadOnlyList<MiiPathCandidate> ValidCandidates, bool Exists, bool IsAmbiguous);

/// <summary>Bounded exact-path probing; never a recursive home or mount scan.</summary>
public sealed class MiiDatabaseLocator(IMiiNandTransport transport)
{
    private const string WiiSuffix = "Wii/shared2/menu/FaceLib/RFL_DB.dat";
    private const string EdenUserSuffix = "nand/system/save/8000000000000030/MiiDatabase.dat";
    private const string EdenNandSuffix = "system/save/8000000000000030/MiiDatabase.dat";
    private readonly MiiFormatWii _wii = new();
    private readonly MiiFormatSwitch _eden = new();

    public MiiOperationSnapshot Preferred(MiiTargetKind kind)
    {
        var candidate = Candidates(kind)[0];
        return new MiiOperationSnapshot(kind, candidate.Path, transport.HostId, transport.Host,
            "Checking exact known database paths…");
    }

    public MiiPathResolution Resolve(MiiTargetKind kind, string? selectedPath = null)
    {
        var candidates = Candidates(kind);
        var valid = new List<MiiPathCandidate>();
        var issues = new List<string>();
        var expectedSize = kind == MiiTargetKind.Wii ? MiiFormatWii.Size : MiiFormatSwitch.Size;
        var format = kind == MiiTargetKind.Wii ? (IMiiFormat)_wii : _eden;
        foreach (var candidate in candidates)
        {
            try
            {
                if (!transport.Exists(candidate.Path)) continue;
                var length = transport.FileLength(candidate.Path);
                if (length != expectedSize)
                {
                    issues.Add(candidate.Source + " has size " + length + " instead of " + expectedSize + ".");
                    continue;
                }
                var validation = format.Validate(transport.ReadBytes(candidate.Path));
                if (!validation.IsValid)
                {
                    issues.Add(candidate.Source + " failed format/CRC validation: " + validation.Error);
                    continue;
                }
                valid.Add(candidate);
            }
            catch (Exception ex) { issues.Add(candidate.Source + " could not be probed: " + ex.Message); }
        }

        if (!string.IsNullOrWhiteSpace(selectedPath) &&
            valid.FirstOrDefault(x => string.Equals(x.Path, selectedPath, StringComparison.Ordinal)) is { } selected)
            return Found(kind, candidates, valid, selected, "Selected verified database at " + selected.Source + ".");

        var authoritative = candidates.FirstOrDefault(x => x.Authoritative);
        if (authoritative is not null)
        {
            var verified = valid.FirstOrDefault(x => string.Equals(x.Path, authoritative.Path, StringComparison.Ordinal));
            if (verified is not null)
                return Found(kind, candidates, valid, verified, "Verified " + verified.Source + ".");
            var alternatives = valid.Count == 0 ? "" : " Verified alternatives require explicit selection: " +
                string.Join("; ", valid.Select(x => x.Path));
            return Missing(kind, candidates, valid, authoritative,
                "The authoritative override/config database is missing or invalid." + alternatives + IssueText(issues),
                approved: valid.Count == 0);
        }

        if (valid.Count == 1)
            return Found(kind, candidates, valid, valid[0], "Detected and verified " + valid[0].Source + ".");
        if (valid.Count > 1)
        {
            var status = "Multiple valid Mii databases were found. Select the active target explicitly: " +
                         string.Join("; ", valid.Select(x => x.Path));
            var target = new MiiOperationSnapshot(kind, valid[0].Path, transport.HostId, transport.Host, status,
                PathApproved: false);
            return new MiiPathResolution(target, candidates, valid, Exists: true, IsAmbiguous: true);
        }

        var preferred = candidates[0];
        return Missing(kind, candidates, valid, preferred,
            "No valid supported database found. Checked exact known paths: " +
            string.Join("; ", candidates.Select(x => x.Path)) + IssueText(issues), approved: true);
    }

    public IReadOnlyList<MiiPathCandidate> Candidates(MiiTargetKind kind)
    {
        var list = new List<MiiPathCandidate>();
        var id = kind == MiiTargetKind.Wii ? "dolphin" : "eden";
        LibraryPaths.Current.EmulatorOverrides.TryGetValue(id, out var overrides);
        if (kind == MiiTargetKind.Wii && EmulatorPaths.NormalizeOverride(overrides?.UserRoot) is { } dolphinOverride)
            Add(list, dolphinOverride, WiiSuffix, "the explicit SESAME Dolphin override", true);
        if (kind == MiiTargetKind.Eden)
        {
            if (EmulatorPaths.NormalizeOverride(overrides?.NandRoot) is { } nandOverride)
                Add(list, nandOverride, EdenNandSuffix, "the explicit SESAME Eden NAND override", true);
            else if (EmulatorPaths.NormalizeOverride(overrides?.UserRoot) is { } edenOverride)
                Add(list, edenOverride, EdenUserSuffix, "the explicit SESAME Eden user-root override", true);
            foreach (var configured in EdenConfiguredNandRoots())
                Add(list, configured, EdenNandSuffix, "Eden's configured nand_directory", true);
        }

        var home = Normalize(transport.Home);
        if (kind == MiiTargetKind.Wii)
        {
            Add(list, DeckClient.Combine(LibraryPaths.Current.EmulationRoot, "saves/dolphin"), WiiSuffix,
                "the EmuDeck Dolphin saves link");
            Add(list, DeckClient.Combine(LibraryPaths.Current.EmulationRoot, "saves/dolphin-emu"), WiiSuffix,
                "the EmuDeck Dolphin-emu saves link");
            Add(list, DeckClient.Combine(home, ".var/app/org.DolphinEmu.dolphin-emu/data/dolphin-emu"), WiiSuffix,
                "the Dolphin Flatpak data root");
            Add(list, EmulatorPaths.UserRoot("dolphin"), WiiSuffix, "the legacy SESAME Dolphin storage default");
            Add(list, DeckClient.Combine(home, ".local/share/dolphin-emu"), WiiSuffix, "the native Dolphin XDG data root");
            Add(list, DeckClient.Combine(home, ".dolphin-emu"), WiiSuffix, "the legacy Dolphin all-in-one user root");
        }
        else
        {
            Add(list, EmulatorPaths.UserRoot("eden"), EdenUserSuffix, "the SESAME/EmuDeck Eden root");
            Add(list, DeckClient.Combine(home, ".var/app/org.eden_emu.eden/data/eden"), EdenUserSuffix,
                "the Eden Flatpak data root (org.eden_emu.eden)");
            Add(list, DeckClient.Combine(home, ".var/app/dev.eden_emu.eden/data/eden"), EdenUserSuffix,
                "the Eden Flatpak data root (dev.eden_emu.eden)");
            Add(list, DeckClient.Combine(home, ".var/app/org.EdenEmu.Eden/data/eden"), EdenUserSuffix,
                "the Eden Flatpak data root (org.EdenEmu.Eden)");
            Add(list, DeckClient.Combine(home, ".local/share/eden"), EdenUserSuffix, "Eden's native XDG data root");
            Add(list, DeckClient.Combine(home, ".local/share/Eden"), EdenUserSuffix, "Eden's uppercase XDG fallback");
        }
        if (list.Count == 0) throw new InvalidOperationException("No safe Mii database candidates were produced.");
        return list;
    }

    private IEnumerable<string> EdenConfiguredNandRoots()
    {
        var home = Normalize(transport.Home);
        foreach (var config in new[]
                 {
                     DeckClient.Combine(home, ".config/eden/qt-config.ini"),
                     DeckClient.Combine(home, ".config/Eden/qt-config.ini")
                 })
        {
            byte[] bytes;
            try
            {
                if (!transport.Exists(config) || transport.FileLength(config) is < 1 or > 1_048_576) continue;
                bytes = transport.ReadBytes(config);
            }
            catch { continue; }
            var inStorage = false;
            foreach (var raw in Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                var line = raw.Trim().TrimEnd('\r');
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inStorage = line.Equals("[Data%20Storage]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inStorage || !line.StartsWith("nand_directory=", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[(line.IndexOf('=') + 1)..].Trim().Trim('"');
                if (IsSafeConfiguredRoot(value, home)) yield return Normalize(value);
                break;
            }
        }
    }

    private static bool IsSafeConfiguredRoot(string value, string home)
    {
        var root = Normalize(value);
        if (!IsSafeAbsolute(root)) return false;
        var emulation = Normalize(LibraryPaths.Current.EmulationRoot);
        return Under(root, home) || Under(root, emulation) || Under(root, "/run/media");
    }

    private static bool Under(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal) || path.StartsWith(root + "/", StringComparison.Ordinal);

    private MiiPathResolution Found(MiiTargetKind kind, IReadOnlyList<MiiPathCandidate> candidates,
        IReadOnlyList<MiiPathCandidate> valid, MiiPathCandidate selected, string status)
    {
        if (valid.Count > 1) status += " Other valid databases also exist; this path is frozen for the operation.";
        return new MiiPathResolution(
            new MiiOperationSnapshot(kind, selected.Path, transport.HostId, transport.Host, status),
            candidates, valid, Exists: true, IsAmbiguous: false);
    }

    private MiiPathResolution Missing(MiiTargetKind kind, IReadOnlyList<MiiPathCandidate> candidates,
        IReadOnlyList<MiiPathCandidate> valid, MiiPathCandidate candidate, string status, bool approved) =>
        new(new MiiOperationSnapshot(kind, candidate.Path, transport.HostId, transport.Host, status, approved),
            candidates, valid, Exists: false, IsAmbiguous: false);

    private static string IssueText(IReadOnlyList<string> issues) =>
        issues.Count == 0 ? "" : " Probe details: " + string.Join(" ", issues);

    private static void Add(List<MiiPathCandidate> list, string root, string suffix, string source, bool authoritative = false)
    {
        root = Normalize(root);
        if (!IsSafeAbsolute(root)) return;
        var path = DeckClient.Combine(root, suffix);
        if (list.Any(x => string.Equals(x.Path, path, StringComparison.Ordinal))) return;
        list.Add(new MiiPathCandidate(path, source, authoritative));
    }

    private static string Normalize(string value) => (value ?? "").Trim().Replace('\\', '/').TrimEnd('/');
    private static bool IsSafeAbsolute(string root) => root.Length > 0 && root[0] == '/' && !root.Contains('\0') &&
        !root.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..");
}
