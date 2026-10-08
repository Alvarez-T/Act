# 02 — API and examples (dev side first)

All examples are sketches for approval: names may still change through ⚑N decisions. pt-BR strings are **app** text; the library
returns codes and English descriptions only.

## 0. Real moments in a pharmacy (what the developer writes)

### 0.1 Composition root (checkout terminal, Windows)

```csharp
services.AddYFexNetworking(network => network
    .ProbeInternet(new Uri("https://central.farma.example/health/204"))   // ⚑N14: our endpoint, no Google default
    .UseWindowsConditions()                                               // .Windows: NLM/NCSI, cost, network id (no extra traffic)
    .WatchEndpoint("central", new Uri("https://central.farma.example/health/204"))
    .WatchEndpoint("sefaz-sp", "nfce.fazenda.sp.gov.br", 443)            // TCP connect only, every 60 s while someone listens
    .WatchEndpoint("adquirente", "tef.adquirente.example", 443));

services.AddYFexTime(time => time.UseCentralAnchor().UseNetworkTime("a.ntp.br"));   // resyncs on INetworkConditions.Changed (D5)
```

Nothing else changes: `INetworkConditions` (core) is now in DI, Time/Logging/Offline pick it up when present and keep today's behaviour
when it is not.

### 0.2 The status bar tells the truth

```csharp
public sealed partial class StatusBarViewModel(INetworkConditions network, IServerConnection server) : ViewModel
{
    [Computed] public string ConnectionText => (server.State, network.Current.Internet) switch
    {
        (ServerConnectionState.Connected, _)               => "Conectado",
        (_, InternetReachability.NoNetwork)                => "Sem rede — verifique o cabo ou o Wi-Fi",
        (_, InternetReachability.CaptivePortal)            => "O Wi-Fi pede login (portal) — abra o navegador",
        (_, InternetReachability.LocalNetworkOnly)         => "Sem internet — rede da loja OK",
        (ServerConnectionState.Reconnecting, _)            => "Reconectando ao servidor…",
        _                                                  => "Servidor central fora do ar (internet OK)",
    };
}
```

### 0.3 Don't download the price table over the 4G dongle

```csharp
// a sync job (CMED price table, product images: ~80 MB)
NetworkConditions now = network.Current;
if (now.Cost >= NetworkCost.Metered || now.Medium == NetworkMedium.Cellular)
    return JobOutcome.Deferred("rede móvel/medida — aguardando rede fixa");
```

### 0.4 "The receipt printer stopped" — support screen, one click

```csharp
DiagnosisReport report = await diagnosis.RunAsync(new DiagnosisPlan
{
    Endpoints = [new("central", new Uri("https://central.farma.example/health/204")),
                 new("sefaz-sp", "nfce.fazenda.sp.gov.br", 443, Tls: true)],
    Hosts     = [new("impressora", IPAddress.Parse("192.168.0.50"), Ports: [9100])],
}, cancellationToken);

foreach (DiagnosisStep step in report.Steps)            // gateway ping, DNS, central TLS, printer TCP 9100, ARP → vendor…
    lines.Add($"{(step.Passed ? "OK " : "ERRO")} {texts.For(step.Code)} {step.Detail}");
// e.g. ERRO printer.tcp.refused "192.168.0.50:9100 recusou a conexão; MAC 00:26:AB:… (Seiko Epson) responde ao ARP"
//      → the printer is on, the port is closed: someone changed it to LPD or it is in an error state
```

### 0.5 "The SEFAZ rejects every NFC-e with a TLS error"

