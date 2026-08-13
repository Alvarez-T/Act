# The Telemetry Pipeline (`YFex.System.Telemetry`)

## Why it exists

Shipping telemetry sounds trivial until you list what can go wrong: the network is down, the endpoint is slow, the buffer fills up, an event contains a URL you shouldn't ship, or a collection call throws and takes the app down with it. This pipeline handles all of that so callers only ever do two things: `Track(...)` an event, or `CollectSnapshotAsync(...)` the machine. Everything else — batching, scrubbing, retry, offline persistence — is internal and failure-tolerant.

**The cardinal rule, enforced everywhere: telemetry must never crash the host.** Every collection method and every ship path wraps its work in a catch that degrades gracefully.

## The flow

```
collector.Track(name, props)                 collector.CollectSnapshotAsync()
        │                                              │ (consent-gated per subsystem)
        ▼                                              ▼
   TelemetryEvent  ──►  TelemetryBatch (bounded Channel<T>)         SnapshotResult union
                              │  batch by BatchSize OR FlushInterval
                              ▼
                       PrivacyScrubber.Scrub  (hash/mask sensitive fields)
                              │
                              ▼
                   ITelemetryTransport.ShipBatchAsync ──► success ─► done
                              │
                          failure
                              ▼
                        IOfflineQueue  ──► drained & retried on next StartAsync
```

## `ITelemetryCollector` — the front door

```csharp
public interface ITelemetryCollector : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    void Track(string eventName, Dictionary<string, object?>? properties = null);
    Task<SnapshotResult> CollectSnapshotAsync(CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
}
```

- **`StartAsync`** resolves the device id (a hashed hardware fingerprint), drains any offline-queued events from a previous run, and starts the background batch processor.
- **`Track`** stamps the event with session id, device-id hash, timestamp, and *the consent state at collection time*, then enqueues it. Non-blocking; swallows exceptions.
- **`CollectSnapshotAsync`** walks every subsystem the current consent allows and returns a union (below).
- **`FlushAsync`** completes the channel and ships whatever remains (with a timeout). Also triggered automatically on a consent downgrade.

### What `Track` records

Each `TelemetryEvent` carries: `EventName`, `SessionId` (per-process GUID), `DeviceIdHash`, `Timestamp`, `ConsentAtCollection`, and your `Properties`. Recording the consent state *with* the event means you always know under what authority a given data point was gathered.

## Snapshots and the result union

`CollectSnapshotAsync` returns a union so callers must handle every outcome explicitly:

```csharp
public union SnapshotResult(PartialResult, Denied, Unsupported, CollectionError);

public record PartialResult(DataField[] Collected, CollectionError[] Errors);
public record CollectionError(string Subsystem, Exception Exception);
```

The normal result is `PartialResult`: a flat list of `DataField(Name, Value, Category)` plus a list of per-subsystem `CollectionError`s. This is deliberate — if the display query throws but everything else succeeds, you still get all the other fields **and** a record of what failed. One broken subsystem never fails the whole snapshot.

Which subsystems run is decided entirely by consent (see [`01-consent-and-privacy.md`](01-consent-and-privacy.md)):

| Consent granted | Subsystems collected |
|---|---|
| `Functional` | system (OS, arch, runtime, processors, app version) |
| `Analytics` | + locale, network, display, idle, behaviour, richer system metrics |
| `Full` | + installed software, active window, browser profiles |

Individual providers return `DataAvailability<T>` — `Available<T>` / `Denied` / `Unsupported` — so a provider that isn't supported on the host degrades cleanly instead of throwing.

## Batching (`TelemetryBatch`)

A bounded `Channel<TelemetryEvent>` decouples producers from the shipper:

- Flushes when it accumulates `BatchSize` events **or** `FlushInterval` elapses — whichever comes first.
- When the channel is full, behavior follows `DropOnFull`: `true` drops the **oldest** event (default), `false` blocks the writer.
- On startup it drains the offline queue first, then processes live events. On shutdown it does a final flush of anything remaining.

## Transport & offline queue

- **`ITelemetryTransport`** ships a scrubbed batch and returns `true`/`false`. Default `HttpTelemetryTransport` POSTs to `TelemetryOptions.Endpoint`.
- **`IOfflineQueue`** catches ship failures. If a batch can't be sent, it's persisted and retried on the next `StartAsync`. The default in `YFex.System` is `NullOfflineQueue` (no persistence); `AddYFexWindows` replaces it with `SqliteOfflineQueue`, which survives process restarts and purges expired entries on drain.

## Options

`TelemetryOptions`:

| Option | Default | Meaning |
|---|---|---|
| `Endpoint` | *(required)* | Ingest URL. |
| `AppId` | `""` | Logical app identifier. |
| `BatchSize` | `50` | Max events per shipped batch. |
| `FlushInterval` | `30 s` | Max time before a partial batch ships. |
| `HttpTimeout` | `5 s` | Per-request transport timeout. |
| `MaxQueueSize` | `5000` | Bounded channel capacity. |
| `DropOnFull` | `true` | Drop oldest vs. block when full. |
| `TransportSaltKey` | `"yfex.telemetry"` | Salt for the privacy scrubber's hashes. |

## Registration

```csharp
services.AddYFexTelemetry(o =>
{
    o.Endpoint = new("https://telemetry.myapp.com/ingest");
    o.AppId = "my-app";
});
services.AddYFexWindows();   // supplies IPlatformProvider + SqliteOfflineQueue
```

`AddYFexTelemetry` alone wires the consent store, transport, batch, and collector but leaves platform providers and the offline queue as null/placeholder implementations — a platform package (`AddYFexWindows`) fills those in.

## Rules of thumb

- Call `StartAsync` once at app startup and `FlushAsync`/`DisposeAsync` at shutdown.
- Always pattern-match the full `SnapshotResult` union — don't assume `PartialResult`.
- Add `AddYFexWindows` (or another platform package) or the offline queue is a no-op and providers return `Unsupported`.
- Name event properties conventionally so the scrubber recognises them.
