# Changelog

## 0.6.23 — 2026-08-30

- Optimize: lock titles so they stay out of Optimize and cannot be edited until unlocked; header checkbox selects/deselects all visible unlocked rows; remove per-column filter boxes (sorting stays).
- Artwork: official Steam store covers (library capsule, header, hero, logo) in the picker and as fallback when SteamGridDB has no match.
- Dashboard: richer tiles with badges and extra Optimize stats (locked / missing art); sixth tile with version, Check update / Install, and Settings.

## 0.6.22 — 2026-08-30

- Covers with category masks: fill the art under the bar again (no black side bars). Hydra/apps still use contain so titles are not cropped. Strip stacked **NL Vertaling** / ROM-hack footers before redrawing.
- Apps: Stop merging Stremio/Kodi/Firefox/Lutris into one Steam shortcut. They share `/usr/bin/flatpak`, so Optimize matched them as the same ROM — only one tile survived (often with a wrong cover such as Killer Instinct). Each catalog app now keeps its own AppId, artwork, and Apps-collection membership.
- Dolphin GameCube: default pad is `SDL/0/Steam Deck Controller` instead of the usually-disconnected `Steam Virtual Gamepad`, so GC controls work in Game Mode. External controllers are left alone; Wii Joy2Wii paths unchanged.

## 0.6.21 — 2026-08-30

- Covers: show the full artwork (contain) in preview and Steam write — no more zoom/crop cutting titles off Hydra covers.
- Apps: stop Killer Instinct (and other games) covers landing on Stremio/Kodi/Lutris/Firefox — search and saved picks are pinned to the catalog app title; contaminated picks are scrubbed.

## 0.6.20 — 2026-08-30

- Steam collections: put ROMs into platform tabs again. Membership IDs are written as **unsigned** appids (Steam’s format); the earlier signed values left collections at 0 games.

## 0.6.19 — 2026-08-30

- Hydra Optimize: replace existing shortcuts (by game folder / name), keep the Steam AppId so artwork and Proton stay linked — no more duplicate tiles when the exe path is fixed.
- Always force the newest installed UMU Proton (e.g. UMU-Proton-10.0.4) into CompatToolMapping; report if it fails.
- Covers: fill the capsule again (no black letterbox bars); portrait uses the vertical grid only; write a hero so Steam does not blow up the cover.

## 0.6.18 — 2026-08-30

- Hydra shortcuts: Target no longer splits on spaces (`"/home/deck/Hydra/Black" Jacket/...` → `"/home/deck/Hydra/Black Jacket/BlackJacket.exe"`); Start in = game folder.
- Optimize skips SteamGridDB download/write when covers already exist and **Overwrite artwork** is off (now the default).
- Covers fit entirely in the capsule (contain, not crop/zoom); missing hero uses the landscape image so Steam does not blow up the cover.

## 0.6.17 — 2026-08-30

- Fix Scan freeze after Hydra path fix: RomScan no longer recursively walks `/home/deck/Hydra` (full Windows game trees). That walk made “Reading ROM folders and mods…” hang and blocked the UI.
- Hydra/Lutris/Other stay on the shallow Optimize folder scan only — not the ROM library walk.

## 0.6.16 — 2026-08-30

- Hydra Optimize: stop reading Hydra LevelDB / orphaned Steam shortcuts (those produced ~200 ghost titles). Scan only real game folders with an existing `.exe`.
- Default Hydra library path and Files quick access: `/home/deck/Hydra` (migrate away from empty `/home/deck/Games/Hydra`).
- Optimize still assigns UMU Proton (or GE / Proton Experimental) for Hydra shortcuts.

## 0.6.15 — 2026-08-30

- Wii Joy-Con pair↔solo without Controllers menu: disconnect Left + SL+SR (or ZR+R) on Right → **SESAME - Joy2Wii (solo)** (Wiimote only, `Extension = None`); recombine with L+R → Joy2Wii + Nunchuk again.
- `sesame-joycon-watch.py` watches Combined vs Right-only, writes the matching profile, and triggers Dolphin’s Next Wiimote Profile (F8) so Extension/Device reload mid-game.
- Re-pair tip: joycond wants **one shoulder on each** (L+R), not ZL+ZR. Steam Input Off still required in Game Mode.
- Optimize progress: clear Prepare Steam steps (session check → Desktop switch → close Steam) instead of a long silent wait before “Waiting until Desktop Mode is ready”; faster session detect (bash first).
- Multiplayer: Wiimote 2–4 enabled with the same Joy2Wii layout on `SDL/1..3` — a second Combined Joy-Con pair is player 2 with no extra setup.
- Per-game profile fixes: clear `WiimoteProfile*` overrides in Dolphin GameSettings so Joy2Wii from the SESAME launcher actually applies on every Wii title.

