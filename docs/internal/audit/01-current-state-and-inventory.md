# 01 — Current state, inventory and migration

Inventory taken on 2026-10-01 by grepping every repo under `C:\Dev\YFex` and `C:\Dev\YFex.Automation` for `Audit`, `Auditor`,
`AuditSink`, `Diff`, `Changes`, `History`, `Trail`, `Journal`, `Ledger`, `RunStore`, `Transition`, `Impersonat`, `Approval`,
`Outbox`, `DeadLetter`, `ChangedBy`/`ModifiedBy`. Generated (`obj/`, `Generated/`) and backup folders excluded.

## 1. What exists (audit-like seams)

| # | Seam | Location | What it does today | Shape / defects |
|---|---|---|---|---|
| 1 | `IDecisionAuditSink.OnFlaggedAsync(object message, object decision, ulong clientFingerprint, ct)` | `YFex/src/Cqrs/YFex.Cqrs/Runtime/RulesStage.cs` | Called only for **flagged** decisions (fingerprint mismatch accepted under `AcceptAndFlag`, offline device approvals) | `object` parameters: boxes, no typed capture, no actor/tenant/trace. Approved non-flagged overrides are **not** recorded anywhere. Optional (resolved from DI, absent by default) |
| 2 | `IRuleAuditSink` (`OnPublishedAsync(RulePublishAudit)`, `OnRejectedAsync(RuleDataKey, actor, Error)`) + `RulePublishAudit(RuleDataInfo, RuleDataDiff)` | `YFex/src/Validation/YFex.Rules.Admin/RuleAdminHandlers.cs` | Every accepted rule-data publish with its diff vs the base version, and every rejected attempt | Good content, no persistence of its own (app hook), not tamper-evident. Actor is `principal.Identity.Name` (display name, may be personal data) |
| 3 | `RuleDataDiff` / `RowChange` / `ValueChange` + query `RuleAdmin.Queries.GetDataChanges` | `YFex/src/Validation/YFex.Rules/Hosting/RuleDataDiff.cs`, `YFex.Rules.Admin/RuleAdmin.cs` | Row-level and value-level diff of canonical JSON payloads; editor "compare with published" | Correct and reusable. It is a **diff engine for rule payloads**, not an audit store (versions live in `IRuleStore`) |
| 4 | Approval tokens: `ApproveDecision` command, `OverrideToken` (server HMAC / device ECDsa), `IDeviceKeyStore`, `DeviceOverrideTokenIssuer` | `YFex/src/Validation/YFex.Rules/Approvals/OverrideTokens.cs` | Manager approval bound to one decision fingerprint, online or offline | **Issuing** an approval is not audited (only its *use* when flagged). Device key infrastructure is generic but lives in YFex.Rules |
| 5 | `ITenantImpersonationAuditor.RecordAsync(realUserId, targetTenantId)` + `NullTenantImpersonationAuditor` default | `YFex.Tenancy/src/YFex.Tenancy.Resolution/` ; called by `YFex.Web.AspNetCore/Tenant/TenantResolutionMiddleware.cs` | One record per HTTP request that impersonates a tenant | **No-op by default.** The commands executed during the impersonated request do not know a real actor exists. Over RPC (`CallContext`) impersonation is not modelled at all. User impersonation ("support acts as joao") does not exist |
| 6 | `IJobStore.AppendAuditAsync(AuditEntry)` with `AuditKind` (ScheduleCreated/Updated/Overridden, JobStarted…Deferred, PolicyDenied/Blocked, HubUnreachable…, PushStopRequested) + EF table `Audit` | `YFex.Jobs/src/YFex.Jobs.Abstractions/Store/IJobStore.cs`, `YFex.Jobs/Engine/JobDispatcher.cs`, `ScheduleManager.cs`, `YFex.Jobs.Persistence.EntityFrameworkCore` | Operational job history mixed with governance changes (schedule overrides) | **Name clash** with the future `AuditEntry`. Mixes run telemetry (started/completed: logging material) with governance facts (schedule overridden by whom: audit material). `Source` is a free string, no tenant, no trace |
| 7 | Hub `IHubStore.AppendAuditAsync(HubAuditEntry)` / `QueryAuditAsync`, `HubAuditKind` (PermissionGranted/Denied, Admitted/Revoked, HoldApplied/Lifted, CapSet, OrderSet, LaunchCommanded, KillCommanded…) | `YFex.Jobs/src/YFex.Jobs.Hub.Abstractions/Store/IHubStore.cs` | Governance audit of the job hub: "append-only, retained indefinitely" | Append-only by convention only; no chain; no actor kind. The governance kinds are genuine audit facts |
| 8 | `IJobObserver` (OnClaimed … OnDeadLettered) | `YFex.Jobs.Abstractions/Observability/IJobObserver.cs` | In-process observer (struct context) | Telemetry seam; belongs to logging (04 §5 of the logging design) |
| 9 | `ICommandExecutedObserver.OnExecutedAsync(object command, object? result, ct)` | `YFex/src/Cqrs/YFex.Cqrs/ICommandExecutedObserver.cs`, called by `LocalDispatcher` | Post-success hook used by `YFex.Cqrs.Compensation` | `object`, success-only, client-side. Not an audit seam (wrong guarantees) |
| 10 | `ICommandJournal` / `CommandJournalEntry` (MemoryPack, `journal:{Seq:D19}`) | `YFex/src/Cqrs/YFex.Cqrs.Compensation/` | Functional LIFO journal of reversible commands (undo) on the device | Functional, not audit: stores assembly-qualified type names + JSON payloads, marks `Reversed` (mutable). Keep as is |
| 11 | `ISyncFailureLog` / `SyncFailure` (+ `AcknowledgeAsync`, `RetryAsync`), `OutboxOptions.MaxEntries` overflow → `"outbox-overflow"` | `YFex/src/Messaging/YFex.Messaging.Rpc/ISyncFailureLog.cs`, `IOutbox.cs` | "Sync issues" screen: commands that never reached the server | **A user acknowledging a failure discards a business command** (e.g. an unsynced sale) and nothing records who did it. Outbox overflow silently evicts the oldest commands |
| 12 | `IDeadLetterSink` / `DeadLetter` (+ `SyncFailureDeadLetterSink`) | `YFex.Orchestration/src/YFex.Orchestration/DeadLetter/`, `YFex.Orchestration.Rpc` | Dead-lettered saga/PM steps go to `ISyncFailureLog` | Operational (logging + alerts). The **human** retry/ack is the audit fact (same path as #11) |
| 13 | `PaymentStatusChanged`, `PaymentRefunded` events; `PaymentStateMachine`; `IPaymentStore.IsWebhookEventProcessedAsync` / `MarkWebhookEventProcessedAsync`; `WebhookSecurity` | `YFex.Payment/src/YFex.Payment.Public/Events.cs`, `YFex.Payment/Internal/*` | One event per effective status transition; webhook de-duplication | Facts exist as events but nothing makes them durable audit. Webhook signature failures are not recorded |
| 14 | Authorization: `AuthorizationResult` (`Allowed` / `Denied`), `PolicyEvaluator`, Rpc `CallAuthorization` (stage 2) | `YFex.Authorization/src/*`, `YFex/src/Messaging/YFex.Messaging.Rpc/CallContext/` | Denials become `Error.Unauthorized` results | No denial seam (correct: `IsAllowed` is also used for UI visibility, which is not an *attempt*). The choke point for audit is the message pipeline |
| 15 | `CallContext` (Auth, Scope, Culture, CorrelationId, Subject), `CallContextSignals` permission versions | `YFex/src/Messaging/YFex.Messaging.Rpc/CallContext/` | Ambient caller identity over RPC | Actor source for audit. Missing: real actor / relation, session id, device id |
| 16 | `IRunStore` (`StartRunAsync`, `AppendAsync(StepRecord)`, …), `PersistingStepObserver`, `AgentLoop` "audit via IRunStore", `AiToolHost` | `C:\Dev\YFex.Automation\src\YFex.Automation.Abstractions\Orchestration\IRunStore.cs`, `…Core\Ai\*` | Resumable run log; every AI tool call persisted | Functional (resume) and must stay. Not tamper-evident; no tenant; AI tool calls that change business data are not in a business audit |
| 17 | YFex.State.History: `UndoContext`, `UndoCaptureDelta` (boxed `object` old/new), `UndoHistoryEntry`, savepoints, navigation boundaries | `YFex/src/State/YFex.State.History/` | Client undo/redo with coalescing | Not an audit source (client-side, unsaved, forgeable). Useful for the "unsaved local edits" overlay only (04 §7) |
| 18 | `[LogChanges]` attribute | `YFex/src/State/YFex.State/Attributes.cs` | Marker "emits boxing-free structured log output" | Logging material (logging 12 makes it an alias of `StateLog(UserInput)`). Not audit |
| 19 | YFex.Persistence: `IKeyValueStore`, `IDurableQueue<T>` (`SqliteDurableQueue`), `NullDurableQueue`, `YFex.Persistence.Encryption` | `YFex/src/Persistence/*` | Local storage | `IDurableQueue<T>.DequeueAsync` **deletes** (no ack: a crash after dequeue loses items), items **expire** (`PurgeExpiredAsync`), SQLite runs `synchronous=NORMAL` (WAL may lose the last commits on power loss). **Unsuitable as an audit outbox.** The logging design's `DataDurableQueue<T>` does not exist |
| 20 | YFex.EventSourcing: `EventRecord(Id, Version, EventTypeName, Payload, At, GlobalPos)`, `IEventStore.AppendAsync/ReadAsync/ReplaceAsync`, Marten provider | `YFex.EventSourcing/src/*` | Append-only streams, offline sync/rebase | **No metadata** (actor, tenant, trace, causation). `ReplaceAsync` rewrites the local stream on rebase (fine for a cache, so local streams are not evidence) |
| 21 | YFex.Data: `RowSecurity*`, `TableCreator`, `TableVersionRepository`, `YFexConnection.StartScopedTransaction`, proposed `IBulkWriter` / `RowReader<T>` / stage-2 row shapes | `C:\Dev\YFex\YFex.Data\src\YFex.Data\*`, `docs\DATA-ACCESS-AND-BULK-WRITE.md` | SQL access | No update/delete API today (raw SQL), so no generic place to compute before/after yet. Stage 2 (generated `IDataRow<T>`, `UpdateAsync`/`MergeAsync`) is the hook. No transaction participant / before-commit callback exists |
| 22 | YFex.Security: `IEventRepository` (`InsertEventAsync`, `PurgeOlderThanAsync`), `EventPersister` | `YFex.Security/YFex.Security/Storage/*` | Network/process capture telemetry (purgeable) | Telemetry, not audit. **Admin actions** (start interception, reset baseline, export captures) are audit facts and are not recorded |
| 23 | YFex.Fiscal / NFe: `NFeEntity` (status Pendente→Assinada→Transmitida→Autorizada→Cancelada…), commands `EmitirNFeCommand`, `CancelarNFeCommand`, `InutilizarNumeracaoCommand`, `CartaCorrecaoCommand` | `YFex.Fiscal/src/YFex.NFe*` | Fiscal documents; signed XML is the legal record | Lifecycle columns are overwritten (no history). Emission/cancellation needs audit with 5-year retention and a **two-phase** intent (SEFAZ call is irreversible) |
| 24 | YFex.Devices `PolicyMode.Audit`, `IDevicePolicyEngine.Audit()`, `PolicyViolation` events | `YFex.Devices/src/YFex.Devices/Policy/*` | "Audit" = report-only policy mode (dry run) | Naming only. Blocked device events are security facts worth auditing (adapter, optional) |
| 25 | YFex.Web credentials: `ApiCredential`, `ISecretProtector`, `AesSecretProtector`, `DapperApiEndpointStore` | `YFex.Web/src/Web/*` | Per-tenant API credentials | Credential create/rotate/delete is not audited (should record the credential **name** only) |
| 26 | YFex.Profiling / YFex.System consent, `PrivacyScrubber` | `YFex.Profiling`, `YFex.System`, core `YFex.Consent` | Behaviour telemetry, consent | Owned by the YFex.Privacy design (consent ledger ⚑P9). Not audit |

Not found anywhere: a generic "who changed this entity" store, `CreatedBy/ModifiedBy` conventions (only `PaymentRecord`, `Brain.Entity`
have timestamps), any hash chain, any signed record other than override tokens.

## 2. Defects and gaps this design closes

| # | Gap | Closed by |
|---|---|---|
| G1 | Five audit sinks default to no-op; a fresh app records nothing | `AddYFexAudit()` registers adapters for every seam it knows (01 §3) |
| G2 | Real actor lost after impersonation; no impersonation over RPC | `ActorContext` in core + Web middleware + `CallContext.RealSubject` (02 §3, ⚑A26) |
| G3 | No durable local outbox suitable for audit | `AuditJournal` with ack cursor, no TTL, `synchronous=FULL`, group commit (05 §3) |
| G4 | No before/after for entity changes | `[AuditChanges]` + YFex.Data stage-2 hooks + ES events (04) |
| G5 | Approval issuance not recorded | `ApproveDecision` handler + device issuer write audit facts (01 §3.3) |
| G6 | Acknowledging a sync failure silently discards a business command; outbox overflow evicts silently | `Sync.FailureAcknowledged`, `Sync.FailureRetried`, `Sync.OutboxOverflow` facts (01 §3.8) |
| G7 | ES events carry no actor/trace | `EventMetadata` (04 §5, ⚑A11) |
| G8 | Payment and fiscal facts not durable | event audit configuration + two-phase intents (03 §4, 05 §4) |
| G9 | Nothing is tamper-evident | chains, signed anchors, day seals, verifier, WORM export (06) |
| G10 | `object` boxing in audit seams | typed `AuditDescriptor<T>` capture through the shared operation descriptors (03 §3) |

## 3. Keep / adapt / replace (⚑A27)

Rule of thumb: **functional stores stay** (they power product features: undo, resume, sync-issue screens, rule history). **Audit sinks
are replaced** by calls to the core contract (`IAuditTrail` / `AuditScope`), so no library references `YFex.Audit`. Interfaces owned by
low layers that cannot reference anything new (Tenancy) keep their interface and get an **adapter** shipped by `YFex.Audit`.

| Seam | Verdict | How | When (phase, 12) |
|---|---|---|---|
| `IDecisionAuditSink` | **Replace** | `RulesStage` writes an `AuditDecision` (rule set, version, fingerprint, verdict, flagged) and the `AuditApproval`s into `AuditScope.Current`. Flagged → entry flag `NeedsReview` + review queue query (09 §5). Interface marked `[Obsolete]` for one release; an adapter forwards old registrations | 4 |
| `IRuleAuditSink` | **Replace** | `RulePublisher` calls `IAuditTrail` (core) with action `Rules.Published` / `Rules.PublishRejected`, target `RuleData` key `"{ruleSet}/{kind}/{code}@{tier}"`, and maps `RuleDataDiff` rows/values to `FieldChange`s (`rows[{rowId}].{column}`). `[Obsolete]` one release | 4 |
| `RuleDataDiff` / `GetDataChanges` | **Keep** | Still the rule editor's "compare versions" engine; audit consumes its output (01 §3.2 below) | — |
| Approval tokens | **Adapt** | `ApproveDecision` handler and `DeviceOverrideTokenIssuer` record `Rules.ApprovalIssued` (approver, permission, fingerprint, kind, key id). Device-issued approvals land in the device journal (offline). `IDeviceKeyStore` moves to the shared device identity location (⚑A17) | 4 |
| `ITenantImpersonationAuditor` | **Keep interface, adapter** | `YFex.Audit` ships `AuditingTenantImpersonationAuditor` (records `Tenancy.ImpersonationStarted`), registered by `AddYFexAudit()`. The middleware **also** sets `ActorContext` so every entry in the request carries the real actor | 4 |
| Jobs `AuditEntry`/`AuditKind`/`AppendAuditAsync` | **Rename + forward** (⚑A28) | Rename to `JobHistoryEntry`/`JobHistoryKind`/`AppendHistoryAsync` (operational history stays in the jobs store). Governance kinds (`ScheduleCreated/Updated/Overridden`, `PolicyDenied`, `PushStopRequested`) are **also** written to `IAuditTrail` | 4 |
| Hub `HubAuditEntry` | **Adapt** | Keep the hub's queryable store (operators use it). Governance kinds forwarded to `IAuditTrail` with actor = the operator (`Source`) and category `Administration` | 4 |
| `IJobObserver` | **Keep** (logging) | Not audit | — |
| `ICommandExecutedObserver` | **Keep** | Not audit. Reversal (`ReverseExecutor`) dispatches a reverse **command**, which the `AuditStage` audits like any other | — |
| `ICommandJournal` | **Keep** | Functional undo journal | — |
| `ISyncFailureLog` | **Adapt** | `AcknowledgeAsync` → `Sync.FailureAcknowledged` (command type, idempotency key, reason), `RetryAsync` → `Sync.FailureRetried`, outbox overflow → `Sync.OutboxOverflow` (Critical). Written to the device journal | 4 |
| `IDeadLetterSink` | **Keep** (logging + alerts) | Human actions covered through `ISyncFailureLog` | — |
| Payment events / webhooks | **Adapt** | Payment's own configuration: `b.Events<PaymentStatusChanged, PaymentRefunded>().Audit(a => a.Category("payment"))` (03 §4). `WebhookSecurity` failures → `Payment.WebhookRejected` (Security category) | 4 |
| Authorization denials | **New choke point** | Cqrs/Rpc pipeline stage 2 result `Denied` recorded by `AuditStage` (03 §6) | 2 |
| `CallContext` | **Adapt** | + `RealSubject`, `ActorRelation`, `SessionId`, `DeviceId`; `CallContextAuditActorSource` (core contract `IAuditActorSource`) | 2 |
| `IRunStore` / AI agent loop | **Keep + adapter** | `AuditingStepObserver` (in YFex.Automation.Core, core contract only) records AI tool calls flagged destructive/costly: actor kind `Agent`, relation `AgentOnBehalf` of the triggering user, confirmation decision + confirmer | 4 |
| State.History | **Keep** (not a source) | UI overlay only (09 §4) | 7 |
| `IDurableQueue<T>` | **Keep** (not used by audit) | Audit uses `AuditJournal` (05 §3). Suggest fixing its `synchronous` mode separately (10 §3) | — |
| EventSourcing | **Adapt** | `EventMetadata` on `EventRecord`; generator writes entry id/actor/trace; audited aggregates list emitted events (04 §5) | 5 |
| YFex.Data | **Adapt** | `ITransactionParticipant` on `YFexConnection` (before-commit hook), stage-2 write hooks, dialect before-image capability (04 §3, 05 §2) | 3 (participant), 5 (hooks) |
| YFex.Security storage | **Keep** | Admin actions → `IAuditTrail` (Security category) | 4 |
| Fiscal / NFe | **Adapt (in the fiscal module)** | Fiscal configuration: `.Audit(a => a.Category("fiscal").TwoPhase())` on emission/cancellation/inutilização/CC-e; stores SHA-256 of the signed XML + access key, not the XML | 6A of validation / after 4 |
| Devices policy | **Optional adapter** | `Devices.Blocked` (Security) when a policy blocks a device | later |
| Web credentials | **Adapt** | Credential store writes `Credentials.Created/Rotated/Deleted` with the credential name only | 4 |

### 3.2 Rule publish: before / after (example of a "replace")

```csharp
// BEFORE — app must implement and register IRuleAuditSink, and persist it somewhere itself
public sealed class MyRuleAudit(IMyAuditTable table) : IRuleAuditSink
{
    public ValueTask OnPublishedAsync(RulePublishAudit audit, CancellationToken ct) => table.InsertAsync(audit, ct);
    public ValueTask OnRejectedAsync(RuleDataKey key, string? actor, Error error, CancellationToken ct) => table.InsertRejectedAsync(key, actor, error, ct);
}
services.AddSingleton<IRuleAuditSink, MyRuleAudit>();
```

```csharp
// AFTER (📦 framework, inside RulePublisher; references only YFex core YFex.Auditing)
var scope = audit.Begin(RulesAudit.Published);                        // IAuditTrail from DI; AuditDescriptor generated once
scope.Target(new AuditTarget("RuleData", key.ToString()));            // "desconto/Table/faixas@acme/loja7"
RulesAudit.WriteDiff(scope, diff);                                    // RowChange/ValueChange → FieldChange, no JSON re-parse
scope.Field("version", info.Version);
scope.Reason(note);                                                   // publisher's note, scrubbed by YFex.Privacy
await audit.CompleteAsync(scope, OperationOutcome.Ok, null, ct);      // same transaction as the IRuleStore append when it is SQL (05 §2)
```

The app writes nothing. Without `AddYFexAudit()`, `IAuditTrail` resolves to the no-op trail (one virtual call, no allocation).

### 3.3 Approval issued (new fact)

```csharp
// 📦 framework, ApproveDecision handler (server) — DeviceOverrideTokenIssuer does the same into the device journal
var scope = audit.Begin(RulesAudit.ApprovalIssued);
scope.Approval(new AuditApproval(token.ApproverId, token.Permission, ApprovalSource.Server, token.KeyId, token.Fingerprint, token.IssuedAt));
await audit.CompleteAsync(scope, OperationOutcome.Ok, null, ct);
```

## 4. Old → new mapping (quick reference)

| Old | New |
|---|---|
| `IDecisionAuditSink.OnFlaggedAsync(object, object, ulong)` | `AuditScope.Current?.Decision(in AuditDecision)` + `AuditEntryFlags.NeedsReview` |
| `IRuleAuditSink` | `IAuditTrail` in `RulePublisher` (actions `Rules.Published`, `Rules.PublishRejected`) |
| `ITenantImpersonationAuditor` | unchanged interface + `AuditingTenantImpersonationAuditor` + `ActorContext` |
| Jobs `AuditEntry`, `AuditKind`, `AppendAuditAsync` | `JobHistoryEntry`, `JobHistoryKind`, `AppendHistoryAsync` + governance facts in `IAuditTrail` |
| `log.Audit("…", $"…")` (logging 11 §6.2) | `[Audit]` partial method or `IAuditTrail.Begin(...)` (03 §7) |
| `.Logging(l => l.Audit())` (logging 04 §3) | `.Audit()` (03 §2) |
| `YFex.Logging.Audit` project | `YFex.Audit` repo |
| `audit.entries` sketch (logging 14 §3) | `audit.*` schema in 07 §2 (adds inbox, seals, targets, field changes, tombstones, keys) |

## 5. Alignment changes needed in the other designs (for their owners)

These designs are owned by parallel sessions. This file does not edit them. It lists what they should change when they confirm
their decisions:

| Design | Change | Why |
|---|---|---|
| Logging 02 §1 | Remove `LogChannel.Audit` (keep `Diagnostic`, `Metric`, `Control`) | Audit is a separate library (✅ L20) |
| Logging 02 §3, 04 §3 | Rename `LogWriter` → `CaptureWriter`, `ILogValue<T>.Write(…, ref LogWriter …)` → `ref CaptureWriter`. `OperationDescriptorFlags.Audit` documented as "audited by YFex.Audit" | One generated capture serves both engines (⚑A3) |
| Logging 04 §3, 06, 11 §2.3 | `.Logging(l => l.Audit())` → `.Audit()`; `[Audited]` documented in the audit design | Configuration ownership |
| Logging README / 10 | Drop `YFex.Logging.Audit` and Phase 5 "Audit"; logging receives `audited #id` marks (09 §6) | Superseded |
| Logging 05 §3 | Spool "Audit lane" disappears; Audit/`≥ Error` priority becomes `≥ Error` only | Audit ships through its own journal |
| Logging 07 | Already points here; §1–§5 superseded | — |
| Logging 14 §3 | `audit.*` tables move to 07 §2 of this design | Single owner |
| Privacy 07 (rights) | `IPersonalDataStore` participant list: "Logging audit" → "YFex.Audit" | Participant owner |
| Privacy 05 (keys) | Provide key scope per **(subject, retention class)** or per **(subject, purpose)**, so a marketing erasure does not shred fiscal audit under legal hold (08 §3) | Erasure vs legal hold |
| Privacy ⚑P9 (consent ledger) | Reuse the core `CanonicalWriter`/`Hash256`/`HashChain` (⚑A31) for the append-only ledger and offline device signatures | One primitive |
| Privacy 08 (integrations) | "Audit" row: audit consumes classification, pseudonymizer, `AesGcmEnvelope`, subject keys, legal holds; it does **not** re-implement any | No duplication |
| YFex.Data adapter design | Add `ITransactionParticipant` (before-commit hook) and a dialect capability for before-images (`RETURNING OLD` / `OUTPUT DELETED`) to stage 2 | Transactional audit + diffs (04 §3, 05 §2) |
