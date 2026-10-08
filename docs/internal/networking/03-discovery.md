# 03 — Discovery: beacons, mDNS/DNS-SD, SSDP, WS-Discovery

## 1. Rule: discovery returns candidates, never trust (⚑N6)

Every discovery API returns **candidates**: "something at `192.168.0.10:7443` claims to be X". Whether to talk to it, and as whom, is
the consumer's decision (YFex.Installations: certificate check, tenancy, enrollment; Devices: allow-lists). Networking offers one hook
so consumers can drop forged announcements early, and it carries opaque proof bytes without understanding them:

```csharp
public interface IAnnouncementSigner   { void Sign(ReadOnlySpan<byte> signedPart, IBufferWriter<byte> proof); }
public interface IAnnouncementVerifier { AnnouncementVerdict Verify(in AnnouncementToVerify announcement); }   // sync, cheap; cache keys
public enum AnnouncementVerdict : byte { Verified, Rejected, NotChecked }

public readonly ref struct AnnouncementToVerify     // what the verifier sees (fields agreed with the Installations session, 2026-10-05)
{
    public string Kind { get; }
    public IPEndPoint Source { get; }                 // sender address and port of the datagram
    public ushort ServicePort { get; }
    public ulong Sequence { get; }
    public ReadOnlySpan<byte> Payload { get; }
    public ReadOnlySpan<byte> Proof { get; }
    public ReadOnlySpan<byte> SignedPart { get; }     // exactly the bytes the proof covers ([0, end of payload) for beacons)
    public string AdapterId { get; }
    public NetworkCategory AdapterCategory { get; }   // Private / Domain / Public / Unknown (Windows NLM; Unknown elsewhere)
    public DateTimeOffset ReceivedAt { get; }
}
```

Replay protection is the consumer's job (Installations puts an issued-at trusted instant inside the signed payload and rejects old
ones); the sequence number is only a duplicate filter.

Networking never references YFex.Cryptography or Installations. A lying beacon costs one failed handshake (offline 04 §2), and with a
verifier it costs nothing: it is dropped and counted (`discovery.announcements.rejected`).

## 2. Beacons (generic multicast announcements) — ⚑N19

The offline site broker's beacon (offline 04 §2, ⚑O6) becomes one **kind** of generic beacon. Kind names and payload formats belong to
the consumer. **Proposed by the YFex.Installations session (2026-10-05):** one kind, `yfex.installation`, that every enrolled
installation may announce (the broker always; terminals when the site mesh is on, or in Standalone for time-peer comparison), with the
role inside the signed payload (Installations' canonical format v1: installation id, role, site tag = HMAC(site key, site scope),
peer-endpoint port, offered channels, `TimeReply`, manifest version, issued-at trusted instant). Other possible kinds: Devices
remoting agents (if they ever choose beacons over mDNS), future relays.

### 2.1 Dev side

```csharp
// any enrolled installation (Installations composes this and supplies the signer/verifier)
services.AddYFexNetworking(network => network.Beacons(beacons => beacons
    .Announce("yfex.installation", every: TimeSpan.FromSeconds(5), port: 7443,
              payload: (sp, writer) => sp.GetRequiredService<InstallationBeacon>().Write(writer),   // role, site tag, TimeReply, …
              signer: sp => sp.GetRequiredService<InstallationAnnouncementSigner>())));

// terminal looking for its broker (only when the manifest addresses fail — offline 04 §2 step 2)
await foreach (BeaconCandidate candidate in beacons.ListenAsync("yfex.installation", verifier, cancellationToken))
{
    if (candidate.Verification != AnnouncementVerdict.Verified) continue;
    if (!InstallationBeacon.TryRead(candidate.Payload.Span, out var beacon) || beacon.Role != InstallationRole.SiteBroker) continue;
    if (await brokerConnector.TryConnectAsync(candidate.ServiceEndPoint, cancellationToken)) break;   // mTLS decides (Installations)
}

// on demand: ask "who is there?" and collect answers for 2 s
ImmutableArray<BeaconCandidate> found = await beacons.QueryAsync("yfex.installation", TimeSpan.FromSeconds(2), verifier, cancellationToken);
```

```csharp
public sealed record BeaconCandidate(string Kind, IPEndPoint Source, IPEndPoint ServiceEndPoint, ulong Sequence,
    ReadOnlyMemory<byte> Payload, AnnouncementVerdict Verification, string AdapterId, DateTimeOffset ReceivedAt, bool Goodbye);
```

