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

## Phase 6 — Telnet move + SNMP

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
- Docs: public/messaging.md, internal/messaging-internals.md, src/Messaging/docs/03, offline 04 §1, time README §10.
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

