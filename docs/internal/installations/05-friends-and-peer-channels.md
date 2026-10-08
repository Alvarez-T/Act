# 05 — Friends, roster, signed bundles, peer channels

## 1. Friend recognition (⚑I7)

A peer is a **friend for a channel** when all of these hold. Nothing here reads an IP address, subnet, SSID, beacon field or
fingerprint:

| # | Check | Source | Failure |
|---|---|---|---|
| 1 | the TLS 1.3 peer certificate chains to the **pinned tenant root**, valid at trusted now (or within grace) | `CertificateTrust.Validate(…, requiredUsage)` | `Untrusted` / `Expired` |
| 2 | no certificate of the chain is in the current **revocation bundle** (central or site revocations) | signed bundle, version floor | `Revoked` |
| 3 | URNs read from the trusted leaf: installation id, scope, role | `InstallationCertificate.Read` | `Malformed` |
| 4 | the installation is listed in **my scope's roster** (as member or visitor), with the same evidence key id; or it is a provisional certificate from my site issuing CA and pairing is enabled | roster bundle | `NotInRoster` / `ProvisionalNotAllowed` |
| 5 | the relation (below) satisfies the channel's sharing scope (§5) | roster channel table | `ChannelNotShared` |

```csharp
// YFex.Installations — one validator for both TLS directions, the beacon verifier and channel opening
public sealed record PeerRefused(PeerRefusal Reason, string Detail);
public sealed record PeerAccepted(Friend Friend);
public union PeerCheck(PeerAccepted, PeerRefused);
public enum PeerRefusal : byte { Untrusted, Expired, Revoked, Malformed, NotInRoster, ProvisionalNotAllowed, ChannelNotShared, RoleNotAllowed }

public PeerCheck Check(X509Certificate2 peer, IReadOnlyList<X509Certificate2> chain, PeerChannel channel);   // InstallationTrust
```

| Relation | When | Example |
|---|---|---|
| `SameSite` | peer's scope = my site scope and it is a roster member | CAIXA-01 ↔ PC-GERENTE |
| `Visitor` | peer is listed under `Visitors` in my site roster | NOTEBOOK-REGIONAL at Loja Centro |
| `OtherSite` | same tenant root, different scope, not listed — friend **only** for channels whose sharing scope is `Tenant` | a terminal of Loja Norte relaying through Loja Centro? (only if the tenant roster allows it; default no LAN contact between stores anyway) |
| `Central` | role `Central` | — (central is reached over the RPC connection, not the LAN mesh) |

## 2. The roster (⚑I8)

```csharp
// YFex.Installations — payload of the bundle kind "installations.roster" (canonical binary; shown as records)
public sealed record InstallationRoster(
    string Scope,                                     // "0=farmaviva/1=loja-centro"
    long Version,
    ImmutableArray<RosterEntry> Members,
    ImmutableArray<RosterVisitor> Visitors,
    MeshMode Mesh,                                    // BrokerOnly | AllInstallations (⚑O25)
    ImmutableArray<ChannelSharing> Channels,          // per channel: FriendScope (⚑O29)
    bool PairingEnabled,                              // ⚑O7
    KeyId SiteTagKey,                                 // which derived key the beacon site tag uses (§6)
    ImmutableArray<X509Certificate2> SiteIssuingCa);  // empty unless pairing is enabled

public sealed record RosterEntry(InstallationId Id, string DisplayName, InstallationRole Role, KeyId EvidenceKey,
    ImmutableArray<IPEndPoint> LastKnownAddresses, ushort PeerPort, RosterEntryStatus Status);   // Active | Standby | Revoked

public sealed record RosterVisitor(InstallationId Id, string DisplayName, ImmutableArray<PeerChannel> Channels, ValidityPeriod Validity);
public enum MeshMode : byte { BrokerOnly = 1, AllInstallations = 2 }
```

- Published by `.Central` whenever an installation of the scope is enrolled, revoked, renamed or changes address; pushed while online,
  pulled at reconnect, **gossiped between friends** (§3). Never applied if older than the floor.
- The roster is **who**; Offline's `SiteManifest` keeps **how the site operates** (topology, route, `OfflineLimits`) and cites
  `RosterVersion`. The fields `SiteManifest.Installations` and `RevokedInstallations` (offline 02 §5) move here.
- `LastKnownAddresses` come from each installation's own report to central while online; they are the first candidates (§6).

## 3. Signed bundles and version floors (⚑I9)

Format exactly as offline 05 §2, with the header in core (02 §4):

