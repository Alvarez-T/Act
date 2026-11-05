# 06 — Phases, acceptance, implementation log

Each phase ships on its own. Every phase ends with: build with `--artifacts-path C:/Temp/networking-artifacts`, all touched test
projects green (counts recorded below), AotSmoke publish with 0 IL/AOT warnings, the memory file `yfex-networking.md` updated, and
`YFex/CLAUDE.md` (solution map + common tasks) updated when code lands.

## Repo layout (template: YFex.Time / YFex.Cryptography)

```
YFex.Networking/
  Directory.Build.props        net11.0, LangVersion preview, Nullable, ImplicitUsings, TreatWarningsAsErrors, NuGetAudit false,
                               YFexRoot, MicrosoftExtensionsVersion 11.0.0-preview.5.26302.115, IsAotCompatible on src/*
  networking.slnx              /src/ /tests/ /benchmarks/ /tools/
  README.md
  src/YFex.Networking/                 src/YFex.Networking.Windows/       src/YFex.Networking.Vendors/
  src/YFex.Networking.Testing/         src/YFex.Networking.Survey/ (phase 7)   src/YFex.Networking.Windows.Survey/ (phase 7)
  tests/YFex.Networking.Tests/         tests/YFex.Networking.Windows.Tests/   tests/YFex.Networking.AotSmoke/
  benchmarks/YFex.Networking.Benchmarks/   (BenchmarkDotNet 0.15.8, InProcessEmitToolchain on net11)
  tools/UpdateMacVendors/              (dev-only, downloads the IEEE CSVs when a developer runs it)
```

Tests that need a real network are `[Trait("Category", "Network")]` (skipped in CI by default); everything else runs against
`InMemoryNetwork`, loopback sockets or golden bytes.

## Phase 0 — core types

- `YFex/src/YFex/Networking/`: `MacAddress` (+ `MacAddressJsonConverter`, hand-written `IClassifiedData<MacAddress>`),
  `INetworkConditions`, `NetworkConditions`, `NetworkConditionsChange`, `InternetReachability`, `NetworkMedium`, `NetworkCost`,
  `NetworkConditionFlags`, `EndpointHealth`.
- Devices switches to core `MacAddress` (§2 of [05](05-consumers-and-migration.md)).
- **Acceptance:** YFex.Tests + new MacAddress tests (every parse form, formats C/D/N and lower case, UTF-8, bytes round trip,
  `PhysicalAddress` round trip, flags, JSON, mask `00:1A:2B:**:**:**`, classification); Devices tests and AotSmoke unchanged;
  parse/format 0 B.

## Phase 1 — `IServerConnection` (⚑N4)

- After `SendMessage` to the authorization session. Rename + `[Obsolete]` bridges; D1–D4 fixed (§3 of [05](05-consumers-and-migration.md)).
- **Acceptance:** Messaging.Tests, Messaging.Rpc.Tests, Rules.Admin.Tests, EventSourcing.Tests counts unchanged + tests for the real
  previous state, no spin on repeated `WhenNext` errors, the obsolete adapter.

## Phase 2 — repo + core library

- Scaffold; `LocalNetwork` (snapshot, primary adapter, debounced changes, Linux `/proc/net/route`); `IPAddressing`; `NetworkProbe`
  (ping, TCP, DNS via BCL, HTTP) with the budget (⚑N24); `Hysteresis`; `InternetProbe` + `NetworkConditionsMonitor`; `LinkQuality`;
  `EndpointMonitor`; `NetworkingSupport`; meter; `AddYFexNetworking`; `.Testing` (`FakeNetworkConditions`, `FakeLocalNetwork`).
- YFex.Time adopts `INetworkConditions` + `NetworkAddressChanged` (D5).
- **Acceptance:** hysteresis table (3 failures/30 s up/doubling/60 s dwell) with `FakeTimeProvider`; captive portal (302, 200 + HTML),
  204, TCP-only and DNS-failure cases against a loopback `HttpListener`; budget waits (11th probe in a second waits); multi-target
  rejects a public address; snapshot diff flags; `Classify` table for every scope incl. IPv4-mapped IPv6; Time resync on medium
  change; AotSmoke; benchmarks: `Current` read ≤ 5 ns 0 B, `Classify` ≤ 10 ns 0 B.

## Phase 3 — Windows, Linux neighbours, vendors, small tools

- `.Windows`: NLM connectivity/cost/roaming/network id/category (source-generated COM), `GetBestInterfaceEx`, `GetIpNetTable2`,
  WLAN (`WlanOpenHandle`/`WlanQueryInterface`, 24H2 permission → `WiFiPermissionRequired`), firewall-rule presence read (decide COM
  vs registry by testing as a standard user).
- Linux `/proc/net/arp` neighbour table; `.Vendors` + `tools/UpdateMacVendors` (⚑N15, licence check first); `WakeOnLan`;
  `NetworkFingerprint`; `InterfaceSelector`; `ProxyDetection`; VPN flag.
- **Acceptance:** golden magic packet; vendor lookup MA-L/MA-M/MA-S longest match, randomized MAC → null, ≤ 50 ns 0 B; `/proc` parsers
  on fixtures; Windows tests on this PC (NLM reports internet + cost, ARP table non-empty, Wi-Fi reading is one of the four cases);
  fingerprint stable across restarts, different keys → different values.

## Phase 4 — DNS, traceroute, TLS, diagnosis, public IP

