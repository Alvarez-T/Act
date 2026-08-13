# YFex.Persistence

Local, offline-first persistence for YFex applications. It saves and restores
**application state** — view-model snapshots, small local documents, and durable
work queues — to whatever storage backend you plug in (file system, database,
in-memory for tests).

> **Scope:** this layer is about *app-state* persistence, not database access.
> Reading and writing your domain data through SQL lives in `YFex.Data`.
> `YFex.Persistence` is "remember where the user was and what they did while
> offline"; `YFex.Data` is "run queries against a database".

---

## Projects at a glance

| Project | Purpose |
|---|---|
| **YFex.Persistence** | Core abstractions + the `PersistenceService` orchestrator + `MemorySnapshotStore` for tests. No storage dependencies. |
| **YFex.Persistence.Generator** | Roslyn source generator. Emits `CaptureSnapshot()` / `RestoreSnapshot()` for classes with `[Observable, Persist]` properties. |
| **YFex.Persistence.FileSystem** | File-system backends: `FileSystemSnapshotStore` (async snapshots) and `FileSystemLocalStore` (sync small documents). Desktop/server. |
| **YFex.Persistence.Data** | Database-backed `DataDurableQueue<T>` built on `YFex.Data`. Engine-neutral (SQLite, Oracle, Postgres, …). |
| **YFex.Persistence.Tests** | Unit tests for the service, providers, and stores. |

---

## Three things this layer persists

The layer has three independent primitives. Pick the one that matches your need.

### 1. Snapshots — "restore the UI to where it was"

A **snapshot** is a versioned blob of a subsystem's state, saved on suspend and
restored on resume. Each subsystem is an `ISnapshotProvider`; the
`PersistenceService` fans a save/restore across all registered providers into an
`ISnapshotStore`.

```
PersistenceService ── save/restore ──▶ ISnapshotProvider (one per subsystem)
        │                                     ▲
        │                                     │ generated for [Persist] props
        ▼                              StateObjectSnapshotProvider<T>
   ISnapshotStore  ◀── SnapshotEnvelope (discriminator, version, bytes, timestamp)
   (FileSystem / Memory / IndexedDb)
```

### 2. Local documents — "read a tiny record synchronously at startup"

`ILocalStore` is a **synchronous** key→bytes store for small state that must be
read during construction (e.g. a consent record needed before any async context
exists). It is the sync counterpart to the async `ISnapshotStore`.

### 3. Durable queues — "keep work across restarts while offline"

`IDurableQueue<T>` is a persistent FIFO queue with TTL. Items survive process
restarts and drain in order once you're back online (e.g. a telemetry/outbox
queue). The store is domain-agnostic; you supply an `IQueueItemSerializer<T>`.

---

## Core types (YFex.Persistence)

| Type | What it is |
|---|---|
| `IPersistenceService` / `PersistenceService` | Orchestrator. Registers providers, drives `SaveSnapshotAsync` / `RestoreSnapshotAsync` / `ClearSnapshotAsync`. |
| `ISnapshotProvider` | Captures/restores one named subsystem. `Discriminator` (stable key) + `Version` (schema). |
| `ISnapshotStore` | Durable async key→bytes store for snapshots. |
| `SnapshotEnvelope` | MemoryPack record wrapping each snapshot: discriminator, version, data, captured-at ticks. |
| `IPersistableStateObject` | `CaptureSnapshot()` / `RestoreSnapshot()`. Implemented by generated code. |
| `StateObjectSnapshotProvider<T>` | Adapts any `IPersistableStateObject` into an `ISnapshotProvider` — no manual serialization. |
| `MemorySnapshotStore` | In-memory `ISnapshotStore` for tests/design-time. |
| `ILocalStore` | Sync key→bytes store for small local documents. |
| `IDurableQueue<T>` / `NullDurableQueue<T>` | Durable FIFO queue interface + no-op default. |
| `IQueueItemSerializer<T>` | Item ↔ string conversion for a durable queue. |
| `[Persist]` (in `YFex.State`) | Marks an `[Observable]` property for inclusion in the snapshot. |
| `[NeverPersist]` | Opt-out marker to exclude a property a convention would otherwise include. |