## 0.6.14 — 2026-08-29

- Wii controller default is now **SESAME - Joy2Wii** (your proven Combined SDL Joy-Con L/R layout with Accel R/L + gyro dead zone). Obsolete SESAME-joycon* auto profiles are removed.
- Companion profile **SESAME - Joy2Wii (no nunchuk)** for games that refuse to continue until the Nunchuk is removed — cycle with Dolphin’s Next/Previous Wiimote Profile hotkey, or set `SESAME_WII_NUNCHUK=0` at launch.
- Launch wrapper no longer invents IMU maps or starts cemuhook by default (faster Game Mode). Steam Input Off on Wii shortcuts remains required so SDL still sees Joy-Con (L/R).

## 0.6.13 — 2026-08-29

- Wii Joy-Con motion: invert pitch/vertical accel for Joy-Con held as Wiimote (fixes swing up→down and upside-down aiming).
- Stop OR-ing Steam Deck gyro into Joy-Con IMU bindings (that caused panicky noise at rest).
- IMU pointer smoothing: Accelerometer Influence **1%**, Calibration Period **3.5s**, Total Yaw **25**; IMUIR on by default.
- Recognize SDL Combined name `Joy-Con (L/R)` on Steam Deck.

## 0.6.12 — 2026-08-29

- Connect shows a progress overlay (session N/N, waking, opening folders). Auto-connect skips the long Wake-on-LAN retry loop so dead sessions fail fast.
- Settings → Emulators: joycond status runs off the UI thread (“Checking joycond…”) so opening Settings no longer freezes.
- Flatpak app covers no longer inherit Stremio: match launch exe/options before DisplayName, scrub contaminated optimizer picks, and refuse artwork from a different catalog app.

## 0.6.11 — 2026-08-29

- Wii Joy-Cons: **Combined-first** (press L+R) for single-player Wiimote+Nunchuk — one Device, one Steam player; cemuhook starts with `-r` (Right IMU). Separate L/R still works as fallback (cross-bind).
- Fix disconnected DSU: `joycon-dsu.status=ok` only when cemuhook is alive **and** at least one Combined/L/R pad exists; never hardcode a DSU Device without that. Buttons fall back to SDL Combined when DSU is missing.
- Softer cemuhook restarts (keep healthy listener on 26761; skip `modprobe` if already loaded) to reduce Bluetooth flakiness after Optimize.

## 0.6.10 — 2026-08-29

- Wii Joy-Cons: when cemuhook DSU is up, Wiimote1 is **DSU-only** — Right = Wiimote, Left = Nunchuk on that same remote. No SDL Combined/pair OR-ed into bindings; Combined/joycon-pair is ignored as Device (may still appear in Dolphin’s dropdown — ignore it).
- `WiimoteNew.ini` is fully overwritten each launch; Wiimote 2–4 stay off; GCPad is forced off Joy-Cons (Steam Virtual Gamepad).
- DSU pad names (`Nintendo Switch Right/Left Joy-Con`), DualShock button aliases, UDP **26761** (Deck gyro stays on 26760); Optimize forces **Steam Input Off** on Dolphin shortcuts.

## 0.6.9 — 2026-08-29

- Settings → Emulators: **Install Joy-Con motion…** — shows risks, lets you view `install-joycond.sh`, then runs the full SteamOS install (headers, joycond, cemuhook) with your Deck sudo password over SSH.
- Joy-Con install script hardened for SteamOS: restore glibc/linux/libevdev/libudev headers from Arch packages, build joycond, enable service, install cemuhook with pacman Python deps (`--no-deps` pip) so cairo/PyGObject does not rebuild from source.
- Optimize no longer renames Firefox/Kodi/Lutris to Stremio: Flatpak apps share `/usr/bin/flatpak`, so picks are keyed per app (not by that shared path).
- Hydra / Lutris / other Windows shortcuts get UMU Proton (or GE-Proton / Proton Experimental) again — CompatToolMapping now uses signed Steam shortcut IDs.

## 0.6.8 — 2026-08-29

- SSH connect feels snappy again: home folder lists before MAC/layout work; SSH+SFTP connect in parallel; auto-try uses a 5s timeout per dead session; known MAC skips the slow learn scripts.
- Library folders are created with one `mkdir -p` instead of many SFTP calls that blocked the file list.
- Joy-Con install: trust pacman keys noninteractively (fixes the AF1D2199EF0A3CCF abort), build with pip Ninja when `make` is missing, clearer errors when gcc/libevdev are still blocked.