```csharp
TlsEndpointReport tls = await TlsEndpointCheck.RunAsync("nfce.fazenda.sp.gov.br", 443, time, cancellationToken);
string hint = tls.Outcome switch
{
    TlsHandshakeSucceeded ok when ok.ClockSkew > TimeSpan.FromMinutes(5) => "O relógio deste computador está errado",
    TlsCertificateRejected { Reason: CertificateProblem.NotYetValid } when tls.DeviceClockSuspect
                                                                        => "Certificado 'ainda não válido': relógio atrasado",
    TlsCertificateRejected { Reason: CertificateProblem.UntrustedRoot } => "Cadeia ICP-Brasil não instalada",
    TlsHandshakeSucceeded                                               => "TLS OK",
    TlsConnectionFailed f                                               => $"Sem conexão: {f.Reason}",
    TlsCertificateRejected r                                            => $"Certificado recusado: {r.Reason}",
};
```

### 0.6 Wake the back-office PC at 7:00, find the PIN pad

```csharp
await WakeOnLan.SendAsync(MacAddress.Parse("00:1A:2B:3C:4D:5E"), cancellationToken);   // magic packet, every up IPv4 adapter

NeighborEntry? entry = await neighbors.FindAsync(IPAddress.Parse("192.168.0.80"), cancellationToken);  // ARP/NDP table; one ping to fill it
MacVendor? vendor = entry is { } e ? MacVendors.Find(e.MacAddress) : null;                            // "Gertec" (YFex.Networking.Vendors)
```

## 1. Conditions (core contract) — ⚑N10, ⚑N14, ⚑N23

```csharp
// YFex core, namespace YFex.Networking — TYPES ONLY
public enum InternetReachability : byte { Unknown = 0, NoNetwork = 1, LocalNetworkOnly = 2, CaptivePortal = 3, Internet = 4 }
public enum NetworkMedium : byte { Unknown = 0, Ethernet = 1, WiFi = 2, Cellular = 3, Other = 4 }        // of the primary route
public enum NetworkCost : byte { Unknown = 0, Unrestricted = 1, Metered = 2, OverDataLimit = 3 }         // ordered: >= Metered = careful
[Flags] public enum NetworkConditionFlags : byte
{
    None = 0, VirtualPrivateNetwork = 1, Proxy = 2, Roaming = 4, ApproachingDataLimit = 8, MultipleRoutes = 16, Degraded = 32,
}
public enum EndpointHealth : byte { Unknown = 0, Healthy = 1, Degraded = 2, Unreachable = 3 }            // also Offline's PeerHealth

public readonly record struct NetworkConditions(InternetReachability Internet, NetworkMedium Medium, NetworkCost Cost,
    NetworkConditionFlags Flags, DateTimeOffset Since)
{
    public static NetworkConditions Unknown { get; } = default;
}

public readonly record struct NetworkConditionsChange(NetworkConditions Previous, NetworkConditions Current);

public interface INetworkConditions
{
    NetworkConditions Current { get; }                 // cached, lock-free read, 0 B
    event Action<NetworkConditionsChange>? Changed;    // after hysteresis; raised on the thread pool, never inline in NetworkChange
}
```

Library (`YFex.Networking`): `NetworkConditionsMonitor : INetworkConditions, IHostedService` combines
- `LocalNetwork` (§2) → `NoNetwork` when no non-loopback adapter is up with a route; `Medium` of the primary route; `MultipleRoutes`;
- `InternetProbe` → `LocalNetworkOnly` / `CaptivePortal` / `Internet`. HTTP probe: 204 (or the configured status/body) = internet;
  3xx or 200 with another body = captive portal; TCP/DNS failures = local only. Runs on start, on every local change, then every
  60 s while `Internet != Internet` and every 10 min otherwise (configurable). **No default third-party URL (⚑N14)**;
- `INetworkConditionsSource` plug-ins (`.Windows` NLM: connectivity, cost, roaming, data limit — read on change and every 60 s, no
  COM event sinks in phase 3; apps on Android supply their own source);
- `Hysteresis` (⚑N23): going *down* needs 3 failures 5 s apart (≈ 15 s) or an adapter-down event; going *up* needs 30 s healthy;
  each failed return doubles the wait (max 10 min); 60 s minimum in a state. A cable unplug is immediate (`NoNetwork`), a lost
  packet is not.

