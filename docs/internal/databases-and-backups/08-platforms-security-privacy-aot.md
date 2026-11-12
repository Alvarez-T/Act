# 08 — Platforms, security, privacy, AOT

## 1. Per-OS differences (spikes where unverified)

| Topic | Windows | Linux | macOS |
|---|---|---|---|
| Native tools | `pg_dump.exe`/SQL Server local; service account needs write access to the backup folder (SQL Server writes the `.bak` itself, as its own service account) | `pg_dump` from distro packages (version must be ≥ server major); SQL Server on Linux writes as `mssql` user | dev only (Postgres.app/Homebrew); not a central target |
| Staging/backup folder protection | ACL to the service account only | mode 0700, umask 077, owner = service user | 0700 |
| Atomic file write | write temp → `FlushFileBuffers` → `File.Move(overwrite)` (ReplaceFile semantics) | write temp → `fsync` file → `rename` → `fsync` directory | same as Linux; `F_FULLFSYNC` for real durability (spike) |
| Removable media (USB) | drive letters, may be absent | `/media/<user>/…` mounts | `/Volumes` |
| Service host | YFex.Hosting as Windows service | systemd unit | launchd (sites only if Mac terminals) |
| SQLite online backup | `Microsoft.Data.Sqlite` `BackupDatabase` — same on all three (spike: WAL mode during backup on each) | | |

Process launching of external tools uses `ProcessStartInfo.ArgumentList` (no shell), credentials through environment variables
(`PGPASSWORD` avoided in favour of a temp `.pgpass` 0600 / Windows integrated auth), never on the command line.

## 2. Threats

| Threat | Mitigation |
|---|---|
| Stolen backup files | always sealed (⚑DB18); manifest leaks only ids, sizes, times |
| Attacker deletes backups (ransomware) | immutable off-site destination; destinations' credentials are write-only where the store allows (S3 `PutObject` without `DeleteObject`); deletion by retention runs with a separate credential |
| Malicious restore (roll a tenant back to erase evidence) | `backup.restore` permission + inline two-person approval in the cloud; Audit chains are never rewound (06 §4) |
| Tampered set | manifest signature + chunk AEAD; restore refuses |
| Credentials in catalog | references only (⚑DB5); secrets from Cryptography secret protection or platform identity |
| Lost key ring | every backup lost: the key-ring backup procedure (Cryptography) is part of the install checklist; admin view shows "key ring not backed up" |
| Cross-tenant leak in an export | export reads through row security with the tenant set; drill checks no foreign tenant ids in the set |
| Stale node writes to the old home after a move | placement version on write connections + source read-only (04 §3) |

## 3. Privacy (LGPD)

- **Erasure (art. 18 VI) vs backups (⚑DB21):** backups are not rewritten. The erasure ledger records *that* subject S was erased in
  scope X at time T (subject key id only) and is re-applied after every restore. Where data is encrypted with subject keys (Audit ⚑A22,
  Privacy tiers), destroying the key makes the data unreadable in every backup at once. Retention of backups is finite (GFS), so old
  copies age out; the privacy notice states the backup window.
- **Portability (art. 18 V):** the portability export (06 §2) reuses the tenant/subject export machinery.
- **International transfer (art. 33):** residency rules (04 §5) also govern backup destinations and failover regions.
- **Minimisation:** logs and metrics carry database/tenant ids and sizes only; manifests carry no personal data.
- Scratch databases from PITR hold full personal data: created in the same region, sealed disk, dropped by a job after N hours
  (default 24), audited.

## 4. AOT and dependencies

- All packages AOT/trim-clean, `IsAotCompatible`, AotSmoke with 0 IL warnings; manifest JSON through System.Text.Json source
  generation; tenant-data declarations are explicit configuration (an optional attribute would use a generator, never reflection).
- No third-party package: compression from the BCL (`BrotliStream`/`ZLibStream`), crypto from YFex.Cryptography, SQLite backup from
  `Microsoft.Data.Sqlite` (already used by YFex.Data's SQLite dialect), cloud stores through YFex.Files. Platform restore APIs (Azure,
  AWS) would be optional bridge packages if ever needed (⚑ open, DB-P10).
- Hot path: only the router (one cached lookup per connection open); benchmarked with allocation tests (0 B per route on a warm cache).
