# 02 — Topologies, roles and configuration

## 1. The situations (user, 2026-10-02)

| # | Situation | Real example | What is missing |
|---|---|---|---|
| 1 | **One computer alone**, no connection at all | Pharmacy checkout 2 loses internet at 14:00 on a Saturday. The router died, or the cable to the manager's office is cut. | central server **and** every other machine |
| 2 | **Shop LAN with a local, more trusted machine** (the manager's PC) that the other terminals use while the central server is unreachable | ISP outage: the 3 checkouts and the counter terminal still see `PC-GERENTE` on the LAN. | central server only |
| 2b | Same LAN, **central down but internet up** (central outage, not an ISP outage) | Cloud maintenance window. SEFAZ (NFC-e), the card acquirer and Farmácia Popular are still reachable. | central server only. External services still work |
| 3 | Everything connected | normal day | nothing |

2 and 2b differ for **external services**. NFC-e transmission, card payments (TEF) and government programs talk to third parties
directly, not through YFex central. The design never assumes "no central = no internet" ([07](07-what-runs-where.md) §3).

## 2. Terms

| Term | Meaning | Pharmacy example |
|---|---|---|
| **Central** | the authoritative YFex server (cloud or datacenter, possibly a farm). The **only** place where the app's server code runs | `api.farmaviva.com.br` |
| **Site** | a physical place with its own LAN, mapped to a Tenancy scope level. The library does not know "store": the app picks the scope level | `0=farmaviva/1=loja-centro` |
| **Site broker** | the **more trusted** machine of a site. It keeps keys safely and coordinates the site while central is unreachable: sessions, approvals, permission answers, budgets, unique claims, custody of envelopes, the site feed, time, log/audit relays. It runs **no app code** and holds no central secrets (§3) | `PC-GERENTE`, in the manager's locked office, running the generic "YFex Site Broker" service |
| **Terminal** | any machine where people work (checkout, counter, handheld, kiosk). Offline **business decisions run here**, with the app's existing `OnOffline<T>` handlers | `CAIXA-01`, `CAIXA-02`, `BALCAO-01` |
| **Installation** | one enrolled, installed copy of the app (central instance, site broker or terminal) with its own key pairs. Reinstalling creates a new installation | — |

**Installation ≠ tenancy scope ≠ actor.** Every offline operation answers three separate questions:

| Question | Example | Owner |
|---|---|---|
| **Who** did it? | Ana (and the real actor when impersonating) | Authorization (`ActorContext`, ⚑Z3) |
| **For which part of the organization** (whose data)? | `0=farmaviva/1=loja-centro` | Tenancy (`ScopeIdentity`) |
| **On which installed copy of the app** was it decided? | CAIXA-02, with its keys and certificate | `YFex.Installations` (this design) |

They are not one-to-one. Loja Centro is one scope with four installations. The regional manager's laptop is one installation that
works in several stores. Central runs as 10 cloud instances serving all tenants. Enrollment **pins** an installation to a scope, and
central checks that every envelope's scope is within its installation's scope ([06](06-offline-authority.md) §9).

Roles are **logical**. A one-PC pharmacy is a site whose only installation is a terminal. An on-premises deployment can run Central on
the store's own server. The role set is closed and owned by the library: authority levels are the library's concept, not the app's.
The *site* level is app-chosen (a Tenancy scope level), so the library stays domain-agnostic.

```csharp
// YFex core, namespace YFex.Installations (⚑O1) — read by Audit, Logging, Privacy, Authorization, Messaging, also online
public enum InstallationRole : byte { Central = 1, SiteBroker = 2, Terminal = 3 }

// YFex core, namespace YFex.Offline (⚑O1)
/// Static, per site, chosen by an administrator at central (part of the signed site manifest, §5).
public enum SiteTopology : byte
{
    Direct = 1,       // no site broker: terminals talk to central; offline, each terminal is on its own
    SiteBroker = 2,   // one site broker on the LAN coordinates the terminals while central is unreachable
}

/// Dynamic, per installation: the highest authority this installation can reach right now (04).
public enum ConnectivityMode : byte
{
    Central = 1,      // central reachable (directly or relayed by the site broker)
    Site = 2,         // central unreachable, site broker reachable (only in SiteTopology.SiteBroker)
    Standalone = 3,   // nothing reachable: the terminal decides alone within its offline limits
}
```

The user's three topologies map onto these as follows:

| User's topology | `SiteTopology` | Modes a terminal can be in |
|---|---|---|
| cloud-connected | `Direct` | `Central` → `Standalone` |
| LAN with local server | `SiteBroker` | `Central` → `Site` → `Standalone` (broker off too) |
| standalone device | `Direct` with one terminal, or any terminal of either topology that lost everything | `Standalone` |

## 3. Why a broker and not a local application server (user, 2026-10-02)

The user's point: central's server code must not be installed on a store PC. The site machine is there to keep keys safe and to
coordinate (auth, tokens, permissions). It is not a second copy of the server.

| | Local application server (rejected) | **Site broker** (chosen) |
|---|---|---|
| App code on the manager's PC | site handlers + a local business database | **none**: one generic YFex host, the same binary for every app |
| Who makes the business decision (the sale) | the local server | **the terminal that made the sale**, with its `OnOffline<T>` handler (that code is already on every terminal) |
| Secrets on the manager's PC | — | only its own installation keys (TPM). Never the tenant CA root, the bundle-signing keys, the online approval HMAC key, subject keys or database credentials |
| App update | site machine must be updated too | the broker rarely changes (protocol only) |
| Worst case if compromised | could run any site-available business logic | can refuse service, or issue sessions/approvals/budget slices **within central's caps**. Everything it signs is attributable to it and checked at central |

What the broker offers (all generic, [06](06-offline-authority.md) §2, [07](07-what-runs-where.md) §2):

| Service | Example |
|---|---|
| **Sessions** | checks operator code + PIN against central's signed verifier bundle; issues a session token bound to that terminal |
| **Approvals** | checks the supervisor and issues a site-signed approval token |
| **Permission answers** | "may Ana do X?" from central's signed site authorization bundle |
| **Budgets** | reserves and returns amounts; cuts slices for each terminal |
| **Unique claims** | "prescription 12345 is already used in this store" |
| **Custody** | takes a signed envelope, returns a receipt, forwards it to central |
| **Site feed** | passes declared shared changes to the other terminals (a customer registered on CAIXA-01, a store price override) without interpreting them |
| **Time, relays** | signed LAN time anchor; logging and audit relays |

No code exposure is added: terminal binaries are already on store machines and already contain the offline handlers, rules and
validation. Logic that must stay secret or authoritative (pricing administration, settlements, permission administration) never
leaves central.

## 4. Upstream route: who do terminals talk to while central is up? (⚑O3)

| | **A. `CentralFirst`** (recommended default) | **B. `ThroughSiteBroker`** |
|---|---|---|
| Normal day | terminals → central directly; the broker is a warm standby and the log/audit relay | terminals → broker → central (the broker relays every call unchanged) |
| Broker switched off (manager went home and shut the PC) | **nothing changes** | every terminal falls to `Standalone`, even with internet |
| Bad shop internet (one 4G modem) | each terminal uses the uplink | one multiplexed uplink: less traffic |
| Split-brain risk (some terminals on central, others in site mode) | possible for a short time. It is harmless because offline decisions are budget-bounded ([06](06-offline-authority.md) §4) | none |
| Broker load and hosting | only matters when central is down | every call passes through it: must be an always-on service |

**Decision (⚑O3, confirmed 2026-10-15):** `CentralFirst` by default. `ThroughSiteBroker` is a per-site manifest option for poor uplinks.
Both use the same code paths: the route only changes where a call goes ([04](04-connectivity-and-failover.md) §3). Since the
Installations `relay` channel (⚑O26), logs, audit batches and envelopes reach central through any friend in both routes.

### 4.1 Which machine is the broker (⚑O2, confirmed 2026-10-15)

**One active site broker per site, chosen automatically by central while it is reachable, and saved in the signed roster.** Nobody has
to pick a machine, and the choice never changes during an outage. The user's rule (2026-10-15): "have a machine elected at least once and
save it", with a manual option.

1. **Every eligible PC of the site also runs the generic broker service, dormant** (07 §2). Each is its own installation with role
   `SiteBroker`, enrolled once (machine-scope keys). The roster (YFex.Installations, built) lists them all: exactly one
   `RosterEntryStatus.Active`, the others `Standby`. A dormant broker keeps its peer endpoint and answers nothing on the `offline.*`
   channels.
2. **Central ranks the candidates** from what it already receives while the site is online (sketch; Offline Phase 4 owns the policy):

   | Signal | Source | Why |
   |---|---|---|
   | hours online during the site's opening hours | last contact per installation at central (assertion, relay ingress, bundle pulls) | the broker must be on when the outage happens |
   | it can listen: peer endpoint up, firewall rule present | the installation's diagnostics report (firewall-rule presence = Installations I5 item) | an unreachable broker is useless |
   | how many terminals reach it, and how reliably | friend reachability reported by the terminals (small Installations addition) | the broker must be seen by the checkouts |
   | hardware-backed key | enrollment record | site approvals are signed with its evidence key |
   | pinned by an admin | `Sites.Commands.Configure(PinnedSiteBroker: …)` | the manual option |

3. **The best candidate becomes `Active`** in a new roster version, which reaches every installation by push and `bundles` gossip.
   Central re-ranks periodically (default weekly) and switches only when the difference is clear and lasting (hysteresis), never in the
   middle of a busy day. A pinned machine wins unless it has been unreachable for longer than the pin's tolerance (then central raises an
   alert and keeps the pin; the admin decides).