## 0.6.7 — 2026-08-29

- Windows SSH connect no longer freezes the UI: MAC/WoL learning and shell resize run off the UI thread; Optimize cache loads in the background.
- Auto-connect on startup: tries known sessions in order (selected first, then the rest) until one connects.
- Joy-Con install script: noninteractive pacman (no stuck PGP prompt), cmake via `pip --user` when pacman fails, out-of-tree cmake build. Re-Optimize once so `install-joycond.sh` is written, then run it in Desktop Mode.

## 0.6.6 — 2026-08-29

- Joy-Con DSU: SESAME no longer tells you to `systemctl enable joycond` when the unit is missing (SteamOS does not ship it). Optimize deploys `~/.local/share/sesame/install-joycond.sh` for a real one-time Desktop Mode install (build + `sudo make install` + enable + cemuhook). Status stays `need-install` until `systemctl status joycond` would succeed.
- Optimize Wii hint is short (bullets + one install command) on Windows and Deck. Wiimote + Nunchuk button map prefers Joy-Con DSU pads when cemuhook is up.

## 0.6.5 — 2026-08-29

- Category masks no longer stack: SESAME strips existing platform bars before applying a fresh one (fixes 2–3× Nintendo Switch / GameCube labels after re-Optimize).
- Optimize checkbox selection and artwork picks persist for apps and games across scan/reconnect on Windows and Deck (PickKey + Selected in optimizer-picks).
- Steam Deck Joy-Con DSU: no pip3/pacman joycond required in the hint path — SESAME uses ensurepip, `python3 -m pip --user`, and builds joycond into `~/.local` when possible. Log: `~/.local/share/sesame/joycon-dsu.log`.

## 0.6.4 — 2026-08-29

- Wii Joy-Cons: always one Wiimote + Nunchuk (Right = remote/gyro, Left = Nunchuk); Wiimote 2–4 stay off.
- Joy-Con motion uses a real DSU server (joycond-cemuhook on UDP 26761), separate from SteamDeckGyroDSU on 26760. SESAME starts it when launching Wii games and points Dolphin Alternate Input Sources at both.
- Aiming uses IMUIR (gyro pointer) with Home to recenter; mouse Cursor IR no longer fights the gyro.
- Optimize hint explains Steam Input Off, SL+SR separate pairing, and one-time joycond install when needed.

## 0.6.3 — 2026-08-29

- Wii / Dolphin: Joy-Cons on the Deck map like BetterJoy — Right → Wiimote (+ gyro), Left → Nunchuk; extra Joy-Cons become remotes 2–4 for Wii Sports. Re-Optimize Wii games after pairing; turn Steam Input Off for the Joy-Cons.

## 0.6.2 — 2026-08-29

- Dashboard tab on Windows and Steam Deck: scan apps/games/library and jump into Optimize from one place.
- Connecting no longer freezes the UI: library scans are opt-in, folder lists run asynchronously, and Deck Optimize hydrates Steam covers off the UI thread.
- Artwork tab renamed to Optimize; Scan uses the same center progress overlay as Apps.
- Deck Game Mode and desktop tabs match Windows (Dashboard first, Optimize tile, shared work overlay).

## 0.6.1 — 2026-08-29

- Hydra, Lutris and other Windows games get UMU Proton (or GE-Proton / Proton Experimental) automatically when the Steam shortcut is written.
- Steam collections are built from every SESAME shortcut, with signed app IDs, so Game Mode tabs appear per platform (PSX, PS2, GameCube, Sega Genesis, …) rather than only NES/SNES/N64.
- Install and updates keep a single SESAME Game Mode shortcut, pointed at the current binary, with artwork from the SESAME assets. `steamos-add-to-steam` no longer stacks extra tiles.

## 0.6.0 — 2026-08-29

- Steam Deck desktop Settings match Windows: General, Covers, Data, Library, Emulators and Updates.
- Light and dark themes share one colour dictionary so Fluent chrome, text and backgrounds stay readable.
- Deck desktop chrome follows Windows (header, tabs, Files and Games toolbars). Settings opens as a window.
- Games list no longer treats Switch save Title ID folders as games.

## 0.5.0 — 2026-08-29

- In-app update for Windows and Steam Deck: check GitHub, install, restart.
- Versioned GitHub releases with Windows zip, Linux tarball and these notes.
- Steam Deck light and dark themes use the same colour tokens as Windows.
- Settings for ROM, Hydra, Lutris and other game folders, plus Switch emulator mods/saves.
- Empty library folders only — no shipped list of games or session names.
- Tagline is the full product name. Deck chrome no longer shows This Deck / Disconnect.
