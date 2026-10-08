# 12 — Phases & acceptance

Order principle: **contract → engine → generators + Cqrs → store/server → migrate existing seams → change sources → privacy & lifecycle →
UI last**. Each phase ends with green tests, a native-AOT smoke publish with zero warnings, benchmarks for the budgets it touches, and a
section appended to the implementation log below (date, what was done, deviations, gotchas, test counts) — the same protocol as the
validation and logging designs.

## Cross-design dependencies

| Needs | From | Used in phase | If not ready |
|---|---|---|---|
| `YFex.Diagnostics` subset: `TraceId`, `OperationId`, `TraceContext`, `OperationOutcome`, `FieldSpec`, `OperationDescriptor<T>`, `OperationDescriptorFor<T>`, capture writer (`CaptureWriter`, ⚑A3) | logging Phase 0 | 0 | implement exactly that subset as specified in logging 02 (with the ⚑A3 rename), log it in **both** implementation logs |
| `YFex.Privacy` core declarations (`DataSensitivity` 7 values, `[Sensitive]`, `IClassifiedData<T>`, `[DataSubjectKey]`) | privacy Phase 0 | 0–2 | same: implement the declaration subset as specified by privacy 02 |
| `YFex.Privacy` runtime (`Pseudonymizer`, `AesGcmEnvelope`, `IKeyVault`, `TextScrubber`) | privacy runtime phase | 6 | until then the engine **fails closed**: every value above `Internal` is written `Erased` (no clear personal data ever) |
| `YFex.Privacy.Rights` (`IPersonalDataStore`, legal holds) | privacy rights phase | 6 | purge job disabled (nothing is deleted) |
| YFex.Data adapter + `IBulkWriter` + `RowReader<T>` | YFex.Data stage 1 (in progress) | 3 | — (required) |
| YFex.Data `ITransactionParticipant` | proposal to YFex.Data (05 §2.2) | 3 | small; implemented in YFex.Data by this design's Phase 3 session with the owner's approval |
| YFex.Data stage 2 (generated row shapes, before-image dialects) | YFex.Data stage 2 | 5 | handlers call `AuditScope.Updated(before, after)` explicitly (manual, still typed and generated diffs) |
| Shared device identity (move of `IDeviceKeyStore`) | ⚑A17 | 1 / 4 | Phase 1 uses a local `IAuditDeviceKey` seam; Phase 4 switches to the shared one |

## Phases

