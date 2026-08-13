# Consent & Privacy (`YFex.System.Consent`, `PrivacyScrubber`)

## Why it exists

Collecting anything about a user's machine is a privacy liability unless two things are true: the user agreed to *that specific level* of collection, and whatever leaves the device can't be traced back to them. This layer enforces both. It's not a formality bolted on top of telemetry — it's the gate the telemetry pipeline checks before every single field.

## The consent ladder

`ConsentLevel` is an ordered enum. Higher levels include everything below:

```
None  <  Functional  <  Analytics  <  Full
```

| Level | Intent | Example fields |
|---|---|---|
| `None` | User declined, or hasn't been asked. Nothing collected. | — |
| `Functional` | Data needed to make the app work / diagnose crashes. | OS, architecture, runtime version, processor count, app version |
| `Analytics` | Aggregate usage understanding. | locale, network adapters, monitors, idle state, click/keystroke counts, memory, machine & user name, elevation |
| `Full` | Rich profiling. | installed software, active window, browser profiles |

## `ConsentState` — the recorded answer

The *level* is the coarse gate; the actual stored decision is a **union** carrying context:

```csharp
public union ConsentState(NotAsked, Declined, FunctionalOnly, AnalyticsConsent, FullConsent);

public record NotAsked;
public record Declined(DateTimeOffset DeclinedAt);
public record FunctionalOnly(DateTimeOffset ConsentedAt, string PolicyVersion);
public record AnalyticsConsent(DateTimeOffset ConsentedAt, string PolicyVersion);
public record FullConsent(DateTimeOffset ConsentedAt, string PolicyVersion);
```

Each grant records **when** it happened and **which policy version** it was given against — so if your privacy policy changes, you can detect stale consent and re-ask. `ITelemetryConsent.GetLevel(state)` collapses a state back down to its `ConsentLevel`.

## Storing & changing consent

`IConsentStore` persists the decision (default `FileConsentStore` writes to local app data). `ITelemetryConsent` is the runtime gate:

```csharp
public interface ITelemetryConsent
{
    ConsentState CurrentState { get; }
    event Action<ConsentState>? ConsentChanged;
    event Action<ConsentLevel, ConsentLevel>? ConsentDowngraded;   // (old, new)
    void UpdateConsent(ConsentState newState);
    bool IsAllowed(ConsentLevel required);
}
```

Two behaviors worth knowing:

- **`IsAllowed` is a strict ladder check.** `FullConsent` allows everything; `AnalyticsConsent` allows Analytics/Functional/None; `FunctionalOnly` allows Functional/None. This is exactly what the collector calls before touching each subsystem.
- **Downgrades fire `ConsentDowngraded` and force a flush.** When the user drops from, say, `Full` to `Functional`, `TelemetryCollector` immediately flushes pending events so nothing gathered under the old, broader consent sits in the buffer under the new, narrower one.

```csharp
// Typical wiring from a consent dialog:
consent.UpdateConsent(new AnalyticsConsent(DateTimeOffset.UtcNow, currentPolicyVersion));

// React to a user revoking consent:
consent.ConsentDowngraded += (old, now) => _log.Info($"Consent lowered {old} → {now}");
```

## The privacy scrubber

Consent decides *whether* a field is collected. `PrivacyScrubber` decides *how it looks when it leaves the device*. It runs on every batch just before transport (inside `TelemetryBatch.ShipOrQueueAsync`), so nothing un-scrubbed is ever shipped or even written to the offline queue.

It walks each event's properties by key name and transforms sensitive ones:

| Property key contains… | Transformation |
|---|---|
| `url` / `uri` | Reduced to host, then salted **SHA-256** hash (`HashUrl`). Invalid URLs → `"invalid_url"`. |
| `title` | Salted SHA-256 hash. |
| `mac` | Salted SHA-256 hash. |
| `ip` (but not `zip`/`script`) | Masked — IPv4 keeps 3 octets (`10.0.1.xxx`), IPv6 drops the last group. |
| anything else | Passed through unchanged. |

The salt comes from `TelemetryOptions.TransportSaltKey`. Hashing is one-way and salted, so a shipped value can be grouped/counted server-side but not reversed to the original URL, window title, or MAC address.

> Note: several providers pre-hash at the source too (e.g. network adapters expose `HashedMac`, not the raw MAC), so sensitive values are protected in depth.

## Rules of thumb

- Always call `UpdateConsent` with the real policy version string — it's your lever for re-consent later.
- Don't hand-roll field redaction in your `Track` calls; name properties conventionally (`url`, `windowTitle`, `macAddress`) and let the scrubber handle them.
- Treat `ConsentDowngraded` as a signal to also purge any app-side caches derived from higher-tier data.
