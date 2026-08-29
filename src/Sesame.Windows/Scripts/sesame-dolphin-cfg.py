#!/usr/bin/env python3
"""SESAME: Dolphin Wiimote config for Steam Deck / Linux.

Default Joy-Con layout (single player — always):
  Right Joy-Con  → one Emulated Wiimote (buttons + gyro / MotionPlus)
  Left Joy-Con   → Nunchuk extension on that same Wiimote (stick + C/Z)

Wiimote 2–4 stay OFF unless SESAME_WII_MULTI=1.

When joycond-cemuhook DSU is ok (UDP 26761):
  Device bindings are DSU-ONLY — Nintendo Switch Right/Left Joy-Con.
  No SDL Combined / pair / joycon-pair OR-ed in. Dolphin may still *list*
  every OS pad in dropdowns; SESAME only wires Wiimote1 to DSU R + Nunchuk L.

Fallback (no DSU): separate L/R SDL pads only — never Combined/pair as Device.
GCPad is forced off Joy-Cons (Steam Virtual Gamepad).

Guide: https://system-maid.neocities.org/post/joycond-cemuhook/
Wiki: https://github.com/joaorb64/joycond-cemuhook/wiki
"""
from __future__ import annotations

import os
import pathlib
import re

# joycond-cemuhook publishes these exact names over DSU (not "Joy-Con (R)").
DSU_RIGHT = "Nintendo Switch Right Joy-Con"
DSU_LEFT = "Nintendo Switch Left Joy-Con"
DSU_DEV_R = "DSUClient/0/%s" % DSU_RIGHT
DSU_DEV_L = "DSUClient/0/%s" % DSU_LEFT

SAFE_GCPAD = "SDL/0/Steam Virtual Gamepad"

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


def is_combined_or_pair(name):
    """Combined / joycon-pair / 'Nintendo Switch Combined Joy-Cons' — never wire these."""
    low = (name or "").lower()
    if "combined" in low and ("joy" in low or "switch" in low):
        return True
    if "joycon-pair" in low or "joy-con-pair" in low:
        return True
    if "joy-con pair" in low or "joycon pair" in low:
        return True
    if "pair" in low and "joy" in low and "pro" not in low:
        return True
    return False


def is_joyconish(name):
    low = (name or "").lower()
    return "joy-con" in low or "joycon" in low or (
        "nintendo switch" in low and "pro" not in low
    )


def sdl(name):
    return "SDL/0/%s" % name


def ref(device, control):
    return "`%s:%s`" % (device, control)


def side_of(name):
    low = (name or "").lower()
    if is_combined_or_pair(name):
        return "C"
    if any(
        x in low
        for x in (
            "joy-con (l)",
            "joycon (l)",
            "joy-con(l)",
            "left joy-con",
            "joy-con left",
            "nintendo switch left joy-con",
        )
    ):
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


def dsu_status():
    path = pathlib.Path(os.path.expanduser("~")) / ".local/share/sesame/joycon-dsu.status"
    try:
        return path.read_text(encoding="utf-8").strip().lower()
    except Exception:
        return ""


def dsu_ok():
    return dsu_status() == "ok"


def dsu_port():
    env = os.environ.get("SESAME_JOYCON_DSU_PORT", "").strip()
    if env.isdigit():
        return env
    path = pathlib.Path(os.path.expanduser("~")) / ".local/share/sesame/joycon-dsu.port"
    try:
        p = path.read_text(encoding="utf-8").strip()
        if p.isdigit():
            return p
    except Exception:
        pass
    return "26761"


def wiimote_buttons_dsu():
    """Right Joy-Con via cemuhook DualShock-style names only (no SDL OR)."""
    r = DSU_DEV_R

    def b(*controls):
        return "|".join(ref(r, c) for c in controls)

    return {
        "Buttons/A": b("Button Circle", "Button East", "Button A"),
        "Buttons/B": b("Button Cross", "Button South", "Button B", "Button R2"),
        "Buttons/1": b("Button Triangle", "Button North", "Button X"),
        "Buttons/2": b("Button Square", "Button West", "Button Y"),
        "Buttons/-": b("Button Share", "Button Minus", "Button Capture"),
        "Buttons/+": b("Button Options", "Button Plus"),
        "Buttons/Home": b("Button PS", "Button Home", "Button Guide"),
        "D-Pad/Up": b("Pad N", "Hat 0 N", "Left Y-"),
        "D-Pad/Down": b("Pad S", "Hat 0 S", "Left Y+"),
        "D-Pad/Left": b("Pad W", "Hat 0 W", "Left X-"),
        "D-Pad/Right": b("Pad E", "Hat 0 E", "Left X+"),
        "Shake/X": b("Button R1", "Button SL", "Button SR"),
        "Shake/Y": b("Button R1", "Button SL", "Button SR"),
        "Shake/Z": b("Button R1", "Button SL", "Button SR"),
    }