```csharp
public sealed class Hysteresis(HysteresisOptions options, TimeProvider time)   // public: Offline 04 §4 reuses it
{
    public EndpointHealth State { get; }
    public EndpointHealth Report(bool success, EndpointHealth observed = EndpointHealth.Healthy);   // returns the (maybe unchanged) state
}
```

## 2. Local network snapshot and change stream

```csharp
public interface ILocalNetwork
{
    LocalNetworkSnapshot Current { get; }
    event Action<LocalNetworkChange>? Changed;         // debounced 500 ms
    LocalNetworkSnapshot Refresh();                    // synchronous re-read (support screens)
}

public sealed record LocalNetworkSnapshot(ImmutableArray<NetworkAdapter> Adapters, NetworkAdapter? Primary, DateTimeOffset CapturedAt);

public sealed record NetworkAdapter(
    string Id, string Name, string Description, NetworkMedium Medium, NetworkCategory Category, bool IsUp, bool IsVirtual,
    MacAddress? MacAddress, long SpeedBitsPerSecond, int Mtu,
    ImmutableArray<IPNetwork> Addresses,               // address + prefix, IPv4 and IPv6
    ImmutableArray<IPAddress> Gateways, ImmutableArray<IPAddress> DnsServers, IPAddress? DhcpServer, string? DnsSuffix);

public readonly record struct LocalNetworkChange(LocalNetworkSnapshot Previous, LocalNetworkSnapshot Current, LocalNetworkDifferences Differences);
public enum NetworkCategory : byte { Unknown, Public, Private, Domain }   // Windows NLM (.Windows); Unknown elsewhere
[Flags] public enum LocalNetworkDifferences : ushort { None = 0, AdapterAdded = 1, AdapterRemoved = 2, AdapterUpDown = 4,
    AddressesChanged = 8, GatewayChanged = 16, DnsChanged = 32, PrimaryChanged = 64 }
```

`Primary` = the adapter holding the default route with the lowest metric (Windows: `GetBestInterfaceEx` in `.Windows`; elsewhere the
first up adapter with an IPv4 gateway, Linux `/proc/net/route`). `IsVirtual` marks Hyper-V/VMware/VirtualBox/WSL/Docker adapters by
description and `NetworkInterfaceType` (they confuse "which network am I on").

## 3. IP addressing (IP math)

```csharp
public enum IPAddressScope : byte { Unspecified, Loopback, LinkLocal, Private, CarrierGradeNat, UniqueLocal, Multicast, Broadcast,
                                  Documentation, Reserved, Public }

public static class IPAddressing
{
    public static IPAddressScope Classify(IPAddress address);            // 0 B, ≤ 10 ns (benchmark)
    public static bool IsLocal(IPAddress address);                     // loopback, link-local, private, CGNAT, ULA
    public static IPAddress GetBroadcastAddress(IPNetwork network);    // IPv4
    public static UInt128 CountHosts(IPNetwork network);
    public static bool TryGetOnLinkNetwork(IPAddress address, LocalNetworkSnapshot snapshot, out IPNetwork network, out NetworkAdapter adapter);
}
```

Extension members on the BCL `IPNetwork`/`IPAddress` (`address.Scope`, `network.Broadcast`) if C# 14 extension members are
comfortable in this codebase; otherwise the static class above.

## 4. Probes and DNS — ⚑N24, ⚑N26, ⚑N27

