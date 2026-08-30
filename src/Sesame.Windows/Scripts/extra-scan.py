import glob, json, os, re, sys

try:
    sys.stdout.reconfigure(line_buffering=True)
except Exception:
    pass

if 'MODE' not in globals():
    MODE = 'all'

HOME = os.path.expanduser('~')
SEEN = set()

def exe_exists(path):
    """True only for a real file on this machine (drops stale / Windows paths)."""
    if not path:
        return False
    p = str(path).strip().strip('"')
    if not p:
        return False
    # Wine-style Windows paths are never valid on the Deck filesystem.
    if len(p) >= 3 and p[1] == ':' and p[0].isalpha():
        return False
    p = p.replace('\\', '/')
    try:
        return os.path.isfile(p)
    except Exception:
        return False

def emit(kind, title, exe, start, opts=''):
    if not title or not exe:
        return
    title = str(title).replace('\t', ' ').strip()
    exe = str(exe).strip().strip('"')
    start = str(start or '').strip()
    opts = str(opts or '').strip()
    if not title or not exe:
        return
    # Hydra/Lutris/Other: never emit ghost entries whose .exe is gone.
    if kind in ('HYDRA', 'LUTRIS', 'OTHER', 'GAME') and not exe_exists(exe):
        return
    key = (kind + '|' + title + '|' + exe).lower()
    if key in SEEN:
        return
    SEEN.add(key)
    print(kind + '\t' + title + '\t' + exe + '\t' + start + '\t' + opts, flush=True)

# Hydra library DB (LevelDB under ~/.config/hydra, Flatpak, …) is intentionally NOT
# scanned: it keeps titles after uninstall and caused 100+ ghost games in SESAME.
# Source of truth = game folders on disk under HYDRA_GAMES (+ common aliases).

if 'LUTRIS_ROOT' not in globals():
    LUTRIS_ROOT = os.path.join(HOME, 'Games', 'Lutris')
if 'OTHER_ROOT' not in globals():
    OTHER_ROOT = os.path.join(HOME, 'Games', 'Other')
if 'HYDRA_GAMES' not in globals():
    HYDRA_GAMES = os.path.join(HOME, 'Hydra')

def hydra_game_roots():
    """Configured Hydra games folder plus common Deck aliases (only dirs that exist)."""
    roots = []
    for cand in (
        HYDRA_GAMES,
        os.path.join(HOME, 'Hydra'),
        os.path.join(HOME, 'hydra'),
        os.path.join(HOME, 'Games', 'Hydra'),
    ):
        if not cand:
            continue
        norm = os.path.normpath(cand)
        if norm in roots:
            continue
        if os.path.isdir(norm):
            roots.append(norm)
    return roots

SKIP_EXE = (
    'unitycrashhandler', 'unitycrashhandler64', 'unitycrashhandler32',
    'uninstall', 'unins000', 'crashpad', 'crash_uploader', 'crashuploader',
    'vcredist', 'vc_redist', 'directx', 'dxsetup', 'dxwebsetup',
    'easyanticheat', 'eac', 'battleye',
    'dotnetfx', 'dotnetfx40', 'oalinst', 'physx',
)

def skip_exe(path):
    name = os.path.splitext(os.path.basename(path or ''))[0].lower()
    return any(s in name for s in SKIP_EXE)

def pick_exe(folder):
    hits = []
    try:
        names = os.listdir(folder)
    except Exception:
        return None
    folder_key = os.path.basename(folder).lower().replace(' ', '')
    for n in names:
        full = os.path.join(folder, n)
        if not os.path.isfile(full):
            continue
        if os.path.splitext(n)[1].lower() != '.exe':
            continue
        if skip_exe(full):
            continue
        try:
            size = os.path.getsize(full)
        except Exception:
            size = 0
        stem = os.path.splitext(n)[0].lower().replace(' ', '')
        score = size
        if stem == folder_key or folder_key.startswith(stem) or stem.startswith(folder_key):
            score += 10 ** 12
        hits.append((score, full))
    if not hits:
        return None
    hits.sort(reverse=True)
    return hits[0][1]

