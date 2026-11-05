# 01 — Current state and inventory

> Surveyed 2026-11-03 against the sibling repos. `C:\Dev\YFex\YFex.Hosting` does not exist yet. File references are to the sibling repos
> under `C:\Dev\YFex`.

## 1. Hosted services the host will run (all exist, none needs a change to be hosted)

| Library | Registration | Hosted services / work | Notes for the host |
|---|---|---|---|
| YFex.Installations | `AddYFexInstallations(i => i.Application(…).SiteBroker().Storage(Machine…).Peers(…))` | state/renewal, `BeaconService`, bundle refresh, peer endpoint (:7443), relay | base of the generic host; machine-scope storage; relay carries `hosting.status` (H19) |
| YFex.Offline.Broker | `AddYFexOfflineBroker()` + `AddSiteSessions`, `AddSiteCustody`, coordination, feed — `YFex.Offline/src/YFex.Offline.Broker/DependencyInjection/OfflineBrokerServiceCollectionExtensions.cs:63` | channels `offline.*`, `SiteVerifierTrackerService`, `SiteJournalForwarder` | **first module** (`offline.broker`); dormant unless the roster says `Active` (reports `Dormant`, 02 §7) |
| YFex.Time | `AddYFexTime(…)` | `TimeSynchronizationService` (`YFex.Time/src/YFex.Time/TimeSynchronizationService.cs:18`, `IHostedLifecycleService`) | base; trusted time for job schedules, logs, status |
| YFex.Networking | `AddYFexNetworking(…)` | `NetworkConditionsMonitor` (`YFex.Networking/src/YFex.Networking/Conditions/NetworkConditionsMonitor.cs:48`) | base; conditions for modules and the tray; diagnosis on demand |
| YFex.Logging | `AddYFexLogging(…)`; `LiveTapSink` (`YFex.Logging/src/YFex.Logging/Sinks/MemorySink.cs:85`) | sinks; shipping (`YFex.Logging.Shipping`) designed, **not built** | base; live tap feeds the tray/CLI log view |
| YFex.Jobs | `AddYFexJobs(…)`; `SchedulerHost : BackgroundService` (`YFex.Jobs/src/YFex.Jobs/Engine/SchedulerHost.cs:16`) | scheduler loop | base (H10); job store today only `.Persistence.EntityFrameworkCore` (ask, 05 §1) |
| YFex.Profiling | `AddYFexProfiling(…)`, `AddYFexProfilingWindows()` | `TelemetryCollector`, offline queue, transport; Windows providers | split machine/session (H15) |
| YFex.Messaging.Rpc | `AddYFexMessagingRpcClient` | `OutboxReplayer`, permissions feed | used by the broker's uplink to central (`AddSiteJournalUplink()`) |
| YFex.Devices | `TcpBridge`, `Rfc2217Server`, virtual COM backends (`YFex.Devices/docs/04-serial.md`, `07-virtual-com-driver.md:119`: "host it as a Windows service") | bridges, watchers | module `devices.serial-bridge` (H3) |
| YFex.Files *(planned)* | `.Exchange` poller | inbox/outbox polling | module `files.exchange` (files 05: "runs as a hosted service in the machine's single host") |
| YFex.Data | `AddYFexSchemaMigration(o => o.ApplyOnStartup = true)` (data-schema 04 §6, option B) | schema apply at start | module `data.schema` for local SQLite stores |

## 2. Hosts that exist today

| Host | Where | What it does | Relation to YFex.Hosting |
|---|---|---|---|
| `YFex.Security.Service` | `YFex.Security/YFex.Security.Service/SecurityHostBuilder.cs` | hand-written Generic Host: Serilog, storage, ETW/pcap capture, analysis, migrations | stays separate (H6), installed by YFex.Installer; may later adopt the `YFex.Hosting` library and the control contract to show in the tray (07 H4, optional) |
| `YFex.Jobs.Hub.Worker` (`yfexworker`) | `YFex.Jobs/src/YFex.Jobs.Hub.Worker` | per-machine **launcher** of other executables; owns child handles; mTLS to the Hub | different purpose (launches processes). Its install and single-instance code is the starting point for ours (§3) |
| Offline broker "host" | only sketches: `offline/02 §5.1`, `installations/03 §1` (`YFex.Offline.Broker.exe --enroll …`) | — | replaced by `yfex-host --enroll …` (H9); those sketches are updated |
| `samples/YFex.Logging.Server.Host` | Logging repo | ASP.NET Core sample | central-type host, not this plan (H5) |