- RFC 1035 codec + `DnsClient` (A/AAAA/SRV/TXT/PTR, UDP with TCP fallback on TC, name compression, loop and length limits), DoH opt-in
  (⚑N26); `Traceroute`; `TlsEndpointCheck` with YFex.Time; `NetworkDiagnosis` + `DiagnosisReport.ToText()` (masked); STUN/HTTPS
  public address (⚑N7, opt-in).
- **Acceptance:** golden DNS packets incl. compression pointers and malicious inputs (pointer loops, oversize labels) never throw
  outside `TryParse`; TLS check against a loopback `SslStream` with expired / not-yet-valid / untrusted certificates and a
  `FakeDeviceClock` set back (→ `DeviceClockSuspect`); diagnosis order and short-circuit; STUN golden binding response.

## Phase 5 — discovery

- Beacons (wire v1, signer/verifier hook, defaults and abuse limits of [03](03-discovery.md) §2.3); mDNS/DNS-SD advertise + browse;
  SSDP search + description; WS-Discovery probe; `InMemoryNetwork` (partitions, impairments, per-node conditions) + `ChaosStream`.
- **Acceptance:** beacon golden bytes; forged beacon rejected and counted; per-source rate limit; off-link query ignored; replayed
  sequence dropped; mDNS probing conflict rename, goodbye, known-answer suppression (golden packets); SSDP description refused for a
  non-local `LOCATION`; two in-memory nodes discover each other, partition stops it, healing restores it; real-LAN test (trait
  Network) finds this PC's own advertisement.

## Phase 6 — Telnet move + SNMP (DONE 2026-10-14, log below)

- Telnet moved (⚑N5/⚑N12); Devices `Rfc2217*` on it; `SnmpClient` v2c + v3 (USM HMAC-SHA-2, AES-128-CFB), BER codec, bounded walk
  of one host.
- **Acceptance:** Devices RFC 2217 tests unchanged; Telnet golden tests moved; SNMP golden PDUs (RFC 3416), v3 test vectors
  (RFC 7860 key localization), malformed BER never throws outside `TryParse`; a walk stops at `maxVariables`.

## Phase 7 — survey package

- `.Survey` + `.Windows.Survey` with every guardrail of [04](04-survey-and-safety.md) §3.1.
- **Acceptance:** build fails without `<YFexNetworkSurvey>true</YFexNetworkSurvey>`; a run without authorization/sink, with an expired
  authorization, a public range or a /21 is refused; pacing never exceeds the configured rate (measured with `FakeTimeProvider`);
  audit records written for start and finish; in-memory site survey finds the fake printer, router and PCs.

## Implementation log

### Phase 0 — core types — 2026-10-07

- `YFex/src/YFex/Networking/MacAddress.cs`: hand-written; same surface as the Devices-generated struct, plus formats C/D/N (lower
  case = lower-case hex), Cisco `001a.2b3c.4d5e` parsing, UTF-8, `Broadcast`/`IsBroadcast`, `PhysicalAddress` interop,
  `MacAddressJsonConverter` (string, also as a dictionary key), hand-written `IClassifiedData<MacAddress>` (Personal /
  identification, mask `00:1A:2B:**:**:**`). `Oui` → `VendorPrefix`.
- `YFex/src/YFex/Networking/NetworkConditions.cs`: `InternetReachability`, `NetworkMedium`, `NetworkCost`, `NetworkConditionFlags`,
  `EndpointHealth`, `NetworkConditions` (+ `PrefersSmallTransfers`), `NetworkConditionsChange`, `INetworkConditions`.
- Devices: `MacAddress.cs` deleted; tests (`<Using Include="YFex.Networking" />`), AotSmoke and benchmarks use the core type;
  README note. Backups in `C:\Temp\networking-backup\YFex.Devices`.
- **Counts:** YFex.Tests 208 (all green; new `MacAddressTests` + `NetworkConditionsTests`, incl. 0-byte parse/format).
  Devices.Tests 155/157: the two failures are environmental and unrelated: `ArchitectureTests` looks for `devices.slnx` above the
  output directory (not found with `--artifacts-path`), `Reserved_ports_are_readable_without_elevation` depends on this PC's COM
  arbiter (COM7/COM8 present but not reserved). Devices AotSmoke: publish 0 IL warnings, "MacAddress flags" PASS; the run then hangs
  in the pre-existing named-pipe `OverlappedTransport` check (not touched here).
- **Deviation:** `PhysicalAddress` has no span overload in .NET 11 preview 4, so `TryFromPhysicalAddress` allocates (interop path only).

### Phase 1 — `IServerConnection` (⚑N4) — 2026-10-07

- Coordination: the "Authorization consolidation phase 3" session was not running; the sessions that own the touched code agreed:
  "TimeProvider integration for Authorization/Offline" (go-ahead; CentralTimeSource notes) and "Design offline + local-network store
  server mode" (no clash; asked for one connection per peer and for offline Phase 0 behaviour to survive).
- New `ServerConnection.cs`: `ServerConnectionState { Disconnected, Reconnecting, Connected }`, `ServerConnectionChange(Previous,
  Current)`, `IServerConnection` (`IsConnected` default member), `AlwaysConnectedServer`, `ServerConnectionChangedEvent`.
- `FusionServerConnection` (public; replaces internal `FusionNetworkStatus`): `(RpcHub)` = default peer, `(RpcHub, RpcPeerRef)` =
  any other peer (site broker); real previous state (D1); a repeating `WhenNext` error waits 1 s instead of spinning, disposal stops
  it (D4).
