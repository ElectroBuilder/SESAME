#!/usr/bin/env python3
"""SESAME: patch Dolphin for Deck gyro and Joy-Con → Wiimote / Nunchuk.

BetterJoyForDolphin is Windows + UDP-Dolphin only. On Steam Deck / Linux we map
paired Joy-Cons directly into Dolphin's emulated Wiimotes:

  Right Joy-Con  → Wiimote (+ MotionPlus / IMU)
  Left Joy-Con   → Nunchuk stick + C/Z

Extra Joy-Cons become Wiimote 2–4 (remote only) for games like Wii Sports.
"""
from __future__ import annotations

import os
import pathlib
import re

DECK_IMU = {
    "IMUAccelerometer/Up": "`SteamDeck/0/Steam Deck:Accel Up`|`DSUClient/0/steamdeckgyro:Accel Up`|`Accel Up`",
    "IMUAccelerometer/Down": "`SteamDeck/0/Steam Deck:Accel Down`|`DSUClient/0/steamdeckgyro:Accel Down`|`Accel Down`",
    "IMUAccelerometer/Left": "`SteamDeck/0/Steam Deck:Accel Left`|`DSUClient/0/steamdeckgyro:Accel Left`|`Accel Left`",
    "IMUAccelerometer/Right": "`SteamDeck/0/Steam Deck:Accel Right`|`DSUClient/0/steamdeckgyro:Accel Right`|`Accel Right`",
    "IMUAccelerometer/Forward": "`SteamDeck/0/Steam Deck:Accel Forward`|`DSUClient/0/steamdeckgyro:Accel Forward`|`Accel Forward`",
    "IMUAccelerometer/Backward": "`SteamDeck/0/Steam Deck:Accel Backward`|`DSUClient/0/steamdeckgyro:Accel Backward`|`Accel Backward`",
    "IMUGyroscope/Pitch Up": "`SteamDeck/0/Steam Deck:Gyro Pitch Up`|`DSUClient/0/steamdeckgyro:Gyro Pitch Up`|`Gyro Pitch Up`",
    "IMUGyroscope/Pitch Down": "`SteamDeck/0/Steam Deck:Gyro Pitch Down`|`DSUClient/0/steamdeckgyro:Gyro Pitch Down`|`Gyro Pitch Down`",
    "IMUGyroscope/Roll Left": "`SteamDeck/0/Steam Deck:Gyro Roll Left`|`DSUClient/0/steamdeckgyro:Gyro Roll Left`|`Gyro Roll Left`",
    "IMUGyroscope/Roll Right": "`SteamDeck/0/Steam Deck:Gyro Roll Right`|`DSUClient/0/steamdeckgyro:Gyro Roll Right`|`Gyro Roll Right`",
    "IMUGyroscope/Yaw Left": "`SteamDeck/0/Steam Deck:Gyro Yaw Left`|`DSUClient/0/steamdeckgyro:Gyro Yaw Left`|`Gyro Yaw Left`",
    "IMUGyroscope/Yaw Right": "`SteamDeck/0/Steam Deck:Gyro Yaw Right`|`DSUClient/0/steamdeckgyro:Gyro Yaw Right`|`Gyro Yaw Right`",
}

IR = {
    "IR/Up": "`Cursor Y-`|`XInput2/0/Virtual core pointer:Cursor Y-`|`Right Y-`",
    "IR/Down": "`Cursor Y+`|`XInput2/0/Virtual core pointer:Cursor Y+`|`Right Y+`",
    "IR/Left": "`Cursor X-`|`XInput2/0/Virtual core pointer:Cursor X-`|`Right X-`",
    "IR/Right": "`Cursor X+`|`XInput2/0/Virtual core pointer:Cursor X+`|`Right X+`",
    "IR/Auto-Hide": "False",
}

