# 03 — API and examples (dev side first)

The vendor runs **YFex Farma** central for **FarmaViva** (tenant `0=farmaviva`) and 300 other tenants in its cloud (scenario A). A
second customer, **Drogaria Boa Saúde**, runs central on its own Windows server with SQL Server (scenario B), later with a second
server for failover. Stores (Loja Centro: PC-GERENTE broker, CAIXA-01, CAIXA-02) come in [09](09-site-engine.md). All code is
`// (sketch)` until ⚑CL approval.

## 1. Composition roots

### 1.1 Vendor cloud: many pods, Postgres

```csharp
// ✍ YFexFarma.Central/Program.cs (ASP.NET Core, container, Kubernetes)
builder.Services.AddYFexData(data => data.UsePostgres(builder.Configuration.GetConnectionString("central")!));
builder.Services.AddYFexCluster(cluster => cluster
    .UseData()                                               // YFex.Cluster.Data: leases, nodes, notices (LISTEN/NOTIFY)
    .Node(node => node.Weight(100))
    .Drain(drain => drain.WaitForBalancer(TimeSpan.FromSeconds(5)).RequestBudget(TimeSpan.FromSeconds(20)))
    .Worker<VendorCheckInSweeper>("licensing.check-in-sweeper")
    .WorkerPerScope<ActivationListSender, PendingActivationScopes>("licensing.activations",
        w => w.RequireGuarantee(LeaseGuarantee.Fenced)));

builder.Services.AddYFexAudit(...).UseClusterSequencer();        // Audit adopts (10 §2)
builder.Services.AddYFexNotifications(...).UseClusterDispatch();  // Notifications adopts (10 §2)

var app = builder.Build();
app.MapYFexHealth();          // YFex.Web.AspNetCore over IClusterHealth: /health/live, /health/ready, /health/startup (10 §2)
app.Run();
```

### 1.2 On-premises central, one node (Boa Saúde, SQL Server)

```csharp
// ✍ The same Program.cs; configuration decides
builder.Services.AddYFexCluster(cluster => cluster.UseInMemory());   // the default when nothing is configured
```

The in-memory engine grants leases at once, keeps no timers beyond the workers themselves, writes nothing to the database and
publishes notices in-process. **One node costs nothing** (acceptance: `TryAcquireAsync` ≤ 100 ns, 0 B on the hot path after the
first acquire of a key; no background thread when no worker is registered).

### 1.3 On-premises central, two nodes (failover)

```json
// appsettings.json on both servers
{ "YFex": { "Cluster": { "Engine": "Data", "Database": "central" } } }
```

```csharp
builder.Services.AddYFexCluster(cluster => cluster.FromConfiguration(builder.Configuration.GetSection("YFex:Cluster")));
```

`Engine: Data` on SQL Server uses the same lease table; `Engine: Data` on **SQLite** refuses a second node on another machine at
startup (05 §8.3).

## 2. The delivery-orders poller: before and after

**Before** (licensing [01 §4](../licensing/01-api-and-examples.md)): fine on one node; on three pods **three pollers** fetch the same
iFood orders, and the marketplace's rate limit is hit three times as fast.

```csharp
public sealed class DeliveryOrdersPoller(ILicense license, IMarketplaceClient marketplace) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await license.WaitUntilAvailableAsync(YFexFarmaLicensing.Modules.Delivery, stoppingToken);
            await marketplace.PollAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
```

**After**: one poller per tenant in the whole cluster, moved when its node goes; the same class runs at a store on the site engine
in the last phases.

```csharp
// ✍ YFexFarma.Delivery/DeliveryOrdersPoller.cs
public sealed class DeliveryOrdersPoller(ILicenses licenses, IMarketplaceClient marketplace, TimeProvider time) : IClusterWorker
{
    public async ValueTask RunAsync(ClusterWorkContext context, CancellationToken cancellationToken)
    {
        TenancyPath tenant = context.Scope!.Value;
        while (!cancellationToken.IsCancellationRequested && !context.HandOverRequested)
        {
            await licenses.For(tenant).WaitUntilAvailableAsync(YFexFarmaLicensing.Modules.Delivery, cancellationToken);
            await marketplace.PollAsync(tenant, cancellationToken);          // orders are deduplicated by the marketplace's order id
            await Task.Delay(TimeSpan.FromSeconds(30), time, cancellationToken);
        }
    }
}

// ✍ registration
cluster.WorkerPerScope<DeliveryOrdersPoller, TenantsWithDelivery>("delivery.poller");   // scopes: tenants with an active integration
```

