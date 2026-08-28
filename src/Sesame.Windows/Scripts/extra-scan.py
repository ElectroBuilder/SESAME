import glob, json, os, re, sqlite3

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
    print(kind + '\t' + title + '\t' + exe + '\t' + start + '\t' + opts)

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

for root in HYDRA_ROOTS:
    if not os.path.isdir(root):
        continue
    for dirpath, dirs, files in os.walk(root):
        base = os.path.basename(dirpath).lower()
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

desktops = []
for d in [
    os.path.join(HOME, '.local', 'share', 'applications'),
    '/usr/share/applications',
    '/var/lib/flatpak/exports/share/applications',
    os.path.join(HOME, '.local', 'share', 'flatpak', 'exports', 'share', 'applications'),
]:
    desktops += glob.glob(os.path.join(d, '*.desktop'))

want_apps = ('kodi', 'stremio', 'tv.kodi', 'com.stremio', 'plex', 'jellyfin')
for path in desktops:
    try:
        text = open(path, encoding='utf-8', errors='ignore').read()
    except Exception:
        continue
    low = (text + ' ' + os.path.basename(path)).lower()
    name = re.search(r'^Name=(.*)$', text, re.M)
    exe = re.search(r'^Exec=(.*)$', text, re.M)
    nodisplay = re.search(r'^NoDisplay=true', text, re.I | re.M)
    if not name or not exe or nodisplay:
        continue
    title = name.group(1).strip()
    cmd = re.sub(r'\s+%[fFuUdDnNickvm]', '', exe.group(1).strip()).strip()
    if not title or not cmd:
        continue
    if 'hydra' in low:
        parts = cmd.split(None, 1)
        emit('HYDRA', title, parts[0], os.path.dirname(parts[0]), parts[1] if len(parts) > 1 else '')
        continue
    if any(w in low for w in want_apps):
        if cmd.startswith('flatpak '):
            emit('APP', title, '/usr/bin/flatpak', HOME, cmd[8:].strip())
        else:
            parts = cmd.split(None, 1)
            emit('APP', title, parts[0], os.path.dirname(parts[0]) or HOME, parts[1] if len(parts) > 1 else '')