- `INetworkStatus.cs` keeps the old names `[Obsolete]` for one release: `INetworkStatus`, `SyncState`, `AlwaysConnectedNetworkStatus`,
  `NetworkStatusProvider` (D3: never read), `NetworkStatusChangedEvent` (still published, now with the real previous state), and
  `NetworkStatusCompatibility.AsServerConnection()/AsNetworkStatus()` bridges that unwrap each other. DI registers `IServerConnection`
  plus an obsolete `INetworkStatus` view.
- Consumers switched: `LocalDispatcher`, `FusionMessageBus`(+`Builder.Build`), `OutboxReplayer`, `CentralTimeSource` (+`UseCentralAnchor`),
  `RpcMessagingExtensions` (client + server), Rules.Admin `RuleSetSync`; each public constructor taking `INetworkStatus` stays as an
  `[Obsolete]` overload. D2: `Syncing` (never produced) is not in the new enum.
- Fakes: Messaging.Tests `ManualServerConnection` (replaces `ManualNetworkStatus`, adds `StartReconnecting`), Rpc.Tests ×3,
  Rules.Admin.Tests, EventSourcing.Tests, EventSourcing AotSmoke. New `ServerConnectionTests` (5): real previous state through the
  replayer (+ the obsolete event), bridges both ways, `Syncing` → Connected, obsolete constructor.
- Docs: public/messaging.md, internal/messaging-internals.md, YFex.Messaging/docs/03, offline 04 §1, time README §10.
- **Counts:** Messaging.Tests 178 + 2 skipped, Messaging.Rpc.Tests 69, Rules.Admin.Tests 27 — all green, no CS0618 outside the
  intended places. EventSourcing.Tests does not compile for a pre-existing reason (`YFex.Quantity` vs `YFex.EventSourcing.Sample.Quantity`
  ambiguity, unrelated); the edited `ModelASyncTests` has no errors; EventSourcing AotSmoke builds.

### Phase 1 follow-up — obsolete names removed — 2026-10-07

- The user asked to drop the `[Obsolete]` bridges: no code anywhere in `C:\Dev\YFex` (any repo) used them. Removed `INetworkStatus.cs`
  (`INetworkStatus`, `SyncState`, `AlwaysConnectedNetworkStatus`, `NetworkStatusProvider`, `NetworkStatusChangedEvent`,
  `NetworkStatusCompatibility`), the obsolete constructor overloads (`LocalDispatcher`, `OutboxReplayer`, `CentralTimeSource`,
  `RuleSetSync`, `FusionMessageBusBuilder.Build`), the obsolete DI view and the double publish of the old event.
- Tests: the three bridge tests went away; Messaging.Tests 175 + 2 skipped, Messaging.Rpc.Tests 69, Rules.Admin.Tests 27,
  YFex.Time.Tests 62 — all green; EventSourcing AotSmoke builds. Backups of the bridge version: `C:\Temp\networking-backup\after-phase1`.

### Phase 2 — repo + core library — 2026-10-07

- New repo `C:\Dev\YFex\YFex.Networking` (layout of YFex.Time): `Directory.Build.props` (TreatWarningsAsErrors, `YFexRoot`,
  `YFexPrivacyRoot`), `networking.slnx`, README; projects `YFex.Networking`, `.Testing`, `.Tests`, `.AotSmoke`, `.Benchmarks`.
- `YFex.Networking`: `IPAddressing` + `IPAddressScope` + `AdapterIPAddress` (⚑N28 naming); `LocalNetwork`/`ILocalNetwork`
  (`NetworkAdapter` classified Personal/location through the Privacy generator, `LocalNetworkSnapshot`, `LocalNetworkDifferences`,
  500 ms debounce, primary = OS hint or Ethernet > Wi-Fi > cellular, virtual switches ignored); `Transports` seams
  (`ILocalNetworkReader` + `SystemLocalNetworkReader` + `LinuxRouteTable`, `IProbeTransport` + `SystemProbeTransport`);
  `ProbeOutcome`/`HttpProbeOutcome` unions; `ProbeBudget` (10/s, burst 20, waits, ⚑N24); `NetworkProbe` (≤ 32 local targets);
  `ProbeTarget` union + `InternetProbe` (captive portal: 3xx, 511, 200-instead-of-204, 204 with body = definitive; TLS interception =
  captive, not definitive); `Hysteresis` (⚑N23); `NetworkConditionsMonitor : INetworkConditions, IHostedService` (no connected
  physical adapter → `NoNetwork` at once; sources combined; 5 s re-checks while settling; lock-free `Current`); `LinkQualityEstimator`
  (RFC 6298 SRTT/RTTVAR, RFC 3550 jitter, 20-sample loss); `EndpointMonitor` (lazy `Watch()`, `Record()` for passive samples, pauses
  non-essential endpoints on metered links); `NetworkingSupport` (⚑N21); `NetworkingMetrics` (meter `YFex.Networking`);
  `AddYFexNetworking` + `NetworkingBuilder`.
- `.Testing`: `FakeNetworkConditions`, `FakeLocalNetworkReader` (Ethernet/WiFi/Cellular/Virtual helpers), `ScriptedProbeTransport`.
- YFex.Time (D5): `TimeSynchronizationService` takes optional core `INetworkConditions` (resync when the internet returns or the medium
  changes); without it, also `NetworkAddressChanged`. Time does not reference this repo.
