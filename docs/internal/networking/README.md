# YFex.Networking — network conditions, diagnostics, LAN discovery — Plan

> **Status:** APPROVED 2026-10-07. ⚑N1–N7 confirmed 2026-10-05 (time session, while discussing offline "auto-detect friends");
> ⚑N8–N27 confirmed 2026-10-07 as recommended (N8 = **Survey**, N9 = packet capture stays in YFex.Security). Implementation log in
> [06](06-phases-and-acceptance.md). **Phases 0 (core types), 1 (`IServerConnection`) and 2 (repo + conditions/probes/endpoints) DONE
> 2026-10-07**; **phase 3 (`.Windows`, `.Vendors`, neighbours, Wake-on-LAN, proxy/VPN, multi-homing, fingerprint evidence) DONE
> 2026-10-07**; **phase 4 (DNS wire client + DoH opt-in, traceroute, TLS endpoint check with clock hints, diagnosis report, opt-in
> public IP) DONE 2026-10-13**; **phase 5 (beacons with signer/verifier hook, mDNS/DNS-SD, SSDP, WS-Discovery, `InMemoryNetwork` +
> `ChaosStream`) DONE 2026-10-13**; **phase 6 (Telnet moved from Devices + `TelnetSession`, SNMP v2c/v3 client with a bounded walk,
> `SnmpTestAgent`) DONE 2026-10-14**; next phase 7 (survey package), the last one. Open: ⚑N29 (macOS).
> **Repo:** `C:\Dev\YFex\YFex.Networking` (sibling, like YFex.Time and YFex.Cryptography). **Core types:** namespace `YFex.Networking`
> in YFex core (`YFex/src/YFex/Networking`, types only, like ⚑K2).

## Files

| File | What it covers |
|---|---|
| README (this file) | why, scope, shape, packages, decisions, phase summary |
| [01 — Inventory](01-current-state-and-inventory.md) | everything network-related that exists today (file:line), live defects found, what .NET 11 gives us |
| [02 — API and examples](02-api-and-examples.md) | **dev-side code first** (pharmacy/POS), then the API per area: conditions, snapshot, probes, quality, diagnosis, TLS, neighbours, vendors, Wake-on-LAN, Wi-Fi, proxy/VPN, multi-homing, SNMP, Telnet, ports/firewall, support matrix, privacy, metrics, testing |
| [03 — Discovery](03-discovery.md) | multicast beacons with a verification hook, mDNS/DNS-SD, SSDP, WS-Discovery; wire formats; abuse limits; how Offline 04 §2 and Devices remoting use it |
| [04 — Survey package and safety](04-survey-and-safety.md) | what stays in the main package, what goes to `YFex.Networking.Survey` (⚑N8), guardrails, why packet capture stays in YFex.Security (⚑N9) |
| [05 — Consumers and migration](05-consumers-and-migration.md) | ⚑N2 Devices, ⚑N3 `MacAddress`, ⚑N4 `IServerConnection`, ⚑N5 Telnet, YFex.System, YFex.Time, Offline `IConnectivity`, Logging metered mode |
| [06 — Phases, acceptance, log](06-phases-and-acceptance.md) | phases 0–7 with acceptance, test/benchmark/AOT gates, implementation log |

## 1. Why one networking library

Network code is scattered, duplicated or only planned in six places (details in [01](01-current-state-and-inventory.md)):

| Where | What | Problem |
|---|---|---|
| Messaging.Rpc `INetworkStatus` | "is my RPC link to the server up?" | the name says "network"; consumers read it as "is there internet" (⚑N4) |
| YFex.System `NetworkSnapshot` | adapters, masked IPs, hashed MAC (telemetry) | unsalted MAC hash (privacy F-list), private IP mask copy, no change stream |
| YFex.Time `TimeSynchronizationService` | resync on `NetworkAvailabilityChanged` | misses "cable → Wi-Fi" (availability never went false) |
| YFex.Devices plan §13 / phase 11 | mDNS, SSDP, ARP/ICMP sweep, OUI, Telnet (inside RFC 2217) | generic network code in a device library (⚑N2, ⚑N5); `MacAddress` is a Devices type (⚑N3) |
| Offline design 04 §1–§2 | `IConnectivity`, LAN beacon, health hysteresis | needs machine-level conditions and beacons it should not own |
| Logging 05 §6 | `OnlyOnUnmeteredFor(...)` | needs "is this link metered?" (Windows `NetworkCostType`), nobody provides it |

A pharmacy terminal has four different questions that today collapse into one boolean:

1. **Is the cable/Wi-Fi up?** (machine) — `INetworkConditions.Current.Internet == NoNetwork`
2. **Is there internet, or a captive portal, or only the shop LAN?** (machine) — `InternetReachability`
3. **Is the central server answering us?** (our RPC link) — `IServerConnection` (renamed from `INetworkStatus`, ⚑N4)
4. **Which mode do we operate in (central / site broker / standalone)?** — Offline `IConnectivity`, which composes 1–3

The status bar can finally say the true thing ("Sem internet — rede da loja OK" vs "Servidor central fora do ar"), support can run
one diagnosis instead of asking the operator to open `cmd`, and sync jobs stop downloading the price table over a 4G dongle.

## 2. Scope

**In:** machine network conditions + change stream; interface/IP/gateway/DNS/DHCP snapshot; reachability with hysteresis (TCP, HTTP 204
/ captive portal, DNS; Windows NCSI/NLM); link quality (latency, jitter, loss) and cost (metered); targeted diagnostics (ping, TCP
connect, traceroute, DNS incl. SRV/TXT/PTR, optional DNS-over-HTTPS, HTTP, TLS endpoint check with clock-skew explanation); endpoint
health monitor; proxy and VPN detection; Wi-Fi info (.Windows); multi-homing (prefer Ethernet over 4G); IP math and classification;
network fingerprint *hint*; ARP/NDP neighbour table; MAC vendor lookup; Wake-on-LAN; discovery (beacons with a verification hook, mDNS/
DNS-SD, SSDP, WS-Discovery); SNMP v2c/v3 get of one host; Telnet (⚑N5); free-port selection; firewall-rule **presence** check; opt-in
public-IP lookup (⚑N7); platform support matrix; privacy-safe logs and metrics; a `.Testing` package with an in-memory LAN.

**Out (never in this library):**
- **Trust, tenancy, installation identity, enrollment, mTLS peer channels, the site mesh** → `YFex.Installations` (⚑N6). Discovery
  returns *candidates*; the consumer verifies them.
- **Changing system settings**: firewall rules, adapters, DNS servers, proxy, Wi-Fi profiles, clock. Installer's job, with consent.
- **Packet capture / promiscuous mode** → stays in `YFex.Security.Capture` (ETW + SharpPcap already there, ⚑N9).
- **Offensive features**: ARP/DNS spoofing, credential brute force, stealth/evasive scanning, exploits. Not in any package.
- **Subnet sweeps, multi-port scans, SNMP walks across a subnet, NetBIOS/SMB enumeration** → the separate survey package (⚑N8, [04](04-survey-and-safety.md)).
- An HTTP framework, a VPN client, a DHCP/DNS server.

## 3. Shape

```
 YFex core (types only, namespace YFex.Networking)          YFex.Networking (library, net11.0, AOT)                consumers
 ┌───────────────────────────────────────────┐   ┌───────────────────────────────────────────────────┐   ┌─────────────────────┐
 │ MacAddress            (moved, ⚑N3)         │   │ NetworkConditionsMonitor : INetworkConditions     │──►│ Offline IConnectivity│
 │ INetworkConditions    NetworkConditions    │◄──│   ← LocalNetwork (NetworkInterface + NetworkChange)│   │ Time (resync)       │
 │ InternetReachability  NetworkMedium        │   │   ← InternetProbe (HTTP 204 / TCP / DNS)          │   │ Logging (metered)   │
 │ NetworkCost           NetworkConditionFlags│   │   ← INetworkConditionsSource (.Windows NLM, app)  │   │ Jobs / sync / UI    │
 │ NetworkConditionsChange  EndpointHealth    │   │ Hysteresis · EndpointMonitor · LinkQuality         │   │ Messaging.Rpc (opt.)│
 └───────────────────────────────────────────┘   │ NetworkProbe · Traceroute · DnsClient · TlsCheck   │   └─────────────────────┘
                                                 │ NeighborTable · WakeOnLan · IPAddressing · Proxies   │
 Messaging.Rpc                                   │ Discovery: Beacons · Mdns · Ssdp · WsDiscovery     │   ┌─────────────────────┐
 ┌───────────────────────────────────────────┐   │ SnmpClient · Telnet (⚑N5) · FreePorts              │──►│ Devices (9100,      │
 │ IServerConnection (was INetworkStatus, ⚑N4)│   │ NetworkingSupport · Meter "YFex.Networking"       │   │ Modbus TCP, remoting│
 └───────────────────────────────────────────┘   └───────────────────────────────────────────────────┘   │ agents, RFC 2217)   │
                                                   YFex.Networking.Windows: NLM conditions/cost/id,      └─────────────────────┘
                                                   Wi-Fi (WLAN API), neighbour table, firewall-rule read
                                                   YFex.Networking.Vendors: IEEE MAC prefix table (⚑N15)
                                                   YFex.Networking.Testing: fakes, InMemoryNetwork, chaos
                                                   YFex.Networking.Survey (+ .Windows.Survey): survey (⚑N8)
```

