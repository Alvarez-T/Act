# 05 — Write path and durability

Principle A: **an audit entry is committed with the change it describes, or the action does not happen.** This file specifies how.

## 1. `AuditScope` lifecycle

```
Begin(descriptor)            Collect (any code in the async flow)                 Complete(outcome)
 ├ rent scope from pool       ├ AuditScope.Current.Updated(before, after)          ├ encode canonical bytes once (02 §9)
 ├ id = UUID v7               ├ RulesStage → Decision(), Approval()                ├ protect values per sensitivity (08 §2)
 ├ actor: ActorContext →      ├ handler → Target(), Field(), Reason()              ├ durable write (§2 server / §3 node)
 │  IAuditActorSource → System├ nested Begin → child scope (ParentId)              ├ node chain link (06 §3)
 ├ tenant: IScopeContext      └ [AuditChanges] writes from YFex.Data / ES          ├ logging mark when present (09 §6)
 ├ trace: TraceContext or new                                                      └ return scope to pool
 └ becomes AuditScope.Current (AsyncLocal)
```

- The scope is a **pooled sealed class** (async flows cannot carry ref structs). Its buffer is an `ArrayPoolBufferWriter<byte>` from YFex core;
  `ChangeWriter` is a ref struct over it, created per write call.
- Actor and tenant are resolved **at Begin**, so a handler that changes the ambient scope halfway (rare) does not change who did it.
- `TraceId` is taken from `TraceContext.Current` (logging) or generated once per root audited operation when logging is absent.
- `AuditScope.IsInstalled` (static bool, set by `AddYFexAudit`) lets generated hooks skip everything with one load when audit is not used.

`AuditDurability` (core) chooses the durable write:

```csharp
public enum AuditDurability : byte
{
    Default = 0,        // Transactional when a YFex.Data transaction is ambient on a server, else WriteThrough
    Transactional = 1,  // insert into the inbox inside the caller's DB transaction (§2); error at Begin if none is ambient
    WriteThrough = 2,   // durable (journal fsync or own DB transaction) before Complete returns
    TwoPhase = 3,       // durable intent at Begin, outcome at Complete (§4)
}
```

## 2. Server: the transactional inbox (⚑A13)

### 2.1 Flow

```
handler opens YFex.Data transaction ─┬─ UPDATE PRODUTO … RETURNING OLD.*  → AuditScope.Updated(before, after)
                                     ├─ (more writes, more changes)
                                     ├─ BeforeCommit participant: encode entry → INSERT audit.entries (+ targets, field changes) = inbox
                                     └─ COMMIT  → business rows and audit rows commit together, or neither does
sequencer (async, 06 §4) ── reads inbox in arrival order per chain → INSERT audit.seals (index, hash) ── anchors
```

- Business transactions **never** touch chain state (no "previous hash" row lock). They only insert inbox rows keyed by entry id.
- The inbox insert uses the business transaction's connection: one multi-row `INSERT` for the entry and its projections (07 §2).
  `IBulkWriter` is not used here (single rows; COPY cannot join RLS tables anyway).

### 2.2 The YFex.Data hook (proposal to the YFex.Data owner)

```csharp
// (sketch) YFex.Data — new, small: lets any library enlist work that must commit with the caller's transaction
public interface ITransactionParticipant
{
    ValueTask BeforeCommitAsync(YFexConnection connection, DbTransaction transaction, CancellationToken ct);
    void AfterCompletion(bool committed);                    // release pooled state; never throws
}
public sealed partial class YFexConnection
{
    public void Enlist(ITransactionParticipant participant); // no-op list when no transaction is active → caller falls back
    public bool HasActiveTransaction { get; }
}
```

The audit engine enlists the open scope the first time a change is recorded inside a transaction. When the handler commits, the entry is
finalized with outcome `Ok` (the commit succeeding *is* the success). `AuditStage` later sees the handler's result:

### 2.3 Outcome after commit (edge case)

If a handler commits and **then** fails (exception after commit, or returns an error result), the committed entry says `Ok` for changes that
did happen. The stage writes a small **amendment** entry (`AuditEntryFlags.Amendment`, `ParentId` = committed entry, the real outcome and
code) by write-through. History shows "changes committed; operation then failed: …". This is rare and never loses a fact.

### 2.4 No transaction on the server

Actions without a YFex.Data transaction (calls to other services, file I/O) use **WriteThrough**: the entry is inserted into the inbox in
its own short transaction **before** the result returns to the caller. If the database is unreachable, the entry goes to the server's
**local journal** (§3, same code as devices) and is drained to the inbox later; a Critical self-log/alert fires (`audit.spill`).

