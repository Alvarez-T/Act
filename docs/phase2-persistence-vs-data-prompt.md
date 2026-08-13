# Prompt — Decide the boundary between YFex.Persistence (local) and YFex.Data (database)

You are a .NET architecture reviewer. **Do not write code or change anything.** Investigate
the two codebases below, then produce an analysis, a recommendation with trade-offs, and a
short list of decisions you need me to make. Look and understand before proposing — I have
been burned by premature solutions, so bias toward evidence over opinion.

## Repos on disk (two separate solutions)

- **Main framework:** `C:\Dev\YFex\YFex` — solution `yfex.slnx`. Mono-repo of YFex libraries.
- **Data framework:** `C:\Dev\YFex\YFex.Data` — solution `data.slnx`. A database / data-mining
  framework. It references the main repo's `YFex` core. Cross-repo project references are
  already used in both directions (e.g. `..\..\..\YFex\src\YFex\YFex.csproj`).

## The two libraries whose responsibilities blur

- **YFex.Persistence** (`YFex/src/Persistence/…`) — meant as **local** persistence: snapshots of
  app/UI state, config, consent, caches, with dumb pluggable backends (Memory, FileSystem,
  IndexedDb planned). Key types: `ISnapshotStore`, `ISnapshotProvider`, `PersistenceService`,
  `SnapshotEnvelope`, `ILocalStore`; file backend in `YFex.Persistence.FileSystem`
  (`FileSystemSnapshotStore`, `FileSystemLocalStore`).
- **YFex.Data** (`YFex.Data/src/YFex.Data`) — **database** access: `YFexConnection`,
  `YFexConnectionFactory`, `IQueryDialect`/`SqlDialect`, `QueryBuilder`, and a schema /
  table-versioning model in `Schema/` (`ITable`, `TableCreator`, `VersioningTableCreator`,
  `TableVersionRepository`, `VersionTable` = `TABLE_VERSION`). Engine providers: `YFex.Sqlite`,
  `YFex.Oracle`, `YFex.Postgres`, `YFex.SqlServer`, plus Mongo/CSV/Excel.

## How we got here (context, not gospel)

Storage code was consolidated toward Persistence, then a SQLite durable queue was **bridged onto
YFex.Data** via a new `YFex.Persistence.Data` project. That bridge is now considered the wrong
direction. The unresolved questions are the **boundary** and the **naming**. My working
hypothesis (challenge it): *"if it lives in a database, it's YFex.Data; YFex.Persistence is
local-only"* — and *migrations/schema-versioning should be one generic capability in YFex.Data,
reused cross-project, never reinvented.*

## Concrete artifacts to inspect

1. **Telemetry offline / durable queue** — a local retry buffer for telemetry events that failed
   to ship; SQLite is just the durable file, the data is opaque (not queryable app data):
   - `YFex/src/System/YFex.System/Telemetry/IOfflineQueue.cs`, `NullOfflineQueue.cs`,
     `TelemetryBatch.cs` (the consumer), `TelemetryEvent.cs`, `TelemetryEventQueueSerializer.cs`
   - `YFex/src/System/YFex.System.Windows/SqliteOfflineQueue.cs` (SQLite-backed)
   - Persistence-side abstractions currently: `YFex/src/Persistence/YFex.Persistence/IDurableQueue.cs`,
     `IQueueItemSerializer.cs`, `NullDurableQueue.cs`
   - The bridge under review: `YFex/src/Persistence/YFex.Persistence.Data/DataDurableQueue.cs` (+ its csproj)
2. **Security event store** — a genuine relational database of captured security events:
   - `YFex/src/Security/YFex.Security.Storage/`: `SecurityDbConnectionFactory.cs`,
     `Migrations/MigrationRunner.cs` (+ embedded `Migrations/Sql/001_initial.sql`),
     `Repositories/EventRepository.cs`, `BaselineRepository.cs`, `ApiCatalogRepository.cs`,
     `StorageServiceCollectionExtensions.cs`
   - Consumer: `YFex/src/Security/YFex.Security.Service/MigrationHostedService.cs`
   - Uses `Microsoft.Data.Sqlite` + Dapper; owns its own connection factory + ordered
     embedded-SQL migration runner with a `_migrations` ledger.
