# 06 — Backup and restore

## 1. Whole-database backups (⚑DB13)

| Dialect / place | Who takes it | YFex.Backup.Data source | PITR |
|---|---|---|---|
| Managed Postgres (cloud) | platform (automatic backups + PITR window) | `PlatformOwned` record: checks freshness through the platform API or a probe table, runs drills | platform |
| Postgres on-prem with pgBackRest/WAL-G | customer's DBA | `PlatformOwned` + drills | the tool |
| Postgres on-prem, nothing installed | YFex.Backup | `PgDumpSource` (`pg_dump -Fc`, external process; tool path detected, override in config) — logical, no PITR; optional `PgBaseBackupSource` + WAL archive folder in a later phase (DB-P10) | only with DB-P10 |
| SQL Server on-prem | YFex.Backup (or SQL Agent → `PlatformOwned`) | `SqlServerNativeSource`: `BACKUP DATABASE … TO DISK` (+ `BACKUP LOG` if full recovery model) into a staging folder the server can write, then sealed and shipped | log backups give PITR |
| SQLite (small central, broker, terminal) | YFex.Backup | `SqliteOnlineSource`: `SqliteConnection.BackupDatabase` (online, consistent) to a temp file, then sealed | none (point = backup time) |

Staging files are deleted after sealing; they hold plaintext, so the staging folder must be on the same protected disk as the database
(08 §1).

## 2. Per-tenant backup = logical export (⚑DB14)

- Scope: one tenant (`TenancyPath`, depth 1): its **rows and its files** (⚑DB28, §9).

- Tables: every row-secured table from `RowSecurityCatalog` (tenant key column known) + owners' `ITenantDataSet` declarations
  (01 F2/F3). Order: parents first (FK graph from `SchemaSnapshot`, overridable).
- Consistency: one **repeatable-read / snapshot** transaction per export (Postgres `REPEATABLE READ` + `SET TRANSACTION SNAPSHOT` for
  parallel readers; SQL Server `SNAPSHOT` isolation; SQLite one read transaction).
- Format: 02 §2.1, versioned, golden test. ⚑ open: binary rows (fast, YFex-only) vs a documented portable format (portability to a
  customer). Recommendation: binary for backups/moves, a separate **portability export** (JSON/CSV) for LGPD art. 18 requests.
- Uses: tenant backup, tenant move copy, scratch-to-live restore, customer leaving the service (hand them their data).

## 3. Point-in-time restore (⚑DB15)

Whole-database only, by the database/platform. YFex.Backup offers `PointInTimeToScratchAsync(db, at)` as a **driver** over platform
APIs (Azure/AWS restore-to-new-server) or native tools on-prem, and then the tenant export from the scratch database. YFex never
replays logs itself.

## 4. Restore (⚑DB16)

Targets: `ScratchDatabase` (drills, investigations), `NewDatabase` (disaster recovery), `InPlaceTenant`, `InPlaceDatabase` (on-prem
disaster recovery, permission + approval).

Common steps:
1. Read manifest, check signature and checksums; unseal the data key (needs the ring — 08 §2).
2. **Schema check:** set's `SchemaVersions` ≤ target's; older sets are imported then migrated by `SchemaInstaller`; newer sets are refused.
3. Restore data.
4. **Re-apply the erasure ledger** from the manifest's `ErasureLedgerPosition` to now (⚑DB21).
5. **Restore epoch:** publish `DataRestored(scope, consistentAt, epoch)`. Owners react (requests 09 §2): Cluster bumps fencing tokens
   for the scope above any issued value; Audit starts a new chain segment linking to the archived head instead of rewinding; Jobs
   recomputes next fires and does not re-run fires already recorded in history after `consistentAt`; Notifications marks restored
   `Pending`/`Sending` rows for review rather than resending.
6. Audit event `backup.restored` with set id, target, who, approvals.

