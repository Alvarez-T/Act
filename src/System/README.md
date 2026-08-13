# YFex.System

The system stack is YFex's **device-awareness layer**. It answers one question safely: *"What can the app know about the machine and session it's running on — and how do I collect that without violating the user's privacy?"*

It gathers system, session, hardware, network, display, and behavioural signals; gates every field behind explicit **user consent**; scrubs personal data before it leaves the device; and ships it in resilient, offline-tolerant batches. On top of that, an optional **security-intelligence** layer watches processes, network connections, files, and the registry.

Three design commitments run through the whole stack:

1. **Consent is not optional plumbing — it's the gate.** Every piece of data is tagged with the `ConsentLevel` it requires. Nothing above the granted level is ever collected.
2. **Telemetry must never crash the host.** Every collection and ship path swallows its own exceptions and degrades to a partial result.
3. **Abstractions are platform-agnostic; implementations are not.** `YFex.System` defines interfaces and the pipeline; `YFex.System.Windows` provides the Windows implementations.

---

## The projects at a glance

| Project | TFM | Purpose |
|---|---|---|
| **YFex.System** | `net11.0` | Platform-agnostic abstractions, the consent model, the telemetry pipeline (collector, batching, privacy scrubbing, transport), and union result types. |
| **YFex.System.Windows** | `net11.0-windows` | Concrete Windows implementations of every platform provider, plus a SQLite offline queue and input-driven behaviour tracker. |
| **YFex.System.Windows.Security** | `net11.0-windows` | Security intelligence: process/network inspection, AMSI antivirus scanning, file integrity, registry & persistence monitoring, input monitoring — with bridges into the telemetry pipeline. |

### How they stack

```
YFex.System                     abstractions + consent + telemetry engine   ← the contract
   └─ YFex.System.Windows            Windows platform providers (implements IPlatformProvider)
        └─ YFex.System.Windows.Security   process/network/file/registry/AV watchers → telemetry
```

`YFex.System` has no OS dependency. Everything Windows-specific — P/Invoke, WMI, the registry, AMSI — lives in the two `.Windows*` projects.

---

## The consent gate (read this first)

`ConsentLevel` is a strict ladder. Each tier unlocks the tiers below it:

| Level | Unlocks |
|---|---|
| `None` | Nothing is collected. |
| `Functional` | OS, architecture, runtime, processor count, app version. |
| `Analytics` | + locale, network adapters, displays, idle state, behaviour counts, richer system metrics (memory, machine/user name, elevation). |
| `Full` | + installed software, active window, browser profiles. |

The user's actual answer is a `ConsentState` union (`NotAsked`, `Declined`, `FunctionalOnly`, `AnalyticsConsent`, `FullConsent`) that also records **when** and **against which policy version** it was given. The collector checks `IsAllowed(level)` before touching any subsystem, so raising or lowering consent immediately changes what's gathered — and a **downgrade forces a flush** so nothing collected under the old level lingers.

---

## 60-second tour

```csharp
// 1. Register the pipeline + a platform
services.AddYFexTelemetry(o =>
{
    o.Endpoint = new("https://telemetry.myapp.com/ingest");
    o.AppId = "my-app";
    o.BatchSize = 50;
    o.FlushInterval = TimeSpan.FromSeconds(30);
});
services.AddYFexWindows();          // Windows platform providers + SQLite offline queue
services.AddYFexSecurity();         // optional: security watchers

// 2. Record the user's choice (e.g. from a consent dialog)
consent.UpdateConsent(new AnalyticsConsent(DateTimeOffset.UtcNow, "policy-2026-01"));

// 3. Start collecting
await collector.StartAsync(ct);

// 4. Track custom events…
collector.Track("checkout.completed", new() { ["items"] = 3 });

// 5. …or take a full consent-gated snapshot of the machine
SnapshotResult result = await collector.CollectSnapshotAsync(ct);
// result is a union: PartialResult | Denied | Unsupported | CollectionError
```

Everything shipped is privacy-scrubbed (URLs/titles/MACs hashed, IPs masked) and, if the network is down, persisted to the offline queue and retried on next startup.

---

## Where to read next

- [`docs/01-consent-and-privacy.md`](docs/01-consent-and-privacy.md) — the consent ladder, `ConsentState`, storage, and the privacy scrubber.
- [`docs/02-telemetry-pipeline.md`](docs/02-telemetry-pipeline.md) — collector, batching, transport, offline queue, options.
- [`docs/03-platform-providers.md`](docs/03-platform-providers.md) — the `IPlatformProvider` surface and the Windows implementations.
- [`docs/04-security.md`](docs/04-security.md) — process/network/file/registry/AV watchers and the telemetry bridges.