```csharp
public record ProbeReached(TimeSpan RoundTrip, IPAddress Address);
public record ProbeRefused(IPAddress Address);                      // TCP RST: host up, port closed
public record ProbeTimedOut(TimeSpan After);
public record ProbeUnreachable(string Reason);                      // no route, ICMP unreachable, adapter down
public record ProbeNameNotResolved(string Name);
public readonly union ProbeOutcome(ProbeReached, ProbeRefused, ProbeTimedOut, ProbeUnreachable, ProbeNameNotResolved);

public sealed class NetworkProbe(NetworkProbeOptions options, TimeProvider time)    // also static NetworkProbe.Default
{
    ValueTask<ProbeOutcome> PingAsync(IPAddress address, CancellationToken ct);               // ICMP echo, 1 s default
    ValueTask<ProbeOutcome> TcpAsync(string host, int port, CancellationToken ct);            // full connect, then close
    ValueTask<HttpProbeOutcome> HttpAsync(Uri uri, CancellationToken ct);                     // status, redirect target, server Date
    ValueTask<ProbeOutcome> ResolveAsync(string name, CancellationToken ct);                  // BCL resolver
    ValueTask<ImmutableArray<ProbeOutcome>> PingManyAsync(ReadOnlySpan<IPAddress> addresses, CancellationToken ct);   // ≤ 32 (⚑N24)
}
```

**Budget (⚑N24):** a process-wide token bucket (10 active probes/s, burst 20) shared by every probe, quality sample, traceroute hop
and SNMP request of the main package; multi-target calls take at most 32 targets, all of them local scope. Discovery listeners and
the ARP table read are passive and not counted. Exceeding the budget **waits**; it never fails silently.

```csharp
public sealed class DnsClient(DnsClientOptions options)            // RFC 1035 over UDP/TCP to the system DNS servers (or given ones)
{
    ValueTask<DnsAnswer<ServiceRecord>> QueryServiceAsync(string name, CancellationToken ct);  // SRV: _yfex._tcp.loja.example
    ValueTask<DnsAnswer<TextRecord>> QueryTextAsync(string name, CancellationToken ct);        // TXT
    ValueTask<DnsAnswer<NameRecord>> QueryPointerAsync(IPAddress address, CancellationToken ct); // PTR (reverse)
    ValueTask<DnsAnswer<AddressRecord>> QueryAddressAsync(string name, AddressFamily family, CancellationToken ct);
}
// opt-in (⚑N26): new DnsClient(DnsClientOptions.OverHttps(new Uri("https://dns.example/dns-query")))   — RFC 8484 wire format
```

`Traceroute.RunAsync(host, maxHops: 30, probesPerHop: 3)` uses `Ping` with increasing TTL (`PingOptions.Ttl`), within the budget.
Results stream as `IAsyncEnumerable<TracerouteHop>` (hop, address, round trips, timed out).

## 5. Endpoint monitor and link quality — ⚑N20

```csharp
public interface IEndpointMonitor
{
    EndpointStatus this[string name] { get; }
    ImmutableArray<EndpointStatus> All { get; }
    event Action<EndpointStatus>? Changed;
}
public readonly record struct EndpointStatus(string Name, EndpointHealth Health, LinkQuality Quality, DateTimeOffset? LastSuccess,
    DateTimeOffset? LastFailure, string? LastError);

public readonly record struct LinkQuality(TimeSpan SmoothedRoundTrip, TimeSpan RoundTripVariation, TimeSpan Jitter,
    double LossRatio, int Samples)
{
    public LinkQualityLevel Level { get; }             // Good / Fair / Poor / Unusable — thresholds in options
}
```

Round trip = RFC 6298 SRTT/RTTVAR, jitter = RFC 3550 inter-arrival estimator, loss over the last 20 samples. The monitor is **lazy**:
it probes only while something reads/subscribes, at most every 60 s per endpoint, and pauses on `NetworkCost >= Metered` unless the
endpoint is marked `essential`. Probes the app already makes can feed it (`monitor.Record("central", success, roundTrip)`), so a
busy terminal adds no traffic.

Resilience (⚑N20) stays decoupled:

```csharp
resilience.Resource("central", r => r.Retry(3).ShouldHandle(_ => network.Current.Internet != InternetReachability.NoNetwork));
```

## 6. Diagnosis report

