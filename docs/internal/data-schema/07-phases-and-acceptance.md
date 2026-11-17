# 07 — Phases and acceptance

Phases are sized for one session each. DS-P0 is independent and can run in parallel with DS-P1/P2; DS-P4 and DS-P5 are independent of each other. Every phase ends
with `data.slnx` built in **Release** (Debug can fail while RoselYn MCP locks the generator DLL), and the private-feed
workaround applied: restore with `-p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true`, then `--no-restore`.
Every phase also passes the convention gates of [09 §5](09-library-conventions.md#5-phase-gates-added-by-this-document):
no new LINQ, 0 new trim/AOT warnings with the AOT smoke green, own types on the public API, and benchmarks for any
hot path touched (DS-P4, DS-P6).

| Phase | Repo(s) | Scope | Depends on |
|---|---|---|---|
| **DS-P0** | YFex.Data (+ consumers' csproj) | Provider split (⚑DS6): `YFex.Data.Sqlite/Postgres/SqlServer/Oracle` = dialect, bulk writer, driver, (later) schema generator, row-security DDL. `YFex.DataMining.<Engine>` = mining provider, interceptor. `YFex.Sqlite` gains `Microsoft.Data.Sqlite` (needed for `ClassifyError`) | — |
| **DS-P1** | YFex.Data | Model (02), `ISchemaDialect` + `SchemaSqlGenerator` + four generators (03 §1–§4), `SchemaValidator`, `SchemaAssert`, golden tests | — |
| **DS-P2** | YFex.Data | `SchemaModule` versions, `yfex_schema_history`, locks, `ScriptSchema` / `ApplySchemaAsync` / `GetSchemaStatusAsync`, `SchemaRegistry`, `AddYFexSchemaMigration`; delete `ITable` & co. (04) | P1 |
| **DS-P3** | YFex.Offline, YFex.Authentication, YFex (Rules.Data) | Modules replace `Statements(SqlEngine)` / `CreateTableScript`; enums deleted; registration; validation tests on four dialects; fix the SQL Server key-width finding (06 §1) | P2 |
| **DS-P4** | YFex.Data | Generator front-end (08 §6): `[DataTable]`/`[DataIndex]`/`[Ascii]`/`[Precision]` + standard and EF attributes by name (⚑DS10); emitted `Schema`, `Columns`, `BulkTable`, `IBulkRowMapper<T>`; compile-time YFSCH diagnostics; `SchemaAssert.InSync` printing missing steps. Pilot: Rules.Data's table | P1, P2 |
| **DS-P5** | YFex.Data | EF bridge: `ApplyYFexModule`, `CreateYFexSchemaHistory`, `EfSchemaRenderer`, model export, `InstallYFexRowSecurity`, catalog-vs-model check, provider→dialect registry (05) | P2 (P0 for the registry's module initializers) |
| **DS-P6** | YFex.Data + consumers | `BuildInsertIfAbsent`, `ClassifyError`; consumers drop catch-all `DbException`; shared connection-per-call helpers (03 §7); `RowSecurityKey.SqlType` derived from modules (03 §6) | P0 (drivers), P3 |
| **DS-P8** | YFex.Data (+ call sites) | Bulk-write ergonomics (10): `IBulkRow<TSelf>` in the base list of every `[DataTable]` row, `WriteAsync` overloads on `IBulkWriter` (span/array/memory/read-only list + `IAsyncEnumerable` + explicit-table escape hatch), concrete `List<T>` source; call sites and the two user guides updated. **Done 2026-11-13** | P4 |
| **DS-P7** | YFex.Data | Optional: live verification (`VerifySchemaAsync`: `module.Current` vs introspection; needs the `RelationalProviderBase` TODOs done), a fifth engine (MySQL) as proof of 03 §5 | P2; introspection |

## Acceptance

### DS-P0
- `YFex.Offline.Data` / `YFex.Authentication.Data` / a host using PostgreSQL resolve **no** `YFex.DataMining`, MemoryPack
  (beyond YFex.Data's own), YamlDotNet or Microsoft.Extensions.AI assembly. Check with `dotnet list package --include-transitive`.
- All existing tests green: YFex.Data.Tests (145 + 3 skipped), EF (8), Tenancy (13), DataMining (2), Web.Data (30),
  Rules.Data (15), plus the Offline and Authentication suites.
- AOT smoke (`tests/YFex.Data.AotSmoke`) publishes and exits 0.

### DS-P1
- Golden files for the test module and for re-modelled copies of the Offline and Authentication tables, on the four
  engines. SQLite and SQL Server output equals today's `Statements(...)` text, apart from whitespace and constraint
  names, **or** each difference is listed in the test with its reason (e.g. the SQL Server key-width fix).
- Each YFSCH001–007 rule has at least one positive and one negative test per engine where it applies.
- Unique-ignoring-NULLs proven by **behaviour** on SQLite (live), and by golden text on SQL Server/Oracle. Live on
  PostgreSQL/SQL Server/Oracle when `YFEX_TEST_*` is set: insert two `(realm, NULL)` rows, both succeed; two
  `(realm, 'A')` rows, the second fails.
- `YFex.Data` stays `IsAotCompatible` with no new IL warnings. The model has no reflection.

### DS-P2
- SQLite live: apply v1, apply again (no-op), add v2 (applies only v2), tamper an applied version's steps
  (`SchemaDriftException`), two concurrent `ApplySchemaAsync` (one waits, both end with each version recorded once),
  failing step (nothing committed, no history row).
- Oracle-style non-transactional path covered by a fake `SchemaFeatures(TransactionalDdl: false)` on SQLite: a failing
  step leaves `state = 1`; the next run refuses; `MarkVersionResolvedAsync` unblocks.
- `ScriptSchema` output equals the statements `ApplySchemaAsync` executed (captured with a command interceptor).
- `src/YFex.Data/Schema/{ITable,TableCreator,TableVersionRepository,VersionTable}.cs` deleted; `ARCHITECTURE.md` and
  `README.md` updated; a `docs/SCHEMA.md` user guide added in YFex.Data.

### DS-P3
- No `SqlEngine` / `RuleStoreDatabase` type left in any repo (`grep`).
- Offline, Authentication and Rules tests create their databases with `ApplySchemaAsync` on SQLite and pass unchanged
  otherwise; `SchemaAssert.Valid` across the four dialects passes (after the 06 §1 fix).
- AOT smoke apps of Offline and Authentication: 0 warnings, exit 0.
- Doc comments of `AddYFexOfflineData` / `AddYFexAuthenticationData` and the repos' READMEs describe the new path.

### DS-P4
- Generator driver tests: every supported CLR type → `ColumnType`; NRT/`Nullable<T>` → nullable; `[Column]` names and
  snake_case default; each compile-time diagnostic fires (unbounded indexed string, missing `[Precision]`, nullable key,
  `[Column(TypeName)]`, identifier > 63) and is suppressible with `#pragma` (real syntax locations, as `DataRowAnalyzer` does).
- The generated `TableSchema` equals the fluent equivalent (structural equality) for the pilot table; its golden DDL is
  unchanged.
- `SchemaAssert.InSync` fails with pasteable step code when a property is added, and passes after the printed step is
  added as a new version.
- Generator stays incremental (`ForAttributeWithMetadataName`, equatable models); AOT smoke still 0 warnings.

### DS-P5
- Tests of 05 §7 green. `YFex.Data` has no reference to EF (checked with a `PublicApiTests`-style assertion).

### DS-P6
- No `catch (DbException)` without a `ClassifyError` filter in Offline.Data / Authentication.Data.
- A forced non-duplicate failure (table dropped) surfaces as an error, not "duplicate".
- Upsert paths use `BuildUpsert` or `BuildInsertIfAbsent`; round trips measured before/after on SQLite (BenchmarkDotNet
  in `YFex.Offline.Benchmarks`), no regression.

### DS-P8
- `data.slnx` Release: 0 errors, no new warnings; AOT smoke publishes with no YFex trim/AOT warning and exits 0.
- Every `[DataTable]` row compiles with `IBulkRow<TSelf>` in its base list, and a row type that declares the interface
  by hand works with the same call.
- The old explicit form (`WriteAsync(Row.BulkTable, BulkRows.From<Row, Row.BulkMapper>(rows), …)`) still compiles: one
  test keeps it, since `BulkMapper` stays public (⚑DS14).
- A `TRow[]` argument binds to the array overload, not the `IReadOnlyList` one (10 §4 trap): asserted by a test that
  reports the source type actually used, not by reading the code.
- Every call site that writes a `[DataTable]` row to its own table uses the one-argument form (⚑DS21);
  `DATA-ACCESS-AND-BULK-WRITE.md` and `SCHEMA.md` updated (10 §7).

## DS-P8 implementation record (2026-11-13)

**Built** in `C:\Dev\YFex\YFex.Data` (uncommitted), design [10](10-bulk-write-ergonomics.md):

- `IBulkRow<TSelf> : IBulkRowMapper<TSelf>` with `static abstract BulkTable BulkTable { get; }`
  (`src/YFex.Data/Bulk/BulkContracts.cs`).
- `DataTableEmitter` puts it in the partial's base list and emits the explicit static
  `IBulkRowMapper<TSelf>.Write` forwarding to `BulkMapper`; the `BulkTable` property already satisfied the interface.
- `BulkWriterExtensions`: a C# 14 `extension(IBulkWriter writer)` block with `ReadOnlySpan<TRow>`, `TRow[]`,
  `ReadOnlyMemory<TRow>`, `IReadOnlyList<TRow>` (each `(rows, ct)` + `(rows, options, ct)`), `IAsyncEnumerable<TRow>`
  and the `(BulkTable, rows)` escape hatch.
- `BulkRows`: the former `ListBulkRowSource` is now `ReadOnlyListBulkRowSource`; `ListBulkRowSource` holds a concrete
  `List<TRow>` (`BulkRows.FromList`); new `AsyncBulkRowSource<TRow, TMapper>` (a class, `IAsyncDisposable`) and
  `BulkRows.From(IAsyncEnumerable<TRow>)`.
- Call sites migrated: `ReadsStore` (YFex.AgentTelemetry), `DataTableRowTests` (3), `AotSmoke`. The rest pass a
  hand-built `BulkTable` or a custom source and stay explicit (⚑DS21).

**Decisions:** ⚑DS14–⚑DS21 approved; ⚑DS16 shipped without the planned `List<TRow>` overload and ⚑DS17 was deferred
(10 §6).

**Findings:**

- A `From(List<TRow>)` overload beside `From(TRow[])` makes every `From<…>([row, row])` collection expression
  ambiguous: it broke six existing call sites immediately (CS0121). A `ReadOnlySpan<TRow>` overload wins over the array
  overload for a collection expression but **ties with `List<TRow>`**, so the extensions carry the span pair and
  recognize a `List` at runtime inside the `IReadOnlyList` overload; `BulkRows` exposes the list source as `FromList`.
- Nested and generic row types never reach the emitter (YFSCH102 rejects them), so the base list needs no special case.

**Acceptance results:** `data.slnx` Release 0 errors (warnings NU1507/NU1603/NU1801 only). Tests: YFex.Data.Tests
**340 passed + 3 skipped** (9 new in `Bulk/BulkRowWriteTests.cs`), EntityFramework 29, Tenancy 13, DataMining 2.
Consumers: YFex.AgentTelemetry builds and its 81 tests pass; YFex.Rules.Data builds. Native AOT:
`dotnet publish tests/YFex.Data.AotSmoke -c Release -r win-x64` → 18/18 checks, exit 0, trim/AOT warnings from
MemoryPack only. Not run: a bulk benchmark comparison (the sources' instruction counts are unchanged by construction,
and the `List` path can only lose a dispatch), and anything on real PostgreSQL / SQL Server / Oracle (the standing gap).

## DS-P6 implementation record (2026-10-27)

**Built** (uncommitted, on top of the uncommitted DS-P0–P5 work):

| Piece | Where |
|---|---|
| `DataErrorKind` (`Other` = 0, `UniqueViolation`, `ForeignKeyViolation`, `Deadlock`, `SerializationFailure`, `Timeout`, `ConnectionFailure`) | `YFex.Data/src/YFex.Data/Dialects/DataErrorKind.cs` |
| `IQueryDialect.BuildInsertIfAbsent(table, columns, keys)` and `IQueryDialect.ClassifyError(DbException)`; `SqlDialect` base = ANSI `INSERT … SELECT … WHERE NOT EXISTS` and SQLSTATE classification (`23505`, `23503`, `40P01`, `40001`, `57014`/`55P03`/`HYT00`/`HYT01`, class `08`, `57P0x`; `ClosedConnectionException` = connection); protected helpers `BuildInsertWhereNotExists(…, tableHint)`, `BuildInsertOnConflictDoNothing`, `AppendColumns`, `CheckInsertIfAbsent`, `ClassifySqlState`. New code renders with `SqlBuilder` on a `stackalloc` buffer, loops only | `Dialects/IQueryDialect.cs`, `Dialects/SqlDialect.cs` |
| Engines: SQLite and PostgreSQL `INSERT … ON CONFLICT (keys) DO NOTHING`; SQL Server `INSERT … SELECT … WHERE NOT EXISTS (SELECT 1 FROM t WITH (UPDLOCK, HOLDLOCK) …)` and `BuildUpsert` = `MERGE INTO t WITH (HOLDLOCK) AS target …`; Oracle `MERGE INTO T target USING (SELECT :c AS C … FROM DUAL) source ON (…) WHEN NOT MATCHED THEN INSERT …`. `ClassifyError`: `SqliteException.SqliteExtendedErrorCode` (2067/1555/2579, 787, 517 busy-snapshot) then primary code (5/6/9 timeout, 14 connection); `PostgresException.SqlState`, `NpgsqlException` with inner `TimeoutException` / `IOException` / `SocketException`; `SqlException.Number` (2627/2601, 547, 1205, 3960, -2/1222, SqlClient's network/availability numbers); `OracleException.Number` (1, 2291/2292, 60, 8177, 1013/54/30006, lost-connection and listener errors). Integer `switch` or string `switch` on SQLSTATE: no allocation, no message parsing | `src/YFex.Data.<Engine>/<Engine>Dialect.cs` |
| `DialectStatement`: a statement rendered once per dialect instance (`InsertIfAbsent`, `Upsert`, or any `Func<IQueryDialect, SqlQuery>`), cached in a copy-on-write array found by reference equality, published with `Interlocked.CompareExchange`; at most 8 dialects cached | `Dialects/DialectStatement.cs` |
| Connection-per-call helpers (03 §7) on `YFexConnectionFactory`: `ExecuteAsync`, `ReadRowsAsync<TConsumer>`, `QueryAsync<T>(map)`, `QueryFirstOrDefaultAsync<T>(map)`, `TryInsertAsync` (`SqlQuery` or `DialectStatement`; `> 0` rows = inserted, `UniqueViolation` = taken, anything else propagates), `UpsertAsync` (one statement; once more on `UniqueViolation`, for non-atomic `MERGE`), `ClassifyError`; plus `QueryAsync` / `QueryFirstOrDefaultAsync` on an open `YFexConnection` (inside transactions). Struct list consumer; without row security the helpers call `DataCommandRunner` directly (no per-call closure, no keyword parse); with row security they go through `DbExtensions` unchanged | `Extensions/ConnectionFactoryExtensions.cs` |
| 03 §6: `RowSecurityKeyTypes.FromModules(policy, generator, modules)` fills `RowSecurityKey.SqlType` from the key column's `TypeName` in `module.Current` (explicit `SqlType` wins; tables outside the modules unchanged; a key column missing from its module table throws). `RowSecurityOptions.SchemaModules` (`ReadOnlyMemory<SchemaModule>`, e.g. `SchemaRegistry.Modules`); `ScriptRowSecurityPolicies` applies it when set and the dialect is an `ISchemaDialect`, so `InstallRowSecurityPoliciesAsync` and EF's `InstallYFexRowSecurity` get it too | `Security/RowSecurityKeyTypes.cs`, `RowSecurityOptions.cs`, `RowSecurityInstaller.cs`, `RowSecurityPolicy.cs` (doc) |
| Tests: `DmlDialectTests` (**28**): golden text ×5 dialects, SQL Server upsert hint, argument checks, insert-if-absent live on SQLite with the SQLite and the ANSI form (1 then 0 rows), conflict on a second unique index = `UniqueViolation`, `TryInsertAsync` true/false for both forms and a **dropped table → `SqliteException` classified `Other`**, upsert, consumers, statement cache, row security through the helpers (application mode rewrites, missing context throws), SQLite FK / PK / NOT NULL / missing table / busy (`Default Timeout=1`) classification, PostgreSQL SQLSTATE theory (9 cases, also through the generic dialect), Npgsql client errors, fallbacks of the other engines, key types per engine, typed installer scripts (PostgreSQL casts, SQL Server function parameters). AOT smoke +4 checks (insert-if-absent, upsert, `ClassifyError`, key types) | `tests/YFex.Data.Tests/DmlDialectTests.cs`, `tests/YFex.Data.AotSmoke/Program.cs` |
| **YFex.Offline**: `OfflineSqlStore` delegates to the helpers (`FirstAsync`, `TryInsertAsync(DialectStatement)`, `UpsertAsync(DialectStatement)`; its `Rows<T>` and update-then-insert-then-update are gone). Nine `static readonly DialectStatement`s: upserts `offline_receipts`, `offline_sequences`, `offline_spending`, `offline_site_settings`; insert-if-absent `offline_sessions`, `offline_claims`, `offline_publications`, `offline_deliveries`, `offline_idempotency` (parameters renamed to the column names; the idempotency claim inserts only its three non-null columns). `SqlOfflineReviewQueue.AddAsync` retries only on `UniqueViolation`/`Deadlock`/`SerializationFailure`. Single-row reads use `FirstAsync` | `YFex.Offline/src/YFex.Offline.Data/*.cs` |
| **Store review (DS-P3 follow-ups):** Oracle stores `''` as NULL, so the text that may be empty is nullable in v1 and written as NULL (`NullIfEmpty`), read back as `""` (`TextOrEmpty`): `offline_review_items.actor`, `offline_findings.actor_subject`, `offline_findings.detail`, `offline_site_settings.central_endpoints`, `.site_feed`. Bounded non-key text (`OfflineSqlSchema.TextLength` = 400) is checked before any SQL on every engine (`BoundedText`): finding scope, kind, actor subject and home scope (`AddAsync`, `ParkAsync`), `resolvedBy`, snapshot subject/scope (`SaveSessionAsync`), idempotency `resultType` | `OfflineSqlSchema.cs`, `OfflineSqlStore.cs`, `SqlReconciliationStores.cs`, `SqlCentralStores.cs`, `SqlIdempotencyRecords.cs` |
| Offline tests +3: `A_failure_that_is_not_a_duplicate_surfaces_as_an_error` (claims, sessions, deliveries, publications, idempotency with their tables dropped → `SqliteException`; before, sessions and delivery receipts were dropped silently), `Empty_text_is_stored_as_null_and_reads_back_empty`, `Bounded_text_columns_are_refused_on_every_engine`; pinned v1 checksum updated | `tests/YFex.Offline.Tests/DataTests.cs` |
| Offline benchmark `StoreBenchmarks` (7 store calls, 200 per invocation, allocations in bytes) | `benchmarks/YFex.Offline.Benchmarks/StoreBenchmarks.cs` |
| **YFex.Authentication**: `SqlStoreBase` over the helpers (`FirstAsync`, `TryInsertAsync`, `IsDuplicate`); `SqlAccountStore.CreateAsync`/`UpdateAsync` catch only `UniqueViolation`; `SqlCredentialStore.SaveAsync` = one `BuildUpsert` keyed by (account, kind), `UniqueViolation` (operator-code index) = `Duplicate`; `TryConsumeFlowAsync`, `SqlExternalLoginStore.AddAsync`, `SqlScopedProviderStore.SaveAsync(…, 0)` = insert-if-absent. Test +1 (`A_failure_that_is_not_a_duplicate_surfaces_as_an_error`: replayed flow, credential upsert, then four dropped tables → `SqliteException`; before, a dropped `authentication_consumed_flows` made every flow look replayed) | `YFex.Authentication/src/YFex.Authentication.Data/*.cs`, `tests/YFex.Authentication.Tests/SqlExternalStoreTests.cs` |
| Docs: `SCHEMA.md` §8 "Row-level security on module tables"; `INTEGRATION-GUIDE.md` §1.2 (helpers, `DialectStatement`, `ClassifyError` filters); `PROVIDER-GUIDE.md` extension points; `ARCHITECTURE.md` project map | YFex.Data |

**Deviations from 03 / the brief:**

- **`BuildInsertIfAbsent` takes `string` / `IReadOnlyList<string>`**, like the existing `BuildUpsert` beside it (09 §1.2 would want `SqlTableName`/`SqlIdentifier`): the stores' statements and `MergeAsync` work on strings, and two parameter styles on one interface would be worse. Revisit both together.
- **Not every form is atomic.** Oracle `MERGE` and the ANSI fallback can still raise a duplicate key under a race, so `TryInsertAsync` also reads `UniqueViolation` as "taken" and `UpsertAsync` runs once more on it. SQLite, PostgreSQL (`ON CONFLICT`) and SQL Server (`UPDLOCK, HOLDLOCK`) are atomic.
- **SQL Server `BuildUpsert` now carries `WITH (HOLDLOCK)`** (not in 03): without it two `MERGE`s of a new key race. `MergeAsync` on SQL Server changes accordingly.
- **SQL Server 547** is foreign key *and* CHECK constraint (one error number); classified `ForeignKeyViolation`.
- **SQLite busy/locked = `Timeout`** (the busy timeout ran out), `SQLITE_BUSY_SNAPSHOT` = `SerializationFailure`; 03's table had no SQLite column for these.
- **The helpers skip `DbExtensions`' secured path when the connection has no row security.** Found by the allocation gate: the first "after" run had `ClaimNew` at 3.09 KB vs 3.08 KB before (the longer `ON CONFLICT` text); the per-call closure and keyword parse of `DbExtensions.Execute` were the avoidable part. Behaviour with row security is unchanged (tested).
- **`QueryFirstOrDefaultAsync<T>`** returns `default` for no row; for a value type that must come back `null` the map returns `T?` (as `GetSequenceAsync` and `ClaimAsync` do).
- **Offline v1 corrected in place again** (five columns became nullable; checksum `161191e6…` → `fca377d2189c13e84115a4d90a051a2a271bc695d7bed811716b3b8cc827d56c`), under the DS-P3 user decision "nothing is in production". A local database created with the DS-P3 v1 now fails with `SchemaDriftException`: recreate it.
- **Bounded non-key text is guarded in the stores**, not with SQLite `check (length(…) <= n)` in the SQLite generator: the generator change would alter every module's SQLite DDL; the store guard matches DS-P3's key-column guard.
- **Not in DS-P6 scope, left as they are:** `RowSecurityStep` (03 §6 second bullet); Rules.Data's `SqlRuleStore` retry `catch (DbException) when (attempt < MaxAppendAttempts)` (acceptance names Offline.Data and Authentication.Data only).

**Acceptance results:**

- `grep "catch (DbException"` in `YFex.Offline.Data` / `YFex.Authentication.Data`: 4 sites, each with a `ClassifyError` filter (`SqlReconciliationStores.cs` review retry; `SqlStores.cs` account create, account update, credential save via `IsDuplicate`). No unfiltered catch.
- Forced non-duplicate failure: tables dropped → every insert path throws `SqliteException` (`Other`), in YFex.Data, Offline and Authentication tests.
- Upsert paths: every update-then-insert is a `BuildUpsert` statement; every "insert, any `DbException` = exists" is `BuildInsertIfAbsent`.
- Round trips on SQLite, BenchmarkDotNet 0.15.8 ShortRun, in-process, Intel Core Ultra 7 258V, per store call (200 per invocation). Before = DS-P5 code (KB as reported, ≈ bytes), after = final code:

  | Store call | Before mean | Before allocated | After mean | After allocated |
  |---|---:|---:|---:|---:|
  | `SaveReceiptAsync`, new key (was update 0 rows + insert) | 29.4 µs | 8.88 KB (≈ 9,090 B) | 21.5 µs | 5,064 B |
  | `SaveReceiptAsync`, existing key | 12.2 µs | 5.32 KB (≈ 5,450 B) | 13.4 µs | 5,064 B |
  | `SetSequenceAsync`, existing | 8.4 µs | 3.43 KB (≈ 3,510 B) | 8.7 µs | 3,008 B |
  | `ClaimAsync`, new | 9.7 µs | 3.08 KB (≈ 3,150 B) | 10.1 µs | 3,008 B |
  | `ClaimAsync`, held by another key (was a caught `DbException` + select) | 38.4 µs | 12.82 KB (≈ 13,130 B) | 14.1 µs | 6,168 B |
  | `SqlIdempotencyRecords.ClaimAsync`, completed key (same) | 39.9 µs | 12.22 KB (≈ 12,510 B) | 15.0 µs | 5,680 B |
  | `SaveAsync` site settings, existing | 9.8 µs | 5.95 KB (≈ 6,090 B) | 12.1 µs | 5,632 B |

  **No regression in allocated bytes** (every call allocates less). Time: duplicate paths 2.6× faster, a new receipt 1.4×; the two "upsert of an existing row" cases are slower on SQLite (`ON CONFLICT … DO UPDATE` vs a plain `UPDATE`: +1.2 µs and +2.3 µs; ShortRun error bars are wide). Logs: `ds-p6-bench-before.log` / `ds-p6-bench-after.log` in the session notes folder.
- `data.slnx` Release: 0 errors; warnings NU1507/NU1603 only; **no CS/RS/IL warnings**.
- Tests (Release): YFex.Data.Tests **331 passed + 3 skipped** (303 + 28), EF **29**, Tenancy **13**, DataMining **2**; YFex.Offline.Tests **97** (94 + 3), YFex.Authentication.Tests **212** (211 + 1), YFex.Rules.Data.Tests **19**, YFex.Web.Data.Tests **51**. All passed.
- Native AOT: `dotnet publish tests/YFex.Data.AotSmoke -c Release -r win-x64` → **18/18 checks, exit 0**; IL warnings only from MemoryPack (14, as at DS-P5). Offline and Authentication smoke apps (`-p:SmokeData=true`, both now running their stores through the new statements): `OK`, exit 0, IL warnings only from MemoryPack.

**Not verified / not done:**

- **No live PostgreSQL, SQL Server or Oracle server**: their `BuildInsertIfAbsent` / `BuildUpsert` text is golden-tested only, and their `ClassifyError` is tested only for PostgreSQL (constructed `PostgresException` / `NpgsqlException`); `SqlException` and `OracleException` have no public constructors, so their number tables are unexercised. Not run: Oracle `MERGE` binding a `BLOB`/`RAW` parameter in the `DUAL` select list (site settings, sessions, publications), SQL Server key-range locking under real concurrency, PostgreSQL `ON CONFLICT` with `jsonb`/`bytea` parameters.
- **Untyped NULL parameters on SQL Server** (pre-existing, not changed): `QueryParameterBuilder.Add(name, null)` binds `DBNull` as `nvarchar`, which SQL Server refuses to convert to `varbinary` (e.g. a null `site_verifier`, `sealed_successor`, idempotency `result`). Worth a typed-null path in `BindParameter`.
- **Authentication's `''` on Oracle** was not reviewed column by column (e.g. an empty `display_name` or `issuer` into NOT NULL text); only Offline's store review was in DS-P6.
- Rules.Data's blanket retry catch (see deviations).

## DS-P5 implementation record (2026-10-27)

**Built** (uncommitted, on top of the uncommitted DS-P0–P4 work):

| Piece | Where |
|---|---|
| `ProviderDialects` (provider name → `ProviderId` + optional `IQueryDialect`; strings only, so `YFex.Data` never references EF) | `YFex.Data/src/YFex.Data/Providers/ProviderDialects.cs` |
| Module initializers registering `Microsoft.EntityFrameworkCore.Sqlite`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.SqlServer`, `Oracle.EntityFrameworkCore` | `src/YFex.Data.<Engine>/ProviderRegistration.cs` |
| `SchemaAssert.Equivalent(actual, expected)` + `SchemaMismatchException` (order-insensitive: columns incl. defaults/identity, PK, indexes, FKs) | `YFex.Data/src/YFex.Data/Schema/SchemaEquivalence.cs`, `SchemaValidator.cs` |
| E1: `CreateYFexSchemaHistory`, `ApplyYFexModule(module, toVersion, fromVersion)`, `RevertYFexModule(module, fromVersion, toVersion)`, `YFexMigrationOptions` (history table, dialect, store types, applied-by, clock) | `src/YFex.Data.EntityFramework/Schema/YFexMigrationBuilderExtensions.cs`, `EfSchemaOperations.cs`, `EfTarget.cs` |
| E2: `EfSchemaRenderer` (`Script`, `ScriptRevert`, `ScriptHistoryTable`) | `Schema/EfSchemaRenderer.cs` |
| E3: `ToYFexTableSchema(IEntityType \| ITable)`, `context.ToYFexTableSchema<T>()`, `ToYFexSchemaState(IModel \| DbContext)` | `Schema/YFexModelExtensions.cs` |
| E4: `InstallYFexRowSecurity` / `RemoveYFexRowSecurity`; `RowSecurityModelCheck` (+ `RowSecurityModelException`) run by the interceptors once per model and catalog version, before the first command or save | `Schema/YFexMigrationBuilderExtensions.cs`, `RowSecurityModelCheck.cs`, `RowSecurityInterceptors.cs` |
| E5: `UseYFexRowSecurity(rowSecurity)` (and `RowSecurityInterceptors(rowSecurity)`): dialect resolved from `Database.ProviderName` at the first command; explicit-dialect overloads unchanged | `RowSecurityInterceptors.cs` |
| Every public bridge type carries `[RequiresUnreferencedCode]` + `[RequiresDynamicCode]` (incl. the existing row-security types); `<YFexBanLinq>` on the project (its one `FirstOrDefault` became a loop) | `YFex.Data.EntityFramework.csproj` |
| Tests: `EntityFrameworkSchemaTests` (17), `EntityFrameworkModelCheckTests` (4), fixtures, golden `Golden/{sqlite,sqlserver}/ef-module.sql`; `PublicApiTests.No_assembly_references_entity_framework` (×5 assemblies) | `tests/YFex.Data.EntityFramework.Tests/`, `tests/YFex.Data.Tests/PublicApiTests.cs` |
| Docs: `SCHEMA.md` §10 "Apps on Entity Framework Core migrations" (§10 → §11 Adding an engine, + step 4), `ARCHITECTURE.md` project map, `README.md` | YFex.Data |

**Deviations from 05 / the brief:**

- **The registry is `ProviderDialects` in `YFex.Data`**, not `EfProviderDialects` in the bridge: the engine packages register from
  their module initializers and must not reference the (non-AOT) bridge. It holds names only.
- **Registration is guarded by `RuntimeFeature.IsDynamicCodeSupported`.** Found by the AOT smoke: an unguarded initializer roots
  `OracleDialect.Instance`, and with it the Oracle driver (40+ new IL warnings from `Oracle.ManagedDataAccess`) in every Native AOT app
  that only uses the Oracle *generator*. ILC folds the flag to false, so nothing is rooted; EF cannot run under Native AOT anyway, so
  `UseYFexRowSecurity(rowSecurity)` without a dialect is a JIT-only convenience (the explicit-dialect overload still works).
- **Module initializers run only when their assembly loads.** An EF app may never touch `PostgresDialect` before its first command, so
  the bridge loads the YFex engine assembly of the four known EF providers **by name** (`Assembly.Load` + `RunModuleConstructor`, no
  scanning) when the registry misses. MySQL's two EF providers map to `ProviderId.MySql` without a dialect.
- **`toVersion` is required** in `ApplyYFexModule` (05's sketch defaulted it): a frozen migration must not pick up versions added to the
  module later. A later migration passes `fromVersion`. Version arguments are `SchemaVersionNumber` (09 §1.2), not `int`.
- **History row = `InsertDataOperation` with store types.** EF 11 refuses data operations on a table no entity maps unless
  `ColumnTypes` are given (probed: "not associated with a model"; an initialized model without the entity also fails). In migrations the
  types come from the engine's YFex generator (`TypeName`), or `YFexMigrationOptions.StoreTypes` / `Dialect` for other engines
  (clear `NotSupportedException` otherwise); `EfSchemaRenderer` uses the EF provider's own `IRelationalTypeMappingSource`.
  `applied_by` defaults to `entity-framework`; `applied_at` is the time the operations are built (migration run or script time);
  `duration_ms` 0; `statements` = EF operations of the version.
- **Names are folded** as the engine folds unquoted identifiers (PostgreSQL lower, Oracle upper; others as written), because EF quotes
  every name and the YFex generators and stores write them unquoted. Not in 05.
- **SQL Server unique indexes:** without a model EF's SQL Server generator filters a unique index on *every* column; the bridge always
  sets `Filter` itself (nullable columns only, as the YFex generator) and `""` when none is nullable, which suppresses EF's filter (probed).
- **SQLite drop column** is rendered by the YFex SQLite generator as a `SqlOperation` (`alter table … drop column`): EF's SQLite
  provider throws `NotSupportedException` for `DropColumnOperation` without a model (found by the revert test).
- **Identity through EF on SQLite (`Sqlite:Autoincrement`) and SQL Server (`SqlServer:Identity`) only.** PostgreSQL/Oracle/MySQL take an
  enum of their provider assembly; the bridge throws a clear `NotSupportedException` naming the column (use `ApplySchemaAsync`).
- **Columns of kind `Json`** become `string` columns (no portable EF JSON column for raw operations), so on PostgreSQL EF creates `text`, not `jsonb`.
- **`RevertYFexModule` / `ScriptRevert`** were in 05's sketch only: drops are recreated from the state before them (empty), a `SqlStep`
  has no inverse (`NotSupportedException` naming the step).
- **E3 reads EF's relational model** (`ITable`), so table splitting, TPH and owned types come out as the database sees them. EF's
  read-optimized runtime model (`context.Model`) does not keep index sort order (`IsDescending` throws), so the `DbContext` overloads read
  `IDesignTimeModel`; the `IEntityType` overload fails with a pointer to it when the model has an index. Decimal without precision →
  `Decimal(18,2)` (EF's default); alternate keys → unique indexes; `HasDefaultValueSql` and index filters are not exported; computed
  columns, JSON-mapped owned types and unmapped CLR types (e.g. `TimeSpan`) throw naming the property. Column order is EF's (not
  meaningful), hence `SchemaAssert.Equivalent`.
- **Catalog check scope:** policies for tables the model does not map are skipped (they may be YFex.Data stores' tables), unless the
  policy's `EntityType` is mapped elsewhere. DbType conversion is by kind (numeric, text, Guid↔text, enum↔numeric/text, boolean,
  temporal, binary).
- **Test-only `VersionOverride`:** the EF SQL Server provider (11.0.0-preview.4, added to `Directory.Packages.props`) needs
  `Microsoft.Data.SqlClient` ≥ 7.0.0, so the EF test project overrides the repo pin (5.2.2). No library changed its SqlClient version.

**Acceptance results:**

- 05 §7 tests (EF tests **29**, 8 before + 21): `ApplyYFexModule` through a real `Database.MigrateAsync()` on SQLite (hand-written
  migration, no target model): the E3 export of the mapping context is `Equivalent` to `module.Current` for both tables, `pragma
  table_info/index_list` show the columns, nullability, key and indexes, identity became `AUTOINCREMENT`; history rows carry the module's
  checksums, `GetSchemaStatusAsync` is up to date, a later `ApplySchemaAsync` applies 0 versions, and a v3 added afterwards applies from v2.
  Unique index lets two `(acme, NULL)` rows in and rejects a second `(acme, EU)`. `Migrate("0")` reverts tables and history rows.
  `EfSchemaRenderer` golden files for **SQLite and SQL Server** (the SQL Server EF provider restored from nuget.org). E3 export of the
  existing `Invoice` entity; unmapped type names `Timing.Elapsed`. Catalog check: misspelt `TENANTID` fails at the first query and before
  `SaveChanges` (`RowSecurityModelException` listing the real columns); wrong `DbType.Guid` reported; dialect inferred from the provider.
  Operation-level checks (no server/provider package): Oracle names upper-case + function-based index identical to the YFex Oracle
  generator + Oracle history store types; PostgreSQL lower-case + provider-picked `SqlStep`; identity refused on PostgreSQL; invalid module
  (Oracle reserved word) fails before any operation; unknown provider needs `StoreTypes`; row-security install/remove equal
  `PostgresDialect.ScriptRowSecurityPolicies/Removal`, SQLite refused.
- `YFex.Data` and the four engine assemblies reference no `Microsoft.EntityFrameworkCore*` / bridge assembly (`PublicApiTests`).
- `data.slnx` Release: 0 errors; warnings NU1507/NU1603 only; **no CS/RS/IL warnings**.
- Tests (Release): YFex.Data.Tests **303 passed + 3 skipped** (298 + the 5-case theory), EF **29**, Tenancy **13**, DataMining **2**.
- Native AOT: `dotnet publish tests/YFex.Data.AotSmoke -c Release -r win-x64` → **14/14 checks, exit 0**; trim/AOT warnings only from
  MemoryPack (`MemoryPackFormatterProvider`, 14), none from YFex code or the drivers.
- **Not verified:** ApplyYFexModule / the renderer against real PostgreSQL, SQL Server, Oracle or MySQL servers or their EF providers
  (Npgsql/Oracle EF providers were not added; SQL Server only renders golden text); identity through Npgsql/Oracle EF; EF compiled
  models (no design-time model for E3); consumer repos (Offline, Authentication, Rules.Data) were not rebuilt: nothing they use changed
  except the new module initializers in the engine packages.

## DS-P4 implementation record (2026-10-27)

**Built** (uncommitted, on top of the uncommitted DS-P0–P3 work):

| Piece | Where |
|---|---|
| Attributes `[DataTable]` (name, `Schema`, `PrimaryKeyName`), `[DataIndex]` (name, members, `Unique`), `[Ascii]`, `[Precision]` | `YFex.Data/src/YFex.Data/Schema/DataTableAttributes.cs` |
| `DataTableGenerator` (`ForAttributeWithMetadataName`, equatable `TableModel`), `DataTableParser` (shared, no LINQ), `DataTableEmitter`, `DataTableAnalyzer` (YFSCH001/002, YFSCH101–111), release tracking | `YFex.Data/src/YFex.Data.Generator/DataTable*.cs` |
| Emitted per row type: `Columns` (`SqlIdentifier`s), `Schema`, `BulkTable`, `BulkMapper : IBulkRowMapper<T>` (nested struct) | generator output |
| `DataRowParser`: a `[DataTable]` row's reader and `MergeAsync` shape use its snake_case column names and table name | `DataRowParser.ColumnOf`, `TableName` |
| `SchemaAssert.InSync(module, tables)`, `SchemaOutOfSyncException` (`MissingSteps`, `Unsupported`), printer `SchemaSync` | `YFex.Data/src/YFex.Data/Schema/SchemaSync.cs`, `SchemaValidator.cs` |
| Tests: `DataTableGeneratorTests` (driver: naming, EF attributes by name, 13 diagnostics × `#pragma`, incrementality, printed versions compiled with Roslyn and re-checked), `DataTableRowTests` (rows compiled into the test project: structural equality, same DDL on 4 engines, bulk write + read back on SQLite, `InSync`) | `tests/YFex.Data.Tests/Schema/` |
| AOT smoke: `LedgerRow` in sync with the smoke module, bulk-written by its mapper, read back by its reader | `tests/YFex.Data.AotSmoke/Program.cs` |
| Pilot: `RuleDataRow` (internal) in YFex.Rules.Data; the project references the generator as an analyzer. `RuleStoreSchema` v1 unchanged (checksum pin still passes); test `The_row_class_describes_the_table_the_module_creates` | `YFex.Rules/src/YFex.Rules.Data/RuleDataRow.cs` |
| User guide §4 "Row classes" | `YFex.Data/docs/SCHEMA.md` |

**Deviations from 08 §6 / 09:**

- **`[StringLength]`, not `[MaxLength]`, in AOT projects.** .NET marks the `MaxLengthAttribute(int)` constructor
  `[RequiresUnreferencedCode]`, so applying it raises IL2026 at the use site (found by the AOT smoke). `StringLength`, `Key`,
  `Required`, `Column`, `NotMapped`, `DatabaseGenerated`, `Table` are clean (checked with a probe project). The generator reads
  both; `[StringLength]` is also accepted on `byte[]`. The pilot and the smoke use `[StringLength]`; 08 §6.1's sketch was updated.
- **Generated `Columns` call `SqlIdentifier.Parse` once per type** (09 §1.2 said "never parses at runtime"). A public unchecked
  factory would let any caller put unvalidated text into DDL; the parse is a `SearchValues` scan at type initialization and the
  generator has already validated the name, so it cannot throw.
- **Compile-time codes YFSCH101–111** were added for rules only the generator has (partial, nested/generic, unmapped type,
  precision, `TypeName`, nullable key, non-portable name, unknown member, duplicate, attribute not applicable, no column).
  YFSCH001/002 keep their runtime meaning (001 at 63 characters, the PostgreSQL limit).
- **The mapper is a nested `BulkMapper` struct**, not an interface on the row, so the row's interface list is untouched.
- **Columns** are public instance properties *with a setter* (`set`/`init`, positional members) and public fields; get-only
  properties are skipped (computed values). `[DatabaseGenerated(Computed)]` members are skipped with YFSCH110.
- **`InSync` does not compare** defaults (a row class does not declare them), column order (`AddColumn` appends) or the module's
  foreign keys (a row class does not describe them). Changed column type/nullability and changed primary keys have no
  portable step: they go to `Unsupported`, not to `MissingSteps`.
- `[DataIndex]` has no descending columns (EF's `IsDescending`/`AllDescending` are honoured). `byte`/`float` members are not
  mapped (YFSCH103); byte-backed enums map to Int16.

**Acceptance results:**

- Driver tests: every supported CLR type → `ColumnType` (incl. enum, `Nullable<T>`, `byte[]`, `ReadOnlyMemory<byte>`, identity,
  `[Required]` over `string?`, public field); `[Column]` and snake_case (`HTTPCode` → `http_code`, `StreamHash64` →
  `stream_hash64`); each diagnostic fires on a source location and disappears under `#pragma warning disable` (13 cases,
  including the five the acceptance lists); generated code compiles, unusable rows emit nothing.
- Generated `TableSchema` equals the fluent one for the 08 §6.1 table and for the pilot (`RuleDataRow.Schema ==
  RuleStoreSchema.Table()`); the DDL from both is identical on the four engines; the pilot's v1 checksum pin is unchanged.
- `InSync` fails with pasteable code when a property and an index are added; the test pastes it as version 2 and passes. Two
  further tests compile the printed text with Roslyn (a whole `CreateTable`, then v1 + v2 in sequence) and re-run `InSync` on the
  loaded module: pass, and v1's checksum is unchanged by adding v2.
- Incremental: an unrelated edit leaves every generator output `Cached`/`Unchanged` (tracked steps).
- `data.slnx` Release: 0 errors, no CS/RS/IL warnings. Tests: YFex.Data.Tests **298 passed + 3 skipped** (220 before + 78),
  EF 8, Tenancy 13, DataMining 2; YFex.Rules.Data.Tests 19; YFex.Web.Data builds.
- Native AOT smoke: 14/14 checks, exit 0; trim/AOT warnings only from MemoryPack, as before.
- **Not done: the 09 §5.4 benchmark.** No runtime hot path changed: the generated mapper emits the same `BulkRowWriter` call per
  column a hand-written mapper does (compare `TypesRowMapper`), and the reader change only alters which column name a member
  matches. `tests/YFex.Data.Benchmarks` still does not exist; measure there when a store moves its DML to row classes (⚑DS11).

## DS-P3 implementation record (2026-10-23)

**User decision (2026-10-23): nothing is in production.** Each library has a single **version 1** = today's tables, corrected
in place (no extra version, no compatibility path, no baseline for databases created by the old `Statements`).

**Built** (uncommitted, on top of the uncommitted DS-P0/P1/P2 work in YFex.Data; **YFex.Data itself unchanged**):

| Repo | Change |
|---|---|
| YFex.Offline | `OfflineSqlSchema` = 12 `TableSchema` properties + `Module` (`yfex.offline`, v1 "Central stores (offline 11 Phase 3e)") and the constants `NameLength` (200), `BundleKindLength` (100); `SqlEngine` and `Statements` deleted. `AddYFexOfflineData` calls `AddYFexSchemaModule(OfflineSqlSchema.Module)`; its doc comment describes `ApplySchemaAsync` / `ScriptSchema` / `AddYFexSchemaMigration`. Stores: `session` → `session_data` in the two statements that name it; new `OfflineSqlStore.Bounded` guard on every write of a narrowed key column (`ClaimAsync`, `SpendAsync`, `PublishAsync`, `RecordAsync`). Tests: `OfflineTestDatabase` applies the module; benchmark `FreshCentral` applies it; AOT smoke (`-p:SmokeData=true`) scripts it, applies it on shared-cache in-memory SQLite, re-applies (no-op), reads status and round-trips a receipt and a claim (references `YFex.Data.Sqlite` in that mode). README, csproj description |
| YFex.Authentication | `AuthenticationSqlSchema` = 9 tables + `Module` (`yfex.authentication`); `SqlEngine` and `Statements` deleted. Tables unchanged from the DS-P1 fixture (already valid on all four engines). `AddYFexAuthenticationData` registers the module (doc comment as Offline). Tests: both SQLite fixtures apply the module; `Every_engine_gets_the_external_tables` is one fact over the four dialects' `ScriptSchema` (was a 3-engine theory). AOT smoke: scripts, applies, re-applies, status, a `SqlScopedProviderStore` read, and the registry entry. README, csproj description |
| YFex (Rules.Data) | New `RuleStoreSchema`: `Module` (`yfex.rules`, table `yfex_rule_data`), `ForTable(table)` (module `yfex.rules.<table>`, same instance per table), `Table(table)`. `RuleStoreDatabase`, `SqlRuleStoreOptions.Database` and `CreateTableScript` deleted. `SqlRuleStore.Schema` (its module) and `EnsureSchemaAsync` = `ApplySchemaAsync(Schema)` (was an engine-specific existence probe + DDL). Tests: the SQLite store fixture applies `RuleStoreSchema.Module` |

**Version-1 corrections, with reasons:**

| Table / column | Was | v1 | Reason |
|---|---|---|---|
| `offline_sessions.session` | `session` | **`session_data`** | Oracle reserved word (YFSCH004) |
| `offline_claims.name`, `.claimed_value` | `String(450)` | **`String(200)`** | PK 1,800 bytes on SQL Server > 900 clustered limit (YFSCH003); now 800 |
| `offline_spending.stream`, `.subject` | `String(450)` | **`String(200)`** | PK ≈ 1,844 bytes; now 844 |
| `offline_publications.kind` / `.stream` | `String(400)` / `String(450)` | **`String(100)`** / **`String(200)`** | PK ≈ 1,708 bytes; now 608 |
| `offline_deliveries.kind` / `.stream` | `String(400)` / `String(450)` | **`String(100)`** / **`String(200)`** | PK ≈ 1,752 bytes; now 652 |

`kind` (a `BundleKind.Name`) stays Unicode `String`, not `AsciiString`: bundle kinds are application-extensible and nothing
restricts them to ASCII, and SQL Server `varchar` would silently code-page a non-ASCII name. Single-column text keys
(`offline_review_items.id`/`group_key`, `offline_site_settings.site`, `offline_idempotency.idempotency_key`, `offline_findings.item_id`)
keep `String(450)` (900 bytes: within the limit). `ix_offline_idempotency_completed` (32 characters) only matters on Oracle < 12.2
(`LegacyIdentifierLength`, off by default), the same as Authentication's long index names.

**Input validation for the narrowed columns** (the brief asked to check): needed. SQLite does not enforce lengths (`text`), and on the
server engines an over-long value makes the insert fail, which `TryInsertAsync` (catch-all `DbException`, DS-P6) reads as "already
exists": a claim would end in "could not be written or read", a publication in "other writers kept winning", and a delivery receipt would be
dropped silently. `OfflineSqlStore.Bounded(value, max)` throws `ArgumentException` (names the argument and the width) before any SQL, so
every engine behaves the same. Reads (`GetSpendingAsync`, `GetLatestAsync`, …) are not guarded: a longer value simply finds nothing.
Nothing in Offline's domain bounds claim values, allowance subjects or stream names today; 200 characters is the store's contract now
(`OfflineSqlSchema.NameLength`).

**Deviations from 06 / the brief:**

- **Rules.Data has no `AddYFex…Data` extension** (apps construct `SqlRuleStore` themselves, validation 13 §4b), so nothing registers
  `RuleStoreSchema.Module` automatically; the type's doc comment tells hosts to call `AddYFexSchemaModule`. `EnsureSchemaAsync` is kept
  as the store's convenience entry point, now through the migrator (it records `yfex_schema_history` and takes the module lock).
- **Rules.Data module naming:** 06 §3 sketched `RuleStoreSchema.Module(string table)` named `yfex.rules:{table}`. A property and a method
  cannot share the name `Module`, and `SchemaModuleName` allows neither `:` nor `_`, so it is `Module` (default table, `yfex.rules`) plus
  `ForTable(table)` → `yfex.rules.<table lower-case, '_' as '-'>` (injective: `-` cannot occur in a SQL identifier). Instances are cached
  per name, because `SchemaRegistry` rejects a second instance under the same name.
- **`YFex.Rules.TableSchema`** (rule tables) shadows `YFex.Data.TableSchema` inside `namespace YFex.Rules.Data`; `RuleStoreSchema`
  uses the alias `SchemaTable`.
- **No store builds a `BulkTable` or column list by hand** in a way `Module.Current` would replace (all DML), so the optional
  `ToBulkTable()` change was not made.
- **Test projects reference `YFex.Data.Postgres/SqlServer/Oracle`** for `SchemaAssert.Valid` (generators only; no server is contacted).
  The libraries still reference only `YFex.Data`.
- **AOT smoke apps** reference `YFex.Data.Sqlite` only in their `SmokeData=true` mode (as before, that mode tolerates MemoryPack's
  third-party warnings).
- YFex.Data's DS-P1 test fixtures (`SchemaFixtures.Offline`) still model the **pre-correction** Offline tables on purpose: they are
  the evidence for the validator findings (YFSCH003/004) and their golden files. The real definitions now live in the libraries.
- Historical phase logs (offline 11 Phase 3e, validation 13 §4b) still name `Statements(SqlEngine)` / `CreateTableScript`; they record
  what was built then and were not rewritten.

**Acceptance results:**

- `grep -rlE "\b(SqlEngine|RuleStoreDatabase)\b" --include=*.cs C:\Dev\YFex` (excluding `obj`/`bin`, `_Backup`, `_ignore`, worktrees):
  **no file**.
- Every test database is created with `ApplySchemaAsync` on SQLite. New tests per library: `SchemaAssert.Valid(Module, SqliteDialect,
  PostgresDialect, SqlServerDialect, OracleDialect)` **passes for all three modules** (Rules.Data also for `ForTable("tenant_rules")`),
  pinned v1 checksum, re-apply = 0 versions + up to date, registry entry. Pinned checksums: `yfex.offline` v1
  `161191e6a1abfcf97926cae885a23f773fb436a86ef81b897fd30ffa347c7ccc`, `yfex.authentication` v1
  `4b6ecaf78d62b52ccd606f71c9c97344aa70516a235fb314d7f973a46c0470f1`, `yfex.rules` v1
  `a3ca2e6acb504360a512b532f1af72cba3f5999c56eba511f7b2b434724dc716`.
- Offline adds a width test (claim/stream/kind one character too wide → `ArgumentException`, full width accepted). Rules.Data adds a
  cross-check: the generated Oracle DDL has exactly the hand-written types (`varchar2(n char)`, `number(5)`, `number(19)`, `blob`),
  `constraint pk_yfex_rule_data primary key (rule_set, version)` and the stream index; SQL Server `nvarchar(2000)`, `varbinary(max)`.
  It also adds a custom-table store that applies, appends and reads on its own module while the default module stays pending.
- Tests (Release): YFex.Rules.Data.Tests **18** (15 before: −1 DDL test, +4), YFex.Web.Data.Tests **51**, YFex.Offline.Tests **94**
  (91: −1, +4), YFex.Authentication.Tests **211** (210: the 3-case theory became 1 fact, +3), all passed. YFex.Offline.Benchmarks builds.
- Native AOT (`-c Release -r win-x64 -p:SmokeData=true`): Offline smoke `OK`, exit 0; Authentication smoke `OK`, exit 0. IL warnings in
  both only from MemoryPack (`MemoryPackFormatterProvider`, 13 each), **none from YFex code**.
- `data.slnx` Release: 0 errors; warnings NU1507/NU1603 and the pre-existing audit NU1902/NU1903 only. `offline.slnx` as a whole maps
  `YFex.Privacy.Generator` / `YFex.Validation.Generator` to Debug, whose DLLs RoselYn.MCP keeps locked (MSB3027): the Offline projects
  were built directly in Release instead (0 errors, NuGet warnings only).
- **Not run on real PostgreSQL / SQL Server / Oracle servers** (no `YFEX_TEST_*` server): the same open item as DS-P1/DS-P2.

**Follow-ups (not DS-P3):** Oracle stores `""` as NULL, and `SqlOfflineReviewQueue` writes `actor ?? ""` / `detail ?? ""` into NOT NULL
columns (06 §1 store review item). Bounded non-key columns (`String(400)` such as `scope`, `kind`, `actor`) are enforced by PostgreSQL
since ⚑DS3 and by SQL Server/Oracle, but not by SQLite or the stores. Both belong with DS-P6 (`ClassifyError`, store review).

## DS-P2 implementation record (2026-10-23)

**Built** (`C:\Dev\YFex\YFex.Data`, uncommitted on top of DS-P0/DS-P1):

| Piece | Where |
|---|---|
| Value types `SchemaModuleName` (lower-case dotted, ≤ 100) and `SchemaVersionNumber` (≥ 1, `Next()`, ordering, `TryFrom`), shaped like `SqlIdentifier` (`ISpanParsable`, `ICanonicalValue`, span/UTF-8 formatting) | `src/YFex.Data/Schema/SchemaModuleName.cs`, `SchemaVersionNumber.cs` |
| Canonical encoding of every step and model type (`TableSchema`, `ColumnSchema`, `IndexSchema`, `PrimaryKeySchema`, `ForeignKeySchema`, `SqlStep`) into `ArrayPoolBufferWriter<byte>`; `SHA256.HashData` → `Hash256` | `Schema/SchemaCanonical.cs` (internal `SchemaCanonicalWriter`), `WriteCanonical` on the model types |
| `SchemaModule` (immutable; `Version(n, description, v => …)` returns a new module; contiguous from 1; `Current`, `StateAfter(version)`), `SchemaVersion` (number, description ≤ 400, steps, `Checksum`), `SchemaStepBuilder` | `Schema/SchemaModule.cs` |
| `SchemaValidator.Validate(module, generator \| dialect)` (diagnostics carry `SchemaDiagnostic.Version`), `SchemaAssert.Valid(module, dialects…)` | `Schema/SchemaValidator.cs`, `SchemaDiagnostic.cs` |
| History table model (`SchemaHistory.Table(name)`, default `yfex_schema_history`, checksum `Binary(32)`, `state` 0/1) | `Schema/SchemaHistory.cs` |
| Generator migrator support: `CreateTableIfNotExists`, `TableExists` (read-only status probe), `MigrationLockScope` (`Unit`/`Session`), `AcquireMigrationLock` / `ReleaseMigrationLock`; per engine as 03 §4 | `SchemaSqlGenerator` + the four engine generators |
| `SchemaInstaller`: `ScriptSchema(dialect \| generator, module, from, to)`, `ApplySchemaAsync(connection, module \| SchemaModule[] \| ReadOnlyMemory<SchemaModule>, options)`, `GetSchemaStatusAsync`, `MarkVersionResolvedAsync`; `SchemaApplyOptions`, `SchemaApplyResult`/`SchemaModuleResult`, `SchemaModuleStatus`/`SchemaVersionStatus`/`SchemaVersionState` | `Schema/SchemaInstaller.cs`, `SchemaApplyTypes.cs` |
| `SchemaMigrationException`, `SchemaDriftException` (expected/recorded `Hash256`), `SchemaPartiallyAppliedException` | `Schema/SchemaMigrationExceptions.cs` |
| `SchemaRegistry`, `AddYFexSchemaModule`, `AddYFexSchemaMigration(o => o.ApplyOnStartup = …)` (off by default, ⚑DS7) + internal hosted service | `Schema/SchemaRegistry.cs` |
| `YFexConnection.KeepOpen()` (internal): `CloseAsync` (and the end of `StartScopedTransaction`) leaves the connection open while held | `Connection/YFexConnection.cs` |
| **Deleted** (⚑DS1, no shim): `ITable`, `ITableCreator<T>`, `IMultiTableCreator<T>`, `TableCreator<T>`, `VersioningTableCreator<T>`, `TablesProcessor<T>`, `TableVersionRepository`, `VersionTable`, `TableAlreadyExistsException` | `Schema/ITable.cs`, `TableCreator.cs`, `TableVersionRepository.cs`, `VersionTable.cs`; `Exceptions/DataExceptions.cs` |
| Tests: `SchemaModuleTests` (value types, contiguity, replay, checksum rules + pinned values + hand-derived bytes, module validation, script ranges), `SchemaMigrationTests` (SQLite live), `SchemaRegistryTests` (DI, hosted service), golden `migrator.sql` + `module.sql` × 4 engines; `DataAccessTests` TableVersionRepository case rewritten on `ApplySchemaAsync` | `tests/YFex.Data.Tests/Schema/`, `DataAccessTests.cs` |
| AOT smoke: builds a two-version module, validates and scripts it for four engines, applies it on SQLite, re-applies (no-op), reads status | `tests/YFex.Data.AotSmoke/Program.cs` |
| Docs: `docs/SCHEMA.md` (user guide, new); `README.md`, `docs/ARCHITECTURE.md` (project map, extension points), a note in `docs/DATA-ACCESS-AND-BULK-WRITE.md` | YFex.Data |

**How apply works** (04 §3–§4 as built): validate every module for the engine (plus the history table itself) → bootstrap
history → per module: session lock (PostgreSQL, SQL Server) → read history; refuse drift, state 1, versions the code does
not know, and gaps → render every pending version (rendering failures stop before execution) → each version as one unit.
Transactional engines: `StartScopedTransaction` { unit lock; re-read the version, skip it if another migrator recorded it
(after checking its checksum); steps; history row }. Non-transactional: claim { unit lock; re-read; insert state 1 } → each
statement on its own → state 0, duration, statement count; a failure writes `statement i of n failed (step k: …): <sql>`
(≤ 400) into `description` and leaves state 1. History/DDL statements go straight to `DataCommandRunner` (no row-security
rewriting) under `RowSecurityContext.Bypass()`.

**Deviations from 02–04 / the brief:**

- **Lock scope is a generator property** (`SchemaMigrationLockScope.Unit | Session`), not implied: PostgreSQL/SQL Server
  hold a session lock for the module run; SQLite (no statement: Microsoft.Data.Sqlite begins `begin immediate`) and Oracle
  (`lock table … in exclusive mode`) lock per unit, released by the commit. Lock methods take `(SchemaModuleName, SqlTableName historyTable)`
  (Oracle locks the history table) and return `SqlQuery[]`.
- **Each unit re-reads its version under the lock** and skips it when recorded meanwhile. That is what makes two concurrent
  migrators safe on SQLite, where the lock is per transaction (both read "pending" before either locks).
- **SQL Server lock** raises an error when `sp_getapplock` returns < 0 (`raiserror`), so a deadlock-victim/cancelled lock
  never continues unlocked; `@LockTimeout = -1` (wait).
- **New `TableExists(SqlTableName)` per engine** so `GetSchemaStatusAsync` is read-only (never creates the history table).
- **`CreateTableIfNotExists` refuses a table with indexes** (one statement by contract); the history table has none.
- **`MarkVersionResolvedAsync(connection, module, version)`** upserts the row to state 0 with the code's checksum and
  description. Besides unblocking a partial apply it records a hand-run `ScriptSchema` output (DBA path: `ScriptSchema` has
  step DDL only) and accepts a deliberate checksum change. Takes the module (not just its name) to know the checksum.
- **The description is not part of the checksum** (steps only, as 02 §4 says); identifiers hash lower-cased, so a case-only
  rename is not drift. Pinned values in `SchemaModuleTests.Checksum_encoding_is_pinned`.
- **A database ahead of the code** (recorded version the code lacks) and **gaps** (v3 recorded, v2 not) are refused with
  `SchemaMigrationException`; status reports them (`SchemaVersionState.Unknown`, pending).
- **Array overloads:** a collection expression cannot target `ReadOnlyMemory<T>`, so `ApplySchemaAsync` /
  `GetSchemaStatusAsync` also take `SchemaModule[]` (`connection.ApplySchemaAsync([a, b])`) and a single module.
- **`SchemaModule.LatestVersion` is a `SchemaVersionNumber`** (default when empty), not `int` (09 §1.2).
- **`SchemaRegistry.Modules` is `ReadOnlyMemory<SchemaModule>`** (it feeds the async API); copy-on-write under a `Lock`.
  Same instance registered twice = no-op; another module with the same name = `ArgumentException`.
- **`DependsOn` not built** (04 §6 says cross-module foreign keys are not used today); modules apply in registration order.
- **Hosting placement:** in .NET 11 `Microsoft.Extensions.DependencyInjection.Abstractions`, `Hosting.Abstractions`,
  `Logging.Abstractions` and `Options` ship **inside `Microsoft.NETCore.App`**, so `AddYFexSchemaMigration` and its
  `IHostedService` live in YFex.Data with **no new package reference** and no AOT impact (factory lambdas, `TryAddEnumerable`,
  no scanning). The test project references `Microsoft.Extensions.DependencyInjection` (container) with the same
  `VersionOverride` as the Tenancy tests. The CLI option A (`schema script|apply` commands) is not built: that belongs to
  each host.
- **Oracle concurrency:** a second migrator that arrives while the first is mid-version sees the state-1 claim and **refuses**
  (`SchemaPartiallyAppliedException`, message says "or another migrator is applying it right now") instead of waiting.
- **Ambient transaction on PostgreSQL:** when `ApplySchemaAsync` joins an application's scope and a step fails, the unlock
  statement cannot run in the aborted transaction; the advisory lock ends with the session (Npgsql's pool reset runs
  `DISCARD ALL`).

**Acceptance results:**

- SQLite live (`SchemaMigrationTests`, temp file database): apply v1+v2 and history rows; re-apply = only bootstrap + one
  history read; v2 added later applies only v2; edited v1 → `SchemaDriftException` (expected/recorded) and v2 does not run;
  **two concurrent `ApplySchemaAsync`** on separate connections (8 versions; WAL and rollback-journal modes) → every version
  recorded once, applied counts sum to 8, stable over 10 repeated runs; failing step → `SchemaMigrationException` naming
  statement 2 of 2, nothing of the version committed, no history row; validation error → no statement executed; database
  ahead of code refused; custom history table; joining an ambient `StartScopedTransaction` rolls schema and history back
  with it; hand-run script + `MarkVersionResolvedAsync`.
- Non-transactional path (`TransactionalDdl = false` generator on SQLite): failing step leaves the first statement committed,
  history `state = 1` with the failing statement in `description`; the next run throws `SchemaPartiallyAppliedException`;
  `MarkVersionResolvedAsync` restores state 0 + description and the next run is a no-op. Success path ends in state 0.
- `ScriptSchema` output **equals** the statements `ApplySchemaAsync` executed (recorded by a test dialect's
  `PrepareCommand`, history bookkeeping filtered out).
- Golden files `Schema/Golden/{sqlite,postgres,sqlserver,oracle}/migrator.sql` (bootstrap, schema-qualified bootstrap,
  table probe, lock pair) and `module.sql` (two-version script). Existing golden files unchanged.
- `data.slnx` Release: 0 errors; warnings only NU1507/NU1603 and pre-existing NuGet audit NU1902/NU1903 (SQLitePCLRaw,
  System.Security.Cryptography.Pkcs, Snappier, SharpCompress); **no CS/IL/RS warnings**. Tests: YFex.Data.Tests
  **271 passed + 3 skipped** (220 before), YFex.Data.EntityFramework.Tests 8, YFex.Data.Tenancy.Tests 13, YFex.DataMining.Tests 2.
- Native AOT: `dotnet publish tests/YFex.Data.AotSmoke -c Release -r win-x64` → **15/15 checks, exit 0**; trim/AOT warnings
  only from MemoryPack (`MemoryPackFormatterProvider`), none from YFex code.
- Consumers (Release, 0 errors, no code change needed: nothing referenced the deleted types): YFex.Rules.Data.Tests **15**,
  YFex.Web.Data.Tests **51**, YFex.Offline.Tests **91**, YFex.Authentication.Tests **210**, all passed; YFex.Offline.Benchmarks builds.
- **Not run on real PostgreSQL / SQL Server / Oracle servers** (no `YFEX_TEST_*` server): their lock, bootstrap and history
  statements are covered by golden text only, the same open item as DS-P1 and the bulk writers.

## DS-P0 implementation record (2026-10-23)

**Moved** (`C:\Dev\YFex\YFex.Data`, files moved unchanged apart from the `namespace` line; DS-P1 files moved with them):

| New project (namespace = project name) | Contents | References |
|---|---|---|
| `src/YFex.Data.Sqlite` | `SqliteDialect`, `SqliteSchemaSqlGenerator` | `YFex.Data`, **`Microsoft.Data.Sqlite` (new)** |
| `src/YFex.Data.Postgres` | `PostgresDialect`, `PostgresBulkWriter`, `PostgresSchemaSqlGenerator` | `YFex.Data`, `Npgsql` |
| `src/YFex.Data.SqlServer` | `SqlServerDialect`, `SqlServerBulkWriter`, `SqlServerSchemaSqlGenerator` | `YFex.Data`, `Microsoft.Data.SqlClient` |
| `src/YFex.Data.Oracle` | `OracleDialect`, `OracleBulkWriter`, `OracleSchemaSqlGenerator` | `YFex.Data`, `Oracle.ManagedDataAccess.Core` |
| `src/YFex.DataMining.Postgres` | `PostgresProvider`, `PostgresProviderModule` | `YFex.DataMining`, `YFex.Data.Postgres` |
| `src/YFex.DataMining.SqlServer` | `SqlServerProvider`, `SqlServerProviderModule`, `SqlServerInterceptor`, `SqlServerInterceptorModule` | `YFex.DataMining`, `YFex.Data.SqlServer` |
| `src/YFex.DataMining.Oracle` | `OracleProvider`, `OracleProviderModule` | `YFex.DataMining`, `YFex.Data.Oracle` |

The four `YFex.Data.<Engine>` projects set `IsAotCompatible` and `YFexBanLinq`; the three `YFex.DataMining.<Engine>`
projects keep `YFexBanLinq` (as before) without `IsAotCompatible` (DataMining is not AOT-annotated). `YFex.Sqlite`,
`YFex.Postgres`, `YFex.SqlServer`, `YFex.Oracle` are deleted. No SQLite mining provider exists, so there is no
`YFex.DataMining.Sqlite`.

**Updated:** `data.slnx` (new `/Engines/` folder; mining projects under `/Providers/`); `InternalsVisibleTo` in
`src/YFex.Data/AssemblyInfo.cs` renamed to the `YFex.Data.<Engine>` assemblies (`YFex.MySql` → `YFex.Data.MySql`);
tests `YFex.Data.Tests`, `YFex.Data.EntityFramework.Tests`, `YFex.Data.Tenancy.Tests` (references and usings);
`YFex.Data.AotSmoke` now references only `YFex.Data.<Engine>`; the commented-out module line in
`YFex.DataMining.Api/Program.cs` (the CLI/API hosts reference no engine project); docs `README.md`,
`docs/ARCHITECTURE.md`, `docs/PROVIDER-GUIDE.md`, `docs/INTEGRATION-GUIDE.md`, `docs/AI-CONTRIBUTION-GUIDE.md`, Q7 in
`docs/DATA-ACCESS-AND-BULK-WRITE.md`. Consumers (project reference + `using YFex.Data.Sqlite;` only):
`YFex/yfex.slnx`, `YFex.Rules.Data.Tests`, `YFex.Web.Data.Tests`, `YFex.Offline.Tests`, `YFex.Offline.Benchmarks`,
`YFex.Authentication.Tests`. No consumer library (`YFex.Offline.Data`, `YFex.Authentication.Data`, `YFex.Rules.Data`,
`YFex.Web.Data`) referenced an engine project, so none changed.

**Acceptance results:**

- `dotnet list package --include-transitive`: `YFex.Data.Postgres` resolves Npgsql, MemoryPack (YFex.Data's own),
  Vogen, System.Reactive and the ban analyzer, **no `YFex.DataMining`, YamlDotNet or Microsoft.Extensions.AI**. The same
  holds for `YFex.Data.Sqlite/SqlServer/Oracle`, `YFex.Offline.Data`, `YFex.Authentication.Tests`, `YFex.Offline.Tests` and
  the AOT smoke (checked in `project.assets.json`; the same check finds 11 such entries in `YFex.DataMining.Postgres`).
- `data.slnx` Release: 0 errors; warnings only NU1507/NU1603 (environment); no CS/IL/RS warnings. Tests: YFex.Data.Tests
  **220 passed + 3 skipped**, YFex.Data.EntityFramework.Tests 8, YFex.Data.Tenancy.Tests 13, YFex.DataMining.Tests 2.
- Consumers (Release, 0 errors): YFex.Rules.Data.Tests, YFex.Web.Data.Tests, YFex.Offline.Tests, YFex.Offline.Benchmarks,
  YFex.Authentication.Tests. Tests: YFex.Rules.Data.Tests 15, YFex.Web.Data.Tests 51 (30 when 07 was written), YFex.Authentication.Tests 210, YFex.Offline.Tests 91, all passed.
- Native AOT: `dotnet publish tests/YFex.Data.AotSmoke -c Release -r win-x64` → 8/8 checks, exit 0; trim/AOT warnings
  only from MemoryPack (`MemoryPackFormatterProvider`), none from YFex code. The smoke no longer references any DataMining
  project.

**Deviations:**

- Files were moved with plain `mv` (not `git mv`) so the index stays untouched while DS-P1 is uncommitted; git shows the
  old paths deleted and the new projects untracked until the work is committed.
- No type-forwarding shims (pre-release; every in-repo and sibling consumer updated).
- `YFex.Data.Sqlite` references `Microsoft.Data.Sqlite` now, although no code uses it yet (DS-P6 `ClassifyError`).

## DS-P1 implementation record (2026-10-23)

**Built** (`C:\Dev\YFex\YFex.Data`):

| Piece | Where |
|---|---|
| Value types `SqlIdentifier`, `SqlTableName`, `ColumnType`, `ColumnDefault` (own types, `ICanonicalValue`, span formatting) | `src/YFex.Data/Schema/` |
| `ColumnSchema`, `IndexSchema`, `IndexColumn`, `PrimaryKeySchema`, `ForeignKeySchema`, `TableSchema` (+ `ToBulkTable`, replay `With*`), `TableSchemaBuilder` | `Schema/TableSchema.cs`, `TableSchemaBuilder.cs` |
| Closed step set (`CreateTableStep` … `DropForeignKeyStep`, `SqlStep`), `SchemaState` (in-memory replay) | `Schema/SchemaSteps.cs`, `SchemaState.cs` |
| `ISchemaDialect`, `SchemaFeatures`, `SchemaSqlGenerator` (ANSI base), `SqlBuilder` (stack buffer), `SchemaDialects.SchemaGenerator()` | `Schema/` |
| `SqliteSchemaSqlGenerator`, `PostgresSchemaSqlGenerator`, `SqlServerSchemaSqlGenerator`, `OracleSchemaSqlGenerator` (+ `Legacy` for < 12.2); each dialect implements `ISchemaDialect` | engine projects |
| `SchemaValidator` (YFSCH001–009), `SchemaDiagnostic`, `SchemaValidationException`, `SchemaAssert` | `Schema/SchemaValidator.cs`, `SchemaDiagnostic.cs` |
| LINQ ban (⚑DS13): `BannedSymbols.txt`, `YFexBanLinq` opt-in in `Directory.Build.props`, 42 existing sites marked | repo root, 12 files |
| Tests: model, generators, validator, golden files (4 engines × offline/authentication/steps, + foreign keys), live SQLite | `tests/YFex.Data.Tests/Schema/` |
| AOT smoke: builds, validates and scripts a table for the four engines, applies it on SQLite | `tests/YFex.Data.AotSmoke` |

**Deviations from 02–04:**

- `DataType` → **`ColumnType`** (CS0104 ambiguity with `DataAnnotations.DataType` for DS-P4 row classes).
- **`SchemaModule`, versions, checksum, `CreateTableIfNotExists` and lock statements moved to DS-P2**: they only serve the
  migrator. DS-P1 validates and renders tables and steps (`SchemaValidator.Validate(generator, steps, start)`).
- `RowSecurityStep` deferred to DS-P6 together with deriving `RowSecurityKey.SqlType` (03 §6).
- Name uniqueness is one portable rule (unique schema-wide on every engine), so `SchemaFeatures.IndexNamesPerTable` was
  dropped; `MaxPrimaryKeyBytes` (SQL Server clustered 900) is separate from `MaxIndexKeyBytes` (1,700).
- New codes YFSCH008 (step the engine cannot run) and YFSCH009 (NOT NULL column added without default, warning).
- Oracle uses the function-based unique index only for **multi-column** indexes with a nullable column; a single-column
  unique index is plain (Oracle already skips all-NULL keys).
- SQLite identity is the rowid alias written inline (`id integer primary key`), so such tables have no separate
  `constraint pk_…`.

**Acceptance results:**

- Structural comparison of the generated DDL with today's `OfflineSqlSchema.Statements` / `AuthenticationSqlSchema.Statements`
  (21 tables, 16 indexes; columns, types, nullability, keys, index columns, filters): **SQLite 0 differences, SQL Server 0
  differences.** PostgreSQL: 72 differences, all `text` → `varchar(n)` for bounded columns (⚑DS3, approved). Constraint
  names differ by design (`constraint pk_<table>` instead of inline `primary key`).
- Validator findings on today's schemas (re-modelled in `SchemaFixtures`): SQL Server YFSCH003 on `offline_claims` (1,800 bytes),
  `offline_spending` (1,844), `offline_publications` (1,708), `offline_deliveries` (1,752) as predicted in 06 §1, and a **new
  finding: `offline_sessions.session` is an Oracle reserved word** (YFSCH004). Authentication is valid on all four engines
  (Oracle < 12.2 only: identifier length). Both are DS-P3 fixes in YFex.Offline.
- Unique-NULL semantics: live on SQLite (two `(realm, NULL)` rows accepted, duplicate `(realm, '0142')` rejected); golden text
  for SQL Server (filtered) and Oracle (function-based). **Not run on real PostgreSQL/SQL Server/Oracle servers** (no
  `YFEX_TEST_*` server available), the same open item as the bulk writers.
- `data.slnx` Release: 0 errors; warnings only NU1507/NU1603/NU1801 (environment); no CS/IL/RS warnings. Tests: YFex.Data.Tests
  **220 passed + 3 skipped** (145 before + 75 schema), YFex.Data.EntityFramework.Tests 8, YFex.Data.Tenancy.Tests 13,
  YFex.DataMining.Tests 2. Consumers YFex.Rules.Data and YFex.Web.Data build unchanged.
- Native AOT: `dotnet publish tests/YFex.Data.AotSmoke -c Release -r win-x64` → 8/8 checks, exit 0; trim/AOT warnings only from
  MemoryPack (as before), none from YFex code. The engine projects are referenced for their generators; the providers and
  drivers are not reached (DS-P0 will make that structural).

## Risks

| Risk | Mitigation |
|---|---|
| Generators untested on real servers (the same gap the bulk writers have) | env-gated live tests per engine; run them once with real servers before any production use (CI containers for PostgreSQL/SQL Server; Oracle Free container) |
| ⚑DS3 changes PostgreSQL column types relative to today's text | confirm no deployed PostgreSQL database; otherwise version 1 keeps `text` with a `PostgresSchemaOptions.UnboundedStrings` switch |
| Model grows into an ORM | closed step set; anything else is a `SqlStep`; no query generation from the model |
| Consumers' module edits to applied versions | checksum drift error + a unit test per consumer that pins each version's checksum |