| Phase | Scope | Loads | Acceptance |
|---|---|---|---|
| **0 — Core contract** (⚑A2, ⚑A3, ⚑A20, ⚑A26, ⚑A31) | YFex core `YFex.Auditing`: ids, action/category, actor (+ `ActorContext`, `IAuditActorSource`), target, change kinds, treatments, approvals/decision, flags, durability, `ChangeWriter`, `IAuditChanges<T>`, `AuditEntityDescriptor`, `AuditDescriptor(<T>)` + `AuditDescriptorFor<T>`, `AuditScope` (API + no-op), `IAuditTrail` + `NullAuditTrail`, attributes, `CanonicalWriter`, `Hash256`, `HashChain` | README, 02, 03 §3/§7, 06 §2 | core has no new dependency; **golden canonical vectors** (≥ 30 entries incl. every value kind, NFC, decimal normalization) committed as test data; `HashChain` test vectors; with audit not installed, a generated `[AuditChanges]` hook and `IAuditTrail` calls cost 0 B and ≤ 2 ns (benchmark) |
| **1 — Engine** (⚑A1, ⚑A13, ⚑A15, ⚑A17) | repo `YFex.Audit` (`audit.slnx`): trail, pooled scopes, encoder/decoder v1, protection pipeline (Clear/Digest/Erased/Omitted until Privacy), `AuditJournal` over `IKeyValueStore` (SQLite `synchronous=FULL`, group commit, cursors, cap/back-pressure, local retention), node chain + device signature, spill ring + crash file, actor sources, `AddYFexAudit`, `YFex.Audit.Testing` (capture, assertions, crash harness, fakes), AotSmoke, benchmarks | 02, 05, 06 §3 | 05 §9 budgets met; **crash test**: 1 000 random `kill -9` during group commit → zero lost, zero duplicated entries, node chain continuous; journal full → producers refused, nothing evicted; decoder round-trips the golden vectors; AOT zero warnings |
| **2 — Generators + Cqrs + actor propagation** (⚑A4–⚑A8, ⚑A25, ⚑A26) | `YFex.Audit.Generator` (`[AuditChanges]` differs, `[Audit]` methods, schema tables, YFA diagnostics, shared emit file); Cqrs generator `.Audit()` / `b.Audit()` / `[Audited]` parsing + descriptor emission; `AuditStage` (LocalDispatcher + server `IHandlerInvoker` decorator), outcome mapping, denial coalescing, queued/replay linking; `AuditSchemaSnapshot`; Rpc `CallContext` actor fields + `CallContextAuditActorSource`; Web middleware `ActorContext` | 03, 04 §2, 02 §3 | generator golden + caching tests (Parse→Group→Emit, `EquatableArray`); in `Messaging.Rpc.Tests` (two hubs) an audited command produces **exactly one** server entry and none on the client; denied command audited before the error returns; offline queued + replay share the idempotency key; impersonated HTTP and RPC requests carry the real actor; existing test counts unchanged |
| **3 — Store & server core** (⚑A16, ⚑A18, ⚑A19, ⚑A30 partial) | `YFex.Audit.Data` (Postgres + SQLite schema, roles, blocking triggers, RLS for readers, transactional inbox writer via `ITransactionParticipant`, `IBulkWriter` ingest, readers); `YFex.Audit.Server` (ingest RPC, node chain/signature checks, sequencer with leases, anchors, day seals + Merkle, verifier incl. projection check, gaps, queries, review queue, `AddYFexAuditServer`); `YFex.Audit.Client` shipper; `samples/YFex.Audit.Server.Host` | 05 §2/§6, 06, 07 | **tamper suite**: T1–T5 and projection tampering each detected at the right index/day; **atomicity**: business rollback → no entry, commit → entry; **offline POS simulation** (3 days, 10 000 entries, flaky network) → all sealed, zero gaps, chain verified; ingest ≥ 5 000 entries/s on a dev Postgres; RLS tests (a store admin never sees another store/tenant); SQLite passes the same store contract suite |
| **4 — Migrate existing seams** (⚑A27, ⚑A28) | Rules (`RulesStage` decision/approvals, `RulePublisher`, `ApproveDecision` + device issuer, `[Obsolete]` sinks + forwarding adapters), Tenancy adapter, Jobs rename + governance facts, Hub forwarding, Rpc `Sync.*` facts, Payment event audit, Automation `AuditingStepObserver`, Security admin facts, Web credentials facts, shared device identity switch | 01 §3 | each seam: test proving the fact is audited with actor/tenant/trace; old interfaces still compile (obsolete) and forward; Rules/Rules.Admin/Jobs/Payment/Automation suites green with unchanged counts + new tests |
| **5 — Change sources** (⚑A9–⚑A12) | YFex.Data stage-2 hooks + before-image per dialect; ES `EventMetadata` + audited aggregate entries (+ Marten mapping); Persistence optional; trigger mode | 04 | per-dialect before-image tests (SQLite always; Postgres ≥18 / <18, SQL Server, Oracle gated by env vars); continuation entries for 10 000-row changes in one transaction; ES metadata round trip in-house + Marten; out-of-band trigger test (DBA `UPDATE` → `OutOfBand | NeedsReview` entry, app writes not duplicated) |
| **6 — Privacy & lifecycle** (⚑A21–⚑A24, ⚑A30) | treatments via YFex.Privacy (encrypt/pseudonymize/tokenize/scrub), reveal + permissions, meta-audit, `IPersonalDataStore` participant, retention classes + purge + tombstones, legal holds, WORM export (`.yaudit`), partial exports with proofs, `yfex-audit` CLI, witness seam, signing key rotation | 06 §8–§10, 07 §8–§9, 08 | **erasure test**: subject key shredded → values read "erased", chain verifies; **hold test**: purge refused while a hold exists; **purge test**: tombstone written, the next day's genesis verifies; export verified by the AOT CLI on a machine without the database; reveal and every query produce access entries |
| **7 — UI (last)** (⚑A29) | `EntityHistoryViewModel`, review queue VM, explorer VM (YFex.Audit.Client); `YEntityHistory` in YFex.UI (Avalonia gallery page; Blazor via conversion); optional LoggY audit module | 09 | gallery page renders §2 of 09 from a sample server; unsaved overlay from State.History; badges for every status; localized labels; no per-page code beyond binding the target |