- **Counts:** YFex.Networking.Tests 80; YFex.Time.Tests 56 → 62. AotSmoke: publish 0 IL warnings, `ALL PASSED` on this PC
  (38 adapters, primary Wi-Fi; real loopback ping/TCP/HTTP 204 → Internet, 302 → CaptivePortal). Benchmarks (ShortRun, in-process):
  `Current` < 1 ns, `Classify` IPv4 1.1 ns / IPv6 6.9 ns, `MacAddress` parse 11 ns / format 7.5 ns, `Hysteresis.Report` 29 ns — all 0 B.
- **Deviations from 02:** `NetworkAdapter.Addresses` is `ImmutableArray<AdapterIPAddress>` (address + prefix; the BCL `IPNetwork`
  keeps only the network part); the probe-target union is `ProbeTarget` (shared by the internet check and the endpoint monitor);
  `Hysteresis` exposes `Report(EndpointHealth)` + `ReportDefinitive(EndpointHealth)`; `FeatureUnsupported(Reason)`.
- **Gotchas:** a `switch` over a native `union` needs a `null =>` arm (a default union holds no case); FluentAssertions
  `BeOfType<T>()` sees the union struct, so tests unwrap with `.Value` (`UnionAssertions.Case()`); MSBuild XML comments cannot contain
  `--`; `HttpListener` needs a URL ACL, so socket tests use a raw loopback TCP server.

### Phase 3 — Windows, vendors, neighbours, small tools — 2026-10-07

- **`.Vendors`** (⚑N15, user: snapshot in the repo): `MacVendors.Find(MacAddress)` (longest match MA-S → MA-M → MA-L; randomized and
  multicast → null; MA-L blocks of the "IEEE Registration Authority" dropped), `MacAddressBlockSize`, `MacVendor`; table format v1
  (`MacVendorTable`, Brotli resource `Data/mac-vendors.bin`, 466 838 bytes, snapshot 2026-10-07: 40 317 MA-L + 6 625 MA-M + 7 220
  MA-S); `MacVendorCsv` + `MacVendorTableWriter` (internal); `tools/UpdateMacVendors` (`download` | `from <folder>`, `date`, `out`).
  IEEE CSVs downloaded once with the user's approval (`C:\Temp\networking-ieee`, 3.9 + 0.76 + 0.67 MB).
- **Main package:** `INeighborTable` + `NeighborTable` base (find = read, ping once if missing, local addresses only) +
  `LinuxNeighborTable` (`/proc/net/arp`, ⚑N11) + `UnsupportedNeighborTable`, `NeighborEntry` (classified); `WakeOnLan` (magic packet,
  SecureOn, directed broadcast per adapter); `ProxyDetection` + `ProxyRoute` union; `VirtualPrivateNetworkDetection`; `InterfaceSelector`
  (preferred-medium source binding as a `SocketsHttpHandler.ConnectCallback`); `IWiFiInformation` + `WiFiReading` union (`WiFiConnected`
  classified); `NetworkFingerprintEvidence` (canonical bytes) + `NetworkFingerprint.Compute(evidence, hasher)`; `IFirewallRules` +
  `FirewallRuleCheck` union; `FreePorts`. The monitor now sets `VirtualPrivateNetwork` and `Proxy` (proxy looked up once per adapter
  snapshot, off-thread, 2 s cap; `NetworkConditionsOptions.ProxyRoutes` for tests). DI registers neighbour table / Wi-Fi / firewall
  defaults (Linux table on Linux, "unsupported" elsewhere).
- **`.Windows`:** source-generated COM for the Network List Manager (`INetworkListManager`, `IEnumNetworkConnections`, `INetworkConnection`,
  `INetwork`, `INetworkCostManager`; IDispatch slots as placeholders) → `WindowsNetworkList.Read()` (connectivity, cost + roaming/limit
  flags, per-adapter category and network id), `WindowsConditionsSource`; `WindowsLocalNetworkReader` (categories + primary via
  `GetBestInterfaceEx`); `WindowsNeighborTable` (`GetIpNetTable2`, IPv4+IPv6, LUID → `{GUID}` adapter id); `WindowsWiFiInformation`
  (WLAN API: SSID, BSSID, quality, RSSI, channel, authentication; `ERROR_ACCESS_DENIED` or a blank SSID → `WiFiPermissionRequired`);
  `WindowsFirewallRules` (registry, local + Group Policy, profiles, all-profiles-off → `FirewallDisabled`); `UseWindowsConditions()`
  (does nothing off Windows). `[assembly: DisableRuntimeMarshalling]`, blittable `LibraryImport`s.
