using Sesame.Models;

namespace Sesame.Services.GameOptimizer;

public enum DeckSessionKind
{
    Desktop,
    GameMode,
    Unknown
}

public static class SteamSession
{
    private const string Env =
        "export XDG_RUNTIME_DIR=/run/user/$(id -u); " +
        "export DBUS_SESSION_BUS_ADDRESS=unix:path=$XDG_RUNTIME_DIR/bus; ";

    // Avoid repeating slow Detect calls within one Optimize run.
    private static DeckSessionKind _cachedKind = DeckSessionKind.Unknown;
    private static long _cachedAt;
    private static string? _cachedClientId;
    private const int CacheMs = 2_500;

    public static DeckSessionKind Detect(DeckClient client, IProgress<string>? progress = null)
    {
        var id = ClientId(client);
        var now = Environment.TickCount64;
        if (_cachedClientId == id && now - _cachedAt < CacheMs && _cachedKind != DeckSessionKind.Unknown)
            return _cachedKind;

        // Bash pgrep is much faster over SSH than scanning all of /proc via Python.
        progress?.Report("Checking Deck session (Desktop vs Game Mode)…");
        try
        {
            var result = client.Execute(Env + DetectBash, 8).Trim().ToLowerInvariant();
            var kind = Parse(result);
            if (kind != DeckSessionKind.Unknown)
            {
                Remember(id, kind);
                return kind;
            }
        }
        catch
        {
            /* fall through to Python */
        }

        progress?.Report("Session still unclear — deeper check…");
        try
        {
            var result = client.Execute("python3 -c " + DeckClient.ShQuote(DetectPy), 12)
                .Trim().ToLowerInvariant();
            var kind = Parse(result);
            Remember(id, kind);
            return kind;
        }
        catch
        {
            Remember(id, DeckSessionKind.Unknown);
            return DeckSessionKind.Unknown;
        }
    }

    public static bool IsGameMode(DeckClient client) => Detect(client) == DeckSessionKind.GameMode;

    public static bool IsDesktopMode(DeckClient client) => Detect(client) == DeckSessionKind.Desktop;

    public static bool PrepareForWrite(DeckClient client, IProgress<string>? progress)
    {
        if (client.IsLocal && HostEnvironment.RunningInGamescope)
        {
            throw new InvalidOperationException(
                "SESAME is running in Game Mode. Steam has to close briefly to write shortcuts, and that closes this app. " +
                "Open SESAME in Desktop Mode (or over SSH from another PC) to optimize.");
        }

        InvalidateCache();
        progress?.Report("Step 1/4 — Checking whether the Deck is in Game Mode or Desktop…");
        var kind = Detect(client, progress);
        var gameMode = kind != DeckSessionKind.Desktop;

        if (gameMode)
        {
            progress?.Report(kind == DeckSessionKind.Unknown
                ? "Step 2/4 — Session unclear; switching to Desktop Mode (Plasma)…"
                : "Step 2/4 — Game Mode detected; switching to Desktop Mode (Plasma)…");
            Try(client, "steamos-session-select plasma");
            WaitUntil(
                () =>
                {
                    InvalidateCache();
                    return Detect(client) == DeckSessionKind.Desktop;
                },
                25_000,
                500,
                progress,
                "Step 2/4 — Waiting for Desktop Mode (Plasma starting)");
            InvalidateCache();
            if (Detect(client) == DeckSessionKind.Desktop)
                progress?.Report("Step 2/4 — Desktop Mode is ready.");
            else
                progress?.Report("Step 2/4 — Desktop not confirmed yet; continuing so Steam can still be closed…");
        }
        else
            progress?.Report("Step 2/4 — Already in Desktop Mode.");

        progress?.Report("Step 3/4 — Closing Steam so shortcut files unlock…");
        CloseSteam(client, progress);

        progress?.Report("Step 4/4 — Steam paused; writing shortcuts next…");
        return gameMode;
    }

    public static void RestoreGameMode(DeckClient client, bool wasGameMode, IProgress<string>? progress)
    {
        if (!wasGameMode) return;
        progress?.Report("Switching the Deck back to Game Mode…");
        InvalidateCache();
        Try(client, "steamos-session-select gamescope");
    }

    public static bool IsSteamRunning(DeckClient client) => SteamRunning(client);

