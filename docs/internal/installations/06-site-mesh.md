# 06 — The site mesh: evaluation and split with YFex.Offline

## 1. The proposal (time session, 2026-10-05, user's idea)

The user asked: can any installation bridge to central, can installations share caches and data, can they auto-detect friendly peers,
and can sharing be scoped by tenancy level? The answer given in chat, now written down and evaluated:

| Idea | Verdict | Why |
|---|---|---|
| any installation can bridge to central | ✅ for **signed or idempotent** traffic (relay) | a relay that cannot forge or read needs no trust; central verifies the producer |
| installations share data | ✅ only **central-signed bundles** and **producer-signed facts** | the receiver verifies the signature, not the messenger |
| installations share caches | ❌ never (⚑O28) | an unsigned `ICacheable` answer is one terminal's view; sharing it lets one compromised checkout poison every price |
| share budgets/credit/stock | ❌ never gossiped (⚑O27) | double spending needs a single writer: partitioned slices + an optional coordinator |
| auto-detect friendly peers | ✅ discovery automatic, **trust only from certificates + roster** (⚑O30) | the network can be spoofed by anyone on the Wi-Fi |
| scope sharing by tenancy level | ✅ per channel, in the signed roster (⚑O29) | relays tenant-wide, facts site-wide, personal data never readable |
| peers as time sources | ✅ as **unauthenticated** fill-in (⚑T13) | like NTP: better than nothing, never for security |

## 2. Channel by channel

