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
have_ninja() { command -v ninja >/dev/null 2>&1; }
have_git() { command -v git >/dev/null 2>&1; }
have_cc() { command -v gcc >/dev/null 2>&1 || command -v cc >/dev/null 2>&1; }

ensure_pip() {
  python3 -m ensurepip --user 2>>"$LOG" || true
  if ! python3 -m pip --version >/dev/null 2>&1; then
    curl -fsSL https://bootstrap.pypa.io/get-pip.py -o /tmp/sesame-get-pip.py
    python3 /tmp/sesame-get-pip.py --user >>"$LOG" 2>&1 || true
  fi
}

ensure_cmake() {
  export PATH="$HOME/.local/bin:$PATH"
  if have_cmake; then
    log "cmake: $(command -v cmake)"
    return 0
  fi
  log "cmake missing — installing via pip --user (no pacman needed)"
  ensure_pip
  python3 -m pip install --user --upgrade cmake ninja >>"$LOG" 2>&1 || true
  export PATH="$HOME/.local/bin:$PATH"
  if have_cmake; then
    log "cmake via pip: $(command -v cmake)"
    return 0
  fi
  log "ERROR: still no cmake. Try: python3 -m pip install --user cmake"
  return 1
}

ensure_ninja() {
  export PATH="$HOME/.local/bin:$PATH"
  if have_ninja; then return 0; fi
  ensure_pip
  python3 -m pip install --user --upgrade ninja >>"$LOG" 2>&1 || true
  export PATH="$HOME/.local/bin:$PATH"
  have_ninja
}

trust_pacman_keys() {
  # The previous failure was interactive "Import PGP key AF1D2199EF0A3CCF?" then abort.
  sudo pacman-key --init 2>>"$LOG" || true
  sudo pacman-key --populate archlinux 2>>"$LOG" || true
  sudo pacman-key --populate holo 2>>"$LOG" || true
  # Lukas Fleischer (and refresh keyring packages when possible)
  for key in AF1D2199EF0A3CCF 3E80319D7E3D1BD6; do
    sudo pacman-key --recv-keys "$key" 2>>"$LOG" || \
      sudo pacman-key --recv-keys --keyserver keyserver.ubuntu.com "$key" 2>>"$LOG" || true
    sudo pacman-key --lsign-key "$key" 2>>"$LOG" || true
  done
  set +e
  yes | sudo pacman -S --needed --noconfirm archlinux-keyring 2>>"$LOG"
  yes | sudo pacman -S --needed --noconfirm holo-keyring 2>>"$LOG"
  set -e
}

install_deps() {
  log "Installing build deps (best-effort, noninteractive on SteamOS)…"
  sudo steamos-readonly disable 2>>"$LOG" || true

  if command -v pacman >/dev/null 2>&1; then
    trust_pacman_keys
    set +e
    # --noconfirm answers PGP import prompts; yes pipe covers residual prompts
    yes | sudo pacman -S --needed --noconfirm \
      base-devel cmake ninja libevdev git python python-pip 2>>"$LOG"
    pacman_rc=$?
    set -e
    if [ "$pacman_rc" -ne 0 ]; then
      log "pacman deps incomplete (exit $pacman_rc) — will try pip cmake/ninja + existing tools"
    else
      log "pacman deps OK"
    fi
  fi

  if [ ! -f /usr/include/libevdev-1.0/libevdev/libevdev.h ] && \
     [ ! -f /usr/include/libevdev/libevdev.h ]; then
    log "WARN: libevdev headers missing — joycond will not compile until: sudo pacman -S libevdev"
  fi
  if ! have_cc; then
    log "WARN: no gcc/cc — need base-devel. Fix pacman keys, then re-run this script."
  fi
}

build_joycond() {
  ensure_cmake || exit 1
  if ! have_cc; then
    log "ERROR: no C compiler (gcc). Pacman keyring blocked base-devel."
    log "Fix: Desktop Mode → sudo pacman-key --lsign-key AF1D2199EF0A3CCF"
    log "Then: sudo pacman -S --needed --noconfirm base-devel libevdev"
    echo "need-compiler" >"$STATUS"
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

  # Prefer Ninja from pip when make/base-devel did not install.
  if ensure_ninja; then
    cmake -G Ninja ..
    ninja
    log "Installing joycond (binary + udev + systemd unit)…"
    sudo ninja install
  elif have_make; then
    cmake ..
    make -j"$(nproc 2>/dev/null || echo 2)"
    log "Installing joycond (binary + udev + systemd unit)…"
    sudo make install
  else
    log "ERROR: neither ninja nor make available after pip fallback"
    echo "need-make" >"$STATUS"
    exit 1
  fi

  sudo udevadm control --reload-rules 2>>"$LOG" || true
  sudo udevadm trigger 2>>"$LOG" || true
}

start_joycond() {
  if [ ! -f /usr/lib/systemd/system/joycond.service ] && \
     [ ! -f /etc/systemd/system/joycond.service ] && \
     [ ! -f /lib/systemd/system/joycond.service ] && \
     [ ! -f /usr/local/lib/systemd/system/joycond.service ]; then
    log "ERROR: joycond.service still missing after install"
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