## 3. Code reused from YFex.Jobs.Hub.Worker

| File | What it does | What changes when it moves to `YFex.Hosting.Windows` / `.Linux` |
|---|---|---|
| `Install/ServiceInstaller.cs` (141 lines) | `sc.exe create … start= auto`, `failure … restart/5000`, systemd unit with `Restart=always` at `/etc/systemd/system/yfex-worker.service` | runs as **`NT SERVICE\YFexHost`** (virtual account, `obj=`), not LocalSystem; instance-aware names; delayed auto-start; SCM API instead of `sc.exe` parsing; unit gets `User=yfex`, `StateDirectory=yfex/{instance}`, `RuntimeDirectory`, `Type=notify`, `WatchdogSec`; idempotent |
| `SingleInstance/SingleInstanceGuard.cs:21` | named `Local\` mutex | `Global\YFex.Hosting.{instance}` (service session 0 vs console run) |
| `Restart/OrphanReadopter.cs` | re-adopts child processes | not needed (no child processes) |

Hub.Worker keeps its own copy until Jobs decides to reference `YFex.Hosting.Windows` (Jobs' choice, not required).

## 4. What .NET 11 gives us

| Need | BCL / Microsoft.Extensions | AOT |
|---|---|---|
| Windows service lifetime | `Microsoft.Extensions.Hosting.WindowsServices` (`AddWindowsService`) | to confirm in the H0 smoke (it uses `ServiceBase`; no reflection expected) |
| systemd lifetime, notify, journald format | `Microsoft.Extensions.Hosting.Systemd` (`AddSystemd`) | same |
| Config binding without reflection | configuration-binding source generator (`EnableConfigurationBindingGenerator`) | yes |
| Hosted-service failures | `HostOptions.BackgroundServiceExceptionBehavior`, `IHostedLifecycleService` | yes |
| Named pipes with ACL | `NamedPipeServerStreamAcl.Create(…, PipeSecurity)` | yes |
| Unix sockets + peer credentials | `UnixDomainSocketEndPoint`; `SO_PEERCRED` through `Socket.GetRawSocketOption` | yes |
| Tray icon | Avalonia `TrayIcon` + `NativeMenu` (Windows, macOS, Linux StatusNotifierItem) | Avalonia supports Native AOT with compiled bindings |

## 5. Gaps and risks found while surveying

| # | Finding | Effect | Where handled |
|---|---|---|---|
| G1 | The broker sketches still name `YFex.Offline.Broker.exe` | wrong executable in docs | H9; fixed in offline 02 and installations 03 |
| G2 | A Windows **virtual service account** must be able to open the **TPM machine key** created by `CngSigningKeyStore` (machine scope) | if not, the host needs LocalSystem (rejected by ⚑O20's low-privilege rule) | **H0 spike**, first item; fallback: grant the key's ACL to `NT SERVICE\YFexHost` at install |
| G3 | ~~Jobs' only durable `IJobStore` is EF Core (not AOT-friendly)~~ **Resolved:** `YFex.Jobs.Persistence.KeyValue` (`KeyValueJobStore`, A2 phase 6) | — | use `UseKeyValueStore()` / `AddYFexJobsKeyValueStore()` (05 §1) |
| G4 | `InstallationPeersBuilder.Publishers(...)` has **one slot** (Offline passes "other publishers" through `OfflineBundles(otherPublishers)`) | Hosting's `hosting.profile` publisher must be composed into that slot | 05 §5; ask Installations for an additive publisher registration |
| G5 | Logging shipping is designed, not built | host logs stay local in H0–H1 | 05 §2 |
| G6 | Profiling's session providers cannot work in session 0 | idle/lock/active-window signals are empty in a service | H15 split |
| G7 | macOS daemons cannot use the data-protection Keychain (crypto 14 §5.2) | no hardware keys for a macOS broker | H8: no broker module on macOS |