def nunchuk_dsu():
    """Left Joy-Con → Nunchuk, DSU only."""
    l = DSU_DEV_L

    def b(*controls):
        return "|".join(ref(l, c) for c in controls)

    keys = {
        "Nunchuk/Buttons/C": b("Button L1", "Button SL", "Button L", "Shoulder L"),
        "Nunchuk/Buttons/Z": b("Button L2", "Button ZL", "Trigger L", "Full Axis 2+"),
        "Nunchuk/Stick/Up": b("Left Y-", "Axis 1-"),
        "Nunchuk/Stick/Down": b("Left Y+", "Axis 1+"),
        "Nunchuk/Stick/Left": b("Left X-", "Axis 0-"),
        "Nunchuk/Stick/Right": b("Left X+", "Axis 0+"),
    }
    for key, axis in NUNCHUK_ACCEL:
        keys[key] = ref(l, axis)
    return keys


def imu_dsu():
    """MotionPlus from Right DSU pad only."""
    out = {}
    for key, axis in IMU_AXES:
        out[key] = ref(DSU_DEV_R, axis)
    return out


def wiimote_buttons_sdl(right_dev):
    def b(*controls):
        return "|".join(ref(right_dev, c) for c in controls)

    return {
        "Buttons/A": b("Button East", "Button Circle", "Button A", "EAST"),
        "Buttons/B": b(
            "Button South", "Button Cross", "Button B", "SOUTH",
            "Button ZR", "Button R2", "Trigger R",
        ),
        "Buttons/1": b("Button North", "Button Triangle", "Button X", "NORTH"),
        "Buttons/2": b("Button West", "Button Square", "Button Y", "WEST"),
        "Buttons/-": b("Button Minus", "Button Capture", "Button Share", "SELECT"),
        "Buttons/+": b("Button Plus", "Button Options", "START"),
        "Buttons/Home": b("Button Home", "Button Guide", "Button PS", "MODE"),
        "D-Pad/Up": b("Pad N", "Hat 0 N", "Left Y-", "Axis 1-"),
        "D-Pad/Down": b("Pad S", "Hat 0 S", "Left Y+", "Axis 1+"),
        "D-Pad/Left": b("Pad W", "Hat 0 W", "Left X-", "Axis 0-"),
        "D-Pad/Right": b("Pad E", "Hat 0 E", "Left X+", "Axis 0+"),
        "Shake/X": b("Button SL", "Button SR", "Button R1", "Shoulder R", "TR"),
        "Shake/Y": b("Button SL", "Button SR", "Button R1", "Shoulder R", "TR"),
        "Shake/Z": b("Button SL", "Button SR", "Button R1", "Shoulder R", "TR"),
    }


def nunchuk_sdl(left_dev):
    def b(*controls):
        return "|".join(ref(left_dev, c) for c in controls)

    keys = {
        "Nunchuk/Buttons/C": b("Button SL", "Button L", "Button L1", "Shoulder L", "TL"),
        "Nunchuk/Buttons/Z": b("Button ZL", "Button L2", "Trigger L", "Full Axis 2+", "Axis 2+"),
        "Nunchuk/Stick/Up": b("Left Y-", "Axis 1-"),
        "Nunchuk/Stick/Down": b("Left Y+", "Axis 1+"),
        "Nunchuk/Stick/Left": b("Left X-", "Axis 0-"),
        "Nunchuk/Stick/Right": b("Left X+", "Axis 0+"),
    }
    for key, axis in NUNCHUK_ACCEL:
        keys[key] = ref(left_dev, axis)
    return keys


def imu_sdl(right_dev):
    out = {}
    for key, axis in IMU_AXES:
        out[key] = "|".join(
            [
                ref(right_dev, axis),
                "`SteamDeck/0/Steam Deck:%s`" % axis,
                "`DSUClient/0/steamdeckgyro:%s`" % axis,
                "`%s`" % axis,
            ]
        )
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


