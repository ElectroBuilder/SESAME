#!/bin/bash
# SESAME Dolphin wrapper: ROM in argv, then exec so Steam Input keeps this PID.
stripq() { local s="$1"; s="${s#\"}"; s="${s%\"}"; s="${s#\'}"; s="${s%\'}"; printf '%s' "$s"; }
ROM=""
prev=""
for a in "$@"; do
  a=$(stripq "$a")
  case "$a" in
    -b|--batch|-e|--exec) prev=e; continue ;;
  esac
  if [ "$prev" = e ]; then ROM="$a"; prev=""; continue; fi
  case "$a" in
    -*) continue ;;
  esac
  ROM="$a"
done
if [ -z "$ROM" ]; then echo "SESAME: geen ROM" >&2; exit 1; fi

export HOME="${HOME:-/home/deck}"
# Steam/Game Mode env doorgeven; alleen defaults zetten als ze ontbreken.
export SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD="${SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD:-1}"
export SDL_JOYSTICK_HIDAPI_STEAM="${SDL_JOYSTICK_HIDAPI_STEAM:-1}"
export SDL_JOYSTICK_HIDAPI_JOY_CONS="${SDL_JOYSTICK_HIDAPI_JOY_CONS:-1}"
export SDL_JOYSTICK_HIDAPI_COMBINED_JOY_CONS="${SDL_JOYSTICK_HIDAPI_COMBINED_JOY_CONS:-1}"
export SDL_JOYSTICK_HIDAPI_SWITCH="${SDL_JOYSTICK_HIDAPI_SWITCH:-1}"
export SDL_JOYSTICK_HIDAPI_PS4="${SDL_JOYSTICK_HIDAPI_PS4:-1}"
export SDL_JOYSTICK_HIDAPI_PS5="${SDL_JOYSTICK_HIDAPI_PS5:-1}"
export SDL_JOYSTICK_HIDAPI_XBOX="${SDL_JOYSTICK_HIDAPI_XBOX:-1}"
export SDL_JOYSTICK_HIDAPI_GAMECUBE="${SDL_JOYSTICK_HIDAPI_GAMECUBE:-1}"

HERE="$(cd "$(dirname "$0")" && pwd)"
if [ -f "$HERE/sesame-dolphin-cfg.py" ]; then
  python3 "$HERE/sesame-dolphin-cfg.py" >/dev/null 2>&1 || true
fi

GC="$HOME/Emulation/tools/launchers/gc.sh"
WII="$HOME/Emulation/tools/launchers/wii.sh"
DOL="$HOME/Emulation/tools/launchers/dolphin.sh"
DOL2="$HOME/Emulation/tools/launchers/dolphin-emu.sh"
case "$ROM" in
  */roms/wii/*|*/wii/*) set -- "$WII" "$DOL" "$DOL2" "$GC" ;;
  *) set -- "$GC" "$DOL" "$DOL2" "$WII" ;;
esac
for s in "$@"; do
  [ "$s" = "$HERE/sesame-dolphin.sh" ] && continue
  [ "$s" = "$HERE/vssh-dolphin.sh" ] && continue
  # EmuDeck gc.sh/wii.sh verwachten alleen het ROM-pad, geen Dolphin -b -e.
  if [ -x "$s" ]; then exec "$s" "$ROM"; fi
done
exec /usr/bin/flatpak run --filesystem=host --device=all org.DolphinEmu.dolphin-emu -b -e "$ROM"