| Channel | What travels | Who may answer | Integrity comes from | Where the logic lives | Phase |
|---|---|---|---|---|---|
| **time** | `TimeReply` (received, sent, uncertainty, flags, stratum) | any friend in `FriendScope` with stratum < mine | YFex.Time rules (stratum, never pull back, unauthenticated flag) — **not** from the peer | transport + `ITimeSource`s: Installations; voting/rules: YFex.Time; site time table + findings: Offline | I4 |
| **relay** | opaque signed items → central (log batches, audit batches, offline envelopes, receipts, findings) | any friend that `ReachesCentral` | producer's signature, verified at central; idempotency id deduplicated at the friend and at central | Installations (forward, hold, retry); owners (format, verify at central); Offline adds **custody receipts** R1/R2 on top | I4 |
| **bundles** | central-signed bundles (roster, revocations, manifest, rule data, snapshots for this installation only, allowances) | any `SameSite` friend that holds a newer version | central's signature + **version floors** | Installations (`IBundleStore`, gossip); owners (payloads) | I4 |
| **facts** (`offline.feed`) | producer-signed append-only facts (`SharedWithSite` envelopes: price override, product blocked, customer registered) | any `SameSite` friend; ordering by the coordinator if present | producer's evidence signature; meaning checked by the receiving terminal's `ISiteFeedProjection<T>` within central's guardrails (⚑O23) | Offline (meaning, projection, guardrails); Installations (transport, dedupe by id) | Offline 4 |
| **signed store decisions** | site approvals, site sessions, budget slices, unique claims | **only the coordinator** (the broker, or the roster's named coordinator) | coordinator's evidence signature, verified as of the operation time | Offline | Offline 4 |
| **scarce resources** | — | — | partitioned slices (⚑O14) + coordinator; without it **accept and flag** at reconciliation | Offline | Offline 3–4 |
| **query caches** | — | — | not offered (⚑O28) | — | — |

> **As built (I4, 2026-10-14; 08 §5):** `time` = `PeerTimeChannelHandler` + `SiteAnchorTimeSource` / `PeerTimeSource` slots
> (`UseSiteAnchor()`, `UsePeerTime()`); `relay` = `InstallationRelay` (both ends) + `.Central` `CentralRelayIngress` + `.Rpc`
> `IInstallationCentralLink`; `bundles` = `BundleGossip` (beacon-triggered pulls, announcements, audience check before offering, `Carry`
> for site members) + central pulls over RPC. Consumers (Logging, Audit, Offline) are their sessions' work.

### 2.1 Relay in detail (why "any friend" is safe)

```
 CAIXA-01 (cable to the router fine, firewall blocks its internet)      PC-GERENTE (reaches central over the store's 4G)
   audit batch head signed by CAIXA-01's evidence key ──relay──►  holds it (bytes only), forwards on its own RPC connection
                                                                     central: verifies CAIXA-01's signature + chain continuity
   ◄── DeliveredToCentral (central's ack carried back on the channel)
```

- The friend sees **metadata** (producer id, kind, size, id) and nothing else: personal fields inside are already sealed to central
  (`RecipientSealing`, privacy 05 §5.5).
- A malicious friend can **drop or delay** (the producer keeps its copy until central's ack — custody rule, offline 04 §5), never
  forge (signature) or replay (id deduplicated at central).
- Tenant-wide default: a terminal of Loja Norte visiting? Only if both are on the same LAN, which normally never happens between
  stores; the default exists for the regional laptop and for multi-store buildings (a shopping centre with two FarmaViva kiosks).

### 2.2 Bundle gossip in detail

- Beacons carry the roster version; a friend with an older version asks on `bundles` for "everything newer than my floors for kinds
  K". Bundles addressed to **one installation** (a permission snapshot of Ana for CAIXA-02) are never offered to others (audience
  check before sending, not only at receipt).
- A revocation reaches every friend within one beacon interval + one request, even when central is unreachable for some of them.

### 2.3 Time in detail (⚑T13 amends T12)

| Source on CAIXA-02 | Authenticated | Can anchor | Usable for security (`IsUsableForSecurity`) |
|---|---|---|---|
| central (RPC, built) | yes | yes | yes |
| PC-GERENTE broker (`UseSiteAnchor`, peer channel, mTLS) | yes, **inherits** the broker's flags/uncertainty (time §8.1) | yes | only when the broker itself is authenticated-anchored |
| a friend terminal (`UsePeerTime`) | **no** (a shop device whose clock the same person can change, time §8.3) | **T13: yes, as an unauthenticated candidate** (like NTP): only replies from anchored peers (no `Unanchored` flag, stratum < 16); `Authenticated = false` even over mTLS. Unanchored or stratum-16 replies stay evidence-only | **never** |
| NTP / HTTPS Date | no | yes (fill-in) | never (T3) |

What T13 changes: before, a terminal alone on a LAN without internet and without broker had **no estimate** beyond its device clock;
now it can follow the majority of its anchored friends for display, fiscal `dhEmi` estimates and log ordering, flagged
`Unauthenticated`. What it does not change: NTP-class sources never pull time back (T10: an unauthenticated correction of ≥ 2 s
backward is refused with `SourcesDisagree`), and they never make security usable (T11).

**How YFex.Time implements it (confirmed by the time session, 2026-10-07):**

- Today `TimeSelection.ToCandidate` makes every `TimeSourceKind.Peer` reply evidence-only. T13 changes only that rule: an anchored
  peer's reply becomes an eligible candidate that is always unauthenticated.
- Precedence needs no new code. In `TimeSelection.Choose`, authenticated candidates (central, broker) always win. With three or more
  unauthenticated answers, the Marzullo vote applies. **Consequence:** peers vote **together with NTP**, not only when NTP is silent.
  If the user wants peers strictly after NTP, YFex.Time needs a small per-kind priority (sub-option of ⚑T13).
- "≥ 2 trustworthy peers raise `RollbackDetected`" (time §8.3) is **not implemented**; today peers raise only `SourcesDisagree`. It
  would need a peer-evidence hook in YFex.Time.
- Installations implements the core `ITimeSource`. t1/t4 come from `device.GetTimestamp()` around the exchange, and the server side
  answers with `TimeProvider.CreateTimeReply(receivedTimestamp)`. On the `time` channel the wire is an explicit versioned form like
  `CentralTimeWire` (version byte + `TimeReply.TryWriteBytes`), never MemoryPack (it copies unmanaged structs as raw memory). Tests
  use `YFex.Time.Testing` (`FakeDeviceClock`, `FakeTimeSource`) and the pattern in `YFex.Time.Tests/SiteTimeTests.cs`.
- The time session updates time README §5 and §8.1/§8.3 and the code after approval. **Done in I4 (2026-10-14)** for the rule itself (no
  Time session was running): `TimeSelection.ToCandidate` and time README §5 (T13 row) and §10; §8.1/§8.3 prose still describes the earlier
  plan (broker health probe, peers evidence only) and is marked there.

## 3. What stays in YFex.Offline (and what it gets from here)

| Offline keeps | Offline gets from Installations |
|---|---|
| `ConnectivityMode`, `IConnectivity` (composes `IServerConnection` + `INetworkConditions` + friend reachability) | `IInstallationFriends` (is the broker a friend and reachable? does any friend reach central?) |
| `SiteTopology`, `UpstreamRoute`, `SiteManifest` (now citing the roster version), `OfflineLimits` | roster, bundle store, verifier, floors |
| `.OnOffline<T>()`, `.Offline(...)`, facts vs requests, findings, review queue, reconciliation as of | — |
| `OfflineAllowance` budgets, unique claims, coordinator choice semantics (⚑O14, ⚑O27) | the coordinator's identity from the roster (`Role = SiteBroker` or a named entry) |
| `OfflineEnvelope`, installation sequence (`HashChain`), custody + signed receipts R1/R2 | evidence key (`IInstallationKeyStore`), relay channel, peer channels |
| site feed meaning + `ISiteFeedProjection<T>`, guardrails (⚑O23) | `offline.feed` channel transport |
| offline login, site sessions, `ApprovalTokenKind.Site` issuing at the broker | peer channels, evidence key |
| site time table + findings (time §8.2) | `TimeReply` from every friend's probe on the `time` channel |
| broker services and the generic broker host (⚑O20) | peer endpoint, TLS helpers, enrollment (`IInstallationEnrollment`; the installer comes later as YFex.Installer) |

**What Offline took (Phases 1–3, as of 2026-10-19):** `IInstallationFriends` for `Reachability` (`OfflineConnectivity`), the roster and
`BundleStore` with floors for its manifest, allowances and publisher endorsement (`OfflineBundles()`, `PullableOfflineBundles()`), the
evidence key for envelopes, sessions and delivery receipts, the `relay` channel for `offline.session` / `offline.envelope` /
`offline.delivery` items (central `IRelayItemConsumer`s) and `BundleStore.Accepted` for automatic delivery receipts. Phase 4 adds the four
`offline.*` peer channels (⚑O31) and the diagnostics summary for the broker ranking.

## 4. Why no automatic election (⚑O2, ⚑O27 restated for the mesh)

With every terminal able to talk to every other, "elect a coordinator when the broker is off" looks free. It is not: two checkouts on
both sides of a flaky switch would both win, and both would hand out the same agreement credit. A pharmacy cannot run a consensus
protocol's failure modes. The roster names the coordinator (normally the broker; optionally a standby that a manager promotes with a
signed site action); without a coordinator, terminals spend **their own pre-cut slices** and exclusive claims are **accepted and
flagged** at reconciliation. The mesh changes only *how messages travel*, never *who decides*.

