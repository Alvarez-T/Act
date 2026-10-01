# 07 — Audit, privacy & compliance

## 1. Audit is not "logs at a higher level"

| | Diagnostic | Audit |
|---|---|---|
| Purpose | Operate and debug the system | Prove who did what to which data, when, under which tenant, and whether it was allowed |
| Loss | acceptable (counted) | **never**: write-through spool, ack-based shipping, blocking when full |
| Sampling / rate limits | yes | **never** |
| Mutability | can be deleted by retention | append-only, **tamper-evident**, retention by law/contract |
| Content | technical (durations, stack traces) | business facts: actor, action, target, before/after, decision, approval |
| Readers | developers | tenant admins, auditors, fiscal inspection, support, and developers |

Both share the producer path, the record format and the shipping protocol (one stack). They differ in channel, durability and
storage.

## 2. The Audit channel (`YFex.Logging.Audit`)

### 2.1 Producers

- `[Audited]` operations (04 §3): the end record is duplicated into the Audit channel, with outcome, actor, tenant and captured fields.
- Adapters for the existing seams (interfaces stay, implementations forward):
  `IDecisionAuditSink` (rules decisions + applied adjustments), `IRuleAuditSink` (rule data diffs, publish, approvals),
  `ITenantImpersonationAuditor`, Authorization denials, Payment transitions, Jobs/Orchestration dead letters, logging-control
  grants (06 §4).
- Manual: `log.Audit("NFe.Cancelada", $"chave {chave} motivo {motivo}")` or `[Audit]` partial methods (generator, like `[Log]`).
- **Entity change audit (suggestion, 09 §2.2):** a Persistence/Data generator option that emits field-level before/after diffs
  for entities marked `[AuditChanges]`. YFex.State.History already computes deltas on the client side.

### 2.2 Entry shape

```csharp
// (sketch) consumer-side view; stored as a normal record on the Audit channel plus these fields
public readonly record struct AuditEntry(
    ulong ChainIndex, Hash256 PrevHash, Hash256 Hash,
    string Action,                 // operation name or explicit action code
    string Actor, string? RealActor, string Tenant,
    OperationOutcome Outcome, string? Code,
    TraceId TraceId,
    RecordFields Fields);          // captured / diff fields, already redacted/encrypted per §3
```

### 2.3 Tamper evidence (⚑L11)

- **Hash chain per `(top-level tenant, UTC day)`:** `Hash = SHA256(PrevHash ‖ canonical(entry))` (BCL `SHA256.HashData` on a
  pooled buffer). The node computes it at write-through time, and the server verifies it on ingest and **re-chains** into its
  own chain (node chains can have gaps across devices; the server chain is the legal one).
- **Anchors:** every N entries / 10 min the server signs `(tenant, day, index, hash)` with an ECDsa P-256 key (BCL) and stores
  the anchor separately (optionally exported daily to WORM/object-lock storage). Verification = recompute the chain between
  anchors.
- **Gaps** in a node's audit sequence create a `Critical` "audit gap" incident (08 §5).
- Alternative worth considering later: store the audit stream as an **event-sourced stream per tenant** in YFex.EventSourcing
  (Marten on the server). Events are already append-only with versions, projections give per-entity history, and the
  infrastructure exists.

## 3. Privacy: classification and redaction

### 3.1 Where values come from

Only captured fields (opt-in, ⚑L8), template holes, exception messages and principal/session ids can carry personal data.
Every one of those passes through the redactor on the **consumer thread, before any sink or spool write**. Nothing personal is
written in clear to disk or network unless policy says `Keep`.

### 3.2 Policies per `DataSensitivity`

| Action | Output | Use |
|---|---|---|
| `Keep` | value | Public / Internal |
| `Mask` | `***.456.789-**` (type-aware via `ILogValue<T>`; default keeps the last 2–4 chars) | Personal in support views |
| `Hash` | `#h:` + HMAC-SHA256(tenant key, value) truncated to 16 hex | joinable ("all operations of this customer") without revealing the value |
| `Encrypt` | AES-GCM with a **per-data-subject key** (§4) | reversible by authorised auditors, erasable |
| `Erase` | `<redacted:SensitivePersonal>` | default for anything unclassified that looks sensitive |

`Financial` and `Secret` are always `Erase`. Config cannot relax them, and the generator errors (YFL0030) when a `.Logging(...)` override
targets a member of those classes.

### 3.3 Exception and free-text scrubbing

Exception messages often embed values (SQL constraint messages with the key, validation messages with input, HTTP errors with
URLs and tokens). The consumer runs a scrubber (`YFex.Profiling`'s `PrivacyScrubber` patterns, moved/shared): CPF/CNPJ/e-mail/
phone/card/IBAN/JWT/bearer/API-key regexes (generated `[GeneratedRegex]` in the engine, compiled once), URL query stripping, and
IP masking. Values found are replaced per the `Personal` policy. This is defence in depth; the main defence is that we capture
ids and codes, not values.

## 4. LGPD (Brazil) and erasure versus immutability

LGPD gives data subjects the right to deletion (art. 18), but audit logs must be immutable. **Crypto-shredding** resolves the
conflict:

- Fields classified `SensitivePersonal`/`Personal` that must stay **reversible** in audit are encrypted with a key per data
  subject (e.g. per customer id), stored in a server-side key vault table (keys encrypted with a master key, BCL AES-GCM).
- An erasure request **deletes the subject's key**. Audit entries and hash chains stay intact (the ciphertext is unchanged),
  but the personal data is now unrecoverable. The erasure itself is audited.
- `Hash` values become unlinkable by rotating the tenant HMAC key, when required.

Also covered: purpose limitation (diagnostic channel never stores personal data in clear), data residency (tenant → region
mapping on the server, 08 §3), access logging (every audit read is itself audited), and retention per category (§5).

## 5. Retention (defaults; per-tenant plan can extend)

| Data | Hot (searchable) | Warm | Archive | Notes |
|---|---|---|---|---|
| Diagnostic Debug/Trace (incl. retro) | 3 days | — | — | |
| Diagnostic Information+ | 14 days | 90 days (no Debug) | — | |
| Errors / issues | 90 days | 1 year (occurrence samples) | — | |
| Metrics roll-ups | 1 min for 7 d, 1 h for 13 months | | | |
| **Audit** | 1 year | 5+ years | WORM export | NF-e/fiscal documents: legal retention ≥ 5 years (prescription period); configure per module |

## 6. Consent

Operational logging and audit rely on **legitimate interest / legal obligation**, not consent. Behaviour data (clicks, active
windows, browsing) stays in YFex.Profiling under `ConsentLevel`. The logging engine links to it only by `session.id`, and the
server only joins the two when the session's consent allows it (consent state is shipped as a session attribute).
