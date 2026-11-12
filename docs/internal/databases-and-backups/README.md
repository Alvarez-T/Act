# YFex databases and backups — tenant placement over several databases and regions, routing, moves, replicas, failover; backup and restore — Design

> **Status:** DESIGN 2026-11-11. Docs only: no code, no repo. **User answered ⚑DB1–⚑DB30 on 2026-11-11 (§6.3); all follow-up proposals accepted the same day — every ⚑DB decided.**
> **Revision 2026-11-11 (user feedback):** tenants are keyed by **`TenancyPath`**, never a string; **regions, residency and new-tenant
> registration moved to [YFex.Tenancy.Registry](../tenancy/README.md)** (⚑TN), with the shared types in core (`RegionId`,
> `ResidencyRule`, `ITenantLocations`); **Files** resolves one bucket per region; **backups cover database and files**; replica reads are
> chosen in the **Cqrs Aggregate Configuration** (`ReadFromReplica`), not by a generator; code reads through **`ITenantDatabase`**
> instead of passing router + scope. Rows changed: ⚑DB1, DB3, DB4, DB7, DB8, DB10, DB14, DB17; new: ⚑DB27–DB30.
> **Origin:** 2026-11-11, while discussing YFex.Cluster ([cluster README §8](../cluster/README.md), "Several databases in the cloud").
> The user agreed: **several regions = one cluster per region**, each with its own coordination database; **which database/region a
> tenant lives in is not Cluster's job** (YFex.Data / Tenancy), and **backups are not Cluster's job** (database/platform), though
> Cluster can make one node run a backup job (lease) and maintenance mode (⚑CL21) can pause a tenant during a restore. Jobs will use
> Cluster leases for single-node scheduled work (⚑CL8 option C, a later `[Singleton]`). This plan names the owners of the rest.
> **Repos (proposed):** a new package **`YFex.Data.Placement`** inside `C:\Dev\YFex\YFex.Data` (⚑DB1) and a new sibling repo
> **`C:\Dev\YFex\YFex.Backup`** (⚑DB12). Everything else is requests to existing owners ([09 §2](09-phases-and-acceptance.md)).
> **Audience:** the user first; then the sessions that implement the phases and the owners of Data, Tenancy, Cluster, Jobs, Files,
> Cryptography, Installations, Offline, Audit and Privacy.
> **Normative vs sketch:** every name here is a **proposal** until the user approves §6; code marked `// (sketch)` is illustration.
> Names of existing YFex code were checked against source on 2026-11-11 ([01](01-current-state-and-inventory.md)).

## 1. What it is, in one paragraph

A YFex app in the vendor cloud will outgrow one Postgres server, and some customers must keep their data in a given country (LGPD,
contracts). On-premises centrals and shop sites need backups that someone can actually restore. **This plan splits the problem into
two parts with different owners.** (1) **Placement:** a small catalog says *which database each tenant lives in* (and so which region),
a router opens the right connection for the current tenant, and a move procedure copies a tenant to another database without losing
writes. (2) **Backup:** the **database or platform** takes whole-database backups and point-in-time recovery where it can (managed
Postgres, WAL archiving, SQL Server native backup, SQLite online backup); **YFex.Backup** schedules what the platform does not,
adds what only the app can do (**one tenant's** export and restore, encryption with YFex keys, off-site copies through YFex.Files,
**verification by restoring**, retention that respects LGPD erasure) and covers on-premises centrals and shop sites where no platform
exists.

## 2. What it is not

