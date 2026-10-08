# Storage Backends & Adapters

`YFex.Messaging.Rpc` keeps its offline state (outbox, sync-failure log, client cache) in whatever `IKeyValueStore` the app
registers (`YFex.Persistence`). With nothing registered it uses `MemoryKeyValueStore` and the in-memory outbox — fine for
tests, lost on restart. Register a durable store and the client uses the **durable outbox** over it automatically (offline
Phase 0, F14 — before that the durable outbox was never wired, see `docs/internal/offline/01`).

## How the selection works

```
IKeyValueStore registered by the app (SQLite file, IndexedDB, …)        ← where bytes live
   → AddYFexStorageEncryption (optional)                                 ← AES-256-GCM decorator over that store
       → AddYFexMessagingRpcClient                                       ← picks the outbox:
            Storage = Auto (default) / Sqlite / IndexedDb and the store is not MemoryKeyValueStore
                 → StorageBackedOutbox + StorageBackedSyncFailureLog   (survive restarts)
            Storage = InMemory, or the store is MemoryKeyValueStore
                 → InMemoryOutbox + InMemorySyncFailureLog
```

Register the store **before** `AddYFexMessagingRpcClient` (it only `TryAdd`s the in-memory default).

---

## SQLite — durable storage (desktop / service)

```csharp
var sqlite = new SqliteConnectionFactory(@"C:\ProgramData\FarmaViva\caixa.db");   // YFex.Persistence.Sqlite (file path)
services.AddYFexPersistence(new SqliteKeyValueStore(sqlite));
services.AddYFexStorageEncryption();                                        // optional, after the store
services.AddYFexMessagingRpcClient(o => o.WebSocketEndpoint = new("wss://api.farmaviva.com.br"));
```

**Purpose:** queued commands, cached query results and dead letters survive an app restart, crash or reboot. This is the
standard production setup for native/desktop apps (a pharmacy checkout that loses internet keeps its queued sales).

## IndexedDB — durable storage (Blazor WASM)

```csharp
services.AddYFexIndexedDbStore();                                           // YFex.Persistence.IndexedDb (replaces IKeyValueStore)
services.AddYFexMessagingRpcClient(/* … */);
```

## Encryption at rest — `YFex.Persistence.Encryption`

```csharp
// Default: a random storage key generated once, kept wrapped by the OS (TPM-backed CNG key on Windows, DataProtection elsewhere)
services.AddYFexStorageEncryption();

// Windows service shared by several accounts: machine-scoped key in %PROGRAMDATA%\YFex\Keys
services.AddYFexStorageEncryption(configure: o => { o.MachineScope = true; o.StoreName = "caixa-01"; });

// Or supply your own 32-byte AES-256 key
services.AddYFexStorageEncryption(EncryptionKeySource.Provided, providedKey: my32Bytes);
```

**Must be called after** the store registration — it decorates the registered `IKeyValueStore` with `EncryptedKeyValueStore`.

**With tenancy partitioning** (`YFex.Persistence.Tenancy`), register encryption **before** the scope decorator so the result is
`Scoped(Encrypted(store))`: the encryption then binds each value to its scope-prefixed key. The reverse order works but leaves
tenants separated only by the key prefix.

- **Key source `OsKeyStore` (default):** `WrappedKeyProvider` generates a random 32-byte key once and stores only its wrapped
  form in `<KeyDirectory>\<KeyName>.storage-key` (default `%LOCALAPPDATA%\YFex\Keys\<entry assembly>.storage-key`). On Windows
  the wrapping key is a non-exportable CNG RSA key — TPM first (`KeyProtection.Hardware`), software provider otherwise. On other
  platforms it is ASP.NET Core DataProtection (call `services.AddDataProtection()` and protect its key ring). The same key comes
  back after every restart. A key that cannot be unwrapped (another account/machine) throws `StorageKeyUnavailableException`; a
  new key is never generated silently.
  *Before offline Phase 0 the key was derived from randomized DataProtection output and changed on every restart (F1).*
- **Values are bound to their location:** each value is sealed with associated data `"yfex.store|" + StoreName + "|" + key`, so
  a value copied under another key or into another store does not decrypt (F2). Layout `0x02`; values written in the old `0x01`
  layout are still read and re-sealed on their next write.
- **Unreadable values are reported, not hidden:** they still read as `null`, but `EncryptedKeyValueStore.ReadFailed` fires,
  `UnreadableValues` counts them, and the first one of each kind is logged as `StorageKeyMismatch`.

## `YFex.Messaging.Rpc.Wolverine` — server handler adapter

```csharp
builder.Services.UseYFexMessagingRpcServerWithWolverine();
```

**Purpose:** dispatch YFex CQRS commands/queries through **[Wolverine](https://wolverinefx.net/)** instead of the default
DI-resolved handlers, while exposing them over the YFex Fusion RPC channel. Runs the base server stack (including idempotency
and validation), then replaces `IHandlerInvoker` with `WolverineHandlerInvoker` and `IDispatcher` with `WolverineLocalDispatcher`.

---

## Choosing a combination

| Scenario | Registration |
|---|---|
| Unit/integration tests | RPC client only (in-memory) |
| Desktop / service app | `AddYFexPersistence(new SqliteKeyValueStore(…))` before the RPC client |
| Blazor WASM app | `AddYFexIndexedDbStore()` before the RPC client |
| Any of the above, sensitive data | `+ AddYFexStorageEncryption()` after the store |
| Server using Wolverine | `UseYFexMessagingRpcServerWithWolverine` |
| Server, plain DI handlers | `UseYFexMessagingRpcServer` |