### What each session loads

| Session | Read | Skip |
|---|---|---|
| Phase 0 | README, 02, 03 §3/§7, 06 §2, logging 02 §1–§5, privacy 02 | 07–11 |
| Phase 1 | README, 02, 05, 06 §3, 10 §1 | 07–09, 11 |
| Phase 2 | README, 02 §3, 03, 04 §2, logging 04 §2–§3, validation 09 (Cqrs pipeline), 11 §2–§6 | 06–08 |
| Phase 3 | README, 05 §2/§6, 06, 07, YFex.Data `DATA-ACCESS-AND-BULK-WRITE.md`, `ROW-LEVEL-SECURITY.md` | 04, 09 |
| Phase 4 | README, 01, 03 §6–§7, 11 §4–§7 | 06–08 |
| Phase 5 | README, 04, event-sourcing-design.md | 06–09 |
| Phase 6 | README, 06 §8–§10, 07 §8–§9, 08, privacy README + 05 + 07 | 03–04 |
| Phase 7 | README, 07 §6–§7, 09, YFex.UI CLAUDE.md | 04–06 |

## Open questions for the user

1. **⚑ decisions** A1–A31 in the README.
2. **Pilot.** Library-first like logging (samples + test suites), or is there a first app (Farma POS? fiscal emission?) to validate Phases 2–3?
3. **Retention periods per category.** Fiscal ≥ 5 years (+ margin) is known. Which other categories do your apps need, and for how long
   (cash, payments, controlled substances / SNGPC records, labor, security)? They become the default `[AuditCategories]` in the samples.
4. **Chain day = UTC day.** Acceptable, with reports filtered by local time (pitfall 2.22)? Alternative: tenant-local day chains (more complex
   for multi-region tenants).
5. **Anchor signing keys.** Software keys wrapped by the YFex.Privacy root key now, HSM/cloud KMS later through the seam — acceptable?
6. **External witness.** Do you want the daily tenant witness (e-mail/webhook) in the first release? RFC 3161 timestamping would need the
   `System.Security.Cryptography.Pkcs` package — approve now, later, or never?
7. **Access audit.** Do your apps need audit of **reads** at launch (patient/prescription data in Farma, customer profiles)? Should cache
   reads on devices be audited too (03 §5)?
8. **User impersonation** ("support acts as user", RFC 8693 `act` claim) — add it to YFex.Web as an explicit, audited feature?
9. **POS defaults.** Journal cap 256 MiB, local retention 30 days, refuse actions when the journal is full — acceptable for your terminals?
10. **YFex.Data stage 2 timing.** Until it lands, handlers call `AuditScope.Updated(before, after)` explicitly. OK, or should audit Phase 5 wait?
11. **Postgres version.** Require Postgres 18 (`RETURNING OLD`) for servers, or keep the lock-and-read path for older versions?
12. **Auditor formats.** Are CSV + JSONL + the signed `.yaudit` package enough? A PDF report would need a package (approval).
13. **AI agents.** Should every AI tool call that changes business data be `NeedsReview` by default?
14. **Actor names.** Resolving names at read time shows a person's *current* name. Is that acceptable, or do you want an encrypted
    name snapshot per entry (erasable through the subject key)?

## Implementation log

*(append per phase: date, what was done, deviations from this design, gotchas, test counts)*

### 2026-10-03 — Phase 0 (adapted to ⚑A2) + engine core of Phase 1 + `[AuditChanges]` generator

Scope chosen by the user: "start with the basics already decided" (A1–A5, A7 confirmed; A6 deferred). Repo
`C:\Dev\YFex\YFex.Audit` (`audit.slnx`, not a git repo, nothing committed).

