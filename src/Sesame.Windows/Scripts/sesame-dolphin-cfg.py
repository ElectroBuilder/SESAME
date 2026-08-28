#!/usr/bin/env python3
"""SESAME: patch Dolphin Wiimote IMU without wiping the rest of the config."""
import os
import pathlib
import re

IMU = {
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


def pick(pool):
    n = find(("joy-con", "nintendo switch combined", "nintendo switch pro"), pool)
    if n:
        return "SDL/0/%s" % n, "sdl"
    n = find(("8bitdo",), pool)
    if n and not is_xboxish(n):
        return "SDL/0/%s" % n, "sdl"
    n = find(("dualsense", "dualshock", "wireless controller"), pool)
    if n and "xbox" not in n.lower() and "8bitdo" not in n.lower():
        return "SDL/0/%s" % n, "sdl"
    n = find(("steam virtual gamepad",), pool)
    if n:
        return "SDL/0/%s" % n, "sdl"
    n = find(("microsoft x-box 360", "x-box 360 pad"), pool)
    if n:
        return "evdev/0/%s" % n, "evdev"
    n = find(("xbox wireless", "xbox one", "xbox controller"), pool)
    if n:
        return "SDL/0/%s" % n, "sdl"
    n = find(("steam deck",), pool)
    if n and "virtual" not in n.lower():
        return "SteamDeck/0/Steam Deck", "sdl"
    if os.environ.get("SteamAppId") or os.environ.get("SteamGameId") or os.environ.get("SteamDeck"):
        return "SDL/0/Steam Virtual Gamepad", "sdl"
    return "SDL/0/Steam Deck Controller", "sdl"


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


def patch_dolphin_ini(path):
    cur = path.read_text(errors="ignore") if path.exists() else "[Core]\n"
    cur = set_key(cur, "Core", "SIDevice0", "6")
    cur = set_key(cur, "Core", "WiimoteSource0", "1")
    cur = set_key(cur, "Input", "BackgroundInput", "True")
    path.write_text(cur)


def patch_wiimote(path, device):
    cur = path.read_text(errors="ignore") if path.exists() else ""
    if not cur.strip():
        cur = "[Wiimote1]\nSource = 1\n"
    cur = set_key(cur, "Wiimote1", "Source", "1")
    cur = set_key(cur, "Wiimote1", "Device", device)
    for k, v in IMU.items():
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
    path.write_text(cur)


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
    dev, _style = pick(names())
    for d in dirs():
        p = pathlib.Path(d)
        if not p.exists():
            continue
        try:
            patch_dolphin_ini(p / "Dolphin.ini")
            patch_wiimote(p / "WiimoteNew.ini", dev)
            patch_gcpad(p / "GCPadNew.ini", dev)
        except Exception:
            pass


if __name__ == "__main__":
    main()