3. **A third self-contained SQLite store (for comparison)**:
   - `YFex/src/Messaging/YFex.Messaging.Rpc.Sqlite/`: `SqliteConnectionFactory.cs` (one shared
     connection + inline `CREATE TABLE` schema), `SqliteOutbox.cs`, `SqliteClientCache.cs`, etc.
     Depends only on `Microsoft.Data.Sqlite`. No YFex.Data.
4. **YFex.Data's existing versioning**: `YFex.Data/src/YFex.Data/Schema/*.cs`.

## Verify these observations yourself (don't take them on faith)

- Only the telemetry queue (via the bridge) currently depends on YFex.Data; Security and
  Messaging are self-contained on `Microsoft.Data.Sqlite`. Confirm via csproj `ProjectReference`
  graphs and grep for `YFexConnection`.
- Three different SQLite stores each re-implement "open WAL connection + create schema."
- YFex.Data's versioning is per-table `CreateScript` (`VersionTable`), **not** ordered SQL-file
  migrations like Security's `MigrationRunner`.

## Questions to answer

1. **Boundary rule.** State a crisp, one-line rule distinguishing YFex.Persistence from YFex.Data
   that someone could apply to any new type. Back it with what the code actually does today.
2. **Migrations.** Should schema versioning live solely in YFex.Data as one generic reusable API?
   If so, sketch that API given the existing `VersionTable`/`TableCreator`, decide whether it must
   support ordered embedded-SQL migrations (Security's style) or per-table versioning is enough,
   and describe how Security adopts it (dropping its own `MigrationRunner`).
3. **Offline queue home.** Is a SQLite-backed telemetry retry buffer a *database* (YFex.Data)
   concern or a *local persistence* (YFex.Persistence) concern? Weigh that it's an opaque local
   buffer, not queryable app data. Give the consequences of each placement (dependencies, reuse).
4. **Shared SQLite foundation.** Should the three self-contained SQLite stores share a foundation,
   or is the duplication acceptable? If shared, where should it live given the boundary and the
   separate-repo constraint?
5. **Naming.** Does "Persistence" cause confusion because databases also persist? Recommend
   keeping `YFex.Persistence` (with a sharpened charter) vs renaming (e.g. `YFex.LocalStore`,
   `YFex.LocalState`), and estimate the rename cost (namespaces, references, package ids).

## What to search / read

- csproj `ProjectReference` graphs for every project above; grep for `YFexConnection`,
  `IQueryDialect`, `Microsoft.Data.Sqlite`, `CREATE TABLE`, `GetManifestResourceStream`,
  `IOfflineQueue`, `IDurableQueue`, `VersionTable`, `TableCreator`.
- `C:\Dev\YFex\YFex\CLAUDE.md` for the dependency-layer map (Persistence is a low layer).
- Whether YFex.Data is consumed as NuGet or only via project refs (`data.slnx`,
  `YFex.Data/Directory.Build.props`, its `Version`).

## Constraints & deliverable

- **Do not implement.** Analysis only.
- Treat YFex.Data as a separate solution/repo; weigh cross-repo coupling honestly.
- Ignore pre-existing unrelated build breakage in `YFex.NavigatR.Tests` and `YFex.Persistence.Tests`.
- Present trade-offs; do not force a single answer; **end by listing the decisions only I can make.**

Deliverable sections: (1) boundary rule; (2) migrations recommendation + generic API sketch +
Security adoption; (3) offline-queue home with trade-offs; (4) shared foundation yes/no + where;
(5) naming recommendation + cost; (6) open questions for me.
