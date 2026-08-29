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
# Joy-Con DSU (joycond-cemuhook) on 26761; SteamDeckGyroDSU stays on 26760.
export SESAME_JOYCON_DSU_PORT="${SESAME_JOYCON_DSU_PORT:-26761}"

HERE="$(cd "$(dirname "$0")" && pwd)"
GC="$HOME/Emulation/tools/launchers/gc.sh"
WII="$HOME/Emulation/tools/launchers/wii.sh"
DOL="$HOME/Emulation/tools/launchers/dolphin.sh"
DOL2="$HOME/Emulation/tools/launchers/dolphin-emu.sh"
case "$ROM" in
  */roms/wii/*|*/wii/*)
    # Separate L/R Joy-Cons so SESAME can map Wiimote + Nunchuk.
    export SDL_JOYSTICK_HIDAPI_COMBINED_JOY_CONS="${SDL_JOYSTICK_HIDAPI_COMBINED_JOY_CONS:-0}"
    set -- "$WII" "$DOL" "$DOL2" "$GC"
    # Start Joy-Con motion DSU before patching Dolphin (guide: joycond-cemuhook).
    if [ -f "$HERE/sesame-joycon-dsu.sh" ]; then
      bash "$HERE/sesame-joycon-dsu.sh" >/dev/null 2>&1 || true
      # Give cemuhook a moment to publish pads before Dolphin cfg / launch.
      sleep 1.5
    fi
    ;;
  *)
    export SDL_JOYSTICK_HIDAPI_COMBINED_JOY_CONS="${SDL_JOYSTICK_HIDAPI_COMBINED_JOY_CONS:-1}"
    set -- "$GC" "$DOL" "$DOL2" "$WII"
    ;;
esac
if [ -f "$HERE/sesame-dolphin-cfg.py" ]; then
  python3 "$HERE/sesame-dolphin-cfg.py" >/dev/null 2>&1 || true
fi
for s in "$@"; do
  [ "$s" = "$HERE/sesame-dolphin.sh" ] && continue
  [ "$s" = "$HERE/vssh-dolphin.sh" ] && continue
  # EmuDeck gc.sh/wii.sh expect only the ROM path.
  if [ -x "$s" ]; then exec "$s" "$ROM"; fi
done
exec /usr/bin/flatpak run --filesystem=host --device=all org.DolphinEmu.dolphin-emu -b -e "$ROM"
