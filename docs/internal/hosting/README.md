# YFex.Hosting — one service host per machine role, module profiles from central, tray and CLI control — Plan

> **Status:** PROPOSED 2026-11-03. No code. ⚑H1 decided 2026-10-19. ⚑H2–H7, H9, H10 answered by the user 2026-11-03 (§5); ⚑H8 and
> ⚑H11–H22 wait for the user. Tray = last phase (H5, user 2026-11-03). Hosting never sends other libraries' data to
> central (05 §0). **Phase H0 is needed by Offline 4f** (the broker's host and its architecture test).
> **Repo:** `C:\Dev\YFex\YFex.Hosting` (sibling, template YFex.Networking / YFex.Time; own `hosting.slnx`). **Core types:** none (⚑H11).
> **Origin:** 2026-10-19, while building authentication AN-P4, the user said the ecosystem will grow many background services that must
> run in **one executable**, not one process each (orientation kept in §1–§3). 2026-11-03 the user added: control from a **tray icon**,
> integration with **YFex.Profiling, YFex.Logging, YFex.Networking** and the rest of the ecosystem, module configuration **from central
> through Tenancy/Installations**, several host kinds per machine, **YFex.Jobs inside the host**, self-update in **YFex.Installer**.

## Files

| File | What it covers |
|---|---|
| README (this file) | why, scope, shape, packages, decisions, phase summary |
| [01 — Inventory](01-current-state-and-inventory.md) | the hosted services every library already ships, existing hosts (Security.Service, Jobs Hub.Worker), what is reused (file:line) |
| [02 — Model and core types](02-model-and-core-types.md) | `HostModuleId`, `HostModule`, the catalog, host kinds and instances, the profile, generations, module states, health |
| [03 — API and examples](03-api-and-examples.md) | **dev-side code first**: the generic `yfex-host`, an app's own host, the central side, the command line |
| [04 — Control channel and tray](04-control-channel-and-tray.md) | the local control channel (named pipe / Unix socket), its permissions, `yfex-tray` (Avalonia), the CLI |
| [05 — Ecosystem integration](05-ecosystem-integration.md) | Jobs, Logging, Networking, Profiling, Installations, Tenancy, Time, Audit, Privacy, Persistence, Resilience, Authorization, Licensing, Devices, Files, Data… |
| [06 — Platforms, install, security, AOT](06-platforms-install-security-aot.md) | Windows service / systemd / launchd, accounts, folders, ACLs, the minimal install command, threats, AOT and trimming gates |
| [07 — Phases, acceptance, log](07-phases-and-acceptance.md) | phases H0–H4 with acceptance, test/benchmark/AOT gates, implementation log |

Related: [YFex.Installer](../installer/README.md) (full install, firewall, self-update — out of this plan), [offline 07](../offline/07-what-runs-where.md),
[offline 11 Phase 4](../offline/11-phases-and-acceptance.md), [installations 09](../installations/09-platforms.md).

## 1. What runs in the background today

Every YFex library that needs background work already ships it as `IHostedService`s registered by its own `Add…` extension. No
library starts a process of its own (full list with file references in [01](01-current-state-and-inventory.md)).

| Library | Hosted services (registered by) | Runs on |
|---|---|---|
| Messaging.Rpc | `OutboxReplayer`, `ServerPermissionsFeed` (`AddYFexMessagingRpcClient/Server`) | apps, central |
| Installations | state/renewal service, `BeaconService`, `RpcCentralBundleRefresh`, central relay ingress | apps, brokers, central |
| Offline | connectivity, site manifest, relay forwarder, custody release, pull-link binder; broker: verifier tracker, journal forwarder… | terminals, brokers, central |
| Authorization.Installations | `SignedSnapshotRefresh`, publisher keys | terminals, central |
| Orchestration, EventSourcing | scheduler, process managers, projections | central |
| Jobs | `SchedulerHost` (the in-process job scheduler) | anywhere it is added |
| Time, Networking | `TimeSynchronizationService`, `NetworkConditionsMonitor` | everywhere |
| Security | capture/analysis services (`YFex.Security.Service` already composes several in one host) | security host |

**So one executable per machine role is already the model**: a composition root calls the `Add…` of every library it needs, and the
Generic Host runs all their hosted services in one process. YFex.Hosting makes that composition root **generic, configured from
central, observable and controllable**, so a store PC runs one YFex service instead of one per library.

## 2. Which pieces are services

| Piece | Service? | Where it runs |
|---|---|---|
| `YFex.Authentication.Client`, `.Offline` | **No.** Libraries inside the desktop/mobile app; their timers stop with the app (no app, no signed-in person) | the POS / back-office app |
| `YFex.Authentication.Offline.Central` | **No.** A data source (`ISiteCredentialSource`) read by Offline's bundle publisher | central's server process |
| Offline's **site broker** | **Yes**: runs without anyone signed in, all day, on one PC of the store | module `offline.broker` of `yfex-host` |
| Files Exchange poller, Devices serial/TCP bridges, Profiling's machine pipeline, Logging shipping | **Yes** | modules of `yfex-host` or of an app's own host (⚑H4) |
| YFex.Security capture service | **Yes, but separate** (needs elevated rights; ⚑H6) | its own service, installed by YFex.Installer |

## 3. What YFex.Jobs is and is not

- **YFex.Jobs (Product A)** is an *embedded* scheduler: `[Job]` methods run inside whatever process calls `AddYFexJobs`. In the host
  it is **always on** (⚑H10): periodic work of every module is visible, overridable and runnable from the tray/CLI.
- **YFex.Jobs.Hub.Worker (Product C)** launches **other** executables. That is the opposite of "one executable": use it only for
  processes that must stay separate. Its `ServiceInstaller` code moves to YFex.Hosting (⚑H7, [01](01-current-state-and-inventory.md) §3).
- **Rule for libraries (unchanged):** keep shipping `IHostedService`s through `Add…`; **no library references YFex.Hosting or
  YFex.Jobs** to be hostable. A library whose work is a schedule may *also* offer a `[Job]` class in an optional `.Jobs` package.

## 4. Shape

```
                      CENTRAL (ASP.NET Core app, ⚑H5: not yfex-host)
        YFex.Hosting.Central: host profiles per scope (Tenancy chain) → signed bundle "hosting.profile"
                              host status store ← relay item "hosting.status"
                                     │  Installations bundle pull/gossip  ▲ Installations relay
                                     ▼                                    │
 STORE PC (PC-GERENTE) ───────────────────────────────────────────────────┼──────────────────────────────
  Windows service "YFex Host" (yfex-host, Native AOT, low-privilege account, machine keys in TPM)
  ├─ generation N  = one Generic Host built from the effective profile
  │   ├─ base:    installations · time · networking · logging · jobs · persistence · hosting.status
  │   └─ modules: offline.broker · devices.serial-bridge · files.exchange · profiling.machine … (from the profile)
  ├─ supervisor: per-module state, health, crash-loop breaker (Resilience), audit of control actions
  └─ control channel: \\.\pipe\yfex-host-default  (ACL: Administrators, "YFex Host Operators"; Interactive = read)
          ▲                                  ▲
  yfex-tray (Avalonia, user session)    yfex-host status|modules|jobs|logs|diagnose (CLI)
  status icon · modules · jobs · network diagnosis · live logs · consent · session signals → Profiling
```

- **One library, many hosts (⚑H4):** `YFex.Hosting` is a library. The **generic** host `yfex-host` (`YFex.Hosting.Host`) composes only
  YFex modules and references no app assembly (architecture test, ⚑O20). An app may build **its own** service executable from the same
  library with its own modules; several hosts can run on one machine, each an **instance** with its own service name, folder and
  control channel, and the tray shows all of them.
- **Modules are compiled in, profiles switch them on (⚑H2):** no plugin loading (Native AOT). A module is a static descriptor in a
  catalog; the profile says which are on and with which settings.
- **Profiles come from central (⚑H3):** central resolves a profile along the installation's **Tenancy scope chain** (tenant → site →
  installation, most specific wins) and publishes it as an Installations **signed bundle** addressed to the installation. A local
  `hosting.json` holds only bootstrap data (central URL, instance name) and a first-run profile.
