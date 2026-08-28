#!/usr/bin/env bash
# SESAME installer. No sudo.
#   git clone https://github.com/ElectroBuilder/SESAME.git
#   cd SESAME
#   bash install.sh
set -euo pipefail

REPO="ElectroBuilder/SESAME"
ASSET="sesame-linux-x64.tar.gz"
RELEASE_URL="https://github.com/${REPO}/releases/latest/download/${ASSET}"

ROOT="$(cd "$(dirname "$0")" && pwd)"
DEST="${SESAME_HOME:-$HOME/Applications/SESAME}"
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/sesame"
mkdir -p "$CACHE" "$DEST"

log() { printf '%s\n' "$*"; }
die() { printf '%s\n' "$*" >&2; exit 1; }

payload_dir() {
  local dir="$1"
  [[ -n "$dir" && -f "$dir/SESAME" ]]
}

find_local_payload() {
  local d
  for d in "$ROOT/artifacts/linux-x64" "$ROOT/linux-x64"; do
    if payload_dir "$d"; then
      printf '%s' "$d"
      return 0
    fi
  done
  # Flattened publish next to the script, not a git checkout.
  if payload_dir "$ROOT" && [[ ! -d "$ROOT/src" ]]; then
    printf '%s' "$ROOT"
    return 0
  fi
  return 1
}

download_release() {
  local tarball="$CACHE/$ASSET"
  local unpack="$CACHE/linux-x64"
  log "Downloading SESAME from GitHub…"
  if command -v curl >/dev/null 2>&1; then
    curl -fL --retry 3 --retry-delay 2 -o "$tarball" "$RELEASE_URL" || return 1
  elif command -v wget >/dev/null 2>&1; then
    wget -O "$tarball" "$RELEASE_URL" || return 1
  else
    return 1
  fi
  rm -rf "$unpack"
  mkdir -p "$unpack"
  tar -xzf "$tarball" -C "$unpack"
  if payload_dir "$unpack"; then
    printf '%s' "$unpack"
    return 0
  fi
  local nested
  nested="$(find "$unpack" -maxdepth 3 -name SESAME -type f 2>/dev/null | head -n 1 || true)"
  if [[ -n "$nested" ]]; then
    printf '%s' "$(dirname "$nested")"
    return 0
  fi
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
  local script="$CACHE/dotnet-install.sh"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$script"
  else
    wget -O "$script" https://dot.net/v1/dotnet-install.sh
  fi
  bash "$script" --channel 8.0 --install-dir "$HOME/.dotnet"
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
}

build_from_source() {
  [[ -f "$ROOT/src/Sesame.Deck/Sesame.Deck.csproj" ]] || return 1
  ensure_dotnet
  command -v dotnet >/dev/null 2>&1 || return 1
  local out="$ROOT/artifacts/linux-x64"
  mkdir -p "$out"
  log "Building SESAME from source (this can take a few minutes)…"
  DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet publish "$ROOT/src/Sesame.Deck/Sesame.Deck.csproj" \
    -c Release -r linux-x64 --self-contained true -o "$out"
  payload_dir "$out" || return 1
  printf '%s' "$out"
}

SRC=""
if SRC="$(find_local_payload)"; then
  log "Using local build: $SRC"
elif SRC="$(download_release)"; then
  log "Using GitHub release"
elif SRC="$(build_from_source)"; then
  log "Using source build"
else
  die "Could not get a SESAME binary. Check your internet connection and try again.
This installer does not use sudo."
fi

log "Installing to $DEST"
cp -a "$SRC"/. "$DEST/"
chmod +x "$DEST/SESAME" || true

ICON_SRC=""
for f in \
  "$DEST/Assets/sesame.png" \
  "$DEST/sesame.png" \
  "$ROOT/src/Sesame.Deck/Assets/sesame.png" \
  "$ROOT/pack/steamdeck/sesame.png"
do
  if [[ -f "$f" ]]; then
    ICON_SRC="$f"
    break
  fi
done
if [[ -n "$ICON_SRC" ]]; then
  mkdir -p "$HOME/.local/share/icons/hicolor/256x256/apps"
  cp "$ICON_SRC" "$HOME/.local/share/icons/hicolor/256x256/apps/sesame.png"
fi

mkdir -p "$HOME/.local/share/applications"
cat > "$HOME/.local/share/applications/sesame.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=SESAME
Comment=Steam Deck Shortcut & Artwork Manager
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

log "SESAME installed in $DEST"
log "Open it from the applications menu, or run: $DEST/SESAME"
