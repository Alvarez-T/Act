# YFex.Audit — Lossless, Tamper-Evident Audit Trail — Design

> **Status:** PROPOSAL (2026-10-01). Nothing implemented. Decisions marked **⚑A#** need user confirmation before coding (the
> list is at the end of this file). No 3rd-party package is added without explicit approval. The design uses only the BCL
> (`SHA256`, `ECDsa` P-256, `AesGcm` through YFex.Privacy, `Guid.CreateVersion7`, `RandomNumberGenerator`).
> **Audience:** the user first, then the AI sessions that will implement it. Read this file, then only the files your phase needs
> (see [12-phases-and-acceptance.md](12-phases-and-acceptance.md)).
> **Normative vs sketch:** type names, member names and signatures in code blocks are **normative** unless the block is marked
> `// (sketch)`. Comments inside code explain *how it works*. Keep them (adapted) as XML docs when implementing.
> **Origin:** user decision **✅ L20** (2026-10-01): audit is **not** part of YFex.Logging. This design supersedes §1–§5 of
> [../logging/07-audit-privacy-compliance.md](../logging/07-audit-privacy-compliance.md) and the "Audit" phase (5) of the logging plan.
> **Parallel designs it aligns with (does not duplicate):** [YFex.Privacy](../privacy/README.md) (sensitivity, pseudonyms, subject
> keys, crypto-shredding, rights, legal holds) and the YFex.Data adapter + `IBulkWriter`
> (`C:\Dev\YFex\YFex.Data\docs\DATA-ACCESS-AND-BULK-WRITE.md`).

---

## Why this exists

The ecosystem has **nine unrelated audit-like seams** and no audit trail (full inventory:
[01-current-state-and-inventory.md](01-current-state-and-inventory.md)):

- `IDecisionAuditSink` (Cqrs), `IRuleAuditSink` (Rules.Admin), `ITenantImpersonationAuditor` (Tenancy), Jobs `IJobStore.AppendAuditAsync`
  + Hub `HubAuditEntry`, Automation `IRunStore`, Compensation `ICommandJournal`, Rpc `ISyncFailureLog`, Orchestration `IDeadLetterSink`,
  Payment `PaymentStatusChanged`. Each one has its own shape, none is tamper-evident, most default to a **no-op**, and two of them
  pass `object` (boxing, no typed capture).
- Nobody can answer **"who changed the price of this product from 10 to 12, when, from which device, approved by whom?"**
- Impersonation is recorded once per HTTP request, but the commands executed during it do not know a **real actor** existed.
  Over RPC (`CallContext`) impersonation does not exist at all.
- Offline POS terminals need a fiscal/financial audit trail **even with diagnostics off**, and the only local durable queue
  (`IDurableQueue<T>`) deletes on dequeue, expires by TTL and runs SQLite with `synchronous=NORMAL`.

Goal: **one library that proves who did what, to which data, under which tenant, with which approval, and whether it was
allowed, that never loses an entry, detects any later modification, keeps entries for as long as the law requires, and still
lets a data subject's personal data disappear.** Developers do almost nothing: they mark an aggregate's commands as audited in the
configuration they already write, put `[AuditChanges]` once on an entity type, and every other fact (actor, tenant, trace,
approvals, rule decisions, field-level before/after) is captured automatically.

## Audit is not logging (✅ L20)

| | YFex.Logging (diagnostics) | YFex.Audit |
|---|---|---|
| Question | What did the system do, and why did it fail? | Who did what to which data, when, under which tenant, was it allowed, who approved it? |
| Readers | developers (LoggY, OTLP) | tenant admins, auditors, fiscal inspection, support, data subjects (inside the **business app**) |
| Loss | allowed under pressure (counted) | **never**: transactional or write-through, ack-based shipping, blocks instead of dropping |
| Sampling | yes (tail-based) | **never** |
| Mutability | deleted by retention | insert-only, **tamper-evident** (hash chains, signed anchors), retention by law |
| Volume | high | ~1000× smaller, so a per-entry durable journal is affordable |
| Content | technical telemetry | business facts and entity before/after diffs |
| Must work without the other | yes | **yes**: an offline POS keeps audit with diagnostics disabled |

**Shared core contracts** (YFex core, so neither library references the other): `TraceContext`/`TraceId`/`OperationId`,
`OperationOutcome`, `OperationDescriptor<T>` and its generated capture, `DataSensitivity` + YFex.Privacy classification. When
YFex.Logging is installed, each audit entry also becomes a **mark** in its trace (`audited #4f1c…`) and each audit view links to the
trace. Neither needs the other.

