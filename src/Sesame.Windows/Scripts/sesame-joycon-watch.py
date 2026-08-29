#!/usr/bin/env python3
"""SESAME: watch Joy-Con topology and switch Dolphin Wiimote Extension live.

Why Combined freezes when Left disconnects
------------------------------------------
Joy2Wii uses Device = SDL \"Nintendo Switch Joy-Con (L/R)\". That Combined pad
disappears when either Joy-Con drops → Dolphin loses its only Device.

Physical workflow (joycond / Switch association)
----------------------------------------------
Pair (Wiimote + Nunchuk):
  Press one shoulder on each Joy-Con at the same time (L on Left + R on Right).
  Not ZL+ZR — joycond wants a *single* trigger on both.

Solo Wiimote (Nunchuk \"removed\" for the game):
  1. Power-off / disconnect the Left Joy-Con
  2. On the Right Joy-Con press ZR+R together, or SL+SR
     → Right becomes a solo pad; LEDs settle on one player light

Re-pair:
  Reconnect Left, then L (left) + R (right) together again.

This watcher
------------
Detects Combined vs Right-only and applies:
  Combined → SESAME - Joy2Wii (Extension = Nunchuk)
  Right only / Left dropped → SESAME - Joy2Wii (solo)
      (Extension = None, Device = Joy-Con (R))

Then forces Dolphin to LoadConfig via Next Wiimote Profile (F8), by briefly
leaving only the target profile visible so one keypress loads the right layout.
No Controllers menu — physical Joy-Con mode drives it.
"""
from __future__ import annotations

import os
import pathlib
import re
import subprocess
import sys
import time

PROFILE_PAIR = "SESAME - Joy2Wii"
PROFILE_SOLO = "SESAME - Joy2Wii (solo)"
STATE_FILE = "joycon-watch.state"
POLL = float(os.environ.get("SESAME_JOYCON_WATCH_POLL", "0.75"))
HOTKEY = os.environ.get("SESAME_WIIMOTE_PROFILE_KEY", "F8")


def home() -> pathlib.Path:
    return pathlib.Path(os.path.expanduser("~"))


def log(msg: str) -> None:
    try:
        path = home() / ".local/share/sesame/dolphin-joycon.log"
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%dT%H:%M:%S ") + msg.rstrip() + "\n")
    except Exception:
        pass


def dirs() -> list[pathlib.Path]:
    h = home()
    return [
        h / ".var/app/org.DolphinEmu.dolphin-emu/config/dolphin-emu",
        h / ".config/dolphin-emu",
    ]


def read(path: pathlib.Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="ignore")
    except Exception:
        return ""