4. **The change happens only while central is reachable**, so there is no state to hand over: the old broker has already delivered its
   journal, receipts and unused budget to central, and the new one starts with a fresh grant. This is why no machine ever has to share
   broker memory with another one.
5. **During an outage nothing is elected or promoted.** The saved broker serves the site; if it is off, terminals are `Standalone`
   within their own slices, and exclusive claims are accepted and flagged (06 §4). Two machines can never both believe they are the
   broker, because only central ever decides.

**Example:** in its first week PC2 is on 11 h a day, PC-GERENTE 6 h. Central marks PC2 `Active` (roster v43); every machine has v43
within seconds. During Saturday's outage PC2 coordinates. If PC2 is off too, the checkouts sell alone within their slices. When the
internet returns, central may promote another machine, still only while online.

**Costs:** one more dormant Windows service per eligible PC; every candidate must be able to listen, so installations ⚑I25 (Windows
refuses TPM keys as a TLS server credential) must be solved or eligible machines use software keys with the hardware-key requirement
relaxed for that role; a small Installations addition (terminals report friend reachability).

**Not offered now:** elections during an outage (consensus, leases, terms, fencing tokens, state hand-over through the terminals and a
per-term succession reserve). The `Standby` list is exactly the ordered successor list such an election would need, so it can be added
later as a per-site opt-in if real sites show the broker off during outages often. Manual promotion during an outage was dropped for the
same split-brain reason (the old broker may be alive on the other side of a cable).

