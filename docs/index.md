# YFex Framework

YFex is a modular .NET framework for building reactive, offline-capable MVVM applications. It combines a source-generator-driven reactive state engine, type-safe navigation, CQRS with offline queueing, and real-time messaging into a cohesive stack that runs on .NET 8+ and is fully AOT-compatible.

Every framework feature uses Roslyn incremental source generators instead of runtime reflection. All metadata is collected, validated, and pre-compiled into frozen lookup tables at startup. Hot paths are zero-allocation.

---

## Architecture

```
Layer 5  Storage Backends
         YFex.Messaging.Rpc.Sqlite / IndexedDb / Encryption / Wolverine
         ─────────────────────────────────────────────────────────────────
Layer 4  Offline & RPC
         YFex.Messaging.Rpc (Outbox, Client Cache, Sync, Dispatchers)
         YFex.Messaging.Fusion (FusionLiveState, FusionStateBinding)
         ─────────────────────────────────────────────────────────────────
Layer 3  Application Services
         YFex.Mvvm (PageViewModel, ViewModel — bridges State + NavigatR)
         YFex.Messaging (IEventBus, [Live], [Subscribe<T>])
         ─────────────────────────────────────────────────────────────────
Layer 2  Domain Frameworks
         YFex.State.Mvvm (INotifyPropertyChanged bridge)
         YFex.State.History (Undo/Redo: UndoContext, UndoManager)
         YFex.NavigatR (Type-safe async navigation, [Route], Prefetch)
         YFex.Cqrs (IQuery<T>, ICommand, Configuration API)
         ─────────────────────────────────────────────────────────────────
Layer 1  Reactive Core
         YFex.State ([Observable], [Computed], [StateCommand], [ReactsTo])
         YFex.State.Collections (StateList<T>)
         YFex.UI.Abstractions (IDialog, IMessageBox, IToast)
         ─────────────────────────────────────────────────────────────────
Layer 0  Primitives
         YFex (SpecializedDictionary, Percentual, Unit, Extensions)
```

Each layer depends only on the layers below it. Source generator projects (`netstandard2.0`) sit alongside their runtime libraries but have no runtime dependency on them.

Three families extend this stack from sibling repos: **YFex.Resilience** (a Layer-1 policy vocabulary consumed by CQRS and orchestration; Polly is an internal detail behind a seam), **YFex.Orchestration** (a Layer-5 saga / process-manager engine built on CQRS, Compensation, Resilience, Persistence, and Messaging), and **YFex.EventSourcing** (an event-sourcing write model built on CQRS, Persistence and Messaging — aggregates whose state is an append-only stream, with an in-house AoT store for client/offline and Marten for the server). All are AOT-clean and source-generated. See [Resilience](public/resilience.md), [Orchestration](public/orchestration.md) and [Event Sourcing](public/event-sourcing.md).

---

## Documentation

### Public API Guides

For .NET developers building applications with YFex.