def build_dsu_only():
    """Hardcoded DSU Right Wiimote + DSU Left Nunchuk — no SDL/Combined ORs."""
    keys = {
        "Device": DSU_DEV_R,
        "Source": "1",
        "Extension": "Nunchuk",
        "Options/Sideways Wiimote": "False",
        "IMUIR/Enabled": "True",
        "IMUIR/Recenter": "`%s:Button PS`|`%s:Button Home`|MODE" % (DSU_DEV_R, DSU_DEV_R),
        "IMUIR/Total Yaw": "16",
        "IR/Auto-Hide": "False",
        "Rumble/Motor": "Strong",
    }
    keys.update(wiimote_buttons_dsu())
    keys.update(imu_dsu())
    keys.update(nunchuk_dsu())
    log("build_dsu_only device=%s nunchuk=%s port=%s" % (DSU_DEV_R, DSU_DEV_L, dsu_port()))
    return "[Wiimote1]\n" + write_section(keys)


def build_sdl_pair(right_name, left_name):
    """Separate L/R SDL only — never Combined. Used when DSU is not ok."""
    right, left = sdl(right_name), sdl(left_name)
    keys = {
        "Device": right,
        "Source": "1",
        "Extension": "Nunchuk",
        "Options/Sideways Wiimote": "False",
        "IMUIR/Enabled": "True",
        "IMUIR/Recenter": "`Button Home`|`Button Guide`|MODE",
        "IMUIR/Total Yaw": "16",
        "IR/Auto-Hide": "False",
        "Rumble/Motor": "Strong",
    }
    keys.update(wiimote_buttons_sdl(right))
    keys.update(imu_sdl(right))
    keys.update(nunchuk_sdl(left))
    log("build_sdl_pair device=%s nunchuk=%s" % (right, left))
    return "[Wiimote1]\n" + write_section(keys)


def pick_mode(pool):
    """Choose wiring mode. Combined/pair is never the primary Device."""
    left, right, combined, loose = classify(pool)
    if combined:
        log(
            "WARN: Combined/pair device(s) present (IGNORED for Wiimote Device): %s — "
            "re-pair each Joy-Con with SL+SR (not L+R). They may still appear in Dolphin's "
            "dropdown but will not be wired." % combined
        )

    if dsu_ok():
        log("mode=dsu (cemuhook ok) — ignoring /proc Combined and SDL Joy-Con names for Device")
        return ("dsu", DSU_RIGHT, DSU_LEFT)

    if right and left:
        return ("pair", right[0], left[0])

    # Never select Combined — fall through to Deck/virtual fallback.
    if combined:
        log("Combined seen but DSU not ok and no separate L/R — using non-Joy-Con fallback")

    if len(loose) >= 2 and not any(is_combined_or_pair(n) for n in loose):
        return ("pair", loose[1], loose[0])
    if len(loose) == 1 and right and not is_combined_or_pair(loose[0]):
        return ("pair", right[0], loose[0])
    if len(loose) == 1 and left and not is_combined_or_pair(loose[0]):
        return ("pair", loose[0], left[0])
    return (None, None, None)


def multi_enabled():
    return os.environ.get("SESAME_WII_MULTI", "").strip() in ("1", "true", "yes", "on")


def fallback_device(pool):
    """Never Joy-Con Combined/pair — prefer Deck / Steam Virtual Gamepad."""
    n = find(("nintendo switch pro",), pool)
    if n:
        return sdl(n)
    n = find(("8bitdo",), pool)
    if n and not is_xboxish(n):
        return sdl(n)
    n = find(("steam virtual gamepad",), pool)
    if n:
        return sdl(n)
    n = find(("steam deck",), pool)
    if n and "virtual" not in n.lower() and "joy" not in n.lower():
        return "SteamDeck/0/Steam Deck"
    if os.environ.get("SteamAppId") or os.environ.get("SteamGameId") or os.environ.get("SteamDeck"):
        return SAFE_GCPAD
    return "SDL/0/Steam Deck Controller"


def gcpad_device(pool):
    """GCPad must never grab Joy-Cons / Combined."""
    return SAFE_GCPAD


def patch_dolphin_ini(path, wiimote_count):
    cur = path.read_text(errors="ignore") if path.exists() else "[Core]\n"
    cur = set_key(cur, "Core", "SIDevice0", "6")
    for i in range(4):
        cur = set_key(cur, "Core", "WiimoteSource%d" % i, "1" if i < wiimote_count else "0")
    cur = set_key(cur, "Input", "BackgroundInput", "True")
    path.write_text(cur)


