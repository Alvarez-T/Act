# 10 — Pitfalls, operations and testing

## 1. Pitfalls (design and code)

| Pitfall | Rule |
|---|---|
| Treating the device clock as truth | every validity check goes through `TrustedTimeProvider`; envelopes carry raw and trusted time (05 §4) |
| "It's encrypted, so it's safe from tampering" | encryption is confidentiality. Integrity of offline decision data comes only from central signatures + re-validation (05 §1) |
| Using a randomized API as a KDF | `IDataProtector.Protect` output is not deterministic (F1). Generate a key once and wrap it |
| Re-signing central data at the site broker | never. The site broker **re-serves** central's signed bytes; its own signature is only for its own decisions (site receipts, site approvals, budget slices, sessions) |
| Rejecting a replayed fact | facts are applied and produce findings (06 §3). Only requests can be rejected |
| Comparing approval age with replay time | use the operation's trusted time (F7, 06 §6) |
| Replaying under the current connection identity | the envelope's actor and scope, verified by installation signature (F4, 08 §2) |
| Wall-clock ordering | installation sequence / site sequence (F5) |
| Queuing events offline | events are notifications. Facts must be commands (07 §1) |
| Letting discovery imply trust | discovery gives addresses. Only certificates give trust (04 §2) |
| One review item per envelope | group per (site, outage, kind, actor) (08 §7) |
| Automatic election between two site brokers | not offered. One active site broker, chosen by central only while online and saved in the roster; nothing changes during an outage (02 §4.1) |
| App code creeping into the site broker | the broker runs only generic YFex.Offline services (sessions, approvals, budgets, claims, custody, feed, time, relays). App decisions run on terminals (`OnOffline<T>`), app projections of shared changes run on terminals (`ISiteFeedProjection<T>`) (⚑O20) |
| Generic data replication creeping into YFex.Offline | the site feed distributes only envelopes declared `SharedWithSite`; terminals keep their own `ICacheable` caches. A replica framework is a separate design |

## 2. Operations checklist for a shop LAN (installer and support)

| Issue seen in real shops | Mitigation |
|---|---|
| Windows Defender Firewall blocks the site broker port | the installer adds an inbound rule for the service (port 7443 TCP + beacon UDP), private profile only |
| Router "client isolation" on Wi-Fi blocks terminal ↔ site broker traffic and multicast | health check shows "site broker unreachable from Wi-Fi"; prefer cable for checkouts; manifest addresses + typed address still work if routing is allowed |
| Manager PC sleeps or hibernates | the service installer sets the power plan (no sleep while on AC). The health beacon reports "going to sleep" when possible |
| Manager shuts the PC down to "fix the internet" | expected, see 04 §6. Terminals continue standalone and hand over custody later |
| Cloned VM or disk image used to "install" new terminals | software keys get cloned too. Enrollment records a hardware fingerprint hash; a mismatch → finding. Recommend hardware (TPM) keys |
| TPM absent or disabled in BIOS | fall back to the software KSP and record `KeyProtection.Software`. Central policy can refuse software keys for site brokers (⚑O5) |
| DST and time-zone confusion | every stored time is UTC (`DateTimeOffset`). The display converts |
| Antivirus quarantining the service or locking SQLite files | publisher-signed binaries; documented exclusions for the data folder |
| Disk full on the site broker during a long outage | journal caps with alerts at 80 %. Terminals keep their backups until central's receipt (04 §5) |

## 3. Testing strategy

> **2026-10-14:** YFex.Installations already ships `InstallationHarness` on YFex.Networking's `InMemoryNetwork` (enrolled
> installations, mTLS peer channels, partitions on one simulated LAN), and YFex.Time ships its fake clock. `YFex.Offline.Testing` builds
> on both instead of a harness of its own; I5 adds the chaos parts (partitions, restarts, disk-image restore). The TLS tests below
> already exist in Installations (I3: forged/replayed beacons, rogue host, wrong scope, revoked, untrusted root); Offline re-runs only the
> cases that touch its own channels.

- **`YFex.Offline.Testing`:** an in-memory **multi-installation harness**. Central, site broker and terminals are real DI containers in one
  process, connected by the in-memory Fusion bridge already used by `YFex.Messaging.Rpc.Tests` and by `InstallationHarness` peer
  channels. It offers:
  - `harness.Partition(installation, from: central)` / `.Heal()`, `harness.Kill(siteBroker)`, `harness.Restart(installation, keepDisk: true)`;
  - `FakeTrustedClock` per installation (`SetWallClock`, `AdvanceMonotonic`, `PowerOff(duration)`);
  - `harness.RestoreDiskImage(installation, snapshot)` for rollback tests;
  - assertions: `harness.Central.Findings.ShouldContain(FindingKind.ClockRollback)`, `ShouldHaveAppliedOnce(idempotencyKey)`.
- **Scenario tests:** every scenario in [09](09-pharmacy-scenarios.md) becomes a test with the harness (S1–S10).
- **Property tests** (hand-written generators, no new package): random partitions/heals/restarts over N operations → invariants:
  every envelope applied exactly once; no budget overspent; installation sequences without forks unless a disk restore was injected; custody
  always has exactly one owner.
- **Crypto golden tests:** canonical encodings of `BundleHeader`, `OfflineEnvelope`, receipts and site approval tokens are pinned by
  byte-for-byte golden files (same practice as the approval-token golden test in ⚑Z12).
- **Two-process test for F1:** write with the OS-key provider, restart the process, read back. This is Phase 0's first test.
- **TLS tests:** on the Installations peer endpoint over loopback with test CAs (unique names, see installations ⚑I25 notes): wrong
  site, wrong role, revoked, expired + grace, provisional with pairing off, untrusted root — against the `offline.*` channels.
- **AOT smoke:** `YFex.Offline.AotSmoke` publishes native with 0 warnings: Offline bundle kinds through the Installations verifier,
  envelope sign/verify, trusted clock, in-memory harness subset.

## 4. Performance budgets (benchmarks gate Phase 3)

| Path | Budget | Note |
|---|---|---|
| Create + sign an envelope (ECDsa P-256) | ≤ 150 µs, 1 allocation (payload buffer) | per offline command; a sale takes ≥ 1 s of human time |
| Verify a bundle | ≤ 150 µs | at receipt and load only |
| Route decision (mode × availability table) | ≤ 50 ns, 0 B | per call, online too |
| Trusted "now" | ≤ 30 ns, 0 B | per check |
| Reconciliation at central | ≥ 200 envelopes/s per site, linear in sites | 08 §4 |
| Site broker: concurrent terminals | 30 terminals on a 4-core office PC with SQLite (WAL) | covers large stores |