| Guide | Libraries | What You'll Learn |
|---|---|---|
| [Reactive State](public/state.md) | YFex.State, YFex.State.Mvvm, YFex.State.Collections, YFex.State.Blazor | Observable properties, computed values, commands, batch updates, validation |
| [Undo & Redo](public/state-history.md) | YFex.State.History, YFex.State.History.Persistence, YFex.State.History.Testing | Undo/redo with coalescing, transactions, navigation boundaries, persistence |
| [Navigation](public/navigatr.md) | YFex.NavigatR | Type-safe routing, prefetch, return values, pool eviction, history policies |
| [CQRS](public/cqrs.md) | YFex.Cqrs, YFex.Cqrs.SourceGenerator, YFex.Cqrs.Analyzer | Queries, commands, events, validation, authorization, caching, offline queueing |
| [Messaging & Offline](public/messaging.md) | YFex.Messaging, YFex.Messaging.Fusion, YFex.Messaging.Rpc + backends | Event bus, live state, offline outbox, client cache, Fusion integration |
| [Resilience](public/resilience.md) | YFex.Resilience, YFex.Resilience.Polly | Retry, timeout, circuit breaker, rate limiter, bulkhead, fallback, hedging — one policy vocabulary over Polly |
| [Orchestration](public/orchestration.md) | YFex.Orchestration + Generator, .Rpc, .Rpc.Wolverine | Sagas, process managers, reverse-order compensation, durable timeouts, event correlation, dead-lettering |
| [Event Sourcing](public/event-sourcing.md) | YFex.EventSourcing + Generator, .Marten | Event-sourced aggregates, fold/replay, `[AggregateCommandHandler]` ↔ CQRS, projections, snapshots, offline sync, Marten server |
| [UI Abstractions](public/ui-abstractions.md) | YFex.UI.Abstractions, YFex.Mvvm | Dialogs, message boxes, toasts, notifications, PageViewModel hierarchy |
| [Core Utilities](public/core.md) | YFex, YFex.Data | Percentual, enum extensions, match extensions, collections, SQL query builder |
| [System & Platform](public/system.md) | YFex.System, YFex.System.Windows | Consent-gated telemetry, privacy scrubbing, platform/environment capture |
| [Security & Network Intelligence](public/security.md) | YFex.Security + Windows.Security, Storage, Capture, Analysis, Service, Cli (+ Hooking/Proxy/ApiReverser) | Passive DNS/TCP/TLS/process capture, JA3, baselines, anomaly detection, app-layer capture & API reversing |

### Internal Documentation

For framework contributors working on YFex internals.