## 4. Packages

| Package | TFM | Contents | Depends on |
|---|---|---|---|
| `YFex.Networking` | net11.0 | everything cross-platform (sections of [02](02-api-and-examples.md) and [03](03-discovery.md)); Linux specifics are file reads behind `OperatingSystem.IsLinux()` (⚑N11) | YFex core, M.E.DI.Abstractions, M.E.Hosting.Abstractions; Privacy generator as analyzer only |
| `YFex.Networking.Windows` | net11.0, `[SupportedOSPlatform("windows")]` (like Cryptography.Windows) | NLM connectivity + cost + network id/category (source-generated COM), WLAN API (SSID, BSSID, signal), `GetIpNetTable2`, firewall-rule presence (read only) | YFex.Networking |
| `YFex.Networking.Vendors` | net11.0 | IEEE MA-L/MA-M/MA-S table, compressed, ~0.4 MB (⚑N15) | YFex core |
| `YFex.Networking.Testing` | net11.0 | `FakeNetworkConditions`, `FakeNeighborTable`, `InMemoryNetwork` (discovery bus, partitions, per-node conditions), `ChaosStream` (latency/jitter/loss/partition) | YFex.Networking |
| `YFex.Networking.Survey` | net11.0 | survey: subnet sweep, port list scan, SNMP walk, NetBIOS name query, site survey report — opt-in, audited, rate-limited ([04](04-survey-and-safety.md)) | YFex.Networking |
| `YFex.Networking.Windows.Survey` | net11.0, windows | `SendARP` sweep, SMB share enumeration (`NetShareEnum`) | the two above |
| tests, AotSmoke, Benchmarks | | `YFex.Networking.Tests`, `.Windows.Tests`, `.AotSmoke` (publish, 0 warnings), BenchmarkDotNet with `InProcessEmitToolchain` | |

## 5. Decisions (⚑N)

### 5.1 Confirmed (user, 2026-10-05)

| # | Decision |
|---|---|
| N1 | New repo `YFex.Networking` (BCL + Microsoft.Extensions abstractions, AOT). Windows-specific code in `YFex.Networking.Windows`. Linux: propose (⚑N11) |
| N2 | Devices' planned `Network` namespace (mDNS/DNS-SD, SSDP/UPnP, ARP/ICMP sweep, OUI) moves here. Devices keeps device-specific discovery (9100 printers, Modbus TCP, remoting agents) built on YFex.Networking |
| N3 | `MacAddress` moves from Devices to YFex core, classified `[Sensitive]` (LGPD: a MAC can identify a person) |
| N4 | Messaging.Rpc `INetworkStatus` → `IServerConnection` (+ state enum), `[Obsolete]` forwarding for one release (**user, 2026-10-07: removed instead — nothing used the old names**). Machine-level state = `INetworkConditions`. Offline `IConnectivity` composes both |
| N5 | Devices' generic `TelnetChannel` (RFC 854) moves here; RFC 2217 stays in Devices on top of it |
| N6 | Installation identity / "friends" is `YFex.Installations` (own task). Networking returns candidates + a verification hook; never decides trust or tenancy |
| N7 | Public-IP lookup (STUN / HTTPS echo) is opt-in only |
| — | Active-scanning split: main package = basic, targeted, rate-limited, passive-leaning checks; anything AV/EDR may flag → `YFex.Networking.Survey` + `.Windows.Survey`. Never evasion, never offensive features |

### 5.2 Confirmed 2026-10-07 (⚑N8–N27, all as recommended)

