# Changelog

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