| Document | What It Covers |
|---|---|
| [State Internals](internal/state-internals.md) | Property ID allocation, bitmap dispatch, activation cascade, MVVM marshaling, generator pipelines |
| [State.History Internals](internal/state-history-internals.md) | Undo stack data structures, delta recording, coalescing, persistence serialization |
| [NavigatR Internals](internal/navigatr-internals.md) | Navigator state machine, pool eviction, prefetch lifecycle, return value plumbing, generator emission |
| [CQRS Internals](internal/cqrs-internals.md) | Registry build process, expression compilation, group expansion, analyzer implementation |
| [Messaging Internals](internal/messaging-internals.md) | Event bus buckets, outbox replay, Fusion integration, RPC bridging, storage backends |
| [Resilience Internals](internal/resilience-internals.md) | Overlay value type, frozen resource/fallback registries, pipeline caching, Polly v8 composition & deviations |
| [Orchestration Internals](internal/orchestration-internals.md) | Saga forward-run & compensation, StepBitmap64, PM runtime, delay-scheduler, generator emission, broker/inbox/DLQ |
| [Orchestration + Resilience Design](internal/orchestration-resilience-design.md) | Original design rationale, layering, AoT strategy, phasing (P0–P3), locked decisions |
| [Event Sourcing Internals](internal/event-sourcing-internals.md) | In-house `IKeyValueStore` store (`es:` ordinal keys), aggregate fold serialization, generator emission, session optimistic append, projection subscriptions, snapshot cadence, sync/rebase |
| [Event Sourcing Design](internal/event-sourcing-design.md) | Proposed ES layer: aggregate domain model, `[EventSourced]`/`[AggregateCommandHandler]` ↔ CQRS, projections, offline sync, Marten provider |
| [Logging & Observability Design](internal/logging/README.md) | Proposed YFex.Logging: generator-driven implicit instrumentation, zero-alloc pipeline, tenant-aware tracing across nodes, audit channel, server, alerts (status: proposal, decisions ⚑L1–L12 pending) |
| [Audit Design](internal/audit/README.md) | Proposed YFex.Audit (sibling of logging, ✅ L20): declarative command audit (`.Audit()`), generated field-level entity diffs (`[AuditChanges]`), transactional/durable write path for offline devices, hash chains + signed anchors + Merkle proofs, Postgres insert-only store, privacy via YFex.Privacy (crypto-shredding), retention classes, entity history UI (status: proposal, decisions ⚑A1–A31 pending) |
| [Authorization Consolidation](internal/authorization-consolidation/README.md) | One YFex.Authorization for every "who may do/see what": single message policy table for CQRS/RPC/HTTP, bitset permission sets, verdicts with reasons, snapshots/versions/signals out of Messaging.Rpc, approvals (from Rules), temporary grants, impersonation, actor context, reveal permissions for Privacy/Audit (status: approved 2026-10-02, ⚑Z1–Z19 confirmed; Phase 3 decisions P1–P7 confirmed 2026-10-04; Phases 0–2b + 3a done, log in 09; 3b waits for YFex.Cryptography) |
| [Offline (YFex.Offline)](internal/offline/README.md) | Offline operating model: cloud-connected / shop LAN with a generic **site broker** (no app code; sessions, approvals, permission answers, budgets, unique claims, custody, site feed) / standalone terminal; builds on YFex.Installations (identity, tenant CA + mutual TLS, signed bundles, relay, peer channels), YFex.Time (trusted clock) and YFex.Networking, offline login, `.OnOffline<T>()` + `.Offline(...)` (facts vs requests, store-shared changes such as a price override), envelopes + custody + reconciliation as of the operation time; fixes live defects F1 (storage key), F3 (idempotency), F4 (replay actor/scope) (status: Phase 0 done 2026-10-03, Phase 1 done 2026-10-15 (repo `YFex.Offline`), Phase 2 done 2026-10-15 (`.Central`, `.Rules`, signed manifest, limits, allowances, receipts, signed rule data); plan re-cut 2026-10-14 after Cryptography 0–3, Time T0–T3, Networking 0–6 and Installations I0–I4 were built — Phases 1–2 can start, 3 needs Authorization 3b/4b, 5 follows Installations I5; confirmed ⚑O1/O2/O3/O9/O11/O14/O16/O18/O20/O21/O22/O23/O24/O25–O30 (O2 = central picks the broker while online), rest pending, new ⚑O31 broker transport) |
| [Cryptography (YFex.Cryptography)](internal/cryptography/README.md) | Should the ecosystem have one cryptography library? **Yes, with a boundary**: sibling repo `YFex.Cryptography` (BCL only, AOT) owns keys (`SecretKey`, `KeyRing`, root-key ladder with CNG/TPM in `.Windows`), sealed value v3 (AES-GCM + key id + length-prefixed AAD), MACs/tokens, HKDF, PBKDF2, domain-separated signatures, ECIES recipient sealing, hash chains/Merkle, certificates; core gets only types (`Hash256`, `KeyId`, `SignatureAlgorithm`, `DetachedSignature`, `ICanonicalValue`); Privacy/Offline/Audit/Authorization keep the meaning. Inventory with file:line, live defects F1/F2 + C1–C11, measured baseline (TPM sign 32 ms), post-quantum plan (agility now, hybrid ML-KEM sealing first), migration map with golden tests (status: approved 2026-10-04, ⚑K1–K18 confirmed, K2 = core holds types only; **Phase 0 DONE 2026-10-04**: repo `C:\Dev\YFex\YFex.Cryptography` (rings, sealed value v3, CNG/TPM root, canonical writer) + Persistence.Encryption moved onto it; log in 12 §5; next Phase 1) |
| [Trusted time (YFex.Time)](internal/time/README.md) | One reusable "what time is it, really?" service: `TrustedTimeProvider : TimeProvider` over pluggable sources (central server anchor, site broker, NTP/NIC.br, HTTPS Date, SEFAZ `dhRecbto`, GNSS; signed Roughtime/NTS/RFC 3161 after YFex.Cryptography), NTP-style offset + uncertainty, never-backward high-water mark, rollback detection; consumers: authorization snapshot/approvals, offline envelopes, crypto validity, logging/audit skew, fiscal emission time (status: approved 2026-10-04/05, ⚑T1–T12 confirmed incl. site time for YFex.Offline §8; **T0 + T1 DONE 2026-10-05**: core types `YFex.Time` + repo `C:\Dev\YFex\YFex.Time` (anchor + monotonic clock, 22 ns reads, SNTP, HTTPS Date, high-water, AOT); log in §10; next T2 central anchor) |
| [Networking (YFex.Networking)](internal/networking/README.md) | Network mechanics in one sibling repo `C:\Dev\YFex\YFex.Networking` (BCL + M.E. abstractions, AOT; `.Windows`, `.Vendors`, `.Testing`, opt-in survey package): machine conditions (`INetworkConditions` in core: no network / LAN only / captive portal / internet, medium, metered cost) with hysteresis, interface snapshot + change stream, targeted probes with a process-wide budget (ping, TCP, DNS incl. SRV/TXT, HTTP 204, traceroute, TLS endpoint check with YFex.Time clock-skew hints), endpoint health + link quality, diagnosis report, ARP/NDP table + MAC vendors, Wake-on-LAN, Wi-Fi, proxy/VPN, multi-homing, network fingerprint hint, discovery that returns candidates only (signed-payload beacons, mDNS/DNS-SD, SSDP, WS-Discovery), SNMP client, Telnet (from Devices); `MacAddress` → core `[Sensitive]`; Messaging.Rpc `INetworkStatus` → `IServerConnection`; trust/tenancy stay in YFex.Installations (status: approved 2026-10-07, ⚑N1–N28 confirmed (N8 = Survey); **phases 0–3 DONE 2026-10-07**: core types, Messaging.Rpc `IServerConnection`, repo `C:\Dev\YFex\YFex.Networking` (conditions monitor, probes with budget, endpoint health, IP math; `.Windows` NLM/Wi-Fi/ARP/firewall via generated COM + P/Invoke; `.Vendors` embedded IEEE snapshot; Wake-on-LAN, proxy/VPN, multi-homing; AOT); **phase 4 DONE 2026-10-13**: DNS wire client (SRV/TXT/PTR, TCP fallback, DoH opt-in),
traceroute, TLS endpoint check with device-clock hints, `NetworkDiagnosis` (masked ticket text), opt-in STUN/HTTPS public IP; **phase 5 DONE 2026-10-13**: beacons (wire v1, signer/verifier hook, Public-network opt-in), mDNS/DNS-SD,
SSDP, WS-Discovery, `InMemoryNetwork` + `ChaosStream`; log in 06; ⚑N29 macOS open; inventory with live defects D1–D6) |
| [Installations (YFex.Installations)](internal/installations/README.md) | Which installed copy of an app is this, and which other machines are trusted friends of the same tenant/site — decided **only from enrollment certificates, never from the network** (⚑N6, fixes offline F11). Sibling repo `C:\Dev\YFex\YFex.Installations` (`.Windows`, `.Central`, `.Rpc`, `.Testing`) on YFex.Cryptography + YFex.Networking; core types only (`InstallationId`, `InstallationRole`, `InstallationStatus`, `InstallationIdentity`, `LocalInstallation`, signed-bundle header): id minted at first run, transport + evidence keys in the platform store, enrollment codes, tenant CA with `urn:yfex:scope:` SANs, signed roster + revocations, signed bundles with version floors, friend recognition, mTLS peer endpoint + channels, installation assertion to central (scope pinning online too), SAS pairing, site mesh substrate (time, relay to central through any friend, bundle gossip); inventory of every identity/pairing/relay (Logging `InstanceId`, Audit `NodeId`, Privacy/Rules device keys, Devices PSK, Profiling fingerprint) with migration (status: all ⚑I1–I23 and mesh ⚑O25–O30/T13 approved; **I0–I4 DONE** (I4 mesh channels time/relay/bundle gossip 2026-10-14; log in 08 §5); I5 pairing/hardening next; open ⚑I24–I25; installer deferred to YFex.Installer) |
| [Licensing (YFex.Licensing)](internal/licensing/README.md) | What has this customer bought, and may this installed copy run this feature? Built on YFex.Installations (⚑I23): licenses are signed **only by the vendor** (keys pinned in the app, so an on-premises tenant cannot sign itself a bigger license) and travel as Installations bundles (`licensing.entitlements` per tenant + product, `licensing.activations` per scope unit). Sibling repo `C:\Dev\YFex\YFex.Licensing` (`.Central`, `.Vendor`, `.Testing`; no core types, no OS packages): products → modules = sets of permissions (base = permissions in no module), editions as vendor-side templates, seats per role and scope counted from the installation registry and enforced by `IEnrollmentPolicy` plus vendor-signed activation lists (7-day pending grace, replace moves a seat), vendor-issued trials, expiry and grace on YFex.Time trusted time (removals only with time usable for security, otherwise a proven lower bound; degrade, never brick), offline allowance per hosting mode, daily or file check-in for on-premises centrals, gating through a generic Authorization hook `IPermissionAvailability` + `DenialKind.Unavailable` (Capabilities hides features automatically), optional soft usage meters, the vendor issuing service, threat model and the honest limit (protect honest customers, detect abuse) (status: proposal 2026-10-07, ⚑LC1–LC22 pending) |
| [Source Generators](internal/source-generators.md) | All generator pipelines: architecture, models, emission patterns, diagnostics, performance |

