# 05 — Ecosystem integration

> Rule for every row: **the library does not reference YFex.Hosting**. The host (or its built-in module adapters, ⚑H18) calls the
> library's `Add…` and reads its public contracts. Asks to other libraries are marked **Ask** and need that library's session.

## 0. Who talks to central (user question 2026-11-03)

**Hosting does not send other libraries' data to central.** Three different owners, one rule each:

| Layer | Owner | Responsibility |
|---|---|---|
| **What** is sent and how durable it is | the **data's library**: Logging (`YFex.Logging.Shipping`: spool, may drop under pressure), Audit (journal + ack-based shipper, never drops), Profiling (offline queue + transport, consent), Offline (envelopes, custody), Installations (diagnostics) | format, batching, local spool, retry, what is lost or not, the server-side ingest (Logging.Server, Audit server, central Offline…) |
| **The road** to central | **YFex.Installations** (identity: mTLS certificate and signing key; `relay` channel through a broker/friend when central is unreachable, ⚑O26) and **YFex.Messaging.Rpc** (the direct client connection) | who the sender is, signing, routing, store-and-forward through the site |
| **Where it runs** | **YFex.Hosting** (or the app, for its own process) | runs the shippers as hosted services in one process per machine, gives them folders and the machine identity, shows their backlog in status/CLI/tray |

```
 inside any process (app or yfex-host)
   Logging ─ spool ─ shipper ┐
   Audit ─ journal ─ shipper ┼─► Messaging.Rpc client ───────────────► central ingest (per library)
   Profiling ─ queue ─ transport┘        └─ unreachable? Installations relay ─► broker/friend ─► central relay ingress
 yfex-host adds only one item of its own:  hosting.status (H19)            (central dispatches relay items by kind to their owner)
```

- So for profiling and logs: the **library** sends, over the **Installations/Rpc** road; the host only makes sure the sender is running
  on machines where no app is open (the broker PC at night, machine telemetry).
- Each process ships **its own** data. An app's logs are shipped by the app while it runs; its spool waits until the app starts again.
- **⚑H22 (open):** should the host also drain spools left by closed apps on the same machine (a machine-wide forwarder)?
  Recommendation: **not in v1**. If wanted later, it is a module **owned by Logging/Audit** (they know their spool formats), hosted by
  Hosting — not Hosting code.

## Overview

| Library | Role in the host | Phase | Ask? |
|---|---|---|---|
| **YFex.Jobs** | base module; one scheduler per host; tray/CLI list, run now, pause | H0 (base), H2 (control) | none (KeyValue store built) |
| **YFex.Logging** | base module; host log files, per-module levels, live tap → tray/CLI; shipping later | H0, H2 | Event Log / journald start-up sink? |
| **YFex.Networking** | base module; conditions, central endpoint watch, firewall/port checks for module listeners, diagnosis | H0, H2 | — |
| **YFex.Profiling** | `profiling.machine` module + tray session providers; consent in the tray | H3 | session-signal ingestion seam |
| YFex.Installations | identity, machine keys, enrollment, profile bundles, status relay | H0, H1 | additive publisher registration (G4) |
| YFex.Tenancy | central resolves profile layers on the scope chain; host's ambient scope = its installation scope | H1 | — |
| YFex.Time | base module; trusted time for schedules, logs, status | H0 | — |
| YFex.Audit | control actions and profile changes as audit records | H1–H2 | — |
| YFex.Privacy | masks in diagnosis, support bundle, log view | H2 | — |
| YFex.Persistence | encrypted local state: last-good profile, override, supervisor counters, job store | H0 | — |
| YFex.Cryptography | storage root key (TPM/CNG, file on Linux) through Persistence/Installations | H0 | — |
| YFex.Resilience | module restart backoff, crash-loop breaker | H0 | — |
| YFex.Authorization | `HostingPermissions` catalog at central; later local gate for control actions | H1, H4 | — |
| YFex.Authentication | later: operator sign-in for control on shared Windows accounts | H4 | — |
| YFex.Licensing *(design)* | a host module may require a license module | when Licensing exists | — |
| YFex.Offline | `offline.broker` module | H0 | — |
| YFex.Devices | `devices.serial-bridge` (TCP/RFC 2217 bridges, virtual COM backends), device watchers | H3 | options binding from settings |
| YFex.Files *(planned)* | `files.exchange` (app hosts, ⚑H4) | H3 | — |
| YFex.Data | `data.schema` for local SQLite stores (data-schema ⚑DS7 option B) | H3 | — |
| YFex.Messaging.Rpc | uplink to central used by modules (broker journal) | H0 | — |
| YFex.System | machine snapshot (OS, memory, disk) in status and support bundle | H2 | — |
| YFex.Capabilities | later: host actions as capabilities for search/AI support tools | not planned | — |
| YFex.Security | separate service (⚑H6); may adopt the library and the control contract to appear in the tray | optional, H4 | — |
| YFex.UI / YFex.Avalonia | tray windows reuse YFex controls and theme where they fit | H2 | — |
| Not used | CustomLogic, Search, Web, Orchestration, EventSourcing (central-side engines; an app host may still add them as its own modules) | — | — |