| Not | Who does it | Our part |
|---|---|---|
| **Coordinating server processes** (leases, single worker, drain) | YFex.Cluster | Backup and move steps run as Cluster single workers / Jobs `[Singleton]`; one cluster **per region** |
| **Replicating a database** (streaming replication, Always On, logical replication setup) | the database / platform (managed Postgres, Patroni, SQL Server AG) | the router **uses** replicas for reads and knows their lag (05 §2); never configures them |
| **Deciding a failover between regions** | a person (the operator), with the platform | the catalog flips a region's role; apps see a new primary; a runbook + checks (05 §4) |
| **Physical backup engines** (pgBackRest, WAL-G, `pg_basebackup`, Azure/AWS PITR) | the database/platform | YFex.Backup **registers** them as "platform-owned" in the catalog, checks they ran, and runs **restore drills** (06 §5) |
| **File storage protocols** (S3, Azure Blob, SFTP, shares) | YFex.Files | Backup writes through `IFileStore` (bridge `YFex.Backup.Files`) |
| **Keys and encryption primitives** | YFex.Cryptography | Backup seals archives to a recipient key ring (06 §6) |
| **Deciding what personal data must be erased or kept** | YFex.Privacy (classification, erasure), YFex.Audit (evidence retention) | backups keep an **erasure ledger** applied after every restore, and prefer crypto-shredding (08 §3) |
| **Tenant identity, registration, home region, residency** | YFex.Tenancy (`ITenantStore`, `ScopeIdentity`) and the new **YFex.Tenancy.Registry** ([tenancy](../tenancy/README.md)); shared types in core (`ITenantLocations`) | placement is keyed by `TenancyPath` and works **inside** the tenant's home region; Tenancy does not learn about databases |
| **Sharding *inside* one tenant** (one tenant's rows across servers) | nobody: not built (⚑DB6) | a tenant always lives in exactly one database |

## 3. Shape and packages

| Package | Repo | Contents | Depends on |
|---|---|---|---|
| `YFex.Data.Placement` | YFex.Data | `DatabaseId`, `DatabaseRole`, `TenantPlacement`, `IPlacementCatalog` (read), `IDatabaseRouter` (tenant → `YFexConnectionFactory`, primary or replica; for infrastructure code), **`ITenantDatabase`** (current tenant, `Read`/`Write`; for app code), scoped `YFexConnectionFactory` that honours core `ReadFreshness`, static (config) catalog, SQL catalog table, the **move** state machine (`TenantMove`), the `data.placement` provisioning step, Meter `YFex.Data.Placement`. Region and residency come from core `ITenantLocations` | YFex.Data, YFex core (`TenancyPath`, `RegionId`, `ITenantLocations`, `ReadFreshness`) |
| `YFex.Data.Placement.Cluster` (bridge) | YFex.Data | runs move steps and catalog refresh as Cluster workers; catalog changes as notices | the above + YFex.Cluster |
| `YFex.Backup` | YFex.Backup (new) | contracts (`BackupPolicy`, `BackupSet`, `BackupManifest`, `IBackupSource`, `IBackupDestination`, `IBackupCatalog`, `RestorePlan`), runner, retention (GFS), verification, **erasure ledger**, local-folder destination, encryption through Cryptography sealing, Meter `YFex.Backup`, `AddYFexBackup` | YFex core, YFex.Cryptography, Microsoft.Extensions.Hosting.Abstractions |
| `YFex.Backup.Data` | YFex.Backup | sources over YFex.Data: **SQLite online backup**, **SQL Server native `BACKUP`**, **Postgres `pg_dump`/`pg_basebackup`** (external tool, on-prem only), **logical tenant export/import** (row-secured tables of one tenant) | the above + YFex.Data (+ dialect package the app picks) |
| `YFex.Backup.Files` (bridge) | YFex.Backup | `IBackupDestination` over YFex.Files `IFileStore` (S3, Blob, SFTP, share) with object lock / immutability where the store has it, **and** `FileStoreSource`: the tenant's files (its prefix in each regional store) as a backup source (⚑DB28) | the above + YFex.Files |
| `YFex.Backup.Jobs` (bridge) | YFex.Backup | policy → scheduled job (`[Singleton]` when Jobs has it, else a Cluster worker) | the above + YFex.Jobs (+ Cluster) |
| `YFex.Backup.Testing` | YFex.Backup | `BackupHarness`: in-memory destination, fake clock, corrupt-byte injection, restore into a throwaway SQLite/Postgres | the above + YFex.Time.Testing |
| tests, AotSmoke, Benchmarks | both | Windows + Linux (`linuxKit`; Postgres/SQL Server in Docker) | |

**Why two homes?** Placement is about *opening connections*, which YFex.Data owns, and must stay tiny on the hot path (one lookup
per connection). Backup has its own lifecycle, storage, keys and schedules and is not on any hot path; putting it in Data would pull
Cryptography and Files into every Data user. **No `YFex.<Engine>` names** (reserved for Data providers): dialect sources live in
`YFex.Backup.Data`.

## 4. The three places (main design: [07](07-three-places.md))

| | **Vendor cloud** | **On-premises central** | **Shop site** |
|---|---|---|---|
| Databases | many Postgres servers in 1+ regions; managed | usually **one** (Postgres, SQL Server or SQLite) | broker store + terminal stores (SQLite) |
| Placement | catalog + router; moves between servers and regions; replicas | **static one-database catalog** (zero cost) | none (each device has its own store) |
| Whole-database backup / PITR | **platform** (managed PITR); YFex checks and drills | **YFex.Backup.Data** drives native tools; PITR only where WAL/log archiving is set up | SQLite online backup of the broker store |
| Per-tenant backup | YFex.Backup logical export (restore one tenant, give a tenant its data) | rarely needed (one tenant) | n/a |
| Off-site | other region bucket (Files) | Files: vendor cloud bucket, customer's S3, a USB/NAS share | upload to central through Offline/Installations, or a local USB |
| Who runs it | Jobs `[Singleton]` / Cluster worker, one per region | the one host (YFex.Hosting) | the broker's host module |

## 5. Vocabulary

| Term | Meaning | Example |
|---|---|---|
| **Database** (`DatabaseId`) | one logical database the app can connect to: server + database name + credentials reference | `br-sp-03` |
| **Tenant key** | the tenant's `TenancyPath` (depth 1); `scope.Current.Root` gives it from any deeper path | `farmaviva` |
| **Region** (`RegionId`, core) | a named location with its own cluster, coordination database, databases and buckets; recorded in the [tenant registry](../tenancy/04-regions-and-residency.md) | `br-south`, `eu-west`, `on-prem` |
| **Home region** | the region of a tenant's data, chosen at registration by YFex.Tenancy.Registry | FarmaViva → `br-south` |
| **Placement** | the record "tenant T lives in database D" **inside its home region** (+ version, state) | FarmaViva → `br-sp-03`, version 7 |
| **Residency** (`ResidencyRule`, core) | where a tenant's data **may** live, owned by the registry, checked by every library | FarmaViva: `br-*` only |
| **Home database** | the database holding a tenant's primary data; exactly one | |
| **Replica** | a read-only copy kept by the platform; has **lag** | `br-sp-03-r1`, lag 300 ms |
| **Move** | copy a tenant to another database and switch its placement, with a short write pause | FarmaViva `br-sp-03` → `br-sp-07` |
| **Backup set** | one finished backup: manifest + encrypted parts + checksums, of a whole database or one tenant | `full/br-sp-03/2026-11-11T03:00Z` |
| **PITR** | point-in-time restore: base backup + log/WAL replay to a moment | "the database as at 14:32:10" |
| **Restore drill** | a scheduled restore into a throwaway database that proves a backup set is usable | weekly, results in the catalog |
| **Erasure ledger** | the list of LGPD erasures done since a backup, re-applied after a restore | subject 81… erased 2026-11-02 |

Pharmacy words (FarmaViva, Loja Centro) appear only in examples.

## 6. Decisions to confirm (⚑DB)

Prefix **⚑DB** (checked 2026-11-11: unused; ⚑DS exists — data-schema).

### 6.1 Placement, routing, residency, moves, replicas, failover

| ⚑ | Question | Recommendation | Alternative (cost) | Details |
|---|---|---|---|---|
| **DB1** | Where does placement live? | **Split by level:** region and residency in **YFex.Tenancy.Registry** + core types (⚑TN1, ⚑TN3); tenant → **database inside the region** in new package **`YFex.Data.Placement`** in the YFex.Data repo (+ bridge `YFex.Data.Placement.Cluster`); tenant → **bucket inside the region** in YFex.Files (⚑DB27) | everything in `YFex.Data.Placement` (Files and Backup would reference Data to learn a region); a new repo `YFex.Regions` (a second catalog that must agree with the tenant registry) | 02 §1, 04, [tenancy](../tenancy/README.md) |
| **DB2** | Unit of placement | **The tenant** (top `TenancyPath` segment): all of a tenant's stores, branches and data live in one home database | per store (a tenant's reports would join across servers); per table (sharding inside the app) | 04 §1 |
| **DB3** | Catalog storage | One **`placement.tenants`** table in the **global catalog database** (the same database as the tenant registry, ⚑TN15; small, replicated by the platform, readable from every region), cached per node, versioned; refresh on a Cluster notice and every 60 s. On-prem: **static catalog** from configuration (one database, no table) | catalog inside each tenant database (cannot find the tenant before connecting); a DNS/service-discovery system (platform-specific) | 04 §2 |
| **DB4** | API for app code | App code **never passes router + scope**: (1) commands and most handlers keep injecting a scoped **`YFexConnectionFactory`**, already routed to the current tenant (primary, or a replica when the query is configured for it, ⚑DB29); (2) explicit choice inside a method: **`ITenantDatabase`** (`Write()`, `Read(maxLag)`) for the current tenant. **`IDatabaseRouter.RouteAsync(TenancyPath, DataIntent)`** stays for infrastructure code working on *another* tenant (backup, moves, registry). Row security keeps working: factories still carry `RowSecurity` | `IDatabaseRouter` + `ITenancyAccessor` in every handler (noise; easy to pass the wrong scope); a connection string in `ITenantInfo` (mixes secrets into tenant records) | 04 §3, 03 §2 |
| **DB5** | Connection credentials | The catalog stores a **credential reference**, never a secret; the router resolves it through the host's secret source (Cryptography secret protection / platform identity: Azure Managed Identity, AWS IAM auth) | connection strings in the table (one leak = every tenant) | 08 §2 |
| **DB6** | Sharding model | **Tenant-per-database-server pool**: many tenants per database (row security), a big tenant may get its own database (`Dedicated`). No sharding inside one tenant | Citus / distributed Postgres (third-party engine, cloud-only, different SQL limits); schema-per-tenant (catalog bloat with thousands of tenants) | 04 §1 |
| **DB7** | Who chooses a new tenant's database? | The **tenant registry** chooses the **region** at registration (⚑TN5) and runs Placement's `data.placement` provisioning step; inside the region an app-supplied **`IPlacementPolicy`** picks the database (default: most free capacity tag); the operator can pin | Placement registers tenants itself (a second registration path; no lifecycle) | 04 §4, [tenancy 05](../tenancy/05-lifecycle-and-provisioning.md) |
| **DB8** | Residency | **Owned by the tenant registry** (⚑TN6), read through core `ITenantLocations`; Placement **refuses** a placement, replica or move outside it; Backup and Files obey the same rule | a residency setting per library (they disagree); documentation only | 04 §5, 08 §3 |
| **DB9** | Moving a tenant | **Copy → catch up → short write pause → switch → verify → retire**: logical copy of the tenant's rows (same code as the per-tenant backup), catch-up by change tracking, then Cluster **maintenance mode for that tenant** (⚑CL21) for seconds, final delta, placement version +1, notice, old rows kept read-only for N days then deleted. Fencing: the placement version is checked when a connection is opened for writes | dump/restore with the tenant offline for minutes–hours; logical replication per tenant (Postgres-only, publication per tenant does not scale) | 05 §1 |
| **DB10** | Read replicas | **Opt-in per query**, declared in the Aggregate Configuration (⚑DB29) or `ITenantDatabase.Read(maxLag)`; the router measures lag (platform metric or `pg_last_xact_replay_timestamp()`) and falls back to the primary when lag > max; a request that just wrote reads its own writes from the primary (session stickiness window, default 5 s) | replicas for all reads by default (stale reads after a sale break UX and invariants) | 05 §2 |
| **DB11** | Failover between regions | **Manual, operator-started, with a runbook**: platform promotes the replica region; the operator flips the region's `DatabaseRole` in the catalog; the catalog version rises; apps reconnect. Automatic only **inside** a region (platform HA). Data residency limits which region may be the standby | automatic cross-region failover (split-brain, residency violations, money written twice with async replication) | 05 §4 |

