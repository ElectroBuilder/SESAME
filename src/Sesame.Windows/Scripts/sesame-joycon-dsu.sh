#!/bin/bash
# SESAME: start Joy-Con DSU (joycond-cemuhook) for Dolphin motion.
# Guide: https://system-maid.neocities.org/post/joycond-cemuhook/
#
# Steam Deck notes:
# - No apt; pacman exists but joycond is usually NOT packaged → build from source once.
# - python3 often has no pip3 → use ensurepip / get-pip, then python3 -m pip --user.
# SteamDeckGyroDSU often owns UDP 26760 (Deck IMU). Joy-Cons use 26761.

export HOME="${HOME:-/home/deck}"
PORT="${SESAME_JOYCON_DSU_PORT:-26761}"
ROOT="$HOME/.local/share/sesame"
SRC="$ROOT/src"
LOG="$ROOT/joycon-dsu.log"
PIDFILE="$ROOT/joycon-dsu.pid"
mkdir -p "$ROOT" "$SRC" "$HOME/.local/bin"

log() { echo "$(date -Iseconds) $*" >>"$LOG"; }

already_running() {
  if [ -f "$PIDFILE" ]; then
    local old
    old=$(cat "$PIDFILE" 2>/dev/null || true)
    if [ -n "$old" ] && kill -0 "$old" 2>/dev/null; then
      return 0
    fi
  fi
  pgrep -f 'joycond.cemuhook|joycond-cemuhook' >/dev/null 2>&1
}

ensure_hid() {
  modprobe hid_nintendo 2>/dev/null || true
  if modinfo hid_nintendo >/dev/null 2>&1; then
    log "hid_nintendo present"
  else
    log "hid_nintendo missing — SteamOS usually has it built-in; if Joy-Cons fail, check kernel modules"
  fi
}

ensure_pip() {
  if python3 -m pip --version >/dev/null 2>&1; then
    return 0
  fi
  log "pip missing — trying ensurepip / get-pip (Steam Deck has no pip3 by default)"
  python3 -m ensurepip --user >>"$LOG" 2>&1 || true
  if python3 -m pip --version >/dev/null 2>&1; then
    return 0
  fi
  curl -fsSL https://bootstrap.pypa.io/get-pip.py -o /tmp/sesame-get-pip.py >>"$LOG" 2>&1 || true
  python3 /tmp/sesame-get-pip.py --user >>"$LOG" 2>&1 || true
  python3 -m pip --version >/dev/null 2>&1
}

ensure_joycond() {
  if systemctl is-active --quiet joycond 2>/dev/null; then
    log "joycond service active"
    return 0
  fi
  if command -v joycond >/dev/null 2>&1 || [ -x "$HOME/.local/bin/joycond" ]; then
    # User-built binary: try starting without systemd (Steam Deck often has no joycond.service)
    if pgrep -x joycond >/dev/null 2>&1; then
      log "joycond process running"
      return 0
    fi
    nohup "$HOME/.local/bin/joycond" >>"$LOG" 2>&1 &
    sleep 0.3
    if pgrep -x joycond >/dev/null 2>&1; then
      log "started user joycond"
      return 0
    fi
    log "joycond binary exists but could not start (may need udev/sudo once)"
    return 1
  fi

  # One-time build into ~/.local (no pacman package on SteamOS).
  if [ ! -d "$SRC/joycond/.git" ]; then
    log "cloning joycond (not in pacman on Steam Deck)"
    git clone --depth 1 https://github.com/DanielOgorchock/joycond.git "$SRC/joycond" >>"$LOG" 2>&1 || {
      log "git clone joycond failed"
      return 1
    }
  fi
  if [ ! -x "$HOME/.local/bin/joycond" ]; then
    log "building joycond into ~/.local"
    (
      cd "$SRC/joycond" || exit 1
      cmake -DCMAKE_INSTALL_PREFIX="$HOME/.local" . >>"$LOG" 2>&1
      make -j"$(nproc 2>/dev/null || echo 2)" >>"$LOG" 2>&1
      make install >>"$LOG" 2>&1
    ) || log "joycond build/install failed (need cmake, make, libevdev headers)"
  fi

  if [ -x "$HOME/.local/bin/joycond" ]; then
    export PATH="$HOME/.local/bin:$PATH"
    nohup "$HOME/.local/bin/joycond" >>"$LOG" 2>&1 &
    sleep 0.3
    if pgrep -x joycond >/dev/null 2>&1; then
      log "started freshly built joycond"
      return 0
    fi
  fi

  if systemctl enable --now joycond >/dev/null 2>&1; then
    log "started joycond via systemctl"
    return 0
  fi
  log "joycond.service missing — expected on Steam Deck until built once; see $LOG"
  return 1
}

find_cemuhook() {
  export PATH="$HOME/.local/bin:$PATH"
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
  # Module form
  if python3 -c 'import joycond_cemuhook' >/dev/null 2>&1 || \
     python3 -c 'import importlib; importlib.import_module("joycond.cemuhook")' >/dev/null 2>&1; then
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
  log "installing joycond-cemuhook via python3 -m pip --user"
  python3 -m pip install --user --upgrade \
    "git+https://github.com/joaorb64/joycond-cemuhook" >>"$LOG" 2>&1 || \
  python3 -m pip install --user --upgrade joycond-cemuhook >>"$LOG" 2>&1 || true
}

start_cemuhook() {
  local bin="$1"
  export PATH="$HOME/.local/bin:$PATH"
  if [ "$bin" = "python3-module" ]; then
    nohup python3 -m joycond_cemuhook --port "$PORT" >>"$LOG" 2>&1 &
    echo $! >"$PIDFILE"
  else
    nohup "$bin" --port "$PORT" >>"$LOG" 2>&1 &
    echo $! >"$PIDFILE"
  fi
  sleep 0.5
  if kill -0 "$(cat "$PIDFILE" 2>/dev/null)" 2>/dev/null; then
    log "started cemuhook on $PORT pid=$(cat "$PIDFILE")"
    return 0
  fi
  # Older builds: no --port
  if [ "$bin" = "python3-module" ]; then
    nohup python3 -m joycond_cemuhook >>"$LOG" 2>&1 &
  else
    nohup "$bin" >>"$LOG" 2>&1 &
  fi
  echo $! >"$PIDFILE"
  sleep 0.5
  if kill -0 "$(cat "$PIDFILE" 2>/dev/null)" 2>/dev/null; then
    log "started cemuhook (default port) pid=$(cat "$PIDFILE")"
    return 0
  fi
  log "failed to start cemuhook"
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
    log "joycond-cemuhook still unavailable — check $LOG"
    exit 0
  fi

  start_cemuhook "$bin" || true
}

main "$@"