def scan_game_root(root, kind):
    if not root or not os.path.isdir(root):
        return
    try:
        names = os.listdir(root)
    except Exception:
        return
    dirs = [n for n in names if os.path.isdir(os.path.join(root, n))]
    direct = pick_exe(root)
    if direct and not dirs:
        emit(kind, os.path.splitext(os.path.basename(direct))[0], direct, root, '')
        return
    for n in dirs:
        if n.startswith('.'):
            continue
        folder = os.path.join(root, n)
        exe = pick_exe(folder)
        if not exe:
            for sub in ('bin', 'Bin', 'x64', 'win64', 'Game', 'game'):
                nested = os.path.join(folder, sub)
                if os.path.isdir(nested):
                    exe = pick_exe(nested)
                    if exe:
                        break
        if exe:
            emit(kind, n, exe, os.path.dirname(exe), '')

def scan_lutris_yaml():
    roots = [
        os.path.join(HOME, '.config', 'lutris', 'games'),
        os.path.join(HOME, '.var', 'app', 'net.lutris.Lutris', 'config', 'lutris', 'games'),
    ]
    for root in roots:
        if not os.path.isdir(root):
            continue
        for name in os.listdir(root):
            if not name.endswith(('.yml', '.yaml')):
                continue
            path = os.path.join(root, name)
            try:
                text = open(path, encoding='utf-8', errors='ignore').read()
            except Exception:
                continue
            title = None
            exe = None
            for line in text.splitlines():
                low = line.strip()
                if low.startswith('name:'):
                    title = low.split(':', 1)[1].strip().strip("'\"")
                elif low.startswith('exe:'):
                    exe = low.split(':', 1)[1].strip().strip("'\"")
            if title and exe and os.path.isfile(exe) and exe.lower().endswith('.exe') and not skip_exe(exe):
                emit('LUTRIS', title, exe, os.path.dirname(exe), '')

if MODE in ('all', 'hydra'):
    # MODE "hydra" from SESAME also carries Lutris/Other folder games (legacy).
    scan_lutris_yaml()
    scan_game_root(LUTRIS_ROOT, 'LUTRIS')
    scan_game_root(OTHER_ROOT, 'OTHER')
    for root in hydra_game_roots():
        scan_game_root(root, 'HYDRA')

if MODE not in ('all', 'apps'):
    raise SystemExit(0)

# Native Deck apps: match desktop id / Name / Exec only (not Comments — that caused false hits).
APP_CATALOG = [
    ('kodi', 'Kodi', ('kodi', 'tv.kodi')),
    ('stremio', 'Stremio', ('stremio', 'com.stremio')),
    ('hydra', 'Hydra', ('hydralauncher', 'com.hydralauncher', 'io.hydralauncher')),
    ('emudeck', 'EmuDeck', ('emudeck', 'com.emudeck')),
    ('lutris', 'Lutris', ('lutris', 'net.lutris')),
    ('chrome', 'Google Chrome', ('google-chrome', 'com.google.chrome', 'chrome')),
    ('chromium', 'Chromium', ('chromium', 'org.chromium')),
    ('firefox', 'Firefox', ('firefox', 'org.mozilla.firefox')),
    ('opera', 'Opera', ('opera', 'com.opera')),
    ('brave', 'Brave', ('brave', 'com.brave')),
    ('edge', 'Microsoft Edge', ('microsoft-edge', 'com.microsoft.edge', 'msedge')),
    ('plex', 'Plex', ('plex', 'tv.plex')),
    ('jellyfin', 'Jellyfin', ('jellyfin', 'org.jellyfin')),
]
FLATPAK_IDS = {
    'kodi': ('tv.kodi.Kodi',),
    'stremio': ('com.stremio.Stremio',),
    'hydra': ('com.hydralauncher.HydraLauncher', 'io.hydralauncher.Launcher'),
    'emudeck': ('com.emudeck.Emudeck',),
    'lutris': ('net.lutris.Lutris',),
    'chrome': ('com.google.Chrome',),
    'chromium': ('org.chromium.Chromium',),
    'firefox': ('org.mozilla.firefox',),
    'opera': ('com.opera.Opera',),
    'brave': ('com.brave.Browser',),
    'edge': ('com.microsoft.Edge',),
    'plex': ('tv.plex.PlexDesktop', 'tv.plex.PlexHTPC'),
    'jellyfin': ('org.jellyfin.JellyfinMediaPlayer', 'com.github.iwalton3.jellyfin-media-player'),
}
APP_SEEN = set()