## The mental model — declare once, scope per operation, seal on the server

```
     DECLARE (compile time)                    CAPTURE (per operation, any node)            SEAL & PROVE (server)
 ┌──────────────────────────────────┐   ┌──────────────────────────────────────┐   ┌─────────────────────────────────────────┐
 │ .Audit(a => …) in the aggregate  │   │ AuditScope (ambient, pooled)         │   │ inbox (insert-only, idempotent by id)   │
 │   configuration  / [Audited]     │──▶│  actor · real actor · tenant · trace │   │   ▼ sequencer (1 writer per chain)      │
 │ [AuditChanges] on entity types   │   │  targets · field before/after        │──▶│ chain per (tenant, retention class, day)│
 │ [Audit] partial methods (manual) │   │  reason · approvals · rule decision  │   │ signed anchors · day seal (Merkle root) │
 │ AuditSchema.verified.txt snapshot│   │  → canonical bytes (encode once)     │   │ verify · gaps · query · export · purge  │
 └──────────────────────────────────┘   │  → same DB transaction  (server)     │   └─────────────────────────────────────────┘
        generators reuse the logging    │  → durable local journal (device)    │        readers: entity history panel,
        operation descriptors + capture │     node chain + device signature    │        audit explorer, auditor export,
                                        └──────────────────────────────────────┘        offline verifier CLI
```

**Principle A — an audit entry is committed with the change it describes, or the action does not happen.** On a server the
entry is inserted **in the same database transaction** as the business change (transactional inbox). Where no shared transaction
exists, the entry is made durable **before** the result returns, and irreversible external actions write a durable **intent**
first (two-phase). Audit is never "best effort after the fact".

**Principle B — capture is declared on types and configurations, never per member.** Commands reuse the operation descriptor and
capture the logging generators already emit. Entities get generated, reflection-free field diffs from `[AuditChanges]` on the
type. Sensitivity comes from the value-object types through YFex.Privacy. A committed `AuditSchema.verified.txt` snapshot shows the
reviewer exactly what is audited and how each field is protected.

**Principle C — encode once, hash the bytes, never the JSON.** Each entry is encoded once into a canonical binary form. Those
bytes are what the node journals, ships, the server stores, the chain hashes and the auditor's offline verifier re-hashes. JSON
columns are projections for querying, checked against the bytes during verification.

**Principle D — nodes record, the server seals.** Devices keep their own chain (per node, signed with the device key) so gaps and
edits in transit are detectable. The legal chain is built **asynchronously by a single sequencer per chain** on the server, so business
transactions never contend on a "previous hash" lock.

**Principle E — erasure destroys keys, retention drops whole chains.** Personal fields are encrypted with YFex.Privacy subject keys
(crypto-shredding keeps chains valid). Chains are partitioned by **retention class**, so purging old entries deletes complete chains
and leaves a signed tombstone instead of breaking continuity.

**Principle F — reading the audit is itself audited.** Every query, reveal and export of audit data produces an access entry.

## Files