IMU_AXES = [
    ("IMUAccelerometer/Up", "Accel Up"),
    ("IMUAccelerometer/Down", "Accel Down"),
    ("IMUAccelerometer/Left", "Accel Left"),
    ("IMUAccelerometer/Right", "Accel Right"),
    ("IMUAccelerometer/Forward", "Accel Forward"),
    ("IMUAccelerometer/Backward", "Accel Backward"),
    ("IMUGyroscope/Pitch Up", "Gyro Pitch Up"),
    ("IMUGyroscope/Pitch Down", "Gyro Pitch Down"),
    ("IMUGyroscope/Roll Left", "Gyro Roll Left"),
    ("IMUGyroscope/Roll Right", "Gyro Roll Right"),
    ("IMUGyroscope/Yaw Left", "Gyro Yaw Left"),
    ("IMUGyroscope/Yaw Right", "Gyro Yaw Right"),
]


def names():
    try:
        raw = pathlib.Path("/proc/bus/input/devices").read_text(errors="ignore")
    except Exception:
        return []
    return re.findall(r'N: Name="([^"]+)"', raw)


def find(needles, pool):
    for orig in pool:
        low = orig.lower()
        if any(n in low for n in needles):
            return orig
    return None


def is_xboxish(name):
    low = (name or "").lower()
    return any(x in low for x in ("x-box", "xbox 360", "xbox wireless", "xbox one", "xbox controller"))


def sdl(name):
    return "SDL/0/%s" % name


def ref(device, control):
    """Absolute control on another (or the same) Dolphin device."""
    # device is already "SDL/0/Name"
    return "`%s:%s`" % (device, control)


def local_imu(device):
    out = {}
    for key, axis in IMU_AXES:
        out[key] = (
            ref(device, axis)
            + "|`DSUClient/0/BetterJoy:"
            + axis
            + "`|`DSUClient/0/joycond:"
            + axis
            + "`|`"
            + axis
            + "`"
        )
    return out


def classify(pool):
    left, right, combined, loose = [], [], [], []
    for n in pool:
        low = n.lower()
        if "steam" in low and "virtual" in low:
            continue
        if "steam deck" in low and "joy" not in low:
            continue
        if "combined" in low and ("joy" in low or "switch" in low):
            combined.append(n)
            continue
        if any(
            x in low
            for x in (
                "joy-con (l)",
                "joycon (l)",
                "left joy-con",
                "joy-con left",
                "nintendo switch left joy-con",
            )
        ):
            left.append(n)
            continue
        if any(
            x in low
            for x in (
                "joy-con (r)",
                "joycon (r)",
                "right joy-con",
                "joy-con right",
                "nintendo switch right joy-con",
            )
        ):
            right.append(n)
            continue
        if "joy-con" in low or "joycon" in low:
            loose.append(n)
    return left, right, combined, loose


def wiimote_buttons():
    return {
        "Buttons/A": "`Button A`|`Button S`|SOUTH|EAST",
        "Buttons/B": "`Button B`|`Button ZR`|`Trigger R`|`Button E`|EAST",
        "Buttons/1": "`Button X`|`Button N`|NORTH",
        "Buttons/2": "`Button Y`|`Button W`|WEST",
        "Buttons/-": "`Button Minus`|`Button Capture`|`Button Back`|SELECT",
        "Buttons/+": "`Button Plus`|`Button Start`|START",
        "Buttons/Home": "`Button Home`|`Button Guide`|MODE",
        "D-Pad/Up": "`Pad N`|`Hat 0 N`|`Axis 7-`|`Left Y-`",
        "D-Pad/Down": "`Pad S`|`Hat 0 S`|`Axis 7+`|`Left Y+`",
        "D-Pad/Left": "`Pad W`|`Hat 0 W`|`Axis 6-`|`Left X-`",
        "D-Pad/Right": "`Pad E`|`Hat 0 E`|`Axis 6+`|`Left X+`",
        "Shake/X": "`Button SL`|`Button SR`|`Shoulder L`|TL",
        "Shake/Y": "`Button SL`|`Button SR`|`Shoulder L`|TL",
        "Shake/Z": "`Button SL`|`Button SR`|`Shoulder L`|TL",
    }