`InPlaceTenant` additionally: tenant maintenance (⚑CL21) → safety export of current rows → delete tenant rows in FK-reverse order →
import → steps 4–6 → resume. The safety export is kept like any set (so the restore itself can be undone).

## 5. Storage locations and verification (⚑DB17, ⚑DB19)

- Destinations: `LocalFolderDestination` (atomic write: temp file + flush + rename; per-OS notes 08 §1) and, through the Files bridge,
  S3 / Azure Blob / SFTP / SMB. Each destination has a **region tag** (residency) and declares `SupportsImmutability` (S3 Object
  Lock, Azure immutable blob policy; local/SFTP: no).
- Production policies need **two destinations, one off-site** (other region or customer's own storage); the runner warns otherwise.
- Verification levels: (1) checksum at write and at re-read (sample of parts daily); (2) manifest signature; (3) **restore drill** into
  a throwaway database (SQLite file, Docker/Postgres scratch in the cloud) with row counts per table vs the manifest and app-declared
  smoke queries. Drill results stored in the catalog; gauge `yfex.backup.last_verified.age` per policy; alert when > 2 × period.
- Platform-owned sources are drilled too (restore-to-scratch through the platform API), because "the platform does backups" is not
  proof.

## 6. Encryption (⚑DB18)

- Per set: random 256-bit data key; parts encrypted in AES-GCM chunks (chunk index in the nonce/AAD, so reordering fails); the data key
  sealed to a `RecipientKeyRing` (Cryptography sealing; hybrid 0x11 when the ring has ML-KEM keys). Several recipients allowed:
  operator ring, **optional** vendor recovery ring on-prem (customer opt-in, audited).
- Tenant exports can be sealed to the tenant's ring from `ScopedKeyRings` → deleting the tenant's ring makes all its exports unreadable
  (tenant offboarding).
- Manifest is signed with the host's signing key (Installations evidence key on-prem/site; service key in the cloud).
- Keys are **not** in the backup. The key-ring backup is a separate, documented procedure (Cryptography) — losing the ring loses every
  backup; the README of the generated policy says so in the admin view.

## 7. Retention (⚑DB20)

- GFS per policy; a set is deleted only when no rule keeps it, it is not `Held`, and (immutable stores) its lock expired.
- Legal hold: command `backup.hold` (permission, audited), scoped to a tenant or a database.
- Owners with legal minimums declare them (Audit evidence, fiscal documents — Brazilian fiscal records commonly 5 years); the policy
  validator refuses a retention shorter than the longest declared minimum for the data in the scope. Backup does not invent legal
  periods.
- Residency applies to retention copies too: a set never lands in a destination whose region is not allowed for every tenant in it.

## 8. Catalog (⚑DB23)

`backup.sets`, `backup.drills`, `backup.erasures` (the ledger: position, subject key id, scope, time, owner) in the region's
coordination database (cloud) or central database (on-prem); at sites and on USB, `index.json` next to the sets. Every set's signed
manifest sits beside its parts, so a restore after total loss needs only: the files + the key ring.

## 9. Files in backups (⚑DB28)

- A backup set of a scope has **two parts under one manifest**: the database part and the files part (each tenant prefix of every
  regional store declared in the policy, e.g. `documents`).
- **Order:** database snapshot first, then files. A file added after the snapshot is an extra file (harmless, removed by the next
  cleanup); a file referenced by a row is never missing.
- Files part = file list (path, size, SHA-256, version id where the store has versions) + content; unchanged files are **not copied
  again** (incremental by checksum/version against the previous set; the manifest references the earlier part).
- Cloud buckets: platform **versioning + cross-region replication** recorded as `PlatformOwnedFiles`, checked for freshness and drilled
  (restore a sample of files). A point-in-time tenant restore uses the store's **object versions as at that time** when available.
- On-prem and sites: YFex.Backup copies files itself (local folders, shares, the customer's store).
- Restore: database part, then files part; files whose rows were not restored are skipped; residency checked for the target store.