| # | File | Contents |
|---|---|---|
| 01 | [current-state-and-inventory](01-current-state-and-inventory.md) | Every audit-like seam in all repos, defects found, keep / adapt / replace per seam, migration plan, alignment changes for the logging and privacy designs |
| 02 | [entry-model](02-entry-model.md) | Vocabulary, `AuditEntry`, actor / real actor / relation, tenant scope, action & category, targets, field changes, outcome, reason, approvals, rule decisions, trace links, core contract (`YFex.Auditing`), canonical encoding v1 |
| 03 | [declarative-and-manual-auditing](03-declarative-and-manual-auditing.md) | CQRS `.Audit()` / `[Audited]`, aggregate-wide defaults, events, access audit of queries, `AuditDescriptor<T>` reusing operation descriptors, Cqrs `AuditStage`, denials, `[Audit]` partial methods, `IAuditTrail` manual API, nested operations |
| 04 | [entity-change-auditing](04-entity-change-auditing.md) | `[AuditChanges]` → generated `IAuditChanges<T>` diffs; YFex.Data (row shapes, before images), optional database trigger mode, YFex.EventSourcing, YFex.Persistence, why State.History is not a source, collections, large values, implicit entries |
| 05 | [write-path-and-durability](05-write-path-and-durability.md) | `AuditScope` lifecycle, transactional inbox, local journal + group commit, two-phase intent/outcome, failure policies, shipping, offline devices, ordering, idempotency, back-pressure, clocks |
| 06 | [tamper-evidence](06-tamper-evidence.md) | Threat model, node chains + device signatures, server chains per (tenant, retention class, day), sequencer, anchors, day seal + Merkle inclusion proofs, verification statuses, gap detection, WORM export, external witness, key management |
| 07 | [storage-and-server](07-storage-and-server.md) | Postgres `audit` schema (insert-only roles, partitions, RLS), SQLite, optional Marten store, YFex.Data integration (`IBulkWriter`, `RowReader<T>`, transactions), server API (CQRS queries/commands), entity history, search, export formats, offline verifier CLI |
| 08 | [privacy-retention-and-access](08-privacy-retention-and-access.md) | Sensitivity treatment for audit via YFex.Privacy, subject keys, crypto-shredding, rights participant, retention classes, legal holds, purge with tombstones, permissions, meta-audit |
| 09 | [ui-and-logging-correlation](09-ui-and-logging-correlation.md) | "Who changed this?", per-entity history panel (Avalonia/Blazor), unsaved local edits overlay, review queue, verification badges, localization, LoggY correlation, behaviour without logging |
| 10 | [testing-pitfalls-and-suggestions](10-testing-pitfalls-and-suggestions.md) | `YFex.Audit.Testing`, snapshot test, tamper/crash harnesses, store contract suite, pitfalls, ecosystem suggestions |
| 11 | [usage-examples](11-usage-examples.md) | ✍ dev writes / ⚙ generated / 👁 result: setup per app type, commands, entities, manual facts, approvals, impersonation, offline POS, fiscal two-phase, history panel, erasure, verification, tests |
| 12 | [phases-and-acceptance](12-phases-and-acceptance.md) | Phases, tasks, acceptance criteria, what each session loads, open questions, implementation log (empty) |

## Projects (⚑A1: new sibling repo `C:\Dev\YFex\YFex.Audit`, `audit.slnx`, like Logging/Privacy/Tenancy/Jobs)

```
YFex/src/YFex/Auditing/            (YFex core, ⚑A2) DECLARATION + PROBE CONTRACT ONLY (~25 small types, 02 §8):
                                    AuditEntryId, AuditAction, AuditCategory, AuditActor(+Kind, ActorRelation), AuditTarget, ChangeKind,
                                    ChangeWriter, IAuditChanges<TSelf>, AuditEntityDescriptor, AuditDescriptor(<T>), AuditDescriptorFor<T>,
                                    AuditScope (ambient), AuditChanges, IAuditTrail, AuditDurability, AuditApproval, AuditDecision,
                                    ActorContext, IAuditActorSource, CanonicalWriter, Hash256, HashChain (⚑A31), attributes
                                    ([Audited], [AuditChanges], [AuditEntity], [NotAudited], [Audit], [AuditName], [AuditCategories])
YFex.Audit/src/
  YFex.Audit                  engine: scope runtime, canonical encoder/decoder, local journal (IKeyValueStore, group commit), node chain
                              + device signatures, durability policies, actor sources, shipper (client side), two-phase intents,
                              adapters for existing seams, AddYFexAudit           (→ YFex core, YFex.Tenancy, YFex.Persistence, YFex.Privacy)
  YFex.Audit.Generator        netstandard2.0: [AuditChanges] differs, [Audit] partial methods, AuditSchema snapshot data, YFA0001–YFA0099;
                              shared emit file linked into the Cqrs, Data and EventSourcing generators (04 §2)
  YFex.Audit.Data             SQL store over YFex.Data: Postgres/SQLite schema + migrations, insert-only roles, transactional inbox writer,
                              IBulkWriter ingest, RowReader<T> read models, optional trigger mode (04 §4)   (→ Audit, YFex.Data)
  YFex.Audit.Server           ingest (RPC messages), sequencer, anchors + day seals, verifier, gap detection, queries (AuditQueries),
                              export + WORM targets, retention/purge jobs, legal holds, IPersonalDataStore participant, meta-audit
                              (→ Audit, Audit.Data, Cqrs, Messaging.Rpc, YFex.Jobs, YFex.Authorization, YFex.Privacy.Rights)
  YFex.Audit.Client           RPC shipping transport, local (offline) query, EntityHistoryViewModel + review queue VM (→ Audit, Messaging.Rpc, YFex.Mvvm)
  YFex.Audit.EventSourcing    optional: Marten sealed-stream store (⚑A19), ES aggregate change source helpers   (→ Audit, YFex.EventSourcing)
  YFex.Audit.Testing          AuditCapture + assertions, AuditSchemaSnapshot.Verify, tamper/crash harnesses, store contract suite, fake clock/keys
  tools/YFex.Audit.Cli        `yfex-audit verify | inspect | prove` over an export package, no database, no app assemblies (⚑A30)
  samples/YFex.Audit.AotSmoke · samples/YFex.Audit.Server.Host · benchmarks/YFex.Audit.Benchmarks · tests/*
YFex.UI (Avalonia first, Blazor by conversion): YEntityHistory control (09 §3), UI phase last
```