---

## Installation

Add the packages you need. Each feature is a separate NuGet package.

```bash
# Core primitives and extensions
dotnet add package YFex

# Reactive state engine (includes source generator automatically)
dotnet add package YFex.State

# MVVM bridge (INotifyPropertyChanged)
dotnet add package YFex.State.Mvvm

# Undo/redo
dotnet add package YFex.State.History

# Type-safe navigation
dotnet add package YFex.NavigatR

# CQRS
dotnet add package YFex.Cqrs

# Messaging and event bus
dotnet add package YFex.Messaging

# Offline-capable RPC with client cache and outbox
dotnet add package YFex.Messaging.Rpc

# Storage backends (pick one)
dotnet add package YFex.Messaging.Rpc.Sqlite      # Desktop / server
dotnet add package YFex.Messaging.Rpc.IndexedDb    # Blazor WASM

# Full MVVM ViewModel stack
dotnet add package YFex.Mvvm

# Resilience policies (retry, timeout, circuit breaker, …) + Polly engine
dotnet add package YFex.Resilience
dotnet add package YFex.Resilience.Polly

# Sagas & process managers (multi-step transactions, compensation, timeouts)
dotnet add package YFex.Orchestration
dotnet add package YFex.Orchestration.Rpc            # cross-service broker + DLQ (server)
```

