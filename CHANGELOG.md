# Changelog

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