> **2026-10-15 (Offline ⚑O2, confirmed by the user):** the coordinator is chosen **automatically by central while online** and saved in
> the roster: every eligible PC runs a dormant `SiteBroker` installation, central ranks them and marks one `Active`, the others
> `Standby`; an admin may pin one; nothing changes during an outage (offline 02 §4.1). Asks of this library (for Offline Phase 4):
> terminals report friend reachability to central, firewall-rule presence in diagnostics (I5), and ⚑I25 for every listening candidate.
> **✅ Built 2026-10-19** (08 §5): diagnostics reports (`ReportDiagnostics()`, signed relay item `installations.diagnostics`, central
> `CollectDiagnostics()` + `SummarizeAsync`), the Windows firewall check of the rule "YFex Installations peer" (`CheckWindowsFirewall()`),
> and ⚑I25 decided as a software transport key for listeners with a hardware evidence key.

## 5. Scenarios at Loja Centro (mesh = `AllInstallations`)

| Time | Event | With the mesh |
|---|---|---|
| 09:00 | normal day | terminals talk to central directly (⚑O3 `CentralFirst`); friends exchange beacons; the roster v42 is everywhere |
| 10:12 | CAIXA-01's route to the internet fails (switch port VLAN mistake), LAN fine | CAIXA-01 stays `Central` for **relay** (its log/audit batches go via PC-GERENTE or CAIXA-02) but its own RPC is down → Offline decides the mode (Site via broker) |
| 11:30 | admin revokes the stolen spare PC at central | revocation bundle v17 reaches PC-GERENTE online, then CAIXA-01 through `bundles` gossip within ~5 s |
| 14:00 | ISP outage | Offline `Site` mode; time from the broker (inherits its last central anchor); relay holds items |
| 15:10 | manager shuts down PC-GERENTE | Offline `Standalone`; CAIXA-01/02 keep `time` from each other (T13, unauthenticated), facts already shared stay, new facts wait for the coordinator or are shared peer-to-peer if the roster allows `offline.feed` without coordinator |
| 16:05 | internet back on CAIXA-02 only (its 4G fallback) | CAIXA-02 `ReachesCentral` → CAIXA-01 relays its held batches and envelopes through CAIXA-02; custody receipts come back from central |

## 6. Costs and limits

- **Firewall:** every listening terminal needs an inbound rule (TCP 7443) — future YFex.Installer work (the technician until then), checked by YFex.Networking; this is why
  ⚑O25 defaults to `BrokerOnly`.
- **TPM signatures:** one beacon per 5 s per listening installation (≈ 0.6 % of one core on a TPM at 30 ms); verifying is software.
- **Connections:** full mesh at a 10-terminal site = 45 TLS connections, idle-closed after 2 min; channels open them lazily.
- **Not a replication framework:** no business tables are synchronized; bigger stores that want a local database replica need a
  separate design (offline 02 §7 unchanged).
</content>
</invoke>
