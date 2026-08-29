#!/usr/bin/env bash
# SESAME installer. No sudo. Idempotent: install or update in place.
#   curl -fsSL https://raw.githubusercontent.com/ElectroBuilder/SESAME/main/install.sh | bash
#   # or from a clone:
#   if [ -d SESAME/.git ]; then git -C SESAME pull --ff-only; else git clone https://github.com/ElectroBuilder/SESAME.git; fi
#   cd SESAME && bash install.sh
set -euo pipefail

REPO="ElectroBuilder/SESAME"
ASSET="sesame-linux-x64.tar.gz"
RELEASE_TAG="linux"
DEST="${SESAME_HOME:-$HOME/Applications/SESAME}"
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/sesame"

log() { printf '%s\n' "$*" >&2; }
die() { printf '%s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'EOF'
Install or update SESAME into ~/Applications/SESAME (no sudo).

  bash install.sh           Install or update (overwrite files in place)
  bash install.sh --update  git pull --ff-only if this is a clone, then install
  curl -fsSL https://raw.githubusercontent.com/ElectroBuilder/SESAME/main/install.sh | bash

Steam Deck / Arch / SteamOS need 64-bit x86 (x86_64). The app is the GitHub
release asset sesame-linux-x64.tar.gz (latest versioned release).
EOF
}