- **Verified on this PC (standard user, not elevated):** NLM = Internet / Unrestricted, 1 connection, category **Public** (a home LAN
  classified Public — the Installations session's concern is real); primary = Wi-Fi via the route table; Wi-Fi quality 83, RSSI −49 dBm,
  channel 124, WPA2-Personal (SSID readable: location access granted here); ARP table 33 entries, gateway vendor found in the snapshot;
  firewall rules readable without elevation; the monitor reports Internet from NLM alone (⚑N14: no probe of ours).
- **Counts:** YFex.Networking.Tests 80 → 105; new YFex.Networking.Windows.Tests 19 (golden layouts for `MIB_IPNET_ROW2`,
  `WLAN_CONNECTION_ATTRIBUTES`, firewall rule strings, NLM flag mapping + 6 real-machine checks). AotSmoke: publish 0 IL warnings,
  `ALL PASSED` incl. generated COM, WLAN, iphlpapi, registry, Brotli resource. Benchmarks: `MacVendors.Find` 14 ns (unknown block 28 ns),
  0 B — after decoding the records into primitive arrays and searching MA-M/MA-S only under subdivided prefixes (first version: 133 ns).
- **Deviations from 02:** `WiFiConnected.Channel` instead of a frequency (6 GHz channel numbers overlap); the fingerprint is hashed by
  the consumer (Networking does no HMAC: the ecosystem's crypto rule); firewall read through the registry, not COM; `FirewallDisabled`
  instead of `FirewallBlockedByPolicy` (`FromPolicy` flag on the found rule); `IFirewallRules.FindInbound` is synchronous.
- **New open decision ⚑N29:** macOS package (user's cross-platform rule, 2026-10-07) — spike on a Mac first.


### Phase 4 — DNS, traceroute, TLS check, diagnosis, public IP — 2026-10-13

- **DNS** (`Dns/`): `DnsMessage` = RFC 1035 codec shared with phase-5 mDNS: `WriteQuery`/`WriteName` (UTF-8 labels, `\.`/`\DDD` escapes),
  `ReverseLookupName` (in-addr.arpa / ip6.arpa nibbles, IPv4-mapped → IPv4), `TryParse` (never throws: pointers must go strictly
  backwards — each jump below the previous one, so loops are impossible — max 127 jumps, labels ≤ 63, names ≤ 255 wire bytes, extended
  label types refused, counts checked against the length before allocating, RDATA bounds-checked, names in A/PTR/SRV RDATA must end
  exactly at the RDATA end). Records: `DnsAddressRecord`, `DnsNameRecord` (PTR/CNAME/NS), `DnsServiceRecord`, `DnsTextRecord`,
  `DnsOpaqueRecord` (SOA, OPT, others: raw). `DnsClient`: A/AAAA, SRV (sorted priority ↑, weight ↓), TXT, PTR, any type; random id,
  answer must echo id + question; UDP (connected socket, ignores datagrams with another id) → TCP on TC; SERVFAIL/REFUSED/timeout →
  next server (≤ 3), NXDOMAIN/NODATA final; CNAME chain followed inside the answer (≤ 8 hops); servers = options or the primary
  adapter's DNS servers then the others (fec0::ffff placeholders and duplicates dropped); IDN → punycode; one budget slot per exchange
  (⚑N24); DNS-over-HTTPS (RFC 8484 POST, id 0) only through `DnsClientOptions.OverHttps(uri)` / `UseDnsOverHttps(uri)` (⚑N26).
  Seam `Transports.IDnsTransport` + `SystemDnsTransport`.
- **Traceroute**: `IProbeTransport.PingHopAsync(address, ttl, …)` (default interface method → `ttl-unsupported`, so other transports
  keep compiling) returning `HopOutcome` (`ProbeReached` | `HopExpired(Router, RoundTrip)` | `ProbeTimedOut` | `ProbeUnreachable`);
  `NetworkProbe.PingHopAsync` (budgeted, metric kind `hop`); `Traceroute.RunAsync(IPAddress, maximumHops = 30, probesPerHop = 3, probe)`
  streams `TracerouteHop` (classified Personal/location), stops at the destination, an error, or 4 silent hops in a row.
- **TLS** (`Tls/TlsEndpointCheck.cs`): `TlsEndpointCheck.RunAsync(host, port, time[, options], ct)` → `TlsEndpointReport` (outcome
  union `TlsHandshakeSucceeded` | `TlsCertificateRejected` | `TlsConnectionFailed`, `DeviceClockSuspect`, `DaysUntilExpiry`, stable
  `Code` `tls.ok` / `tls.certificate.<problem>` / `tls.connection.<reason>`). Own chain build in the validation callback (device-clock
  verification time, OS store or `TrustedRoots` custom trust, revocation off by default), validity problem from the chain elements,
  then revocation → untrusted root → partial chain → name mismatch. **Nothing is sent to a rejected server** (callback returns false).
  `HEAD /` for the `Date` header only on 443/8443 (or `ReadHttpDate`), `ClockSkew` = trusted now − server date. `DeviceClockSuspect`
  = rollback flag, or anchored trusted time vs `options.DeviceClock` beyond 5 min, or NotYetValid/Expired by the device clock but
  valid at anchored trusted time, or (unanchored) server date beyond 5 min. Trusted time through core `ITrustedClock`/`GetTrustedNow()`:
  no reference to the YFex.Time repo.
- **Diagnosis** (`Diagnosis/NetworkDiagnosis.cs`): `NetworkDiagnosis.RunAsync(DiagnosisPlan)` → `DiagnosisReport` (ordered
  `DiagnosisStep(Code, Passed, Subject, Detail, Duration)` + `MaskedDetail`; `Passed`, `FirstFailure`, `ToText(masked: true)`). Order:
  `adapter.up|none` → `address.assigned|link-local` (stop) → `gateway.ping.*` → `gateway.neighbor.found|missing` (gateway neither pings
  nor shows in ARP → internet branch skipped) → `dns.server.answered|refused|timeout|…` per primary DNS server (root NS query; each
  server once) → per endpoint `endpoint.proxy.direct|via` (proxied: skip direct DNS/TCP/TLS) → `endpoint.dns.resolved|not-found` →
  `endpoint.tcp.*` → `endpoint.tls.*` (+ `endpoint.tls.clock-suspect`) → `endpoint.http.responded|proxy-authentication|server-error|tls|…`
  → per LAN host `host.ping.*` → `host.tcp.*` (≤ 8 ports) → `host.neighbor.found|missing` (on-link only; skipped when the platform has
  no neighbour table). Plans: ≤ 16 endpoints, ≤ 32 hosts, local addresses only (⚑N24). Vendor names through
  `NetworkDiagnosisOptions.VendorName` (the main package does not reference `.Vendors`).
- **Public IP** (⚑N7): `PublicIPAddressSource.Stun("host[:port]")` / `.Https(uri)`, `IPublicIPAddress` + `PublicIPAddressLookup`
  (sources in order, one budget slot each; `PublicIPAddressReading` = `PublicIPAddressFound` (classified) | `PublicIPAddressUnavailable`).
  STUN binding codec (RFC 8489: XOR-MAPPED-ADDRESS, MAPPED-ADDRESS fallback, transaction id checked, one retransmission). Registered
  only by `UsePublicIPAddressLookup(...)`.
- DI: `DnsClient`, `IDnsTransport`, `NetworkDiagnosis` (+ `NetworkingBuilder.Dns`, `.Diagnosis`, `UseDnsOverHttps`, `UseDnsTransport`,
  `UsePublicIPAddressLookup`); `NetworkProbe.Budget` exposed for helpers that share it. `.Testing`: `ScriptedDnsTransport` (+ `WithId`),
  `FakeNeighborTable` (`LearnOnPing`), `ScriptedProbeTransport.Hop/Path`.
- **Verified on this PC (AOT):** DNS UDP → TCP fallback against a loopback server; TLS 1.3 against a loopback `SslStream` (self-signed →
  untrusted-root; pinned root → ok, `TLS_AES_256_GCM_SHA384`, skew < 1 s); STUN; traceroute to loopback; a real LAN-only diagnosis:
  adapter, address, gateway ping 23 ms, gateway MAC + vendor, IPv4 DNS answers — and the router's advertised **IPv6 link-local DNS
  server answers REFUSED** (Windows' own `Resolve-DnsName` gets the same; Windows lists it twice, so servers are de-duplicated).
- **Counts:** YFex.Networking.Tests 105 → 168 (golden DNS packets incl. compression; 14 hostile messages; 20 000 random corruptions +
  5 000 noise buffers; client: TC → TCP, NXDOMAIN vs SERVFAIL fallback, timeouts, id/question mismatch, DoH id 0, PTR, server list; real
  loopback UDP/TCP DNS server; traceroute; TLS with generated ECDSA certificates: valid + server date, skewed date, expired + nothing
  sent, device clock set back with/without an anchored trusted clock, untrusted root, name mismatch, refused; RFC 5769 STUN vectors
  IPv4/IPv6 + 5 000 mutations + loopback server; HTTPS echo; diagnosis order/short-circuits/masking/validation/TLS step/DI).
  YFex.Networking.Windows.Tests 19 (unchanged). AotSmoke: publish 0 IL/AOT warnings, `ALL PASSED`.
- **Deviations from 02:** DNS record types carry a `Dns` prefix (`DnsAddressRecord`, `DnsServiceRecord`, … — ⚑N28: no bare "Address",
  and `ServiceRecord`/`TextRecord` are too generic); `DnsAnswer<T>` is a record with a `DnsQueryStatus` instead of a union (generic
  cases); `Traceroute.RunAsync` takes an `IPAddress` (a name that does not resolve has no hop to report; resolve first); the TLS check
  takes the OS clock as `TlsEndpointCheckOptions.DeviceClock` (YFex.Time's provider already returns corrected time); `DiagnosisStep`
  has a `Subject` and `MaskedDetail`; `DiagnosisHost(Name, IPAddress, Ports)`; DNS server reachability is a root `NS` query per server.


### Phase 5 — discovery, in-memory LAN — 2026-10-13

- **Seam** `Transports.IMulticastTransport` (`Join(group)` = bound to the group port, shared; `OpenClient(group)` = ephemeral port for
  searches) / `IMulticastSocket` (`SetAdapters`, `SendAsync(datagram, destination?, adapter?)`, `ReceiveAsync`); `MulticastGroup`
  presets `Beacons`, `MulticastDns`, `Ssdp`, `WsDiscovery`. `SystemMulticastTransport`: one IPv4 + one IPv6 socket, `ReuseAddress` (works
  next to Windows' mDNS responder — verified), memberships per adapter (IPv4 by address, IPv6 by the link-local scope id), loopback on,
  IPv6 best effort. Arrival adapter = the adapter the **source** is on-link for (IPv6 link-local by scope id); anything else is dropped.
- **Beacons** (`Discovery/Beacons.cs`, `BeaconService.cs`): wire v1 exactly as 03 §2.2 (`BeaconWire.Write/TryRead`; reserved flag bits
  rejected); `IAnnouncementSigner`, `IAnnouncementVerifier`, `AnnouncementToVerify` (ref struct, fields as agreed with Installations),
  `AnnouncementVerdict { NotChecked = 0, Verified, Rejected }` (NotChecked is the default value, not Verified), `BeaconCandidate`
  (classified). `BeaconService` (also `IHostedService` when kinds are announced): announce every ≥ 1 s ±20 %, goodbye on stop, sequence =
  wall-clock µs or previous + 1 (survives restarts); listen / `QueryAsync(kind, window)`; inbound: > 512 B, off-link, > 5/s per source
  (bounded table), malformed → dropped and counted (`BeaconStatistics`, `yfex.networking.discovery.announcements.rejected{kind,reason}`);
  queries answered by multicast at most once per second per querier. Public category: only kinds in `AllowPublicNetworkCategory`, and then
  listening requires a verifier and announcing a signer (`InvalidOperationException` otherwise). **Deviation from 03 §2.3:** the
  duplicate filter is per listener and updated only for candidates that passed its verifier, so a forged datagram with a huge sequence
  cannot block the real sender (no verdict cache needed: duplicates never reach the verifier).
- **DNS writer** `DnsMessageWriter.TryWrite` (name compression everywhere incl. PTR/SRV data); records carry `Class` (+ `CacheFlush`).
- **mDNS/DNS-SD** (`MulticastDns`): `AdvertiseAsync(ServiceAdvertisement)` → `ServiceRegistration` (probe ×3 / 250 ms, conflict →
  `"Name (2)"`, simultaneous-probe tie-break on the SRV data, announce ×2, answers PTR / SRV / TXT / `_services._dns-sd._udp`, known-answer
  suppression, legacy unicast (id + question echoed, TTL ≤ 10 s, no cache-flush bit), 20–120 ms delay for shared answers, one multicast
  per record per second, goodbye TTL 0; not on Public networks unless `AllowPublicNetworkCategory`); `BrowseAsync` / `FindAsync` (PTR
  queries 1 s doubling to 1 h with known answers, SRV/TXT and A/AAAA follow-ups, goodbye/expiry → `Goodbye`). **Deviations:** no QU bit
  (the port is shared with the OS responder, a unicast reply could reach its socket); the host name is not claimed (the OS responder owns
  it; addresses ride in the additional section; `AnswerHostAddressQueries` opt-in); conflicts after the announcement are not re-probed;
  `ServiceTextEntry(Key, Value)` instead of tuples.
- **SSDP** (`SsdpClient`): `SearchAsync(st, window)` (M-SEARCH ×2, MX ≤ 5, IPv4, ≤ 20 answers/s per sender, de-duplicated by USN+ST);
  `GetDescriptionAsync` only for an IP-literal http `LOCATION` that is on-link (loopback only from loopback), ≤ 64 KB, no proxy, no
  redirects, `XmlReader` with DTDs ignored and no resolver; root device fields only. **WS-Discovery** (`WsDiscoveryClient.ProbeAsync`,
  `WsDiscoveryType.Device/PrintDevice/NetworkVideoTransmitter`): Probe ×2 to 239.255.255.250:3702, matches only with our `RelatesTo`.
- **`.Testing`**: `InMemoryNetwork` (`Join(name, address, category, prefix, gateway)`, `Partition/Heal`, `Impair`, `ImpairmentFor`,
  `DroppedDatagrams`), `InMemoryNode` (adapter with per-node category, `Conditions`, `LocalNetwork`, `Probes` — ping/TCP to nodes, names
  resolve to node names, `Listen(port)` — `Multicast`, `Unplug/Plug`), `NetworkImpairment`, `ChaosStream`, `UseInMemory(node)`.
- DI: `IMulticastTransport`, `BeaconService`, `MulticastDns`, `SsdpClient`, `WsDiscoveryClient` (sockets open on first use);
  `network.Beacons(b => b.Announce(...).AllowPublicNetworkCategory(kind))`, `network.MulticastDns` options, `UseMulticastTransport`.
- **Verified on this PC (tests + AOT, real sockets):** own beacon (4 candidates: IPv4 + IPv6 sources) and own mDNS advertisement found
  next to Windows' responder; SSDP found 2 root devices and read the router's description ("Linux UPnP IGD"); WS-Discovery: no devices.
- **Counts:** YFex.Networking.Tests 168 → 215 (+1 real-LAN test, trait `Network`); Windows.Tests 19. AotSmoke: 0 IL/AOT warnings,
  `ALL PASSED`. mDNS tests browse **before** advertising: a record is multicast at most once per second (RFC 6762 §6), so a browser started
  right after the announcements waits for the next round (~2 s).


### Phase 6 — Telnet move + SNMP client — 2026-10-14

- **Telnet** (`Telnet/`, ⚑N5/⚑N12): `TelnetChannel` moved from Devices, public, over a `Stream` (owned); `ITelnetHandler`, `TelnetCommands`
  (RFC 854 bytes incl. NOP/AYT/GA…), `TelnetOptions` (Binary, Echo, SuppressGoAhead, TerminalType, WindowSize), `TelnetChannel.MaxSubnegotiation`
  = 1 024, internal `AsyncSignal` copy. Changes on the way: the receive loop ends on **any** stream or handler exception and keeps it in `Fault`
  (it listed Devices' `DeviceException` before), nothing after disposal is a fault, `QueueCommand(byte)` for two-byte commands, the
  `Transport` property is gone. `TelnetSession` (line-oriented client): `ConnectAsync(host, 23)`, `Create(stream)`, `WriteLineAsync` (CR LF),
  `ReadLineAsync` (CR LF / LF / CR NUL / lone CR, long lines in pieces), `ReadUntilAsync(prompt)` (bounded), `ServerEchoes`; accepts the
  server's ECHO/SGA/BINARY, refuses every option on our side, asks DO SGA; Latin-1 by default.
- **Devices migration:** `TelnetChannel.cs` deleted; `YFex.Devices` references `YFex.Networking`; `Rfc2217.OptionComPort = 44`;
  `Rfc2217SerialPort` creates `new TelnetChannel(transport.AsStream(leaveOpen: false), …)` and keeps its own `IByteTransport` for
  `FlushAsync`; it no longer implements `ITelnetHandler` publicly (a private forwarding class: callers cannot inject "received" bytes);
  `Rfc2217Server` passes `client.AsStream()`. `ArchitectureTests.Layers` loses `Network` (05 §1). **There were no Telnet or RFC 2217 tests in
  Devices to move:** new `Serial/Rfc2217Tests` (client ↔ server over loopback TCP on a `VirtualSerialPair`: settings applied to the real
  port, IAC both ways, DTR → peer DSR, peer RTS → client CTS via NOTIFY-MODEMSTATE, `ConfigureAsync`; a server answering `DONT COM-PORT` →
  `NotSupported`).
- **SNMP** (`Snmp/`, ⚑N17): BER codec (`BerReader` ref struct — definite lengths of ≤ 3 bytes, single-byte tags, never throws; `BerWriter`
  with minimal lengths), `SnmpObjectIdentifier` (2–128 arcs of 32 bits, lexicographic order, `Append`, `StartsWith`), `SnmpValue` (struct with
  a type tag: Integer32, OctetString, Null, OID, IpAddress, Counter32, Gauge32, TimeTicks, Opaque, Counter64, noSuchObject/Instance,
  endOfMibView), `SnmpVariable`, `SnmpErrorStatus` (RFC 3416), `SnmpStatus` + `SnmpResult<T>` (record with status and stable `Code`, like
  `DnsAnswer<T>`), `SnmpException` (walks only). `SnmpCredentials` (`Community(…)` / `User(name, auth, priv)`, classified **Secret**, ToString
  hides secrets), `SnmpAuthentication.Sha256/Sha384/Sha512(passphrase)` (RFC 7860, 24/32/48-byte MACs; the 1 MiB password-to-key runs once,
  the passphrase is not kept), `SnmpPrivacy.Aes128(passphrase)` (RFC 3826 CFB-128). `SnmpClient(address, credentials, options)`: `GetAsync`
  (one or ≤ 64 OIDs), `GetNextAsync`, `GetBulkAsync`, `WalkAsync(prefix, maxVariables ≤ 10 000)` (GetBulk, halves max-repetitions on tooBig,
  stops at the subtree end, endOfMibView or a non-increasing OID). v3: engine discovery, keys localized per engine, authenticated
  notInTimeWindow resync (clock moves forward only), rediscovery on unknownEngineID, Reports → statuses (unknown user, wrong digest,
  decryption, security level), responses checked (msgID, MAC, engine id, request id, no downgrade). One `ProbeBudget` slot **per datagram**
  (retransmissions and discovery included); metric kind `snmp`. Read only: no Set.
- **Crypto rule:** all USM HMAC/AES is in `Snmp/SnmpSecurity.cs`, added to the YFex.Cryptography architecture-test allow-list as
  protocol-mandated (P-class), next to Payment's webhook HMAC. Nothing else in Networking uses keyed crypto.
- **`.Testing`:** `SnmpTestAgent` (loopback UDP, v2c community or one v3 USM user; Get/GetNext/GetBulk over a test MIB; discovery,
  notInTimeWindow, unknown user/engine, wrong digest, decryption and security-level Reports; `DropNextRequests`, `EngineBoots` (reboot),
  `MaximumResponseSize`, `AnswerTooBigToLargeBulk`, `ReportClockOnDiscovery`). Shares the internal codec (`InternalsVisibleTo`
  YFex.Networking.Testing). Meant also for Devices.Peripherals' Printer-MIB/UPS-MIB helpers.
- **Vectors:** RFC 3414 A.3.2 (SHA-1 `Ku`/`Kul` for "maplesyrup" — checks the procedure), SHA-256/384/512 localized keys and a truncated
  HMAC computed independently with Python `hashlib` (`C:\Temp\networking-edits\snmp_vectors.py`; SHA-256 equals RFC 7860's example), NIST
  SP 800-38A F.3.13 AES-128-CFB (+ a partial block), X.690 `2.999.3`, a hand-assembled v2c GetRequest.
- **Counts:** YFex.Networking.Tests 216 → 284 (11 Telnet, 57 SNMP; incl. the real-LAN test); Windows.Tests 19; YFex.Devices.Tests 157 → 159
  (157 pass; the same two environmental failures as phase 0: `ArchitectureTests` cannot find `devices.slnx` under `--artifacts-path`, the
  COM-arbiter test); YFex.Cryptography architecture tests 2/2. Networking AotSmoke: 0 IL/AOT warnings, `ALL PASSED` (Telnet session, SNMP
  v2c get, v3 authPriv walk, `SnmpCredentials` classified Secret). Devices AotSmoke publish: 0 warnings (its exe not run: the pre-existing
  named-pipe hang).
- **Not verified against a real agent:** no SNMP device on this LAN was queried (the router's SNMP was not probed); interoperability rests
  on the vectors and the RFCs. First real check: a printer with SNMPv3 at a shop.
- **Deviations from 02/05:** types in namespace `YFex.Networking` (folders `Telnet/`, `Snmp/`), as every other area, not
  `YFex.Networking.Telnet`; `TelnetSession` **refuses** a non-local peer unless `TelnetSessionOptions.AllowNonLocalPeer` (02 §12 said "logs a
  warning": the library has no logger); `SnmpResult<T>` is a record with a status (generic cases), `SnmpValue` a struct with a type tag (walks
  return hundreds); no HMAC-SHA-224 (not in the BCL), MD5/SHA-1 not offered; `SnmpCredentials` is a class; no DI registration (one client per
  agent, built by the caller); `WalkAsync` throws `SnmpException` (an async enumeration has no status).