def nunchuk_on(left_dev):
    """Nunchuk controls bound to the Left Joy-Con device."""
    return {
        "Nunchuk/Buttons/C": "|".join(
            [
                ref(left_dev, "Button L"),
                ref(left_dev, "Button SL"),
                ref(left_dev, "Shoulder L"),
                "`Shoulder L`|TL",
            ]
        ),
        "Nunchuk/Buttons/Z": "|".join(
            [
                ref(left_dev, "Button ZL"),
                ref(left_dev, "Trigger L"),
                ref(left_dev, "Full Axis 2+"),
                "`Trigger L`|`Full Axis 2+`",
            ]
        ),
        "Nunchuk/Stick/Up": "|".join(
            [ref(left_dev, "Left Y-"), ref(left_dev, "Axis 1-"), "`Left Y-`|`Axis 1-`"]
        ),
        "Nunchuk/Stick/Down": "|".join(
            [ref(left_dev, "Left Y+"), ref(left_dev, "Axis 1+"), "`Left Y+`|`Axis 1+`"]
        ),
        "Nunchuk/Stick/Left": "|".join(
            [ref(left_dev, "Left X-"), ref(left_dev, "Axis 0-"), "`Left X-`|`Axis 0-`"]
        ),
        "Nunchuk/Stick/Right": "|".join(
            [ref(left_dev, "Left X+"), ref(left_dev, "Axis 0+"), "`Left X+`|`Axis 0+`"]
        ),
    }


def classic_stub():
    return {
        "Classic/Buttons/A": "`Button E`|EAST",
        "Classic/Buttons/B": "`Button S`|SOUTH",
        "Classic/Buttons/X": "`Button N`|NORTH",
        "Classic/Buttons/Y": "`Button W`|WEST",
        "Classic/Buttons/ZL": "`Shoulder L`|TL",
        "Classic/Buttons/ZR": "`Shoulder R`|TR",
        "Classic/Buttons/-": "`Button Back`|SELECT",
        "Classic/Buttons/+": "`Button Start`|START",
        "Classic/Left Stick/Up": "`Left Y-`|`Axis 1-`",
        "Classic/Left Stick/Down": "`Left Y+`|`Axis 1+`",
        "Classic/Left Stick/Left": "`Left X-`|`Axis 0-`",
        "Classic/Left Stick/Right": "`Left X+`|`Axis 0+`",
        "Classic/Right Stick/Up": "`Right Y-`|`Axis 4-`",
        "Classic/Right Stick/Down": "`Right Y+`|`Axis 4+`",
        "Classic/Right Stick/Left": "`Right X-`|`Axis 3-`",
        "Classic/Right Stick/Right": "`Right X+`|`Axis 3+`",
        "Classic/Triggers/L": "`Trigger L`|`Full Axis 2+`",
        "Classic/Triggers/R": "`Trigger R`|`Full Axis 5+`",
        "Classic/D-Pad/Up": "`Pad N`|`Axis 7-`",
        "Classic/D-Pad/Down": "`Pad S`|`Axis 7+`",
        "Classic/D-Pad/Left": "`Pad W`|`Axis 6-`",
        "Classic/D-Pad/Right": "`Pad E`|`Axis 6+`",
    }


def section_span(text, section):
    header = "[" + section + "]"
    low = text.lower()
    i = low.find(header.lower())
    if i < 0:
        return None
    start = i + len(header)
    nxt = re.search(r"\n\[", text[start:])
    end = start + nxt.start() if nxt else len(text)
    return i, start, end


def set_key(text, section, key, value, overwrite=True):
    line = key + " = " + value
    span = section_span(text, section)
    if span is None:
        return text.rstrip() + "\n[" + section + "]\n" + line + "\n"
    _, start, end = span
    body = text[start:end]
    rx = re.compile(r"^" + re.escape(key) + r"\s*=.*$", re.I | re.M)
    if rx.search(body):
        if not overwrite:
            return text
        body = rx.sub(line, body, count=1)
        return text[:start] + body + text[end:]
    insert = "\n" + line
    return text[:start] + insert + body + text[end:]


def get_key(text, section, key):
    span = section_span(text, section)
    if span is None:
        return ""
    body = text[span[1] : span[2]]
    m = re.search(r"^" + re.escape(key) + r"\s*=\s*(.*)$", body, re.I | re.M)
    return m.group(1).strip() if m else ""


def write_section(keys):
    lines = []
    for k, v in keys.items():
        lines.append("%s = %s" % (k, v))
    return "\n".join(lines) + "\n"


