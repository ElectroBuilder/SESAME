#!/usr/bin/env python3
"""SESAME: apply the user's proven Joy-Con → Wiimote profile for Dolphin.

Canonical profile name: "SESAME - Joy2Wii"
  - Based on the working Desktop Mode layout (Combined SDL Joy-Con L/R,
    Accel R / Gyro R for Wiimote, Accel L for Nunchuk, gyro dead zone).
  - Companion: "SESAME - Joy2Wii (no nunchuk)" with Extension = None for
    games that refuse to continue until the Nunchuk is removed.

This script does NOT invent IMU axis maps. It only:
  1) Ensures Joy2Wii profiles exist (migrate mike.ini if present)
  2) Writes WiimoteNew.ini from the chosen profile
  3) Removes obsolete SESAME-joycon* auto profiles
  4) Optionally sets Extension=None via SESAME_WII_NUNCHUK=0 / --no-nunchuk

Hotkey: Dolphin "Next/Previous Wiimote Profile" cycles Joy2Wii ↔ no-nunchuk
while a game is running (configure under Options → Hotkey Settings).

Game Mode: Steam Input must be Off on the Wii shortcut so SDL still sees
Nintendo Switch Joy-Con (L/R). SESAME Optimize ForceOff handles that.
"""
from __future__ import annotations

import os
import pathlib
import re
import sys

PROFILE_NUNCHUK = "SESAME - Joy2Wii"
PROFILE_BARE = "SESAME - Joy2Wii (no nunchuk)"
OLD_AUTO = (
    "SESAME-joycon.ini",
    "SESAME-joycon-nunchuk.ini",
    "SESAME-joycon-remote.ini",
    "SESAME-gyro.ini",
)


def log(msg: str) -> None:
    try:
        home = pathlib.Path(os.path.expanduser("~"))
        path = home / ".local/share/sesame/dolphin-joycon.log"
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as f:
            f.write(msg.rstrip() + "\n")
    except Exception:
        pass


def dirs() -> list[pathlib.Path]:
    home = pathlib.Path(os.path.expanduser("~"))
    return [
        home / ".var/app/org.DolphinEmu.dolphin-emu/config/dolphin-emu",
        home / ".config/dolphin-emu",
    ]


def want_nunchuk() -> bool:
    if "--no-nunchuk" in sys.argv or "--bare" in sys.argv:
        return False
    if "--nunchuk" in sys.argv:
        return True
    env = os.environ.get("SESAME_WII_NUNCHUK", "1").strip().lower()
    return env not in ("0", "false", "no", "off", "none")


def read_text(path: pathlib.Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="ignore")
    except Exception:
        return ""


