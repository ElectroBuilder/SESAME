#!/bin/bash
# SESAME: start Joy-Con DSU (joycond-cemuhook) for Dolphin motion.
# Guide: https://system-maid.neocities.org/post/joycond-cemuhook/
#
# SteamDeckGyroDSU often already owns UDP 26760 (Deck IMU only).
# Joy-Con motion uses 26761 so both can coexist.
# Dolphin DSUClient: Server1=joycond@26761, Server2=steamdeckgyro@26760.

set -e
export HOME="${HOME:-/home/deck}"
PORT="${SESAME_JOYCON_DSU_PORT:-26761}"
LOG="$HOME/.local/share/sesame/joycon-dsu.log"
PIDFILE="$HOME/.local/share/sesame/joycon-dsu.pid"
mkdir -p "$(dirname "$LOG")"

log() { echo "$(date -Iseconds) $*" >>"$LOG"; }

already_running() {
  if [ -f "$PIDFILE" ]; then
    local old
    old=$(cat "$PIDFILE" 2>/dev/null || true)
    if [ -n "$old" ] && kill -0 "$old" 2>/dev/null; then
      return 0
    fi
  fi
  # Any joycond-cemuhook process is enough
  pgrep -f 'joycond.cemuhook|joycond-cemuhook' >/dev/null 2>&1
}

port_busy() {
  ss -uln 2>/dev/null | grep -q ":$PORT " || \
    lsof -iUDP:"$PORT" >/dev/null 2>&1
}

ensure_hid() {
  if ! modinfo hid_nintendo >/dev/null 2>&1; then
    log "hid_nintendo module info missing (may still be built-in)"
  fi
  modprobe hid_nintendo 2>/dev/null || true
}

ensure_joycond() {
  if systemctl is-active --quiet joycond 2>/dev/null; then
    return 0
  fi
  if systemctl enable --now joycond >/dev/null 2>&1; then
    log "started joycond via systemctl"
    return 0
  fi
  if command -v joycond >/dev/null 2>&1; then
    log "joycond binary present but service not running (needs: sudo systemctl enable --now joycond)"
  else
    log "joycond not installed — one-time Deck setup required (see SESAME Optimize hint)"
  fi
  return 1
}

find_cemuhook() {
  if command -v joycond-cemuhook >/dev/null 2>&1; then
    command -v joycond-cemuhook
    return 0
  fi
  # pip --user installs
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
  return 1
}

install_cemuhook() {
  log "installing joycond-cemuhook via pip --user"
  python3 -m pip install --user --upgrade \
    "git+https://github.com/joaorb64/joycond-cemuhook" >>"$LOG" 2>&1 || \
  python3 -m pip install --user --upgrade joycond-cemuhook >>"$LOG" 2>&1 || true
}

start_cemuhook() {
  local bin="$1"
  # Prefer a free dedicated port; fall back if the tool ignores --port.
  nohup "$bin" --port "$PORT" >>"$LOG" 2>&1 &
  echo $! >"$PIDFILE"
  sleep 0.4
  if kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then
    log "started $bin --port $PORT pid=$(cat "$PIDFILE")"
    return 0
  fi
  # Older builds: no --port flag
  nohup "$bin" >>"$LOG" 2>&1 &
  echo $! >"$PIDFILE"
  sleep 0.4
  if kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then
    log "started $bin (default port) pid=$(cat "$PIDFILE")"
    return 0
  fi
  log "failed to start $bin"
  return 1
}

main() {
  if already_running; then
    log "joycond-cemuhook already running"
    exit 0
  fi

  ensure_hid
  ensure_joycond || true

  local bin=""
  bin=$(find_cemuhook) || true
  if [ -z "$bin" ]; then
    install_cemuhook
    bin=$(find_cemuhook) || true
  fi

  if [ -z "$bin" ]; then
    log "joycond-cemuhook not available after pip attempt"
    exit 0
  fi

  if port_busy; then
    log "UDP $PORT busy — still starting cemuhook (may share/fail)"
  fi

  start_cemuhook "$bin" || true
}

main "$@"
