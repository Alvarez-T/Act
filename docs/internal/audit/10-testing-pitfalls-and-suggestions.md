# 10 — Testing helpers, pitfalls and ecosystem suggestions

## 1. `YFex.Audit.Testing`

Framework-agnostic (works with xUnit/NUnit/MSTest, like `YFex.Validation.Testing`).

```csharp
// (sketch) API surface
public sealed class AuditCapture : IAuditTrail, IDisposable          // in-memory trail; decodes canonical bytes back (proves the encoding too)
{
    public static AuditCapture Install(IServiceCollection services);  // replaces IAuditTrail, journal and actor sources with deterministic fakes
    public IReadOnlyList<AuditEntry> Entries { get; }
    public AuditEntry Single(string action);
}

public static class AuditAssertions                                  // fluent, message-rich failures
{
    public static AuditEntryAssertion ShouldHaveAudited(this AuditCapture capture, string action);
    public static void ShouldNotHaveAudited(this AuditCapture capture, string action);
}
public sealed class AuditEntryAssertion
{
    public AuditEntryAssertion By(string actorId); public AuditEntryAssertion AsRealActor(string realActorId, ActorRelation relation);
    public AuditEntryAssertion InTenant(string tenantPath); public AuditEntryAssertion WithOutcome(OperationOutcome outcome, string? code = null);
    public AuditEntryAssertion Target(string entityType, string key);
    public AuditEntryAssertion WithChange<T>(string path, T before, T after);
    public AuditEntryAssertion WithProtected(string path, ValueTreatment treatment);   // e.g. Cpf must be Encrypted
    public AuditEntryAssertion WithApproval(string approverId); public AuditEntryAssertion WithReason(string text);
    public AuditEntryAssertion Flagged(AuditEntryFlags flags);
}

public static class AuditSchemaSnapshot                              // the reviewed safety net (04 §2.4)
{
    public static Task Verify(global::System.Reflection.Assembly assembly, [CallerFilePath] string path = "");   // reads generated
}                                                                    // static schema tables (no reflection over members), writes/compares
                                                                     // AuditSchema.verified.txt next to the test

public static class AuditTamper                                      // for verifier tests and app-level "prove it" tests
{
    public static void FlipByte(IAuditTestStore store, AuditEntryId id, int offset);
    public static void DeleteEntry(IAuditTestStore store, AuditEntryId id);
    public static void SwapOrder(IAuditTestStore store, ulong indexA, ulong indexB);
    public static void TruncateTail(IAuditTestStore store, string tenant, string retentionClass, DateOnly day, int count);
    public static void TamperProjection(IAuditTestStore store, AuditEntryId id, string path, string newText);
}

public abstract class AuditStoreContractTests { /* the same suite runs against in-memory, SQLite and gated Postgres (YFEX_TEST_POSTGRES) */ }
public sealed class FakeAuditClock; public sealed class FakeDeviceKey; public sealed class InMemoryAuditJournal;
public sealed class JournalCrashHarness { /* runs a child process, kills it mid group-commit, asserts no loss and no duplicate */ }
```

**✍ A typical app test**

```csharp
[Fact]
public async Task Changing_price_is_audited_with_before_and_after()
{
    using var audit = AuditCapture.Install(services);
    using var _ = ActorContext.Enter(new AuditActor(AuditActorKind.User, "joao"));

    await dispatcher.SendAsync(new Produto.Commands.AlterarPreco(id, NovoPreco: 12m, Motivo: "reajuste"), ct);

    audit.ShouldHaveAudited("Produto.AlterarPreco")
         .By("joao").WithOutcome(OperationOutcome.Ok)
         .Target("Produto", id.ToString())
         .WithChange("Preco", new Money(10m), new Money(12m))
         .WithReason("reajuste");
}

[Fact] public Task Audit_schema_is_reviewed() => AuditSchemaSnapshot.Verify(typeof(ProdutoEntity).Assembly);
```

## 2. Pitfalls