## 1. YFex.Jobs (⚑H10)

**Without Jobs** each library keeps its own `PeriodicTimer` in a hosted service: it works, but nobody sees when it last ran, a support
person cannot "run now", schedules cannot be changed per site, and failures are only log lines. **With Jobs in the base** (decided):

- One `SchedulerHost` per host generation; modules that offer a `.Jobs` package (optional, library-owned) register their `[Job]` classes
  in their module adapter. Libraries without one keep their hosted services — both are fine.
- Schedule overrides come from the profile: settings `jobs:{key}:cron` / `:enabled` under the `jobs` module (central-controlled, Tenancy
  chain). Jobs does **not** read these keys: the Hosting Jobs module adapter maps them and applies them through Jobs' override API
  (`IJobControl` / definition sets) — not edited on the machine (except a local override, H17).
- Policy: `OpenPolicy` (standalone) in v1; `ReportingPolicy` when YFex.Jobs.Hub exists.
- Times come from YFex.Time's `TimeProvider` (trusted), so a PC with a wrong clock does not run the nightly job at noon.
- **Store:** `YFex.Jobs.Persistence.KeyValue` over YFex.Persistence's `IKeyValueStore` (built, A2 phase 6; AOT, no EF Core): `.UseKeyValueStore()`.

## 2. YFex.Logging

```csharp
// (sketch) what AddYFexHostLogging(m) does
services.AddYFexLogging(log => log
    .Node(n => n.App(m.Kind.Value).Instance(m.Instance.Value).Kind(NodeKind.Worker))
    .Profile(LoggingProfile.Server)
    .FromConfiguration(m.Settings.GetSection("levels"))                  // per-module levels from the profile
    .WriteTo(w => w.RollingFile(Path.Combine(m.Paths.Logs, "{date}.ylog")).LiveTap(liveTap))
    .Bridge(b => b.MicrosoftExtensionsLogging(LogLevel.Warning)));       // Generic Host, SCM, Kestrel-free libraries
```

- Files in the instance's `logs` folder (06 §2), retention by size/days from the profile (defaults 14 days, 200 MB).
- Each module start/stop is an **operation** (`Operations.Start("hosting.module.start")`) with the module id as a tag; generations too.
- Live tap → control channel → tray/CLI (only while someone watches).
- **Shipping** to central: when `YFex.Logging.Shipping` exists, through the Installations relay (the broker already relays logging
  items for its terminals, offline 07 §2). Until then local only, and the support bundle carries recent warnings.
- **Ask (Logging):** start-up failures before the engine runs (bad `hosting.json`, lock held) must reach the Windows **Event Log** /
  **journald**. Either a tiny Logging sink or Hosting writes those few lines itself (recommended: Hosting, `.Windows`/`.Linux`, no
  Logging change).

## 3. YFex.Networking

- Base module: `NetworkConditionsMonitor` with `UseWindowsConditions()` and `WatchEndpoint("central", …, essential: true)`; modules read
  `INetworkConditions` (core contract) as they already do.
- **Listener checks:** for every `HostListener` a running module declares, the host checks at start that the port is free and (Windows)
  that a firewall rule exists (`CheckWindowsFirewall()` pattern from Installations, built on Networking). Missing rule → finding
  `hosting.firewall.missing` in the status and the tray; **creating** it is YFex.Installer's (with admin consent).
- **Diagnosis:** `RunDiagnosis` builds a `DiagnosisPlan` from the profile's watched endpoints + module listeners (and, on a broker, the
  site's terminals from the roster), returns `DiagnosisReport.ToText()` (already masked).
- Conditions changes are pushed to the tray (one line, translated).

## 4. YFex.Profiling (⚑H15)

| Part | Runs in | What |
|---|---|---|
| `TelemetryCollector`, offline queue (`SqliteOfflineQueue` on Windows), transport | host, module `profiling.machine` | one pipeline per machine |
| machine providers: hardware fingerprint, installed software, display, `ISystemInfoProvider` snapshot | host | collected on a Jobs schedule (daily) |
| session providers: idle, session lock/unlock, active-window category, behaviour **counts** | **tray**, per user session | forwarded as `SessionSignals` batches |
| consent | `IConsentStore` at **machine scope** in the host's state folder; edited in the tray (Administrators) | default: not asked → nothing collected |