### 6.2 Backup and restore

| ⚑ | Question | Recommendation | Alternative (cost) | Details |
|---|---|---|---|---|
| **DB12** | Where does backup live? | New sibling repo **`YFex.Backup`**: `YFex.Backup`, `YFex.Backup.Data`, `YFex.Backup.Files`, `YFex.Backup.Jobs`, `YFex.Backup.Testing` | inside YFex.Data (pulls Cryptography + Files into every Data user); inside Files (Files is transport, has no idea of databases or restore) | §3 |
| **DB13** | Who takes whole-database backups? | **Platform first**: managed PITR in the cloud, pgBackRest/WAL-G or SQL Server Agent on-prem when the customer has them; YFex.Backup **records** them as `PlatformOwned` sources and checks they are fresh. YFex.Backup **drives** native tools only where nothing else exists (small on-prem, SQLite, sites) | YFex does all backups itself (re-implements mature tools, worse PITR); YFex does none (small shops end with no backup at all) | 06 §1 |
| **DB14** | Per-tenant backup | **Logical export** of one tenant (keyed by `TenancyPath`): its rows (row-secured tables, plus owner-declared tables through `ITenantDataSet`) **and its files** (⚑DB28), stream format versioned with a golden test, imported into the same or another database. Also feeds tenant moves (⚑DB9) and LGPD portability (08 §3) | per-tenant physical backups (impossible in a shared database); no per-tenant restore (one tenant's mistake means restoring everyone) | 06 §2 |
| **DB15** | Point-in-time restore | **Whole database only**, by the platform/native tooling. "Restore one tenant to 14:32" = PITR into a **scratch database**, then per-tenant export from the scratch, then import (⚑DB16). YFex never replays WAL itself | per-tenant change log in YFex (a second WAL; huge) | 06 §3 |
| **DB16** | Restoring one tenant into a live shared database | Under **tenant maintenance mode** (⚑CL21): export current rows (safety copy) → delete tenant rows → import → re-apply erasure ledger → bump placement version + Cluster epoch for that tenant → resume. Audit chains of the tenant are **not rewound**: the restore is an audited event, and audit rows come back only from the Audit archive | rewrite in place without pause (concurrent writes interleave with restored rows) | 06 §4 |
| **DB17** | Backup storage locations | `IBackupDestination`; built-in **local folder**; others through **`YFex.Backup.Files`** (S3, Blob, SFTP, SMB share). Policy names **≥ 2 destinations** for production (3-2-1: one off-site / other region), with **immutable** (object lock / WORM) where the store supports it | one destination (ransomware or a region loss deletes the only copy) | 06 §5 |
| **DB18** | Encryption | **Always encrypted** before leaving the process: a random data key per backup set, sealed to a **`RecipientKeyRing`** (Cryptography sealing, hybrid where available). The vendor's recovery key is an extra recipient **only when the customer opts in** (on-prem). Keys for a tenant export can be the tenant's ring → crypto-shredding works on backups | store-side encryption only (the store operator can read it); one shared password (lost or leaked for every customer) | 06 §6 |
| **DB19** | Verification | Three levels: **checksums** of every part at write and at read; **manifest signature**; **restore drill** into a throwaway database on a schedule (default weekly for full sets, monthly per-tenant sample) with row counts and app-declared checks. A set is `Verified` only after a drill | checksums only (restorable-ness unknown until the bad day) | 06 §5 |
| **DB20** | Retention | **GFS** (daily N, weekly N, monthly N, yearly N) per policy, set by the app/operator; **legal holds** pin sets; deletion is a job; minimums for fiscal/audit evidence come from their owners (Audit seals, Fiscal), not from Backup | keep forever (cost; contradicts LGPD minimisation); fixed 30 days (loses fiscal evidence windows) | 06 §7 |
| **DB21** | LGPD erasure vs backups | **Erasure ledger** (subject + scope + time, no personal data beyond the subject key) re-applied after any restore; prefer **crypto-shredding** for columns encrypted with subject keys (Audit ⚑A22) so old backups become unreadable for that subject. Backups are **not** rewritten to erase | rewrite every backup on each erasure (impossible with immutable storage); ignore (a restore resurrects erased data: an LGPD violation) | 08 §3 |
| **DB22** | Scheduling | Policies become jobs through **`YFex.Backup.Jobs`**, using **Jobs `[Singleton]`** (Cluster lease, ⚑CL8 option C) so one node per region runs each; on-prem one host, site the broker | own scheduler in Backup (a second cron engine) | 03 §4 |
| **DB23** | Backup catalog | A **`backup.sets`** table in the region's coordination database (cloud/central) or a JSON index next to the files (site, USB): set id, source, scope, time range, destinations, verification state, key id, manifest hash. The **manifest travels with the set** so a restore works with the catalog lost | catalog only in the database (lost with the database it describes) | 06 §8 |
| **DB24** | Shop-site backups | Broker store: SQLite online backup nightly + **upload to central** through Offline/Installations (sealed to central's ring) + optional local USB; terminals: none by default (they resync from broker/central), opt-in for terminals holding unsent sales | per-terminal full backups to the cloud (bandwidth; data already flows through the outbox) | 07 §3 |
| **DB25** | Restore permissions and audit | Restore and move are **commands** with permissions `backup.restore`, `placement.move` (two-person approval for whole-database restore in the cloud, Authorization inline approvals); every backup/restore/move/delete is an Audit event | ops scripts outside the app (no audit trail) | 08 §2 |
| **DB27** | File buckets per region | YFex.Files maps **one logical store name to one bucket per region** (`Store("documents").PerRegion(...)`) and a tenant prefix; app code names the store only; residency checked through `ITenantLocations`. Files contributes the `files.prefix` provisioning step | one bucket for all regions (data abroad); app code picks buckets (region logic everywhere) | 04 §6 |
| **DB28** | Backup covers files | Database and files of a scope go in **one backup set** (one manifest, two parts). Order: database snapshot **first**, files after (extra files are harmless, missing files are not). Cloud buckets: platform versioning + cross-region replication recorded as `PlatformOwned`; YFex copies files itself only for tenant exports, moves, portability, on-prem and sites | separate file backups (a restore brings back rows whose files are gone) | 06 §2, §9 |
| **DB29** | Who decides replica reads? | The **query declares** its tolerance in the **Cqrs Aggregate Configuration**: `b.Query<Q,R>().ReadFromReplica(maxLag)` / `.ReadFromPrimary()`; commands always primary; a global default in Placement options. The Cqrs pipeline sets core **`ReadFreshness`** for the query; the scoped factory reads it (Cqrs does not reference Data). Placement owns *which* replica and the fallback; Cluster only runs the lag probe on one node. An attribute (`[Stale]`) + generator is an optional shortcut later. Name chosen to avoid the existing cache method `StaleAfter` | a generator/attribute as the main way; Cluster or an orchestrator deciding (they cannot know if a query tolerates old data) | 05 §2, 03 §3 |
| **DB30** | Tenant key in APIs | **`TenancyPath`** of depth 1 (⚑TN2); `scope.Current.Root` from deeper paths; never a free string | strings (store path or display name passed by mistake); a new `TenantId` struct (second scope key) | 02 §1 |
| **DB26** | Phase order ("start small") | P1 **on-prem/site SQLite + SQL Server native backup to a local folder, encrypted, checked, retained, restorable**; P2 off-site via Files + restore drills + platform-owned records; P3 static placement catalog + router (one database, zero behaviour change); P4 per-tenant export/restore; P5 multi-database catalog + new-tenant policy + residency; P6 tenant move; P7 replicas; P8 region failover; P9 site upload; P10 Postgres native on-prem + PITR orchestration | placement first (cloud's need, but nobody restores anything until P5) | 09 §4 |

### 6.3 User answers 2026-11-11

Confirmed as recommended: **DB1, DB5, DB7, DB8, DB9, DB11, DB12, DB14, DB18–DB23, DB26–DB30**. Confirmed with additions; the proposals below were **accepted by the user 2026-11-11**:

| ⚑ | User answer | Proposal |
|---|---|---|
| DB2 | OK, but configurable (e.g. a database for multi-tenant) | Unit stays the tenant; *where* tenants go is configuration: `Shared` pool databases (many tenants) and `Dedicated` databases, declared per database in the catalog |
| DB3 | OK, without leaking anything | Catalog holds no secrets (DB5) and no tenant personal data: `TenancyPath`, database id, version, state only; read access by a read-only role; changes are audited commands; per-node cache in memory only |
| DB4 | Extend `YFexConnectionFactory`/`YFexConnection` or add `ITenantDatabase`? | **Keep `YFexConnectionFactory` unchanged** (it already takes a `Func<DbConnection>`): Placement registers it **scoped**, with the delegate resolved by the router for the current tenant — app code changes nothing. **No tenant on `YFexConnection`**: a connection is already bound to one database, switching tenant on it would bypass routing and row security. `ITenantDatabase` stays a small optional service only for an explicit `Write()` / `Read(maxLag)` choice inside one method |
| DB6 | Configurable through rules, automatic, or fixed by dev/registry | Three layers, highest wins: (1) **operator / tenant registry pin**; (2) **rules in `IPlacementPolicy`** (e.g. plan = Enterprise → `Dedicated`; size > N GB or load > N → *suggest* `Dedicated`); (3) dev default. New tenants are placed automatically; **moving an existing tenant is only suggested** (alert + one-click move command), never automatic, because a move pauses writes |
| DB10 | Default? | **Primary** (`ReadFromPrimary`) for every query unless it opts in; global default in Placement options can be changed per app |
| DB13 | Configurable counts and intervals (e.g. 1 last month, 1 per week, last 3 days) | Yes: schedule (how often) is separate from keep rules (how many). Example `Schedule.Daily(02:00)` + `Keep.Daily(3).Weekly(4).Monthly(1).Yearly(0)` = the last 3 daily, 4 weekly, 1 monthly set. Per policy, per scope (database, tenant, site) |
| DB15 | Needs a special column in every table? | **No.** PITR is whole-database from the database's own log (WAL / SQL Server log backups): no column. The tenant is then cut out of the scratch database by the **tenant column row security already uses** (`RowSecurityCatalog`, request DA2); tables without one are declared by their owner (`ITenantDataSet`) |
| DB16 | While paused, can replica reads, jobs, or async commands continue? | **Async commands**: accepted and **queued** (outbox/messaging), run after resume. **Jobs** for that tenant: deferred, run after resume (no lost fires, ⚑DB22 rule). **Sync commands**: refused with "tenant in maintenance" + Retry-After. **Reads**: off by default (the replica replays the restore's deletes/imports, so it would show half-restored data); optional `ReadDuringRestore = SafetyCopy` serving reads from the safety export in a scratch database for long restores |
| DB17 | Configurable; options for cloud providers | Built-in local folder / UNC share / USB; via `YFex.Backup.Files`: **S3 API with Object Lock** (best default: one driver covers AWS S3, MinIO, Wasabi, Backblaze B2, Cloudflare R2), **Azure Blob** with immutability policy, **Google Cloud Storage** with bucket lock, **SFTP**, SMB/NFS. Recommended pair: local + S3-compatible with Object Lock in another region/site |
| DB24 | OK, respect the broker rules | Checked against Offline ⚑O20/⚑O2: the broker stays **generic** (no app code, no central secrets); what it backs up is **its own broker store** (roster, custody envelopes, budgets, site time), never a business database; sealed to **central's** ring, so the broker cannot read old sets; it is a module of the generic broker host (HO1), not app code; only the active broker (⚑O2) runs it; a replaced broker restores from central's copy then resyncs |
| DB25 | User doubts it | **Keep** permissions (`backup.restore`, `placement.move`, …) and Audit events: a restore changes or brings back personal data (LGPD evidence) and is cheap with Authorization already built. **Drop the mandatory two-person approval**: make it an optional policy (`RequireApproval`), off by default, suggested for cloud whole-database restore and failover |

## 7. Files

| # | File | Content |
|---|---|---|
| 01 | [01-current-state-and-inventory.md](01-current-state-and-inventory.md) | what exists today (file:line), gaps, findings |
| 02 | [02-model-and-contracts.md](02-model-and-contracts.md) | placement types, router, move states; backup types, sources, destinations, catalog, manifest |
| 03 | [03-api-and-examples.md](03-api-and-examples.md) | dev-side code first: composition per place, routed query, move, policies, restore one tenant, tests |
| 04 | [04-placement-routing-residency.md](04-placement-routing-residency.md) | unit of placement, catalog, router, new tenants, residency |
| 05 | [05-moves-replicas-failover.md](05-moves-replicas-failover.md) | tenant move, read replicas, in-region HA, cross-region failover runbook |
| 06 | [06-backup-and-restore.md](06-backup-and-restore.md) | whole-database, per-tenant, PITR, one-tenant restore, storage, verification, encryption, retention, catalog |
| 07 | [07-three-places.md](07-three-places.md) | scenarios: vendor cloud, on-premises central, shop site |
| 08 | [08-platforms-security-privacy-aot.md](08-platforms-security-privacy-aot.md) | Windows/Linux/macOS, threats, LGPD, AOT |
| 09 | [09-phases-and-acceptance.md](09-phases-and-acceptance.md) | working rules, requests to other libraries, dependencies, phases DB-P0…DB-P10, acceptance, empty log |

Related: [tenancy](../tenancy/README.md) (registry, regions, residency, provisioning steps — ⚑TN), [cluster](../cluster/README.md) (⚑CL8, ⚑CL21, one cluster per region), [files](../files/README.md), [data-schema](../data-schema/README.md),
[audit 08](../audit/08-privacy-retention-and-access.md) (crypto-shredding), [hosting](../hosting/README.md), [offline](../offline/README.md),
[installations](../installations/README.md), [privacy](../privacy/README.md).

## 8. Open questions (not blocking the decisions)

- **Global catalog availability:** if the catalog database's region is down, other regions run on their cached catalog (read-only
  catalog; no new tenants, no moves). Is a per-region copy of the catalog (logical replication) needed from day one?
- **Event-sourced stores** (YFex.EventSourcing) and **Audit archives**: do they get their own backup sources (append-only, cheap
  incremental) or ride the database backup? Proposed: ride the database until measured.
- **Search indexes and caches** are rebuilt, never backed up — confirm per owner (Search, Persistence).
- **Billing for off-site storage** per tenant (Licensing meters?) — out of scope here.
- **Customer-held keys** (BYOK) for cloud backups — later, with Cryptography.
