# 09 — Phases, acceptance, requests to other libraries, implementation log

## 1. Working rules (every phase)

- **No code before the user confirms ⚑DB1–⚑DB30** (README §6, answers §6.3). Each phase re-reads current source of the libraries it touches.
- `C:\Dev\YFex` is not a git repository; each `YFex.*` sibling is its own Git root. Commit on `main` of the changed repo only when
  the user asks. Before touching an existing file in another repo, copy it to `C:\Temp\dbbackup-backup\<phase>\` (same relative path).
- Requests in §2 are done **by their owners**, who decide the shape.
- RoslynMcp for exploration, `dry_run_compile` before non-trivial edits, the `yfex-csharp` skill. No new NuGet package without asking.
- Build `--artifacts-path C:/Temp/dbbackup-artifacts`; AOT smoke 0 IL warnings; tests on Windows **and** Linux (`linuxKit`), Postgres
  and SQL Server suites in Docker.
- Record test counts in §5; keep a memory file (`yfex-databases-backups.md`) current.

## 2. Requests to other libraries

| # | Owner | Request | Why | When |
|---|---|---|---|---|
| TN1 | YFex.Tenancy (Registry) + core | [tenancy plan](../tenancy/README.md): registry, regions, residency, provisioning steps; core `TenancyPath.Root`, `RegionId`, `ResidencyRule`, `ITenantLocations`, `ReadFreshness` | ⚑DB1, DB7, DB8, DB30 | TN-P1 before DB-P3 |
| CQ1 | YFex.Cqrs | `QueryConfigurationBuilder.ReadFromReplica(maxLag)` / `ReadFromPrimary()` (distinct from the cache's `StaleAfter`); the pipeline sets core `ReadFreshness` while a query runs | ⚑DB29 | DB-P7 |
| FI2 | YFex.Files | one logical store → one bucket **per region** + tenant prefix; `files.prefix` provisioning step with `MoveAsync`; object versions readable by time | ⚑DB27, DB28 | DB-P2 / DB-P5 |
| DA1 | YFex.Data | host `YFex.Data.Placement` (+ `.Cluster` bridge); a scoped `YFexConnectionFactory` registration fed by the router | ⚑DB1, ⚑DB4 | DB-P3 |
| DA2 | YFex.Data | expose the row-security catalog's table → tenant column map read-only; snapshot transactions per dialect | tenant export | DB-P4 |
| DA3 | YFex.Data | a "read-only for scope" row-security flag (source after a move) | 04 §3 | DB-P6 |
| TE1 | YFex.Tenancy | nothing in contracts; optionally a `ResidencyRule` slot on the app's tenant record (app-defined) | Tenancy stays database-free | — |
| CL1r | YFex.Cluster | tenant maintenance (⚑CL21) usable by moves/restores; notice `placement.changed`; **fencing-token bump on `DataRestored`**; one cluster per region | 05 §1, 06 §4 | DB-P4/P6 |
| JO1r | YFex.Jobs | `[Singleton]` (⚑CL8 C) for backup/drill/retention jobs; on `DataRestored` recompute next fires, don't repeat recorded fires | ⚑DB22 | DB-P1 uses one-node host; DB-P2 |
| FI1 | YFex.Files | `IFileStore` with write-only credentials and immutability capability (S3 Object Lock, Azure immutable policy) | ⚑DB17 | DB-P2 |
| CR1 | YFex.Cryptography | sealing of large streams in chunks (or a documented pattern on `RecipientKeyRing`); **key-ring backup/escrow procedure** | ⚑DB18 | DB-P1 — **BUILT 2026-11-11**: `SealedStream` (0x82), `KeyRingEscrow`, `KeyRingProvider.RestoreAsync`, procedure cryptography 06 §8 |
| IN1 | YFex.Installations | signing key for site/central manifests; site → central upload channel (resumable) | ⚑DB24 | DB-P9 |
| OF1 | YFex.Offline | broker host module runs the site policy; reconciliation after a central restore (sites resend what central lost) | 07 §2–§3 | DB-P9 |
| AU1 | YFex.Audit | on `DataRestored`/`TenantMoved`: new chain segment linking to the archived head; events `backup.*`, `placement.*` | 06 §4 | DB-P4 |
| PR1 | YFex.Privacy | **erasure ledger** writer (subject key id, scope, time) called by every erasure; subject-key crypto-shredding stays Privacy/Audit | ⚑DB21 | DB-P1 (ledger table), DB-P4 — **file + in-memory log and replay BUILT 2026-11-11** (`YFex.Privacy.Erasure`); SQL log waits for `YFex.Privacy.Data` |
| NO1 | YFex.Notifications | declare the outbox as tenant data (`backup.TenantData(...)` in its module configuration) with `OnRestore = Review`; no blind resend after restore | 03 §6 | DB-P4 |
| HO1 | YFex.Hosting | backup module in host profiles (central, broker) | ⚑DB22 | DB-P1/P9 |
| AZ1 | YFex.Authorization | permissions `backup.view/run/restore/hold`, `placement.view/move/failover`; inline approval for whole-database restore and failover | ⚑DB25 | DB-P2 |
| ALL | every owner with tenant data outside row security | tenant-data declarations in module configuration (`backup.TenantData(...)`; attribute optional later) | 01 F2 | DB-P4 |

## 3. Dependencies

- DB-P1 needs Cryptography (built) and Data (built) only. DB-P3 needs tenancy TN-P1 (core types); DB-P5 needs TN-P3 (regions, residency). DB-P2 needs Files F-P1 (planned) and ideally Jobs `[Singleton]`
  (needs Cluster CL-P2). DB-P6/P8 need Cluster tenant maintenance (CL21 phase). DB-P9 needs Offline 4f and Hosting H0.

## 4. Phases

### DB-P0 — Decisions — **DONE 2026-11-12** (except owner acknowledgements)
User confirms ⚑DB1–⚑DB30; repo `YFex.Backup` created from the YFex.Time template; owners acknowledge requests.

### DB-P1 — Smallest useful backup (on-prem / site, local folder) — **DONE 2026-11-12** (YFex.Backup `7f8ed46`; log §5)
- `YFex.Backup` contracts, runner, local-folder destination, sealing, signed manifest, GFS retention, `index.json` catalog, erasure
  ledger table; `YFex.Backup.Data` with **SQLite online** and **SQL Server native** sources; restore to `NewDatabase`/`InPlaceDatabase`.
- **Acceptance:** a SQLite and a SQL Server (Docker) database are backed up, a byte flip is detected, the set restores into an empty
  database with equal row counts; an erasure done after the backup is re-applied after restore; retention keeps exactly the GFS
  sets over a simulated year (fake clock); Windows + Linux; AOT 0 warnings.

### DB-P2 — Off-site, drills, platform-owned records
- `YFex.Backup.Files`, `YFex.Backup.Jobs`, two-destination rule, immutability, restore drills, `PlatformOwned` freshness checks,
  `backup.sets` table, gauges, permissions.
- **Acceptance:** off-site copy to S3-compatible storage (MinIO in Docker) with object lock; delete refused before lock expiry;
  weekly drill result visible; stale platform backup raises the alert.

### DB-P3 — Placement catalog + router, one database
- `YFex.Data.Placement` with static catalog and router; scoped factory registration; `ITenantDatabase`; `data.placement` provisioning step.
- **Acceptance:** existing apps run unchanged with the static catalog; 0 B allocated per route on a warm cache (benchmark).

### DB-P4 — Per-tenant export and restore
- logical export/import, tenant-data declarations, scratch restore, in-place tenant restore under maintenance, `DataRestored`
  event, portability export.
- **Acceptance:** two tenants in one Postgres; tenant A restored to an earlier export while B keeps writing; B unchanged; A's
  outbox rows marked for review; Audit chain continues with a new segment.

### DB-P5 — Several databases
- `SqlPlacementCatalog`, `IPlacementPolicy`, residency checks, notices, `TenantInOtherRegionException`.
- **Acceptance:** 3 databases, 2 regions in Docker; a residency-restricted tenant cannot be placed or backed up outside its region.

### DB-P6 — Tenant move
- **Acceptance:** move under write load; pause ≤ 5 s at p95 for a 1 GB tenant; no lost or duplicated rows; stale node write fails.

### DB-P7 — Read replicas
- **Acceptance:** lag above max falls back to primary; read-your-writes holds within the window.

### DB-P8 — Region failover support
- failover command, runbook, epoch bump, tenants without an allowed region stay paused.
- **Acceptance:** harness: region A cut, failover to B, fencing tokens above pre-failure values, no outbox resend without review.

### DB-P9 — Shop-site backups
- broker module, upload to central, USB destination, restore of a replaced broker.
- **Acceptance (with Offline/Installations):** broker replaced from the latest set and resynced; site sets unreadable by other sites.

### DB-P10 — Postgres on-prem PITR and platform restore drivers
- `PgBaseBackupSource` + WAL archive folder, PITR to scratch on-prem; optional cloud restore drivers if the user wants them.

## 5. Implementation log

- **2026-11-12 — DB-P1 built** (repo `YFex.Backup`, committed `7f8ed46`).
  - **`YFex.Backup`** (→ core, Cryptography, **Privacy**): `BackupContracts.cs` (`BackupSetId` v7, `BackupKind`, `BackupScope`,
    `RetentionRule`, `BackupSetState`, `RestoreTarget`); `BackupSources.cs` (`IBackupSource`, `IBackupPartWriter`, `IBackupPartReader`,
    `BackupSnapshot`, `RestoreRequest`, `RestoredDatabase`, `BackupIntegrityException`); `BackupDestinations.cs` (`IBackupDestination`,
    `LocalFolderDestination`: temp → flush to disk → rename, 0700/0600 on Unix); `BackupCatalog.cs` (`IBackupCatalog`,
    `JsonIndexCatalog` = `index.json` format 1); `BackupManifest.cs` + `ManifestFormat.cs` (manifest format 1, signature file format 1,
    domains `yfex.backup.manifest.v1` / `yfex.backup.part.v1`, registered in cryptography 05 §9); `PartPipeline.cs` (Brotli → `SealedStream`
    to 1–8 recipients with context `[set id, part]`, through `System.IO.Pipelines`; reverse writes a file only when every chunk
    authenticated and the length matches); `BackupRunner.cs` (run → read-back check of every destination → `Checked`; failed runs
    recorded `Failed` and their files removed; warning under two destinations; `ApplyRetentionAsync`); `GfsRetention.cs` (newest set per
    day / ISO week / month / year in the policy's time zone); `BackupVerifier.cs` (`VerifyResult`: signature, part presence, length,
    SHA-256); `BackupRestorer.cs` (verify → unseal → source restore → row-count comparison → `ErasureReplayer` from the set's start
    minus clock skew; `RestoreWithoutErasureReplayAsync` only for throwaway databases).
  - **`YFex.Backup.Data`** (→ YFex.Data, YFex.Data.Sqlite, YFex.Data.SqlServer; one package for every dialect, user decision
    2026-11-12): `SqliteOnlineSource` (`BackupDatabase` to staging, `journal_mode=DELETE` in the copy, exact row counts; restore to a new
    file or online over the live database); `SqlServerNativeSource` + `SqlServerStaging` (server/local view of one folder;
    `BACKUP DATABASE … WITH COPY_ONLY, CHECKSUM`; `RESTORE … WITH CHECKSUM` + `MOVE` to the instance's default folders for a new
    database, `REPLACE` under `SINGLE_USER` in place; all names and paths as parameters).
  - **`YFex.Backup.Testing`**: `BackupHarness` (fake clock, rings, staging), `InMemoryBackupDestination` (`CorruptByte`), `InMemoryBackupCatalog`.
  - **Tests** (`YFex.Backup.Tests`, 40): runner/verify/restore (13, incl. byte flip, edited manifest, part moved into another set,
    foreign ring, failed run, two recipients), retention (5, incl. a simulated year with `Daily(3).Weekly(4).Monthly(1)` = exactly 6 sets
    and `7/4/12/1` = the month ends, held/failed sets, time zone), stored formats (10 golden/negative), SQLite (6: restore into a new
    file with equal counts, byte flip, erasure after the backup re-applied, in-place, local folder + `index.json` + Unix modes, no
    overwrite), SQL Server (1, env-gated), architecture (5). **Windows 39 + SQL Server 1 (separate run); Linux (WSL) 40/40 with SQL
    Server 2022 Developer in Docker** (container `mcr.microsoft.com/mssql/server:2022-latest`, staging bind-mounted at
    `/var/opt/mssql/staging`; env `YFEX_TEST_SQLSERVER`, `…_STAGING`, `…_STAGING_LOCAL`). AOT smoke (SQLite backup to a local folder,
    verify, restore with erasure replay, byte flip): win-x64 and linux-x64 **0 IL warnings, exit 0**.
  - **Acceptance:** all items met (SQLite and SQL Server backed up; byte flip detected; restore into an empty database with equal row
    counts; erasure after the backup re-applied; GFS over a simulated year; Windows + Linux; AOT 0 warnings).
  - **Changed from the 02 sketch:** a source writes parts through `IBackupPartWriter` instead of returning `IAsyncEnumerable<BackupStream>`
    (the runner owns sealing and staging); `BackupScope.Database` is a logical name `string` until `YFex.Data.Placement`'s `DatabaseId`
    exists (DB-P3); the manifest's `ErasureLedgerPosition` became `ErasureReplayFrom` (a time: Privacy's log is read by time) and gained
    `Policy`, `Source`, `Dialect`, `Tables`, `RowCountsExact`; each part has its own data key (one `SealedStream` per part) rather than
    one per set; `RetentionRule` counts only (schedule = DB-P2 Jobs, ⚑DB13).
  - **Not in DB-P1 / open:** the erasure **ledger table** stays Privacy's (`YFex.Privacy.Data`, PR1) — restores take any `IErasureLog`
    (file log on-prem/site); no `AddYFexBackup` DI yet (no new package; with the Jobs bridge in DB-P2); the schema-version check of 06 §4
    step 2 is not done (sources report no versions yet); SQL Server row counts are taken from the live database right after the backup
    (`RowCountsExact = false`); SQL Server **in-place** restore and log backups are untested/not built; no directory fsync after rename
    (same as Cryptography's protected files); Windows folder ACLs are inherited, not set. ⚑DB31–⚑DB33 (README §6.4) pending; ⚑EG4 (Orchestration for restore) and ⚑EG12 (Validation for policies) of the ecosystem-fit review touch DB-P1 and are pending too.
  - **Seen, not ours:** YFex.Data pins `Microsoft.Data.Sqlite` 9.0.1 → `SQLitePCLRaw.lib.e_sqlite3` 2.1.10, which NuGet flags NU1903
    (high-severity advisory GHSA-2m69-gcr7-jv3q); for YFex.Data's owner. In WSL, Docker containers stop when the VM idles between
    `linuxKit` calls: start the SQL Server container and run the tests in one `linuxKit run`.

- **2026-11-12 — DB-P0.** ⚑DB1–⚑DB30 decided (README §6.3). CR1, PR1 and this design committed (YFex.Cryptography `a8ee2ad`,
  YFex.Privacy `4d41610`, YFex `11db902`). Repo `C:\Dev\YFex\YFex.Backup` created (git `main`, from the YFex.Time template):
  `backup.slnx`; `src/YFex.Backup` (→ core, Cryptography), `src/YFex.Backup.Data` (→ YFex.Backup, YFex.Data),
  `src/YFex.Backup.Testing` (→ YFex.Backup, YFex.Time.Testing), all empty and `IsAotCompatible`; `tests/YFex.Backup.Tests` with
  `ArchitectureTests` (main package never references Data/Files/Jobs/Cluster, the Data bridge references only YFex.Backup + YFex.Data,
  every source project is AOT-compatible; the repo path is stamped into the test assembly so it works with `--artifacts-path`);
  `tests/YFex.Backup.AotSmoke`. `.Files` and `.Jobs` bridges wait for DB-P2. Windows 4/4, Linux (WSL) 4/4; AOT smoke win-x64 and
  linux-x64 0 IL warnings, exit 0 (an empty package proves only that the projects link). **Open:** the owners in §2 have not
  acknowledged their requests yet; each is picked up in that owner's own session.

- **2026-11-11 — prerequisites CR1 + PR1 (no DB phase started).** Committed 2026-11-12 (see DB-P0 entry).
  - CR1, YFex.Cryptography: `Sealing/SealedStream.cs` (chunked STREAM sealing to 1–8 recipients, golden), `Keys/KeyRingEscrow.cs`
    (escrow export/import, golden plaintext), `KeyRingProvider.RestoreAsync` (store an imported ring, never overwrite); tests
    `SealedStreamTests` (14) + `KeyRingEscrowTests` (6, incl. hybrid ML-KEM); AOT smoke check. Windows 264/265, Linux (WSL, OpenSSL
    3.5) 264/265 — the one failure is `ArchitectureTests` flagging YFex.Automation's DPAPI store (not this work). AOT 0 IL warnings.
  - PR1, YFex.Privacy: `Erasure/ErasureLog.cs` (`ErasureRecord`, `IErasureLog`, `IErasureReplayTarget`, `ErasureReplayer`,
    `ErasureRecordRules`, `InMemoryErasureLog`), `Erasure/FileErasureLog.cs` (JSON lines, fsync per entry, 0600, torn-line tolerant,
    strict otherwise); `ErasureLogTests` (5); AOT smoke check. Windows erasure tests 5/5 (two TextScrubber timeout tests flake under
    parallel load, pass alone), Linux 97/97. AOT 0 IL warnings.
