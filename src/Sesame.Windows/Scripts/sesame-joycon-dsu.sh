#!/bin/bash
# SESAME: start joycond-cemuhook for Dolphin Joy-Con motion.
# Default cemuhook port is 26760 (same as SteamDeckGyroDSU) — we ALWAYS use 26761
# so Deck gyro and Joy-Cons do not fight. Dolphin DSUClient.ini is pointed here by
# sesame-dolphin-cfg.py.
#
# Device names from joycond-cemuhook (must match Dolphin DSUClient):
#   Nintendo Switch Right Joy-Con / Nintendo Switch Left Joy-Con
# Pair each Joy-Con with SL+SR (not L+R combined). Steam Input Off on the Wii shortcut.

export HOME="${HOME:-/home/deck}"
PORT="${SESAME_JOYCON_DSU_PORT:-26761}"
ROOT="$HOME/.local/share/sesame"
LOG="$ROOT/joycon-dsu.log"
PIDFILE="$ROOT/joycon-dsu.pid"
STATUS="$ROOT/joycon-dsu.status"
PORTFILE="$ROOT/joycon-dsu.port"
INSTALL="$ROOT/install-joycond.sh"
mkdir -p "$ROOT" "$HOME/.local/bin"
export PATH="$HOME/.local/bin:$PATH"

log() { echo "$(date -Iseconds) $*" | tee -a "$LOG"; }
set_status() { echo "$1" >"$STATUS"; }
set_port() { echo "$1" >"$PORTFILE"; }

joycond_ready() {
  systemctl is-active --quiet joycond 2>/dev/null
}

port_listening() {
  local p="$1"
  if command -v ss >/dev/null 2>&1; then
    ss -ulnH 2>/dev/null | grep -qE ":${p}\\b" && return 0
  fi
  python3 - "$p" <<'PY' 2>/dev/null
import socket, sys
p=int(sys.argv[1])
s=socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
try:
    s.bind(("127.0.0.1", p))
except OSError:
    # bind failed → something already listens (good for us)
    sys.exit(0)
else:
    s.close()
    sys.exit(1)
PY
}

cemuhook_pids() {
  pgrep -f 'joycond_cemuhook|joycond-cemuhook' 2>/dev/null || true
}

stop_cemuhook() {
  local pids
  pids=$(cemuhook_pids)
  if [ -n "$pids" ]; then
    log "Stopping old cemuhook: $pids"
    # shellcheck disable=SC2086
    kill $pids 2>/dev/null || true
    sleep 0.4
    # shellcheck disable=SC2086
    kill -9 $pids 2>/dev/null || true
  fi
  rm -f "$PIDFILE"
}

ensure_pip() {
  python3 -m pip --version >/dev/null 2>&1 && return 0
  python3 -m ensurepip --user >>"$LOG" 2>&1 || true
  python3 -m pip --version >/dev/null 2>&1 && return 0
  curl -fsSL https://bootstrap.pypa.io/get-pip.py -o /tmp/sesame-get-pip.py >>"$LOG" 2>&1 || return 1
  python3 /tmp/sesame-get-pip.py --user --break-system-packages >>"$LOG" 2>&1 || return 1
  python3 -m pip --version >/dev/null 2>&1
}

find_cemuhook() {
  if command -v joycond-cemuhook >/dev/null 2>&1; then
    command -v joycond-cemuhook
    return 0
  fi
  local pybin
  pybin=$(python3 -c 'import sysconfig; print(sysconfig.get_path("scripts"))' 2>/dev/null || true)
  if [ -n "$pybin" ] && [ -x "$pybin/joycond-cemuhook" ]; then
    echo "$pybin/joycond-cemuhook"
    return 0
  fi
  if [ -x "$HOME/.local/bin/joycond-cemuhook" ]; then
    echo "$HOME/.local/bin/joycond-cemuhook"
    return 0
  fi
  if python3 -c 'import joycond_cemuhook' >/dev/null 2>&1; then
    echo "python3-module"
    return 0
  fi
  return 1
}

install_cemuhook() {
  ensure_pip || return 1
  log "pip install joycond-cemuhook --user --no-deps"
  python3 -m pip install --user --upgrade --break-system-packages --no-deps \
    "git+https://github.com/joaorb64/joycond-cemuhook" >>"$LOG" 2>&1 || return 1
  python3 -m pip install --user --upgrade --break-system-packages termcolor >>"$LOG" 2>&1 || true
}

joycon_evdev_count() {
  python3 - <<'PY' 2>/dev/null || echo 0
import evdev
names=("Nintendo Switch Left Joy-Con","Nintendo Switch Right Joy-Con",
       "Nintendo Switch Combined Joy-Cons","Joy-Con (L)","Joy-Con (R)")
n=0
for path in evdev.list_devices():
    try:
        d=evdev.InputDevice(path)
    except Exception:
        continue
    if any(d.name.startswith(x) or d.name==x for x in names):
        n+=1
print(n)
PY
}

start_cemuhook() {
  local bin="$1"
  stop_cemuhook
  modprobe hid_nintendo 2>/dev/null || true

  # Always bind 26761 — never default 26760 (SteamDeckGyroDSU).
  if [ "$bin" = "python3-module" ]; then
    nohup python3 -m joycond_cemuhook -ip 127.0.0.1 -p "$PORT" >>"$LOG" 2>&1 &
  else
    nohup "$bin" -ip 127.0.0.1 -p "$PORT" >>"$LOG" 2>&1 &
  fi
  echo $! >"$PIDFILE"
  sleep 1.2

  if ! kill -0 "$(cat "$PIDFILE" 2>/dev/null)" 2>/dev/null; then
    log "cemuhook process died"
    return 1
  fi
  if ! port_listening "$PORT"; then
    log "WARN: UDP $PORT not detected yet (cemuhook still starting?)"
  fi
  set_port "$PORT"
  log "cemuhook listening intent=$PORT pid=$(cat "$PIDFILE") joycons=$(joycon_evdev_count)"
  return 0
}

main() {
  modprobe hid_nintendo 2>/dev/null || true

  if ! joycond_ready; then
    log "joycond.service not active"
    set_status "need-install"
    [ -f "$INSTALL" ] && log "run: bash $INSTALL"
    exit 0
  fi

  local count
  count=$(joycon_evdev_count)
  if [ "$count" = "0" ]; then
    log "No Nintendo Switch Joy-Con evdev devices — pair with SL+SR each (not L+R)"
  else
    log "Found $count joycond-compatible device(s)"
  fi

  # Restart if already running so we always own 26761 (not 26760).
  local bin=""
  bin=$(find_cemuhook) || true
  if [ -z "$bin" ]; then
    install_cemuhook || true
    bin=$(find_cemuhook) || true
  fi
  if [ -z "$bin" ]; then
    log "cemuhook missing"
    set_status "no-cemuhook"
    exit 0
  fi

  if start_cemuhook "$bin"; then
    set_status "ok"
  else
    set_status "cemuhook-failed"
  fi
}

main "$@"