| # | Question | Recommendation | Alternatives | Where |
|---|---|---|---|---|
| **N8** | Name of the intrusive package | **`YFex.Networking.Survey`** (+ `.Windows.Survey`): neutral, says what it is for (site/asset survey by support) | `Probing` (sounds like attack tooling), `Elevated` (most of it does not need admin), `Inventory` (clashes with Devices `Inventory`) | 04 §1 |
| **N9** | Separate `Capture` package? | **No.** Packet capture already lives in `YFex.Security.Capture` (ETW, SharpPcap, JA3). Networking never opens raw/promiscuous sockets | `YFex.Networking.Capture` with pktmon/ETW (duplicates Security) | 04 §4 |
| **N10** | Core contract types | **YFex core namespace `YFex.Networking`**: `MacAddress`, `INetworkConditions`, `NetworkConditions`, `NetworkConditionsChange`, `InternetReachability`, `NetworkMedium`, `NetworkCost`, `NetworkConditionFlags`, `EndpointHealth` (also replaces Offline's planned `PeerHealth`). Time, Logging, Offline, Messaging read conditions without referencing the repo | keep `INetworkConditions` in the library (every consumer references YFex.Networking) | 02 §1, 05 |
| **N11** | Linux | **in the core package** behind `OperatingSystem.IsLinux()`: only file reads (`/proc/net/arp`, `/proc/net/route`, `/proc/sys/net/ipv4/ping_group_range`); the BCL already uses netlink for `NetworkChange` | `YFex.Networking.Linux` (one more package for ~200 lines) | 02 §15 |
| **N12** | Telnet transport | **`TelnetChannel` over `Stream`** (BCL). Devices passes `transport.AsStream()` (exists, `IByteTransport.cs:30`); `AsyncSignal` copied as internal | move Devices' `IByteTransport` to core (a second byte-stream abstraction next to `Stream`) | 05 §4 |
| **N13** | `IServerConnection` details | stays in **Messaging.Rpc** (it is about the RPC peer); `ServerConnectionState { Connected, Reconnecting, Disconnected }` (drop `Syncing`: never produced); fix the guessed `Previous` (D1); `NetworkStatusProvider` (written, never read) obsoleted without replacement; `ServerConnectionChangedEvent` published next to the old event for one release | move to core | 05 §3 |
| **N14** | Default internet probe target | **no third-party default**: Windows uses NLM/NCSI (no traffic from us); elsewhere probe the app's own endpoint (e.g. central `/health/204`). Google/Microsoft/Cloudflare 204 endpoints only when the app adds them | built-in `connectivitycheck.gstatic.com` (leaks the shop IP to Google every 30 s) | 02 §1 |
| **N15** | MAC vendor table | (user 2026-10-07: snapshot committed in the repo, refreshed by the tool, never fetched at run time; IEEE download approved) **data-only package `YFex.Networking.Vendors`**: IEEE CSV → Brotli resource (~0.4 MB) by a dev tool; binary search over sorted prefixes, 0 B per lookup. Check the IEEE listing's redistribution terms before shipping | embed in the core package (+0.4 MB for everyone); source generator emitting 37 k entries (slow builds) | 02 §8 |
| **N16** | Privacy of IP/MAC | `MacAddress` = `[Sensitive(Personal, Category = "identification", Mask = "##:##:##:**:**:**")]` (vendor prefix stays visible), **hand-implemented** `IClassifiedData<MacAddress>` in core (core has no Privacy generator). `IPAddress` (BCL) cannot carry an attribute: Networking logs only masked text (`Masks.TryWriteIpAddress`) until Privacy ships `[assembly: SensitiveType<T>]`; its records reference the Privacy generator as analyzer | reference the Privacy generator from core; a `NetworkAddress` wrapper type | 02 §16 |
| **N17** | SNMP scope | Networking = SNMP **client** (v2c + v3 USM SHA-2/AES-128, `Get`/`GetNext`/`GetBulk` on **one** host) + BER codec. Well-known device MIBs (Printer-MIB supplies, UPS-MIB battery) live in **Devices.Peripherals** | MIB helpers in Networking; v2c only in the first phase | 02 §11 |
| **N18** | mDNS backend on Windows | **managed responder everywhere** (`ReuseAddress` on 5353 works next to Windows' own responder; one code path, testable); a `dnsapi` (`DnsServiceRegister/Browse`) backend in .Windows only if a conflict is measured | dnsapi first on Windows | 03 §3 |
| **N19** | Beacon defaults | group `239.255.70.1` / `ff05::7966`, port 47001, configurable; ≤ 512-byte datagrams; interval ≥ 1 s with ±20 % jitter; answers only on-link queriers; on Windows listen only on networks of category Private/Domain, with a per-kind `AllowPublicNetworkCategory()` opt-in that requires a verifier (Installations' request: shop LANs are often classified Public); kind names/payloads belong to consumers (Installations proposes one kind `yfex.installation`) | broadcast `255.255.255.255`; listen on every adapter | 03 §2 |
| **N20** | Resilience integration | **no package**: apps read `INetworkConditions` in a Resilience predicate ("do not burn retries while `NoNetwork`"); `EndpointMonitor` reports health only | `YFex.Networking.Resilience` adapter | 02 §5 |
| **N21** | Platform matrix name | `NetworkingSupport` (not "Capabilities": clashes with YFex.Capabilities) | `NetworkingPlatform` | 02 §15 |
| **N22** | YFex.System `NetworkSnapshot` | **keep System independent**; telemetry keeps working; the unsalted MAC hash is fixed with the Privacy pseudonymizer (privacy 05 §3); Profiling may switch to `LocalNetwork` later | System.Windows references YFex.Networking now | 05 §5 |
| **N23** | Hysteresis | one public `Hysteresis` type with Offline 04 §4 defaults (3 failures 5 s apart to go down, 30 s healthy to go up, doubling to 10 min, 60 s minimum dwell); Offline reuses it | Offline keeps its own | 02 §1 |
| **N24** | Core active-probe budget | the main package enforces it: ≤ 32 targets per call, ≤ 10 active probes/s per process (token bucket), private/link-local subnets only for anything multi-target; more → survey package. Makes the ⚑N8 split a rule in code, not a convention | documentation only | 04 §2 |
| **N25** | QUIC | no code; notes for Installations (msquic is built into Windows 11/Server 2022, Linux needs `libmsquic`; `QuicConnection.IsSupported`); `NetworkingSupport` reports it | a QUIC peer-channel helper here | 02 §15 |
| **N26** | DNS-over-HTTPS | opt-in only (sends lookups to a third party; corporate networks block it) | on by default as a fallback | 02 §4 |
| **N27** | Result style | cold I/O APIs (probe, TLS check, Wi-Fi read, SNMP) return native `union` types with record cases; hot paths (MAC parse, IP classification, vendor lookup, `Current` read) are struct/`TryX`, 0 B | exceptions; `[Union]` tagged structs everywhere | 02 |
| **N28** | Naming of IP types (user, 2026-10-07) | **no bare "Address"** (in a pharmacy/POS app it reads as a customer/delivery address): `IPAddressScope`, `IPAddressing`, `PublicIPAddressSource`/`PublicIPAddressReading`; `IP` cased like the BCL (`IPAddress`, `IPNetwork`) | `IpAddressScope`, `NetworkAddressScope` | 02 §3, §14 |
| **N29** | macOS (user rule 2026-10-07: core + libs must work on Windows/Linux/macOS; OS code in per-OS packages) | **proposed:** `YFex.Networking.MacOS` for the ARP table (`sysctl` `NET_RT_FLAGS`), Wi-Fi (CoreWLAN) and cost (`Network.framework`); the main package already runs on macOS; apps document `NSLocalNetworkUsageDescription`/`NSBonjourServices` (macOS 15 Local Network permission) — spike on a Mac before building | nothing macOS-specific (no ARP/Wi-Fi/cost there) | 02 §15 |

## 6. Phases (details and acceptance in [06](06-phases-and-acceptance.md))

| Phase | Content |
|---|---|
| 0 | Core types (`YFex/src/YFex/Networking`), `MacAddress` moved (Devices updated, ⚑N3) |
| 1 | `IServerConnection` rename in Messaging.Rpc (⚑N4; the `[Obsolete]` bridges were removed 2026-10-07; coordinated with the "Authorization consolidation phase 3" session) |
| 2 | Repo scaffold; `LocalNetwork` snapshot + change stream; `IPAddressing`; `NetworkProbe` (ping, TCP, DNS, HTTP); `Hysteresis`; `NetworkConditionsMonitor`; `LinkQuality`; `EndpointMonitor`; `NetworkingSupport`; meter; `.Testing` fakes; AotSmoke; benchmarks; Time adopts conditions |
| 3 | `.Windows` (NLM, cost, Wi-Fi, neighbour table, firewall read) + Linux neighbour table; `.Vendors`; Wake-on-LAN; fingerprint hint; multi-homing; proxy/VPN detection |
| 4 | DNS wire client (SRV/TXT/PTR, DoH opt-in); traceroute; TLS endpoint check (+ YFex.Time skew); `NetworkDiagnosis` report; public IP (opt-in) |
| 5 | Discovery: beacons, mDNS/DNS-SD, SSDP, WS-Discovery; `InMemoryNetwork` + chaos (for the offline multi-installation harness) |
| 6 | Telnet moved (⚑N5) + SNMP client; Devices migrates (RFC 2217 on Networking Telnet; plan §13 implemented as device discovery on Networking) |
| 7 | Survey package |