`NetworkDiagnosis.RunAsync(DiagnosisPlan, ct)` runs a fixed, ordered checklist and stops expanding a branch once its parent fails:
adapter up → address (not `169.254.x.x`: "DHCP did not answer") → gateway ping → gateway MAC in ARP → DNS servers answer →
name resolution of each endpoint → TCP connect → TLS (§7) → HTTP status → proxy route (§10.4) → host ports (≤ 8 per host) →
neighbour MAC + vendor for hosts. Each `DiagnosisStep(Code, Passed, Detail, Duration)` has a stable code (`gateway.ping.timeout`,
`dns.server.refused`, `printer.tcp.refused`, `tls.clock.skew`…) for app texts and support scripts. `report.ToText()` gives a masked
plain-text summary for a ticket (IPs/MACs masked, ⚑N16).

## 7. TLS endpoint check (ties to YFex.Time)

```csharp
public record TlsHandshakeSucceeded(SslProtocols Protocol, TlsCipherSuite CipherSuite, CertificateSummary Leaf,
    ImmutableArray<CertificateSummary> Chain, TimeSpan? ClockSkew);
public record TlsCertificateRejected(CertificateProblem Reason, X509ChainStatusFlags Status, CertificateSummary? Leaf);
public record TlsConnectionFailed(string Reason);
public readonly union TlsOutcome(TlsHandshakeSucceeded, TlsCertificateRejected, TlsConnectionFailed);

public sealed record TlsEndpointReport(string Host, int Port, TlsOutcome Outcome, bool DeviceClockSuspect, int? DaysUntilExpiry);
```

`ClockSkew` = HTTP `Date` of the endpoint vs `time.GetTrustedNow()` when the endpoint speaks HTTPS. `DeviceClockSuspect` is true when
`GetTrustedNow()` is unanchored/rolled back or the certificate error is `NotTimeValid` while the trusted time says it is valid. The
check uses the OS trust store (it **diagnoses**; certificate trust decisions stay in YFex.Cryptography / the app).

## 8. Neighbours and vendors — ⚑N15

```csharp
public interface INeighborTable           // Windows: GetIpNetTable2 (.Windows); Linux: /proc/net/arp + /proc/net/ndisc? (IPv4 first)
{
    ValueTask<ImmutableArray<NeighborEntry>> ReadAsync(CancellationToken ct);                      // passive
    ValueTask<NeighborEntry?> FindAsync(IPAddress address, CancellationToken ct, bool populate = true); // populate = one ping first
}
public readonly record struct NeighborEntry(IPAddress Address, MacAddress MacAddress, NeighborState State, string AdapterId);

public static class MacVendors             // YFex.Networking.Vendors
{
    public static MacVendor? Find(MacAddress address);     // longest match MA-S (36 bits) → MA-M (28) → MA-L (24); 0 B; null for randomized
    public static DateOnly TableDate { get; }
}
public readonly record struct MacVendor(string Name, MacVendorBlock Block);
```

**Snapshot in the repo, never fetched at run time (user, 2026-10-07):** `tools/UpdateMacVendors` (a developer runs it: `download` or
`from <folder>`) turns the IEEE CSVs into `src/YFex.Networking.Vendors/Data/mac-vendors.bin` (Brotli, ~0.47 MB for 54 k blocks), which is
committed and embedded; first use decompresses it into primitive arrays. Refresh before a release or a couple of times a year: a block
newer than `MacVendors.TableDate` just has no vendor. MA-M/MA-S are searched only under the ~440 MA-L prefixes the IEEE subdivided
(14 ns, 0 B). The IEEE download page states no redistribution terms; embedding follows what Wireshark/nmap do (user's call). `MacAddress.IsLocallyAdministered` addresses (phones' randomized MACs)
always return null: no false vendor.

## 9. Wake-on-LAN

`WakeOnLan.SendAsync(MacAddress target, CancellationToken ct, WakeOnLanOptions? options = null)`: magic packet (6 × `FF` + 16 × MAC,
optional SecureOn password) to UDP 9 on the directed broadcast of each up IPv4 adapter (or the given one/address). Three sends 100 ms
apart. Benign: it is what every NAS/router UI does.