- **Generations:** the DI container is immutable, so a profile that changes the module set builds a **new generation** of the host in
  the same process (graceful stop of the old one, then start of the new one). Settings-only changes reach modules through
  `IOptionsMonitor` without a new generation.

## 5. Decisions

| # | Question | Decision / recommendation | Alternative | Where |
|---|---|---|---|---|
| **H1 ✅ 2026-10-19** | Where does the shared host live, and when is it built? | New repo **`YFex.Hosting`** (host executable + module registry + service install), built **with Offline Phase 4**, `offline.broker` its first module | put it in YFex.Jobs; or every product writes its own host | §3 |
| **H2 ✅ 2026-11-03** | How do modules get into an AOT host? | **Compiled in**; the profile only switches them on/off and sets them | load plugin assemblies at run time (impossible with Native AOT, and a code-injection path) | 02 §2 |
| **H3 ✅ 2026-11-03** | Where does the module configuration come from? | **From central**: profile per scope resolved on the **Tenancy** scope chain, delivered as an **Installations** signed bundle (`hosting.profile`); local file = bootstrap + first-run only | local `hosting.json` signed by an admin | 02 §5 |
| **H4 ✅ 2026-11-03** | App-specific services vs "no app code" | **Generic**: one library, several host **kinds** (the generic `yfex-host`, app-built hosts) and **several instances** per machine; the architecture test covers the generic host | allow app assemblies in the generic host | 02 §3 |
| **H5 ✅ 2026-11-03** | Central's role | Central keeps its **own ASP.NET Core app**; it may use the library's catalog/supervisor, but `yfex-host` is for store/edge machines | central runs `yfex-host` too | 03 §3 |
| **H6 ✅ 2026-11-03** | Security.Service as a module? | **No**: it needs elevated rights; it stays its own service and is **installed by YFex.Installer** | module of `yfex-host` (would raise the host's privileges) | 06 §1 |
| **H7 ✅ 2026-11-03** | Service install now or with YFex.Installer? | Hosting ships a **minimal** `install` / `uninstall` (service, account, folders, ACLs, tray auto-start; code moved from Hub.Worker `ServiceInstaller`); firewall, enrollment UI, updates = **YFex.Installer** | everything in YFex.Installer (no way to run the broker before it exists) | 06 §3 |
| **H8** | Platforms and builds in v1 | Native AOT **win-x64**, **linux-x64**, **linux-arm64**; **linux-arm** (32-bit Pi) self-contained JIT (no AOT there); **macOS**: host + tray build and run, **no broker module** (installations R4) | Windows only in v1 | 06 §2 |
| **H9 ✅ 2026-11-03** | Executable and service names | `yfex-host` / service "YFex Host" (instance `default`; others "YFex Host (name)"); `yfex-tray`. Old `YFex.Offline.Broker.exe` examples are updated | one executable per product | 03 §4 |
| **H10 ✅ 2026-11-03** | Jobs in the host? | **Yes, always on** in the base: one scheduler per host, jobs visible/runnable from tray and CLI; libraries still don't reference Jobs | plain `PeriodicTimer` services only (no overview, no overrides, no "run now") | 05 §1 |
| **H11** | Core types? | **None**: every Hosting type is named only by Hosting and its tray/CLI | `HostModuleId` in core (no second library names it) | 02 §1 |
| **H12** | Control channel transport | **Named pipe** (Windows) / **Unix domain socket** (Linux, macOS) with a small versioned binary codec (golden-pinned, like Offline's), OS ACL + peer credentials | Messaging.Rpc/Fusion over loopback TCP (heavier, a port to protect) | 04 §1 |
| **H13** | Who may control the host | Read status: **interactive users**; control (restart module, run job, apply override): **Administrators + local group "YFex Host Operators"**; every control action **audited**. Operator sign-in through Authentication = later (H4 phase) | sign-in for every action from day one | 04 §2 |
| **H14** | Tray technology | **Avalonia** `TrayIcon` + small windows (AOT, Windows/macOS/Linux SNI); view models on YFex.State; one tray per user session for **all** host instances | WinForms `NotifyIcon` (Windows only) | 04 §3 |
| **H15** | Profiling in a service | Split: **machine** providers and the telemetry pipeline run in the host (`profiling.machine`); **session** providers (idle, lock, active window, behaviour counts) run in the **tray** and forward through the control channel; machine-scope consent edited in the tray; default **off** | everything in the service (impossible: session 0 isolation) | 05 §4 |
| **H16** | Module failure policy | A failing module never stops the host: `BackgroundServiceExceptionBehavior.Ignore` + supervisor restarts the module's hosted services with backoff; **crash-loop breaker** (5 failures / 10 min → `Faulted`, finding, tray toast); base modules failing = host `Degraded` | .NET default (`StopHost`) and rely on SCM restart | 02 §7 |
| **H17** | Local override of the central profile | Allowed only when the profile says `AllowLocalOverride` (technician mode), time-limited (default 4 h), audited, shown in the tray | never; or always | 02 §5.3 |
| **H18** | Where built-in module descriptors live | In **`YFex.Hosting.Host`** (the generic exe) as adapters over each library's `Add…`; libraries ship no Hosting code | each library ships a `.Hosting` package (would make libraries depend on Hosting) | 02 §2 |
| **H19** | Status to central | Relay item **`hosting.status`** (signed, like `installations.diagnostics`) on change + every 15 min; central keeps the latest per installation | Rpc query from central (needs a listener path to the host) | 05 §5 |
| **H20** | Names that clash with Licensing | Hosting says **host module** (`HostModuleId`, e.g. `offline.broker`); Licensing's `ModuleId` (`fiscal`) stays; a host module may **require** a license module (when Licensing exists) | one shared "module" concept | 05 §9 |
| **H22** | Should the host drain spools (logs, audit) left by **closed apps** on the same machine? | **Not in v1.** Each process ships its own data; senders are owned by Logging/Audit/Profiling, the road by Installations/Rpc (05 §0); a later machine-wide forwarder would be a Logging/Audit-owned module | Hosting forwards every process' data |  05 §0 |
| **H21** | Identity of an app-built host on a machine that already runs `yfex-host` | its **own installation** (own enrollment, shown in the roster, same machine hint) | share the generic host's installation (needs a local key-sharing path) | 05 §5 |

## 6. Packages

| Package | Contents | Depends on |
|---|---|---|
| `YFex.Hosting` | `HostModuleId`, `HostModule`, `HostModuleCatalog`, `HostProfile`, `HostBuilder` (generations), supervisor, module states/health, profile `IConfigurationSource`, command-line verbs, `AddYFexServiceHost` | YFex core, M.E.Hosting, M.E.Configuration, YFex.Resilience |
| `YFex.Hosting.Control` | control-channel contract, codec, server (in the host) and client (tray, CLI), `HostingPermissions` catalog | YFex core, YFex.Hosting (server part only via DI) |
| `YFex.Hosting.Windows` | service lifetime, SCM install/uninstall, virtual service account, folder/pipe ACLs, Event Log for start-up failures | YFex.Hosting, M.E.Hosting.WindowsServices |
| `YFex.Hosting.Linux` | systemd notify/watchdog, unit writer, `yfex` user/group, socket peer credentials | YFex.Hosting, M.E.Hosting.Systemd |
| `YFex.Hosting.MacOS` *(H4)* | launchd plist writer, daemon user | YFex.Hosting |
| `YFex.Hosting.Installations` | `hosting.profile` bundle kind + tracker, `hosting.status` relay item, scope of the host from enrollment | YFex.Hosting, YFex.Installations |
| `YFex.Hosting.Central` | profile authoring per scope (Tenancy chain), publisher, status store, CQRS commands/queries + permissions | YFex.Hosting, YFex.Installations.Central, YFex.Tenancy, YFex.Cqrs |
| `YFex.Hosting.Host` | **the generic executable `yfex-host`**: built-in module table (H18) | all of the above + the YFex libraries it hosts (no app assembly) |
| `YFex.Hosting.Tray` | **the executable `yfex-tray`** (Avalonia) | YFex.Hosting.Control, Avalonia, YFex.State, YFex.Profiling (session providers) |
| `YFex.Hosting.Testing` | `HostHarness` (in-memory host + control client), fake profile source, fake service manager | YFex.Hosting, YFex.Hosting.Control |

## 7. Phase summary

| Phase | Content | Needed by |
|---|---|---|
| **H0** | repo, `YFex.Hosting` (catalog, generations, supervisor, local profile), `.Windows` + `.Linux` service lifetime and minimal install, `yfex-host` with base + `offline.broker`, `--enroll`, architecture test, AOT smoke | **Offline 4f** |
| **H1** | profiles from central (`.Installations` + `.Central`, Tenancy chain, last-good profile, generations on change), `hosting.status` relay, audit | first store roll-out |
| **H2** | control channel + CLI verbs (status, modules, jobs, logs, diagnose, override) | support in shops |
| **H3** | more modules: `devices.serial-bridge`, `files.exchange`, `profiling.machine`, `data.schema`; the shippers of Logging/Audit/Profiling run as their libraries deliver them; license gating when Licensing exists | as those libraries land |
| **H4** | macOS (`.MacOS`, launchd), Linux TPM keys, operator sign-in for control (Authentication), multiple-instance polish | later |
| **H5 (last)** | `yfex-tray` (Avalonia) on the H2 control channel; Profiling session signals from the tray | user 2026-11-03: tray last |

Details and acceptance: [07](07-phases-and-acceptance.md).