**Built**

| Project | Contents |
|---|---|
| `YFex.Audit.Public` (leaf: YFex core + BCL) | `AuditEntryId` (UUID v7), `AuditAction` (stable hash id), `AuditCategory`, `AuditActor`/`AuditActorKind`/`ActorRelation`, `AuditTarget`, `ChangeKind`, `ValueTreatment`, `AuditEntryFlags`, `AuditReason`/`AuditApproval`/`AuditDecision`; `AuditBuffer` (pooled, canonical primitives, NFC UTF-8, normalized decimals); `AuditValueWriter` + `AuditSpanReader`/`AuditValue` (value encoding, Privacy Tier 1 treatments); `ChangeWriter`; `IAuditChanges<T>` + `AuditEntityDescriptor`/`AuditMemberSpec`/`AuditEntities` registry; attributes `[AuditChanges]`, `[AuditEntity]`, `[NotAudited]`; `AuditScope` (ambient, abstract); `IAuditTrail` + `NullAuditTrail`; `AuditDescriptor`/`AuditDescriptor<T>`/`AuditDescriptorFor<T>`; `IAuditActorSource`, `IAuditTenantSource`; `AuditChanges` helper; `AuditKeys`, `AuditEquality` |
| `YFex.Audit` | `AuditTrail` (pooled scopes, actor sources, tenant via YFex.Tenancy, trace from `Operations.Current` or a new root, gap-free node sequence), canonical entry encoder v1 + `AuditEntryReader` (rejects bad magic/version, out-of-order or unknown sections, unread bytes), `AuditEntry` read model, `CaptureTranscoder` (runs the shipped logging `OperationCapture<T>` into a private `LogWriter` and re-encodes it, ⚑A3), `IAuditEntrySink` + `InMemoryAuditSink`, `AddYFexAudit` + `AuditBuilder`, `FixedAuditActorSource`, `TenancyAuditTenantSource` |
| `YFex.Audit.Generator` | `[AuditChanges]` → explicit `IAuditChanges<T>` implementation + `[ModuleInitializer]` registration; members by type/convention (values, enums by name, `IRawValue<T>` value objects, Privacy-classified value objects, text with secret-name erasure, blobs as digests); keys `Id`/`{Type}Id`/`{Entity}Id`/`[Key]` (≤ 3, composite with `|`); excluded: concurrency tokens, self-timestamps, `[NotAudited]`, collections and nested objects (YFA0020 info); record-level `[Sensitive]` floors members except the key. Diagnostics YFA0001/0003/0004/0020 |
| `YFex.Audit.Testing` | `AuditCapture` (decodes every entry: proves the round trip), `AuditEntryAssertion` (By, AsRealActor, InTenant, WithOutcome, Target, WithChange, WithChangeValue, WithProtected, WithField, WithReason, WithApproval, Flagged), `AuditSchemaSnapshot` (`AuditSchema.verified.txt`) |
| `samples/YFex.Audit.AotSmoke` | native AOT publish: 0 warnings, exit 0 (generated diff + masked phone + money + enum + reason, 413-byte entry) |

**Tests:** `YFex.Audit.Tests` 25 (entity diffs incl. real `Cpf`/`Email` from YFex.Entity.Public, privacy treatments, composite keys,
descriptor/registry, snapshot, trail facts, impersonation, approvals → `NeedsReview`, nested ambient scopes + shared trace, unscoped
counting, sink failure, sequences, `NullAuditTrail`, logging-capture reuse, DI + tenancy override, every value kind round trip,
canonical equality of `10.00m`/`10m` and NFC/NFD text, digest of long text, decoder rejections), `YFex.Audit.Generator.Tests` 6
(compiles warning-free, emitted calls, YFA0001/0003/0004/0020, attribute overrides, incremental caching). Solution: 0 warnings.

**Deviations from the design text (review)**

1. **Namespace:** contracts in assembly `YFex.Audit.Public` use namespace `YFex.Audit` (one `using` for apps), like `YFex.NFe.Public` →
   `YFex.NFe`. Authorization.Public uses `YFex.Authorization.Public` as namespace instead; say so if you prefer that.