```csharp
// YFex.Installations
public sealed record SignedBundle(BundleHeader Header, ReadOnlyMemory<byte> Payload, DetachedSignature Signature);
public enum BundleCheck : byte { Valid, BadSignature, UnknownKey, WrongAudience, NotYetValid, Expired, OlderThanFloor, ConflictingSameVersion, Malformed }

public interface IBundleVerifier                          // the one verifier every consumer uses — at receipt AND at every load
{
    BundleCheck Verify(SignedBundle bundle, TrustedInstant now);   // audience = this installation, its scope, its tenant
}
public interface IBundleStore                             // persisted bundles + per-stream floors (sealed with device/storage)
{
    ValueTask<BundleCheck> AcceptAsync(SignedBundle bundle, CancellationToken cancellationToken);   // verifies, raises the floor, stores
    ValueTask<SignedBundle?> GetLatestAsync(BundleKind kind, string stream, CancellationToken cancellationToken);
    event Action<SignedBundle>? Accepted;
}
```

- Signature: `SignedPayload` with domain `yfex.bundle.v1` over the canonical header (crypto 05 §9); bundle signing keys = central
  `SigningKeyRing` `installations/bundles` (yearly, public keys in the state file and refreshed by a signed key-list bundle).
- **Owners keep meaning**: Offline registers `OfflineBundleKinds.Manifest/Allowance/CredentialVerifiers/SiteAuthorization`,
  Authorization 3b `PermissionSnapshot`, Rules.Admin `RuleData` — they call `IBundleStore.AcceptAsync` and read payloads they
  understand. Offline's Phase 2 acceptance ("S4 tampered table") runs through this verifier.
- **Same version, different hash** = `ConflictingSameVersion`, a security finding (offline 05 §6, unchanged).

## 4. The peer endpoint and channels (⚑I10)

```
 PeerEndpoint (one per installation that listens; SiteBroker always, terminals when Mesh = AllInstallations)
   TCP :7443 ─► SslStream (TLS 1.3 only, ALPN "yfex-peer/1", client certificate REQUIRED, server certificate = transport key)
            ─► InstallationTrust.Check (both directions, §1) ─► FriendConnection
            ─► frames: [channel id u16][kind u8: request | response | push | close][correlation u32][length u32][payload]
                       max frame 1 MiB, max 64 concurrent requests per connection, idle close after 2 min
```

- **Both sides authenticate** with installation certificates: a terminal connecting to the broker presents its client certificate;
  the broker presents its server certificate. The same `InstallationTrust` runs in `RemoteCertificateValidationCallback` and
  `ClientCertificateRequired = true` (BCL `SslServerAuthenticationOptions`).
- **Channel ids** are registered by name (`"time"`, `"relay"`, `"bundles"`, `"offline.feed"`, `"devices.serial"`); the first frame on a
  channel names it and the receiver checks the channel's sharing scope against the friend relation **before** dispatching.
- **One connection per friend pair**, opened lazily, reused by every channel. A friend that drops is reported to
  `FriendReachability` with YFex.Networking's `Hysteresis` (⚑N23 defaults).
- **Rate limits per friend** (frames/s, bytes/s) and per channel; a peer that sends malformed frames is disconnected and counted.

```csharp
// YFex.Installations — what a channel owner implements
public readonly record struct PeerChannel(string Name);
public enum FriendScope : byte { SameSite = 1, SiteAndVisitors = 2, Tenant = 3 }

public interface IPeerChannelHandler
{
    ValueTask<ReadOnlyMemory<byte>> OnRequestAsync(Friend from, ReadOnlyMemory<byte> request, CancellationToken cancellationToken);
    ValueTask OnPushAsync(Friend from, ReadOnlyMemory<byte> message, CancellationToken cancellationToken);
}
```

### 4.1 TLS helpers for other hosts

Offline's broker may keep Fusion RPC over a WebSocket (offline 04 §3), and Devices remoting has its own RFC 2217 port. Both use the
same identity and validator through:

```csharp
SslServerAuthenticationOptions server = installationTls.CreateServerOptions(PeerChannel channel);   // Kestrel: listen.UseHttps(server-like adapter)
SslClientAuthenticationOptions client = installationTls.CreateClientOptions(InstallationId expectedPeer, PeerChannel channel);
// ClientWebSocketOptions: options.ClientCertificates + RemoteCertificateValidationCallback from the same object
```

**Recommendation to Offline (its decision, Phase 4):** move the broker services onto peer channels (`offline.session`,
`offline.coordination`, `offline.custody`) instead of a second Fusion peer. Reason: one port, one framing, no ASP.NET Core on the
broker, the same code path for terminal-to-terminal mesh traffic. Offline keeps the message contracts and handlers either way.
**Constraint (Offline session, 2026-10-05):** whatever carries custody, the envelope's idempotency key must reach central
**unchanged**. Central deduplicates through the `yfex-idempotency` header and `IdempotentExecution` inside `ValidatingHandlerInvoker`
(offline Phase 0, offline 11 §5). The `relay` and `offline.custody` channels forward the signed envelope bytes as they are, and the
broker's forward to central sends the key from the envelope, never a new one.

