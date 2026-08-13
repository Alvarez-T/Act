# System & Platform

**Libraries:** `YFex.System`, `YFex.System.Windows`

## Overview

`YFex.System` is the platform-abstraction and telemetry layer of the framework. It answers two questions for an application:

1. **"What is the environment my app is running in?"** — OS, hardware, network, displays, installed software, the active window, user idle/session state, and (with consent) browser data.
2. **"How do I report usage telemetry without violating privacy?"** — a consent-gated, privacy-scrubbing, offline-capable telemetry pipeline.

The core library (`YFex.System`) is **platform-agnostic** — it defines interfaces, data records, discriminated unions, and the telemetry engine, but contains no OS-specific code. Platform implementations live in companion packages; `YFex.System.Windows` is the Windows backend.

Two design rules run through the whole layer:

- **Everything is consent-gated.** No personal data is collected unless the user has granted the matching [`ConsentLevel`](#consent-model). Data you cannot collect is represented as a typed value (`Denied` / `Unsupported`), never a null or an exception.
- **Everything is privacy-scrubbed before it leaves the machine.** URLs are reduced to a salted host hash, MAC addresses and titles are hashed, and IP addresses are masked.

> **Namespace note:** `YFex.System` deliberately shadows the BCL `System` namespace. Inside this assembly (and any assembly that does `using YFex.System;`), fully-qualified BCL types may need a `global::` prefix (e.g. `global::System.Diagnostics.Process`). `using System.Text.Json;`-style directives at the top of a file are unaffected.

---

## What Information Can You Get?

All collectable data is grouped by subsystem. The right-hand column is the `ConsentLevel` required before the data is returned (anything below that level yields a `Denied`).

| Subsystem | Type | Contents | Min. consent |
|---|---|---|---|
| **System** | `SystemSnapshot` | OS description/arch, 64-bit flag, runtime version, processor count, working set & GC memory, machine/user/domain name, elevation, app version/path/uptime | `Functional` |
| **Network** | `NetworkSnapshot` | Per-adapter: name, **hashed** MAC, **masked** IPs, link speed, up/down; plus `HasNetworkAccess` | `Functional` |
| **Locale** | `LocaleSnapshot` | Culture, UI culture, time zone, keyboard layout | `Functional` |
| **Hardware** | `HardwareFingerprint` | A single stable `DeviceIdHash` + the hashing `Algorithm` (no raw identifiers) | `Analytics` |
| **Idle / Session** | via `IIdleDetector`, `ISessionMonitor` | Idle duration, lock/unlock & session-switch events | `Functional` |
| **Active window** | via `IActiveWindowDetector` | Foreground window title / owning process | `Analytics` |
| **Displays** | `DisplaySnapshot` | Monitor count, resolutions, DPI scaling, primary display | `Functional` |
| **User behaviour** | `UserBehaviour` | Click / keystroke / scroll / resize counts, active vs idle duration (counts only — never keystroke *content*) | `Analytics` |
| **Installed software** | `InstalledAppList` | Per app: name, version, publisher, install date | `Analytics` |
| **Browser data** | `BrowserSnapshot` | Profiles, history entries, bookmarks, cookie domains (counts + **hashed** domains), categorized visits | `Full` |

### Discriminated unions for "maybe-available" data

Browser sub-collections and snapshot results never throw. They are `[Union]` types you pattern-match exhaustively:

```csharp
public readonly union DataAvailability<T>(Available<T>, Denied, Unsupported);

// Available<T>(T Value, DateTimeOffset CollectedAt)
// Denied(string Reason, ConsentLevel RequiredLevel)
// Unsupported(string Platform, string Details)
```

```csharp
BrowserSnapshot snap = /* ... */;

switch (snap.History)
{
    case Available<IReadOnlyList<BrowsingHistoryEntry>> a:
        Console.WriteLine($"{a.Value.Count} history entries as of {a.CollectedAt}");
        break;
    case Denied d:
        Console.WriteLine($"Blocked — needs {d.RequiredLevel}: {d.Reason}");
        break;
    case Unsupported u:
        Console.WriteLine($"Not available on {u.Platform}: {u.Details}");
        break;
}
```

The telemetry collector's snapshot uses the same idea at the top level:

```csharp
public union SnapshotResult(PartialResult, Denied, Unsupported, CollectionError);

// PartialResult(DataField[] Collected, CollectionError[] Errors)
//   — a snapshot can partially succeed: collected fields plus per-subsystem errors.
```

---

## Consent Model

Consent is a four-level ladder. Each level is a superset of the one below it.

```csharp
public enum ConsentLevel { None, Functional, Analytics, Full }
```

| Level | Grants collection of |
|---|---|
| `None` | Nothing personal. App still runs. |
| `Functional` | Environment basics: system, network, locale, displays, idle/session. |
| `Analytics` | Adds hardware fingerprint, active window, behaviour counts, installed software. |
| `Full` | Adds browser data (history, bookmarks, cookies). |

The **current** consent decision is stored as a `[Union]` `ConsentState` — richer than the enum because it carries timestamps and the policy version that was agreed to:

```csharp
public union ConsentState(NotAsked, Declined, FunctionalOnly, AnalyticsConsent, FullConsent);
```

It is persisted through `IConsentStore` (the default `FileConsentStore` writes to disk):

```csharp
public interface IConsentStore
{
    ConsentState Load();
    void Save(ConsentState state);
    bool HasBeenAsked { get; }
}
```

A typical first-run flow:

```csharp
var store = serviceProvider.GetRequiredService<IConsentStore>();

if (!store.HasBeenAsked)
{
    // ... show your consent UI, then record the decision:
    store.Save(new AnalyticsConsent(DateTimeOffset.UtcNow, PolicyVersion: "2026-06-01"));
}
```

---

## Telemetry Pipeline

`ITelemetryCollector` is the single entry point for emitting events and capturing snapshots. It is consent-aware and privacy-scrubbing by construction.

```csharp
public interface ITelemetryCollector : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    void Track(string eventName, Dictionary<string, object?>? properties = null);
    Task<SnapshotResult> CollectSnapshotAsync(CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
}
```

- **`Track`** is fire-and-forget — it enqueues a `TelemetryEvent`. Properties are scrubbed (see below) and batched.
- **`CollectSnapshotAsync`** gathers everything the current consent level allows, returning a `SnapshotResult` union (partial success is normal).
- Events are flushed in batches (`BatchSize`, `FlushInterval`) over an `ITelemetryTransport`. If the transport is offline, events spill to an `IOfflineQueue` and replay on reconnect.

### Privacy scrubbing

Every event passes through `PrivacyScrubber.Scrub` before transport. Property keys are matched case-insensitively:

| Key pattern | Transformation |
|---|---|
| `url` / `uri` | Replaced with a salted SHA-256 hash of the **host only** |
| `*title*` | Salted SHA-256 hash |
| `*mac*` | Salted SHA-256 hash |
| `*ip*` (but not `script`/`zip`) | Masked — IPv4 → `a.b.c.xxx`, IPv6 → prefix + `:xxxx` |

```csharp
// Helpers are public if you need them directly:
string hostHash = PrivacyScrubber.HashUrl("https://example.com/path?q=1", salt);
string masked   = PrivacyScrubber.MaskIpAddress("192.168.1.42");   // "192.168.1.xxx"
```

---

## Step-by-Step Usage

### 1. Register telemetry (core)

```csharp
using YFex.System;

services.AddYFexTelemetry(options =>
{
    options.Endpoint      = new Uri("https://telemetry.example.com/ingest");
    options.AppId         = "my-app";
    options.BatchSize     = 50;
    options.FlushInterval = TimeSpan.FromSeconds(30);
    options.HttpTimeout   = TimeSpan.FromSeconds(5);
    options.MaxQueueSize  = 5000;
    options.DropOnFull    = true;
    options.TransportSaltKey = "my-app.telemetry";   // salt for the privacy scrubber
});
```

`AddYFexTelemetry` registers the consent store, consent gate, HTTP transport, a (no-op by default) offline queue, a no-op behaviour tracker, the batch engine, and the collector.

### 2. Add the Windows platform backend

```csharp
using YFex.System.Windows;

services.AddYFexWindows();
```

This binds every platform interface to its Windows implementation and **upgrades two services**: the offline queue becomes a durable `SqliteOfflineQueue`, and the behaviour tracker becomes the real `WindowsUserBehaviourTracker` (global input hooks). Registration order matters — call `AddYFexWindows()` *after* `AddYFexTelemetry()` so the Windows registrations win.

### 3. Start and emit

```csharp
var collector = provider.GetRequiredService<ITelemetryCollector>();
await collector.StartAsync(ct);

collector.Track("page_viewed", new() { ["url"] = "https://app.local/dashboard" });
//                                              ^ scrubbed to a host hash before transport

var result = await collector.CollectSnapshotAsync(ct);
if (result is PartialResult p)
    Console.WriteLine($"{p.Collected.Length} fields, {p.Errors.Length} subsystem error(s)");

await collector.FlushAsync(ct);   // also runs automatically on the flush interval
```

### 4. Query the platform directly (without telemetry)

You don't have to go through the collector. Inject `IPlatformProvider` for synchronous, on-demand reads:

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

    SystemSnapshot CaptureSystemSnapshot();
    NetworkSnapshot CaptureNetworkSnapshot();
    LocaleSnapshot CaptureLocaleSnapshot();
}
```

```csharp
var platform = provider.GetRequiredService<IPlatformProvider>();
SystemSnapshot sys = platform.CaptureSystemSnapshot();
Console.WriteLine($"{sys.OsDescription} — elevated: {sys.IsElevated}, uptime {sys.AppUptimeMs} ms");
```

### 5. Feed the behaviour tracker (optional)

If you want behaviour counts on a platform without input hooks, drive `IUserBehaviourTracker` yourself (this is exactly how the Security layer's input bridge does it):

```csharp
public interface IUserBehaviourTracker
{
    void RecordClick();
    void RecordKeystroke();
    void RecordScroll();
    void RecordWindowResize();
    UserBehaviour GetSnapshot();   // counts + active/idle durations
    void Reset();
}
```

---

## `YFex.System.Windows` — What the Backend Provides

`AddYFexWindows()` registers Windows implementations built on thin P/Invoke wrappers (`User32`, `Kernel32`, `Advapi32`, `Shcore`):

| Service | Implementation | Source |
|---|---|---|
| `IPlatformProvider` | `WindowsProvider` | aggregates all of the below |
| `IIdleDetector` | `WindowsIdleDetector` | `GetLastInputInfo` |
| `ISessionMonitor` | `WindowsSessionMonitor` | WTS session notifications |
| `IInstalledSoftwareReader` | `WindowsInstalledSoftwareReader` | Uninstall registry keys |
| `IBrowserDataReader` | `WindowsBrowserDataReader` | Chrome/Edge/Firefox profile stores |
| `IHardwareFingerprintProvider` | `WindowsHardwareFingerprint` | hashed machine identifiers |
| `IActiveWindowDetector` | `WindowsActiveWindowDetector` | `GetForegroundWindow` |
| `IDisplayInfoProvider` | `WindowsDisplayInfoProvider` | `EnumDisplayMonitors` + per-monitor DPI |
| `IOfflineQueue` | `SqliteOfflineQueue` | durable SQLite spill buffer |
| `IUserBehaviourTracker` | `WindowsUserBehaviourTracker` | low-level input hooks |

---

## Reference

### Internal building block: `ObservableSource<T>`

`YFex.System.Internal.ObservableSource<T>` is a lightweight, thread-safe `IObservable<T>` used across the framework to expose event streams (the Security watchers build on it). It is `internal` and shared via `InternalsVisibleTo` — consumers see only the `IObservable<T>` surface and subscribe with a standard `IObserver<T>`.

### Key namespaces

| Namespace | Holds |
|---|---|
| `YFex.System.Telemetry` | `ITelemetryCollector`, `TelemetryOptions`, `PrivacyScrubber`, transport & batch |
| `YFex.System.Consent` | `ConsentLevel`, `ConsentState` union, `IConsentStore` |
| `YFex.System.Platform` | all `I*` platform interfaces |
| `YFex.System.Data` | snapshot records (`SystemSnapshot`, `NetworkSnapshot`, `BrowserSnapshot`, …) |
| `YFex.System.Unions` | `DataAvailability<T>`, `SnapshotResult`, supporting records |

### See also

- [Security](security.md) — the network/process intelligence toolkit built on top of this layer.
- [Core Utilities](core.md) — primitives and the `[Union]` mechanism used throughout.
