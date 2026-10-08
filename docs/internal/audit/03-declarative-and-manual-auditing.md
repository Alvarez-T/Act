# 03 — Declarative and manual auditing

## 1. What the developer decides

| Decision | Where | Default if you do nothing |
|---|---|---|
| Which commands are audited | `.Audit(…)` in the aggregate configuration, or `b.Audit(…)` for the whole aggregate, or `[Audited]` | denials, impersonation, approvals, rule publishing, sync failures acknowledged, credentials, governance (adapters, 01 §3) |
| Which entities record field-level changes | `[AuditChanges]` once on the entity type (04) | none |
| Category / retention | `.Audit(a => a.Category(...))`; categories declared once with their retention class (02 §2, 08 §5) | `data-change` → class `standard` |
| Which member is the target | nothing: convention (§2.3); `.Target(c => c.X)` when the convention picks wrong | first entity-key member |
| Which member is the reason | `.Reason(c => c.Motivo)` | none |
| Which members are captured | nothing: same automatic, type-decided capture as logging (logging 04 §3); `.Ignore(c => c.X)` | all members, treated per sensitivity (08 §2) |
| Reads of sensitive data | `.Audit(a => a.Access())` on the query (§5) | not audited (diagnostic YFA0040 suggests it) |
| Irreversible external effect | `.Audit(a => a.TwoPhase())` (05 §4) | single entry after the outcome |
| Facts that are not a message | `[Audit]` partial methods or `IAuditTrail` (§7) | — |

## 2. CQRS commands

### 2.1 The configuration API (⚑A5)

```csharp
namespace YFex.Cqrs;

public sealed class AuditConfigurationBuilder<TMessage>
{
    public AuditConfigurationBuilder<TMessage> Category(string category);                          // app-declared (02 §2)
    public AuditConfigurationBuilder<TMessage> Target<TKey>(Func<TMessage, TKey> key);              // override the convention (§2.3)
    public AuditConfigurationBuilder<TMessage> Targets<TItem, TKey>(Func<TMessage, IEnumerable<TItem>> items, Func<TItem, TKey> key);
    public AuditConfigurationBuilder<TMessage> Reason(Func<TMessage, string?> text);                // free text, scrubbed
    public AuditConfigurationBuilder<TMessage> ReasonCode(Func<TMessage, string?> code);
    public AuditConfigurationBuilder<TMessage> Ignore<TMember>(Func<TMessage, TMember> member);     // the rare exception
    public AuditConfigurationBuilder<TMessage> TwoPhase();                                           // 05 §4
    public AuditConfigurationBuilder<TMessage> Durability(AuditDurability durability);               // 05 §2 (default: Transactional when possible)
    public AuditConfigurationBuilder<TMessage> RecordQueued(bool enabled = true);                    // client-side Queued entry (§6.2), default true
    public AuditConfigurationBuilder<TMessage> Access();                                              // queries only (§5)
}

// on CommandConfigurationBuilder<TCommand> / <TCommand, TResult>, QueryConfigurationBuilder<TQuery, TResult>, EventGroupConfigurationBuilder:
public CommandConfigurationBuilder<TCommand, TResult> Audit(Action<AuditConfigurationBuilder<TCommand>>? configure = null);

// aggregate-wide default (new on AggregateConfigurationBuilder<TAggregate>):
public AggregateConfigurationBuilder<TAggregate> Audit(Action<AggregateAuditBuilder> configure);
public sealed class AggregateAuditBuilder
{
    public AggregateAuditBuilder AllCommands();                  // every command of the aggregate is audited
    public AggregateAuditBuilder Category(string category);      // default category for the aggregate's audited messages
    public AggregateAuditBuilder Except<TCommand>();             // e.g. a high-frequency "heartbeat" command
}
```