    public static void CloseSteam(DeckClient client, IProgress<string>? progress = null)
    {
        Try(client, "steam -shutdown");
        WaitUntil(() => !SteamRunning(client), 8_000, 400, progress,
            "Waiting for Steam to exit");

        if (SteamRunning(client) && Detect(client) != DeckSessionKind.GameMode)
        {
            progress?.Report("Steam still running — forcing exit…");
            Try(client, "killall -9 steam steamwebhelper steam.sh");
            Thread.Sleep(400);
        }

        if (!SteamRunning(client))
            progress?.Report("Steam has exited.");
        else
            progress?.Report("Steam may still be running — continuing anyway…");
    }

    private static string ClientId(DeckClient client) =>
        client.IsLocal ? "local" : (client.ActiveProfile?.Id ?? client.ActiveProfile?.Host ?? "ssh");

    private static void Remember(string id, DeckSessionKind kind)
    {
        _cachedClientId = id;
        _cachedKind = kind;
        _cachedAt = Environment.TickCount64;
    }

    private static void InvalidateCache()
    {
        _cachedKind = DeckSessionKind.Unknown;
        _cachedAt = 0;
    }

    private static DeckSessionKind Parse(string result)
    {
        if (result.Contains("gamemode")) return DeckSessionKind.GameMode;
        if (result.Contains("desktop")) return DeckSessionKind.Desktop;
        return DeckSessionKind.Unknown;
    }

    private static bool SteamRunning(DeckClient client)
    {
        try
        {
            return client.Execute("pgrep -x steam >/dev/null && echo yes || echo no", 8)
                .Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void WaitUntil(Func<bool> done, int timeoutMs, int stepMs,
        IProgress<string>? progress, string message)
    {
        var started = Environment.TickCount64;
        var lastReport = -1;
        while (Environment.TickCount64 - started < timeoutMs)
        {
            if (done()) return;
            if (!string.IsNullOrEmpty(message))
            {
                var sec = (int)((Environment.TickCount64 - started) / 1000);
                if (sec != lastReport)
                {
                    lastReport = sec;
                    progress?.Report(message + "… " + sec + "s");
                }
            }
            Thread.Sleep(stepMs);
        }
    }

    private static void Try(DeckClient client, string command)
    {
        try
        {
            client.Execute(Env + command + " >/dev/null 2>&1 || true", 20);
        }
        catch
        {
            /* sessie-wissel of steam-stop is best-effort */
        }
    }

    private const string DetectPy =
        "import glob,os,sys\n" +
        "def reads(p):\n" +
        "    try: return open(p,'rb').read()\n" +
        "    except: return b''\n" +
        "names=set(); cmds=[]; desks=[]\n" +
        "for d in glob.glob('/proc/[0-9]*'):\n" +
        "    comm=reads(d+'/comm').decode('utf-8','replace').strip()\n" +
        "    if comm: names.add(comm)\n" +
        "    cmd=reads(d+'/cmdline').replace(b'\\x00', b' ').decode('utf-8','replace').lower()\n" +
        "    if cmd: cmds.append(cmd)\n" +
        "    if comm in ('steam','reaper','steam.sh'):\n" +
        "        for e in reads(d+'/environ').split(b'\\x00'):\n" +
        "            if e.lower().startswith(b'xdg_current_desktop=') or e.lower().startswith(b'desktop_session=') or e.lower().startswith(b'xdg_session_desktop='):\n" +
        "                desks.append(e.split(b'=',1)[1].decode('utf-8','replace').lower())\n" +
        "blob=' '.join(cmds)\n" +
        "if any('gamescope' in x for x in desks):\n" +
        "    print('gamemode'); sys.exit(0)\n" +
        "if any(('kde' in x or 'plasma' in x) for x in desks):\n" +
        "    print('desktop'); sys.exit(0)\n" +
        "gs=any(n.startswith('gamescope') for n in names) or 'gamescope-session' in blob or 'gamescope' in blob\n" +
        "pl='plasmashell' in names or any(n.startswith('startplasma') for n in names)\n" +
        "if gs and not pl: print('gamemode')\n" +
        "elif pl and not gs: print('desktop')\n" +
        "elif gs: print('gamemode')\n" +
        "elif pl: print('desktop')\n" +
        "else: print('unknown')\n";

    private const string DetectBash =
        "if pgrep -x gamescope >/dev/null 2>&1 || pgrep -f gamescope-session >/dev/null 2>&1; then echo gamemode; " +
        "elif pgrep -x plasmashell >/dev/null 2>&1 || pgrep -f startplasma >/dev/null 2>&1; then echo desktop; " +
        "elif systemctl --user is-active --quiet gamescope-session.service 2>/dev/null; then echo gamemode; " +
        "elif systemctl --user is-active --quiet plasma-plasmashell.service 2>/dev/null; then echo desktop; " +
        "else echo unknown; fi";
}