def write(path: pathlib.Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def restore_stashed_profiles() -> None:
    for base in dirs():
        prof = base / "Profiles" / "Wiimote"
        if not prof.is_dir():
            continue
        for stash in prof.glob("*.ini.sesame_stash"):
            orig = pathlib.Path(str(stash)[: -len(".sesame_stash")])
            try:
                if not orig.exists():
                    stash.rename(orig)
                else:
                    stash.unlink()
            except Exception as ex:
                log("stash restore failed %s: %s" % (stash, ex))


def input_names() -> list[str]:
    try:
        raw = pathlib.Path("/proc/bus/input/devices").read_text(errors="ignore")
    except Exception:
        return []
    return list(dict.fromkeys(re.findall(r'N: Name="([^"]+)"', raw)))


def classify(names: list[str]) -> str:
    """Return pair | solo_right | solo_left | empty."""
    combined = False
    left = False
    right = False
    for n in names:
        low = n.lower()
        if "steam" in low and "virtual" in low:
            continue
        if "imu" in low:
            continue
        if ("l/r" in low or "combined" in low) and "joy" in low:
            combined = True
            continue
        if "joy-con (l)" in low or "joycon (l)" in low or "left joy-con" in low:
            if "l/r" not in low:
                left = True
        if "joy-con (r)" in low or "joycon (r)" in low or "right joy-con" in low:
            if "l/r" not in low:
                right = True
    if combined:
        return "pair"
    if right and not left:
        return "solo_right"
    if left and not right:
        return "solo_left"
    if right and left:
        # Separate L+R without Combined — treat as waiting to pair; keep last.
        return "pair_separate"
    return "empty"


def find_profile(name: str) -> pathlib.Path | None:
    for base in dirs():
        p = base / "Profiles" / "Wiimote" / (name + ".ini")
        if p.is_file():
            return p
    return None


def ensure_source(body: str) -> str:
    if re.search(r"(?m)^Source\s*=", body):
        return body
    return body.replace("[Wiimote1]\n", "[Wiimote1]\nSource = 1\n", 1)


def remap_sdl_index(text: str, index: int) -> str:
    if index == 0:
        return text
    return text.replace("SDL/0/", "SDL/%d/" % index)


def wiimote_section(profile_text: str, slot: int) -> str:
    body = re.sub(r"^\[Profile\]\s*", "", profile_text.strip() + "\n", count=1, flags=re.I)
    body = remap_sdl_index(body, slot)
    body = re.sub(r"(?m)^Source\s*=.*\n?", "", body)
    if not body.endswith("\n"):
        body += "\n"
    return "[Wiimote%d]\nSource = 1\n%s" % (slot + 1, body)


def profile_to_wiimote(profile_text: str, other_slots_text: str | None = None) -> str:
    other = other_slots_text if other_slots_text is not None else profile_text
    parts = [wiimote_section(profile_text if i == 0 else other, i) for i in range(4)]
    parts.append("[BalanceBoard]\nSource = 0\n")
    return "".join(parts)


def apply_named(profile_name: str) -> bool:
    path = find_profile(profile_name)
    if path is None:
        log("missing profile %s" % profile_name)
        return False
    text = read(path)
    if not text.strip():
        return False
    # Solo only changes player 1; keep Joy2Wii on Wiimote2–4 for extra pairs.
    other = None
    if profile_name == PROFILE_SOLO:
        pair = find_profile(PROFILE_PAIR)
        if pair is not None:
            other = read(pair) or None
    wiimote = profile_to_wiimote(text, other)
    for base in dirs():
        if not base.exists() and base != dirs()[0]:
            continue
        write(base / "WiimoteNew.ini", wiimote)
    log("applied %s" % profile_name)
    return True


def stash_other_profiles(keep_name: str) -> list[tuple[pathlib.Path, pathlib.Path]]:
    """Hide every Wiimote profile except keep_name so Next Profile loads it."""
    stashed: list[tuple[pathlib.Path, pathlib.Path]] = []
    for base in dirs():
        prof = base / "Profiles" / "Wiimote"
        if not prof.is_dir():
            continue
        for p in prof.glob("*.ini"):
            if p.stem == keep_name:
                continue
            stash = pathlib.Path(str(p) + ".sesame_stash")
            try:
                if stash.exists():
                    stash.unlink()
                p.rename(stash)
                stashed.append((p, stash))
            except Exception as ex:
                log("stash failed %s: %s" % (p, ex))
    return stashed


def unstash(stashed: list[tuple[pathlib.Path, pathlib.Path]]) -> None:
    for orig, stash in stashed:
        try:
            if stash.exists():
                if orig.exists():
                    stash.unlink()
                else:
                    stash.rename(orig)
        except Exception as ex:
            log("unstash failed %s: %s" % (stash, ex))


def ensure_hotkey() -> None:
    """Set Next/Previous Wiimote Profile to F8/F7 when unset or still SESAME defaults."""
    for base in dirs():
        path = base / "Hotkeys.ini"
        cur = read(path) if path.exists() else "[Hotkeys]\n"
        if not cur.strip():
            cur = "[Hotkeys]\n"
        if "[Hotkeys]" not in cur:
            cur = "[Hotkeys]\n" + cur

        def set_hk(text: str, key: str, value: str) -> str:
            line = key + " = " + value
            rx = re.compile(r"^" + re.escape(key) + r"\s*=.*$", re.I | re.M)
            m = rx.search(text)
            if m:
                existing = m.group(0).split("=", 1)[-1].strip()
                # Don't clobber a real user binding.
                if existing and existing not in ("", value, "@(" + value + ")"):
                    if "f8" not in existing.lower() and "f7" not in existing.lower():
                        if "sesame" not in existing.lower():
                            return text
                return rx.sub(line, text, count=1)
            # Insert into [Hotkeys]
            i = text.lower().find("[hotkeys]")
            if i < 0:
                return text.rstrip() + "\n[Hotkeys]\n" + line + "\n"
            start = i + len("[Hotkeys]")
            return text[:start] + "\n" + line + text[start:]

        # Dolphin labels (recent): "Next Wiimote Profile" / older builds may use "Next Profile"
        for key, val in (
            ("Next Wiimote Profile", HOTKEY),
            ("Previous Wiimote Profile", "F7"),
            ("Next Profile", HOTKEY),
            ("Previous Profile", "F7"),
        ):
            cur = set_hk(cur, key, val)
        write(path, cur)


def try_reload_dolphin() -> None:
    """Fire Next Wiimote Profile (F8) so Dolphin LoadConfig mid-game."""
    displays = []
    for d in (os.environ.get("DISPLAY"), os.environ.get("WAYLAND_DISPLAY"), ":0", ":1"):
        if d and d not in displays:
            displays.append(d)

    key = HOTKEY
    attempts: list[list[str]] = [
        ["xdotool", "key", key],
        ["xdotool", "key", "--clearmodifiers", key],
        ["wtype", "-k", key],
        ["ydotool", "key", "66:1", "66:0"],  # F8 keycode on many layouts
    ]
    for display in displays:
        if display.startswith(":"):
            env = {**os.environ, "DISPLAY": display}
        else:
            env = dict(os.environ)
        for cmd in attempts:
            try:
                subprocess.run(
                    cmd,
                    check=False,
                    timeout=2,
                    env=env,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                )
            except Exception:
                continue
    try:
        subprocess.run(
            [
                "notify-send",
                "-a",
                "SESAME",
                "Joy-Con mode",
                "Wiimote layout updated (F8 = Next Wiimote Profile if input stuck)",
            ],
            check=False,
            timeout=2,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    except Exception:
        pass


def mode_to_profile(mode: str, prev_stable: str) -> str | None:
    if mode == "pair":
        return PROFILE_PAIR
    if mode == "solo_right":
        return PROFILE_SOLO
    # Left dropped: Combined is gone, Right not yet solo — apply solo so Extension=None
    # and Device awaits Joy-Con (R) after SL+SR / ZR+R.
    if mode == "empty" and prev_stable in ("pair", "solo_right", "pair_separate"):
        return PROFILE_SOLO
    if mode == "pair_separate":
        # Both present separately — wait for L+R combine; don't thrash.
        return None
    return None


def write_state(text: str) -> None:
    try:
        write(home() / ".local/share/sesame" / STATE_FILE, text + "\n")
    except Exception:
        pass


def switch_to(profile_name: str) -> bool:
    if not apply_named(profile_name):
        return False
    ensure_hotkey()
    stashed = stash_other_profiles(profile_name)
    try:
        time.sleep(0.15)
        try_reload_dolphin()
        time.sleep(0.35)
        # Second tap helps if first landed before stash finished.
        try_reload_dolphin()
        time.sleep(0.2)
    finally:
        unstash(stashed)
    return True


def main() -> int:
    restore_stashed_profiles()
    ensure_hotkey()
    log("joycon-watch start poll=%.2fs key=%s" % (POLL, HOTKEY))
    last = ""
    stable = ""
    stable_count = 0
    while True:
        mode = classify(input_names())
        if mode == last:
            stable_count += 1
        else:
            last = mode
            stable_count = 1
        if stable_count >= 2 and mode != stable:
            prof = mode_to_profile(mode, stable)
            if prof:
                if switch_to(prof):
                    write_state("%s:%s" % (mode, prof))
                    log("mode %s → %s" % (mode, prof))
                    stable = mode
                else:
                    log("mode %s apply failed" % mode)
            else:
                write_state(mode)
                if mode != "empty":
                    stable = mode
                log("mode %s (no profile switch)" % mode)
        time.sleep(POLL)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        restore_stashed_profiles()
        log("joycon-watch stop")
        raise SystemExit(0)
