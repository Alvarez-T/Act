# 08 — Sync, reconciliation and conflicts

## 1. Directions

```
 terminal ──envelopes──► site broker ──envelopes (site sequence order)──► central
          ◄─receipts/findings/bundles─          ◄─receipts/findings/bundles─
 terminal ─────────────────── direct, when there is no site broker or it is down ──────────────────► central
```

The same envelope travels unchanged in every direction. Only receipts are added on the way.

## 2. The envelope (fixes F3, F4, F5; ⚑O16)

```csharp
// Before — what an outbox entry knows today (IOutbox.cs:9)
public sealed record OutboxEntry(Guid IdempotencyKey, string CommandTypeName, byte[] Payload, DateTimeOffset EnqueuedAt,
    int AttemptCount, string? LastFailure, DateTimeOffset? LastAttemptAt);

// After — the outbox stores an envelope; everything needed to replay faithfully is captured at creation time
// (sketch) YFex.Offline contract in core; canonical binary, signed by the installation's evidence key
public sealed record OfflineEnvelope(
    Guid IdempotencyKey,                     // created once; also sent as the "yfex-idempotency" header on ONLINE calls (Phase 0)
    InstallationId Installation, long InstallationSequence,          // gap-free per installation, persisted — replay order no longer depends on the wall clock
    string CommandType, ReadOnlyMemory<byte> Payload,
    ActorIdentity Actor, ActorIdentity? RealActor,    // from the session — not from whoever is connected at replay (F4)
    Guid Session,                            // the OfflineSession (05 §5) the actor was signed in with
    string Scope,                            // the scope at creation time — not the scope at replay (F4)
    OfflineProvenance Provenance,            // mode, coordinated by, snapshot version, trusted time, flags (05 §6)
    ImmutableArray<BundleReference> Used,    // (stream, version) of rule data and budgets the decision used
    TraceContext Trace,                      // logging: replay becomes a new trace with a Replay link (logging 05 §4)
    DateTimeOffset WallClock,
    ReadOnlyMemory<byte> Signature);

public readonly record struct SiteReceipt(Guid IdempotencyKey, InstallationId SiteBroker, long SiteSequence,
    ImmutableArray<CoordinationResult> Coordination,                 // budget reservations and unique claims the broker granted (06 §4)
    ReadOnlyMemory<byte> Signature);                                 // custody + place in the site sequence; the broker never executes app code
public readonly record struct CentralReceipt(Guid IdempotencyKey, ReceiptOutcome Outcome, int Findings, long ServerTimeTicks);
public enum ReceiptOutcome : byte { Applied = 1, AppliedWithFindings = 2, Rejected = 3, Duplicate = 4 }
```

- **Who is the caller at replay?** The connection belongs to the **installation** (installation assertion or mTLS), which holds the internal
  permission `offline.replay`. Central verifies the envelope signature and then enters `ActorContext` with
  `Actor = envelope.Actor` and `OfflineContext.Current = envelope.Provenance`. Ana's 10:00 sales stay Ana's even if Bruno is logged in
  at 14:00. Audit and logging read the same `ActorContext` (⚑Z3).
- The envelope is created **once**, at the first dispatch, even online. A call that ends "in doubt" is re-sent with the same key
  ([04](04-connectivity-and-failover.md) §4).

## 3. Central reconciliation pipeline

### 3.1 Steps per envelope

