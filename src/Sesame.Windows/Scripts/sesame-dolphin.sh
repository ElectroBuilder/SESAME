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
export SDL_JOYSTICK_HIDAPI_SWITCH="${SDL_JOYSTICK_HIDAPI_SWITCH:-1}"
export SDL_JOYSTICK_HIDAPI_PS4="${SDL_JOYSTICK_HIDAPI_PS4:-1}"
export SDL_JOYSTICK_HIDAPI_PS5="${SDL_JOYSTICK_HIDAPI_PS5:-1}"
export SDL_JOYSTICK_HIDAPI_XBOX="${SDL_JOYSTICK_HIDAPI_XBOX:-1}"
export SDL_JOYSTICK_HIDAPI_GAMECUBE="${SDL_JOYSTICK_HIDAPI_GAMECUBE:-1}"
export SESAME_JOYCON_DSU_PORT="${SESAME_JOYCON_DSU_PORT:-26761}"

HERE="$(cd "$(dirname "$0")" && pwd)"
GC="$HOME/Emulation/tools/launchers/gc.sh"
WII="$HOME/Emulation/tools/launchers/wii.sh"
DOL="$HOME/Emulation/tools/launchers/dolphin.sh"
DOL2="$HOME/Emulation/tools/launchers/dolphin-emu.sh"
case "$ROM" in
  */roms/wii/*|*/wii/*)
    # Combined Joy-Cons (L+R) → SDL "Nintendo Switch Joy-Con (L/R)" for Joy2Wii.
    export SDL_JOYSTICK_HIDAPI_COMBINED_JOY_CONS="${SDL_JOYSTICK_HIDAPI_COMBINED_JOY_CONS:-1}"
    set -- "$WII" "$DOL" "$DOL2" "$GC"
    # Optional DSU (cemuhook). Joy2Wii uses SDL Accel R/L — skip by default for faster Game Mode starts.
    if [ "${SESAME_JOYCON_DSU:-0}" = "1" ] && [ -f "$HERE/sesame-joycon-dsu.sh" ]; then
      bash "$HERE/sesame-joycon-dsu.sh" >/dev/null 2>&1 || true
      sleep 0.5
    fi
    ;;
  *)
    export SDL_JOYSTICK_HIDAPI_COMBINED_JOY_CONS="${SDL_JOYSTICK_HIDAPI_COMBINED_JOY_CONS:-1}"
    set -- "$GC" "$DOL" "$DOL2" "$WII"
    ;;
esac

# Apply SESAME - Joy2Wii (or no-nunchuk) into WiimoteNew.ini — never invent IMU maps.
# SESAME_WII_NUNCHUK=0 → Extension=None for games that require removing the Nunchuk.
if [ -f "$HERE/sesame-dolphin-cfg.py" ]; then
  python3 "$HERE/sesame-dolphin-cfg.py" >/dev/null 2>&1 || true
fi
for s in "$@"; do
  [ "$s" = "$HERE/sesame-dolphin.sh" ] && continue
  [ "$s" = "$HERE/vssh-dolphin.sh" ] && continue
  if [ -x "$s" ]; then exec "$s" "$ROM"; fi
done
exec /usr/bin/flatpak run --filesystem=host --device=all org.DolphinEmu.dolphin-emu -b -e "$ROM"
