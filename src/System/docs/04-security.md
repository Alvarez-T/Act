# Security Intelligence (`YFex.System.Windows.Security`)

## Why it exists

The telemetry stack answers *"what is this machine?"*. The security layer answers a sharper question: *"what is happening on this machine right now, and does any of it look dangerous?"* It watches the live surfaces attackers use — processes, network connections, files, the registry, keyboard/mouse input — and can scan content with the OS antivirus engine. It's built for endpoint monitoring, insider-risk signals, and threat telemetry.

Everything here is **read-only inspection**. It observes and reports; it does not block, kill, or quarantine. Results flow into the same consent-gated, privacy-scrubbed telemetry pipeline as everything else, via bridges.

> This is a dual-use capability layer. It's designed for an app monitoring the device it runs on (endpoint agents, security-aware desktop apps) with the user's consent — not for surveilling third parties.

## The modules

`AddYFexSecurity()` registers eleven services across seven areas. Targets `net11.0-windows`.

| Area | Interface | Does |
|---|---|---|
| **Process** | `IProcessInspector` | Enumerate processes, look one up by PID, list a process's loaded modules. |
| | `IProcessWatcher` | `IObservable<ProcessEvent>` stream of process created/terminated. Start/Stop. |
| **Network** | `INetworkInspector` | List TCP connections / UDP endpoints, globally or filtered by owning PID. |
| **Antivirus** | `IAmsiScanner` | Scan a buffer or file through Windows **AMSI** (Antimalware Scan Interface). |
| | `IAntivirusProvider` | Report Windows Defender status. |
| **File system** | `IFileScanner` | Hash a file, verify its Authenticode signature, scan a file or a directory. |
| | `IFileWatcher` | `IObservable<FileChangeEvent>` for created/modified/deleted/renamed. `WatchSecurityPaths()` watches known-sensitive locations. |
| **Registry** | `IRegistryMonitor` | `IObservable<RegistryChangeEvent>` for value/key changes. |
| | `IPersistenceDetector` | Enumerate autostart entries; diff against a baseline to surface **new persistence** (a classic malware foothold). |
| **Integrity** | `ISystemIntegrityChecker` | One report covering Secure Boot, UAC, firewall, and Windows Update status. |
| **Input** | `ISystemInputMonitor` | `IObservable<InputEvent>` low-level keyboard/mouse stream. Start/Stop. |

The observable modules (`IProcessWatcher`, `IFileWatcher`, `IRegistryMonitor`, `ISystemInputMonitor`) are `IDisposable` and must be `Start()`ed. Their events are discriminated unions (e.g. `ProcessEvent` → `ProcessCreated`/`ProcessTerminated`, `FileChangeEvent` → `FileCreated`/`FileModified`/`FileDeleted`/`FileRenamed`).

Native interop for these lives in `YFex.System.Windows.Security/Interop` (`AmsiInterop`, `HookInterop` for the input hooks, `IphlpapiInterop` for the TCP/UDP tables).

## The bridges — wiring security into telemetry

The watchers produce raw event streams; the bridges turn those into pipeline activity. Two are provided.

### `SecurityTelemetryBridge` — security events → telemetry

Subscribes watcher streams and calls `ITelemetryCollector.Track(...)` for each event, so security activity flows through the same batching, scrubbing, and offline-queue machinery as everything else.

```csharp
var bridge = new SecurityTelemetryBridge(collector);
bridge.SubscribeProcessEvents(processWatcher);   // → "security.process.created" / ".terminated"
bridge.SubscribeFileEvents(fileWatcher);         // → "security.file.created" / ".modified" / ".deleted" / ".renamed"
bridge.SubscribeRegistryEvents(registryMonitor); // → "security.registry.valueChanged" / ".keyCreated" / ".keyDeleted"
```

Because it goes through `Track`, every emitted event carries the consent state at collection time and is privacy-scrubbed before shipping (file paths, values, etc. follow the scrubber's rules).

### `InputBehaviourBridge` — raw input → behaviour counts

Bridges the low-level input monitor into the platform layer's `IUserBehaviourTracker`. Keyboard-down → `RecordKeystroke()`; mouse button-down → `RecordClick()`; scroll → `RecordScroll()`. This is what makes the Analytics-tier behaviour counters (`clicks`, `keystrokes`, `scrolls`) non-zero.

```csharp
var input = new InputBehaviourBridge(inputMonitor, behaviourTracker);
input.Start();   // starts the monitor if it isn't already running
```

> The bridge only records **counts** — it never captures which keys or what text. That distinction is deliberate: behaviour analytics without keylogging.

## Putting it together

```csharp
services.AddYFexTelemetry(o => { o.Endpoint = new("https://telemetry.myapp.com/ingest"); });
services.AddYFexWindows();     // platform providers + behaviour tracker + SQLite queue
services.AddYFexSecurity();    // the watchers/inspectors above

// After building the provider and recording consent:
await collector.StartAsync(ct);

var secBridge = new SecurityTelemetryBridge(collector);
secBridge.SubscribeProcessEvents(sp.GetRequiredService<IProcessWatcher>());
sp.GetRequiredService<IProcessWatcher>().Start();

new InputBehaviourBridge(
    sp.GetRequiredService<ISystemInputMonitor>(),
    sp.GetRequiredService<IUserBehaviourTracker>()).Start();
```

## Rules of thumb

- The bridges are **not auto-registered** — construct and start them yourself, and dispose them at shutdown (both are `IDisposable`).
- Watchers must be `Start()`ed and disposed; they hold OS handles/hooks.
- On-demand inspectors (`IProcessInspector`, `INetworkInspector`, `IFileScanner`, `ISystemIntegrityChecker`, `IPersistenceDetector`) are cheap to call ad hoc; the observable watchers are the long-lived ones.
- Security events reach the wire only through the telemetry pipeline, so they inherit consent gating and privacy scrubbing — collect responsibly and only on devices you're authorized to monitor.
