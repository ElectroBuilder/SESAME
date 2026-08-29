#!/usr/bin/env python3
"""SESAME: Dolphin Wiimote config for Steam Deck / Linux.

Default Joy-Con layout (single player — always):
  Right Joy-Con  → one Emulated Wiimote (buttons + gyro / MotionPlus)
  Left Joy-Con   → Nunchuk extension on that same Wiimote (stick + C/Z)

Wiimote 2–4 stay OFF unless SESAME_WII_MULTI=1.

Motion (reliable): joycond-cemuhook DSU on UDP 26761 (started by sesame-joycon-dsu.sh).
Fallback: SteamDeckGyroDSU / Deck IMU on 26760.
Aiming without a sensor bar uses Dolphin IMUIR (gyro → pointer).
Home on the Right Joy-Con recenters the pointer when it drifts.

Guide: https://system-maid.neocities.org/post/joycond-cemuhook/
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

# Mouse IR only for Deck/pad fallback — fights gyro when Joy-Cons aim via IMUIR.
CURSOR_IR = {
    "IR/Up": "`Cursor Y-`|`XInput2/0/Virtual core pointer:Cursor Y-`",
    "IR/Down": "`Cursor Y+`|`XInput2/0/Virtual core pointer:Cursor Y+`",
    "IR/Left": "`Cursor X-`|`XInput2/0/Virtual core pointer:Cursor X-`",
    "IR/Right": "`Cursor X+`|`XInput2/0/Virtual core pointer:Cursor X+`",
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

NUNCHUK_ACCEL = [
    ("Nunchuk/IMUAccelerometer/Up", "Accel Up"),
    ("Nunchuk/IMUAccelerometer/Down", "Accel Down"),
    ("Nunchuk/IMUAccelerometer/Left", "Accel Left"),
    ("Nunchuk/IMUAccelerometer/Right", "Accel Right"),
    ("Nunchuk/IMUAccelerometer/Forward", "Accel Forward"),
    ("Nunchuk/IMUAccelerometer/Backward", "Accel Backward"),
]


def log(msg):
    try:
        home = os.path.expanduser("~")
        path = pathlib.Path(home) / ".local/share/sesame/dolphin-joycon.log"
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as f:
            f.write(msg.rstrip() + "\n")
    except Exception:
        pass


def names():
    """Unique input device names (Joy-Cons often appear twice in /proc)."""
    try:
        raw = pathlib.Path("/proc/bus/input/devices").read_text(errors="ignore")
    except Exception:
        return []
    return list(dict.fromkeys(re.findall(r'N: Name="([^"]+)"', raw)))


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
    return "`%s:%s`" % (device, control)


def side_of(name):
    low = (name or "").lower()
    if any(
        x in low
        for x in (
            "joy-con (l)",
            "joycon (l)",
            "joy-con(l)",
            "left joy-con",
            "joy-con left",
            "nintendo switch left joy-con",
            "(l)",
        )
    ) and "right" not in low and "(r)" not in low:
        # bare "(l)" is risky; require joy/con context unless explicit left
        if "joy" in low or "left" in low:
            return "L"
    if any(
        x in low
        for x in (
            "joy-con (r)",
            "joycon (r)",
            "joy-con(r)",
            "right joy-con",
            "joy-con right",
            "nintendo switch right joy-con",
        )
    ):
        return "R"
    if ("(r)" in low or " right" in low) and "joy" in low:
        return "R"
    if ("(l)" in low or " left" in low) and "joy" in low:
        return "L"
    if "combined" in low and ("joy" in low or "switch" in low):
        return "C"
    if "joy-con" in low or "joycon" in low:
        return "?"
    return ""


def classify(pool):
    left, right, combined, loose = [], [], [], []
    for n in pool:
        low = n.lower()
        if "steam" in low and "virtual" in low:
            continue
        if "steam deck" in low and "joy" not in low:
            continue
        side = side_of(n)
        if side == "L":
            left.append(n)
        elif side == "R":
            right.append(n)
        elif side == "C":
            combined.append(n)
        elif side == "?":
            loose.append(n)
    return left, right, combined, loose


def wiimote_buttons(right_dev):
    """All Wiimote face buttons forced onto the Right Joy-Con device."""
    def b(*controls):
        return "|".join(ref(right_dev, c) for c in controls)

    return {
        "Buttons/A": b("Button A", "Button S", "SOUTH", "EAST"),
        "Buttons/B": b("Button B", "Button ZR", "Trigger R", "Button E", "EAST"),
        "Buttons/1": b("Button X", "Button N", "NORTH"),
        "Buttons/2": b("Button Y", "Button W", "WEST"),
        "Buttons/-": b("Button Minus", "Button Capture", "Button Back", "SELECT"),
        "Buttons/+": b("Button Plus", "Button Start", "START"),
        "Buttons/Home": b("Button Home", "Button Guide", "MODE"),
        "D-Pad/Up": b("Pad N", "Hat 0 N", "Axis 7-", "Left Y-"),
        "D-Pad/Down": b("Pad S", "Hat 0 S", "Axis 7+", "Left Y+"),
        "D-Pad/Left": b("Pad W", "Hat 0 W", "Axis 6-", "Left X-"),
        "D-Pad/Right": b("Pad E", "Hat 0 E", "Axis 6+", "Left X+"),
        "Shake/X": b("Button SL", "Button SR", "Shoulder L", "TL"),
        "Shake/Y": b("Button SL", "Button SR", "Shoulder L", "TL"),
        "Shake/Z": b("Button SL", "Button SR", "Shoulder L", "TL"),
    }


def nunchuk_on(left_dev):
    def b(*controls):
        return "|".join(ref(left_dev, c) for c in controls)

    keys = {
        "Nunchuk/Buttons/C": b("Button L", "Button SL", "Shoulder L", "TL"),
        "Nunchuk/Buttons/Z": b("Button ZL", "Trigger L", "Full Axis 2+", "Axis 2+"),
        "Nunchuk/Stick/Up": b("Left Y-", "Axis 1-"),
        "Nunchuk/Stick/Down": b("Left Y+", "Axis 1+"),
        "Nunchuk/Stick/Left": b("Left X-", "Axis 0-"),
        "Nunchuk/Stick/Right": b("Left X+", "Axis 0+"),
    }
    for key, axis in NUNCHUK_ACCEL:
        keys[key] = "|".join(
            [
                "`DSUClient/0/Nintendo Switch Left Joy-Con:%s`" % axis,
                "`DSUClient/1/Nintendo Switch Left Joy-Con:%s`" % axis,
                "`DSUClient/0/Joy-Con (L):%s`" % axis,
                "`DSUClient/1/Joy-Con (L):%s`" % axis,
                ref(left_dev, axis),
                "`%s`" % axis,
            ]
        )
    return keys


def imu_from_right(right_dev):
    """Wiimote MotionPlus: Joy-Con DSU first, then SDL Right, then Deck fallback."""
    out = {}
    for key, axis in IMU_AXES:
        parts = [
            "`DSUClient/0/Nintendo Switch Right Joy-Con:%s`" % axis,
            "`DSUClient/1/Nintendo Switch Right Joy-Con:%s`" % axis,
            "`DSUClient/0/Joy-Con (R):%s`" % axis,
            "`DSUClient/1/Joy-Con (R):%s`" % axis,
            "`DSUClient/0/Nintendo Switch Combined Joy-Cons:%s`" % axis,
            "`DSUClient/0/:%s`" % axis,
            "`DSUClient/1/:%s`" % axis,
            ref(right_dev, axis),
            "`SteamDeck/0/Steam Deck:%s`" % axis,
            "`DSUClient/0/steamdeckgyro:%s`" % axis,
            "`%s`" % axis,
        ]
        out[key] = "|".join(parts)
    return out


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
    return text[:start] + "\n" + line + body + text[end:]


def get_key(text, section, key):
    span = section_span(text, section)
    if span is None:
        return ""
    body = text[span[1] : span[2]]
    m = re.search(r"^" + re.escape(key) + r"\s*=\s*(.*)$", body, re.I | re.M)
    return m.group(1).strip() if m else ""


def write_section(keys):
    return "\n".join("%s = %s" % (k, v) for k, v in keys.items()) + "\n"


def build_pair(right_name, left_name):
    right = sdl(right_name)
    left = sdl(left_name)
    keys = {
        "Device": right,
        "Source": "1",
        "Extension": "Nunchuk",
        "Options/Sideways Wiimote": "False",
        # Gyro pointer replaces the sensor bar. Lower Total Yaw = less twitchy.
        "IMUIR/Enabled": "True",
        "IMUIR/Recenter": ref(right, "Button Home")
        + "|"
        + ref(right, "Button Guide")
        + "|`Button Home`|MODE",
        "IMUIR/Total Yaw": "16",
        "IR/Auto-Hide": "False",
        "Rumble/Motor": "Strong",
    }
    keys.update(wiimote_buttons(right))
    keys.update(imu_from_right(right))
    keys.update(nunchuk_on(left))
    # Do NOT bind mouse Cursor to IR — that made aiming feel broken next to IMUIR.
    return "[Wiimote1]\n" + write_section(keys)


def build_combined(name):
    """Single combined Joy-Con pad: Wiimote + Nunchuk on the same device."""
    dev = sdl(name)
    keys = {
        "Device": dev,
        "Source": "1",
        "Extension": "Nunchuk",
        "Options/Sideways Wiimote": "False",
        "IMUIR/Enabled": "True",
        "IMUIR/Recenter": "`Button Home`|`Button Guide`|MODE",
        "IMUIR/Total Yaw": "16",
        "IR/Auto-Hide": "False",
        "Rumble/Motor": "Strong",
        "Buttons/A": "`Button A`|`Button S`|SOUTH|EAST",
        "Buttons/B": "`Button B`|`Button ZR`|`Trigger R`|EAST",
        "Buttons/1": "`Button X`|NORTH",
        "Buttons/2": "`Button Y`|WEST",
        "Buttons/-": "`Button Minus`|SELECT",
        "Buttons/+": "`Button Plus`|START",
        "Buttons/Home": "`Button Home`|MODE",
        "D-Pad/Up": "`Pad N`|`Right Y-`",
        "D-Pad/Down": "`Pad S`|`Right Y+`",
        "D-Pad/Left": "`Pad W`|`Right X-`",
        "D-Pad/Right": "`Pad E`|`Right X+`",
        "Shake/X": "`Button SL`|`Button SR`|TL",
        "Shake/Y": "`Button SL`|`Button SR`|TL",
        "Shake/Z": "`Button SL`|`Button SR`|TL",
        "Nunchuk/Buttons/C": "`Button L`|`Shoulder L`|TL",
        "Nunchuk/Buttons/Z": "`Button ZL`|`Trigger L`|`Full Axis 2+`",
        "Nunchuk/Stick/Up": "`Left Y-`|`Axis 1-`",
        "Nunchuk/Stick/Down": "`Left Y+`|`Axis 1+`",
        "Nunchuk/Stick/Left": "`Left X-`|`Axis 0-`",
        "Nunchuk/Stick/Right": "`Left X+`|`Axis 0+`",
    }
    keys.update(imu_from_right(dev))
    return "[Wiimote1]\n" + write_section(keys)


def pick_pair(pool):
    left, right, combined, loose = classify(pool)
    log(
        "devices left=%s right=%s combined=%s loose=%s all=%s"
        % (left, right, combined, loose, [n for n in pool if "joy" in n.lower() or "switch" in n.lower()])
    )

    if right and left:
        return ("pair", right[0], left[0])
    if combined:
        return ("combined", combined[0], None)
    # Two unlabeled Joy-Cons: treat first as Left, second as Right (common when names lack L/R).
    if len(loose) >= 2:
        return ("pair", loose[1], loose[0])
    if len(loose) == 1 and right:
        return ("pair", right[0], loose[0])
    if len(loose) == 1 and left:
        return ("pair", loose[0], left[0])
    return (None, None, None)


def multi_enabled():
    return os.environ.get("SESAME_WII_MULTI", "").strip() in ("1", "true", "yes", "on")


def fallback_device(pool):
    n = find(("joy-con", "nintendo switch combined", "nintendo switch pro"), pool)
    if n:
        return sdl(n)
    n = find(("8bitdo",), pool)
    if n and not is_xboxish(n):
        return sdl(n)
    n = find(("steam virtual gamepad",), pool)
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
    """Dolphin Alternate Input Sources: Joy-Con DSU first, Deck gyro second."""
    cur = path.read_text(errors="ignore") if path.exists() else ""
    if not cur.strip():
        cur = "[Server]\n"
    cur = set_key(cur, "Server", "Enabled", "True")
    # Rewrite so older SESAME builds that only had steamdeckgyro are corrected.
    cur = set_key(cur, "Server", "Server1Name", "joycond")
    cur = set_key(cur, "Server", "Server1IP", "127.0.0.1")
    cur = set_key(cur, "Server", "Server1Port", os.environ.get("SESAME_JOYCON_DSU_PORT", "26761"))
    cur = set_key(cur, "Server", "Server2Name", "steamdeckgyro")
    cur = set_key(cur, "Server", "Server2IP", "127.0.0.1")
    cur = set_key(cur, "Server", "Server2Port", "26760")
    path.write_text(cur)


def write_profile(path, wiimote_body):
    path.parent.mkdir(parents=True, exist_ok=True)
    text = re.sub(r"^\[Wiimote\d+\]\n", "[Profile]\n", wiimote_body, count=1)
    path.write_text(text)


def inactive_wiimotes(start=2):
    parts = []
    for i in range(start, 5):
        parts.append("[Wiimote%d]\nSource = 0\n" % i)
    parts.append("[BalanceBoard]\nSource = 0\n")
    return "".join(parts)


def write_joycon_config(path, kind, right_name, left_name):
    if kind == "pair":
        body = build_pair(right_name, left_name)
        log("mode=pair right=%s left=%s → Wiimote1+Nunchuk only" % (right_name, left_name))
    else:
        body = build_combined(right_name)
        log("mode=combined device=%s → Wiimote1+Nunchuk only" % right_name)
    path.write_text(body + inactive_wiimotes(2))
    return 1


def patch_fallback(path, device):
    cur = path.read_text(errors="ignore") if path.exists() else ""
    if not cur.strip():
        cur = "[Wiimote1]\nSource = 1\n"
    cur = set_key(cur, "Wiimote1", "Source", "1")
    cur = set_key(cur, "Wiimote1", "Device", device)
    for k, v in DECK_IMU.items():
        cur = set_key(cur, "Wiimote1", k, v)
    for k, v in CURSOR_IR.items():
        cur = set_key(cur, "Wiimote1", k, v)
    cur = set_key(cur, "Wiimote1", "IMUIR/Enabled", "True")
    cur = set_key(cur, "Wiimote1", "IMUIR/Total Yaw", "16")
    cur = set_key(cur, "Wiimote1", "IMUIR/Recenter", "`Button Guide`|MODE")
    if not get_key(cur, "Wiimote1", "Extension"):
        cur = set_key(cur, "Wiimote1", "Extension", "Nunchuk")
    # Force other remotes off so leftover Joy-Cons never become Wiimote2.
    for i in range(2, 5):
        cur = set_key(cur, "Wiimote%d" % i, "Source", "0")
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
    kind, right_name, left_name = pick_pair(pool)
    fallback = fallback_device(pool)

    for d in dirs():
        p = pathlib.Path(d)
        if not p.exists():
            continue
        try:
            profiles = p / "Profiles" / "Wiimote"
            if kind == "pair":
                count = write_joycon_config(p / "WiimoteNew.ini", "pair", right_name, left_name)
                body = build_pair(right_name, left_name)
                write_profile(profiles / "SESAME-joycon.ini", body)
                write_profile(profiles / "SESAME-joycon-nunchuk.ini", body)
                primary = sdl(right_name)
            elif kind == "combined":
                count = write_joycon_config(p / "WiimoteNew.ini", "combined", right_name, None)
                body = build_combined(right_name)
                write_profile(profiles / "SESAME-joycon.ini", body)
                write_profile(profiles / "SESAME-joycon-nunchuk.ini", body)
                primary = sdl(right_name)
            else:
                log("mode=fallback device=%s (no Joy-Con L/R pair seen)" % fallback)
                count = patch_fallback(p / "WiimoteNew.ini", fallback)
                primary = fallback

            # Multiplayer remotes only when explicitly requested.
            if multi_enabled() and kind == "pair":
                left, right, _, loose = classify(pool)
                extras = right[1:] + left[1:] + loose
                # Keep Wiimote1 as pair; add remotes without touching Wiimote1.
                # (Advanced — default stays single Wiimote.)
                log("SESAME_WII_MULTI extras ignored in this build beyond logging: %s" % extras)

            patch_dolphin_ini(p / "Dolphin.ini", count)
            patch_gcpad(p / "GCPadNew.ini", primary)
            enable_dsu(p / "DSUClient.ini")
        except Exception as ex:
            log("error in %s: %s" % (d, ex))


if __name__ == "__main__":
    main()