The lambdas are **never executed and never compiled into `Expression`s**. The Cqrs generator reads them **syntactically** (same mechanism as
`.Validation`, D27, and logging's `.Logging`) and emits the `AuditDescriptor<TMessage>` (§3). At run time the builder only records
"audited = yes" plus the flags, which the generated descriptor already knows. If a lambda is not a simple member access the generator
reports YFA0010 and the setting is ignored (it never falls back to reflection).

### 2.2 Before / after

```csharp
// BEFORE (today): nothing is audited; apps hand-roll tables or implement IRuleAuditSink-like hooks per module.
public sealed class ProdutoConfiguration : IAggregateConfiguration<Produto>
{
    public void Configure(AggregateConfigurationBuilder<Produto> b)
    {
        b.Command<Produto.Commands.AlterarPreco, Result>()
            .Validation(v => v.Field(c => c.NovoPreco).Positive())
            .RequirePermission(Permissions.ProdutoPreco);
    }
}
```

```csharp
// AFTER: one line per command, or one line for the whole aggregate
public sealed class ProdutoConfiguration : IAggregateConfiguration<Produto>
{
    public void Configure(AggregateConfigurationBuilder<Produto> b)
    {
        b.Audit(a => a.AllCommands().Category("catalog"));                     // every Produto command

        b.Command<Produto.Commands.AlterarPreco, Result>()
            .Validation(v => v.Field(c => c.NovoPreco).Positive())
            .RequirePermission(Permissions.ProdutoPreco)
            .Audit(a => a.Reason(c => c.Motivo));                               // optional refinement
    }
}
```

The message records stay clean (YFex convention: configuration over attributes for CQRS). `[Audited]` on the record is accepted for apps
that prefer it and for messages without a configuration class, with the same effect as `.Audit()` with defaults.

### 2.3 Target convention (no attributes)

The generator picks the target(s) per message, in this order, and writes the result into `AuditSchema.verified.txt` (04 §2.4):

1. `.Target(...)`/`.Targets(...)` in the configuration.
2. The first member whose type is an **entity key**: a value object marked `[AuditEntity("Produto")]`, or whose type name ends with `Id` and
   implements `IRawValue<T>` (e.g. `ProdutoId` → entity type `Produto`), or a `[DataSubjectKey]` type (YFex.Privacy, e.g. `CustomerId`).
3. `[AuditChanges]` writes inside the handler add their own targets with changes (04), so a command without a key member still ends up
   with the right targets.
4. Nothing found → no target; info diagnostic YFA0011 (fine for "Caixa.Abrir"-style actions).

`[AuditEntity]` goes **once on the id struct**, never on members:

```csharp
[AuditEntity("Produto")]                                                     // only when the name convention doesn't fit
public readonly partial record struct CodigoBarras : IRawValue<CodigoBarras> { … }
```

## 3. `AuditDescriptor<T>` (generated, reuses the operation descriptor)

```csharp
namespace YFex.Auditing;

[Flags]
public enum AuditDescriptorFlags : ushort
{
    None = 0, TwoPhase = 1, Access = 2, RecordQueued = 4, Event = 8, Manual = 16,
}

public abstract class AuditDescriptor
{
    public AuditAction Action { get; }                     // = operation descriptor name unless [AuditName]
    public AuditCategory Category { get; }
    public AuditDurability Durability { get; }
    public AuditDescriptorFlags Flags { get; }
    public ReadOnlySpan<FieldSpec> Fields { get; }         // YFex.Diagnostics FieldSpec (name + sensitivity): same schema as logging
}

public sealed class AuditDescriptor<T> : AuditDescriptor
{
    /// <summary>The shared operation descriptor (name, kind, capture). Logging and audit use the same generated capture.</summary>
    public global::YFex.Diagnostics.OperationDescriptor<T> Operation { get; }
    public delegate void TargetsFunction(in T value, ref ChangeWriter writer);     // writes AuditTarget(s) with ChangeKind.None
    public delegate void ReasonFunction(in T value, out string? code, out string? text);
    public TargetsFunction? Targets { get; }
    public ReasonFunction? Reason { get; }
    public ulong IgnoredFields { get; }                    // bit per field index: removed from audit capture only (logging may still log them)
}

/// <summary>Static generic slot: the AuditStage finds the descriptor for TMessage with one static load.</summary>
public static class AuditDescriptorFor<T> { public static AuditDescriptor<T>? Descriptor; }
```

**Why reuse the operation descriptor:** the capture code (types → `CaptureWriter` calls, sensitivity per field, value objects via
`IRawValue<T>`/`IClassifiedData<T>`) is generated once and reviewed once. Logging writes it to its slot with log policies (hash/mask);
audit writes the same fields with audit policies (encrypt for subjects, 08 §2). The descriptor is emitted whenever a message is audited,
**even if** `<YFexInstrumentation>false</YFexInstrumentation>` turned logging instrumentation off.

**⚙ Generated** for `AlterarPreco` (inside the module initializer the Cqrs generator already emits):

```csharp
// (sketch) generated
global::YFex.Auditing.AuditDescriptorFor<Produto.Commands.AlterarPreco>.Descriptor =
    new global::YFex.Auditing.AuditDescriptor<Produto.Commands.AlterarPreco>(
        operation: global::YFex.Diagnostics.OperationDescriptorFor<Produto.Commands.AlterarPreco>.Descriptor!,
        action: global::YFex.Auditing.AuditAction.Of("Produto.AlterarPreco"),
        category: new global::YFex.Auditing.AuditCategory("catalog"),
        durability: global::YFex.Auditing.AuditDurability.Default,
        flags: global::YFex.Auditing.AuditDescriptorFlags.RecordQueued,
        targets: static (in Produto.Commands.AlterarPreco c, ref global::YFex.Auditing.ChangeWriter w)
            => w.Reference(new global::YFex.Auditing.AuditTarget("Produto", c.Id.ToString())),   // convention: ProdutoId → Produto
        reason: static (in Produto.Commands.AlterarPreco c, out string? code, out string? text) { code = null; text = c.Motivo; },
        ignoredFields: 0UL);
```

## 4. Events

Some facts are naturally **events** (YFex.Payment publishes `PaymentStatusChanged` once per effective transition). Auditing them at
publish time on the server keeps the producing library free of audit code:

```csharp
// YFex.Payment's own aggregate configuration (📦 framework, inside the Payment module)
b.Events<PaymentStatusChanged, PaymentRefunded>()
    .Audit(a => a.Category("payment"));         // target convention: PaymentId → "Payment"
```

- The audit record is written by the event bus publish path (`IEventBus.PublishAsync` choke point in YFex.Messaging, through the core contract)
  **on the node that publishes**, inside the publisher's transaction when there is one.
- Domain events of event-sourced aggregates are covered by 04 §5 (they *are* the change record); do not configure both.

## 5. Queries: access audit (⚑A6)

Reading health data, a customer's full profile, or a prescription history is itself a regulated fact (LGPD art. 37 records of
processing, sector rules for controlled substances). Access audit is **opt-in**:

```csharp
b.Query<Paciente.Queries.GetProntuario, ProntuarioDto>()
    .RequirePermission(Permissions.ProntuarioLer)
    .Audit(a => a.Access().Category("health"));
```

- One entry per execution: action `Paciente.GetProntuario`, category `access`/`health`, outcome, targets from the query's key members, **no
  result content** (only the result's target keys when the result shape exposes entity keys, capped at 50).
- Cached results served from the client cache are **not** re-audited (the access happened when the server answered). Offline cache reads
  on a device can be audited locally with `.Audit(a => a.Access().IncludeCacheReads())` (sketch; ⚑ open question 12 §Q9).
- **Diagnostic YFA0040 (info):** a query whose result shape contains `SensitivePersonal` members (YFex.Privacy classification) and is not
  access-audited. It is a nudge, not an error.

## 6. The Cqrs `AuditStage`

### 6.1 Position and cost

```
LoggingStage (logging, optional) → AuditStage → stage 2 authorization → validation → rules (3–4) → context rules (5) → handler
```

- Installed **per message type** at registry compile time only when `AuditDescriptorFor<T>.Descriptor` is set. Non-audited messages
  have no stage: **0 ns, 0 B**.
- It is outermost after logging so it sees denials (stage 2), validation rejections and rule rejections as outcomes.
- It opens the `AuditScope` (ambient), runs the inner pipeline, maps the result to `OperationOutcome` (same mapping as logging 04 §5:
  `Ok`, `Queued`, `Rejected(code)`, `Denied`, `Conflict`, `Cancelled`, `Faulted`), and completes the scope (05).
- Server side it lives in the `IHandlerInvoker` decorator chain (next to `ValidatingHandlerInvoker`); client side in `LocalDispatcher`.

### 6.2 Which node writes what

| Situation | Entry written by | Outcome | Notes |
|---|---|---|---|
| Online command executed on the server | server (transactional inbox, 05 §2) | real outcome | the client writes **nothing** (no duplicate) |
| Command executed locally (desktop/POS with local handlers, e.g. NFC-e in contingency) | the executing node (journal) | real outcome | shipped later |
| `IQueueable` command queued offline | client journal | `Queued`, flag `Offline`, `IdempotencyKey` = outbox key | only if `RecordQueued` (default true for audited commands) |
| Same command replayed on reconnect | server | real outcome, same `IdempotencyKey` | the history panel shows "requested offline 10:41 · executed 12:40" as one line (09 §2) |
| Replay permanently failed, user acknowledges | client journal | `Sync.FailureAcknowledged` entry (01 §3) | closes the story |
| Denied by stage 2 | executing node | `Denied` + code | never executes; written **before** returning the error |

### 6.3 Denials (⚑A25)

- Every **denied command** is audited (category `security`), audited or not by configuration: a denial is an attempted state change.
- Denied **queries** are audited only when the query is access-audited (otherwise logging records them).
- **Coalescing:** an attacker (or a buggy client loop) can produce thousands of identical denials. The stage folds identical
  `(actor, tenant, action, code)` denials inside a 60-second window into one entry with `AuditEntryFlags.Coalesced` and fields
  `count`, `firstAt`, `lastAt`. Nothing is sampled away: the count is exact and the window closes durably (05 §3).

## 7. Manual API

### 7.1 `IAuditTrail` (core contract)

```csharp
namespace YFex.Auditing;

public interface IAuditTrail
{
    /// <summary>False for the no-op trail (YFex.Audit not installed): callers can skip building expensive facts.</summary>
    bool IsEnabled { get; }

    /// <summary>Opens an ambient scope for one entry. Pooled; must be completed exactly once.</summary>
    AuditScope Begin(AuditDescriptor descriptor);

    /// <summary>Encodes and makes the entry durable per the descriptor's durability (05). Returns the entry id.</summary>
    ValueTask<AuditEntryId> CompleteAsync(AuditScope scope, OperationOutcome outcome, string? outcomeCode, CancellationToken ct);

    /// <summary>Typed one-shot record for descriptors with a capture (generated [Audit] methods call this).</summary>
    ValueTask<AuditEntryId> RecordAsync<T>(AuditDescriptor<T> descriptor, T facts, OperationOutcome outcome, CancellationToken ct);
}

public sealed class AuditScope   // (members used by producers; lifecycle in 05 §1)
{
    public static AuditScope? Current { get; }                     // AsyncLocal; null when nothing is being audited
    public AuditEntryId Id { get; }
    public void Target(in AuditTarget target);                     // reference, ChangeKind.None
    public void Created<T>(in T after) where T : IAuditChanges<T>;
    public void Updated<T>(in T before, in T after) where T : IAuditChanges<T>;
    public void Deleted<T>(in T before) where T : IAuditChanges<T>;
    public void Field(string name, long value);                    // + int, decimal, bool, Guid, DateTimeOffset, string overloads
    public void Field<T>(string name, in T value) where T : IRawValue<T>;
    public void Reason(string? code, string? text);
    public void Approval(in AuditApproval approval);
    public void Decision(in AuditDecision decision);
    public void Flag(AuditEntryFlags flags);
}
```

`NullAuditTrail` (core) is registered by default by every library that needs `IAuditTrail` (`TryAddSingleton`), so libraries can always
inject it.

### 7.2 `[Audit]` partial methods (preferred for repeated manual facts)

**✍ Dev writes**

```csharp
public static partial class CaixaAuditoria
{
    [Audit("Caixa.Sangria", Category = "cash")]
    public static partial ValueTask<AuditEntryId> Sangria(this IAuditTrail audit, CaixaId caixa, Money valor, string motivo,
        CancellationToken ct);
}

// call site
await audit.Sangria(caixa.Id, valor, motivo, ct);
```

**⚙ Generated** (YFex.Audit.Generator): an `AuditDescriptor<(CaixaId, Money, string)>` with the usual type-decided capture (`caixa` → target
by convention, `valor` via `IRawValue<Money>`, `motivo` recognised as the reason because its name matches the reason convention
`motivo|reason|justificativa`, reported in the snapshot), and a body that calls `RecordAsync`. No string building, no boxing.

### 7.3 Imperative (dynamic facts)

```csharp
var scope = audit.Begin(PrecosAuditoria.ReajusteEmMassa);      // descriptor generated from [Audit] on a parameterless method, or new AuditDescriptor(...)
foreach (ref readonly var item in itens.AsSpan())
    scope.Updated(in item.Antes, in item.Depois);              // Produto : IAuditChanges<Produto>
scope.Reason(null, motivo);
await audit.CompleteAsync(scope, OperationOutcome.Ok, null, ct);
```

## 8. Nested operations

- An audited command dispatched inside another audited command's handler gets its **own** entry with `ParentId` = the outer entry.
- `[AuditChanges]` writes attach to the **innermost** open scope.
- A non-audited inner command's entity changes attach to the outer audited scope (the outer action "caused" them).
- Scopes are strictly nested per async flow. A scope completed out of order (bug) is reported by YFA runtime self-log and the entry is still written.

## 9. Generator diagnostics (YFA0001–YFA0099, `YFex.Audit.Generator` + Cqrs generator `.Audit` parsing)

| Id | Severity | Meaning |
|---|---|---|
| YFA0001 | Error | `[AuditChanges]` / `[Audit]` type or method not `partial` |
| YFA0002 | Error | `[AuditChanges]` on a type without any capturable member |
| YFA0010 | Warning | `.Audit(...)` lambda is not a simple member access; setting ignored |
| YFA0011 | Info | audited message has no target (convention found no entity key) |
| YFA0012 | Error | category name not declared in any `[AuditCategories]` class of the compilation or its references |
| YFA0020 | Warning | member type not capturable for audit (no `IRawValue<T>`, primitive, value object or generated shape) — stored as `Omitted` |
| YFA0021 | Info | raw `string` member classified personal by convention (YFex.Privacy) — suggests the existing value object |
| YFA0030 | Warning | `[NotAudited]` on a member of a `fiscal`-like category message whose retention class has `RequireComplete = true` (08 §5) |
| YFA0040 | Info | query returns `SensitivePersonal` data and is not access-audited |
| YFA0050 | Warning | `[AuditChanges]` entity written through raw SQL in the same assembly (best-effort syntactic detection of `Execute("UPDATE <table>")` on a known table) |
