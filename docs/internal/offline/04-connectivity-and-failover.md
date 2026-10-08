# 04 — Connectivity, discovery, routing and failover

## 1. Connectivity is a mode, not a boolean (fixes F9)

```csharp
// YFex core, namespace YFex.Offline — read by Messaging, Audit/Logging shippers, Jobs, UI
public interface IConnectivity
{
    ConnectivityMode Mode { get; }                 // Central | Site | Standalone (02 §2)
    Reachability Reachability { get; }             // what this installation sees right now
    event Action<ConnectivityChange>? Changed;     // (previous, current, reason, at trusted time)
}

public readonly record struct Reachability(
    PeerHealth Central,                            // Healthy | Degraded | Unreachable
    PeerHealth SiteBroker,                         // NotConfigured when SiteTopology.Direct
    bool SiteBrokerReachesCentral);                // reported by the broker in its health reply
```

`IServerConnection` (Messaging.Rpc; renamed from `INetworkStatus` by networking ⚑N4, one instance per server peer: central and the site broker) stays the per-peer link state; machine-level conditions come from core `INetworkConditions`, and `PeerHealth` is core `EndpointHealth` (networking 05 §7). `SyncStatus` gains `Mode`,
`ModeText`, `OfflineSince`, `PendingOnThisInstallation`, `HeldBySiteBroker`, `LastCentralContact` and `OpenFindings`. The UI can then
say what is true:

| Mode | Status bar (pt-BR, app text) | What the user can expect |
|---|---|---|
| `Central` | "Conectado" | everything |
| `Central`, relayed by the broker | "Conectado (via servidor da loja)" | everything |
| `Site` | "Modo loja — sem conexão com a central desde 14:02" | offline-capable operations, PIN sign-in, approvals by present managers, site budgets, unique claims, shared store changes; central-only actions disabled |
| `Standalone` | "Modo isolado — 12 operações aguardando envio" | offline-capable operations within this terminal's limits; device-signed approvals only |

## 2. Finding the site broker (⚑O6)

> **2026-10-05:** candidates come from YFex.Networking beacons (kind `yfex.installation`, signed and verified by
> YFex.Installations) and the roster's last known addresses; trust from the installation certificate + roster (installations 05 §1, §6).

Discovery only returns **candidate addresses**. Trust always comes from the certificate check ([03](03-trust-enrollment-pairing.md) §4),
so a lying beacon costs a failed handshake and nothing more.

1. **Manifest addresses.** While online, the broker reports its hostname and current LAN addresses to central. They reach terminals in
   the manifest. The terminal tries the last address that worked, then the others.
2. **LAN beacon.** Every 5 s, and on request, the broker multicasts a 120-byte UDP datagram (BCL `UdpClient.JoinMulticastGroup`;
   group and port configurable): `magic | version | installation id | port | site hash | trusted time | evidence signature`.
   Terminals listen only when the known addresses fail. The site hash is an HMAC of the site id with a manifest secret, so passers-by
   cannot learn the tenant. If YFex.Devices "LAN discovery" lands first, the beacon becomes one of its announcement kinds.
3. **Typed address.** A manager can type `192.168.0.10:7443` in the terminal settings. The certificate is still checked.

DHCP changing the broker's IP needs no reconfiguration: the next beacon carries the new address.

## 3. Routing: where a call goes in each mode (Fusion, no fork)

There are two Fusion client peers: `central` and `broker`. The broker peer exposes only the generic broker services. **No app message
is ever executed on the broker.** In `Site` and `Standalone` modes the terminal runs the app's own `OnOffline<T>` handler locally.
The broker only coordinates around that handler (session, permission answer, budget, unique claim) and then takes custody of the
envelope.

```csharp
// Before — FusionMessageBus.CommandAsync (simplified): one server, online or not
if (!network.IsConnected && command is IQueueable) return await OfflineCommandAsync(command, ct);
return await Send(proxy.Execute, command, await HeadersAsync(ct), ct);

// After (sketch) — the envelope is created once and travels unchanged
var offline = registry.OfflinePolicy<TCommand>();                        // compiled from .OnOffline<T>() + .Offline(...) — 06 §2
var envelope = envelopes.Create(command);                                // idempotency key, installation sequence, actor, scope, versions
if (connectivity.Mode == ConnectivityMode.Central)
    return await central.SendAsync(envelope, ct);                        // central runs the app's server handler, as today
if (!offline.IsAvailable)
    return Error.Unavailable(offline.Reason);                            // "requires the central server"

var coordination = await coordinator.PrepareAsync(envelope, offline, ct);  // Site: broker. Standalone: local slice/claims (06 §4)
if (coordination.Refused is { } refusal) return refusal;                 // over budget, number already claimed, approval required…
var result = await local.RunOfflineHandlerAsync(envelope, ct);            // the app's IOfflineHandler<TCommand, TResult> on THIS terminal
await custody.HandOverAsync(envelope.WithCoordination(coordination), ct); // Site: broker receipt R1 (+ site feed if shared). Standalone: outbox
return result;
```

