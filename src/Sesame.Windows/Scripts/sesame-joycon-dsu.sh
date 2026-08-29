#!/bin/bash
# SESAME: start joycond-cemuhook for Dolphin Joy-Con motion.
# Default cemuhook port is 26760 (same as SteamDeckGyroDSU) — we ALWAYS use 26761
# so Deck gyro and Joy-Cons do not fight. Dolphin DSUClient.ini is pointed here by
# sesame-dolphin-cfg.py.
#
# Preferred pairing for single-player Wiimote+Nunchuk (Wii Sports, etc.):
#   Press L+R together → "Nintendo Switch Combined Joy-Cons" (one Steam player).
#   cemuhook is started with -r/--right-only so the Right IMU drives Combined motion.
# Separate SL+SR L/R still works as a fallback (Wiimote=Right, Nunchuk=Left).
# Steam Input Off on the Wii shortcut.

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

ensure_hid_nintendo() {
  # Soft: only load if not already present — avoids Bluetooth churn on every launch.
  if lsmod 2>/dev/null | grep -q '^hid_nintendo'; then
    return 0
  fi
  if [ -d /sys/module/hid_nintendo ]; then
    return 0
  fi
  modprobe hid_nintendo 2>/dev/null || true
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

# Pad inventory: echoes "total|combined|left|right|summary"
joycon_inventory() {
  python3 - <<'PY' 2>/dev/null || echo "0|0|0|0|none"
import evdev
left_names=("Nintendo Switch Left Joy-Con","Joy-Con (L)")
right_names=("Nintendo Switch Right Joy-Con","Joy-Con (R)")
combined=left=right=0
parts=[]
for path in evdev.list_devices():
    try:
        d=evdev.InputDevice(path)
    except Exception:
        continue
    name=d.name or ""
    low=name.lower()
    if "imu" in low:
        continue
    if ("combined" in low and ("joy" in low or "switch" in low)) or "joycon-pair" in low or "joy-con pair" in low:
        combined+=1; parts.append("C:"+name); continue
    if any(name.startswith(x) or name==x for x in left_names) or (
        (("(l)" in low or " left" in low or low.endswith(" left joy-con")) and "joy" in low)
    ):
        left+=1; parts.append("L:"+name); continue
    if any(name.startswith(x) or name==x for x in right_names) or (
        (("(r)" in low or " right" in low or low.endswith(" right joy-con")) and "joy" in low)
    ):
        right+=1; parts.append("R:"+name); continue
total=combined+left+right
summary="; ".join(parts) if parts else "none"
print("%d|%d|%d|%d|%s" % (total, combined, left, right, summary))
PY
}

joycon_pad_count() {
  local inv total
  inv=$(joycon_inventory)
  total="${inv%%|*}"
  echo "${total:-0}"
}

mark_status_from_health() {
  local pid_ok=0
  local pads
  pads=$(joycon_pad_count)
  if [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE" 2>/dev/null)" 2>/dev/null; then
    pid_ok=1
  elif [ -n "$(cemuhook_pids)" ]; then
    pid_ok=1
  fi
  if [ "$pid_ok" = "1" ] && [ "${pads:-0}" -gt 0 ] 2>/dev/null; then
    set_status "ok"
    set_port "$PORT"
    log "status=ok pads=$pads port=$PORT"
    return 0
  fi
  if [ "$pid_ok" = "1" ]; then
    set_status "no-pads"
    set_port "$PORT"
    log "status=no-pads (cemuhook alive but zero Combined/L/R pads) — Dolphin will not hardcode DSU Device"
    return 1
  fi
  set_status "cemuhook-failed"
  log "status=cemuhook-failed"
  return 1
}

cemuhook_healthy_on_port() {
  # Already listening on our port + has pads → keep it (no kill/restart).
  local pads
  if ! port_listening "$PORT"; then
    return 1
  fi
  if [ -z "$(cemuhook_pids)" ]; then
    return 1
  fi
  pads=$(joycon_pad_count)
  [ "${pads:-0}" -gt 0 ] 2>/dev/null
}

start_cemuhook() {
  local bin="$1"
  ensure_hid_nintendo

  # -r / --right-only: Right Joy-Con IMU as motion for Combined (official path).
  if [ "$bin" = "python3-module" ]; then
    nohup python3 -m joycond_cemuhook -ip 127.0.0.1 -p "$PORT" -r >>"$LOG" 2>&1 &
  else
    nohup "$bin" -ip 127.0.0.1 -p "$PORT" -r >>"$LOG" 2>&1 &
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
  log "cemuhook started port=$PORT pid=$(cat "$PIDFILE") -r (right IMU)"
  return 0
}

main() {
  ensure_hid_nintendo

  if ! joycond_ready; then
    log "joycond.service not active"
    set_status "need-install"
    [ -f "$INSTALL" ] && log "run: bash $INSTALL"
    exit 0
  fi

  local inv total combined left right summary
  inv=$(joycon_inventory)
  total=$(echo "$inv" | cut -d'|' -f1)
  combined=$(echo "$inv" | cut -d'|' -f2)
  left=$(echo "$inv" | cut -d'|' -f3)
  right=$(echo "$inv" | cut -d'|' -f4)
  summary=$(echo "$inv" | cut -d'|' -f5-)
  log "evdev Joy-Con pads: total=$total combined=$combined left=$left right=$right ($summary)"
  if [ "${combined:-0}" -gt 0 ] 2>/dev/null; then
    log "Preferred mode: Combined (L+R) — one player, Wiimote+Nunchuk on one Device"
  elif [ "${left:-0}" -gt 0 ] 2>/dev/null && [ "${right:-0}" -gt 0 ] 2>/dev/null; then
    log "Mode: separate L+R (press L+R to combine for simpler Wiimote+Nunchuk)"
  elif [ "${total:-0}" = "0" ]; then
    log "No Combined/L/R Joy-Con pads — pair BT, then L+R (Combined) or SL+SR each"
  fi

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

  if cemuhook_healthy_on_port; then
    log "cemuhook already healthy on $PORT — not restarting"
    mark_status_from_health || true
    exit 0
  fi

  # Wrong port / dead / no pads while process hung → soft restart once.
  if [ -n "$(cemuhook_pids)" ]; then
    log "cemuhook present but not healthy on $PORT — restarting once"
    stop_cemuhook
  fi

  if start_cemuhook "$bin"; then
    mark_status_from_health || true
  else
    set_status "cemuhook-failed"
  fi
}

main "$@"