def token_hit(hay, needle):
    i = hay.find(needle)
    if i < 0:
        return False
    before = i == 0 or not hay[i - 1].isalnum()
    after = i + len(needle) >= len(hay) or not hay[i + len(needle)].isalnum()
    return before and after

def match_app(*parts):
    hay = ' '.join(p for p in parts if p).lower().replace('\\', '/')
    for app_id, title, needles in APP_CATALOG:
        if any(token_hit(hay, n) for n in needles):
            return app_id, title
    return None, None

def emit_app(app_id, title, exe, start, opts):
    emit('APP', title, exe, start, opts)
    APP_SEEN.add(app_id)

def emit_cmd(app_id, title, cmd):
    cmd = re.sub(r'\s+%[fFuUdDnNickvm]', '', (cmd or '').strip()).strip()
    if not cmd:
        return
    if cmd.startswith('flatpak '):
        emit_app(app_id, title, '/usr/bin/flatpak', HOME, cmd[8:].strip())
    else:
        parts = cmd.split(None, 1)
        emit_app(app_id, title, parts[0], os.path.dirname(parts[0]) or HOME, parts[1] if len(parts) > 1 else '')

desktop_dirs = [
    os.path.join(HOME, '.local', 'share', 'applications'),
    '/usr/share/applications',
    '/usr/local/share/applications',
    '/var/lib/flatpak/exports/share/applications',
    os.path.join(HOME, '.local', 'share', 'flatpak', 'exports', 'share', 'applications'),
]
desktops = []
for d in desktop_dirs:
    desktops += glob.glob(os.path.join(d, '*.desktop'))
    desktops += glob.glob(os.path.join(d, '**', '*.desktop'), recursive=True)

for path in desktops:
    try:
        text = open(path, encoding='utf-8', errors='ignore').read()
    except Exception:
        continue
    name = re.search(r'^Name=(.*)$', text, re.M)
    exe = re.search(r'^Exec=(.*)$', text, re.M)
    wm = re.search(r'^StartupWMClass=(.*)$', text, re.M)
    flatpak = re.search(r'^X-Flatpak=(.*)$', text, re.M)
    if not name or not exe:
        continue
    title = name.group(1).strip()
    cmd = exe.group(1).strip()
    ident = ' '.join([
        os.path.basename(path),
        title,
        cmd,
        wm.group(1) if wm else '',
        flatpak.group(1) if flatpak else '',
    ])
    app_id, pretty = match_app(ident)
    if not app_id:
        continue
    emit_cmd(app_id, pretty or title, cmd)

for app_id, ids in FLATPAK_IDS.items():
    pretty = next(t for i, t, _ in APP_CATALOG if i == app_id)
    for fid in ids:
        for root in (
            os.path.join(HOME, '.local', 'share', 'flatpak', 'app', fid),
            os.path.join('/var/lib/flatpak/app', fid),
        ):
            if os.path.isdir(root):
                emit_app(app_id, pretty, '/usr/bin/flatpak', HOME, 'run ' + fid)
                break
        if app_id in APP_SEEN:
            break

BINARIES = {
    'kodi': ('kodi', 'kodi.bin', 'kodi-gbm', 'kodi-wayland', 'kodi-x11'),
    'stremio': ('stremio',),
    'lutris': ('lutris',),
    'chrome': ('google-chrome-stable', 'google-chrome', 'chrome'),
    'chromium': ('chromium', 'chromium-browser'),
    'firefox': ('firefox',),
    'opera': ('opera',),
    'brave': ('brave-browser', 'brave'),
    'edge': ('microsoft-edge-stable', 'microsoft-edge'),
    'emudeck': ('emudeck',),
}
path_dirs = os.environ.get('PATH', '').split(':')
for app_id, names in BINARIES.items():
    pretty = next(t for i, t, _ in APP_CATALOG if i == app_id)
    found = None
    for name in names:
        for folder in path_dirs:
            candidate = os.path.join(folder, name)
            if os.path.isfile(candidate) and os.access(candidate, os.X_OK):
                found = candidate
                break
        if found:
            break
    if found:
        emit_app(app_id, pretty, found, os.path.dirname(found) or HOME, '')
