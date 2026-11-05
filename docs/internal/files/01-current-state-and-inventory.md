# 01 — Current state and inventory

Surveyed 2026-10-29 (text search over `C:\Dev\YFex`, backups excluded). **Verify with RoselYn before coding** — this lists what
the design builds on, not a guarantee the signatures are unchanged.

## 1. Remote files today: nothing

| Searched | Result |
|---|---|
| `ftp`, `ftps`, `sftp` as words in `.cs`/`.md` | only `YFex.Networking/tests/YFex.Networking.Tests/ProbeTests.cs:88`: `NetworkProbe.HttpAsync` **rejects** an `ftp://` URI (correct: probes are HTTP only) |
| `IFileStore`, `IBlobStore`, `IObjectStore`, `IFileSystem` interfaces | none |
| S3 / Azure Blob packages in any `*.csproj` | none |

No app or library has to migrate. Nothing to `[Obsolete]`.

## 2. Neighbours that are *not* this library

| Library | What it does | Why it is not the home |
|---|---|---|
| `YFex/src/Persistence` — `IKeyValueStore` (`YFex.Persistence/IKeyValueStore.cs:15`), `IDurableQueue<T>` (`IDurableQueue.cs:14`), `FileSystemKeyValueStore` | local app state, offline durability, caches | keyed values on this machine, no listing by folder, no partner protocols. Exchange may **use** it for a single-machine ledger (⚑F14) |
| YFex.Web — Refit clients, per-scope endpoints, `ISecretProtector` sealed credentials | outbound HTTP APIs | a partner REST API is a Web concern; a partner bucket/server is a file store |
| YFex.Networking — probes, DNS, TLS check, Telnet, SNMP | network mechanics | protocol *clients for diagnostics*; a file store with credentials, trust, streaming and partner workflow is a product-sized feature (agreed 2026-10-29) |
| YFex.Authentication / YFex.Authorization | who / what | consumers of a future server only (⚑F17) |

## 3. What we reuse

| Need | Existing piece | Where |
|---|---|---|
| SHA-256 value | `Hash256` (`ISpanParsable`, `IUtf8SpanFormattable`, 32 bytes inline) | `YFex/src/YFex/Cryptography/Hash256.cs:27` |
| hashing | `Hashing.Sha256(ReadOnlySpan<byte>)`, `Hashing.Sha256Async(Stream)` | `YFex.Cryptography/src/YFex.Cryptography/Hashing.cs:12,15,23` — we need an **incremental** form over pipe segments (request ⚑C-F1 below) |
| secrets | `ISecretStore.GetAsync(name)` → `byte[]?` (caller clears) | `YFex.Cryptography/src/YFex.Cryptography/Secrets/SecretStores.cs:18-30` |
| certificate trust (FTPS) | `CertificateTrust.Validate(cert, intermediates, at, isRevoked, requiredUsage)` → `CertificateCheck` | `YFex.Cryptography/src/YFex.Cryptography/Certificates/CertificateTrust.cs:35` |
| tagged-struct unions | `[Union]` + `IUnion` (example `OpenResult`) | `YFex.Cryptography/src/YFex.Cryptography/Sealing/OpenResult.cs:61` |
| native unions for cold results | `public readonly union FirewallRuleCheck(…)` style | `YFex.Networking/src/YFex.Networking/Firewall/FirewallRules.cs:33` |
| log values / masks | `ILogValue<TSelf>`, `Masks` | `YFex/src/YFex/Diagnostics/LogValue.cs:14`, `YFex/src/YFex/Privacy/Masks.cs:33` |
| canonical bytes (ledger keys) | `ICanonicalValue` | `YFex/src/YFex/Canonical/ICanonicalValue.cs` |
| retries / timeouts | YFex.Resilience (+ `.Polly`) | `YFex.Resilience/src` |
| schedules | `IJob` | `YFex.Jobs/src/YFex.Jobs.Abstractions/Execution/IJob.cs` |
| audit | YFex.Audit.Public + generator | `YFex.Audit/src` |
| pause polling with no network | `INetworkConditions` (core) | `YFex/src/YFex/Networking` |
| trusted time for "older than" decisions | `ITrustedClock` (core) | `YFex/src/YFex/Time` |
| one host per machine | YFex.Hosting (⚑H1, planned) | `docs/internal/hosting/README.md` |

### Requests to other libraries

| # | Library | Request | Needed by |
|---|---|---|---|
| C-F1 | YFex.Cryptography | incremental SHA-256 that is fed `ReadOnlySequence<byte>` segments and returns `Hash256` (a struct wrapper over `IncrementalHash`, or confirm we may use `IncrementalHash` directly inside YFex.Files) | F0 |
| C-F2 | YFex.Cryptography | `SshHostKeyFingerprint` home: here (`.Sftp`) or Cryptography (it is a key fingerprint) — owner decides | F1 |
| D-F1 | YFex core | `DataSize` (⚑F21) | F0 |

## 4. Candidate libraries — AOT probe (2026-10-29)

A scratch app (`net10.0`, `PublishAot`, `TrimmerSingleWarn=false` so every call site reports) referenced the latest stable
versions and called connect + list on each. ILC generated native code with **0 IL trim/AOT warnings** (link failed only because
`vswhere` was not on PATH — after the warning stage).

| Library | Version | Maintained | Probe | Note |
|---|---|---|---|---|
| SSH.NET (`Renci.SshNet`) | 2026.0.0 | yes (year-versioned releases) | 0 warnings | `HostKeyReceived` gives SHA-256 fingerprint → our pinning |
| FluentFTP | 55.1.0 | yes | 0 warnings | `ValidateCertificate` event → our `CertificateTrust` |
| AWSSDK.S3 (+ Core 4.0.102.14) | 4.0.104.8 | yes (AWS) | 0 warnings | v4 changed client creation to avoid AOT-unsafe type loads (.NET 8+) |
| Azure.Storage.Blobs (+ Azure.Core 1.60.0) | 12.30.1 | yes (Microsoft) | 0 warnings | Azure.Identity not probed (⚑F18) |

**Limits:** only connect/list paths were exercised in one app. F0's AotSmoke publishes **one app per backend** covering
upload, download with range, delete, multipart and the trust callbacks; F1–F3 re-run it. A warning in a path we need reopens ⚑F3–F5.