### 2.2 Wire format v1 (≤ 512 bytes, one unfragmented datagram)

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `YFXB` |
| 4 | 1 | version = 1 |
| 5 | 1 | flags: bit 0 query, bit 1 goodbye (sender leaving), bits 2–7 reserved = 0 |
| 6 | 1 | kind length *k* (1–64) |
| 7 | *k* | kind, ASCII `[a-z0-9.-]` |
| … | 2 | service port (big-endian; 0 = none) |
| … | 8 | sequence (sender-monotonic; receivers drop repeats/older per source, a replay hint, not security) |
| … | 2 | payload length *p* |
| … | *p* | payload (opaque, the consumer's format) |
| … | 2 | proof length *s* |
| … | *s* | proof over bytes `[0, end of payload)` (opaque; the signer's format) |

Golden tests pin the bytes. Offline's 120-byte layout maps onto it: `installation id | site hash | trusted time` → payload,
`evidence signature` → proof.

### 2.3 Defaults and abuse limits

| Rule | Default |
|---|---|
| Group / port | `239.255.70.1` (IPv4 organization-local scope) and `ff05::7966` (site-local), UDP 47001; configurable per app |
| Multicast TTL / hop limit | 1 (never leaves the subnet) |
| Interval | ≥ 1 s, ±20 % jitter; a goodbye datagram on shutdown |
| Adapters | up, physical (not virtual), with an on-link address; on Windows only networks of category **Private/Domain** (never Public Wi-Fi) via `.Windows` NLM. Per-kind opt-in `AllowPublicNetworkCategory()` (Installations' request: many shop PCs classify the shop LAN as Public), **accepted only for a kind that has a verifier** (configuration error otherwise), so an unverified datagram is still dropped; off by default |
| Inbound | datagrams > 512 bytes dropped; ≤ 5 datagrams/s per source (excess dropped and counted); unknown magic/version dropped silently |
| Queries | answered only when the querier's address is on-link for the receiving adapter, at most once per second per querier, reply ≤ 512 bytes (no amplification) |
| Verification | the verifier runs before the candidate is yielded; verdicts cached by (source, sequence) |

## 3. mDNS / DNS-SD (RFC 6762 / 6763) — ⚑N18

Use: Devices remoting agents advertise themselves; terminals find network printers that speak Bonjour (`_pdl-datastream._tcp` = raw
9100, `_ipp._tcp`, `_printer._tcp`), and any `_http._tcp` admin pages.

```csharp
await using ServiceRegistration registration = await mdns.AdvertiseAsync(new ServiceAdvertisement(
    ServiceType: "_yfex-device._tcp", InstanceName: "Balcão 2 — COM3", Port: 2217,
    Text: [("v", "1"), ("kind", "serial")]), cancellationToken);          // probing + conflict rename ("Balcão 2 — COM3 (2)")

await foreach (ServiceInstance printer in mdns.BrowseAsync("_pdl-datastream._tcp", cancellationToken))
    printers.Add(printer);   // InstanceName, HostName, Port, Addresses, Text, Ttl; removal = Goodbye flag
```

Implementation: managed responder/querier on UDP 5353 with `ReuseAddress` (works next to Windows' own mDNS responder; Linux Avahi
too), the shared DNS wire codec from `DnsClient`, probing/announcing, known-answer suppression, continuous querying with exponential
back-off (1 s → 1 h), goodbye packets (TTL 0), QU bit for one-shot lookups, on-link source check (RFC 6762 §11). A `dnsapi`
(`DnsServiceRegister`/`DnsServiceBrowse`) backend in `.Windows` only if a real conflict shows up (⚑N18).

## 4. SSDP (UPnP discovery)

```csharp
await foreach (SsdpResponse r in ssdp.SearchAsync("ssdp:all", TimeSpan.FromSeconds(3), cancellationToken))
    // r.SearchTarget, r.UniqueServiceName, r.Location, r.Server; r.Source
SsdpDeviceDescription? d = await ssdp.GetDescriptionAsync(r, cancellationToken);   // friendlyName, manufacturer, modelName, serialNumber
```

`M-SEARCH` to `239.255.255.250:1900` with `MX` ≤ 5, sent twice. **Search only** (no `NOTIFY` advertising; nothing in the ecosystem
needs it). Description fetch only when `LOCATION` is a local-scope address on an up adapter (a response cannot make us fetch an
internet URL), 64 KB limit, `XmlReader` with DTD processing off.

## 5. WS-Discovery (phase 5, optional)

SOAP-over-UDP `Probe` to `239.255.255.250:3702` (what Windows does for WSD printers and ONVIF cameras); parse `ProbeMatches`
(`Types`, `XAddrs`, `Scopes`). Search only.

## 6. How consumers use it

| Consumer | Uses | Stays in the consumer |
|---|---|---|
| YFex.Offline / Installations | beacon kind `yfex.installation` (role in the payload) + signer/verifier (evidence key, public key from the signed site manifest); `NetworkFingerprint` (per-tenant key from the manifest) to order candidates; `InMemoryNetwork` for the multi-installation harness | kind/payload format, broker selection, certificate pinning, tenancy, mesh; Installations does not use mDNS |
| YFex.Devices Remoting (§12) | mDNS `_yfex-device._tcp` advertise/browse | allow-lists, TLS/PSK → Installations identity |
| YFex.Devices printers | mDNS `_pdl-datastream._tcp`/`_ipp._tcp`, SSDP/WSD descriptions, `NeighborTable` + `MacVendors`, TCP 9100 probe of ≤ 32 **listed** candidates | ESC/POS status (DLE EOT), printer model catalog |
| YFex.Devices Modbus TCP | TCP 502 probe of listed hosts | Modbus identification (FC 43/14) |
| YFex.Time site peers | beacons carry `TimeReply` bytes in the payload | comparison as evidence only (Time §8.3) |

A sweep of the whole subnet for 9100/502 is **not** here: it is the survey package ([04](04-survey-and-safety.md)).
