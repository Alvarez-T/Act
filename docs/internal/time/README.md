# YFex.Time — trusted time from pluggable sources — Plan

> **Status:** APPROVED (2026-10-04: ⚑T1–T7; 2026-10-05: ⚑T8–T11 and the site-time instructions §8 for YFex.Offline).
> **T0 + T1 + T2 (central anchor) DONE 2026-10-05** (implementation log §10). Next: Offline adoption (offline Phase 1), T3.
> **Origin:** authorization-consolidation Phase 3 (Q7). The persisted permission snapshot, approval tokens and offline envelopes
> must not trust the device clock. The offline design (`../offline/05` §4, ⚑O10) planned a `TrustedTimeProvider` inside
> YFex.Offline. The user asked for **one reusable service for real time** that every project can use, with different providers.
> **Rule from the user (2026-10-04):** questions about signatures, cryptography or hashing wait until YFex.Cryptography is
> done (another session). Authenticated (signed) time sources are therefore listed here but scheduled after crypto Phase 2.

## 1. Who needs "what time is it, really?"

| Consumer | Question | What goes wrong with the device clock |
|---|---|---|
| Authorization (Phase 3b) | is the persisted permission snapshot still valid? (default 72 h) | clock set back → an expired or revoked snapshot works again |
| Authorization approvals (Phase 4) | was the approval token valid **at the operation time**? | backdated operations; F7 of the offline design |
| Offline envelopes / outbox | in which order did things happen, and when? | F5: wall-clock ordering; reordering by clock change |
| YFex.Cryptography | key rotation, certificate and token validity | keys rotate early or late; expired certificates accepted |
| YFex.Logging / YFex.Audit | timestamps comparable across machines (logging 05 §5 warns at skew > 2 s) | POS with a dead CMOS battery logs in 2001 |
| YFex.Fiscal (NF-e / NFC-e) | emission time (`dhEmi`) close to the tax authority's clock | SEFAZ rejects an emission time ahead of its receipt time (beyond its tolerance); a slow clock produces late-looking documents |
| Temporary grants, Jobs, TTLs | when does it start or expire? | grants that never expire on a terminal whose clock is behind |

Every one of them already takes a BCL `TimeProvider` (the ecosystem convention; `FakeTimeProvider` in tests). **The plan keeps
that:** the library registers a `TimeProvider`, so consumers that only need "a better now" change nothing. Consumers that make
security decisions ask for the extra information (source, uncertainty, rollback) through `time.GetTrustedNow()` (⚑T8).

### 1.1 What a clock rollback is

The **device clock** (Windows/Linux date and time) can be changed by anyone with access, and by hardware faults (dead CMOS battery,
BIOS reset after repair, VM snapshot restore, a bad sync). A **rollback** is that clock moving **backward** compared with real time.
Every validity check is `now <= ValidUntil`, so a clock that can go back revives expired snapshots, approvals, promotions, keys and
certificates, and lets offline sales look older than they are. **Rule:** our clock never goes backward. The device clock may do
anything; we notice it (`RollbackDetected`), and keep counting from where we were.

## 2. Shape

```
            sources (pluggable, each a small class)                       library (YFex.Time)                      consumers
 ┌───────────────────────────────────────────────────┐   samples   ┌─────────────────────────────────┐  TimeProvider  ┌──────────────┐
 │ CentralAnchor (Messaging.Rpc, our server)          │───────────►│ offset estimation (NTP-style)    │──────────────►│ everything    │
 │ SiteAnchor (YFex.Offline: the site broker, §8)     │            │ source selection + outlier check │               │ (unchanged)   │
 │ Ntp (SNTP v4, UDP 123: a.ntp.br, time.google.com…) │            │ anchor + monotonic clock         │ TrustedInstant │              │
 │ HttpsDate (Date header over TLS)                   │            │ high-water mark (never backward) │──────────────►│ snapshot,     │
 │ Sefaz (YFex.Fiscal: NFeStatusServico dhRecbto)     │            │ rollback detection + events      │               │ approvals,    │
 │ Gnss (YFex.Devices: GPS NMEA)                      │            │ uncertainty that grows with drift│               │ fiscal, audit │
 │ Peer (YFex.Offline: other installations, §8.3)     │            └─────────────────────────────────┘               └──────────────┘
 │ later, after crypto: Roughtime, NTS, RFC 3161 TSA, │
 │ signed central anchor                              │
 └───────────────────────────────────────────────────┘
```

### 2.1 Core types (YFex core, namespace `YFex.Time`, **types only** like ⚑K2)