## 10. Wi-Fi, fingerprint, multi-homing, proxy, VPN

### 10.1 Wi-Fi (`.Windows`, WLAN API)

```csharp
public sealed partial record WiFiConnected(string Ssid, MacAddress Bssid, int SignalQuality, int? Rssi, int? Channel, string Authentication);   // [Sensitive] location
public record WiFiNotConnected;
public record WiFiPermissionRequired(string How);   // Windows 11 24H2+: SSID/BSSID need "Let desktop apps access your location"
public record WiFiUnsupported(string Platform);
public readonly union WiFiReading(WiFiConnected, WiFiNotConnected, WiFiPermissionRequired, WiFiUnsupported);

WiFiReading wifi = await wifiInfo.ReadAsync(cancellationToken);
```

The library never asks for or changes the location permission; it reports the state so the app can explain it.

### 10.2 Network fingerprint (a hint, never trust)

```csharp
var evidence = new NetworkFingerprintEvidence(gatewayEntry?.MacAddress, wifi is WiFiConnected c ? c.Ssid : null, nlmNetworkId, primary?.DnsSuffix);
NetworkFingerprint? here = NetworkFingerprint.Compute(evidence, bytes => installationKeys.Tag("network-fingerprint", bytes));
// Networking writes canonical evidence bytes ("YFNF" | 1 | parts | MAC | network id | SSID | DNS suffix); the CONSUMER hashes them with
// its key (YFex.Cryptography). Built as designed except that Networking does no HMAC itself (ecosystem crypto rule, phase 3).
```

Offline uses it to guess "we are in branch 12" before any server answers (pick the right site profile, try the right broker
address). It is spoofable (anyone can copy a gateway MAC and SSID), so it only **orders candidates**. HMAC with a consumer key keeps
the fingerprint from identifying a home network across tenants.

### 10.3 Multi-homing (prefer Ethernet over the 4G dongle)

```csharp
var handler = new SocketsHttpHandler
{
    ConnectCallback = InterfaceSelector.CreateConnectCallback(localNetwork, prefer: [NetworkMedium.Ethernet, NetworkMedium.WiFi]),
};
```