## 5. Configuration: data at central, enrollment on the installation

Two layers, so nobody edits the site setup in a local file:

1. **Site manifest** (data, central). An administrator configures each site at central. Central signs the manifest
   ([05](05-protecting-offline-data.md) §2), and every installation of the site keeps the latest copy.
2. **Enrollment** (once per installation, YFex.Installations — [installations 04](../installations/04-keys-enrollment-certificates.md)).
   The installation gets its identity, role, scope and the tenant's trust anchor. **Who** belongs to the site comes from the signed
   **roster** (Installations, ⚑I8); **how the site operates** comes from the manifest.

> **2026-10-14:** `Installations` and `RevokedInstallations` left the manifest: the roster and the revocation bundle of
> YFex.Installations carry them (built in I1). The manifest cites the roster version it was written against. The coordinator is the
> roster's `SiteBroker` entry (or a named entry, ⚑O27).

```csharp
// (sketch) YFex.Offline — the signed manifest (canonical binary on the wire; shown as a record)
public sealed record SiteManifest(
    string Site,                                  // canonical ScopeIdentity text, e.g. "0=farmaviva/1=loja-centro"
    long Version,                                 // monotonic per site
    SiteTopology Topology,
    UpstreamRoute Route,                          // CentralFirst | ThroughSiteBroker
    long RosterVersion,                           // the Installations roster this manifest was written against (members, roles, addresses)
    ImmutableArray<Uri> CentralEndpoints,         // ordered list (farm, region fallback)
    OfflineLimits Limits,                         // typed limit values that differ from the defaults (05 §7, ⚑O11)
    ImmutableArray<string> SiteFeed);             // commands switched on for the site feed (06 §5, ⚑O23)

public enum UpstreamRoute : byte { CentralFirst = 1, ThroughSiteBroker = 2 }
```

> **2026-10-15 (⚑O11, ⚑O23 confirmed):** `Limits` holds `OfflineLimit<T>` values (built-in `OfflineLimitKeys` and app keys, 05 §7);
> the manifest signs the **resolved** values, so a terminal never needs central's precedence rules. `SiteFeed` lists the commands an
> admin switched on for this site; central refuses a command the developer did not mark `.SharedWithSite<TProjection>()` (06 §5). The
> manifest is signed with Offline's publisher key; installations learn that key from a tenant-signed `offline.publishers` bundle
> (05 §2.3). Built in Phase 2 — the source in `YFex.Offline` (`SiteManifest`, `SiteManifestCodec`) is normative.

### 5.1 Developer side

```csharp
// ✍ Terminal (checkout) — Program.cs
services.AddYFexInstallations(installations => installations     // identity, keys (CNG/TPM), enrollment, friends (YFex.Installations)
    .Application("farma-pdv", version)
    .Terminal()
    .UseWindowsKeyStore()
    .EnrollAt(new Uri("https://api.farmaviva.com.br/"))
    .Peers());

services.AddYFexTime(time => time.UseCentralAnchor().UseSiteAnchor().UsePeerTime());   // YFex.Time + Installations (built)

services.AddYFexOffline(offline => offline
    .Terminal());                                 // modes, envelopes, custody; central endpoints and coordinator from roster + manifest

services.AddYFexMessagingRpcClient(rpc => rpc
    .UseInstallationAssertion()                   // YFex.Installations.Rpc (built)
    .UseOffline());                               // routes to central / broker / local (04)
```