| # | Step | Failure → outcome |
|---|---|---|
| 1 | Verify the installation signature; the installation is enrolled and **was not revoked at signing time** (revocation time vs corrected decision time) | signed after revocation → `Rejected` + Critical finding. Before → continue, finding if the installation is revoked now |
| 2 | Deduplicate by idempotency key (`IIdempotencyStore`: key → outcome + result bytes, kept ≥ 30 days) | seen → `Duplicate` + stored result. No re-execution, no re-audit (audit 05 §7) |
| 3 | Installation sequence continuity | gap → open a **gap record**, continue. Fork (same sequence, different hash) → Critical finding "installation state rolled back" |
| 4 | Scope within the installation's site | outside → `Rejected` + High finding |
| 5 | Time: corrected decision time from the clock offset; plausibility vs `SinceLastAnchor` and arrival | `ClockRollback` flag → High finding |
| 6 | Session and actor: session issued by an enrolled installation of the site, within 12 h, user had a valid snapshot of the cited version | invalid → Fact: Critical finding / Request: `Rejected` |
| 7 | Authorization **as of** the decision time (cited snapshot) and **now** | denied as-of → Fact: High finding / Request: rejected. Allowed as-of, revoked now → Low finding ("revocation had not reached the site") |
| 8 | Approvals verified as of the decision time (06 §6) | invalid → Fact: High finding / Request: `ApprovalRequired` |
| 9 | Rules re-evaluated with the cited version (06 §8) | mismatch → Fact: finding with the difference / Request: `DecisionMismatchPolicy` |
| 10 | Budget accounting per cited budget version | over grant → High finding (cannot happen without tampering) |
| 11 | Execute: **Fact** → handler applies, then `IFactReconciler<T>` with the findings. **Request** → handler or `ConflictPolicy` as today | — |
| 12 | Receipt; findings to the review queue and to audit (`Security`/business category) | — |

### 3.2 State central needs for "as of" checks

- **Issued snapshots by version.** Central signs every snapshot it hands out, so it keeps `(subject, version, issued at, valid until,
  permission hash)` rows. "As of" = the cited version existed, was valid at the corrected decision time, and no revocation of the
  subject had been **delivered** to that installation before the decision.
- **Delivery receipts.** Every bundle download is acknowledged with the installation's current floors, so central knows what each installation had.
- **Permission change history** (`subject, version, changed at`) for `IPermissionDirectory.HeldAtAsync` (06 §6). Authorization's
  `IPermissionVersions` already counts versions; the store keeps timestamps.

## 4. Sync order when the link returns

| Step | Direction | Why first |
|---|---|---|
| 1 | handshake: installation assertion / mTLS, time exchange | time anchor for everything after |
| 2 | **download** revocations, manifest, then bundles newer than the installation's floors | a dismissed cashier must stop as early as possible |
| 3 | **upload evidence**: audit journal batches, logging Audit lane | evidence before effects |
| 4 | **upload envelopes**, installation/site sequence order, by lane: fiscal facts with legal deadlines → other facts → requests | deadlines; facts are irreversible |
| 5 | receive receipts and findings; the site broker relays them down to terminals | terminals can drop their backup copies |
| 6 | budgets: report consumption, return unused, receive new grants | — |
| 7 | terminals leave `Site` mode after the hysteresis window (04 §4) | — |

**Backlog size (medium pharmacy, 3-day outage):** 4 terminals × 2 000 sales/day × 3 days ≈ 24 000 envelopes × ~2 KB ≈ 48 MB, sent in
batches of ≤ 256 envelopes / 1 MiB. Target ≥ 200 envelopes/s reconciled per site, so about 2 minutes.

## 5. Conflicts — catalog with pharmacy examples

| Situation | Kind | Outcome |
|---|---|---|
| Call in doubt, re-sent | — | `Duplicate`, stored result returned, no finding |
| Customer address edited at the site and at central | Request, expected version | `ConflictPolicy.Escalate`. The UI shows both values (existing `IConflictResolver<T>` for custom merges) |
| The store sold the last 2 units offline while the e-commerce at central sold 1 | Fact | applied → stock −1 → `IFactReconciler<FinalizarVenda>` opens "conferir estoque" and notifies the e-commerce order owner (business choice, not library) |
| Price raised at central at 15:00; the offline store sold at the old price until 16:05 | Fact | Low finding: "vendido com tabela v41; v42 vigente desde 15:00 não havia chegado" |
| Cashier dismissed at central at 14:30; the site was offline since 14:00 and she sold until 16:00 | Fact | Low finding per sale ("revogação não entregue"), grouped in one review item |
| Same, but the site broker **had received** the revocation bundle and still let her sell | Fact | Critical finding against the site broker ("revogação ignorada") |
| Agreement credit consumed above the granted budget | Fact | High finding (tampering) |
| Offline approval by a supervisor whose permission was revoked before the approval | Fact | High finding |
| Till closing computed offline differs from central's recomputation | Fact | Medium finding with the difference |
| A terminal's provisional result differs from central's result for the same in-doubt envelope (e.g., price) | Fact | Medium finding. Central's result is the record, the terminal's receipt is kept as evidence |
| Store price override (06 §5) rejected at central, e.g. the manager had lost the permission before the change | Fact (the override and the sales) | the sales stay; one grouped High item; terminals return to central's price when the rejection reaches them |
| Same product's price changed at central during the outage | Fact | rule tiers decide: the store override wins in the store unless central sealed the price; sealed → terminals switch, sales get a Low finding |

