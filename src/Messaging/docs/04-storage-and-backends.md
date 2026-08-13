# Storage Backends & Adapters

`YFex.Messaging.Rpc` ships with an **in-memory** outbox and cache by default — fine for tests, lost on restart. The packages here swap in durable storage, encrypt it, or adapt the server side to Wolverine. They're all opt-in DI one-liners layered on top of `AddYFexMessagingRpcClient` / `UseYFexMessagingRpcServer`.

## How the swap works

The RPC layer resolves `IClientStorage`, `IClientCache`, `IOutbox`, and `ISyncFailureLog` from DI. Each backend package calls `services.Replace(...)` on those, so **call order doesn't matter** — register the backend before or after the RPC client, the durable implementation wins either way. Each backend also publishes its raw storage under a keyed registration (`StorageServiceKeys.Inner`) so the encryption decorator can wrap it without a circular dependency.

```
AddYFexMessagingRpcClient        ← in-memory defaults
   → AddYFexSqliteStorage        ← replaces with durable SQLite
       → AddYFexStorageEncryption ← decorates storage with AES-GCM
```

---

## `YFex.Messaging.Rpc.Sqlite` — durable storage (desktop / mobile)

```csharp
services.AddYFexMessagingRpcClient(/* … */)
        .AddYFexSqliteStorage(o => o.DatabaseFileName = "app.db");
```

**Purpose:** persist the offline outbox, client cache, and sync-failure log in a SQLite file so a queued command survives an app restart, crash, or reboot. This is the standard production backend for native/desktop apps.

Registers `SqliteClientStorage`, `SqliteClientCache`, `SqliteOutbox`, and `SqliteSyncFailureLog` and `Replace`s the in-memory defaults.

## `YFex.Messaging.Rpc.IndexedDb` — durable storage (Blazor WASM)

```csharp
services.AddYFexMessagingRpcClient(/* … */)
        .AddYFexIndexedDBStorage();
```

**Purpose:** the browser equivalent of the SQLite backend. Persists the same four stores in IndexedDB so state survives a page reload or tab close in a Blazor WebAssembly app. Ships a small JS interop in `wwwroot`.

Registers `IndexedDBClientStorage` plus the storage-backed outbox/cache/failure-log and `Replace`s the in-memory defaults.

## `YFex.Messaging.Rpc.Encryption` — AES-GCM at rest

```csharp
// OS key store (recommended) — key managed by Microsoft.AspNetCore.DataProtection
services.AddYFexStorageEncryption();

// Or supply your own 32-byte AES-256 key
services.AddYFexStorageEncryption(EncryptionKeySource.Provided, providedKey: my32Bytes);
```

**Purpose:** encrypt everything the client persists (queued commands, cached query results) so data at rest is unreadable without the key. A regulatory/privacy requirement for offline caches on shared or portable devices.

**Must be called after** a storage backend — it decorates the raw `IClientStorage` registered under `StorageServiceKeys.Inner` with `EncryptedClientStorage`. Two key sources:

- `OsKeyStore` *(default)* — key derived via `Microsoft.AspNetCore.DataProtection` (OS-backed).
- `Provided` — you pass a 32-byte key (validated for length).

## `YFex.Messaging.Rpc.Wolverine` — server handler adapter

```csharp
builder.Services.UseYFexMessagingRpcServerWithWolverine();
```

**Purpose:** let the server dispatch YFex CQRS commands/queries through **[Wolverine](https://wolverinefx.net/)** instead of the default DI-resolved handlers — so you keep Wolverine's handler discovery, middleware, and messaging features while exposing them over the YFex Fusion RPC channel.

Runs the base server stack, then replaces `IHandlerInvoker` with `WolverineHandlerInvoker` and `IDispatcher` with `WolverineLocalDispatcher` (routes through Wolverine's `IMessageBus`). Optionally wires exception→result middleware and the server-pushed-events bridge.

---

## Choosing a combination

| Scenario | Registration |
|---|---|
| Unit/integration tests | RPC client only (in-memory) |
| Desktop / MAUI / mobile app | `+ AddYFexSqliteStorage` |
| Blazor WASM app | `+ AddYFexIndexedDBStorage` |
| Any of the above, sensitive data | `+ AddYFexStorageEncryption` (after the backend) |
| Server using Wolverine | `UseYFexMessagingRpcServerWithWolverine` |
| Server, plain DI handlers | `UseYFexMessagingRpcServer` |

All decorators/backends are independent — mix as needed. Order only matters for encryption (after a storage backend).