Queries: in `Site` and `Standalone` mode an `ICacheable` query is answered from the terminal's own cache (stale flags shown).
Non-cacheable queries return `Error.Unavailable`, and the UI shows "indisponível offline". The broker keeps no business cache.

`ErrorType.Unavailable` is a new additive member of the Cqrs `ErrorType` and of MemoryPack wire v2, like `Forbidden` in ⚑Z11. It is
distinct from `Fail` so the UI can say *why*.

## 4. Health checks and hysteresis

| Rule | Default | Why |
|---|---|---|
| Leave a mode | 3 failed probes, 5 s apart (≈ 15 s), or immediately after a dropped connection fails to reconnect twice | a single lost packet must not flip the store into site mode |
| Probe | Fusion peer state + `Sites.Queries.Health` (returns trusted time, `SiteBrokerReachesCentral`, manifest version) | one round trip gives time anchor, reachability and staleness |
| Go back up (Standalone → Site → Central) | after 30 s of healthy probes; each failed return doubles the wait (max 10 min) | prevents flapping on a bad 4G link |
| Minimum time in a mode | 60 s | the UI should not blink |

**A call in flight when the link drops is "in doubt".** Central may or may not have executed it. The terminal re-sends the **same
envelope** (same idempotency key). If the operation is offline-capable, the terminal may run its offline handler for it, but central
deduplicates by key (Phase 0 fixes F3), so the operation is recorded once. A difference between the terminal's provisional result and
central's result becomes a finding ([08](08-sync-reconciliation-conflicts.md) §5).

## 5. Custody: exactly one owner of every queued operation

```
 terminal outbox ──(broker receipt R1)──► broker journal ──(central receipt R2)──► central (reconciled + findings)
       │                                       │                                         │
       └─ keeps its copy until R2 arrives ─────┴─ R2 is relayed down to the terminal ◄───┘
```

- **Custody transfers only on a signed receipt.** The broker signs R1 with its evidence key. Central signs R2.
- **The terminal keeps a backup copy until R2.** The broker is a normal PC, and its disk can die during the outage. If the broker is
  lost, revoked or replaced, terminals re-send their unconfirmed envelopes straight to central. Deduplication makes that harmless.
- **Standalone terminals hand over custody** as soon as the broker reappears, even if central is still down. The broker then
  accounts their budget slices and claims, feeds their shared changes to the other terminals, and forwards everything even if the
  terminal is switched off afterwards.
- The broker stores envelopes as signed bytes. It never re-signs them, and it never interprets the payload, except to forward shared
  changes on the site feed.
- Logging and audit shipping use the same custody idea through their own relays (logging 05 §6, audit 05 §6). Their batches are
  already signed end to end, so the broker needs no trust to relay them ([07](07-what-runs-where.md) §2).

## 6. Scenarios (medium pharmacy: 3 checkouts, 1 counter, `PC-GERENTE` as broker, `CentralFirst`)

| Time | Event | What happens |
|---|---|---|
| 14:00 | ISP outage | probes fail; at 14:00:15 every terminal enters `Site` mode |
| 14:03 | sale on CAIXA-01 | CAIXA-01 runs its `OnOffline` handler. If the sale is on account, the broker first reserves the agreement credit. PC-GERENTE takes custody (R1) |
| 14:20 | discount above the limit on CAIXA-01 | `ApprovalRequired`. The manager types her code + PIN on CAIXA-01, and PC-GERENTE verifies it and issues a **site-signed** approval ([06](06-offline-authority.md) §6) |
| 14:30 | manager lowers the price of dipyrone on CAIXA-01 | store override, applied on CAIXA-01, then shared through the site feed. CAIXA-02 and BALCAO-01 show the new price within seconds ([06](06-offline-authority.md) §5) |
| 15:10 | the manager shuts down `PC-GERENTE` to "fix the internet" | terminals enter `Standalone`. New envelopes stay local; device-signed approvals only, smaller caps; budgets from each terminal's own slice |
| 15:30 | `PC-GERENTE` is back | terminals hand over custody (R1) and catch up on the site feed |
| 16:05 | internet back | the broker forwards its journal to central (evidence first, then envelopes in site-sequence order, [08](08-sync-reconciliation-conflicts.md) §4). Terminals return to `Central` after 30 s; R2 receipts flow down; findings appear in the review queue |
| — | variant: the Wi-Fi counter terminal cannot reach the internet (firewall), but the broker can | the counter terminal sends calls through the broker relay: mode `Central` (relayed). No offline decisions |

## 7. Split brain: why it is tolerable

During a transition one checkout may still reach central while another is already in `Site` mode. The two cannot overspend the same
thing:

- **Budgets are partitioned in advance.** Central sets aside the site's budget, and the broker gives each terminal a slice
  ([06](06-offline-authority.md) §4). Central never spends the site's share while the site holds it.
- **Identifiers are created where the data is created** (UUID v7, ⚑O21), so two installations never mint the same sale id. Fiscal
  numbering uses each terminal's own NFC-e series.
- What remains (e.g., the e-commerce channel at central selling the last unit that the store sold offline) is a **business conflict**,
  resolved at reconciliation ([08](08-sync-reconciliation-conflicts.md) §5). It is not a consistency bug.