def write_text(path: pathlib.Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def find_profile_file(name: str) -> pathlib.Path | None:
    """Prefer flatpak config (Game Mode Dolphin), then native."""
    for base in dirs():
        p = base / "Profiles" / "Wiimote" / (name + ".ini")
        if p.is_file():
            return p
    return None


def find_mike() -> pathlib.Path | None:
    for base in dirs():
        for cand in ("mike.ini", "Mike.ini", "MIKE.ini"):
            p = base / "Profiles" / "Wiimote" / cand
            if p.is_file():
                return p
    return None


def strip_nunchuk(profile_text: str) -> str:
    lines = []
    for line in profile_text.splitlines():
        if line.startswith("Extension ="):
            lines.append("Extension = None")
            continue
        if line.startswith("Nunchuk/"):
            continue
        lines.append(line)
    return "\n".join(lines).rstrip() + "\n"


def ensure_source(wiimote_body: str) -> str:
    if re.search(r"(?m)^Source\s*=", wiimote_body):
        return wiimote_body
    return wiimote_body.replace("[Wiimote1]\n", "[Wiimote1]\nSource = 1\n", 1)


def profile_to_wiimote(profile_text: str) -> str:
    body = re.sub(r"^\[Profile\]\s*", "[Wiimote1]\n", profile_text, count=1, flags=re.I)
    body = ensure_source(body)
    if not body.endswith("\n"):
        body += "\n"
    body += (
        "[Wiimote2]\nSource = 0\n"
        "[Wiimote3]\nSource = 0\n"
        "[Wiimote4]\nSource = 0\n"
        "[BalanceBoard]\nSource = 0\n"
    )
    return body


def patch_dolphin_ini(path: pathlib.Path) -> None:
    cur = read_text(path) if path.exists() else "[Core]\n"
    if not cur.strip():
        cur = "[Core]\n"

    def set_key(text: str, section: str, key: str, value: str) -> str:
        header = "[" + section + "]"
        line = key + " = " + value
        low = text.lower()
        i = low.find(header.lower())
        if i < 0:
            return text.rstrip() + "\n" + header + "\n" + line + "\n"
        start = i + len(header)
        nxt = re.search(r"\n\[", text[start:])
        end = start + nxt.start() if nxt else len(text)
        body = text[start:end]
        rx = re.compile(r"^" + re.escape(key) + r"\s*=.*$", re.I | re.M)
        if rx.search(body):
            body = rx.sub(line, body, count=1)
        else:
            body = "\n" + line + body
        return text[:start] + body + text[end:]

    cur = set_key(cur, "Core", "WiimoteSource0", "1")
    for i in range(1, 4):
        cur = set_key(cur, "Core", "WiimoteSource%d" % i, "0")
    cur = set_key(cur, "Input", "BackgroundInput", "True")
    write_text(path, cur)


def migrate_and_seed() -> pathlib.Path | None:
    """Ensure Joy2Wii profiles exist; seed from mike.ini when needed."""
    nunchuk = find_profile_file(PROFILE_NUNCHUK)
    bare = find_profile_file(PROFILE_BARE)
    mike = find_mike()

    if nunchuk is None and mike is not None:
        text = read_text(mike)
        # Write into every config tree so Game Mode + Desktop share it.
        for base in dirs():
            if not base.exists() and base != dirs()[0]:
                continue
            prof = base / "Profiles" / "Wiimote"
            write_text(prof / (PROFILE_NUNCHUK + ".ini"), text)
            write_text(prof / (PROFILE_BARE + ".ini"), strip_nunchuk(text))
            # Keep a backup of the original name once.
            bak = prof / "mike.ini.bak"
            if not bak.exists():
                write_text(bak, text)
            mike_path = prof / "mike.ini"
            if mike_path.exists():
                try:
                    mike_path.unlink()
                except Exception:
                    pass
        log("migrated mike.ini → %s + bare" % PROFILE_NUNCHUK)
        nunchuk = find_profile_file(PROFILE_NUNCHUK)
        bare = find_profile_file(PROFILE_BARE)

    if nunchuk is not None and bare is None:
        text = read_text(nunchuk)
        for base in dirs():
            if not (base / "Profiles" / "Wiimote").exists() and base != nunchuk.parents[2]:
                continue
            write_text(base / "Profiles" / "Wiimote" / (PROFILE_BARE + ".ini"), strip_nunchuk(text))
        bare = find_profile_file(PROFILE_BARE)
        log("created bare profile from Joy2Wii")

    # Mirror profiles across both config roots when only one side has them.
    for name in (PROFILE_NUNCHUK, PROFILE_BARE):
        src = find_profile_file(name)
        if src is None:
            continue
        text = read_text(src)
        for base in dirs():
            dest = base / "Profiles" / "Wiimote" / (name + ".ini")
            if dest == src:
                continue
            if not dest.exists() or read_text(dest) != text:
                if base.exists() or base == dirs()[0]:
                    write_text(dest, text)

    return find_profile_file(PROFILE_NUNCHUK if want_nunchuk() else PROFILE_BARE)


def remove_old_auto_profiles() -> None:
    for base in dirs():
        prof = base / "Profiles" / "Wiimote"
        if not prof.is_dir():
            continue
        for name in OLD_AUTO:
            path = prof / name
            if path.exists():
                try:
                    path.unlink()
                    log("removed obsolete %s" % path)
                except Exception as ex:
                    log("could not remove %s: %s" % (path, ex))


def apply_profile(profile_path: pathlib.Path) -> None:
    text = read_text(profile_path)
    if not text.strip():
        log("empty profile %s" % profile_path)
        return
    # If user asked for bare but we loaded nunchuk file, strip.
    if not want_nunchuk() and "Extension = Nunchuk" in text:
        text = strip_nunchuk(text)
    wiimote = profile_to_wiimote(text)
    for base in dirs():
        if not base.exists() and base != dirs()[0]:
            continue
        write_text(base / "WiimoteNew.ini", wiimote)
        patch_dolphin_ini(base / "Dolphin.ini")
    log(
        "applied %s → WiimoteNew.ini (nunchuk=%s)"
        % (profile_path.name, "yes" if want_nunchuk() else "no")
    )


def main() -> int:
    remove_old_auto_profiles()
    chosen = migrate_and_seed()
    if chosen is None:
        # Fall back to bare/nunchuk sibling if only one exists.
        chosen = find_profile_file(PROFILE_NUNCHUK) or find_profile_file(PROFILE_BARE)
    if chosen is None:
        log(
            "no Joy2Wii profile found — save your working layout in Dolphin as "
            "'%s' (or leave mike.ini for auto-migrate)" % PROFILE_NUNCHUK
        )
        return 0
    apply_profile(chosen)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
