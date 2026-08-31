# Plan A — Store + Emulator Paths (Wii / GC / PS1 / PS2)

> Status: critically reviewed; implementation completed through A4.
> Scope: shared emulator-path contract, Paths settings UI, Windows Store routing/install, archive safety.
> Out of scope: Mii Manager, emulator language, generic disc-save import, Deck Store install (A5), release/version bump.

## 0. Review verdict and binding corrections

These corrections override older or conflicting wording:

1. There is exactly one persisted path document: `library-paths.json`. `LibraryPaths` owns overrides; `EmulatorPaths` is a stateless resolver and must never load or save a second mutable singleton.
2. Remote Deck paths are not local paths. Defaults derive deterministically from `LibraryPaths.EmulationRoot`; local `Directory.Exists` must not select a remote path. Optional connected probing may only offer a suggestion.
3. Switch `TitleId` remains separate from the validated, system-specific `PlatformId`/`GameId` used by Dolphin and PlayStation. Archive folder names are never trusted as IDs.
4. Routing precedence is: explicitly selected game/system, known catalog mapping, trusted hit metadata, then heuristics. An explicit non-All selection cannot be redirected by conflicting search metadata.
5. `.iso`, `.bin`, `.img`, `.chd`, `.rvz` and `.ciso` are ambiguous and are not auto-routed. Manual upload keeps its fallback unless a disc system was selected explicitly. Unambiguous catalog routes must still be relocated through `LibraryPaths`.
6. Support is capability/layout based:
   - Dolphin textures require a validated Game ID and recognizable image payload.
   - Dolphin Graphic Mods require a recognizable `metadata.json` plus supported layout directory.
   - DuckStation textures use `textures/<SERIAL>/replacements`.
   - PCSX2 textures use current `textures/<SERIAL>/replacements`; no obsolete `<SERIAL>_<CRC>` requirement.
   - DuckStation cheats require validated `.cht`; PCSX2 cheats require validated eight-hex `.pnach` files.
   - Wii/GC/PS1/PS2 saves have no generic importer in v1 and must be rejected before writing.
   - Unknown capabilities/layouts are staged only under `_incoming/<safePackName>` and never reported active.
7. Archives are hostile input. Extraction rejects traversal, absolute escape, symbolic/hard links and reparse points, and applies entry and expanded-size limits. No prepared payload may escape its extraction root.
8. Do not release, bump a version, or update the changelog in this implementation task.

## Path contract

`LibraryPaths` persists ROM/library roots, Switch selections, and optional per-emulator `UserRoot`, `TexturesRoot`, `ModsRoot`, and display-only `SavesRoot` overrides. `EmulatorPaths` derives:

| Emulator | User root | Textures | Mods/cheats |
|---|---|---|---|
| Dolphin | `{Emulation}/storage/dolphin-emu` | `Load/Textures` | `Load/GraphicMods` |
| DuckStation | `{Emulation}/storage/duckstation` | `textures` | `cheats` |
| PCSX2 | `{Emulation}/storage/pcsx2` | `textures` | `cheats` |
| Switch emulators | existing `LibraryPaths.Switch*` contract | existing load/contents roots | unchanged |

Both Windows and Deck Settings expose one Paths tab. Emulator folders are not broadly created merely by saving settings; existing library-folder behavior remains unchanged.

## Store routing result

Install planning returns a typed state: `Active`, `Staged`, `Unsupported`, or `RequiresLayoutValidation`. Only a validated payload can become active. Staged records remain deletable and persist as staged rather than installed.

Game-ID trust order is selected library metadata, selected/catalog identity, then explicitly validated hit metadata. Trusted ROM filename metadata must have an explicit ID shape; arbitrary extracted names never participate.

## Phases

- A0: one path API/document and Windows/Deck Paths UI.
- A1: Wii catalog/search wiring, selected-system precedence, ambiguous-route removal.
- A2: Dolphin texture/Graphic Mod routing, unsupported save guard.
- A3: DuckStation/PCSX2 texture and validated-cheat layouts.
- A4: `_incoming` truthfulness, Store safety hint, archive traversal/symlink hardening, regression tests.
- A5: Deck Store install remains optional/out of scope.

## Verification gates

- Full solution build succeeds without warnings.
- Automated tests cover derived and persisted overrides, typed ID validation, explicit-system precedence, unknown-ID staging, unsupported saves, Dolphin payloads, current DuckStation/PCSX2 replacement layouts, rejection of raw/obsolete layouts, ambiguous extension routes, Switch path regression, traversal, and symlink archives.
- No new hardcoded `/home/deck/Emulation` appears in `MainWindow` install routing, `EmulatorPaths`, or `DiscPackRouting`.
- Live Deck/emulator smoke tests remain a manual release gate: close the emulator, verify configured remote roots, then test one active pack per supported capability.