---

## How to use

### Snapshots for a StateObject (the common case)

**Step 1 — mark the properties.** On a `partial` `StateObject` subclass, add
`[Persist]` alongside `[Observable]`:

```csharp
public partial class EditorViewModel : StateObject
{
    [Observable, Persist] public partial string DraftText { get; set; }
    [Observable, Persist] public partial int    CaretPosition { get; set; }

    [Observable] public partial bool IsBusy { get; set; }  // NOT persisted
}
```

The generator emits `CaptureSnapshot()` / `RestoreSnapshot()` and makes the class
implement `IPersistableStateObject`. Only `[Observable, Persist]` properties are
serialized (via MemoryPack). Non-persisted observables are ignored.

> The class **must be `partial`**, or the generator raises **YFPER0002** (error).
> If a `[Persist]` property's type isn't MemoryPack-serializable, you get
> **YFPER0001** (warning) — add `[MemoryPackable]` or use a built-in type.

**Step 2 — register DI and a store.** In startup:

```csharp
services.AddYFexPersistence(sp =>
    new FileSystemSnapshotStore(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyApp", "snapshots")));
```

**Step 3 — register the provider** for each instance you want persisted:

```csharp
var editor = new EditorViewModel();
PersistenceService.Current.Register(
    new StateObjectSnapshotProvider<EditorViewModel>(
        editor, discriminator: "editor", version: 1));
```

**Step 4 — save/restore at lifecycle points:**

```csharp
await PersistenceService.Current.RestoreSnapshotAsync();  // on startup/resume
// ... user works ...
await PersistenceService.Current.SaveSnapshotAsync();     // on suspend/shutdown
```

You can also inject `IPersistenceService` instead of using the `Current` static.

### A hand-written provider (no generator)

Implement `ISnapshotProvider` directly when you want full control over the bytes:

```csharp
public sealed class CartSnapshotProvider : ISnapshotProvider
{
    private readonly Cart _cart;
    public CartSnapshotProvider(Cart cart) => _cart = cart;

    public string Discriminator => "cart";
    public int    Version       => 2;

    public ValueTask<byte[]?> CaptureAsync(CancellationToken ct = default)
        => ValueTask.FromResult<byte[]?>(
               _cart.IsEmpty ? null : MemoryPackSerializer.Serialize(_cart.Items));

    public ValueTask RestoreAsync(byte[] data, int storedVersion, CancellationToken ct = default)
    {
        if (storedVersion != Version) return ValueTask.CompletedTask;  // migrate or skip
        _cart.Load(MemoryPackSerializer.Deserialize<List<Item>>(data)!);
        return ValueTask.CompletedTask;
    }
}
```

**Semantics to know:**
- `CaptureAsync` returning `null` = "nothing to persist" → the existing stored
  snapshot is left untouched (no overwrite).
- On restore, a missing/unreadable/corrupt snapshot is **skipped silently** — the
  service swallows deserialization errors so one bad blob can't break startup.
- **Version mismatches are your responsibility** inside `RestoreAsync`
  (migrate or skip). `StateObjectSnapshotProvider<T>` skips on mismatch by default.

### Local documents (sync, at startup)

```csharp
ILocalStore store = new FileSystemLocalStore(appDataDir);

if (store.Exists("consent.json"))
{
    byte[] bytes = store.Read("consent.json")!;
    // ... parse synchronously, before async is available ...
}

store.Write("consent.json", JsonSerializer.SerializeToUtf8Bytes(consent));
```

Unlike the snapshot store, `FileSystemLocalStore` uses the **key verbatim** as the
filename (after sanitizing), so you can keep a real extension like `consent.json`.

### Durable queue (offline outbox / telemetry)