**Cons:**
- A takeover after a **crash** waits for the lease to expire (~35 s): one or two polls are late. After a **drain** the handover takes
  ~1 s.
- A poll in flight when the lease is lost (a paused pod) can still finish its HTTP call: the next holder may fetch the same orders.
  The **order id** must be the idempotency key when orders are stored (fencing protects writes to our database, not the marketplace).
- The worker must watch `HandOverRequested` and `cancellationToken`; a loop that ignores both is stopped by the runner after the
  drain budget (logged as `cluster.worker.forced-stop`).

## 3. The Audit sequencer: before and after

**Before** (audit 06 §4, design): a session advisory lock per chain.

```csharp
// (sketch of the audit design)
await using var connection = await data.OpenAsync(ct);
bool owner = await connection.ScalarAsync<bool>("select pg_try_advisory_lock(@k)", new { k = ChainKeyHash(chain) }, ct);
if (!owner) return;                                    // another server seals this chain
while (!ct.IsCancellationRequested) await SealBatchAsync(connection, chain, ct);   // must use THIS connection: the lock is on it
```

Limits: only Postgres; the lock lives on one pooled connection (a sealing statement sent on another connection is not protected); no
token; nobody can see who seals which chain; a stopping pod drops the lock only when its connection closes; chains are not spread
(the node that starts first may take every chain).

**After:**

```csharp
// ✍ YFex.Audit.Server/ChainSequencer.cs (Audit owns this code; Cluster only provides the lease)
internal sealed class ChainSequencer(IAuditSealStore seals, AuditServerOptions options, TimeProvider time) : IClusterWorker
{
    public async ValueTask RunAsync(ClusterWorkContext context, CancellationToken cancellationToken)
    {
        ChainKey chain = ChainKey.Parse(context.Key);
        while (!cancellationToken.IsCancellationRequested && !context.HandOverRequested)
        {
            await using YFexConnection connection = await seals.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await context.Lease.FenceAsync(connection, cancellationToken);      // 0 rows → LeaseLostException → rollback
            int sealedCount = await seals.SealNextBatchAsync(connection, chain, max: 1_000, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (sealedCount == 0) await Task.Delay(options.SealInterval, time, cancellationToken);
        }
    }
}

// ✍ AddYFexAuditServer(...).UseClusterSequencer()
cluster.WorkerPerScope<ChainSequencer, OpenChains>("audit.sequencer", w => w.RequireGuarantee(LeaseGuarantee.Fenced));
```

**Gains:** works on Postgres, SQL Server and SQLite; chains spread across pods (04 §1.1); a stopping pod hands its chains over in ~1 s;
the admin view shows who seals what; the fence makes a paused pod's late batch roll back.
**Cons:** one extra `UPDATE` per batch (≤ 1 000 entries, so negligible); Audit's correctness did **not** depend on the lock (PK = chain
+ index already refuses a second writer), so the change buys operations, not safety; Audit's decision of 2026-11-04 ("not a Jobs
lease") must be revisited by the Audit owner.

## 4. A notice: Authorization and Licensing on every node

```csharp
// ✍ YFex.Authorization.Cluster (bridge owned by Authorization, 10 §2): signals cross nodes
internal sealed class ClusterAuthorizationRelay(IClusterNotices notices, IAuthorizationSignals signals) : IAuthorizationChangeObserver, IHostedService
{
    static readonly NoticeTopic Topic = new("authorization.changed");
    public void OnChanged(in AuthorizationChange change)
    {
        if (RelayScope.IsReplaying) return;                              // do not echo what we received
        _ = notices.PublishAsync(Topic, AuthorizationChangeCodec.Encode(change));   // subject id is a key, not personal data in clear (Privacy rule)
    }
    public Task StartAsync(CancellationToken ct)
    {
        _subscription = notices.Subscribe(Topic, notice =>
        {
            using var replay = RelayScope.Replaying();
            AuthorizationChange change = AuthorizationChangeCodec.Decode(notice.Payload.Span);
            if (change.IsEveryone) signals.AllPermissionsChanged(); else signals.PermissionsChanged(change.Subject!);
        });
        return Task.CompletedTask;
    }
}
```

