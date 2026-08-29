#!/usr/bin/env python3
"""SESAME: Dolphin Wiimote config for Steam Deck / Linux.

Preferred layout (single player Wiimote + Nunchuk):
  Combined Joy-Cons (L+R) → one Emulated Wiimote
    Left half  → Nunchuk (stick + C/Z)
    Right half → Wiimote buttons + motion (Right IMU via cemuhook -r)

Official joycond-cemuhook path: Combined + -r/--right-only for Right IMU.

Dolphin *can* cross-bind Left while Device=Right via `LeftDevice:control`,
but Combined is simpler (one Device, one Steam player, left stick→Nunchuk /
right buttons→Wiimote).

Fallback when only separate L+R exist:
  Device = Right, Nunchuk bindings from Left (SDL and/or DSU).

Never write a DSU Device name unless joycon-dsu.status is "ok" (process alive
AND at least one Combined/L/R pad was present). That avoids Dolphin showing
[disconnected] DSUClient/... after a false-ok status.

Wiimote 2–4 stay OFF unless SESAME_WII_MULTI=1.
GCPad is forced off Joy-Cons (Steam Virtual Gamepad).

Guide: https://system-maid.neocities.org/post/joycond-cemuhook/
Wiki: https://github.com/joaorb64/joycond-cemuhook/wiki
"""
from __future__ import annotations

import os
import pathlib
import re

# joycond-cemuhook publishes these exact names over DSU.
DSU_COMBINED = "Nintendo Switch Combined Joy-Cons"
DSU_RIGHT = "Nintendo Switch Right Joy-Con"
DSU_LEFT = "Nintendo Switch Left Joy-Con"
DSU_DEV_C = "DSUClient/0/%s" % DSU_COMBINED
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
    """Combined / joycon-pair / 'Nintendo Switch Combined Joy-Cons'."""
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


def or_join(*parts):
    seen = []
    for p in parts:
        if not p:
            continue
        for bit in str(p).split("|"):
            bit = bit.strip()
            if bit and bit not in seen:
                seen.append(bit)
    return "|".join(seen)


def side_of(name):
    low = (name or "").lower()
    if is_combined_or_pair(name):
        return "C"
    if "imu" in low:
        return ""
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
        if "imu" in low:
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
    """True only when sesame-joycon-dsu.sh verified process + pads."""
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


def wiimote_buttons(device, dualshock=True):
    """Wiimote face/dpad/+/- /home/shake on one device (Combined right half or Right)."""

    def b(*controls):
        return "|".join(ref(device, c) for c in controls)

    if dualshock:
        return {
            "Buttons/A": b("Button Circle", "Button East", "Button A"),
            "Buttons/B": b("Button Cross", "Button South", "Button B", "Button R2"),
            "Buttons/1": b("Button Triangle", "Button North", "Button X"),
            "Buttons/2": b("Button Square", "Button West", "Button Y"),
            "Buttons/-": b("Button Share", "Button Minus", "Button Capture"),
            "Buttons/+": b("Button Options", "Button Plus"),
            "Buttons/Home": b("Button PS", "Button Home", "Button Guide"),
            "D-Pad/Up": b("Pad N", "Hat 0 N", "Right Y-", "Left Y-"),
            "D-Pad/Down": b("Pad S", "Hat 0 S", "Right Y+", "Left Y+"),
            "D-Pad/Left": b("Pad W", "Hat 0 W", "Right X-", "Left X-"),
            "D-Pad/Right": b("Pad E", "Hat 0 E", "Right X+", "Left X+"),
            "Shake/X": b("Button R1", "Button SL", "Button SR"),
            "Shake/Y": b("Button R1", "Button SL", "Button SR"),
            "Shake/Z": b("Button R1", "Button SL", "Button SR"),
        }
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
        "D-Pad/Up": b("Pad N", "Hat 0 N", "Right Y-", "Left Y-", "Axis 1-"),
        "D-Pad/Down": b("Pad S", "Hat 0 S", "Right Y+", "Left Y+", "Axis 1+"),
        "D-Pad/Left": b("Pad W", "Hat 0 W", "Right X-", "Left X-", "Axis 0-"),
        "D-Pad/Right": b("Pad E", "Hat 0 E", "Right X+", "Left X+", "Axis 0+"),
        "Shake/X": b("Button SL", "Button SR", "Button R1", "Shoulder R", "TR"),
        "Shake/Y": b("Button SL", "Button SR", "Button R1", "Shoulder R", "TR"),
        "Shake/Z": b("Button SL", "Button SR", "Button R1", "Shoulder R", "TR"),
    }