```csharp
public enum TimeSourceKind : byte { System = 0, CentralAnchor = 1, SiteAnchor = 2, NetworkTime = 3, HttpsDate = 4, Authority = 5,
                                    Satellite = 6, Peer = 7 }

[Flags] public enum TimeFlags : byte
{
    None = 0,
    Unanchored = 1,            // no source has answered since this process started
    RollbackDetected = 2,      // the device clock went back behind trusted time (or behind the stored high-water at start)
    SourcesDisagree = 4,       // a source was dropped by the vote or refused (e.g. NTP trying to pull time back)
    Unauthenticated = 8,       // no authenticated source backs the value (NTP, HTTPS Date, GNSS, peers, or no anchor at all)
    ClockAdjusted = 16,        // the device clock jumped forward, or the monotonic clock paused (sleep on Linux/macOS)
    AuthenticatedFloor = 32,   // the value is bounded below by a high-water mark written while an authenticated anchor existed (⚑T11)
}

public readonly record struct TrustedInstant(DateTimeOffset Value, TimeSpan Uncertainty, TimeSourceKind Source,
    TimeSpan SinceLastAnchor, TimeFlags Flags);

public interface ITrustedClock { TrustedInstant GetTrustedNow(); }          // ⚑T8: implemented by TrustedTimeProvider

public static class TrustedClockExtensions                                  // ⚑T8: the one tiny piece of logic in core
{
    // TrustedTimeProvider → its answer; any other TimeProvider (System, FakeTimeProvider) → its time, flagged Unanchored|Unauthenticated
    public static TrustedInstant GetTrustedNow(this TimeProvider time);
}

// What a time server answers (central health reply, site broker, a peer): wire-neutral, carried by Rpc and Offline contracts
public readonly record struct TimeReply(DateTimeOffset Received, DateTimeOffset Sent, TimeSpan Uncertainty, TimeSourceKind Source,
    TimeFlags Flags, byte Stratum);

// One exchange measured by the client: device monotonic timestamps around the reply
public readonly record struct TimeSample(long LocalSent, long LocalReceived, TimeReply Reply, TimeSpan Resolution);

public interface ITimeSource
{
    string Name { get; }
    TimeSourceKind Kind { get; }
    bool IsAuthenticated { get; }               // the answer cannot be forged by the network (TLS/mTLS to our server, signed reply)
    ValueTask<TimeSample?> SampleAsync(TimeProvider device, CancellationToken cancellationToken);   // device = timestamps for t1/t4
}
```

