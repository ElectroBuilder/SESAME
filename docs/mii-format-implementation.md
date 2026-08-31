# Mii manager implementation and safety status

This implementation is deliberately split between format verification and NAND transactions. It uses pure C# and synthetic fixtures only; no user NAND dump or third-party parser is included.

## Capability truth

| Target | Read/list/export | Local backup | Verified restore | Import/new Push |
|---|---:|---:|---:|---:|
| Dolphin Wii (`RFL_DB.dat`) | Read-only verified | Enabled for a valid live DB | Enabled for a host/path-bound valid backup | Experimental opt-in, backup-first |
| Eden (`MiiDatabase.dat`) | Read-only verified | Enabled for a valid live DB | Enabled for a host/path-bound valid backup | Experimental opt-in, backup-first |
| Eden CharInfo/NFIF (88 bytes) | Unsupported | Unsupported | Unsupported | Unsupported |

`WriteGateVerified` and the service write gates remain `false`. Synthetic CRC and mutation tests do not prove that an emulator will accept a write. The normal editor flow is deliberately short: select or create a Mii, apply changes to the offline draft, then choose **Save to emulator**. That last action asks for one explicit confirmation for the exact host/path and creates verified backups before replacement. Import, export, backup and restore are grouped under the advanced section.

The first appearance editor supports the fields that have an unambiguous direct representation in both formats: name, gender, favourite colour, hair style, hair colour and eye colour. It preserves all other record bytes. Wii values follow the documented RFL bit layout; SwitchDB values follow its StoreData bit sequence and both checksums are regenerated. Style and colour IDs are intentionally shown as emulator IDs because their visible order differs between Wii and Switch. A thumbnail-driven catalog and the remaining face controls (face, eyes, brows, nose, mouth, facial hair, glasses and mole) are not yet implemented rather than being written with unverified mappings.

## Format invariants encoded in tests

Wii databases are exactly 127456 bytes, start with `RNOD`, contain 100 records of 74 bytes at offset 4, and store a big-endian CRC-16/XMODEM at byte 127454 over bytes `[0, 127454)`. Bytes 7404 through 127453 are opaque and preserved verbatim. Record text and every documented bit-field range are fail-closed.

Eden databases are exactly `0x1A98` bytes, start with the native little-endian `NFDB` magic bytes, contain 100 `StoreData` areas of `0x44` bytes, and place version/count/database CRC after those records. The database CRC covers every byte before the final CRC. Each record validates its UUID, CoreData ranges, data CRC, and Eden's zero-device-id CRC. Whole-database transactions never treat an 88-byte CharInfo/NFIF object as a database.

CRC-16/XMODEM uses polynomial `0x1021`, initial value zero, no reflection and no final XOR. The canonical `123456789` fixture must produce `0x31C3`.

## Transaction invariants

- The operation captures target kind, resolved path, stable connection-profile ID, and host before asynchronous work begins. The Windows panel is disabled for the entire operation, and disconnect is blocked.
- The emulator process is checked three times. A running emulator refuses the operation; an unavailable check requires an explicit UI acknowledgement.
- An existing live source is backed up locally and to a unique sibling file. Both copies are reread and SHA-256 verified before staging begins.
- Staging always writes a new sibling temp file. The live NAND file is never passed to `DeckClient.WriteBytes`.
- Immediately before rename, host identity and live existence/hash are compared with the captured preflight state (CAS).
- Rename stays on the same filesystem. Best-effort fsync is issued for temp, target, and parent.
- A timeout or disconnect around rename is reconciled by rereading the target. A verified post-write hash is success, an unchanged pre-write hash is not committed, and any other or unreadable state is indeterminate. The UI never labels a potentially committed transaction as refused.
- A valid backup bound to the same host ID, target kind, and path may restore a missing target. This is recorded with `HadLiveSource=false`; no fictitious pre-restore backup is claimed.
- Manifests and inventory are host/profile bound. Backup content and format are revalidated before restore. Successful operations append host, path, pre/post/backup hashes and backup locations to a protected audit log.

The automated fault-injection suite covers live-file races, host switches, missing-target restore, partial upload, rename timeout after commit, pre-commit rename failure, post-move disconnect, post-write mismatch, local/selected-backup corruption, process gates, and immutable target snapshots.