def enable_dsu(path):
    """Dolphin Alternate Input Sources: Joy-Con DSU (actual port) + Deck gyro."""
    cur = path.read_text(errors="ignore") if path.exists() else ""
    if not cur.strip():
        cur = "[Server]\n"
    port = dsu_port()
    cur = set_key(cur, "Server", "Enabled", "True")
    cur = set_key(cur, "Server", "Server1Name", "joycond")
    cur = set_key(cur, "Server", "Server1IP", "127.0.0.1")
    cur = set_key(cur, "Server", "Server1Port", port)
    cur = set_key(cur, "Server", "Server2Name", "steamdeckgyro")
    cur = set_key(cur, "Server", "Server2IP", "127.0.0.1")
    cur = set_key(cur, "Server", "Server2Port", "26760")
    path.write_text(cur)
    log("DSUClient.ini joycond port=%s status=%s" % (port, dsu_status()))


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


def write_wiimote_full(path, body):
    """Full overwrite — clears leftover Combined / Wiimote2 junk from prior runs."""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(body + inactive_wiimotes(2))
    return 1


def patch_fallback(path, device):
    """Deck/pad fallback — still full rewrite, never leave Combined Device."""
    keys = {
        "Device": device,
        "Source": "1",
        "Extension": "Nunchuk",
        "Options/Sideways Wiimote": "False",
        "IMUIR/Enabled": "True",
        "IMUIR/Total Yaw": "16",
        "IMUIR/Recenter": "`Button Guide`|MODE",
        "Rumble/Motor": "Strong",
    }
    keys.update(DECK_IMU)
    keys.update(CURSOR_IR)
    body = "[Wiimote1]\n" + write_section(keys)
    write_wiimote_full(path, body)
    return 1


def patch_gcpad(path, device):
    """Force GCPad1 off Joy-Cons / Combined — full section rewrite when present."""
    safe = device if device and not is_joyconish(device) else SAFE_GCPAD
    if is_combined_or_pair(safe) or is_joyconish(safe):
        safe = SAFE_GCPAD
    cur = path.read_text(errors="ignore") if path.exists() else ""
    if not cur.strip():
        # Write a minimal GCPad so leftover empty files don't get Joy-Cons later.
        path.write_text("[GCPad1]\nDevice = %s\nSource = 0\n" % safe)
        log("GCPadNew.ini created Device=%s (Source=0)" % safe)
        return
    # Keep existing bindings but force Device away from Joy-Cons; disable if was Joy-Con.
    was = get_key(cur, "GCPad1", "Device")
    cur = set_key(cur, "GCPad1", "Device", safe)
    if was and is_joyconish(was):
        cur = set_key(cur, "GCPad1", "Source", "0")
        log("GCPad1 was Joy-Con (%s) → Device=%s Source=0" % (was, safe))
    else:
        log("GCPad1 Device=%s (was %s)" % (safe, was or "(empty)"))
    path.write_text(cur)


def dirs():
    home = os.path.expanduser("~")
    return [
        os.path.join(home, ".var/app/org.DolphinEmu.dolphin-emu/config/dolphin-emu"),
        os.path.join(home, ".config/dolphin-emu"),
    ]


def main():
    pool = names()
    kind, right_name, left_name = pick_mode(pool)
    fallback = fallback_device(pool)
    pad = gcpad_device(pool)

    for d in dirs():
        p = pathlib.Path(d)
        if not p.exists():
            continue
        try:
            profiles = p / "Profiles" / "Wiimote"
            if kind == "dsu":
                body = build_dsu_only()
                count = write_wiimote_full(p / "WiimoteNew.ini", body)
                write_profile(profiles / "SESAME-joycon.ini", body)
                write_profile(profiles / "SESAME-joycon-nunchuk.ini", body)
            elif kind == "pair":
                body = build_sdl_pair(right_name, left_name)
                count = write_wiimote_full(p / "WiimoteNew.ini", body)
                write_profile(profiles / "SESAME-joycon.ini", body)
                write_profile(profiles / "SESAME-joycon-nunchuk.ini", body)
            else:
                log("mode=fallback device=%s (no separate Joy-Con L/R; Combined ignored)" % fallback)
                count = patch_fallback(p / "WiimoteNew.ini", fallback)

            if multi_enabled() and kind in ("pair", "dsu"):
                left, right, _, loose = classify(pool)
                extras = right[1:] + left[1:] + loose
                log("SESAME_WII_MULTI extras ignored in this build beyond logging: %s" % extras)

            patch_dolphin_ini(p / "Dolphin.ini", count)
            patch_gcpad(p / "GCPadNew.ini", pad)
            enable_dsu(p / "DSUClient.ini")
        except Exception as ex:
            log("error in %s: %s" % (d, ex))


if __name__ == "__main__":
    main()