This replaces `TrustedInstant` / `TimeSource` that offline 05 §4 placed in the core `YFex.Offline` namespace (⚑T2). `Stratum` follows
NTP: 1 = a reference (central, a public NTP server's stratum), each hop adds 1, 16 = unsynchronized. A client never anchors to a reply
whose stratum is ≥ its own when it already has an anchor (no loops between broker and terminals).

### 2.2 Library (sibling repo `C:\Dev\YFex\YFex.Time`, AOT)

```csharp
// ✍ composition root (POS terminal)
services.AddYFexTime(time => time
    .UseCentralAnchor()                          // T2: extension from Messaging.Rpc
    .UseSiteAnchor()                             // §8: extension from YFex.Offline
    .UseNetworkTime("a.ntp.br", "b.ntp.br", "c.ntp.br")
    .UseHttpsDate("https://www.google.com", "https://www.cloudflare.com")
    .PersistHighWater()                          // YFex.Time.Persistence: over the registered IKeyValueStore
    .WaitAtStartup(TimeSpan.FromSeconds(2))      // ⚑T9
    .Refresh(TimeSpan.FromMinutes(15)));

// ✍ a security decision (Authorization 3b, sketch)
TrustedInstant now = time.GetTrustedNow();      // ⚑T8: any TimeProvider
if (!now.IsUsableForSecurity(maximumUncertainty: TimeSpan.FromMinutes(10))) return SnapshotUse.Refused;   // ⚑T11 policy
```

| Project | Contents |
|---|---|
| `YFex.Time` | `TrustedTimeProvider` (sync read path), `TimeSynchronizer` (async sampling + vote), `TimeSynchronizationService` (refresh loop, network trigger, device-clock check, high-water saves), `IHighWaterStore`, `NetworkTimeSource` (SNTP v4), `HttpsDateTimeSource`, `TimeChange` events, `YFex.Time` meter, `AddYFexTime` |
| `YFex.Time.Persistence` | `KeyValueHighWaterStore` over `IKeyValueStore` (kept out of the main project so YFex.Time does not pull MemoryPack/FusionCache) |
| `YFex.Time.Testing` | `FakeDeviceClock` (FakeTimeProvider + `ChangeSystemClock`), `FakeTimeSource` |
| tests, AotSmoke, Benchmarks | §9 |

Dependencies: YFex core, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions`
(same family as Cryptography). Tests: `Microsoft.Extensions.TimeProvider.Testing`.

### 2.3 What .NET gives us (checked 2026-10-05 against the .NET 11 preview ref packs)

- `TimeProvider` has had **no new members from .NET 8 to .NET 11**. In .NET 11 the change is adoption (ASP.NET Core Identity uses it for
  all its time logic). We subclass it; nothing newer replaces it.
- **.NET 11 breaking change:** on Windows `Environment.TickCount64` now uses `QueryUnbiasedInterruptTime` (excludes sleep).
  `Stopwatch`/`TimeProvider.GetTimestamp()` (QPC) **include** sleep on Windows and **exclude** it on Linux/macOS (CLOCK_MONOTONIC).
  So "elapsed since the anchor" may pause during sleep → the device-clock check (§2.4) handles it.
- Reused as-is: `TimeProvider` (base), `GetTimestamp`/`GetElapsedTime` (monotonic clock), `PeriodicTimer(TimeSpan, TimeProvider)`,
  `ITimer`, `CancellationTokenSource(TimeSpan, TimeProvider)`, `BackgroundService`, `HttpResponseHeaders.Date`,
  `NetworkChange.NetworkAvailabilityChanged`, `System.Diagnostics.Metrics`, `FakeTimeProvider`.
- Not in .NET: an NTP client, trusted time, a cross-platform clock-change event (`SystemEvents.TimeChanged` is Windows-only and a
  package), a high-water mark. Ours.
- `FakeTimeProvider.AdjustTime` **also moves `GetTimestamp`** (measured: elapsed −1 day). It cannot simulate "the operator changed the
  Windows clock", so `.Testing` has `FakeDeviceClock` that offsets only the wall clock.
- Measured on net11 (this PC): system `GetUtcNow` 47 ns, `GetTimestamp` 25 ns, `TickCount64` 3 ns, anchor + elapsed 45 ns.
- Registration: Jobs, Cryptography and Payment `TryAddSingleton(TimeProvider.System)`, so `AddYFexTime` **replaces** the registration
  (order does not matter).

### 2.4 How "now" is computed (the cached anchor)

Fetch real time **once** (async, background), keep it as an **anchor** `(Utc, Timestamp)`, and compute every "now" synchronously:

```
sample      RTT = (t4 − t1) − (t3 − t2) ; server time at t4 = t3 + RTT/2 ; uncertainty = RTT/2 + resolution + reply.Uncertainty
selection   authenticated samples first; with ≥ 3 answers intersect [time ± uncertainty] (Marzullo) and drop the ones outside the
            majority → SourcesDisagree; differences < 1 s are ignored (leap smearing, ⚑T3)
anchor      (Utc, Timestamp = t4, Uncertainty, Source, Stratum, Flags)
now         = anchor.Utc + monotonic elapsed since anchor.Timestamp (+ slew of a pending small correction)   ← ~45 ns, no I/O, no lock
uncertainty = anchor.Uncertainty + elapsed × 50 ppm (≈ 4.3 s/day)
```

Rules when a new anchor arrives (`Publish`):

| New anchor vs current value | Authenticated source | Unauthenticated source |
|---|---|---|
| ahead (any amount) | continue from the current value; the difference is slewed in (max 500 ppm) when < 2 s, stepped otherwise | same |
| behind by < 2 s (normal drift) | slewed out (max 500 ppm): the value never goes backward | same |
| behind by ≥ 2 s | **step back** + `TimeChange.Stepped` (e.g. fixing an accidental "2030"); high-water corrected | **refused** + `SourcesDisagree` (NTP/peers cannot pull time back, ⚑T4) |

Device-clock check (an `ITimer` every 10 s; `GetUtcNow` stays pure). The device clock's own offset ("skew") is measured once, at
the first anchor (or at start-up from the high-water mark): a POS whose CMOS clock is 4 min slow is **skew, not a rollback**. Only a
**movement** of the device clock away from that baseline counts:

| Device clock movement since the baseline | Meaning | Action |
|---|---|---|
| within ±2 min | normal (incl. Windows Time nudges) | — |
| back by > 2 min | someone set it back (or Windows Time stepped a fast clock back — indistinguishable, reported the same) | `RollbackDetected` + event + resync now; the flag stays until an anchor sees the device back within the tolerance |
| ahead by > 2 min | set forward, or we slept with a paused monotonic clock | `ClockAdjusted` + resync now; when `Unanchored`, adopt the forward value (forward only makes things expire sooner) and drop `AuthenticatedFloor` |

After a rollback seen at start-up (device behind the high-water), the first anchor expects the device to match real time (baseline 0),
so a device still years behind keeps the flag.

Start-up anchor (before any source answers), with the stored high-water mark `H` (saved every 60 s and at shutdown, with "was an
authenticated anchor active" as a bit):

| Situation | Starting value | Flags |
|---|---|---|
| no `H` (first run) | device clock | `Unanchored, Unauthenticated` |
| device ≥ `H` − 2 min | max(device, `H`) | `Unanchored, Unauthenticated` (+ `AuthenticatedFloor` when `H` was written under an authenticated anchor) |
| device < `H` − 2 min | `H` (keeps counting from there) | `Unanchored, Unauthenticated, RollbackDetected` |

The high-water mark limits the **device** clock only. An authenticated anchor may correct it (a stored "2030" would otherwise freeze
the shop forever). `GetUtcNow()` respects it too (⚑T10), so every `TimeProvider` consumer is protected.

Timers, `Task.Delay(x, time)`, `GetTimestamp`, `LocalTimeZone` pass through to the device clock: durations are real seconds and must
not follow corrections.

### 2.5 Using it — six patterns

| # | Need | Call | Who |
|---|---|---|---|
| A | a better "now" | `time.GetUtcNow()` (unchanged code) | Jobs, key rotation, certificates, token issuing, logging |
| B | a security decision | `time.GetTrustedNow()` + `IsUsableForSecurity(...)` | snapshot validity, approval as-of, offline budgets |
| C | must have real time first | `await clock.WaitForAnchorAsync(timeout, ct)` | first NFC-e of the shift, offline sign-in |
| D | keep evidence | store the whole `TrustedInstant` | offline envelopes, audit records |
| E | react | `trusted.Changed += …` (on `TrustedTimeProvider`) | Audit (rollback finding), Logging (skew), UI banner |
| F | durations | `Task.Delay(x, time)`, `PeriodicTimer(x, time)` (unchanged) | everything |

⚑T11 — `IsUsableForSecurity(maximumUncertainty)`: true when an authenticated anchor backs the value within the maximum
uncertainty (a rollback of the device clock does not matter then — the value does not come from the device clock; the flag stays as
evidence), **or** the value carries `AuthenticatedFloor` (the stored high-water came from authenticated time) without
`RollbackDetected`. Consumers that want deterrence (refuse while the device clock is set back) check the flag themselves. The residual (powered-off time is not counted when the clock was set back while off) is accepted; offline budgets
are the hard bound. A standalone PC that boots without internet can therefore use its offline snapshot.

## 3. Providers — where real time comes from

| Provider | Transport / where | Authenticated? | Accuracy | Works when | Implemented in | Phase |
|---|---|---|---|---|---|---|
| Device clock | the wrapped `TimeProvider` (System) | no | whatever the OS has | always | library (start-up anchor and fallback; **not** a voting source — the planned `SystemTimeSource` was dropped: it would pull the vote toward the device clock) | T1 |
| **Central anchor** | our server's clock over the existing Fusion RPC connection (`TimeReply` in a health call) | yes — TLS (mTLS after offline Phase 2) to our own server | RTT/2 (≈ 10–50 ms) | online | Messaging.Rpc (`UseCentralAnchor()`) | T2 |
| **Site anchor** | the shop's site broker (§8) | yes — mTLS on the LAN | ≈ 1 ms + the broker's own uncertainty | LAN up, internet down | YFex.Offline (`UseSiteAnchor()`) | offline Phase 4 |
| Peers | other installations of the same site (§8.3) | channel yes, but peers are equally untrusted devices | ms | LAN | YFex.Offline | offline Phase 4 (disagreement only) |
| **NTP (SNTP v4)** | UDP 123 to public servers: NIC.br `a/b/c.ntp.br` (Brazil's official time, ONRJ-traceable), `time.google.com`, `time.windows.com`, `time.cloudflare.com`; AWS `169.254.169.123` on servers | no (spoofable on the LAN) | ms | internet, UDP 123 not blocked | library | T1 |
| HTTPS Date header | `Date:` of HEAD requests to a few large HTTPS sites (port 443 passes most firewalls/proxies) | no for voting — TLS proves the host, but certificate validation itself assumes a roughly right clock | ±1 s | internet | library | T1 |
| SEFAZ | NF-e/NFC-e status service (`NfeStatusServico`) answers with `dhRecbto` | yes — mTLS with the company's A1 certificate | ±1 s + RTT | internet, certificate installed | YFex.Fiscal | T3 |
| GNSS receiver | serial/USB NMEA (`$GPRMC`/`$GNZDA`) | no | < 1 s | no internet at all | YFex.Devices | T3 (optional) |
| Windows time service | W32Time status — a **quality signal**, not a source | — | — | Windows | `.Windows` package | T3 (optional) |
| Roughtime / NTS / RFC 3161 TSA / signed central anchor | signed or AEAD replies | yes | ±1 s / ms | internet | after YFex.Cryptography Phase 2; RFC 3161 needs `System.Security.Cryptography.Pkcs` (ask, ⚑K14) | T4 |

## 4. Phases

| Phase | Content | Depends on | Acceptance |
|---|---|---|---|
| **T0** | core types (§2.1) + tests in YFex.Tests | ⚑T1, T2, T6, T8 | value semantics; `GetTrustedNow()` on System/Fake providers flags `Unanchored` |
| **T1** | repo `YFex.Time` (§2.2): provider, synchronizer, selection, slew, drift, device-clock check, high-water (`IHighWaterStore` + `.Persistence` over `IKeyValueStore`), events, SNTP, HTTPS Date, server-side `CreateReply` (used by central and the broker), `AddYFexTime`, `.Testing`, AOT smoke, benchmarks (`GetUtcNow` ≤ 50 ns, no allocation) | T0 | §9 |
| **T2** | `UseCentralAnchor()` in Messaging.Rpc (`TimeReply` in a health call); YFex.Offline adopts the library; logging uses the offset for skew reports | T1 | anchor over a real RPC round trip in `Messaging.Rpc.Tests`; offline 05 §4 example passes |
| **T3** | domain providers: `SefazTimeSource` (Fiscal), `GnssTimeSource` (Devices), W32Time quality (Windows) | T1 | each with a recorded-response test |
| **T4** | authenticated sources: signed central anchor, Roughtime, NTS, RFC 3161 TSA; sealed/monotonic high-water; signed beacons | **YFex.Cryptography Phase 2** (user rule: crypto waits) | signed-reply verification; tampered reply rejected |
| (offline) | site time server, `UseSiteAnchor()`, site time table, peer comparison (§8) | T1, offline Phases 1–4 | offline 11 |

## 5. Decisions (⚑T) — CONFIRMED

| ⚑ | Decision | Date |
|---|---|---|
| **T1** | sibling repo **`C:\Dev\YFex\YFex.Time`** (AOT) + core types | 2026-10-04 |
| **T2** | core namespace **`YFex.Time`** (types + contracts only); offline 05 §4 types move there | 2026-10-04 |
| **T3** | NIC.br `a/b/c.ntp.br` first, `time.google.com` fourth; disagreement < 1 s ignored (leap smearing) | 2026-10-04 |
| **T4** | unauthenticated sources (NTP, HTTPS Date, GNSS, peers) fill in when no authenticated anchor exists and always count for disagreement; they never pull time back and never make a value "authenticated" | 2026-10-04 |
| **T5** | high-water through `IKeyValueStore` (sealed per key when `AddYFexStorageEncryption` is on); protection against restoring an older stored copy is crypto → T4 | 2026-10-04 |
| **T6** | name **`YFex.Time`** | 2026-10-04 |
| **T7** | no new packages for T0–T3 beyond Microsoft.Extensions abstractions; RFC 3161 → ask | 2026-10-04 |
| **T8** | core `ITrustedClock` + core extension `TimeProvider.GetTrustedNow()`; consumers keep injecting `TimeProvider` | 2026-10-05 |
| **T9** | start-up waits for the first anchor up to a configurable budget (default 2 s), then continues `Unanchored` | 2026-10-05 |
| **T10** | `GetUtcNow()` respects the high-water mark (every `TimeProvider` consumer is protected) | 2026-10-05 |
| **T11** | security checks accept an authenticated anchor, or `Unanchored` + `AuthenticatedFloor` without `RollbackDetected` (§2.5) | 2026-10-05 |
| **T12** | the site broker is the shop's time server (authenticated, stratum + 1); installations compare clocks across the site (§8) | 2026-10-05 |

## 6. Pitfalls to test

- Use the device `GetTimestamp()` for t1/t4 and for elapsed time; never subtract wall-clock values (they jump).
- Sleep/hibernate: Windows QPC counts sleep, Linux/macOS do not → the device-clock check detects it (`ClockAdjusted`) and resyncs.
- `Environment.TickCount64` changed meaning on Windows in .NET 11 (no sleep) — never use it for wall-time elapsed.
- HTTPS Date when the device clock is years off: TLS validation fails before the Date is read. The source reports "no sample".
- Leap seconds and smearing (⚑T3). DST and time-zone changes never matter: everything is UTC.
- Timeouts: one source must never block the others (sample all in parallel with a 2 s budget).
- Loops: broker ↔ terminal must not anchor to each other (stratum rule, §2.1).

## 7. Scenarios (what the user sees)

| # | Scenario | Device clock | Our clock | Flags | Consumers |
|---|---|---|---|---|---|
| 1 | online all day | drifts 30 ppm | central anchor every 15 min, drift slewed | — | normal |
| 2 | Windows Time nudges +3 s | +3 s | unchanged (< 2 min) | — | — |
| 3 | time-zone change | local display changes | UTC unchanged | — | receipts show the new zone |
| 4 | start with internet | ok | anchored within the 2 s start budget | — | normal |
| 5 | start without internet | ok | max(device, high-water) | `Unanchored, Unauthenticated, AuthenticatedFloor` | sales ok; Fiscal warns; snapshot allowed (⚑T11) |
| 6 | start without internet, CMOS dead (2001) | 2001 | high-water | + `RollbackDetected` | Fiscal blocks emission; audit finding; snapshot refused |
| 7 | first install, offline, wrong clock | wrong | device | `Unanchored, Unauthenticated` | undetectable — enrollment requires being online |
| 8 | set back while online | Sun 09:00 | Mon 13:30 (continues) | `RollbackDetected` within 10 s | time stays right (anchored); sales flagged; audit finding; a deterrence policy may refuse |
| 9 | set back while offline | back | continues | `RollbackDetected` | floor case refused (⚑T11) until a source answers |
| 10 | set back while powered off | back | high-water (off-time not counted) | `RollbackDetected` | residual; budgets are the hard bound |
| 11 | set forward / accidental 2030 | ahead | anchored: ignored; unanchored: adopted | `ClockAdjusted` | central later steps it back (`Stepped`) |
| 12 | Windows sleep | — | QPC counted sleep: right | — | — |
| 13 | Linux/macOS suspend | right | lags until the check (≤ 10 s) | `ClockAdjusted` then resync | Fiscal waits for an anchor |
| 14 | internet down, LAN up | — | site anchor (§8) | `Source = SiteAnchor` | normal |
| 15 | everything down 3 days | — | anchor + monotonic | uncertainty ±13 s | snapshot ok until its own validity |
| 16 | fake NTP on the LAN | — | NTP cannot pull back | `SourcesDisagree` | security still needs authenticated time |
| 17 | one public server wrong | — | vote drops it | `SourcesDisagree` | log only |
| 18 | leap second smear | — | difference < 1 s ignored | — | — |

## 8. Site time — instructions for YFex.Offline (⚑T12)

The offline design has three kinds of installation: **central**, the shop's **site broker** (the manager's PC) and **terminals**
(offline 02). Time follows the same tree. The broker **serves** time to the shop and **compares** the shop's clocks; it never changes
anyone's Windows clock (that is a privileged OS setting, and our clock does not depend on the OS clock anyway).

```
                           NIC.br / public NTP (unauthenticated, stratum 1–2)
                                        │ votes / disagreement
                 ┌──────────────────────▼──────────────────────┐
                 │ CENTRAL  stratum 1–2: OS-synced, YFex.Time    │◄──── skew reports, findings (envelopes, health)
                 └──────────────────────┬──────────────────────┘
                     TimeReply over Fusion RPC (TLS/mTLS)  = CentralAnchor
                 ┌──────────────────────▼──────────────────────┐
                 │ SITE BROKER  stratum central + 1              │  serves TimeReply on the LAN (mTLS) = SiteAnchor
                 │ keeps the SITE TIME TABLE (every installation)│  compares every terminal's clock (§8.2)
                 └───────┬───────────────┬───────────────┬─────┘
                  mTLS   │               │               │   beacons (every 5 s, trusted time inside) = Peer
                 ┌───────▼─────┐  ┌──────▼──────┐  ┌──────▼──────┐
                 │ POS-01       │  │ POS-02       │  │ POS-03       │  stratum broker + 1; compare with peers when the broker is down
                 └─────────────┘  └─────────────┘  └─────────────┘
```

### 8.1 The broker as the shop's time server

- **Broker side.** The broker runs YFex.Time like any installation (`UseCentralAnchor()` + NTP, `PersistHighWater()`). It answers
  time requests with `TrustedTimeProvider.CreateReply(receivedTimestamp)` → `TimeReply` (received/sent from its trusted clock, its own
  uncertainty, source, flags, stratum + 1). The reply rides on the existing probe `Sites.Queries.Health` (offline 04 §4: "one round
  trip gives time anchor, reachability and staleness") — no new port, no new protocol.
- **Terminal side.** `UseSiteAnchor()` (YFex.Offline) is an `ITimeSource` with `Kind = SiteAnchor`, `IsAuthenticated = true` (mTLS
  with the tenant CA, offline 03). It measures t1/t4 with the device clock around the health call. The library then:
  - adds the broker's own `Uncertainty` to the RTT/2 (a broker that last saw central 3 h ago passes on its ±0.5 s);
  - **inherits the broker's flags**: a broker backed only by NTP passes on `Unauthenticated`; a broker that is `Unanchored` (never
    reached central since it started; stratum 16) is **evidence only** — it cannot anchor a terminal, it only counts for disagreement;
  - prefers central when both answer and agree; uses the broker when the internet is down; never lets the broker anchor to a terminal
    (stratum rule).
- **Order of preference on a terminal:** central (authenticated) → broker (authenticated, inherits quality) → NTP / HTTPS Date
  (unauthenticated, fill-in) → device clock (start-up only).
- **Optional (not in this plan's phases, ask before building):** the broker's `.Windows` host could also answer plain SNTP on UDP 123
  so Windows Time on the terminals can point at it (cosmetic: the taskbar clock matches). It changes nothing for security (SNTP is
  unauthenticated) and opens a UDP port on the manager's PC.

```csharp
// ✍ terminal composition root (offline design, Phase 4)
services.AddYFexOffline(o => o.Terminal());
services.AddYFexTime(time => time
    .UseCentralAnchor()            // Messaging.Rpc
    .UseSiteAnchor()               // YFex.Offline: the broker over the health probe
    .UseNetworkTime("a.ntp.br", "b.ntp.br", "c.ntp.br")
    .PersistHighWater());

// ✍ broker composition root
services.AddYFexOfflineBroker(...);
services.AddYFexTime(time => time.UseCentralAnchor().UseNetworkTime("a.ntp.br", "b.ntp.br").PersistHighWater());
// Sites.Queries.Health handler (sketch):  reply.Time = trusted.CreateReply(receivedTimestamp);
```

### 8.2 The site time table (the broker compares the shop's clocks)

Every terminal's health probe also **reports** its own clock, so the broker sees all machines of the shop:

```csharp
// core YFex.Offline (sketch, offline Phase 4) — sent by each terminal in its health probe
public readonly record struct InstallationTimeReport(TrustedInstant Trusted, DateTimeOffset DeviceClock, TimeSpan LastRoundTrip);

// broker (sketch) — one row per installation, refreshed by every probe
public sealed record SiteTimeEntry(InstallationId Installation, TimeSpan TrustedOffset, TimeSpan DeviceOffset, TimeFlags Flags,
    DateTimeOffset LastSeen);
```

| Check on the broker | Rule (configurable) | Result |
|---|---|---|
| a terminal's **device** clock is off | `|DeviceOffset|` > 2 min | site finding `time.device-skew` (manager screen: "PDV-02: relógio do Windows 1 dia atrasado"); relayed to central |
| a terminal reports `RollbackDetected` | flag set | site finding `time.rollback` (high severity), relayed to central audit |
| a terminal's **trusted** clock disagrees with the broker | `|TrustedOffset|` > its uncertainty + the broker's | terminal is told to resync now; a repeat is a finding (`time.disagreement`) |
| a terminal is `Unanchored` for long | > 30 min while the broker is anchored | the terminal is told to resync (its probe is failing or it ignores the broker) |

Findings use the offline review queue and the audit relay (offline 08, 07); the time library only produces the numbers.

### 8.3 Comparing with peers when the broker is down

- The broker's LAN beacon already carries trusted time (offline 04 §2). Terminals will also emit a beacon in Standalone mode (offline
  Phase 4) with `TimeReply` fields, so terminals can compare with each other when no broker answers.
- A peer is an `ITimeSource` with `Kind = Peer`, **unauthenticated for time decisions** even over mTLS: a peer is just another shop
  device whose clock the same person can change. Peers therefore:
  - count for **disagreement only** (⚑T4): `SourcesDisagree` when this terminal differs from the peers' majority by more than the
    combined uncertainty;
  - may raise `RollbackDetected` on this terminal when ≥ 2 peers that are themselves `AuthenticatedFloor` or anchored agree that this
    device's clock is behind by > 2 min (detection only, never moves the clock);
  - never move this terminal's time and never become an anchor.
- Beacons are one-way: the uncertainty is the beacon age + LAN delay bound (default 50 ms). Signed beacons come with crypto (T4).

### 8.4 Central

- Central reads `InstallationTimeReport` from health probes and the `TrustedInstant` + device clock in every envelope (offline 05 §4),
  keeps a per-installation skew history (the `yfex.time.offset` metric) and raises the same findings for direct terminals (no broker).
- Reconciliation recomputes corrected operation times from `DecidedAt` (a `TrustedInstant`) and flags envelopes with
  `RollbackDetected`/`Unauthenticated` (offline 08 §5).

## 9. T1 acceptance

Golden SNTP packets (request bytes; reply parsing incl. stratum 0 "kiss-o'-death" and leap "unsynchronized"); vote with a liar →
`SourcesDisagree`; device clock set back → `RollbackDetected` and the value never decreases; small backward correction is slewed
(monotonic values); authenticated large correction steps back with `Stepped`; unauthenticated cannot pull back; restart keeps the
high-water (`RollbackDetected` at start when the device is behind it, `AuthenticatedFloor` bit round-trips); no anchor → `Unanchored`;
paused monotonic clock (Linux-style sleep) → `ClockAdjusted` + resync; a slow source never blocks the others; `WaitForAnchorAsync`
returns on the first anchor or at the timeout; `AddYFexTime` replaces an earlier `TryAddSingleton(TimeProvider.System)`;
`CreateReply` → `TimeSample` round trip between two providers gives the broker's time ± RTT/2 with inherited flags and stratum + 1;
AOT smoke 0 warnings; `GetUtcNow` ≤ 50 ns, `GetTrustedNow` 0 allocations.

## 10. Implementation log

### T0 + T1 — 2026-10-05

**Core** (`YFex/src/YFex/Time/`, namespace `YFex.Time`): `TimeSourceKind` (+ `Peer`), `TimeFlags` (+ `ClockAdjusted`,
`AuthenticatedFloor`), `TrustedInstant` (`IsAnchored`, `IsAuthenticated`, `IsUsableForSecurity`, `FromUntrustedClock`, 26-byte
canonical form, `ICanonicalValue`, `ILogValue`, text form), `TimeReply` (27-byte form, `Unsynchronized = 16`), `TimeSample`,
`ITimeSource` (`SampleAsync(TimeProvider device, …)`), `ITrustedClock` (`GetTrustedNow`, `WaitForAnchorAsync`) +
`TrustedClockExtensions` on any `TimeProvider` (⚑T8). Tests: `YFex.Tests/Time/TrustedInstantTests.cs`.

**Repo `C:\Dev\YFex\YFex.Time`** (`time.slnx`): `YFex.Time`, `YFex.Time.Persistence`, `YFex.Time.Testing`, `.Tests`, `.AotSmoke`,
`.Benchmarks` (README in the repo).

**Deviations from the plan**

- `ITimeSource.SampleAsync` takes the device `TimeProvider` (t1/t4 must come from the same monotonic clock the provider uses; tests
  drive it with `FakeDeviceClock`). `TimeSample` = (t1, t4, `TimeReply`, resolution), so central, the broker and peers share one reply type.
- No `SystemTimeSource`: the device clock is the start-up anchor and fallback, not a voter.
- High-water storage behind `IHighWaterStore`; the `IKeyValueStore` adapter is `YFex.Time.Persistence`, so YFex.Time does not pull
  YFex.Persistence (MemoryPack, FusionCache). Stored form: version 1 + ticks + "authenticated" byte.
- Rollback is measured against the device clock's offset at the first anchor (§2.4): a slow CMOS clock is skew, not a rollback.
  Without this, an anchored POS with a 4-min-slow Windows clock would have been flagged forever.
- `IsUsableForSecurity` follows ⚑T11 literally: an authenticated anchor is usable even when the device clock was set back (the flag
  stays as evidence); the rollback check applies to the floor case.
- Reference mode (`TrustDeviceClock()`) for central servers whose OS clock is managed: replies as an anchored server of the configured
  stratum; sources only report disagreement. Without it, central (no sources) would answer "unsynchronized" and no terminal could anchor.
- Evidence-only answers (peers, unanchored servers) are compared with the clock's own value when nothing else anchors in a round.
- `FakeTimeProvider` derives `GetTimestamp()` from the virtual `GetUtcNow()`, so `FakeDeviceClock` overrides both (wall vs monotonic).
- Events are a native `union TimeChange` (not a hot path).

**Counts:** YFex.Tests 158 → 173. YFex.Time.Tests 56 (5 repeated runs green). AOT smoke: 0 IL warnings, exit 0. Benchmarks
(in-process, ShortRun): system `GetUtcNow` 21.4 ns, `TrustedTimeProvider.GetUtcNow` 22.5 ns, `GetTrustedNow` 28.5 ns,
`TimeProvider.GetTrustedNow()` extension 33.8 ns; all 0 B.

**Open items**

- T2: `UseCentralAnchor()` (Messaging.Rpc health call returning `TimeReply` via `CreateReply`). Coordinate with the sessions working
  in Messaging.Rpc (authorization, crypto EventChannel).
- Offline Phase 1 uses the core types in envelopes; Phase 4 builds `UseSiteAnchor()`, the site time table and peers (§8).
- Authorization 3b: `time.GetTrustedNow().IsUsableForSecurity(...)` for snapshot validity.
- Not registered anywhere yet: apps opt in with `AddYFexTime`. Jobs/Cryptography/Payment keep `TryAddSingleton(TimeProvider.System)`,
  which `AddYFexTime` replaces.
- `KeyRingProvider` (Cryptography) has a fast path only for `TimeProvider.System`; with the trusted clock it reads `GetUtcNow` per
  call (22 ns). No change needed.
- T4 (crypto): signed anchors, sealed/monotonic high-water against restoring an older stored copy.

### T2 — central anchor — 2026-10-05

**Where:** Messaging.Rpc, not YFex.Offline — the anchor rides the client↔central RPC connection that Messaging.Rpc owns, and
online-only apps (no offline library) need it too. YFex.Offline adds the site/peer layer on top (§8).

- `YFex.Messaging.Rpc/Time/CentralTime.cs`: `ICentralTime : IRpcService` (plain RPC, never a compute method),
  `CentralTimeService` (answers with the registered `TimeProvider` via `CreateTimeReply`), `CentralTimeWire` (version byte 1 + the
  27-byte `TimeReply` form). Checked-in proxy `Time/ActualLabProxies/ICentralTimeProxy.cs` (ActualLab.Generators 13.0.12), covered
  by `ProxyGoldenTests` (source file is now a test parameter).
- `CentralTimeSource` + `TimeBuilder.UseCentralAnchor(bool? authenticated = null)`: authenticated when the RPC endpoint is
  `wss`/`https`; no answer while `INetworkStatus` says disconnected (the RPC client would wait for a reconnect).
- Registration: `AddYFexMessagingRpcClient` adds the `ICentralTime` client; `UseYFexMessagingRpcServer` adds the server.
  Central must register `AddYFexTime(time => time.TrustDeviceClock())`; without it the reply is "unsynchronized" (stratum 16) and
  no client anchors to it (tested).
- YFex.Time: `TimeProvider.CreateTimeReply(receivedTimestamp)` (any provider; unsynchronized reply when not the trusted clock).
- Messaging.Rpc references `YFex.Time` (sibling repo, like YFex.Cryptography).

**Deviation:** the RPC result is `byte[]`, not `TimeReply`. MemoryPack copies unmanaged structs as raw memory (it never calls a
registered formatter for them), and that layout depends on `DateTimeOffset` internals — not a contract between a client and a server
on different runtimes. The explicit wire form is versioned and validated (`CentralTimeWire.Read`).

**Counts:** Messaging.Rpc.Tests 60 → 69 (real two-hub round trip: central reference reply; client trusted clock anchored over RPC with
stratum 3; unsynchronized central never anchors; disconnected = no call; TLS inference; wire form). Messaging.Tests 173 + 2 skipped,
Rules.Admin.Tests 27, YFex.Time.Tests 56 — unchanged and green. `YFex.Messaging.Rpc.AotSample` publish with AOT: 158 warnings, all
in ActualLab/MessagePack/Newtonsoft (none in YFex code; pre-existing third-party state).

**Rename (2026-10-07, networking ⚑N4):** `CentralTimeSource` now takes `IServerConnection? server` (was `INetworkStatus? network`;
the old constructor was removed with `INetworkStatus` on 2026-10-07) and `UseCentralAnchor()` resolves `IServerConnection`. Behaviour unchanged: no sample
unless the connection is `Connected` (also not while `Reconnecting`); no registered connection = always try. Messaging.Rpc.Tests 69
green after the rename.

**Network trigger (2026-10-07, networking D5):** `TimeSynchronizationService` takes the optional core `INetworkConditions` (registered by
YFex.Networking) and resynchronizes when the internet returns or its medium changes (cable → Wi-Fi/4G); without it, it also listens
to `NetworkAddressChanged` (before: availability only, which never fires on a cable → Wi-Fi switch). YFex.Time.Tests 56 → 62.