def build_remote(slot, device, extension=None, nunchuk_dev=None, sideways=False):
    """Build one [WiimoteN] block."""
    keys = {"Device": device, "Source": "1"}
    keys.update(wiimote_buttons())
    keys.update(local_imu(device))
    keys.update(IR)
    keys["IMUIR/Enabled"] = "True"
    keys["Rumble/Motor"] = "Strong"
    keys["Options/Sideways Wiimote"] = "True" if sideways else "False"
    if extension == "Nunchuk" and nunchuk_dev:
        keys["Extension"] = "Nunchuk"
        keys.update(nunchuk_on(nunchuk_dev))
    else:
        keys["Extension"] = "None"
    keys.update(classic_stub())
    return "[Wiimote%d]\n" % slot + write_section(keys)


def build_joycon_plan(pool):
    left, right, combined, loose = classify(pool)
    remotes = []  # list of (device_name, extension, nunchuk_name|None, sideways)

    # Pair L+R first → Wiimote + Nunchuk (Galaxy / boxing / etc.)
    pairs = min(len(left), len(right))
    for i in range(pairs):
        remotes.append((right[i], "Nunchuk", left[i], False))
    left = left[pairs:]
    right = right[pairs:]

    # Remaining singles → remotes only (Wii Sports multiplayer)
    singles = right + left + loose
    for name in singles:
        remotes.append((name, None, None, True))

    # Combined pair as one pad with Nunchuk on same device if nothing else
    if not remotes and combined:
        remotes.append((combined[0], "Nunchuk", combined[0], False))

    return remotes[:4]


def fallback_device(pool):
    n = find(("joy-con", "nintendo switch combined", "nintendo switch pro"), pool)
    if n:
        return sdl(n)
    n = find(("8bitdo",), pool)
    if n and not is_xboxish(n):
        return sdl(n)
    n = find(("dualsense", "dualshock", "wireless controller"), pool)
    if n and "xbox" not in n.lower() and "8bitdo" not in n.lower():
        return sdl(n)
    n = find(("steam virtual gamepad",), pool)
    if n:
        return sdl(n)
    n = find(("microsoft x-box 360", "x-box 360 pad"), pool)
    if n:
        return "evdev/0/%s" % n
    n = find(("xbox wireless", "xbox one", "xbox controller"), pool)
    if n:
        return sdl(n)
    n = find(("steam deck",), pool)
    if n and "virtual" not in n.lower():
        return "SteamDeck/0/Steam Deck"
    if os.environ.get("SteamAppId") or os.environ.get("SteamGameId") or os.environ.get("SteamDeck"):
        return "SDL/0/Steam Virtual Gamepad"
    return "SDL/0/Steam Deck Controller"


def patch_dolphin_ini(path, wiimote_count):
    cur = path.read_text(errors="ignore") if path.exists() else "[Core]\n"
    cur = set_key(cur, "Core", "SIDevice0", "6")
    for i in range(4):
        cur = set_key(cur, "Core", "WiimoteSource%d" % i, "1" if i < wiimote_count else "0")
    cur = set_key(cur, "Input", "BackgroundInput", "True")
    path.write_text(cur)


def enable_dsu(path):
    """Optional DSU (BetterJoy / joycond-cemuhook) on localhost."""
    cur = path.read_text(errors="ignore") if path.exists() else ""
    if not cur.strip():
        cur = "[Server]\n"
    cur = set_key(cur, "Server", "Enabled", "True")
    # Prefer existing; otherwise localhost BetterJoy/joycond default
    if not get_key(cur, "Server", "Server1Name"):
        cur = set_key(cur, "Server", "Server1Name", "BetterJoy")
        cur = set_key(cur, "Server", "Server1IP", "127.0.0.1")
        cur = set_key(cur, "Server", "Server1Port", "26760")
    path.write_text(cur)


def write_profile(path, body):
    path.parent.mkdir(parents=True, exist_ok=True)
    # Strip [Wiimote1] header for profile format
    text = body
    if text.startswith("[Wiimote"):
        text = re.sub(r"^\[Wiimote\d+\]\n", "[Profile]\n", text, count=1)
    path.write_text(text)


