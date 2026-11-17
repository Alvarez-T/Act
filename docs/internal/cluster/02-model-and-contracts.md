# 02 — Model and contracts

All names are proposals pending ⚑CL1–⚑CL26. Types are `public` only where an adopter or an engine must name them; everything else
is `internal`. No YFex core type is added except the optional `InstallationId` link on `NodeIdentity` (⚑CL4).

## 1. Node

```csharp
// (sketch) YFex.Cluster
/// <summary>One process start that joined the cluster. A restart is a new node (⚑CL3).</summary>
public readonly record struct ClusterNodeId(Guid Value) : ISpanFormattable, IUtf8SpanFormattable, ISpanParsable<ClusterNodeId>
{
    public static ClusterNodeId New(TimeProvider time) => new(Guid.CreateVersion7(time.GetUtcNow()));
}

/// <summary>Closed set owned by the library.</summary>
public enum ClusterNodeState : byte { Joining = 1, Ready = 2, Draining = 3, Left = 4 }

public sealed record ClusterNode(
    ClusterNodeId Id,
    NodeIdentity Identity,                    // core; App, Version, Host, ProcessId, Kind…
    ClusterNodeState State,
    ImmutableArray<ClusterFeature> Features,  // feature tags this binary understands (06 §3)
    int Weight,                               // placement weight, default 100 (a smaller VM can say 50)
    DateTimeOffset LastSeen);                 // database clock (05 §4)
```

