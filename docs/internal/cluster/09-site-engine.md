# 09 — The shop site: contracts in Cluster, the engine in Offline/Installations (last phases)

Built **last** (CL-P8/P9), after Offline 4f (broker choice) and Hosting H0. Recorded now so the contracts of the first phases already
fit it (the user: "start small", site scenarios at the end).

## 1. Why the site is different

| | Central (cloud / on-premises) | Shop site |
|---|---|---|
| Shared state | one database: the arbiter | **none**; each machine has its own stores |
| Splits | rare, short; the database decides | **normal**: a switch fails, a terminal's cable is pulled, the broker PC is switched off at night |
| Devices | servers in a data centre or the customer's server room | PCs at the counter, handhelds; less trusted |
| Trust | database credential (08 §3) | enrollment evidence + mTLS (Installations), roster, revocations |
| Coordination owner | YFex.Cluster | **Installations + Offline** (friends, gossip, roster `Active`/`Standby` broker, ranking, 4f broker choice) |

So Cluster does **not** coordinate the site. But app code needs the same question answered there: "only one machine in Loja Centro
polls iFood", "only one machine refreshes the label printer's price file", "only one machine uploads the night's camera snapshots to
central". Writing that code twice (one way for central, another for the store) is what Cluster avoids.

## 2. The split of work

| Piece | Owner |
|---|---|
| Contracts (`IClusterLeases`, `IClusterWorker`, `IClusterNotices`, `IClusterMembership`, `IClusterHealth`), the runner, drain, `IClusterEngine` seam, `LeaseGuarantee` | **YFex.Cluster** (already built by then) |
| Site engine `SiteClusterEngine : IClusterEngine` with `Guarantee = BestEffort` | **YFex.Offline** (broker services) on **YFex.Installations** (peer channel, roster, evidence) |
| A `cluster` peer channel (request/response: acquire, renew, release, notice; ALPN-negotiated, framed, rate-limited like the other channels) | **YFex.Installations** (`PeerChannel` registry) |
| The coordinator: lease table in the **Active broker** (memory + the durable broker store of Offline 4c), token counter persisted there | **YFex.Offline** |
| Membership = the site's `FriendSet` (reachable, enrolled, not revoked) + roster roles | **YFex.Installations** |
| Fallback coordinator choice when the broker is away = the signed ranking (`offline.feed.ranking`, factors of 4e/4f) | **YFex.Offline** |
| Site time for lease durations = the coordinator's monotonic clock; terminals use their own monotonic deadlines (same rule as 05 §3); trusted time from the site time table (4f) only for the clock check | **YFex.Offline**, **YFex.Time** |
| Where app workers run: terminal apps, or an app's own host module (Hosting ⚑H4). **The broker runs no app code** (offline: generic service); it only coordinates | apps, **YFex.Hosting** |

## 3. The low-stakes rule (⚑CL24)

- The site engine grants only **`LeaseGuarantee.BestEffort`**. During a split, two machines may both believe they hold a key.
- A worker declares what it needs. `RequireGuarantee(LeaseGuarantee.Fenced)` → **registration fails** on a site engine (startup
  error naming the worker): money and fiscal work cannot be placed at the site by accident.
- **Money or fiscal work stays with central**, or with Offline's **allowance model** (budgets granted by central, spent locally,
  reconciled later) — never with a site lease.
- Site leases still carry **tokens** (from the coordinator's counter). Work that reports to central sends its token, so central can
  *detect* a duplicate after the fact ("two machines polled iFood with tokens 41 and 42 between 14:02 and 14:05") and flag it in the
  review queue. Detection, not prevention.
- **No consensus protocol** (Raft, Paxos, quorum leases) among terminals (⚑CL23): weak devices, 2–30 machines that come and go, splits
  that are normal, and an operations burden no pharmacy can carry. If a piece of work ever needs consensus at a store, it is not
  low-stakes and belongs to central or the allowance model.

## 4. Behaviour

| Situation | What the site engine does |
|---|---|
| Normal: the Active broker reachable | the broker grants, renews, releases; one holder per key; notices relayed by the broker |
| Broker restarts | the coordinator rebuilds its table from the durable broker store; holders renew within their margin; tokens keep growing |
| Broker away (switched off at night, crashed) | per worker: **`WhenCoordinatorAway.Pause`** (default: the work waits) or **`RankedFallback`**: the highest-ranked reachable machine (signed ranking) acts as a temporary coordinator for keys that opted in. When the broker returns, its table wins; the fallback's holders release at their next renewal |
| Split: CAIXA-02 cut from the broker but online to central | CAIXA-02's lease expires locally (monotonic deadline); it stops. Another machine takes the key through the broker. No overlap as long as clocks of the coordinator and holder advance normally |
| Split with fallback: two halves, each with a coordinator | overlap possible (BestEffort). Tokens from different coordinators carry the coordinator's installation id, so central sees the overlap when both report |
| Standby broker promoted (4f) | the new Active broker starts with an empty table + a token epoch above the old one's (epoch from the roster version); holders re-acquire |
| Terminal revoked | removed from membership; its lease requests are refused by mTLS before they reach the coordinator |

## 5. Scenarios (examples only; pharmacy words allowed here)

| Scenario | Worker | Site guarantee OK? | Away policy |
|---|---|---|---|
| Poll iFood/Rappi orders for the store (licensing 01 §4) | `DeliveryOrdersPoller`, `Worker` per store | ✅ orders deduplicated by order id | `RankedFallback` (orders must keep flowing) |
| Refresh the shelf-label printer's price file after a price change | `LabelPriceSync` | ✅ idempotent file | `Pause` |
| Upload the night's backup of the broker store to central or a NAS | `SiteBackupUploader` | ✅ upload keyed by date | `Pause` |
| Pre-fetch product images for the counter terminals | `CatalogImagePrefetch` | ✅ cache | `RankedFallback` |
| **SNGPC report** (controlled medicines) | — | ❌ regulatory: sent by **central** from the reconciled data | — |
| **NFC-e contingency transmission** | — | ❌ fiscal: **each terminal transmits its own documents** (it owns them; no coordination needed) | — |
| **Card payment / PIX confirmation** | — | ❌ money: central or the allowance model | — |
| Cash-drawer opening limits offline | — | ❌ Offline allowances | — |

The same `DeliveryOrdersPoller` class from 03 §2 runs unchanged: on central it is per tenant (Fenced not required), at the store it
is per store (BestEffort accepted). The composition root decides which engine is installed.

## 6. Contracts that must already fit (checked in CL-P1)

- `LeaseGuarantee` exists from P1, and `RequireGuarantee` is enforced at registration.
- `IClusterEngine` has no SQL-specific member; counters and claims are separate seams (the site does not offer them).
- `LeaseGrant.Remaining` is a duration, not an instant (the site coordinator has no shared wall clock).
- `ClusterNode.Identity.Installation` carries the terminal's installation id at the site.
- The harness can run with `Guarantee = BestEffort` and inject splits, so site behaviour is testable before the site engine exists.