## 5. Sharing scopes per channel (⚑O29)

Defaults written by `.Central` into every roster; an admin can narrow (never widen beyond `Tenant`) per site:

| Channel | Owner (meaning) | Default `FriendScope` | Data | Why |
|---|---|---|---|---|
| `time` | Installations + YFex.Time | `Tenant` | `TimeReply` only | no data; any anchored friend helps |
| `relay` | Installations (transport); Logging, Audit, Offline (items) | `Tenant` | **signed opaque items** to central | the friend cannot read or alter them |
| `bundles` | Installations (verify + floors); owners (payloads) | `SameSite` | central-signed bundles for this site | rosters, revocations, rule data, manifest |
| `offline.feed` | Offline | `SameSite` | signed facts (`SharedWithSite`, ⚑O23) | store price override, product blocked |
| `offline.session`, `offline.coordination`, `offline.custody` | Offline | `SameSite`, coordinator only | sessions, slices, claims, receipts | ⚑O27: only the coordinator answers |
| `devices.serial` | Devices | `SameSite` + per-port allow-list | serial bytes | |
| personal data | — | **no channel** | sealed to central with `RecipientSealing`, travels only inside relay items | ⚑O29 "minimal" |

## 6. Discovery (⚑I12, Networking contract in README §5)

Order of candidates for a friend: (1) the address that worked last time; (2) `RosterEntry.LastKnownAddresses`; (3) verified
`yfex.installation` beacons; (4) a typed address. `NetworkFingerprint` (HMAC with a per-tenant key from the roster) only orders (1)–(2)
("we look like Loja Centro's LAN, try PC-GERENTE first"). **Every** candidate still goes through §1.

```csharp
// As built in YFex.Networking Phase 3 (networking 02 §10.2): Networking writes canonical evidence bytes, Installations keys them
var evidence = new NetworkFingerprintEvidence(gatewayMac, ssid, nlmNetworkId, dnsSuffix);   // INeighborTable.FindAsync(gateway),
                                                                                         // IWiFiInformation.ReadAsync() → WiFiConnected.Ssid,
                                                                                         // WindowsNetworkList.Read().Connections[adapter].NetworkId
NetworkFingerprint? here = NetworkFingerprint.Compute(evidence,
    bytes => MessageAuthentication.ComputeTag(fingerprintKey, InstallationDomains.NetworkFingerprint, bytes));   // per-tenant key from the roster
// null when there is no evidence (no gateway, no Wi-Fi, no NLM id): skip ordering, try candidates in roster order
```

The key is derived per tenant (`installations/fingerprint`), so the same home or shop network gives unrelated values for two tenants.
Each installation reports its fingerprint with its addresses to central while online, and the roster carries it next to
`LastKnownAddresses`.

Beacon payload v1 (canonical, inside Networking's `YFXB` frame):

| Field | Size | Notes |
|---|---|---|
| version | 1 | 1 |
| installation id | 16 | |
| role | 1 | |
| site tag | 16 | `MessageAuthentication(site tag key, "yfex.installation.site-tag.v1", scope)` truncated: passers-by cannot learn the tenant or store; friends recompute it |
| peer port | 2 | 0 = not listening |
| offered channels | 4 | bitmask of well-known channels |
| roster version | 8 | a friend with an older roster asks for the newer one on the `bundles` channel |
| time | 27 | `TimeReply` (time §8.3: beacon age bounds the uncertainty) |
| issued at | 8 | trusted ticks; verifier rejects > 30 s old or > 5 s in the future |
| proof | 64 + 16 | evidence-key signature + `KeyId` (Networking carries it as opaque proof) |

`InstallationAnnouncementVerifier` (sync, cached by source + sequence): key id → roster entry → `SignatureVerifier`; unknown key,
bad signature, wrong site tag or stale → `Rejected`.

## 7. Failure and attack table

| Attack / failure | Result |
|---|---|
| laptop on the shop Wi-Fi, no certificate | TLS refused (§1 check 1); beacons ignored (no roster key) |
| employee copies CAIXA-01's `installation.state` to a home PC | cannot sign (keys in CAIXA-01's TPM/KSP): no handshake |
| stolen CAIXA-01, still powered | admin or manager revokes → revocation bundle gossiped on `bundles` in seconds → every friend refuses it |
| terminal of Loja Norte brought to Loja Centro | chain valid, scope differs, not in roster → `OtherSite`: only `Tenant` channels (time, relay); never feed, budgets, sessions |
| rogue DHCP/ARP redirects CAIXA-02 to a fake broker | fake broker has no tenant certificate → refused → Offline falls to Standalone |
| replayed old roster | `OlderThanFloor` |
| central compromise | out of scope: central is the trust root (same as offline 05 §1) |
</content>
</invoke>