```csharp
IDurableQueue<TelemetryEvent> queue = new DataDurableQueue<TelemetryEvent>(
    connections: connectionFactory,          // YFex.Data connection factory
    serializer:  new TelemetryJsonSerializer(),
    ttl:         TimeSpan.FromDays(7),
    table:       "telemetry_queue");

await queue.EnqueueAsync(events, ct);          // while offline
// ... later, on reconnect ...
var batch = await queue.DequeueAsync(maxCount: 100, ct);  // FIFO, non-expired
await queue.PurgeExpiredAsync(ct);
int pending = await queue.CountAsync(ct);
```

`DataDurableQueue<T>` creates its table on first use and uses the connection's
`IQueryDialect` for engine-specific SQL (e.g. pagination), so the same queue runs
against any database `YFex.Data` supports. Use `NullDurableQueue<T>` as the default
when durable queueing is disabled.

> The `table` name is interpolated into DDL/DML — pass a **trusted constant**, never
> user input.

---

## Storage backends

| Store | Interface | Use for |
|---|---|---|
| `MemorySnapshotStore` | `ISnapshotStore` | Unit tests, design-time previews. Data lost on restart. |
| `FileSystemSnapshotStore` | `ISnapshotStore` | Desktop/server. Atomic write-then-rename; one `.snap` file per key. |
| `FileSystemLocalStore` | `ILocalStore` | Sync small documents; keeps caller's extension. |
| `DataDurableQueue<T>` | `IDurableQueue<T>` | DB-backed offline queue via YFex.Data. |
| *(IndexedDbSnapshotStore)* | `ISnapshotStore` | Blazor WASM (referenced by contract; provided elsewhere). |

Both file-system stores create their base directory on first write and sanitize
keys into safe filenames. `FileSystemSnapshotStore` writes to a `.tmp` file then
renames — crash-safe on most file systems.

---

## The source generator

**Triggers on:** classes with at least one property carrying both `[Observable]`
and `[Persist]`.

**Emits:** a `partial class` implementing `IPersistableStateObject` with
`CaptureSnapshot()` / `RestoreSnapshot()`. Each property is MemoryPack-serialized to
its own `byte[]`, and the array-of-arrays is wrapped once more — so adding/removing a
property is length-tolerant on restore (missing indices are simply left at their
current value).

**Diagnostics:**

| ID | Severity | Meaning |
|---|---|---|
| `YFPER0001` | Warning | `[Persist]` property's type may not be MemoryPack-serializable. Add `[MemoryPackable]` or use a built-in type. |
| `YFPER0002` | Error | Class has `[Persist]` properties but isn't `partial`. |

The generator is referenced as an **Analyzer** by `YFex.Persistence`, so it runs
automatically on any consuming project.

---

## DI reference

```csharp
// Register with a store factory (resolved from the container):
services.AddYFexPersistence(sp => new FileSystemSnapshotStore(path));

// Or with a pre-built store instance:
services.AddYFexPersistence(new MemorySnapshotStore());
```

`AddYFexPersistence` registers `PersistenceService` as a singleton, exposes it as
`IPersistenceService`, wires the static `PersistenceService.Current` facade, and
registers your `ISnapshotStore`. `PersistenceService.Current` throws until
`AddYFexPersistence` has run.

---

## Design notes

- **MemoryPack everywhere.** Snapshots and envelopes serialize with MemoryPack —
  fast, allocation-light, AOT-friendly. All projects target `net11.0` and are
  `IsAotCompatible`.
- **Fail-soft restore.** Corrupt or version-mismatched snapshots never crash
  startup; they're skipped. (Add logging in production — the service intentionally
  swallows these.)
- **Save is non-destructive.** A provider returning `null` from `CaptureAsync`
  preserves whatever was previously stored.
- **Thread-safe registration.** `PersistenceService` guards its provider list with a
  lock and snapshots it before iterating, so registration during save/restore is safe.