**Identifiers:** offline-creatable entities use client-generated `Guid.CreateVersion7()` (BCL), so there is no id remapping (⚑O21,
confirmed 2026-10-14). Human-facing numbers (sale number, NFC-e number) come from per-installation series. Today's design does not need a
temporary-id → server-id table, which signed envelopes would not allow anyway (central cannot rewrite an id inside signed bytes).

Security rules for these ids (user question, 2026-10-14):

| Concern | Rule |
|---|---|
| A compromised terminal picks an id that already exists (another sale, another tenant's customer) to overwrite it | a **create** whose id already exists is a duplicate only when type, scope and content match (idempotency, Phase 0); anything else is refused with a High finding, **never** an overwrite. Ids are checked against the envelope's scope like every other field (06 §9) |
| The 48-bit timestamp in a v7 id comes from the device clock and can be forged | an id is **not a clock**: nothing orders, expires or trusts by the id's time. Order = installation/site sequence, time = the envelope's `TrustedInstant` |
| Guessing ids (IDOR) | an id is **not a secret** (74 random bits make guessing impractical, but no check may rely on that): every read and write is authorized as usual |
| The id reveals when a record was created (millisecond precision) | ids of personal data (customer, prescription) are not printed on receipts, QR codes or public URLs; a public reference uses its own token (Privacy) |
| Per-installation number series reveal volume and gaps | expected and required by fiscal rules (one NFC-e series per point of sale); voided numbers are Fiscal's concern |

**Causality across installations:** a customer registered on CAIXA-01 and sold to on CAIXA-02 during site mode is ordered by the **site
sequence**, because both went through the site journal. Central processes a site's envelopes in site-sequence order and a standalone
installation's envelopes in installation-sequence order. The installation sequence is still checked separately for gaps and forks.

## 6. Priorities and deadlines

`OfflineEnvelope` lanes are declared per message: `.Offline(o => o.RecordsFact().Lane(OfflineLane.Deadline))`. The fiscal module also
records the deadline itself: NFC-e contingency documents have a state-defined transmission deadline, and the fiscal module tracks it.
YFex.Offline only guarantees that `Deadline` lane envelopes and external transmissions are not stuck behind a 48 MB backlog. With
internet up but central down (situation 2b), fiscal transmission happens directly from the installation. It does not wait for central at all.

## 7. Findings and the review queue (⚑O17)

```csharp
// (sketch) YFex.Offline.Central
public readonly record struct FindingKind(string Name);         // library kinds (ClockRollback, RevokedActor, StaleRuleData, OverBudget,
                                                                // InstallationRollback, DuplicateApproval…) + app kinds from IFactReconciler
public enum FindingSeverity : byte { Low = 1, Medium = 2, High = 3, Critical = 4 }

public sealed record OfflineFinding(Guid Id, Guid IdempotencyKey, InstallationId Installation, string Scope, FindingKind Kind,
    FindingSeverity Severity, ActorIdentity Actor, DateTimeOffset DecidedAt, string Detail);

public interface IOfflineReviewQueue
{
    ValueTask AddAsync(OfflineFinding finding, CancellationToken cancellationToken);
    // grouped per (site, outage, kind, actor) for humans; resolution: Accept | Compensate | Escalate — audited
}
```

- Every finding is also an authorization or audit event (⚑Z17 sink). Critical findings raise an alert through logging's alert hooks.
- Grouping matters. A 3-day outage with a stale price table should produce **one** review item with 2 000 lines, not 2 000 items.
- The review UI is built with the Avalonia phase (after the reusable grid). Until then the queue is a query plus an export.