- **Core change (⚑CL4):** `NodeIdentity` gains `InstallationId? Installation = null`. On-premises central fills it from
  `LocalInstallation` (its `Central` installation); cloud processes leave it null; desktop apps may fill it for logging. Owner of the
  record: core + YFex.Logging (the `.ylog` header writes it; a new optional field needs the format's version rule, request 10 §2).
- Node state transitions: `Joining` (connected to the engine, not taking work) → `Ready` (own checks pass, features compatible,
  clock within bounds) → `Draining` → `Left`. A node that misses heartbeats is shown as **gone** by the others (derived, not stored).

## 2. Node list

```csharp
// (sketch)
public interface IClusterMembership
{
    ClusterNode Self { get; }
    ClusterView Current { get; }                         // immutable snapshot: ready nodes, draining nodes, version, features
    event Action<ClusterView>? Changed;
}
```

- In-memory engine: the view holds one node. SQL engine: `cluster.nodes`, heartbeat 5 s, gone after 15 s (⚑CL14).
- The node list is **advisory**: placement, admin view, minimum version, metrics. Correctness never depends on it (a node that
  thinks it is alone still needs the lease).

## 3. Lease and fencing token

```csharp
// (sketch)
/// <summary>Grammar a-z 0-9 . - _ : / =, ≤ 200 chars. Kind + optional scope: "audit.sequencer:0=farmaviva/2026-11-10/standard".</summary>
public readonly record struct LeaseKey : ISpanFormattable, ISpanParsable<LeaseKey>
{
    public static LeaseKey Of(string kind);
    public static LeaseKey Of(string kind, TenancyPath scope);
    public static LeaseKey Of(string kind, TenancyPath scope, string part);
    public string Kind { get; }
    public TenancyPath? Scope { get; }
}

/// <summary>Grows by at least one each time the key changes holder (05 §2).</summary>
public readonly record struct FencingToken(ulong Value) : IComparable<FencingToken>, IComparisonOperators<FencingToken, FencingToken, bool>;

/// <summary>What the engine promises (⚑CL24).</summary>
public enum LeaseGuarantee : byte
{
    /// <summary>Exclusive, and stores can check the token in the same transaction (central engines).</summary>
    Fenced = 1,
    /// <summary>Usually exclusive; may overlap during a network split (site engine).</summary>
    BestEffort = 2,
}

public sealed record LeaseOptions(TimeSpan Duration, TimeSpan RenewEvery, TimeSpan SafetyMargin)
{
    public static LeaseOptions Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
}

public interface IClusterLeases
{
    LeaseGuarantee Guarantee { get; }
    /// <summary>Null when another node holds it. Never waits.</summary>
    ValueTask<Lease?> TryAcquireAsync(LeaseKey key, LeaseOptions? options = null, CancellationToken cancellationToken = default);
    /// <summary>Waits until acquired (wakes on the "released" notice, else polls at RenewEvery).</summary>
    ValueTask<Lease> AcquireAsync(LeaseKey key, LeaseOptions? options = null, CancellationToken cancellationToken = default);
    /// <summary>Read-only: who holds the key now (admin view, diagnostics).</summary>
    ValueTask<LeaseHolder?> FindAsync(LeaseKey key, CancellationToken cancellationToken = default);
}

/// <summary>Renewed in the background while held. Dispose = release + "released" notice.</summary>
public sealed class Lease : IAsyncDisposable
{
    public LeaseKey Key { get; }
    public FencingToken Token { get; }
    public LeaseGuarantee Guarantee { get; }
    /// <summary>Fires when the holder may no longer act: renewal failed past the safety margin, or the engine says another node took it.</summary>
    public CancellationToken Lost { get; }
    public bool IsHeld { get; }               // local view; false once Lost fired
    /// <summary>Fenced engines: checks the lease row inside the caller's open transaction (05 §2). Throws LeaseLostException.</summary>
    public ValueTask FenceAsync(YFexConnection connection, CancellationToken cancellationToken = default);   // in YFex.Cluster.Data
}
```

- `FenceAsync` is an extension in `YFex.Cluster.Data` (the main package does not reference YFex.Data). The in-memory engine's
  fence is a token comparison in memory.
- `LeaseLostException` is the only exception type the library throws on purpose; everything else is a result.

## 4. Single worker

```csharp
// (sketch)
public interface IClusterWorker
{
    /// <summary>Runs while this node holds the worker's lease. <paramref name="cancellationToken"/> fires on host stop, drain or lease lost.
    /// Return to give the work back at a safe point (the runner re-acquires later). Throw = logged, retried after a back-off.</summary>
    ValueTask RunAsync(ClusterWorkContext context, CancellationToken cancellationToken);
}

public sealed record ClusterWorkContext(LeaseKey Key, TenancyPath? Scope, Lease Lease, ClusterNode Node)
{
    /// <summary>True when the drain coordinator or placement asks the worker to hand over at its next safe point.</summary>
    public bool HandOverRequested { get; }
}

/// <summary>The owner of per-scope work says which scopes have work (⚑CL9).</summary>
public interface IClusterWorkScopes
{
    ValueTask<IReadOnlyList<TenancyPath>> ListAsync(CancellationToken cancellationToken);
}

// Registration (⚑CL8)
services.AddYFexCluster(cluster => cluster
    .Worker<DeliveryOrdersPoller>("delivery.poller")                                     // one in the cluster
    .WorkerPerScope<NotificationsDispatchWorker, OutboxScopes>("notifications.dispatch",   // one per tenant with pending work
        perScope => perScope.MaxConcurrencyPerScope(2).RequireGuarantee(LeaseGuarantee.Fenced)));
```

- `RequireGuarantee(Fenced)` makes registration fail on an engine that only offers `BestEffort` (the site, ⚑CL24): a startup
  error, not a silent run.
- The runner: acquire → `RunAsync` with a linked token → on return, release → back-off → acquire again. On `Lost`, the worker's
  token fires; the runner waits for it to return (bounded) before trying again.

## 5. Notices

```csharp
// (sketch)
/// <summary>Grammar like LeaseKey kinds: "authorization.all-changed", "licensing.changed".</summary>
public readonly record struct NoticeTopic(string Value);

public interface IClusterNotices
{
    /// <summary>Best effort, at most once, to every other node (and this one when <paramref name="includeSelf"/>). Payload ≤ 4 KB, no personal data.</summary>
    ValueTask PublishAsync(NoticeTopic topic, ReadOnlyMemory<byte> payload, bool includeSelf = false, CancellationToken cancellationToken = default);
    IDisposable Subscribe(NoticeTopic topic, Action<ClusterNotice> handler);
    /// <summary>Raised when notices may have been missed (listener reconnect, polling cursor behind the sweep): re-read your versions.</summary>
    event Action? Gap;
}

public readonly record struct ClusterNotice(NoticeTopic Topic, ClusterNodeId From, ReadOnlyMemory<byte> Payload);
```

Rule for every consumer (⚑CL12): a notice is a **hint**. The consumer keeps a durable version (or re-reads its state on a timer) so
a lost notice only delays, never loses, the change.

## 6. Health and drain

```csharp
// (sketch)
public interface IClusterHealth
{
    ClusterNodeState State { get; }
    bool IsReady { get; }                       // Ready and every IClusterReadinessCheck passes
    bool IsLive { get; }                        // the runner and heartbeat loops are responsive
    ValueTask DrainAsync(DrainReason reason, CancellationToken cancellationToken);   // idempotent; also triggered by host stop
    event Action<ClusterNodeState>? StateChanged;
}

public interface IClusterReadinessCheck { ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken); }

public enum DrainReason : byte { HostStopping = 1, Maintenance = 2, Upgrade = 3, Operator = 4 }

/// <summary>Things that must finish before the node leaves (open requests, WebSockets). Web.AspNetCore and Messaging.Rpc register them.</summary>
public interface IClusterDrainParticipant
{
    int Order { get; }                                      // requests 100, workers 200, leases 300, sockets 400
    ValueTask DrainAsync(DrainContext context, CancellationToken deadline);
}
```

## 7. Features (rolling upgrades)

```csharp
// (sketch)
public readonly record struct ClusterFeature(string Value);     // "audit.seal.v2", "licensing.activation-list.v2"

public interface IClusterFeatures
{
    /// <summary>True when every Ready node declares the tag, or the tag is latched. Once true, latches (06 §3).</summary>
    ValueTask<bool> IsOnEverywhereAsync(ClusterFeature feature, CancellationToken cancellationToken);
}
```

A library declares the tags its binary understands (`cluster.Understands("audit.seal.v2")`), usually from its own `Add…`.

## 8. Engine seam

```csharp
// (sketch) public: the site engine lives in Offline/Installations
public interface IClusterEngine
{
    LeaseGuarantee Guarantee { get; }
    ValueTask JoinAsync(ClusterNode self, CancellationToken cancellationToken);
    ValueTask HeartbeatAsync(ClusterNode self, CancellationToken cancellationToken);
    ValueTask<LeaseGrant?> TryAcquireAsync(LeaseKey key, ClusterNodeId holder, TimeSpan duration, CancellationToken cancellationToken);
    ValueTask<LeaseGrant?> RenewAsync(LeaseKey key, ClusterNodeId holder, FencingToken token, TimeSpan duration, CancellationToken cancellationToken);
    ValueTask ReleaseAsync(LeaseKey key, ClusterNodeId holder, FencingToken token, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ClusterNode>> ListNodesAsync(CancellationToken cancellationToken);
    ValueTask PublishAsync(NoticeTopic topic, ClusterNodeId from, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
    IAsyncEnumerable<ClusterNotice> ListenAsync(CancellationToken cancellationToken);
    ValueTask LeaveAsync(ClusterNodeId self, CancellationToken cancellationToken);
}

public readonly record struct LeaseGrant(FencingToken Token, TimeSpan Remaining);   // Remaining measured by the engine's clock
```

The runner, the drain coordinator, placement and the metrics are written **once** over this seam; engines only store and signal.
Counters (05 §6) and the duplicate filter (05 §7) are separate optional seams of `YFex.Cluster.Data`, not part of `IClusterEngine`
(the site engine does not offer them).

## 9. Metrics (Meter `YFex.Cluster`)

| Instrument | Kind | Dimensions |
|---|---|---|
| `yfex.cluster.nodes` | gauge | state |
| `yfex.cluster.leases.held` | gauge | kind (never the scope) |
| `yfex.cluster.lease.takeovers` | counter | kind, reason (expired, released, handover) |
| `yfex.cluster.lease.renew.failures` | counter | kind |
| `yfex.cluster.lease.lost` | counter | kind |
| `yfex.cluster.drain.duration` | histogram | reason |
| `yfex.cluster.notice.published` / `.received` | counter | topic |
| `yfex.cluster.clock.skew` | gauge (ms) | — |

No tenant dimension (cardinality, like Logging's `MeterExport`); per-tenant load goes to the hourly history ([07 §3](07-scaling-and-storage.md)).
