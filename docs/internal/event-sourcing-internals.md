# Event Sourcing Internals

Implementation notes for `YFex.EventSourcing` (sibling repo). Read the [design](event-sourcing-design.md) for
rationale and the [public guide](../public/event-sourcing.md) for usage. Everything here is AoT-clean and
reflection-free; the `YFex.EventSourcing.AotSmoke` sample enforces zero `IL2xxx/IL3xxx` via
`<IsAotCompatible>` + `<TreatWarningsAsErrors>`.

## Primitives

`StreamId`, `AggregateId`, `EventId`, `StreamVersion`, `GlobalPosition` are `readonly record struct`s. The three
embedded in the persisted `EventRecord` (`EventId`, `StreamVersion`, `GlobalPosition`) — and `AggregateId`, which
appears inside domain events — are `[MemoryPackable]`. `StreamVersion` is `ISpanFormattable` with a `D19`
zero-padded form so stream keys sort by version under an ordinal prefix scan.

## The aggregate contract

Three bases: `EventSourcedAggregateBase` (shared — identity, version, staged `Raise` integration events, `Ensure`,
snapshot restore; declares the serialization half of `IEventSourced` abstract), and two user bases deriving it —
`EventSourcedAggregate<TEvent>` (typed, `List<TEvent>` buffer, one `When(TEvent)`; for **union** and **marker
interface**) and non-generic `EventSourcedAggregate` (`List<object>` buffer, generated `Fold(object)` dispatching to
`When(TSpecificEvent)` methods; the **When-convention**). They are siblings, not a chain, to avoid an
`Emit(object)`/`Emit(TEvent)` overload clash (and the `(TEvent)boxedConcrete` cast a union would fail on).

The generator fills the serialization half into the aggregate's `partial`:

| Member | Who writes it | What it does |
|---|---|---|
| `Emit(TEvent)` | base (`protected`) | fold via `When`, buffer as uncommitted, bump `Version` |
| `Replay(TEvent)` | base | fold via `When`, bump `Version` (load path, no buffer) |
| `Ensure(bool, object)` | base (`protected static`) | throw `DomainException(error)` on a violated invariant |
| `TakeUncommitted()` | base (`protected`) | take + clear the typed uncommitted buffer |
| `StreamPrefix` | **generated** | the stream-key prefix (`[StreamKey]` or type name lower-cased) |
| `Drain()` | **generated** | switch the union → `CommittedEvent(name, MemoryPack bytes, boxed concrete)`, clearing the buffer |
| `ApplyRecord(name, bytes)` | **generated** | name `switch` → `MemoryPackSerializer.Deserialize<TCase>` → `Replay` |
| `EmitBoxed(object)` | **generated** | concrete `switch` → `Emit` (the functional `IEventStream.Append` path) |
| `PublishAsync(bus, obj, ct)` | **generated** | concrete `switch` → `bus.PublishAsync<TCase>` |

`CommittedEvent` carries **both** the wire form (name + `byte[]`) for the store and the boxed concrete event for
typed bus publication, so `SaveChanges` never re-(de)serializes.

### Why the union is never boxed on the hot path

The fold, `Drain`, `EmitBoxed`, and `PublishAsync` are all plain C# `switch`/`is` over the native `union`'s case
types or over the concrete boxed event — generated into the aggregate's own partial, where the case types are
visible. A `switch` *expression* (used in `Drain`) is compiler-checked for exhaustiveness. The session works only
against the non-generic `IEventSourced` (identity + `Drain`/`ApplyRecord`/`PublishAsync`) and `EventRecord`, so it
never touches the union type and never boxes a union value. (A boxed `union` reports its runtime type as the union
struct, not the case — so branch with `is`/`switch`, never `GetType()`/`BeOfType`.)

## In-house store — `KeyValueEventStore`

Over `IKeyValueStore`; also implements `ISnapshotStore`.

| Key | Purpose |
|---|---|
| `es:{stream}:{version:D19}` | one MemoryPack-serialized `EventRecord`; ordinal prefix scan == version order |
| `es-head:{stream}` | current `StreamVersion` (8 bytes, `BitConverter`) for optimistic concurrency |
| `es-snap:{stream}` | latest `SnapshotEnvelope` (folded state + version) |

- **`AppendAsync(stream, expected, events)`** reads the head; if it ≠ `expected` it throws
  `StreamConcurrencyException`; otherwise it writes each event key then the new head. A pure KV store has no
  cross-key transaction, so the append is *optimistic* (read → write → CAS head), correct for the single-writer
  client. The Marten provider appends a whole stream in one transaction — the reason it exists.
