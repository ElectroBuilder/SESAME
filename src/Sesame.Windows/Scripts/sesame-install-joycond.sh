#!/bin/bash
# SESAME: one-shot Joy-Con motion stack for Steam Deck (Desktop Mode / Settings).
#   bash ~/.local/share/sesame/install-joycond.sh
#
# SteamOS ships stripped packages (no headers). This script restores missing
# headers from Arch packages, builds joycond, enables the service, and installs
# joycond-cemuhook for Dolphin DSU motion.
#
# If SUDO_ASKPASS is set, all privileged steps use "sudo -A" (SESAME remote install).

set -euo pipefail
export HOME="${HOME:-/home/deck}"
export PATH="$HOME/.local/bin:/usr/bin:/bin:$PATH"
export PKG_CONFIG_PATH="/usr/local/lib/pkgconfig:/usr/lib/pkgconfig:${PKG_CONFIG_PATH:-}"
ROOT="$HOME/.local/share/sesame"
SRC="$ROOT/src"
LOG="$ROOT/install-joycond.log"
STATUS="$ROOT/joycon-dsu.status"
mkdir -p "$ROOT" "$SRC" "$HOME/.local/bin" /tmp/sesame-pkgs

log() { echo "$(date -Iseconds) $*" | tee -a "$LOG"; }

sudocmd() {
  if [ -n "${SUDO_ASKPASS:-}" ] && [ -x "${SUDO_ASKPASS}" ]; then
    sudo -A "$@"
  else
    sudo "$@"
  fi
}

pac() {
  set +e
  yes | sudocmd pacman -S --needed --noconfirm "$@" >>"$LOG" 2>&1
  local r=$?
  set -e
  return $r
}

fetch() { curl -fL --retry 3 -o "$1" "$2" >>"$LOG" 2>&1; }

log "=== SESAME joycond install ==="
sudocmd steamos-readonly disable 2>>"$LOG" || true
sudocmd mkdir -p /usr/local/lib/pkgconfig /usr/local/include

if command -v pacman >/dev/null; then
  sudocmd pacman-key --init 2>>"$LOG" || true
  sudocmd pacman-key --populate archlinux 2>>"$LOG" || true
  sudocmd pacman-key --populate holo 2>>"$LOG" || true
  for key in AF1D2199EF0A3CCF 3E80319D7E3D1BD6; do
    sudocmd pacman-key --lsign-key "$key" 2>>"$LOG" || true
  done
  pac base-devel cmake ninja git python python-pip glibc linux-api-headers \
    libevdev systemd systemd-libs cairo python-cairo python-gobject python-pyudev || true
fi

if [ ! -f /usr/include/errno.h ]; then
  log "Restoring glibc headers…"
  fetch /tmp/glibc.pkg.tar.zst "https://archlinux.org/packages/core/x86_64/glibc/download/"
  sudocmd tar -C / -xf /tmp/glibc.pkg.tar.zst usr/include
fi
test -f /usr/include/errno.h
log "libc OK"

if [ ! -f /usr/include/linux/types.h ] || [ ! -f /usr/include/linux/errno.h ]; then
  log "Restoring linux-api-headers from Arch…"
  fetch /tmp/linux-api-headers.pkg.tar.zst \
    "https://archlinux.org/packages/core/x86_64/linux-api-headers/download/"
  sudocmd tar -C / -xf /tmp/linux-api-headers.pkg.tar.zst usr/include
fi
test -f /usr/include/linux/types.h
log "linux headers OK"

if ! pkg-config --exists libevdev 2>/dev/null || [ ! -f /usr/include/libevdev-1.0/libevdev/libevdev.h ]; then
  log "Restoring libevdev headers…"
  fetch /tmp/libevdev.pkg.tar.zst "https://archlinux.org/packages/extra/x86_64/libevdev/download/"
  sudocmd tar -C / -xf /tmp/libevdev.pkg.tar.zst usr/include usr/lib/pkgconfig || true
  if ! pkg-config --exists libevdev 2>/dev/null; then
    sudocmd tee /usr/local/lib/pkgconfig/libevdev.pc >/dev/null <<'PC'
prefix=/usr
libdir=${prefix}/lib
includedir=${prefix}/include
Name: libevdev
Version: 1.13.4
Libs: -L${libdir} -levdev
Cflags: -I${includedir}/libevdev-1.0
PC
  fi
fi
pkg-config --exists libevdev
log "libevdev OK"

if ! pkg-config --exists libudev 2>/dev/null || [ ! -f /usr/include/libudev.h ]; then
  log "Restoring libudev headers…"
  fetch /tmp/systemd-libs.pkg.tar.zst \
    "https://archlinux.org/packages/core/x86_64/systemd-libs/download/"
  sudocmd tar -C / -xf /tmp/systemd-libs.pkg.tar.zst usr/include/libudev.h usr/lib/pkgconfig/libudev.pc || true
  if [ ! -f /usr/include/libudev.h ]; then
    fetch /tmp/libudev.h "https://raw.githubusercontent.com/systemd/systemd/v257.7/src/libudev/libudev.h"
    sudocmd install -m 644 /tmp/libudev.h /usr/include/libudev.h
  fi
  if ! pkg-config --exists libudev 2>/dev/null; then
    sudocmd tee /usr/local/lib/pkgconfig/libudev.pc >/dev/null <<'PC'
prefix=/usr
libdir=${prefix}/lib
includedir=${prefix}/include
Name: libudev
Version: 257
Libs: -L${libdir} -ludev
Cflags: -I${includedir}
PC
  fi
fi
pkg-config --exists libudev
test -f /usr/include/libudev.h
log "libudev OK"

if [ ! -d "$SRC/joycond/.git" ]; then
  git clone --depth 1 https://github.com/DanielOgorchock/joycond.git "$SRC/joycond"
else
  git -C "$SRC/joycond" pull --ff-only 2>>"$LOG" || true
fi
cd "$SRC/joycond"
rm -rf build
mkdir build
cd build
log "Building joycond…"
cmake -G Ninja ..
ninja
log "Installing joycond…"
sudocmd ninja install
sudocmd udevadm control --reload-rules 2>>"$LOG" || true
sudocmd udevadm trigger 2>>"$LOG" || true
sudocmd systemctl daemon-reload 2>>"$LOG" || true
sudocmd systemctl enable --now joycond
systemctl is-active --quiet joycond
log "joycond active"

log "Installing joycond-cemuhook…"
python3 -m ensurepip --user 2>>"$LOG" || true
pac cairo python-cairo python-gobject python-pyudev || true
if ! python3 -m pip install --user --upgrade --break-system-packages --no-deps \
    "git+https://github.com/joaorb64/joycond-cemuhook" >>"$LOG" 2>&1; then
  log "WARN: cemuhook --no-deps failed, retrying…"
  python3 -m pip install --user --upgrade --break-system-packages \
    "git+https://github.com/joaorb64/joycond-cemuhook" >>"$LOG" 2>&1 || true
fi
python3 -m pip install --user --upgrade --break-system-packages termcolor >>"$LOG" 2>&1 || true
if command -v joycond-cemuhook >/dev/null 2>&1 || \
   python3 -c 'import joycond_cemuhook' >/dev/null 2>&1; then
  log "joycond-cemuhook OK"
else
  log "WARN: cemuhook not importable — check $LOG"
fi

echo ok >"$STATUS"
log "DONE"
systemctl is-active joycond
