#!/bin/bash
# SESAME: one-time Joy-Con motion stack for Steam Deck (Desktop Mode).
# Do NOT run "systemctl enable joycond" until this script has installed joycond —
# SteamOS does not ship joycond.service by default.
#
# Based on: https://system-maid.neocities.org/post/joycond-cemuhook/
#           https://github.com/DanielOgorchock/joycond/issues/102

set -e
export HOME="${HOME:-/home/deck}"
export PATH="$HOME/.local/bin:/usr/bin:/bin:$PATH"
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

have_cmake() { command -v cmake >/dev/null 2>&1; }
have_make() { command -v make >/dev/null 2>&1; }
have_git() { command -v git >/dev/null 2>&1; }

ensure_pip() {
  python3 -m ensurepip --user 2>>"$LOG" || true
  if ! python3 -m pip --version >/dev/null 2>&1; then
    curl -fsSL https://bootstrap.pypa.io/get-pip.py -o /tmp/sesame-get-pip.py
    python3 /tmp/sesame-get-pip.py --user >>"$LOG" 2>&1 || true
  fi
}

ensure_cmake() {
  if have_cmake; then
    log "cmake: $(command -v cmake)"
    return 0
  fi
  log "cmake missing — installing via pip --user (no pacman needed)"
  ensure_pip
  python3 -m pip install --user --upgrade cmake ninja >>"$LOG" 2>&1 || true
  export PATH="$HOME/.local/bin:$PATH"
  # pip cmake often lands as ~/.local/bin/cmake
  if have_cmake; then
    log "cmake via pip: $(command -v cmake)"
    return 0
  fi
  log "ERROR: still no cmake. Install Desktop Mode build tools or: python3 -m pip install --user cmake"
  return 1
}

install_deps() {
  log "Installing build deps (best-effort, noninteractive on SteamOS)…"
  sudo steamos-readonly disable 2>>"$LOG" || true

  # Avoid interactive "Import PGP key … ?" prompts that abort pacman.
  if command -v pacman >/dev/null 2>&1; then
    sudo pacman-key --init 2>>"$LOG" || true
    sudo pacman-key --populate archlinux holo 2>>"$LOG" || true
    # Common Arch packager key that blocked the previous run
    sudo pacman-key --recv-keys AF1D2199EF0A3CCF 2>>"$LOG" || true
    sudo pacman-key --lsign-key AF1D2199EF0A3CCF 2>>"$LOG" || true
    # --noconfirm + yes pipe; ignore total failure and fall back to pip cmake
    set +e
    yes | sudo pacman -S --needed --noconfirm \
      base-devel cmake libevdev git python python-pip 2>>"$LOG"
    pacman_rc=$?
    set -e
    if [ "$pacman_rc" -ne 0 ]; then
      log "pacman deps incomplete (exit $pacman_rc) — will try pip cmake / existing tools"
    fi
  fi

  # libevdev headers help joycond build; soft fail if missing
  if [ ! -f /usr/include/libevdev-1.0/libevdev/libevdev.h ] && \
     [ ! -f /usr/include/libevdev/libevdev.h ]; then
    log "WARN: libevdev headers not found — joycond build may fail until pacman -S libevdev succeeds"
  fi
}

build_joycond() {
  ensure_cmake || exit 1
  if ! have_make; then
    log "ERROR: make not found. Run in Desktop Mode and install base-devel, or retry after pacman works."
    echo "need-make" >"$STATUS"
    exit 1
  fi
  if ! have_git; then
    log "ERROR: git not found"
    echo "need-git" >"$STATUS"
    exit 1
  fi

  if [ ! -d "$SRC/joycond/.git" ]; then
    log "Cloning joycond…"
    git clone --depth 1 https://github.com/DanielOgorchock/joycond.git "$SRC/joycond"
  else
    log "Updating joycond clone…"
    git -C "$SRC/joycond" pull --ff-only 2>>"$LOG" || true
  fi
  cd "$SRC/joycond"
  rm -rf build
  mkdir -p build
  cd build
  log "Building joycond…"
  cmake ..
  make -j"$(nproc 2>/dev/null || echo 2)"
  log "Installing joycond (binary + udev + systemd unit)…"
  sudo make install
  sudo udevadm control --reload-rules 2>>"$LOG" || true
  sudo udevadm trigger 2>>"$LOG" || true
}

start_joycond() {
  if [ ! -f /usr/lib/systemd/system/joycond.service ] && \
     [ ! -f /etc/systemd/system/joycond.service ] && \
     [ ! -f /lib/systemd/system/joycond.service ] && \
     [ ! -f /usr/local/lib/systemd/system/joycond.service ]; then
    log "ERROR: joycond.service still missing after make install"
    echo "need-install" >"$STATUS"
    exit 1
  fi
  sudo systemctl daemon-reload 2>>"$LOG" || true
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
  log "Installing joycond-cemuhook (python3 -m pip --user)…"
  ensure_pip
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