```csharp
// ✍ Central — the tenant CA, enrollment and roster are AddYFexInstallationsCentral (installations 03 §1)
services.AddYFexOfflineCentral(central => central
    .UseSigningKeys(keyRings)                     // Offline's bundle publisher key (manifest, allowances, verifiers, site authorization), 05 §2.3
    .Limits(limits => limits                      // ⚑O11: app defaults (precedence: site admin value → this → library default)
        .Default(OfflineLimitKeys.PermissionSnapshotValidity, TimeSpan.FromHours(48))
        .Declare(FarmaLimits.MaxOfflineDiscount))  // an app key with its own default and hard bounds
    .UseSqlStore(connectionFactory));             // manifests, delivery receipts, budgets, review queue (YFex.Offline.Data, later)
```

The site broker has **no developer code**. It is the generic host `YFex.Offline.Broker`, installed as a Windows service and
configured only by enrollment. Until the future YFex.Installer exists, the code is a start-up argument:

```bash
YFex.Offline.Broker.exe --enroll 7Q4K-M2XD --display-name PC-GERENTE
```

```csharp
// (sketch) what the generic host does inside — shown only to make clear that no app assembly is referenced
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddYFexInstallations(installations => installations
    .Application("yfex-site-broker", version).SiteBroker()
    .Storage(InstallationStorageScope.Machine, directoryPath: null)       // machine-scope state and keys (service account)
    .UseWindowsKeyStore().EnrollAt(centralUri)
    .Peers());                                                             // peer endpoint always on for SiteBroker (⚑O25)
builder.Services.AddYFexOfflineBroker();   // sessions, approvals, permission answers, budgets, claims, custody, feed, site time table
builder.Build().Run();                     // transport: peer channels (⚑O31, recommended), no Kestrel; time, relay, bundles and
                                           // pairing (I5) are Installations channels already
```

Message code changes only when a message needs offline coordination ([06](06-offline-authority.md) §2).

### 5.2 Administrator side: changing a site's setup

```csharp
// (sketch) message at central; protected by sites.configure
await Sites.Commands.ConfigureAsync(new Sites.Commands.Configure(
    Site: "0=farmaviva/1=loja-centro",
    Topology: SiteTopology.SiteBroker,
    Route: UpstreamRoute.CentralFirst,
    PinnedSiteBroker: null,                       // optional: an admin pins a machine; null = central chooses (§4.1)
    Limits: [OfflineLimitSetting.Of(OfflineLimitKeys.PermissionSnapshotValidity, TimeSpan.FromHours(48))],
    SiteFeed: ["AlterarPrecoNaLoja"]), cancellationToken);
```

An admin value outside the key's bounds, an unknown key, or a site-feed entry for a command the developer did not mark is refused when
the command is saved (`Error.Validation`), never when a terminal applies the manifest (⚑O11, ⚑O23). `PinnedSiteBroker` arrives with
Phase 4.

The new manifest version reaches installations like every bundle: pushed while online, pulled at reconnect. An installation never
applies a manifest older than the one it has ([05](05-protecting-offline-data.md) §6).

## 6. Pharmacy deployments (YFex.Farma)

| Deployment | Topology | Notes |
|---|---|---|
| Small: one PC at the counter (sells, registers, closes the till) | `Direct`, one terminal | offline = `Standalone`. There is no broker to verify a manager PIN, so approvals are device-signed and capped (06 §6) |
| Medium: 3 checkouts + 1 counter + manager PC | `SiteBroker`, `CentralFirst`, broker on the manager PC as a Windows service | ISP outage → `Site` mode. Manager PIN approvals verified by the broker. Agreement credit reserved from the site's budget |
| Chain: central in the cloud + 40 stores of mixed sizes | per-store manifest | each store chooses its topology. A store with a bad 4G uplink uses `ThroughSiteBroker` |
| On-premises (no cloud contract) | Central role on the store server | "offline" means only a terminal losing the LAN, i.e. `Standalone` |

## 7. What this design does **not** do

- **No business data replication.** Terminals keep their own `ICacheable` caches. The site feed carries only the envelopes that
  messages declare `SharedWithSite` ([06](06-offline-authority.md) §5). A replica framework would be a separate design (⚑O20).
- **Fiscal contingency rules** (NFC-e offline emission and its transmission deadline) belong to the fiscal module. YFex.Offline tells
  it the mode and gives it the outbox, priorities and trusted clock ([08](08-sync-reconciliation-conflicts.md) §6).