def patch_wiimote_file(path, remotes, fallback_dev):
    if remotes:
        parts = []
        for i, (name, ext, nunchuk, sideways) in enumerate(remotes, start=1):
            parts.append(
                build_remote(
                    i,
                    sdl(name),
                    extension=ext,
                    nunchuk_dev=sdl(nunchuk) if nunchuk else None,
                    sideways=sideways,
                )
            )
        for i in range(len(remotes) + 1, 5):
            parts.append("[Wiimote%d]\nSource = 0\n" % i)
        parts.append("[BalanceBoard]\nSource = 0\n")
        path.write_text("".join(parts))
        return len(remotes)

    # Deck / pad fallback — keep previous soft-merge behaviour
    cur = path.read_text(errors="ignore") if path.exists() else ""
    if not cur.strip():
        cur = "[Wiimote1]\nSource = 1\n"
    cur = set_key(cur, "Wiimote1", "Source", "1")
    cur = set_key(cur, "Wiimote1", "Device", fallback_dev)
    for k, v in DECK_IMU.items():
        existing = get_key(cur, "Wiimote1", k)
        if "SteamDeck/0/Steam Deck" in existing and "DSUClient" in existing:
            continue
        cur = set_key(cur, "Wiimote1", k, v)
    ir_up = get_key(cur, "Wiimote1", "IR/Up")
    if "Cursor" not in ir_up:
        for k, v in IR.items():
            cur = set_key(cur, "Wiimote1", k, v)
    else:
        cur = set_key(cur, "Wiimote1", "IR/Auto-Hide", "False")
    cur = set_key(cur, "Wiimote1", "IMUIR/Enabled", "True")
    if not get_key(cur, "Wiimote1", "Extension"):
        cur = set_key(cur, "Wiimote1", "Extension", "Nunchuk")
    path.write_text(cur)
    return 1


def patch_gcpad(path, device):
    if not path.exists():
        return
    cur = path.read_text(errors="ignore")
    if not cur.strip():
        return
    if get_key(cur, "GCPad1", "Device") or "[GCPad1]" in cur:
        cur = set_key(cur, "GCPad1", "Device", device)
        path.write_text(cur)


def dirs():
    home = os.path.expanduser("~")
    return [
        os.path.join(home, ".var/app/org.DolphinEmu.dolphin-emu/config/dolphin-emu"),
        os.path.join(home, ".config/dolphin-emu"),
    ]


def main():
    pool = names()
    remotes = build_joycon_plan(pool)
    fallback = fallback_device(pool)
    primary = sdl(remotes[0][0]) if remotes else fallback

    for d in dirs():
        p = pathlib.Path(d)
        if not p.exists():
            continue
        try:
            count = patch_wiimote_file(p / "WiimoteNew.ini", remotes, fallback)
            patch_dolphin_ini(p / "Dolphin.ini", count)
            patch_gcpad(p / "GCPadNew.ini", primary if not remotes else fallback)
            enable_dsu(p / "DSUClient.ini")
            profiles = p / "Profiles" / "Wiimote"
            if remotes:
                name, ext, nunchuk, sideways = remotes[0]
                write_profile(
                    profiles / "SESAME-joycon.ini",
                    build_remote(
                        1,
                        sdl(name),
                        extension=ext,
                        nunchuk_dev=sdl(nunchuk) if nunchuk else None,
                        sideways=sideways,
                    ),
                )
                # Always also ship a nunchuk pair profile for Galaxy-style games
                if len(remotes) >= 1:
                    # Prefer explicit L+R if present in plan
                    pair = next((r for r in remotes if r[1] == "Nunchuk"), None)
                    if pair:
                        write_profile(
                            profiles / "SESAME-joycon-nunchuk.ini",
                            build_remote(
                                1,
                                sdl(pair[0]),
                                extension="Nunchuk",
                                nunchuk_dev=sdl(pair[2]),
                                sideways=False,
                            ),
                        )
                write_profile(
                    profiles / "SESAME-joycon-remote.ini",
                    build_remote(1, sdl(name), extension=None, sideways=True),
                )
        except Exception:
            pass


if __name__ == "__main__":
    main()