def nunchuk_buttons(device, dualshock=True):
    """Nunchuk from Combined left half or separate Left."""

    def b(*controls):
        return "|".join(ref(device, c) for c in controls)

    if dualshock:
        keys = {
            "Nunchuk/Buttons/C": b("Button L1", "Button SL", "Button L", "Shoulder L"),
            "Nunchuk/Buttons/Z": b("Button L2", "Button ZL", "Trigger L", "Full Axis 2+"),
            "Nunchuk/Stick/Up": b("Left Y-", "Axis 1-"),
            "Nunchuk/Stick/Down": b("Left Y+", "Axis 1+"),
            "Nunchuk/Stick/Left": b("Left X-", "Axis 0-"),
            "Nunchuk/Stick/Right": b("Left X+", "Axis 0+"),
        }
    else:
        keys = {
            "Nunchuk/Buttons/C": b("Button SL", "Button L", "Button L1", "Shoulder L", "TL"),
            "Nunchuk/Buttons/Z": b("Button ZL", "Button L2", "Trigger L", "Full Axis 2+", "Axis 2+"),
            "Nunchuk/Stick/Up": b("Left Y-", "Axis 1-"),
            "Nunchuk/Stick/Down": b("Left Y+", "Axis 1+"),
            "Nunchuk/Stick/Left": b("Left X-", "Axis 0-"),
            "Nunchuk/Stick/Right": b("Left X+", "Axis 0+"),
        }
    for key, axis in NUNCHUK_ACCEL:
        keys[key] = ref(device, axis)
    return keys


def imu_from(device, extra_fallback=True):
    out = {}
    for key, axis in IMU_AXES:
        parts = [ref(device, axis)]
        if extra_fallback:
            parts.extend(
                [
                    "`SteamDeck/0/Steam Deck:%s`" % axis,
                    "`DSUClient/0/steamdeckgyro:%s`" % axis,
                    "`%s`" % axis,
                ]
            )
        out[key] = "|".join(parts)
    return out


def merge_binding_dicts(*dicts):
    """OR-merge binding values for the same keys (SDL primary + optional DSU IMU)."""
    out = {}
    for d in dicts:
        for k, v in d.items():
            out[k] = or_join(out.get(k), v)
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


def build_combined(device, dualshock, imu_extra=None):
    """One Device for Wiimote + Nunchuk (Combined preferred)."""
    keys = {
        "Device": device,
        "Source": "1",
        "Extension": "Nunchuk",
        "Options/Sideways Wiimote": "False",
        "IMUIR/Enabled": "True",
        "IMUIR/Recenter": or_join(
            ref(device, "Button PS"),
            ref(device, "Button Home"),
            ref(device, "Button Guide"),
            "MODE",
        ),
        "IMUIR/Total Yaw": "16",
        "IR/Auto-Hide": "False",
        "Rumble/Motor": "Strong",
    }
    keys.update(wiimote_buttons(device, dualshock=dualshock))
    keys.update(nunchuk_buttons(device, dualshock=dualshock))
    imu = imu_from(device, extra_fallback=not dualshock)
    if imu_extra:
        imu = merge_binding_dicts(imu, imu_extra)
    keys.update(imu)
    log("build_combined device=%s dualshock=%s" % (device, dualshock))
    return "[Wiimote1]\n" + write_section(keys)


def build_separate(right_dev, left_dev, dualshock_right=False, dualshock_left=False, imu_pref=None):
    """Cross-bind: Device=Right, Nunchuk=Left. Dolphin allows `LeftDevice:control`."""
    keys = {
        "Device": right_dev,
        "Source": "1",
        "Extension": "Nunchuk",
        "Options/Sideways Wiimote": "False",
        "IMUIR/Enabled": "True",
        "IMUIR/Recenter": or_join(
            ref(right_dev, "Button PS"),
            ref(right_dev, "Button Home"),
            ref(right_dev, "Button Guide"),
            "MODE",
        ),
        "IMUIR/Total Yaw": "16",
        "IR/Auto-Hide": "False",
        "Rumble/Motor": "Strong",
    }
    keys.update(wiimote_buttons(right_dev, dualshock=dualshock_right))
    keys.update(nunchuk_buttons(left_dev, dualshock=dualshock_left))
    if imu_pref:
        keys.update(imu_pref)
    else:
        keys.update(imu_from(right_dev, extra_fallback=True))
    log("build_separate device=%s nunchuk=%s" % (right_dev, left_dev))
    return "[Wiimote1]\n" + write_section(keys)