Binds the outgoing socket to an address of the preferred up adapter that has a gateway; falls back to the OS choice. Works for
`HttpClient` (Fusion's WebSocket client accepts a `SocketsHttpHandler` through `WebSocketOwnerFactory`).

### 10.4 Proxy and VPN

`ProxyDetection.GetRoute(Uri)` → `ProxyRoute` union (`DirectRoute`, `ProxiedRoute(Uri Proxy, bool RequiresAuthentication?)`) from
`HttpClient.DefaultProxy` (WinHTTP on Windows incl. PAC/WPAD). Authentication is learned by the diagnosis (407). VPN detection is a
heuristic flag (`NetworkConditionFlags.VirtualPrivateNetwork`): the primary route goes through a `Ppp`/`Tunnel` adapter or a known
VPN adapter description (WireGuard, OpenVPN TAP, Fortinet, Cisco AnyConnect, GlobalProtect).

## 11. SNMP client — ⚑N17

```csharp
await using var snmp = new SnmpClient(IPAddress.Parse("192.168.0.60"), SnmpCredentials.Community("public"));   // v2c
await using var snmp3 = new SnmpClient(address, SnmpCredentials.User("suporte", SnmpAuthentication.Sha256(authKey), SnmpPrivacy.Aes128(privKey)));

SnmpResult<SnmpValue> description = await snmp.GetAsync(SnmpObjectIdentifier.Parse("1.3.6.1.2.1.1.1.0"), ct);  // sysDescr
await foreach (SnmpVariable v in snmp.WalkAsync(prefix, maxVariables: 256, ct)) { }       // one host, bounded (GetBulk)
```

`SnmpCredentials` is classified `Secret` (community strings and USM keys are credentials). Walking **many hosts** is survey
([04](04-survey-and-safety.md)). Printer supplies (RFC 3805) and UPS battery (RFC 1628) helpers belong to Devices.Peripherals.

## 12. Telnet — ⚑N5, ⚑N12

```csharp
public interface ITelnetHandler { ValueTask OnDataAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
                                  void OnSubnegotiation(byte option, ReadOnlySpan<byte> payload);
                                  void OnOptionNegotiated(byte option, bool local, bool enabled); }
public sealed class TelnetChannel(Stream stream, ITelnetHandler handler, ReadOnlySpan<byte> localOptions, ReadOnlySpan<byte> remoteOptions)
    : IAsyncDisposable { /* today's API, unchanged: Start, RequestLocal/Remote, QueueSubnegotiation, WriteDataAsync, FlushControlAsync, Completion */ }
public static class TelnetOptions { public const byte Binary = 0, SuppressGoAhead = 3, Echo = 1, TerminalType = 24, WindowSize = 31; }

// line-oriented convenience (old UPS / switch consoles on the LAN)
await using TelnetSession session = await TelnetSession.ConnectAsync("192.168.0.70", 23, ct);
await session.WriteLineAsync("show status", ct);
string? line = await session.ReadLineAsync(ct);
```

Telnet is plaintext: the docs say LAN only, and `TelnetSession` logs a warning when the peer is not local scope.

## 13. Ports and firewall (read only)

```csharp
int port = FreePorts.Find(preferred: 7443, fallback: 7443..7499, ProtocolType.Tcp);   // bind test on the wildcard address; racy by nature
FirewallRuleCheck check = firewall.FindInbound(7443, ProtocolType.Tcp);              // IFirewallRules; .Windows implements it
// union: FirewallRuleFound(name, profiles, enabled, fromPolicy) | FirewallRuleMissing | FirewallDisabled | FirewallUnknown(reason)
```

Reading is through the rule registry keys (local `FirewallPolicy\FirewallRules` + Group Policy `Policies\Microsoft\WindowsFirewall\FirewallRules`):
verified readable as a standard (non-elevated) user on 2026-10-07; no COM needed. **Never** adds or changes rules: the offline installer does that with consent (offline 10 row 26).

## 14. Public IP (opt-in, ⚑N7)

```csharp
network.UsePublicIPAddressLookup(PublicIPAddressSource.Stun("stun.example:3478"), PublicIPAddressSource.Https(new Uri("https://central/ip")));
PublicIPAddressReading reading = await publicIPAddress.ReadAsync(ct);   // not registered → the service does not exist
```

STUN binding request (RFC 8489, no TURN). Used by support ("which branch is behind which ISP") and later by Installations NAT
traversal notes. Never runs unless registered.

## 15. Platform support — ⚑N11, ⚑N21, ⚑N25

```csharp
NetworkingSupport support = NetworkingSupport.Current;
// support.Sockets, .Icmp, .Multicast, .NeighborTable, .WiFi, .Cost, .Quic : FeatureSupport union
public record FeatureAvailable;
public record FeatureNeedsPermission(string What);      // Android multicast lock, Windows location for SSID, Linux ping_group_range
public record FeatureUnsupported(string Platform);      // browser WASM: no sockets at all
public readonly union FeatureSupport(FeatureAvailable, FeatureNeedsPermission, FeatureUnsupported);
```

| Platform | Notes |
|---|---|
| Windows 10/11 | everything; `.Windows` adds NLM (connectivity, cost, category, network id), Wi-Fi (WLAN API), ARP/NDP table, route-table primary adapter, firewall read — verified on this PC 2026-10-07 (NativeAOT included) |
| Linux | `/proc/net/arp`, `/proc/net/route`; ICMP works unprivileged when `ping_group_range` allows, else the BCL falls back to `ping`; no cost source (NetworkManager is D-Bus: app-supplied source) |
| macOS (⚑N29, spike) | main package works (BCL adapters, ICMP is unprivileged, sockets); **macOS 15+ asks the user for "Local Network" access** the first time an app talks to the LAN (multicast, unicast to private addresses) — the app bundle needs `NSLocalNetworkUsageDescription` (and `NSBonjourServices` for mDNS); a planned `YFex.Networking.MacOS` would add the ARP table (`sysctl` route dump), Wi-Fi (CoreWLAN; SSID needs Location permission), cost (`Network.framework` `isExpensive`/`isConstrained`). Not built or tested yet (no Mac here) |
| Android / iOS | multicast needs a platform lock (Android `WifiManager.MulticastLock`) → `IMulticastLock` hook supplied by the app; cost from the app's `INetworkConditionsSource` |
| Browser (WASM) | no sockets/ICMP/multicast: `AddYFexNetworking` registers a conditions monitor driven by an app-supplied source (`navigator.onLine`); everything else reports `FeatureUnsupported` |
| QUIC | reported only (`QuicConnection.IsSupported`) for YFex.Installations |

## 16. Privacy — ⚑N16

- `MacAddress` (core): `[Sensitive(DataSensitivity.Personal, Category = "identification", Mask = "##:##:##:**:**:**")]`, hand-written
  `IClassifiedData<MacAddress>`. The vendor prefix stays visible in masked logs (useful for support, not identifying).
- IP addresses: the library's own log calls write `Masks.TryWriteIpAddress` output (`192.168.10.*`); local-scope gateway addresses
  are written in clear only in the diagnosis report the user explicitly exports. Records holding IPs/MACs/SSIDs (`NetworkAdapter`,
  `NeighborEntry`, `WiFiConnected`, `DiagnosisReport`) are classified as a whole (`Personal`, category `location`) through the
  Privacy generator (analyzer reference).
- SSIDs are personal data (a home network named after a person): never in metrics, masked in logs.
- No telemetry leaves the machine from this library. Probes go only to endpoints the app configured (⚑N14).

## 17. Metrics (`Meter "YFex.Networking"`)

`yfex.networking.conditions.internet` (observable gauge, enum value), `.conditions.changes` (counter), `.probe.duration` (histogram,
tags: kind, outcome — **no addresses**), `.probe.budget.waits` (counter), `.endpoint.health` (gauge per configured endpoint name),
`.endpoint.round_trip` (histogram), `.discovery.announcements.received/rejected` (counters, tag: kind).

## 18. Testing (`YFex.Networking.Testing`)

```csharp
var conditions = new FakeNetworkConditions(InternetReachability.Internet);
conditions.Set(InternetReachability.CaptivePortal);                 // raises Changed synchronously (deterministic tests)

var lan = new InMemoryNetwork(time);                               // one shared LAN for the offline multi-installation harness
InMemoryNode broker = lan.Join("PC-GERENTE", IPAddress.Parse("192.168.0.10"));
InMemoryNode caixa1 = lan.Join("CAIXA-01",   IPAddress.Parse("192.168.0.21"), category: NetworkCategory.Public);   // per-host adapter category
services.AddYFexNetworking(n => n.UseInMemory(caixa1));            // beacons/mDNS/SSDP/probes/conditions go through the fake LAN
lan.Partition(broker, caixa1);                                     // multicast and probes between them are dropped
lan.Impair(caixa1, latency: TimeSpan.FromMilliseconds(300), jitter: TimeSpan.FromMilliseconds(50), loss: 0.05);
caixa1.Conditions.Set(InternetReachability.LocalNetworkOnly);      // per-node machine conditions

Stream chaotic = new ChaosStream(inner, lan.ImpairmentFor(caixa1));  // latency/loss/partition for any byte stream (RPC tests)
```

The library talks to sockets through two small public seams (namespace `YFex.Networking.Transports`: `IMulticastTransport`,
`IProbeTransport`); `InMemoryNetwork` implements them, so the same discovery and probe code runs in tests. `FakeNeighborTable` and `FakeLocalNetwork` (snapshots by hand) complete it.