- **`ReadAsync(stream, from)`** lists keys under the `es:{stream}:` prefix, sorts them ordinally in a pooled
  `ArrayPool<string>` buffer (== version order), skips versions ≤ `from`, and yields deserialized records.
- **`ReplaceAsync(stream, canonical)`** deletes every event key and rewrites the canonical set + head — the client
  rebase step.

## Session — `InHouseAggregateSession`

- **`FetchForWritingAsync<T>`**: `new T()`, build `StreamId` from `T.StreamPrefix + "-" + id`, restore a snapshot
  if `T : ISnapshottable` and one exists (via the internal `IInternalSnapshotRestore` bridge that sets the private
  `Version`), then fold the tail via `ApplyRecord`. Tracks the stream with its **loaded** version.
- **`SaveChangesAsync`**: for each tracked stream, `Drain()` the aggregate; build `EventRecord`s at
  `loaded+1 … loaded+N`; `AppendAsync(stream, loaded, records)` (optimistic); then `PublishAsync` each committed
  concrete event to the `IEventBus`; then snapshot if the head crossed the cadence.
- Registered **transient** (one per unit of work). The session is `IAsyncDisposable` (Marten's is too); the
  in-house `DisposeAsync` is a no-op, so a scoped/transient session in a hosted app is disposed per request.

## Generator — `EventSourcingGenerator`

`netstandard2.0` incremental generator. Aggregates and projections are discovered **by type**, not by attribute — a
cheap syntactic pre-filter (partial class whose base list textually mentions `EventSourcedAggregate` / `IProjection`)
plus a semantic confirmation in the transform, keeping the `CreateSyntaxProvider` path nearly as incremental as
`ForAttributeWithMetadataName`. Handlers and translators still use `ForAttributeWithMetadataName`.

- **Aggregate (derives `EventSourcedAggregate[<T>]`)** → the aggregate partial. The generated members are the same
  five for the typed styles (`StreamPrefix`, `Drain`, `ApplyRecord`, `EmitBoxed`, `PublishAsync`); the
  When-convention adds a generated `Fold(object)` and takes `EmitBoxed` from the base. Event-case discovery branches
  on the style: **union** → scan the union declaration for `[MemoryPackable]` types (nested fallback); **marker
  interface/abstract** → scan the assembly for `[MemoryPackable]` implementers; **When-convention** → the parameter
  types of the `When(T)` methods. Diagnostics: `YFES001` (not partial), `YFES003` (no event cases).
- **`[AggregateCommandHandler]`** → an `ICommandHandler<TCommand[,TResult]>`. Finds the static
  `Decide(TCommand, TAggregate)`; the result type comes from `ICommand<TResult>` (void → `ICommand`); the aggregate
  id is the command's single `AggregateId` property. Diagnostics: `YFES004`, `YFES005`.
- **Projection (implements `IProjection`)** → an `IProjectionModule` plus one `IAsyncEventRecipient<TEvent>` per
  `On(TEvent[, ct])` method (resolving the projection from DI per receive, subscribed `KeepAlive = true`).
- **`[EventTranslator]`** → a `GeneratedEventTranslatorRegistry : IEventTranslatorRegistry` whose
  `PublishTranslationsAsync` switches on the committed domain event's concrete type and calls each
  `Translate(TDomain) → TIntegration`, publishing the typed result (with a null-check for reference results). The
  session consults it at commit (design §9); no translator for a type ⇒ that domain event publishes only.
- **Registration** (`GeneratedEventSourcingRegistration`, combined pipeline): `AddGeneratedEventSourcing(this
  IServiceCollection)` registers the handlers (transient), projections (singleton) + `ProjectionHost`, the translator
  registry (singleton), and the snapshot `SnapshotPolicy`; the `LocalHandlerInvokerRegistryBuilder` overload adds
  each command to the reflection-free invoker registry so the existing `LocalDispatcher` routes to the ES handlers
  unchanged.

## Integration events at commit (design §9)

`SaveChangesAsync(notifyIntegration = true)`: after appending and publishing the domain events, unless suppressed the
session (1) invokes the translator registry for each committed domain event, and (2) flushes the aggregate's staged
`Raise` events. `Raise<T>` captures the concrete `T` in a typed publish delegate (`(bus, ct) => bus.PublishAsync(e)`)
so it is reflection-free and never fires on replay (only decisions call it). `notifyIntegration: false` publishes the
domain events but discards translators + staged events — for replay, import, or bulk seeding.

## CQRS integration

CQRS does **not** reference ES. A generated ES handler is an ordinary `ICommandHandler<>` discovered by the
existing generated registry, so the `LocalDispatcher` routes to it and it inherits validation, authorization,
resilience, invalidation, and the offline outbox. Because ES commands are usually top-level records (not nested in
an aggregate's `Commands` class), the `YFex.Messaging.Rpc` generator does not register them — the ES generator's
`AddGeneratedEventSourcing(builder)` overload does.

## Projections at runtime

`ProjectionHost` (an `IHostedService`) subscribes every registered `IProjectionModule` on start and disposes the
tokens on stop. In-house projections are eventually consistent off the committed-event bus; because
`IEventBus.PublishAsync` awaits async recipients, a projection is up to date by the time `SaveChangesAsync`
returns in-process.

## Sync

**Model A (command replay).** Inherited: an ES aggregate command is an `IQueueable` CQRS command, so the
`LocalDispatcher` enqueues it on the existing outbox when offline (`ModelASyncTests` verifies the enqueue) and the
`OutboxReplayer` replays it on reconnect, with `IConflictResolver`/`ISyncFailureLog` for conflicts/dead-letters —
no ES-specific transport. ES adds three event-level pieces:

- **`IEventInbox`** / `KeyValueEventInbox` — dedup by `EventId` under `esinbox:{eventId}` (TTL), the §9.1 server
  idempotency guard; two-phase (append → mark) so a lost race redelivers rather than drops.
- **`GlobalPositionSequence`** — a monotonic counter under `es-gpos` assigning `GlobalPosition`s (single-writer on
  the authoritative server, the same optimistic constraint as the store).
- **`StreamReconciler.AdoptCanonicalAsync(stream, canonical)`** — the client rebase: indexes canonical `EventId`s,
  rebuilds the local stream as `canonical` (adopted verbatim, with server version + `GlobalPosition`) followed by
  any local event whose id is *not* in the canonical set (still-pending, re-versioned, `GlobalPos = null`), then
  `store.ReplaceAsync`. Rejected commands are dropped by the conflict path, not here.

**`LocalEventIngestSink : IEventSyncClient`** is the in-house authoritative sink (and the reference the Marten
server sink mirrors): if the server head still equals the client's baseline it appends the pushed events —
skipping ids already in the inbox — assigning consecutive versions + global positions, and returns `Applied`;
if the head moved it returns `Rebased` with the canonical stream. `AddInHouseEventIngest()` registers the inbox,
sequence, sink, and reconciler.

**Model B (event shipping) — `StreamRebaser`.** Reads the local tail since the last known server version, pushes it
via `IEventSyncClient`, and reconciles the `SyncResult` union: **Applied** (no-op), **Rebased**
(`store.ReplaceAsync` with the server's canonical events), or **Conflicted** (write a
`SyncFailure { Category = "es-conflict" }` into the shared `ISyncFailureLog`).

## Marten provider

`MartenAggregateSession` wraps `IDocumentSession` behind `IAggregateSession`
(`Events.FetchForWriting`/`FetchLatest`, `SaveChangesAsync`); `DomainEventBridge<TEvent>` is a Wolverine cascading
handler forwarding committed events onto `IEventBus`. The project is opt-in, server-only, not AoT, and excluded
from `eventsourcing.slnx` (it needs the Marten/Wolverine packages). Aggregates must be registered as Marten
single-stream aggregations there — the in-house fold is used only by the in-house provider.

## Tests

`tests/YFex.EventSourcing.Tests` (31 xUnit tests): store append/read/version-order/concurrency/replace; session
decide→save→rehydrate and optimistic conflict; the three event styles (union, marker interface, When-convention)
fold + rehydrate; projection fold (direct + via the generated module off the bus); translator auto-fire + `Raise`
+ `notifyIntegration:false` suppression; snapshot cadence + restore; Model B sync Applied/Rebased/Conflicted with
dead-lettering; and Model A — inbox idempotency, ingest-sink version/global-position assignment + idempotent
re-send, reconciler adopt-by-`EventId` (no-conflict + diverged), the full push→ingest→adopt cycle, and an offline
`IQueueable` command riding the outbox. The AoT smoke exercises the full path (dispatcher → generated handler →
append → projection → snapshot → all three styles → translator/Raise) with warnings-as-errors.
