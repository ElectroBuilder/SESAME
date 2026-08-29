#!/bin/bash
# SESAME: start Joy-Con DSU (joycond-cemuhook) for Dolphin.
# Never "systemctl enable" a missing unit — that only works AFTER install-joycond.sh.
# SteamDeckGyroDSU = 26760 (Deck). Joy-Cons = 26761.

export HOME="${HOME:-/home/deck}"
PORT="${SESAME_JOYCON_DSU_PORT:-26761}"
ROOT="$HOME/.local/share/sesame"
LOG="$ROOT/joycon-dsu.log"
PIDFILE="$ROOT/joycon-dsu.pid"
STATUS="$ROOT/joycon-dsu.status"
INSTALL="$ROOT/install-joycond.sh"
mkdir -p "$ROOT" "$HOME/.local/bin"
export PATH="$HOME/.local/bin:$PATH"

log() { echo "$(date -Iseconds) $*" >>"$LOG"; }

set_status() { echo "$1" >"$STATUS"; }

joycond_ready() {
  if systemctl is-active --quiet joycond 2>/dev/null; then
    return 0
  fi
  # Only treat as ready if the systemd unit exists and is active — a random
  # user binary without udev is not enough for cemuhook.
  if [ -f /usr/lib/systemd/system/joycond.service ] || \
     [ -f /etc/systemd/system/joycond.service ] || \
     [ -f /lib/systemd/system/joycond.service ]; then
    return 1
  fi
  return 1
}

cemuhook_running() {
  if [ -f "$PIDFILE" ]; then
    local old
    old=$(cat "$PIDFILE" 2>/dev/null || true)
    if [ -n "$old" ] && kill -0 "$old" 2>/dev/null; then
      return 0
    fi
  fi
  pgrep -f 'joycond_cemuhook|joycond-cemuhook' >/dev/null 2>&1
}

ensure_pip() {
  if python3 -m pip --version >/dev/null 2>&1; then
    return 0
  fi
  log "pip missing — ensurepip / get-pip (Deck has no pip3)"
  python3 -m ensurepip --user >>"$LOG" 2>&1 || true
  if python3 -m pip --version >/dev/null 2>&1; then
    return 0
  fi
  curl -fsSL https://bootstrap.pypa.io/get-pip.py -o /tmp/sesame-get-pip.py >>"$LOG" 2>&1 || return 1
  python3 /tmp/sesame-get-pip.py --user >>"$LOG" 2>&1 || return 1
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
  ensure_pip || {
    log "cannot install cemuhook without pip"
    return 1
  }
  log "pip install joycond-cemuhook --user"
  python3 -m pip install --user --upgrade --break-system-packages \
    "git+https://github.com/joaorb64/joycond-cemuhook" >>"$LOG" 2>&1 || return 1
}

start_cemuhook() {
  local bin="$1"
  if [ "$bin" = "python3-module" ]; then
    nohup python3 -m joycond_cemuhook --port "$PORT" >>"$LOG" 2>&1 &
  else
    nohup "$bin" --port "$PORT" >>"$LOG" 2>&1 &
  fi
  echo $! >"$PIDFILE"
  sleep 0.6
  if kill -0 "$(cat "$PIDFILE" 2>/dev/null)" 2>/dev/null; then
    log "cemuhook on $PORT pid=$(cat "$PIDFILE")"
    return 0
  fi
  # Older entrypoints ignore --port
  if [ "$bin" = "python3-module" ]; then
    nohup python3 -m joycond_cemuhook >>"$LOG" 2>&1 &
  else
    nohup "$bin" >>"$LOG" 2>&1 &
  fi
  echo $! >"$PIDFILE"
  sleep 0.6
  kill -0 "$(cat "$PIDFILE" 2>/dev/null)" 2>/dev/null
}

main() {
  modprobe hid_nintendo 2>/dev/null || true

  if ! joycond_ready; then
    log "joycond.service not active (not installed on Steam Deck by default)"
    set_status "need-install"
    # Keep install script ready; do not pretend systemctl enable will work.
    if [ -f "$INSTALL" ]; then
      log "run once in Desktop Mode: bash $INSTALL"
    fi
    exit 0
  fi

  if cemuhook_running; then
    log "cemuhook already running"
    set_status "ok"
    exit 0
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

  if start_cemuhook "$bin"; then
    set_status "ok"
  else
    set_status "cemuhook-failed"
  fi
}

main "$@"