def pick_and_build(pool):
    left, right, combined, loose = classify(pool)
    ok = dsu_ok()
    log(
        "classify combined=%s left=%s right=%s loose=%s dsu_ok=%s status=%s"
        % (combined, left, right, loose, ok, dsu_status())
    )

    # --- Primary: Combined ---
    if combined:
        name = combined[0]
        if ok:
            # DSU Combined verified — DualShock names; OR SDL Combined so buttons
            # still work if a DSU pad briefly drops (never leave dead DSU-only).
            dsu_body = build_combined(DSU_DEV_C, dualshock=True)
            sdl_body = build_combined(sdl(name), dualshock=False)
            return merge_profiles_prefer_device(dsu_body, sdl_body, DSU_DEV_C)
        # SDL Combined; if status were ok we'd also OR DSU IMU (handled above).
        return build_combined(sdl(name), dualshock=False)

    # Combined not in /proc but DSU ok → still try DSU Combined (cemuhook may expose it).
    if ok and not left and not right:
        log("dsu ok, no /proc Combined/L/R — trying DSU Combined Device")
        return build_combined(DSU_DEV_C, dualshock=True)

    # --- Fallback: separate L+R ---
    if right and left:
        r_name, l_name = right[0], left[0]
        if ok:
            # Prefer SDL for buttons (always live) + DSU Right IMU when verified.
            # Never Device=DSU unless we know pads exist — here ok means pads exist,
            # but Combined is preferred; for separate use Device=SDL Right with DSU IMU OR,
            # or Device=DSU Right if we want full DSU. Prefer hybrid: SDL Device + DSU IMU
            # so buttons work even if one DSU pad flickers; if ok, also allow DSU Device.
            imu = merge_binding_dicts(
                imu_from(DSU_DEV_R, extra_fallback=False),
                imu_from(sdl(r_name), extra_fallback=True),
            )
            # Buttons: OR SDL + DSU so either path works.
            keys_body = build_separate(
                sdl(r_name),
                sdl(l_name),
                dualshock_right=False,
                dualshock_left=False,
                imu_pref=imu,
            )
            # Enrich button lines with DSU refs (cross-bind Left DSU for Nunchuk).
            return enrich_separate_with_dsu(keys_body, sdl(r_name), sdl(l_name))
        return build_separate(
            sdl(r_name),
            sdl(l_name),
            dualshock_right=False,
            dualshock_left=False,
        )

    if ok and right and not left:
        log("dsu ok + Right only — Wiimote without Nunchuk stick from Left")
        return build_separate(
            DSU_DEV_R if ok else sdl(right[0]),
            DSU_DEV_L,
            dualshock_right=True,
            dualshock_left=True,
            imu_pref=imu_from(DSU_DEV_R, extra_fallback=False),
        )

    if len(loose) >= 2 and not any(is_combined_or_pair(n) for n in loose):
        return build_separate(sdl(loose[1]), sdl(loose[0]))
    if len(loose) == 1 and right and not is_combined_or_pair(loose[0]):
        return build_separate(sdl(right[0]), sdl(loose[0]))
    if len(loose) == 1 and left and not is_combined_or_pair(loose[0]):
        return build_separate(sdl(loose[0]), sdl(left[0]))

    return None


def merge_profiles_prefer_device(primary_body, fallback_body, device):
    """Keep Device from primary; OR-merge all other binding values with fallback."""
    prim = {}
    for line in primary_body.splitlines():
        if "=" in line and not line.startswith("["):
            k, _, v = line.partition("=")
            prim[k.strip()] = v.strip()
    fb = {}
    for line in fallback_body.splitlines():
        if "=" in line and not line.startswith("["):
            k, _, v = line.partition("=")
            fb[k.strip()] = v.strip()
    keys = {
        "Device": device,
        "Source": prim.get("Source", "1"),
        "Extension": prim.get("Extension", "Nunchuk"),
        "Options/Sideways Wiimote": prim.get("Options/Sideways Wiimote", "False"),
        "IMUIR/Enabled": prim.get("IMUIR/Enabled", "True"),
        "IMUIR/Total Yaw": prim.get("IMUIR/Total Yaw", "16"),
        "IR/Auto-Hide": prim.get("IR/Auto-Hide", "False"),
        "Rumble/Motor": prim.get("Rumble/Motor", "Strong"),
    }
    all_keys = list(dict.fromkeys(list(prim.keys()) + list(fb.keys())))
    for k in all_keys:
        if k in keys:
            continue
        keys[k] = or_join(prim.get(k), fb.get(k))
    # Recenter: OR both
    keys["IMUIR/Recenter"] = or_join(prim.get("IMUIR/Recenter"), fb.get("IMUIR/Recenter"), "MODE")
    log("merge_profiles device=%s (+ SDL button fallback)" % device)
    return "[Wiimote1]\n" + write_section(keys)