Licensing (LC-P3/P4) needs the same for "the License of tenant X changed": publish `licensing.changed` with the tenant, each node
reloads `ICentralLicenses.For(tenant)` from its store and calls `AllPermissionsChanged()` locally.

**Cons:** a lost notice leaves a node stale until its version check; therefore `IPermissionVersions` gets a shared backing (a version
row per subject + a global version) that nodes re-read at least every 30 s (10 §2). A notice storm (an admin script changing 10 000
grants) becomes 10 000 notices: the relay coalesces per 100 ms into one "everyone" notice above a threshold.

## 5. Drain in practice

```csharp
// What a rolling deploy looks like on pod central-7f9c (Kubernetes sends SIGTERM)
// t=0     IHostApplicationLifetime.ApplicationStopping → IClusterHealth.DrainAsync(HostStopping)
// t=0     state Draining → /health/ready = 503; heartbeat says Draining; placement stops choosing this node
// t=0–5s  WaitForBalancer: the balancer notices readiness and stops new requests (no new request is refused meanwhile)
// t=5s    participants by order: requests (wait ≤ RequestBudget) → workers (HandOverRequested, then cancel) → leases (release + notice)
//         → WebSockets (close 1012 "service restart"; Messaging.Rpc clients reconnect to another pod)
// t≈8s    Left; host stops. Other pods took FarmaViva's dispatcher within ~1 s of the release notice.
```

Host settings that must allow it (08 §1): `HostOptions.ShutdownTimeout` ≥ drain budget + margin; Kubernetes
`terminationGracePeriodSeconds` larger than that.

## 6. Tests with the harness

```csharp
// ✍ YFexFarma.Central.Tests
[Fact]
public async Task Dispatcher_moves_when_its_node_is_cut_from_the_database()
{
    await using var harness = new ClusterHarness(nodes: 3);              // in-memory "database", fake time, Fenced guarantee
    harness.Configure(c => c.WorkerPerScope<RecordingWorker, FixedScopes>("notifications.dispatch"));
    await harness.StartAsync();

    ClusterHarnessNode first = await harness.WaitForHolderAsync(LeaseKey.Of("notifications.dispatch", Tenants.FarmaViva));
    harness.Partition(first);                                            // node keeps running, cannot reach the database
    harness.Time.Advance(TimeSpan.FromSeconds(31));

    ClusterHarnessNode second = await harness.WaitForHolderAsync(LeaseKey.Of("notifications.dispatch", Tenants.FarmaViva));
    Assert.NotEqual(first, second);
    Assert.True(first.Worker<RecordingWorker>().StoppedBeforeExpiry);    // the cut node stopped at expiry − margin
    Assert.Empty(harness.FencedWritesAfterLoss());                       // no write with an old token reached the store
}
```

The harness also offers `Pause(node, duration)` (a GC or VM freeze: the node's timers stop, then resume with old state), `Skew(node,
offset)`, `Drain(node)`, `Join()`, and runs the same suite against Postgres in Docker (`linuxKit`) through `ClusterHarness.UsePostgres`.

## 7. What app code does **not** change

- Request handlers: none of them see the cluster. A command that must not run twice keeps its idempotency key (Messaging) — on a farm
  the key reaches the shared duplicate filter (05 §7) instead of one node's memory.
- `[Job]` methods: Jobs keeps its own claim (10 §2.2). Whether a job that must never overlap itself across nodes gets a Jobs option
  mapped to a lease is the Jobs owner's choice (10 §2.2).
- Desktop and terminal code: unchanged until the site engine (09).
