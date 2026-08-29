#!/bin/bash
# SESAME: one-time Joy-Con motion stack for Steam Deck (Desktop Mode).
# Do NOT run "systemctl enable joycond" until this script has installed joycond —
# SteamOS does not ship joycond.service by default.
#
# Based on: https://system-maid.neocities.org/post/joycond-cemuhook/
#           https://github.com/DanielOgorchock/joycond/issues/102

set -e
export HOME="${HOME:-/home/deck}"
ROOT="$HOME/.local/share/sesame"
SRC="$ROOT/src"
LOG="$ROOT/install-joycond.log"
STATUS="$ROOT/joycon-dsu.status"
mkdir -p "$ROOT" "$SRC" "$HOME/.local/bin"

log() { echo "$(date -Iseconds) $*" | tee -a "$LOG"; }

need_sudo() {
  if [ "$(id -u)" -eq 0 ]; then return 0; fi
  if ! command -v sudo >/dev/null 2>&1; then
    log "ERROR: sudo required once to install joycond system-wide"
    exit 1
  fi
}

install_deps() {
  log "Installing build deps (best-effort on SteamOS)…"
  # SteamOS is read-only until unlocked; ignore failures and continue with what we have.
  sudo steamos-readonly disable 2>>"$LOG" || true
  sudo pacman-key --init 2>>"$LOG" || true
  sudo pacman-key --populate archlinux 2>>"$LOG" || true
  sudo pacman -S --needed --noconfirm base-devel cmake libevdev git python python-pip 2>>"$LOG" || \
    log "pacman deps incomplete — continuing if cmake/make/git already exist"
}

build_joycond() {
  if [ ! -d "$SRC/joycond/.git" ]; then
    log "Cloning joycond…"
    git clone --depth 1 https://github.com/DanielOgorchock/joycond.git "$SRC/joycond"
  fi
  cd "$SRC/joycond"
  log "Building joycond…"
  cmake .
  make -j"$(nproc 2>/dev/null || echo 2)"
  log "Installing joycond (binary + udev + systemd unit)…"
  sudo make install
  sudo udevadm control --reload-rules 2>>"$LOG" || true
  sudo udevadm trigger 2>>"$LOG" || true
}

start_joycond() {
  if [ ! -f /usr/lib/systemd/system/joycond.service ] && \
     [ ! -f /etc/systemd/system/joycond.service ] && \
     [ ! -f /lib/systemd/system/joycond.service ]; then
    log "ERROR: joycond.service still missing after make install"
    echo "need-install" >"$STATUS"
    exit 1
  fi
  sudo systemctl enable --now joycond
  if systemctl is-active --quiet joycond; then
    log "joycond.service is active"
  else
    log "ERROR: joycond.service failed to start — see: systemctl status joycond"
    echo "joycond-failed" >"$STATUS"
    exit 1
  fi
}

install_cemuhook() {
  log "Installing joycond-cemuhook (python3 -m pip --user; no pip3 required)…"
  python3 -m ensurepip --user 2>>"$LOG" || true
  if ! python3 -m pip --version >/dev/null 2>&1; then
    curl -fsSL https://bootstrap.pypa.io/get-pip.py -o /tmp/sesame-get-pip.py
    python3 /tmp/sesame-get-pip.py --user >>"$LOG" 2>&1
  fi
  python3 -m pip install --user --upgrade \
    "git+https://github.com/joaorb64/joycond-cemuhook" >>"$LOG" 2>&1
  export PATH="$HOME/.local/bin:$PATH"
  if command -v joycond-cemuhook >/dev/null 2>&1 || \
     python3 -c 'import joycond_cemuhook' >/dev/null 2>&1; then
    log "joycond-cemuhook OK"
  else
    log "WARN: cemuhook import failed — check $LOG"
  fi
}

main() {
  log "=== SESAME joycond install ==="
  need_sudo
  install_deps
  build_joycond
  start_joycond
  install_cemuhook
  # Start DSU helper if present
  if [ -x "$HOME/Emulation/tools/launchers/sesame-joycon-dsu.sh" ]; then
    bash "$HOME/Emulation/tools/launchers/sesame-joycon-dsu.sh" >>"$LOG" 2>&1 || true
  elif [ -x "$HOME/.local/share/sesame/sesame-joycon-dsu.sh" ]; then
    bash "$HOME/.local/share/sesame/sesame-joycon-dsu.sh" >>"$LOG" 2>&1 || true
  fi
  echo "ok" >"$STATUS"
  log "Done. Pair Joy-Cons, press SL+SR on each, then launch a Wii game via SESAME."
  echo ""
  echo "Installed. Pair Joy-Cons (SL+SR each), Steam Input Off on the Wii shortcut, then Optimize again."
}

main "$@"
