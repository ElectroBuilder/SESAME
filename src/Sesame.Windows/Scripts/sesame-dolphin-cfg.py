#!/usr/bin/env python3
"""SESAME: apply the user's proven Joy-Con → Wiimote profile for Dolphin.

Canonical profile name: "SESAME - Joy2Wii"
  - Based on the working Desktop Mode layout (Combined SDL Joy-Con L/R,
    Accel R / Gyro R for Wiimote, Accel L for Nunchuk, gyro dead zone).
  - Companion: "SESAME - Joy2Wii (no nunchuk)" — Combined + Extension=None
  - Solo: "SESAME - Joy2Wii (solo)" — Device Joy-Con (R), Extension=None
    for the physical Left-disconnect + SL+SR workflow (see joycon-watch).

This script does NOT invent IMU axis maps. It only:
  1) Ensures Joy2Wii profiles exist (migrate mike.ini if present)
  2) Writes WiimoteNew.ini for Wiimote1–4 (SDL/0..3 Combined pairs)
  3) Clears per-game WiimoteProfile* overrides in GameSettings
  4) Removes obsolete SESAME-joycon* auto profiles
  5) Optionally sets Extension=None via SESAME_WII_NUNCHUK=0 / --no-nunchuk

Live pair↔solo: sesame-joycon-watch.py (started by sesame-dolphin.sh for Wii).

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
PROFILE_SOLO = "SESAME - Joy2Wii (solo)"
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


def remap_sdl_index(text: str, index: int) -> str:
    """Profiles are authored for SDL/0/; remap to SDL/{index}/ for Wiimote slots."""
    if index == 0:
        return text
    return text.replace("SDL/0/", "SDL/%d/" % index)


def wiimote_section(profile_text: str, slot: int) -> str:
    """Build [WiimoteN] from a [Profile] body for SDL slot 0..3."""
    body = re.sub(r"^\[Profile\]\s*", "", profile_text.strip() + "\n", count=1, flags=re.I)
    body = remap_sdl_index(body, slot)
    body = re.sub(r"(?m)^Source\s*=.*\n?", "", body)
    if not body.endswith("\n"):
        body += "\n"
    return "[Wiimote%d]\nSource = 1\n%s" % (slot + 1, body)


def profile_to_wiimote(profile_text: str, other_slots_text: str | None = None) -> str:
    """Wiimote1–4 emulated: each Combined Joy-Con pair = one player (SDL/0..3)."""
    other = other_slots_text if other_slots_text is not None else profile_text
    parts = [wiimote_section(profile_text if i == 0 else other, i) for i in range(4)]
    parts.append("[BalanceBoard]\nSource = 0\n")
    return "".join(parts)


def clear_game_wiimote_profile_overrides() -> int:
    """Remove per-game WiimoteProfile* so WiimoteNew.ini (Joy2Wii) actually applies.

    EmuDeck / older Dolphin GameSettings often pin another profile per title; that
    silently overrides our launch-time WiimoteNew.ini for those games only.
    """
    rx = re.compile(r"(?im)^WiimoteProfile\d*\s*=.*\n?")
    changed = 0
    for base in dirs():
        gs = base / "GameSettings"
        if not gs.is_dir():
            continue
        for path in gs.glob("*.ini"):
            cur = read_text(path)
            if not cur or "WiimoteProfile" not in cur:
                continue
            new = rx.sub("", cur)
            if new != cur:
                write_text(path, new)
                changed += 1
    if changed:
        log("cleared WiimoteProfile overrides in %d GameSettings file(s)" % changed)
    return changed


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

    # All four emulated Wiimotes on — 2nd Combined pair = player 2, etc.
    for i in range(4):
        cur = set_key(cur, "Core", "WiimoteSource%d" % i, "1")
    cur = set_key(cur, "Input", "BackgroundInput", "True")
    write_text(path, cur)


def script_dir() -> pathlib.Path:
    return pathlib.Path(__file__).resolve().parent


def load_bundled_solo() -> str:
    """Prefer sesame-joy2wii-solo.ini next to this script (deployed by Optimize)."""
    path = script_dir() / "sesame-joy2wii-solo.ini"
    text = read_text(path)
    if text.strip():
        return text
    return ""


def restore_stashed_profiles() -> None:
    """Undo a crashed joycon-watch stash (*.ini.sesame_stash)."""
    for base in dirs():
        prof = base / "Profiles" / "Wiimote"
        if not prof.is_dir():
            continue
        for stash in prof.glob("*.ini.sesame_stash"):
            orig = pathlib.Path(str(stash)[: -len(".sesame_stash")])
            try:
                if not orig.exists():
                    stash.rename(orig)
                    log("restored stashed profile %s" % orig.name)
                else:
                    stash.unlink()
            except Exception as ex:
                log("stash restore failed %s: %s" % (stash, ex))


def ensure_solo_profile() -> None:
    solo = find_profile_file(PROFILE_SOLO)
    if solo is not None and read_text(solo).strip():
        # Mirror to the other config root if needed.
        text = read_text(solo)
        for base in dirs():
            dest = base / "Profiles" / "Wiimote" / (PROFILE_SOLO + ".ini")
            if dest == solo:
                continue
            if base.exists() or base == dirs()[0]:
                if not dest.exists() or read_text(dest) != text:
                    write_text(dest, text)
        return
    text = load_bundled_solo()
    if not text.strip():
        log("no bundled solo profile — place sesame-joy2wii-solo.ini next to cfg")
        return
    for base in dirs():
        if not base.exists() and base != dirs()[0]:
            continue
        write_text(base / "Profiles" / "Wiimote" / (PROFILE_SOLO + ".ini"), text)
    log("seeded %s" % PROFILE_SOLO)


def migrate_and_seed() -> pathlib.Path | None:
    """Ensure Joy2Wii profiles exist; seed from mike.ini when needed."""
    restore_stashed_profiles()
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

    ensure_solo_profile()

    # Mirror profiles across both config roots when only one side has them.
    for name in (PROFILE_NUNCHUK, PROFILE_BARE, PROFILE_SOLO):
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
    clear_game_wiimote_profile_overrides()
    wiimote = profile_to_wiimote(text)
    for base in dirs():
        if not base.exists() and base != dirs()[0]:
            continue
        write_text(base / "WiimoteNew.ini", wiimote)
        patch_dolphin_ini(base / "Dolphin.ini")
    log(
        "applied %s → Wiimote1–4 (nunchuk=%s)"
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