Thin integrations live **inside the owning libraries** and reference **only YFex core** (`YFex.Auditing` + `YFex.Diagnostics`):
the Cqrs `AuditStage` and generator parsing of `.Audit()`, the Rules decision/approval facts, the Messaging.Rpc actor source and
sync-failure facts, the YFex.Data stage-2 write hooks, the EventSourcing metadata, the Tenancy/Web impersonation propagation. The app's
composition root references `YFex.Audit` and calls `AddYFexAudit(...)`.

**Dependency layers**

```
Layer 0   : YFex core — YFex.Auditing contract (no dependencies), next to YFex.Diagnostics (logging) and YFex.Privacy (declarations)
Layer 1+  : Cqrs, Rules, Messaging.Rpc, Data, EventSourcing, Tenancy, Jobs, Payment, Automation … → core probes only
Engine    : YFex.Audit (→ core, Tenancy, Persistence, YFex.Privacy) — must NOT reference Cqrs/Data/Logging/UI (they call it through core)
Store     : YFex.Audit.Data (→ Audit, YFex.Data)
Server    : YFex.Audit.Server (→ Audit, Audit.Data, Cqrs, Messaging.Rpc, Jobs, Authorization, Privacy.Rights)
Client    : YFex.Audit.Client (→ Audit, Messaging.Rpc, Mvvm)       UI: YFex.UI controls (→ Audit.Client view models)
No cycle  : Privacy.Rights audits its own actions through core IAuditTrail; Audit.Server references Privacy.Rights, not the reverse
```

## Hard constraints (apply to every file)

1. **AOT & trimming:** `IsAotCompatible=true`, trim analyzers on, `TreatWarningsAsErrors`. No reflection, no `Expression`, no `dynamic`,
   no `Enum.ToString()` on hot paths (generated name tables). JSON only through source-generated contexts. Generators use
   `ForAttributeWithMetadataName`, `EquatableArray<T>`, and the Parse → Group → Emit pipeline shape.
2. **Zero allocation on hot paths:** a non-audited message pays **nothing** (the stage is not installed). `[AuditChanges]` diffs write into a
   pooled buffer through the `ChangeWriter` ref struct, values through `IRawValue<T>`/`RawValue` (no boxing), generic constrained calls,
   no closures, no params arrays, no LINQ (own loops/iterators). Allocation budget per audited operation: **one** pooled scope (returned), and the
   unavoidable `byte[]` handed to `IKeyValueStore.SetAsync` or the ADO.NET parameter on the durable write (documented in 05 §9).
3. **`[Union]` + `IUnion` tagged structs** (pattern of `YFex.Cqrs/Result.cs`) on hot paths. The native `union` keyword only on cold paths
   (server read models, verification reports).
4. **Domain-agnostic base types.** The library owns closed sets it controls (actor kinds, change kinds, durability, verification status). It does
   **not** know Sale, NF-e, Money, stores or fiscal law. Actions, categories, retention classes, entity names and permissions are **app-declared**.
   Business names appear only in docs, tests and samples.
5. **Own types first** (`AuditEntryId`, `AuditTarget`, `ChangeWriter`, `AuditJournal`), BCL where it is the standard (`SHA256`, `ECDsa`,
   `Guid.CreateVersion7`). **BCL cryptography only.** No NuGet package without the user's approval.
6. **No per-member boilerplate.** `[AuditChanges]` goes on the type, actions in the aggregate configuration, sensitivity on value-object types
   (YFex.Privacy). Members are included by convention and reviewed in `AuditSchema.verified.txt`. `[NotAudited]` is the rare exception.
7. **Fail closed.** Unknown sensitivity → treated as personal. A missing subject key → value unreadable, never "returned in clear".
   A durable write that cannot happen → the action is refused when that is still possible (05 §5).
