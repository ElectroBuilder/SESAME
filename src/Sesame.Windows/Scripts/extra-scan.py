import glob, json, os, re, sqlite3, sys

try:
    sys.stdout.reconfigure(line_buffering=True)
except Exception:
    pass

if 'MODE' not in globals():
    MODE = 'all'

HOME = os.path.expanduser('~')
SEEN = set()
MAX_BYTES = 8 * 1024 * 1024

def emit(kind, title, exe, start, opts=''):
    if not title or not exe:
        return
    title = str(title).replace('\t', ' ').strip()
    exe = str(exe).strip().strip('"')
    start = str(start or '').strip()
    opts = str(opts or '').strip()
    if not title or not exe:
        return
    key = (kind + '|' + title + '|' + exe).lower()
    if key in SEEN:
        return
    SEEN.add(key)
    print(kind + '\t' + title + '\t' + exe + '\t' + start + '\t' + opts, flush=True)

def take_game(obj):
    if not isinstance(obj, dict):
        return None
    exe = obj.get('executablePath') or obj.get('executable') or obj.get('exe') or obj.get('gamePath') or obj.get('path')
    title = obj.get('title') or obj.get('name') or obj.get('appName') or obj.get('gameTitle')
    if not exe or not title:
        return None
    if isinstance(exe, dict):
        exe = exe.get('path') or exe.get('exe')
    opts = obj.get('launchOptions') or obj.get('launchParameters') or obj.get('args') or ''
    return str(title), str(exe), str(opts or '')

def walk_obj(obj):
    hit = take_game(obj)
    if hit:
        yield hit
    if isinstance(obj, dict):
        for v in obj.values():
            yield from walk_obj(v)
    elif isinstance(obj, list):
        for v in obj:
            yield from walk_obj(v)

def extract_json_objects(text):
    i = 0
    n = len(text)
    while i < n:
        j = text.find('{', i)
        if j < 0:
            return
        depth = 0
        k = j
        in_str = False
        esc = False
        while k < n and k - j < 250000:
            c = text[k]
            if in_str:
                if esc:
                    esc = False
                elif c == '\\':
                    esc = True
                elif c == '"':
                    in_str = False
            else:
                if c == '"':
                    in_str = True
                elif c == '{':
                    depth += 1
                elif c == '}':
                    depth -= 1
                    if depth == 0:
                        blob = text[j:k + 1]
                        try:
                            yield json.loads(blob)
                        except Exception:
                            pass
                        break
            k += 1
        i = j + 1

def read_bytes(path):
    try:
        size = os.path.getsize(path)
        if size <= 0 or size > MAX_BYTES:
            return b''
        with open(path, 'rb') as fh:
            return fh.read()
    except Exception:
        return b''

def scan_blob(data, kind='HYDRA'):
    if not data:
        return
    for enc in ('utf-8', 'utf-16le'):
        try:
            text = data.decode(enc, 'ignore')
        except Exception:
            continue
        for obj in extract_json_objects(text):
            for title, exe, opts in walk_obj(obj):
                emit(kind, title, exe, os.path.dirname(exe), opts)

def scan_json_file(path):
    data = read_bytes(path)
    if not data:
        return
    try:
        obj = json.loads(data.decode('utf-8', 'ignore'))
        for title, exe, opts in walk_obj(obj):
            emit('HYDRA', title, exe, os.path.dirname(exe), opts)
        return
    except Exception:
        pass
    scan_blob(data)

def ident(name):
    return bool(re.match(r'^[A-Za-z_][A-Za-z0-9_]*$', name or ''))

def scan_sqlite(path):
    try:
        con = sqlite3.connect('file:%s?mode=ro' % path.replace('?', '%3F'), uri=True)
        con.row_factory = sqlite3.Row
    except Exception:
        return
    try:
        tables = [r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")]
        for table in tables:
            if not ident(table):
                continue
            try:
                cols = [r[1] for r in con.execute('PRAGMA table_info(%s)' % table)]
            except Exception:
                continue
            low = {c.lower(): c for c in cols}
            tcol = next((low[k] for k in ('title', 'name', 'game_title', 'appname', 'gametitle') if k in low), None)
            ecol = next((low[k] for k in ('executablepath', 'executable', 'exe', 'path', 'gamepath') if k in low), None)
            if not tcol or not ecol:
                continue
            ocol = next((low[k] for k in ('launchoptions', 'launch_options', 'args', 'launchparameters') if k in low), None)
            try:
                for row in con.execute('SELECT * FROM %s' % table):
                    title = row[tcol]
                    exe = row[ecol]
                    opts = row[ocol] if ocol else ''
                    if title and exe:
                        emit('HYDRA', title, exe, os.path.dirname(str(exe)), opts or '')
            except Exception:
                continue
    finally:
        try:
            con.close()
        except Exception:
            pass

HYDRA_ROOTS = [
    os.path.join(HOME, '.config', 'hydra'),
    os.path.join(HOME, '.config', 'hydralauncher'),
    os.path.join(HOME, '.var', 'app'),
    os.path.join(HOME, '.local', 'share', 'hydra'),
    os.path.join(HOME, '.local', 'share', 'hydralauncher'),
]

for root in (HYDRA_ROOTS if MODE in ('all', 'hydra') else []):
    if not os.path.isdir(root):
        continue
    for dirpath, dirs, files in os.walk(root):
        base = os.path.basename(dirpath).lower()
        # .var/app holds every Flatpak; only walk Hydra there or we pick up Kate, browsers, …
        if os.path.basename(root) == 'app' and dirpath == root:
            dirs[:] = [d for d in dirs if 'hydra' in d.lower()]
        if any(skip in base for skip in ('cache', 'gpu', 'code_cache', 'blob_storage', 'dawn')):
            dirs[:] = []
            continue
        if len(dirpath) - len(root) > 180:
            dirs.clear()
            continue
        hydra_db = 'hydra-db' in base or base == 'hydra-db'
        for name in files:
            low = name.lower()
            path = os.path.join(dirpath, name)
            if hydra_db or low.endswith(('.ldb', '.log')) or low in ('current', 'manifest', '000003.log'):
                scan_blob(read_bytes(path))
            elif low.endswith(('.json', '.jsonc')) and ('hydra' in dirpath.lower() or 'hydra' in low):
                scan_json_file(path)
            elif low.endswith(('.db', '.sqlite', '.sqlite3')) and 'hydra' in (dirpath.lower() + low):
                scan_sqlite(path)

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
