# Plan B — Mii manager (reviewed and implemented scope)

## 0. Review verdict and binding corrections

The manager must fail closed around unknown formats and live NAND writes. Pure C# parsers are required; MiiJS/Node and real user dumps are not fixtures. `profiles.dat` is not a Mii database. WM4K is not a Mii engine.

Both targets remain **Push-disabled** after synthetic validation. A parser, CRC test and transactional fault-injection suite establish read/backup/restore behavior, but do not establish emulator write compatibility. `WriteVerified` requires a separate, explicit manual validation and acknowledgement per target.

All writes use the stateless `EmulatorPaths` resolver and persisted `LibraryPaths.EmulatorOverrides`. The UI captures host/profile ID, target and path before async work and blocks target/disconnect changes through completion. A replacement is staged to a new sibling, reread and verified. The live path is changed only by same-filesystem rename after a final emulator check and compare-and-swap of existence/source SHA. Rename-timeout/disconnect results are reconciled by live reread/hash; possible commits are reported indeterminate, never refused.

Backups are local and remote, reread/hash verified, and bound in their manifest/inventory to the stable connection-profile ID, target kind and path. A valid bound backup may restore a missing target; in that case the result and audit explicitly state that no live pre-backup existed. The audit records host, path, backup/pre/post SHA-256 and backup locations. No code calls `DeckClient.WriteBytes` on a live NAND path.

## M0 — format spike

- Encode CRC-16/XMODEM and strict fixed UTF-16 handling.
- Wii: exact `RNOD` size/layout/checksum, 100 records, documented bit ranges and opaque-byte preservation.
- Eden: exact `NFDB` database size/layout, StoreData/CoreData/UUID/CRC validation; reject 88-byte CharInfo/NFIF.
- Prove byte-local mutations and exact record export with synthetic fixtures.

## M1a — read-only manager

- Resolve Dolphin and Eden database paths through `EmulatorPaths.UserRoot`.
- Show target, resolved path, integrity, honest capability, slots, names and IDs.
- Export exact records without conversion.
- Show host-bound backup inventory.

## M1b — backup and verified restore

- Create protected local backup+manifest and a unique remote sibling backup for any existing live bytes.
- Restore only after manifest binding, SHA-256 and format validation.
- Permit missing-target restore only when the parent exists; do not invent a pre-backup.
- Freeze the UI operation snapshot and block disconnect during work.

## M2 — feature-gated editor and Push

- Allow offline draft editing: rename existing records, import exact records, create a named basic record and export the exact draft/database.
- The normal editor exposes only fields with validated mappings: gender, hair style/colour, eye colour and favourite colour. More facial parts require separate mapping/rendering validation and remain out of this phase.
- Keep the permanent per-target manual write gate false, but expose an explicit per-session acknowledgement for experimental Push. The acknowledgement is bound to the exact host, target kind and resolved path and is never inferred from parser tests.
- Retain backup-first transactional replacement and synthetic fault-injection tests; a manual emulator validation campaign is still required before changing the permanent gate.
- Never infer Wii Sports links or rewrite unrelated save/profile files.

See `docs/mii-format-implementation.md` for the implemented capability matrix and transaction invariants.
