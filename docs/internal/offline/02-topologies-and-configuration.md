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

**Recommendation:** `CentralFirst` by default. `ThroughSiteBroker` is a per-site manifest option for poor uplinks. Both use the same
code paths: the route only changes which Fusion peer a call goes to ([04](04-connectivity-and-failover.md) §3).

**One active site broker per site.** A standby can be listed and promoted by a manager (signed manifest change, or offline promotion
with `sites.broker.promote`, flagged). Automatic election between two brokers is a consensus problem with failure modes worse than the
outage it solves. Not offered (⚑O2).

## 5. Configuration: data at central, enrollment on the installation

Two layers, so nobody edits the site setup in a local file:

1. **Site manifest** (data, central). An administrator configures each site at central. Central signs the manifest
   ([05](05-protecting-offline-data.md) §2), and every installation of the site keeps the latest copy.
2. **Enrollment** (once per installation, [03](03-trust-enrollment-pairing.md)). The installation gets its identity, role, site and
   the tenant's trust anchor. After that, everything else comes from the manifest.

```csharp
// (sketch) YFex.Offline — the signed manifest (canonical binary on the wire; shown as a record)
public sealed record SiteManifest(
    string Site,                                  // canonical ScopeIdentity text, e.g. "0=farmaviva/1=loja-centro"
    long Version,                                 // monotonic per site
    SiteTopology Topology,
    UpstreamRoute Route,                          // CentralFirst | ThroughSiteBroker
    ImmutableArray<InstallationEntry> Installations,   // id, role, display name, certificate thumbprint, LAN addresses, Active/Standby/Revoked
    ImmutableArray<Uri> CentralEndpoints,         // ordered list (farm, region fallback)
    OfflineLimits Limits,                         // validity windows, caps, pairing switch, store-shared message list (05 §7, 06 §4–§5)
    ImmutableArray<string> RevokedInstallations); // also delivered as a separate, smaller revocation bundle (05 §2)

public enum UpstreamRoute : byte { CentralFirst = 1, ThroughSiteBroker = 2 }
```

### 5.1 Developer side

```csharp
// ✍ Terminal (checkout) — Program.cs
services.AddYFexOffline(offline => offline
    .Terminal()                                   // role; site, central endpoints and broker come from enrollment + manifest
    .UseWindowsKeyStore());                       // installation keys in CNG/TPM (YFex.Offline.Windows, 03 §2)

services.AddYFexMessagingRpcClient(rpc => rpc.UseOffline());   // replaces WebSocketEndpoint: routes to central / broker / local (04)
```

```csharp
// ✍ Central
services.AddYFexOfflineCentral(central => central
    .UseSigningKeys(rootKeyProvider)              // Privacy IRootKeyProvider wraps the CA and bundle-signing keys (05 §2.3)
    .UseSqlStore(connectionFactory));             // manifests, enrollments, delivery receipts, budgets, review queue (YFex.Offline.Data, later)
```

The site broker has **no developer code**. It is the generic host `YFex.Offline.Broker`, installed as a Windows service and
configured only by enrollment:

```bash
yfex-install enroll --role site-broker --code 7Q4K-M2XD
```

```csharp
// (sketch) what the generic host does inside — shown only to make clear that no app assembly is referenced
var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddYFexOfflineBroker(broker => broker.Listen(port: 7443));   // Kestrel, TLS 1.3, client certificates required (03 §4)
var app = builder.Build();
app.MapYFexOfflineBroker();      // sessions, approvals, permission answers, budgets, claims, custody, feed, time, pairing, relays
app.Run();
```

Message code changes only when a message needs offline coordination ([06](06-offline-authority.md) §2).

### 5.2 Administrator side: changing a site's setup

```csharp
// (sketch) message at central; protected by sites.configure
await Sites.Commands.ConfigureAsync(new Sites.Commands.Configure(
    Site: "0=farmaviva/1=loja-centro",
    Topology: SiteTopology.SiteBroker,
    Route: UpstreamRoute.CentralFirst,
    SiteBroker: pcGerenteInstallationId,
    Limits: OfflineLimits.Default with { PermissionSnapshotValidity = TimeSpan.FromHours(48) }), cancellationToken);
```

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