---

## Where to Start

**Building a desktop/mobile MVVM app?**
Start with [Reactive State](public/state.md) to learn `[Observable]`, `[Computed]`, and `[StateCommand]`. Then read [Navigation](public/navigatr.md) for routing between pages, and [UI Abstractions](public/ui-abstractions.md) for dialogs and toasts.

**Building a client-server app with offline support?**
Start with [CQRS](public/cqrs.md) to define your queries and commands, then [Messaging & Offline](public/messaging.md) to set up the outbox, client cache, and Fusion integration.

**Coordinating multi-step transactions or long-running workflows?**
Read [Orchestration](public/orchestration.md) for sagas (atomic multi-step with reverse-order compensation) and process managers (event-driven state machines with durable timeouts). Pair it with [Resilience](public/resilience.md) to protect the commands they dispatch (retry, timeout, circuit breaker, fallback).

**Adding undo/redo to an existing app?**
Read [Undo & Redo](public/state-history.md). It builds on top of the reactive state engine, so familiarity with `[Observable]` properties helps.

**Collecting telemetry or reading the host environment?**
Read [System & Platform](public/system.md) for consent-gated telemetry and platform capture. For network/process monitoring, anomaly detection, and (authorized) app-layer traffic capture, see [Security & Network Intelligence](public/security.md).

**Contributing to the framework?**
Read the [Source Generators](internal/source-generators.md) overview first, then dive into the specific internal doc for the subsystem you're working on.

---

## Build & Test

```bash
dotnet build yfex.slnx
dotnet test yfex.slnx
```

Most libraries multi-target `net8.0;net9.0`. Source generators target `netstandard2.0`.