## 3. Nodes: the durable local journal

Devices (POS, desktop, mobile, browser) and servers without a reachable database write to `AuditJournal`:

```csharp
// (sketch) YFex.Audit — node journal over YFex.Persistence IKeyValueStore (SQLite, IndexedDB, file system)
public sealed class AuditJournal
{
    // keys: "audit:j:{nodeSequence:D20}" → canonical entry bytes (ordinal order = sequence order)
    //       "audit:head"                 → node chain head (sequence, hash)            (06 §3)
    //       "audit:cursor:received"      → highest sequence the server acknowledged as stored
    //       "audit:cursor:sealed"        → highest sequence the server acknowledged as sealed
    public ValueTask<ulong> AppendAsync(ReadOnlyMemory<byte> canonicalEntry, CancellationToken ct);   // returns node sequence after fsync
    public ValueTask<AuditJournalBatch> ReadFromAsync(ulong afterSequence, int maxEntries, CancellationToken ct);
    public ValueTask AcknowledgeAsync(ulong receivedThrough, ulong sealedThrough, CancellationToken ct);
    public ValueTask<long> SizeBytesAsync(CancellationToken ct);
}
```

| Requirement | How |
|---|---|
| **No loss on crash/power cut** | SQLite backend opened with `journal_mode=WAL; synchronous=FULL` (a dedicated `SqliteConnectionFactory` option; YFex.Persistence's default `NORMAL` can lose the last commits on power loss) |
| **No loss on dequeue** | Nothing is dequeued: entries are deleted only after the server acknowledges them **sealed** and the local retention elapsed (§3.2). Not `IDurableQueue<T>` (it deletes on dequeue and expires by TTL, 01 §1 #19) |
| **Throughput without one fsync per entry** | **Group commit**: one writer loop drains a bounded channel; all entries that arrived during the previous fsync go into the next SQLite transaction (≤ 64 entries or ≤ 2 ms). Each producer awaits a pooled `IValueTaskSource` completed after *its* batch is durable |
| **Ordering** | Node sequence assigned by the writer loop at append; gap-free by construction |
| **Browser (Blazor WASM)** | IndexedDB backend; durability is the browser's (documented weaker guarantee); ships aggressively when online |
| **Encryption at rest** | Optional `YFex.Persistence.Encryption` wrapping (shared terminals). Personal fields are already protected per 08 §2 |

### 3.1 Capacity and back-pressure (never drop)

- `MaxJournalBytes` (default 256 MiB on devices). At 80 %: Warning + logging alert hook. At 100 %: producers **wait** (bounded, default 5 s)
  for the shipper to free space, then fail with `AuditJournalFullException`. Under "no audit, no action" (§5) the action is refused with a clear
  error ("audit storage full — connect to sync"). Nothing is ever evicted.
- Expected sizes: 500 B/entry × 2 000 entries/day/POS ≈ 1 MiB/day: a POS can stay offline for months before the cap matters.

### 3.2 Local retention

Sealed entries stay on the device for `LocalRetention` (default 30 days) so the POS can show its own history offline (09 §7), then the
journal deletes them. Unsealed entries are never deleted.

## 4. Two-phase intent / outcome (⚑A14)

For irreversible external effects (SEFAZ authorization, payment capture, sending a bank transfer file), a crash between the external
call and the audit write would leave an effect with no record. Two-phase closes that window:

```csharp
// ✍ configuration (fiscal module)
b.Command<NFe.Commands.Emitir, ResultadoEmissao>().Audit(a => a.Category("fiscal").TwoPhase());
```

```
AuditStage: Begin → durable INTENT entry (flags Intent; action, actor, targets, captured fields) ──▶ handler calls SEFAZ
           Complete → OUTCOME entry (flags Outcome, IntentId = intent id, outcome + code + changes)
crash in between → the intent stays alone → server job "audit.intents.dangling" (after 15 min) lists it in the review queue as
                   "outcome unknown"; the app's reconciliation (e.g. SEFAZ status query) records the outcome later with the same IntentId
```

- The intent is written **before** the external call. If it cannot be written durably, the call is not made (§5).
- The history panel shows intent + outcome as one line; a dangling intent shows a warning badge (09 §5).

## 5. Failure policies (⚑A15)

| Moment | Durability | If the audit write fails | Rationale |
|---|---|---|---|
| Transactional (server, DB) | `Transactional` | the business transaction **rolls back**, the command fails (`Faulted`, code `AUDIT.UNAVAILABLE`) | no audit, no action — it is free here |
| Before an irreversible effect | `TwoPhase` intent | the effect is **not** started; command fails | same |
| Denial / rejection (nothing executed) | `WriteThrough` | the denial is still returned; entry spills to the in-memory spill ring + Critical self-log; retried until durable | refusing a refusal is pointless; never silently dropped |
| After a non-transactional effect already happened | `WriteThrough` | entry kept in the bounded **spill ring** (memory) and retried to the journal; Critical alert; on process exit the ring is flushed to a crash file (same mechanism as logging's crash hooks) | the fact must survive; the caller's result is unchanged |
| Device journal full | `WriteThrough` | action refused before it starts (§3.1) | the POS must sync |

The spill ring is the only memory buffer in the design, and it exists only for the failure path.

## 6. Shipping (nodes → server)

```csharp
// (sketch) YFex.Audit.Server message (CQRS, generated RPC contract via YFEX_RPC) — idempotent by entry id
public static partial class AuditIngest
{
    public static partial class Commands
    {
        public sealed partial record Submit(Guid NodeId, ulong FirstSequence, ReadOnlyMemory<byte> Entries /* framed canonical entries */,
            NodeChainHead Head, ReadOnlyMemory<byte> DeviceSignature) : ICommand<SubmitReceipt>;
    }
}
public readonly record struct SubmitReceipt(ulong ReceivedThrough, ulong SealedThrough, long ServerTimeTicks);
```

- `AuditShipper` (YFex.Audit.Client) reads the journal from `cursor:received`, sends batches (≤ 256 entries / 1 MiB), applies the receipt, and
  backs off exponentially while offline. It uses the app's existing Fusion RPC connection and `CallContext` (device credentials).
- **At-least-once**, deduplicated by entry id on the server (inbox primary key). A resent batch is harmless.
- The server verifies the **node chain continuity** and the **device signature** before accepting (06 §3). A batch that skips sequences is
  accepted (entries are evidence) and a **gap** record is opened (06 §6).
- Sealing is asynchronous; the receipt returns the highest sealed sequence so the device can show "sealed" badges and prune (§3.2).
- Not the logging spool: audit ships even when YFex.Logging is absent, and logging's spool keeps only diagnostics (01 §5).
- A store **relay** (LAN edge server forwarding for many terminals) is a later option: it forwards batches unchanged (signatures stay
  end-to-end), so the relay needs no trust.

## 7. Idempotency and duplicates

| Duplicate source | Protection |
|---|---|
| Shipper resends a batch | inbox PK = entry id |
| Server retries a transactional command after a deadlock (handler re-runs) | the rolled-back attempt's inbox rows rolled back too; the retry creates a new entry (it is a new attempt) |
| Offline command replayed twice by the outbox | Cqrs idempotency key: the second execution returns the stored result and is **not** re-audited (the stage sees the idempotent short-circuit) |
| Transactional entry + amendment | different ids, linked by `ParentId` |

## 8. Ordering, clocks, offline

- **Within a node:** node sequence (gap-free). **Within a chain:** sealing order (server). **For readers:** history is ordered by
  `OccurredAt` (producer time, corrected) then `NodeId`/`NodeSequence`.
- Device clocks are not trusted. The ingest computes a **clock offset** per batch from `SubmitReceipt.ServerTimeTicks` exchanges (same
  approach as logging 05 §5) and stores `occurred_at_corrected` next to the raw `occurred_at`. Both are kept; the raw value is what was hashed.
- Entries produced offline carry `AuditEntryFlags.Offline`. The chain they are sealed into is the day they **arrive** (06 §4), so a 3-day
  offline POS does not reopen closed days.

## 9. Cost budget

| Path | Budget | Notes |
|---|---|---|
| Non-audited message | **0 ns, 0 B** | no stage installed |
| `[AuditChanges]` write with audit not installed | 1 static load | `AuditScope.IsInstalled` |
| `Updated(before, after)` of a 10-field entity, 2 fields differ | ≤ 300 ns, **0 B** steady state | pooled buffer, typed writes |
| Encode a typical entry (1 target, 3 changes, 5 fields) | ≤ 3 µs, 0 B | canonical writer + `SHA256.HashData` into stack buffers |
| Device journal append | dominated by fsync (≈ 0.3–5 ms SSD/eMMC), amortized by group commit | **1 allocation**: the `byte[]` that `IKeyValueStore.SetAsync` requires (span overload suggested, 10 §3) |
| Server transactional insert | 1 multi-row `INSERT` in the existing transaction (≈ 0.2–1 ms) | ADO.NET parameters box at the driver boundary (documented by YFex.Data) |