is_script_file() {
  local p="$1"
  [[ -n "$p" && -f "$p" ]] || return 1
  case "$p" in
    /dev/*|/proc/*|/fd/*|-) return 1 ;;
  esac
  return 0
}

resolve_root() {
  local p="${BASH_SOURCE[0]:-${0:-}}"
  ROOT=""
  if is_script_file "$p"; then
    ROOT="$(cd "$(dirname "$p")" && pwd)"
  elif [[ -f "$PWD/src/Sesame.Deck/Sesame.Deck.csproj" ]]; then
    ROOT="$PWD"
  fi
}

need_x86_64() {
  local m
  m="$(uname -m 2>/dev/null || echo unknown)"
  case "$m" in
    x86_64|amd64) return 0 ;;
    *)
      die "SESAME's Linux build is for 64-bit x86 (Steam Deck, SteamOS, Arch). This machine reports: ${m}
A 64-bit Intel/AMD CPU is required. ARM is not supported yet."
      ;;
  esac
}

payload_dir() {
  local dir="${1:-}"
  [[ -n "$dir" && -f "$dir/SESAME" && -s "$dir/SESAME" ]]
}

# User sessions, keys and caches must never ship next to the binary.
sanitize_install() {
  local d="${1:-}"
  [[ -n "$d" && -d "$d" ]] || return 0
  rm -rf "$d/secrets" "$d/optimizer-cache" "$d/art-cache" "$d/store-cache" \
    "$d/Data" "$d/AppData" "$d/.local"
  rm -f "$d/sessions.json" "$d/optimizer.json" "$d/optimizer-cache.json" \
    "$d/optimizer-picks.json" "$d/launchers.json" "$d/quickaccess.json" \
    "$d/manual-shortcuts.json" "$d/theme.txt" "$d/terminal.txt" "$d/crash.log" \
    "$d/catalog.json"
}

http_get() {
  local url="$1" out="$2"
  if command -v curl >/dev/null 2>&1; then
    curl -fL --retry 5 --retry-delay 2 --connect-timeout 20 \
      -A "SESAME-install" -o "$out" "$url"
  elif command -v wget >/dev/null 2>&1; then
    wget --tries=5 --timeout=20 -O "$out" "$url"
  else
    log "Neither curl nor wget is available."
    return 1
  fi
}

find_local_payload() {
  local d
  [[ -n "$ROOT" ]] || return 1
  for d in "$ROOT/artifacts/linux-x64" "$ROOT/linux-x64"; do
    if payload_dir "$d"; then
      PAYLOAD="$d"
      return 0
    fi
  done
  # Flattened publish next to the script, not a git checkout.
  if payload_dir "$ROOT" && [[ ! -d "$ROOT/src" ]]; then
    PAYLOAD="$ROOT"
    return 0
  fi
  return 1
}

download_release() {
  mkdir -p "$CACHE" || return 1
  local tarball="$CACHE/$ASSET"
  local unpack="$CACHE/unpack-$$"
  local url ok=0
  local urls=(
    "https://github.com/${REPO}/releases/latest/download/${ASSET}"
    "https://github.com/${REPO}/releases/download/${RELEASE_TAG}/${ASSET}"
  )

  for url in "${urls[@]}"; do
    log "Downloading SESAME from GitHub…"
    log "  $url"
    rm -f "$tarball"
    if ! http_get "$url" "$tarball"; then
      log "Download failed: $url"
      continue
    fi
    if [[ ! -s "$tarball" ]]; then
      log "Download was empty: $url"
      continue
    fi
    if ! tar -tzf "$tarball" >/dev/null 2>&1; then
      log "Download is not a valid gzip tarball (wrong file or an HTML error page)."
      continue
    fi
    ok=1
    break
  done
  [[ "$ok" -eq 1 ]] || return 1

  rm -rf "$unpack"
  mkdir -p "$unpack" || return 1
  if ! tar -xzf "$tarball" -C "$unpack"; then
    log "Extract failed for $tarball"
    rm -rf "$unpack"
    return 1
  fi

  if payload_dir "$unpack"; then
    PAYLOAD="$unpack"
    return 0
  fi

  local nested=""
  nested="$(find "$unpack" -maxdepth 3 -type f -name SESAME 2>/dev/null | head -n 1 || true)"
  if [[ -n "$nested" && -s "$nested" ]]; then
    PAYLOAD="$(dirname "$nested")"
    return 0
  fi

  log "Archive extracted but the SESAME binary was missing (empty or unexpected layout)."
  rm -rf "$unpack"
  return 1
}

ensure_dotnet() {
  if command -v dotnet >/dev/null 2>&1; then
    return 0
  fi
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
    return 0
  fi
  log "Installing .NET 8 SDK into $HOME/.dotnet (no sudo)…"
  mkdir -p "$CACHE"
  local script="$CACHE/dotnet-install.sh"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$script" || return 1
  else
    wget -O "$script" https://dot.net/v1/dotnet-install.sh || return 1
  fi
  bash "$script" --channel 8.0 --install-dir "$HOME/.dotnet" || return 1
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
}

build_from_source() {
  [[ -n "$ROOT" && -f "$ROOT/src/Sesame.Deck/Sesame.Deck.csproj" ]] || return 1
  ensure_dotnet || return 1
  command -v dotnet >/dev/null 2>&1 || return 1
  local out="$ROOT/artifacts/linux-x64"
  mkdir -p "$out"
  log "Building SESAME from source (this can take a few minutes)…"
  DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet publish "$ROOT/src/Sesame.Deck/Sesame.Deck.csproj" \
    -c Release -r linux-x64 --self-contained true -o "$out" || return 1
  payload_dir "$out" || return 1
  PAYLOAD="$out"
  return 0
}

maybe_git_pull() {
  [[ -n "$ROOT" && -d "$ROOT/.git" ]] || return 0
  command -v git >/dev/null 2>&1 || return 0
  log "Updating git clone in $ROOT …"
  if git -C "$ROOT" pull --ff-only; then
    return 0
  fi
  log "git pull did not fast-forward. Installing the GitHub release anyway."
}

install_payload() {
  local src="$1"
  payload_dir "$src" || die "Payload is missing the SESAME binary: $src"

  local parent stage old
  parent="$(dirname "$DEST")"
  mkdir -p "$parent"
  stage="${DEST}.new.$$"
  old="${DEST}.old.$$"
  rm -rf "$stage"
  mkdir -p "$stage"
  if ! cp -a "$src"/. "$stage/"; then
    rm -rf "$stage"
    die "Copy into staging folder failed."
  fi
  payload_dir "$stage" || {
    rm -rf "$stage"
    die "Install produced an empty app folder (SESAME binary missing after copy)."
  }
  chmod +x "$stage/SESAME" || true
  sanitize_install "$stage"

  if [[ -e "$DEST" || -L "$DEST" ]]; then
    rm -rf "$old"
    if ! mv "$DEST" "$old"; then
      rm -rf "$stage"
      die "Could not replace $DEST (is it in use?)."
    fi
  fi
  if ! mv "$stage" "$DEST"; then
    [[ -e "$old" ]] && mv "$old" "$DEST" || true
    rm -rf "$stage"
    die "Could not write $DEST"
  fi
  rm -rf "$old"
}

install_desktop_files() {
  local icon_src="" f
  for f in \
    "$DEST/Assets/sesame.png" \
    "$DEST/sesame.png" \
    "${ROOT:-}/src/Sesame.Deck/Assets/sesame.png" \
    "${ROOT:-}/pack/steamdeck/sesame.png"
  do
    if [[ -f "$f" ]]; then
      icon_src="$f"
      break
    fi
  done
  if [[ -n "$icon_src" ]]; then
    mkdir -p "$HOME/.local/share/icons/hicolor/256x256/apps"
    cp "$icon_src" "$HOME/.local/share/icons/hicolor/256x256/apps/sesame.png"
  fi

  mkdir -p "$HOME/.local/share/applications"
  cat > "$HOME/.local/share/applications/sesame.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=SESAME
Comment=Steam Easy Shortcut Artwork Manager Engine
Exec=$DEST/SESAME --desktop
Icon=sesame
Terminal=false
Categories=Utility;Game;
StartupWMClass=SESAME
EOF
  cat > "$HOME/.local/share/applications/sesame-gamemode.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=SESAME (Game Mode)
Comment=SESAME with controller-first navigation
Exec=$DEST/SESAME --gamemode
Icon=sesame
Terminal=false
Categories=Game;
StartupWMClass=SESAME
EOF
  update-desktop-database "$HOME/.local/share/applications" >/dev/null 2>&1 || true
  if command -v steamos-add-to-steam >/dev/null 2>&1; then
    steamos-add-to-steam "$DEST/SESAME" || true
  fi
}

# Empty ROM / Hydra folders only. Never write a list of games.
ensure_library_folders() {
  local home="${HOME:-/home/deck}"
  local roms="$home/Emulation/roms"
  local sys
  log "Creating empty ROM and Hydra folders…"
  for sys in nes snes n64 gc wii wiiu switch gb gbc gba nds 3ds genesis mastersystem saturn dreamcast psx ps2 psp psvita arcade xbox; do
    mkdir -p "$roms/$sys"
  done
  mkdir -p "$home/Emulation/hdpacks" \
    "$home/Emulation/bios/Mupen64plus/hires_texture" \
    "$home/Emulation/bios/Mupen64plus/cache" \
    "$home/Emulation/saves/retroarch" \
    "$home/Emulation/saves/nes/retroarch/saves" \
    "$home/Emulation/saves/snes/retroarch/saves" \
    "$home/Emulation/saves/n64/retroarch/saves" \
    "$home/Emulation/saves/gc/dolphin/User/GC" \
    "$home/Emulation/saves/gba/retroarch/saves" \
    "$home/Emulation/saves/nds/retroarch/saves" \
    "$home/Emulation/saves/genesis/retroarch/saves" \
    "$home/Emulation/saves/psx/duckstation" \
    "$home/Emulation/saves/ps2/pcsx2" \
    "$home/Emulation/storage/eden/load" \
    "$home/Emulation/storage/eden/nand/user/save" \
    "$home/Games/Hydra" \
    "$home/Games/Lutris" \
    "$home/Games/Other" \
    "$home/.config/hydra" \
    "$home/.config/hydralauncher" \
    "$home/.local/share/hydra" \
    "$home/.local/share/hydralauncher"
}

PULL=0
for arg in "$@"; do
  case "$arg" in
    --update|-u|update) PULL=1 ;;
    --help|-h) usage; exit 0 ;;
    *) die "Unknown option: $arg (try --help)" ;;
  esac
done

need_x86_64
resolve_root
if [[ "$PULL" -eq 1 ]]; then
  maybe_git_pull
fi

PAYLOAD=""
if find_local_payload; then
  log "Using local build: $PAYLOAD"
elif download_release; then
  log "Using GitHub release ($ASSET)"
elif build_from_source; then
  log "Using source build"
else
  die "Could not get a SESAME binary.
Download failed for GitHub release tag '${RELEASE_TAG}' asset '${ASSET}'.
If you cloned the repo, run: git pull && bash install.sh
This installer does not use sudo."
fi

log "Installing to $DEST"
install_payload "$PAYLOAD"
if [[ -n "${CACHE:-}" && "$PAYLOAD" == "$CACHE/"* ]]; then
  rm -rf "$PAYLOAD"
fi
install_desktop_files
ensure_library_folders

if ! payload_dir "$DEST"; then
  die "Install finished but $DEST is empty (SESAME binary missing)."
fi

log "SESAME installed in $DEST"
log "Open it from the applications menu, or run: $DEST/SESAME"
log "To update later: bash install.sh   or   curl -fsSL https://raw.githubusercontent.com/ElectroBuilder/SESAME/main/install.sh | bash"