8. **Explicit names, no abbreviations.** `Id` is the only accepted short suffix, as in the existing `TraceId`/`OperationId`/`StreamId`;
   never `Aud`, `Op`, `Chg`, `Ctx`.
9. **net11 preview, `LangVersion=preview`.** `YFex.System` shadows `System`: generated code always uses `global::` qualified names.

## Decisions to confirm (⚑)

| # | Proposal | Recommendation |
|---|---|---|
| ✅ A1 | Where the library lives | **CONFIRMED 2026-10-02:** new sibling repo `C:\Dev\YFex\YFex.Audit` (`audit.slnx`) |
| ✅ A2 | Shared contract | **CONFIRMED 2026-10-02 (changed):** a separate leaf contract assembly **`YFex.Audit.Public`** (refs YFex core + BCL only, like `YFex.Authorization.Public`), **not** a namespace in YFex core. Cqrs/Rules/Jobs/Data/ES and their generators reference `YFex.Audit.Public`; the engine is `YFex.Audit`. Every "YFex core `YFex.Auditing`" mention in 02–12 now means `YFex.Audit.Public` (docs updated in Phase 0) |
| ✅ A3 | Sharing with logging | **CONFIRMED 2026-10-02 (refined):** reuse what logging already shipped in `YFex.Diagnostics` as is: `[Audited]`, `OperationDescriptor<T>` + `OperationCapture<T>`, `LogWriter`/`LogFieldReader`, `TraceContext`, `OperationOutcome`. **No rename** to `CaptureWriter` |
| ✅ A4 | Entry model | **CONFIRMED 2026-10-02** as proposed (actor + real actor + relation, several targets, field changes per target, outcome + code, app-declared actions/categories) (02) |
| ✅ A5 | Declaring audited commands | **CONFIRMED 2026-10-02** as proposed: `.Audit(a => …)` in the aggregate configuration (generator-parsed), `b.Audit(…)` aggregate-wide, `[Audited]` accepted, target by convention (03 §2) |
| ⏸ A6 | Read (access) audit | **DEFERRED 2026-10-02:** not needed for now. Design kept in 03 §5 for a future feature; nothing built |
| ✅ A7 | Entity change auditing | **CONFIRMED 2026-10-02** as proposed: `[AuditChanges]` on the type → generated `IAuditChanges<T>` diffs, members by convention, `AuditSchema.verified.txt` (04 §2). The diff *source* is still open (⚑A9) |
| ⚑A8 | Change without a declared action | **Never lost:** an `[AuditChanges]` write outside any audited scope creates an **implicit entry** `"{Entity}.Changed"` with the ambient actor (04 §8) |
| ⚑A9 | YFex.Data change source | Hook the **stage-2 generated row shapes** (`UpdateAsync`/`MergeAsync`/`DeleteAsync`); before-image by dialect: Postgres 18 `RETURNING OLD.*`, SQL Server `OUTPUT DELETED.*`, otherwise lock-and-read in the same transaction (04 §3). Raw SQL is not covered (analyzer can't see it) unless ⚑A10 |
| ⚑A10 | Database trigger mode | **Optional** (Postgres/SQL Server): generated triggers write out-of-band changes (DBA edits, other apps) into the inbox, flagged `OutOfBand` when the session carries no audit context (04 §4) |
| ⚑A11 | EventSourcing | Events **are** the change record: audited aggregate commands list emitted events (captured by their descriptors). **Add `EventMetadata`** (entry id, actor, trace, causation) to `EventRecord` (04 §5) |
| ⚑A12 | State.History | **Not an audit source** (client-side, forgeable, unsaved). Used only for the "unsaved local edits" overlay in the history panel and the logging edit timeline (04 §7, 09 §4) |
| ⚑A13 | Write path | **Transactional inbox** on servers (same DB transaction, via a new YFex.Data transaction participant hook); **durable local journal** on nodes (`IKeyValueStore`, ordered keys, SQLite `synchronous=FULL`, group commit); ack-based shipping; never dropped; blocking back-pressure (05) |
| ⚑A14 | Irreversible external effects | **Two-phase intent/outcome** (`.Audit(a => a.TwoPhase())`): a durable intent before the external call (SEFAZ, payment provider), the outcome after; a dangling intent shows as "outcome unknown" (05 §4) |
| ⚑A15 | Failure policy | **"No audit, no action"** wherever the audit can precede or commit with the action (transactional, two-phase, denials refused before execution). After an irreversible action, a failing audit write spills to a local durable journal + Critical, never throws away the fact (05 §5) |
| ⚑A16 | Server chain partition | **One chain per (top-level tenant, retention class, UTC day)**, sealed asynchronously by **one sequencer per chain** (Postgres advisory lock per chain, 06 §4); seals in a separate insert-only table (06 §4) |
| ⚑A17 | Node chain + device signature | Per-node chain; every shipped batch signed with the **device's ECDsa P-256 key**. Reuse and **move** the device key infrastructure now in YFex.Rules (`IDeviceKeyStore`) to a shared place (06 §3, 10 §3) |
| ⚑A18 | Anchors & proofs | Signed anchors every **1 000 entries or 10 min** per active chain, plus a **day seal with a Merkle root** (RFC 6962 style) so an export of one entity's history carries inclusion proofs without revealing other entries (06 §5) |
| ⚑A19 | Storage | **Postgres `audit` schema** (monthly partitions by recorded time, insert-only writer role, `UPDATE`/`DELETE` blocking triggers, RLS for the reader role), **SQLite** for single box/dev. Marten stream store only as an optional package, **not recommended** (07 §5) |
| ⚑A20 | Canonical encoding | **Binary canonical encoding v1** is the hashed and stored truth; JSON is a projection (02 §9) |
| ⚑A21 | Sensitive values | Consume YFex.Privacy: treatment per sensitivity **for audit** (reversible for auditors, unlike logs): Personal/PersonalIdentifier → subject-key encryption + pseudonym; SensitivePersonal → encryption only; Financial → token reference or erased; Secret → erased (08 §2) |
| ⚑A22 | Erasure | **Crypto-shredding through YFex.Privacy**; `YFex.Audit.Server` is an `IPersonalDataStore` participant; legal holds come from YFex.Privacy.Rights (08 §3–§4) |
| ⚑A23 | Retention | **App-declared retention classes**; categories map to classes; purge drops **whole chains** only when expired, sealed, exported and not on hold; a signed **tombstone** keeps continuity (08 §5) |
| ⚑A24 | Meta-audit | Every audit query, reveal and export writes an access entry (coalesced per query, one entry per reveal) (08 §7) |
| ⚑A25 | Denials | Audit **every denied command**; denied queries only when access-audited; identical denials from the same actor/action coalesce **per minute with a count** (lossless count, bounded volume) (03 §6) |
| ⚑A26 | Real actor propagation | New ambient **`ActorContext`** in YFex core (effective + real actor + relation), set by the YFex.Web impersonation middleware, carried by Rpc `CallContext` (new `RealSubject`), read by every audit producer (02 §3) |
| ⚑A27 | Existing seams | Keep / adapt / replace per seam as in 01 §3 (summary: keep functional stores, replace audit sinks with core calls, adapters for interfaces owned by low layers) |
| ⚑A28 | Jobs naming clash | Rename YFex.Jobs `AuditEntry`/`AuditKind`/`AppendAuditAsync` to `JobHistoryEntry`/`JobHistoryKind`/`AppendHistoryAsync`; forward governance kinds to YFex.Audit (01 §3.6) |
| ⚑A29 | UI | `YEntityHistory` control in YFex.UI (Avalonia, Blazor by conversion) + `EntityHistoryViewModel` in YFex.Audit.Client; **actor display names resolved at read time** through `IAuditActorDirectory`, never stored in clear in the chain (09) |
| ⚑A30 | Offline verifier | `yfex-audit` CLI verifies an export package (chains, anchors, Merkle proofs, signatures) with no database and no app assemblies (07 §9) |
| ⚑A31 | Shared ledger primitive | Put `CanonicalWriter`/`Hash256`/`HashChain` (SHA-256 chaining over canonical bytes, 06 §2) in YFex core so the YFex.Privacy consent ledger (⚑P9) and Rules publish history reuse it instead of re-implementing (10 §3). **Superseded 2026-10-04 by cryptography ⚑K2/K11 (confirmed):** the `Hash256` type is in core (`YFex.Cryptography`, types only); `CanonicalWriter`, `HashChain`, `MerkleTree` live in the YFex.Cryptography library; audit domains `yfex.audit.*.v1` |

Open questions for the user are at the end of [12-phases-and-acceptance.md](12-phases-and-acceptance.md#open-questions-for-the-user).