- Without consent the module runs but collects nothing (state `Running`, reason `profiling.no-consent`).
- Transport to central is **Profiling's** (its transport today, the Installations relay later — Profiling's decision, §0).
- The tray part (session providers) moves with the tray to the last phase (H5); `profiling.machine` alone is H3.
- **Ask (Profiling):** a seam to ingest session signals produced in another process (`ISessionSignalSink` or using the collector's
  public API), so the tray can feed the host's collector. Small, additive.

## 5. YFex.Installations and YFex.Tenancy (⚑H3, ⚑H19)

- **Identity:** the generic host is an installation (`Application("yfex-host")`, machine storage, role from enrollment: `SiteBroker`,
  or `Terminal`-class roles for other machines). Whether an app-built host on the same PC has its own installation is ⚑H21 below.
- **Profile bundle:** kind `hosting.profile` (Hosting declares it, like Offline declares its kinds), publisher = central's Hosting
  publisher key, audience = installation, stream `{kind}/{instance}`. `HostProfileTracker` watches the bundle store, verifies, and hands
  the profile to the shell (02 §4).
- **Ask (Installations, G4):** `Publishers(...)` has one slot; Offline works around it with `OfflineBundles(otherPublishers)`. Hosting
  needs an additive publisher registration (or the same workaround: `HostingBundles.Publisher` passed into Offline's slot, as 03 §1 does).
- **Status relay:** relay item kind `hosting.status` (signed, like `installations.diagnostics`, `InstallationDiagnostics.RelayKind`),
  sent on change and every `StatusInterval`. Central keeps the latest per installation + instance. Through a broker it rides the site's
  relay like every other item.
- **Tenancy:** central resolves layers on the installation's `ScopeIdentity` (02 §5.2). On the machine, the shell sets the ambient
  scope to the installation's scope for every module (`ScopeOverride.Enter(scope)` around generation start), so scope-aware libraries
  (Persistence partitioning, CustomLogic) see the right store.
- **Open (H21, add to the user's list):** does an app-built host on the same PC enroll its **own** installation, or use the machine's
  generic one (shared key store → needs a local trust path)? Recommendation: its own installation (simple, roster shows it), same
  machine hint.

## 6. YFex.Audit, YFex.Privacy, YFex.Persistence, YFex.Cryptography, YFex.Resilience

- **Audit:** records for control actions (04 §2), profile applied/rejected, override set/cleared/expired, module faulted. Written to the
  host's durable audit journal (Audit's offline path) and relayed to central.
- **Privacy:** diagnosis text, support bundle and the log view go through Privacy masks; reasons are keys, not text (02 §6).
- **Persistence:** `AddYFexStorageEncryption` over the instance's state folder; root key from Cryptography (CNG/TPM on Windows,
  `FileRootKeyProvider` 0600 on Linux). Keeps last-good profile, override, supervisor counters, (later) job store.
- **Resilience:** restart backoff and the crash-loop breaker of 02 §7 are YFex.Resilience policies (no hand-written retry loop).

## 7. YFex.Authorization and YFex.Authentication

- Central: `HostingPermissions` (`hosting.profiles.read/write`, `hosting.status.read`, `hosting.override.allow`) in the Authorization
  catalog, guarding `Hosting.Commands/Queries`.
- Machine: v1 decides by OS groups (04 §2). H4: optional operator sign-in + permission check from the site authorization bundle
  (`hosting.control.module.restart`, `hosting.control.override`).

## 8. Modules of other libraries

| Module | Adapter | Settings | Notes |
|---|---|---|---|
| `offline.broker` | 03 §1 | Offline limits come from the site manifest, not the profile | Windows + Linux (⚑H8) |
| `devices.serial-bridge` | `TcpBridge` / `Rfc2217Server` per configured port | `bridges:{n}:port`, `:tcp`, `:mode` (raw/rfc2217), `:allow` (LAN only) | listener declared → firewall check; Devices' Telnet rule "refuses non-local peers" applies |
| `files.exchange` | app hosts only (handlers are app code) | per exchange (files 05) | ⚑H4 |
| `data.schema` | `AddYFexSchemaMigration(ApplyOnStartup)` | — | for local SQLite stores of other modules |
| `profiling.machine` | §4 | schedule, categories | consent |

## 9. YFex.Licensing (⚑H20, when it exists)

A `HostModule` may declare `RequiresLicense(ModuleId)`. The supervisor then keeps it `Stopped` with reason `hosting.license.missing`
unless Licensing's local check grants it. Licensing stays optional: without it, the requirement is ignored and logged once.
