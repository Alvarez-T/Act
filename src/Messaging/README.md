# YFex.Messaging

The messaging stack is YFex's **communication layer**. It answers three questions an app-level developer keeps asking:

1. *"How do parts of my app talk to each other without referencing each other?"* → the **event bus**.
2. *"How do I show server data that stays fresh and survives going offline?"* → **live state**.
3. *"How do I call the server, keep working when the network drops, and sync later?"* → the **RPC / offline stack**.

Everything is attribute-driven and source-generated. You write intent (`[Subscribe<T>]`, `[Live]`), the generators emit the plumbing. No runtime reflection, AOT-safe.

---

## The projects at a glance

| Project | Purpose | You use it when… |
|---|---|---|
| **YFex.Messaging** | In-process event bus + `[Live]`/`[Subscribe<T>]` attributes + `MessagingHost` | Always — the base layer. |
| **YFex.Messaging.Generator** | Source generator for `[Live]` and `[Subscribe<T>]` | Automatic — referenced as an analyzer. |
| **YFex.Messaging.Fusion** | Backs `[Live]` with [ActualLab.Fusion](https://github.com/ActualLab/Fusion)'s reactive cache | You want computed-value caching + automatic invalidation. |
| **YFex.Messaging.Rpc** | Client/server RPC, offline outbox, client cache, sync status | Your app talks to a server and must work offline. |
| **YFex.Messaging.Rpc.Sqlite** | Durable storage backend (desktop/mobile) | You want the outbox/cache to survive process restarts. |
| **YFex.Messaging.Rpc.IndexedDb** | Durable storage backend (Blazor WASM) | Same, but in the browser. |
| **YFex.Messaging.Rpc.Encryption** | AES-GCM encryption decorator over any storage backend | Cached/queued data at rest must be encrypted. |
| **YFex.Messaging.Rpc.Wolverine** | Server-side handler/dispatch adapter for Wolverine | Your server uses Wolverine for message handling. |
| **YFex.Messaging.Tests** | Integration + compliance tests | Reference for real usage patterns. |

### How they stack

```
YFex.Messaging            (event bus, [Live], [Subscribe<T>])   ← start here
   └─ YFex.Messaging.Fusion   (reactive cache behind [Live])
        └─ YFex.Messaging.Rpc      (client/server, outbox, cache, sync)
             ├─ .Rpc.Sqlite        ┐
             ├─ .Rpc.IndexedDb     ├─ pick one storage backend
             ├─ .Rpc.Encryption    ┘  (optional decorator over the backend)
             └─ .Rpc.Wolverine     (server-side handler adapter)
```

Each higher layer is optional. A pure in-process app only needs `YFex.Messaging`.

---

## 60-second tour

**Publish/subscribe between components — no direct references:**

```csharp
public partial record OrderPlaced(int OrderId, decimal Total);

// Publisher (anywhere)
Event.Publish(new OrderPlaced(42, 99.90m));

// Subscriber — a ViewModel/StateObject
public partial class OrdersBadge : ViewModel
{
    [Observable] public partial int Count { get; set; }

    [Subscribe<OrderPlaced>]
    void OnOrderPlaced(in OrderPlaced e) => Count++;
}
```

**Show live server data that self-refreshes and works offline:**

```csharp
public partial class DashboardViewModel : PageViewModel
{
    // Generates: TotalCustomers, IsTotalCustomersLoading,
    //            TotalCustomersError, RefreshTotalCustomersAsync()
    [Live(PollMs = 5000)]
    private Task<int> TotalCustomersAsync(CancellationToken ct)
        => Customer.Queries.GetCount(ct);
}
```

**Wire it up in DI:**

```csharp
// In-process only
services.AddYFexMessaging();

// Full offline-capable client
services.AddYFexMessagingRpcClient(o => o.WebSocketEndpoint = new("wss://api.myapp.com/rpc/ws"))
        .AddYFexSqliteStorage()          // durable outbox + cache
        .AddYFexStorageEncryption();     // encrypt data at rest
```

---

## Where to read next

- [`docs/01-event-bus.md`](docs/01-event-bus.md) — publish/subscribe, targeting, groups, debounce/throttle, `MessagingHost`.
- [`docs/02-live-state.md`](docs/02-live-state.md) — the `[Live]` attribute, polling, staleness, dependencies, Fusion.
- [`docs/03-rpc-and-offline.md`](docs/03-rpc-and-offline.md) — client/server setup, outbox, client cache, sync status, conflict resolution.
- [`docs/04-storage-and-backends.md`](docs/04-storage-and-backends.md) — SQLite, IndexedDB, encryption, Wolverine.