2. **Static member names** on `IAuditChanges<T>` are prefixed (`AuditEntity`, `AuditTargetOf`, `AuditCreated`, `AuditUpdated`,
   `AuditDeleted`) so they never collide with entity members named `Entity`/`Created`/`Deleted` (04 §2.1 had them unprefixed).
3. **`ValueTreatment.Masked` added** (02 §5 had no masked treatment): Privacy Tier 1 has masks but no encryption, so personal values are
   stored as their mask until Tier 2. `SensitivePersonal`, `Financial`, `Secret` → `Erased` (same rule as `IsNeverRecorded`).
   `Pseudonymized`/`Encrypted`/`Tokenized` are reserved values.
4. **Free text** (reasons, unclassified strings) is `Internal` → clear, following Privacy Tier 1 classification; scrubbing plugs in with
   Privacy's `TextScrubber` (Tier 2). Raw members/records declared `[Sensitive]` are masked or erased.
5. **⚑A8 pending:** a change with no open scope is **not** recorded; `AuditChanges.UnscopedChanges` counts it.
6. **⚑A13 pending:** `IAuditEntrySink` is the seam for the durable path; only `InMemoryAuditSink` exists. `AuditOptions.NodeId` is random
   per process until the journal persists it; node sequence is in-memory.
7. **⚑A26 / Authorization ⚑Z3:** no `ActorContext` created (Authorization's design owns it). Actors come from `IAuditActorSource`
   adapters; the core actor context becomes the first source when it exists.
8. **Canonical encoding v1 implemented** as in 02 §9 with fixed-width `u32` section lengths (patchable while writing) instead of varint
   section lengths; strings keep varint prefixes. Hashing/chains (06) not started.
9. **Diffs of YFex.Data writes (⚑A9 re-opened):** handlers call `AuditChanges.Updated(before, after)` explicitly; no automatic hook yet.
10. Collections and nested objects are excluded with YFA0020 (keyed collection diffs, nested shapes: later).

**Gotchas**

- `AuditScope.Current` changes made **inside an `async` method/lambda** do not flow back to the caller (AsyncLocal semantics). That is
  why `IAuditTrail.Begin`/`CompleteAsync` are not `async`; tests must call `CompleteAsync` directly, not inside `async () => …`.
- Generated pattern variables leak to the enclosing block (`if (x is { } v)`): use one temporary name per member.
- Bash heredocs turn `\u0301` escapes into real characters: write Python edit scripts with the Write tool (`C:\Temp\yfex-edits`).
- After editing the generator: `dotnet build-server shutdown`, build in Release.

**Next (needs decisions):** ⚑A8 (implicit entries), ⚑A9 (automatic diff sources: `MergeAsync`, EF `SaveChanges`), ⚑A13–A15
(durable journal, transactional inbox, failure policy), Cqrs `.Audit()` + `AuditStage` (coordinate with the logging Phase 2 and
authorization sessions, which touch Cqrs/Messaging.Rpc), ⚑A16+ (chains).

### 2026-10-04 — cryptography alignment (cryptography session, ⚑K2/K11 confirmed)

- `AuditDigest.Sha256` is now the core `Hash256` type (was a lower-case hex `string`). Format it with `ToString()` or compare values.
- `AuditValueWriter.WriteDigest` erases blobs above Internal, like every other non-text value. An unkeyed digest of personal or
  secret content lets anyone confirm a guess (cryptography 01 A-1). Keyed digests can come later as `ValueTreatment.Pseudonymized`.
  The generator's schema snapshot shows such members as `Erased`.
- The ⚑A31 primitives are re-homed: `Hash256` type → core; `CanonicalWriter`, `HashChain`, `MerkleTree` → YFex.Cryptography (not
  built yet). The engine's own entry codec stays. Domain strings for chains, when they are built: `yfex.audit.node.v1`,
  `yfex.audit.chain.v1`, `yfex.audit.anchor.v1`, `yfex.audit.tombstone.v1`.
- Tests: Audit.Tests 26, Generator.Tests 6. Details in docs/internal/cryptography/12 §5.
