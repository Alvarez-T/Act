# 05 — Permission snapshots, versions and change signals (fixes D7, D8)

## 1. What moves out of Messaging.Rpc

Validation D29 built three authorization concepts inside the RPC transport. They are right; their home is wrong:

| Concept | Today (Messaging.Rpc) | After (Authorization) | Rpc keeps |
|---|---|---|---|
| Per-user permission version | `IPermissionsVersions`, `PermissionsVersions` (`CallContextSignals.cs:17-42`) | `IPermissionVersions`, `InMemoryPermissionVersions` (.Public / runtime) | — |
| Change signals | `CallContextSignals.PermissionsChanged/AllPermissionsChanged/RevokeAsync` | `AuthorizationSignals.PermissionsChanged/AllPermissionsChanged/Revoked` + `Changed` event | a subscriber that invalidates `ICallPermissions` and **disconnects** the subject's connections on `Revoked` |
| Snapshot of "what I may do" | `CallPermissions(Subject, IsAuthenticated, Version, string[] Permissions)` | `PermissionSnapshot` (.Public) + codec | `ICallPermissions` Fusion compute method returning it (+ MemoryPack formatter delegating to the codec) |
| Resolved-context cache by version | `CallContextResolver.AuthFor` (`CallContextMiddleware.cs:58-70`) | `AuthorizationContextCache` (runtime) | calls the cache from the middleware |

## 2. `PermissionSnapshot`

> **Phase 3 decision P2 (2026-10-04):** implemented as **payload only** — `PermissionSnapshot(Subject, IsAuthenticated, Scope,
> Version, Permissions, Roles, Relation)` in `.Public`, `From(caller)`, `Has`, `IsInRole`; the client rebuilds the caller with
> `AuthorizationContext.FromSnapshot` (runtime, unknown ids dropped). `Validity`, `KeyId` and `Signature` below move to the signed
> envelope (YFex.Offline `SignedBundle`, kind `authorization.snapshot`) in Phase 3b, after YFex.Cryptography Phase 2.

```csharp
// (sketch) YFex.Authorization.Public — ids on the wire, never ordinals (03 §2)
public sealed record PermissionSnapshot(
    string? Subject,
    bool IsAuthenticated,
    string? Scope,                       // canonical ScopeIdentity text the permissions were resolved in
    long Version,
    ImmutableArray<string> Permissions,  // sorted ids
    ImmutableArray<string> Roles,
    ActorRelation Relation,              // the client can show "acting as tenant X"
    ValidityPeriod Validity,             // core type (⚑Z13): From = issued, Until = offline use limit (⚑Z9, configurable)
    string KeyId,                        // server signing key (rotation)
    ReadOnlyMemory<byte> Signature)      // server signature over the canonical bytes — verified BEFORE use (⚑Z9)
{
    public static PermissionSnapshot Anonymous { get; }
    public static PermissionSnapshot From(IAuthorizationContext caller, DateTimeOffset now, TimeSpan offlineValidity);
    public IAuthorizationContext ToContext();          // ids → declared PermissionKeys → PermissionSet (unknown ids ignored)
}
// PermissionSnapshotCodec: canonical binary (signed + persisted on devices) + System.Text.Json source-generated context (AOT).
// IPermissionSnapshotSigner (server) / IPermissionSnapshotVerifier (client): ECDsa by default (clients hold only the public key),
// same key-rotation model as approval tokens (06 §1).
```

**Integrity (⚑Z9, confirmed):** a snapshot is only turned into a context after its signature verifies against a configured server
key, its subject matches the logged-in user and `Validity` contains `now`. Editing the persisted file (adding `sales.void`, extending
`Until`) breaks the signature, and the client falls back to anonymous. *How* the file is stored (OS key store / DPAPI, device binding,
clock-tampering defence) and how a shop LAN "store server" re-issues snapshots while the cloud is unreachable are decided by the
separate **`docs/internal/offline/`** design; this design only fixes the format and the verification hook.

## 3. Client side: server-granted permissions drive the UI (new)

> **Phase 3a (2026-10-04):** online only. `UseServerPermissions()` registers `SnapshotPrincipalAccessor` + the hosted
> `ServerPermissionsFeed` (live `ICallPermissions` computed). Nothing is persisted yet; the offline paragraph below is Phase 3b, and
> a persisted snapshot is used only after an offline sign-in established the subject (decision P3).

```csharp
// ✍ Dev writes (client composition root) — one line
services.AddYFexMessagingRpcClient(…).UseServerPermissions();     // registers SnapshotPrincipalAccessor as IPrincipalAccessor
```

```
 server                                                   client
 ICallPermissions.GetMyPermissionsAsync() ──Fusion──►  SnapshotPrincipalAccessor
   (invalidated by AuthorizationSignals)                 ├─ Current = snapshot.ToContext()   → NavigatR, Capabilities, VM CanExecute
                                                         ├─ raises AuthorizationSignals.Changed locally → re-check open page/commands
                                                         └─ persists the snapshot (IKeyValueStore, tenant-partitioned) for offline start
```

**Offline (⚑Z9):** at startup without a connection, the last persisted snapshot is used while its signature verifies and
`Validity.Contains(now)`. After that, the
client falls back to anonymous (or an app-chosen "offline profile"). Default offline validity 72 h,
configurable per app (the offline (YFex.Offline) design may tighten it per topology). The snapshot drives **UX and local-only operations**. Queued commands are re-authorized on the server at
replay (stage 2 already runs there), so a stale snapshot never grants server-side effects. Device-signed approvals stay capped
(06 §1).

## 4. Signals

> **Phase 3a (2026-10-04):** `IPermissionVersions`, `IAuthorizationSignals` (`PermissionsChanged`, `AllPermissionsChanged`,
> `RevokedAsync`, `Changed`), `AuthorizationChange` and `IAuthorizationChangeObserver` (DI-registered subscribers: the context cache,
> Messaging.Rpc's `RpcAuthorizationChangeObserver`) live in `.Public`; `InMemoryPermissionVersions` and `AuthorizationSignals` in
> the runtime (`AddYFexAuthorizationSignals()`).

```csharp
// Before — only RPC hosts can signal; desktop/HTTP hosts have no equivalent
callContextSignals.PermissionsChanged("joao");
await callContextSignals.RevokeAsync("joao");

// After — any host; transports subscribe
authorizationSignals.PermissionsChanged(subject: "joao");          // role assignment edited, grant issued/expired
authorizationSignals.AllPermissionsChanged();                       // role map / catalog change
await authorizationSignals.RevokedAsync(subject: "joao");           // logout / account disabled: Rpc closes connections
```

```csharp
// (sketch) YFex.Authorization.Public
public interface IAuthorizationSignals
{
    void PermissionsChanged(string subject);
    void AllPermissionsChanged();
    ValueTask RevokedAsync(string subject, CancellationToken cancellationToken = default);
    event Action<AuthorizationChange>? Changed;        // (subject or all, kind) — subscribers: Rpc, AspNetCore cache, client UI, audit
}
```

Farms: `IPermissionVersions` is the only state. Replace the in-memory implementation with a shared one (cache/DB), or feed
`PermissionsChanged` from the message bus on every node, the same advice as today (`CallContextSignals.cs:29-30`).
`TemporaryGrantSource` raises `PermissionsChanged` when a grant starts or expires (06 §2).