| # | Pitfall | What goes wrong | Design answer |
|---|---|---|---|
| 2.1 | Audit written after the business commit, in another transaction | crash between the two = change without audit | transactional inbox (05 §2), two-phase for external effects (05 §4) |
| 2.2 | Chaining inside business transactions | every write of a tenant serializes on the "last hash" row → throughput collapse, deadlocks | async sequencer per chain (06 §4) |
| 2.3 | Hashing JSON | key order, number formatting and Unicode normalization differ between serializers → false "tampered" | canonical binary encoding (02 §9) |
| 2.4 | Raw SQL writes on audited tables | no diff, no entry | YFA0050 hint, stage-2 API as the norm, optional trigger mode (04 §4) |
| 2.5 | Trusting client-side "audit" (UI deltas, client logs) | forgeable, unsaved, undone | server/executing node writes; State.History never a source (04 §7) |
| 2.6 | Using `IDurableQueue<T>` as an outbox | delete-on-dequeue loses entries on crash; TTL expires them | `AuditJournal` with ack cursors (05 §3) |
| 2.7 | SQLite `synchronous=NORMAL` | last commits lost on power cut | `FULL` for the journal + group commit |
| 2.8 | Outbox/journal overflow eviction | silent loss | never evict; block and refuse (05 §3.1) |
| 2.9 | Storing display names, e-mails, IPs in entries | erasure impossible without breaking chains | subject ids only; names at read time; protected values (08) |
| 2.10 | Deleting rows for LGPD erasure | breaks chains, destroys evidence | crypto-shredding (08 §3) |
| 2.11 | One chain mixing retention periods | cannot purge 1-year data without breaking the 5-year chain | chain per retention class (06 §4) |
| 2.12 | Purging by deleting old rows | continuity broken, looks like tampering | signed tombstones; next genesis links to them (08 §5) |
| 2.13 | Ordering by device clock | wrong "who changed it last" | `occurred_corrected` + node sequence; raw time kept for the hash (05 §8) |
| 2.14 | Re-auditing replays | one action shows twice | entry id idempotency; Cqrs idempotency short-circuit not re-audited (05 §7) |
| 2.15 | Impersonation lost after the middleware | actions look like the admin's own or the user's own | `ActorContext` + `CallContext.RealSubject` (02 §3) |
| 2.16 | Auditing every query | volume explodes, noise | access audit opt-in (03 §5) |
| 2.17 | Denial floods | attacker fills the audit store | per-minute coalescing with exact counts (03 §6.3) |
| 2.18 | Audit-of-audit recursion | each read produces an entry, read by the next query… | one entry per query, `access` excluded from default views (08 §7) |
| 2.19 | Enum stored by number | renumbering corrupts history | name + value (02 §5) |
| 2.20 | Entity schema evolves (field renamed) | old entries show unknown fields | paths stored as strings; `AuditEntityDescriptor.Version`; snapshot diff reviewed; UI falls back to the raw path |
| 2.21 | Large values (XML, blobs) in diffs | entries of megabytes | `Digest` above 4 KiB (04 §2.3) |
| 2.22 | UTC day vs tenant's local day | reports "per business day" cross two chains | chains are per UTC day (technical); reports filter by local time ranges; verification is per chain day |
| 2.23 | Signing key and data on the same box, no witness | an insider can rewrite and re-sign | WORM export + tenant witness (06 §8–§9) |
| 2.24 | Event-sourced local streams as evidence | `ReplaceAsync` rewrites them on rebase | server stream + entries are the evidence (04 §5) |
| 2.25 | Audit failure silently swallowed in a `catch` | lost facts nobody notices | spill ring + Critical + self-log; `IAuditTrail` methods never return "ok" without durability (05 §5) |
| 2.26 | Approval *use* audited but not *issuance* | cannot prove the manager approved | `Rules.ApprovalIssued` (01 §3.3) |
| 2.27 | `object` parameters in audit hooks | boxing, no typed capture, no snapshot | typed descriptors (03 §3) |

## 3. Ecosystem suggestions (found while designing; owners decide)

| Library | Suggestion |
|---|---|
| **YFex core** | Host `CanonicalWriter`, `Hash256`, `HashChain` (⚑A31) for every append-only ledger (audit, Privacy consent ledger, Rules publish history). Add `ActorContext` (02 §3) |
| **Shared device identity** | Move `IDeviceKeyStore` + device key generation/enrollment/revocation out of YFex.Rules into a shared place (core contract + platform key protection in `YFex.System.Windows`/mobile), reused by Rules offline approvals, audit batch signatures, Privacy offline consent (⚑A17) |
| **YFex.Persistence** | `IKeyValueStore.SetAsync(string, ReadOnlyMemory<byte>)` overload (removes the per-entry `byte[]`); `SqliteConnectionFactory` durability option (`synchronous=FULL`); fix `IDurableQueue<T>` semantics (peek/ack) or document it as lossy |
| **YFex.Data** | `ITransactionParticipant` (05 §2.2); before-image dialect capability (04 §3.2) in stage 2; generated `IDataRow<T>` with `UpdateAsync(before, after)` overloads |
| **YFex.EventSourcing** | `EventMetadata` on `EventRecord` (04 §5); Marten metadata mapping; projection "who changed" reads from audit, not by replaying |
| **YFex.Cqrs** | Remove the no-op `.Telemetry(...)` stub (superseded by logging `.Logging` and audit `.Audit`); `ICommandExecutedObserver` → typed generic overload |
| **YFex.Messaging.Rpc** | `CallContext.RealSubject`/`Relation`/`SessionId`/`DeviceId`; audit `Sync.*` facts in `ISyncFailureLog`; never evict on outbox overflow without an audit fact |
| **YFex.Web** | Impersonation middleware enters `ActorContext`; add **user** impersonation ("act as") with the RFC 8693 `act` claim as an explicit, audited feature |
| **YFex.Jobs** | Rename `AuditEntry` family (⚑A28); governance facts to `IAuditTrail`; the Hub store keeps its operator view |
| **YFex.Rules / Rules.Admin** | `ApprovalIssued` facts; decisions into `AuditScope`; `IRuleAuditSink`/`IDecisionAuditSink` obsolete |
| **YFex.Payment** | `.Audit()` on its events; `Payment.WebhookRejected` security fact |
| **YFex.Fiscal / NFe** | Two-phase audit on emission/cancellation/inutilização/CC-e; store the SHA-256 of the signed XML in the entry; status history comes from audit instead of overwritten lifecycle columns |
| **YFex.Automation** | `AuditingStepObserver` for agent tool calls (actor kind `Agent`, relation `AgentOnBehalf`) |
| **YFex.Security** | Admin actions (interception on/off, baseline reset, capture export) to `IAuditTrail`; keep telemetry in its own purgeable store |
| **YFex.CustomLogic / Capabilities** | Publishing/activating tenant-authored logic is an `Administration` fact (which version ran is logging's) |
| **YFex.Web.Data credentials** | `Credentials.Created/Rotated/Deleted` with the credential **name** only |
| **YFex.Privacy** | Key scope per (subject, retention class) (08 §3); consent ledger on the shared chain primitive |
| **YFex.LoggY** | Audit explorer view (reads `AuditQueries`) as a separate module that only appears when the user has `audit.read` |