def enrich_separate_with_dsu(wiimote_body, right_sdl, left_sdl):
    """OR DualShock DSU bindings onto an SDL separate profile (buttons + nunchuk)."""
    # Parse keys and merge DSU refs for Wiimote (Right) and Nunchuk (Left).
    lines = wiimote_body.splitlines()
    dsu_w = wiimote_buttons(DSU_DEV_R, dualshock=True)
    dsu_n = nunchuk_buttons(DSU_DEV_L, dualshock=True)
    extra = {}
    extra.update(dsu_w)
    extra.update(dsu_n)
    out = []
    for line in lines:
        if "=" not in line or line.startswith("["):
            out.append(line)
            continue
        key, _, val = line.partition("=")
        key = key.strip()
        val = val.strip()
        if key in ("Device", "Source", "Extension", "Options/Sideways Wiimote",
                   "IMUIR/Enabled", "IMUIR/Total Yaw", "IR/Auto-Hide", "Rumble/Motor"):
            out.append(line)
            continue
        if key in extra:
            out.append("%s = %s" % (key, or_join(val, extra[key])))
        else:
            out.append(line)
    log("enrich_separate_with_dsu right=%s left=%s + DSU L/R" % (right_sdl, left_sdl))
    return "\n".join(out) + "\n"


def multi_enabled():
    return os.environ.get("SESAME_WII_MULTI", "").strip() in ("1", "true", "yes", "on")


def fallback_device(pool):
    """Prefer Deck / Steam Virtual Gamepad when no Joy-Cons."""
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
    """Full overwrite — clears leftover disconnected DSU / Wiimote2 junk."""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(body + inactive_wiimotes(2))
    return 1


def patch_fallback(path, device):
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
    """Force GCPad1 off Joy-Cons / Combined."""
    safe = device if device and not is_joyconish(device) else SAFE_GCPAD
    if is_combined_or_pair(safe) or is_joyconish(safe):
        safe = SAFE_GCPAD
    cur = path.read_text(errors="ignore") if path.exists() else ""
    if not cur.strip():
        path.write_text("[GCPad1]\nDevice = %s\nSource = 0\n" % safe)
        log("GCPadNew.ini created Device=%s (Source=0)" % safe)
        return
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
    body = pick_and_build(pool)
    fallback = fallback_device(pool)
    pad = gcpad_device(pool)

    for d in dirs():
        p = pathlib.Path(d)
        if not p.exists():
            continue
        try:
            profiles = p / "Profiles" / "Wiimote"
            if body:
                # Guard: never leave Device as disconnected DSU if status not ok.
                if "DSUClient/" in body and not dsu_ok():
                    log("REFUSING DSU Device bindings — status=%s; rebuilding without DSU Device" % dsu_status())
                    # Strip to SDL-only by re-picking with forced no-ok is hard; sanitize Device line.
                    body = sanitize_no_dsu_device(body, pool)
                count = write_wiimote_full(p / "WiimoteNew.ini", body)
                write_profile(profiles / "SESAME-joycon.ini", body)
                write_profile(profiles / "SESAME-joycon-nunchuk.ini", body)
            else:
                log("mode=fallback device=%s" % fallback)
                count = patch_fallback(p / "WiimoteNew.ini", fallback)

            if multi_enabled():
                left, right, combined, loose = classify(pool)
                extras = combined[1:] + right[1:] + left[1:] + loose
                log("SESAME_WII_MULTI extras ignored beyond logging: %s" % extras)

            patch_dolphin_ini(p / "Dolphin.ini", count)
            patch_gcpad(p / "GCPadNew.ini", pad)
            enable_dsu(p / "DSUClient.ini")
        except Exception as ex:
            log("error in %s: %s" % (d, ex))


def sanitize_no_dsu_device(body, pool):
    """If Device is a DSUClient path but status is not ok, switch Device to SDL Combined/R."""
    left, right, combined, _ = classify(pool)
    m = re.search(r"^Device\s*=\s*(.*)$", body, re.M | re.I)
    if not m:
        return body
    dev = m.group(1).strip()
    if not dev.startswith("DSUClient/"):
        return body
    if combined:
        new = sdl(combined[0])
    elif right:
        new = sdl(right[0])
    else:
        new = SAFE_GCPAD
    log("sanitize Device %s → %s" % (dev, new))
    return re.sub(r"^Device\s*=\s*.*$", "Device = " + new, body, count=1, flags=re.M | re.I)


if __name__ == "__main__":
    main()
