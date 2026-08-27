using VisualSSH.Models;

namespace VisualSSH.Services.GameOptimizer;

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

    public static DeckSessionKind Detect(DeckClient client)
    {
        try
        {
            var result = client.Execute("python3 -c " + DeckClient.ShQuote(DetectPy), 10)
                .Trim().ToLowerInvariant();
            var kind = Parse(result);
            if (kind != DeckSessionKind.Unknown)
                return kind;
        }
        catch
        {
            /* bash-fallback */
        }

        try
        {
            var result = client.Execute(Env + DetectBash, 10).Trim().ToLowerInvariant();
            return Parse(result);
        }
        catch
        {
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
                "SESAME draait in Game Mode. Steam moet even dicht om shortcuts te schrijven, en dat sluit deze app. " +
                "Open SESAME in Desktop Mode (of via SSH vanaf een andere pc) om te optimaliseren.");
        }

        var kind = Detect(client);
        var gameMode = kind != DeckSessionKind.Desktop;
        if (gameMode)
        {
            progress?.Report(kind == DeckSessionKind.Unknown
                ? "Sessie onduidelijk — uitgaan van Game Mode, naar Desktop Mode…"
                : "Game Mode gedetecteerd — naar Desktop Mode…");
            Try(client, "steamos-session-select plasma");
            WaitUntil(() => Detect(client) == DeckSessionKind.Desktop, 25_000, 400,
                progress, "Wachten tot Desktop Mode klaar is");
            if (Detect(client) == DeckSessionKind.Desktop)
                progress?.Report("Desktop Mode gedetecteerd.");
            else
                progress?.Report("Desktop Mode niet bevestigd — Steam toch afsluiten…");
        }
        else
            progress?.Report("Desktop Mode gedetecteerd — Steam afsluiten…");

        progress?.Report("Steam afsluiten…");
        CloseSteam(client);
        return gameMode;
    }

    public static void RestoreGameMode(DeckClient client, bool wasGameMode, IProgress<string>? progress)
    {
        if (!wasGameMode) return;
        progress?.Report("Game Mode weer starten…");
        Try(client, "steamos-session-select gamescope");
    }

    public static void CloseSteam(DeckClient client)
    {
        Try(client, "steam -shutdown");
        WaitUntil(() => !SteamRunning(client), 8_000, 400, null, "");

        if (SteamRunning(client) && Detect(client) != DeckSessionKind.GameMode)
        {
            Try(client, "killall -9 steam steamwebhelper steam.sh");
            Thread.Sleep(400);
        }
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
        while (Environment.TickCount64 - started < timeoutMs)
        {
            if (done()) return;
            if (!string.IsNullOrEmpty(message))
            {
                var sec = (Environment.TickCount64 - started) / 1000.0;
                progress?.Report(message + " (" + sec.ToString("0") + "s)");
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
