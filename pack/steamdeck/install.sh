#!/usr/bin/env bash
set -euo pipefail
# Install SESAME on SteamOS / Steam Deck (Desktop Mode).
ROOT="$(cd "$(dirname "$0")" && pwd)"
DEST="${SESAME_HOME:-$HOME/Applications/SESAME}"
mkdir -p "$DEST"
# Copy published payload sitting next to this script, or from a release folder.
if [[ -f "$ROOT/SESAME" ]]; then
  SRC="$ROOT"
elif [[ -f "$ROOT/linux-x64/SESAME" ]]; then
  SRC="$ROOT/linux-x64"
else
  echo "Zet de linux-x64 publish (bestand SESAME) naast dit script." >&2
  exit 1
fi
cp -a "$SRC"/. "$DEST/"
chmod +x "$DEST/SESAME" || true
mkdir -p "$HOME/.local/share/icons/hicolor/256x256/apps"
if [[ -f "$DEST/Assets/sesame.png" ]]; then
  cp "$DEST/Assets/sesame.png" "$HOME/.local/share/icons/hicolor/256x256/apps/sesame.png"
elif [[ -f "$ROOT/sesame.png" ]]; then
  cp "$ROOT/sesame.png" "$HOME/.local/share/icons/hicolor/256x256/apps/sesame.png"
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
echo "SESAME geïnstalleerd in $DEST"
echo "Desktop: toepassingenmenu of sesame.desktop"
echo "Game Mode: voeg $DEST/SESAME toe als non-Steam game, launch options: --gamemode"
echo "Artwork: pack/steamdeck/*.png (hero / portrait / landscape)"
