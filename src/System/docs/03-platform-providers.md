# Platform Providers (`YFex.System.Platform` + `YFex.System.Windows`)

## Why it exists

The telemetry collector needs to ask the operating system a lot of questions — *how much memory, which monitors, is the user idle, what's the active window* — but it must not depend on any specific OS. So `YFex.System` defines the questions as interfaces (`YFex.System.Platform`), and each platform package answers them. Today that package is `YFex.System.Windows`; the abstraction leaves room for others.

Two patterns make the providers safe to call blindly:

- **`DataAvailability<T>` return type.** Instead of throwing or returning null, providers return a union: `Available<T>` (got it), `Denied` (consent/permission), or `Unsupported` (not available on this OS/edition). The collector pattern-matches and records the outcome without ever crashing.
- **They're pure readers.** Providers don't decide *whether* to collect — the consent gate does that upstream. A provider just answers if asked.

## The `IPlatformProvider` surface

One aggregate root exposes every reader plus three eager snapshot captures:

```csharp
public interface IPlatformProvider
{
    IIdleDetector IdleDetector { get; }
    ISessionMonitor SessionMonitor { get; }
    IInstalledSoftwareReader InstalledSoftware { get; }
    IBrowserDataReader BrowserData { get; }
    IHardwareFingerprintProvider Fingerprint { get; }
    IActiveWindowDetector ActiveWindow { get; }
    IDisplayInfoProvider Display { get; }

    SystemSnapshot  CaptureSystemSnapshot();
    NetworkSnapshot CaptureNetworkSnapshot();
    LocaleSnapshot  CaptureLocaleSnapshot();
}
```

## The providers and what they answer

| Provider | Method(s) | Returns | Consent tier* |
|---|---|---|---|
| `CaptureSystemSnapshot()` | — | OS, arch, runtime, processors, app version; (+ memory, machine/user name, elevation, uptime at Analytics) | Functional / Analytics |
| `CaptureLocaleSnapshot()` | — | culture, UI culture, timezone, UTC offset, input language | Analytics |
| `CaptureNetworkSnapshot()` | — | network access, adapters (name, **hashed** MAC, speed, up/down) | Analytics |
| `IDisplayInfoProvider` | `Capture()` | monitors (res, DPI, scaling, primary), theme, high-contrast, accent color | Analytics |
| `IIdleDetector` | `GetCurrentIdleState()` | idle duration + `IsIdle` | Analytics |
| `IUserBehaviourTracker` | `GetSnapshot()` | click/keystroke/scroll/resize counts, active vs idle time | Analytics |
| `IInstalledSoftwareReader` | `GetInstalledSoftware()` | installed app list (name, publisher, …) | Full |
| `IActiveWindowDetector` | `GetActiveWindow()` | process name, PID, window title (title itself is a nested `DataAvailability`) | Full |
| `IBrowserDataReader` | `DetectProfiles()`, `ReadProfile(p)` | installed browser profiles / profile contents | Full |
| `IHardwareFingerprintProvider` | `GetDeviceId()` | stable **hashed** device id (used as `DeviceIdHash`) | used at start |
| `ISessionMonitor` | `SessionEvents`, `StartAsync()` | observable stream of logon/lock/unlock/logoff events | — |

*\* The tier column reflects how `TelemetryCollector` gates each subsystem — it's the collector's policy, not a hard limit on the provider.*

Sensitive identifiers (MAC addresses, device id) are **hashed at the source**, before the pipeline's scrubber even sees them — defense in depth.

## The Windows implementation (`YFex.System.Windows`)

`AddYFexWindows()` registers the full Windows implementation set:

```csharp
services.AddYFexWindows();
```

| Interface | Windows implementation | Backed by |
|---|---|---|
| `IPlatformProvider` | `WindowsProvider` | aggregates the below |
| `IIdleDetector` | `WindowsIdleDetector` | `User32` (`GetLastInputInfo`) |
| `ISessionMonitor` | `WindowsSessionMonitor` | `Microsoft.Win32.SystemEvents` |
| `IInstalledSoftwareReader` | `WindowsInstalledSoftwareReader` | registry uninstall keys |
| `IBrowserDataReader` | `WindowsBrowserDataReader` | browser profile paths |
| `IHardwareFingerprintProvider` | `WindowsHardwareFingerprint` | `System.Management` / WMI |
| `IActiveWindowDetector` | `WindowsActiveWindowDetector` | `User32` (`GetForegroundWindow`) |
| `IDisplayInfoProvider` | `WindowsDisplayInfoProvider` | `Shcore` / `User32` (DPI, monitors) |
| `IUserBehaviourTracker` | `WindowsUserBehaviourTracker` | in-memory counters (fed by input events) |
| `IOfflineQueue` | `SqliteOfflineQueue` | `Microsoft.Data.Sqlite` |

P/Invoke declarations live in `YFex.System.Windows/Interop` (`User32`, `Kernel32`, `Advapi32`, `Shcore`). The project targets `net11.0-windows`.

### The behaviour tracker

`IUserBehaviourTracker` is a write-then-read counter: something calls `RecordClick()`/`RecordKeystroke()`/`RecordScroll()`/`RecordWindowResize()`, and `GetSnapshot()` returns the tallies plus active/idle durations. On its own the Windows tracker just holds counters — the security layer's `InputBehaviourBridge` is what feeds it real input events (see [`04-security.md`](04-security.md)). Without that bridge (or manual calls), counts stay zero. `YFex.System` ships a `NullUserBehaviourTracker` as the no-op default.

## Rules of thumb

- Depend on the interfaces, not the `Windows*` classes — that's what keeps your code portable.
- Every provider call can come back `Denied` or `Unsupported`; pattern-match, don't assume `Available`.
- Register a platform package (`AddYFexWindows`) or the collector gets an empty/`Unsupported` view of the machine and the offline queue is a no-op.
