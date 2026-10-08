# 01 — Current state and inventory (checked 2026-10-05)

## 1. Network code that exists today

| Where | What | Notes |
|---|---|---|
| `YFex/src/Messaging/YFex.Messaging.Rpc/INetworkStatus.cs` | `enum SyncState { Connected, Disconnected, Reconnecting, Syncing }`, `INetworkStatus { IsConnected, State, Changed }`, `AlwaysConnectedNetworkStatus`, static `NetworkStatusProvider` | Means "the Fusion RPC peer to the server is connected" (⚑N4) |
| `.../YFex.Messaging.Rpc/FusionNetworkStatus.cs` | projects `RpcPeer.ConnectionState` (`AsyncState.WhenNext()` loop) | `Error is null → Reconnecting`, else `Disconnected` |
| Consumers of `INetworkStatus` | `LocalDispatcher.cs:25,38`, `OutboxReplayer.cs:33,51,80`, `FusionMessageBus.cs:106,139,153`, `RpcMessagingExtensions.cs:76–79,107,170–171,197`, `Time/CentralTimeSource.cs:15,18,64`, Rules.Admin `RuleAdminSetup.cs:153–170` (`RuleSetSync`), `SyncEvents.cs:25` (`NetworkStatusChangedEvent(SyncState, SyncState)`) | 9 production files |
| Test doubles of `INetworkStatus` | Messaging.Tests `Fixtures/ManualNetworkStatus.cs` (+ 7 test files), Rpc.Tests `CallContextTests.cs:286`, `CentralTimeTests.cs:121`, `RpcFixture.cs:163`, Rules.Admin.Tests `AdminFixture.cs:165`, EventSourcing.Tests `ModelASyncTests.cs:156`, EventSourcing AotSmoke `Program.cs:38` | 6 hand-written fakes → `.Testing`-style fakes after the rename |
| `YFex.System/YFex.System/Data/NetworkSnapshot.cs` | `AdapterInfo(Name, HashedMac, MaskedIpAddresses, SpeedBps, IsUp)`, `NetworkSnapshot(Adapters, HasNetworkAccess)` | telemetry shape |
| `YFex.System/YFex.System.Windows/WindowsSystemInfoProvider.cs:43–79` | `NetworkInterface.GetAllNetworkInterfaces()`, `SHA256(mac)` **without a key**, private `MaskIpAddress` | cross-platform BCL code inside a `.Windows` project; privacy inventory already lists the unsalted hash (privacy 01 rows 30/42) |
| `YFex.Profiling/.../Telemetry/TelemetryCollector.cs:146–158` | reads `CaptureNetworkSnapshot()`: `networkAccess`, `adapterCount`, `adapter_{Name}_mac` (hashed), speed | consumer of the above |
| `YFex.Time/src/YFex.Time/TimeSynchronizationService.cs:60,95,116` | resync on `NetworkChange.NetworkAvailabilityChanged` when `IsAvailable` | D5 below |
| `YFex.Devices/src/YFex.Devices/MacAddress.cs` | `[DeviceValue<ulong>]` struct: `FromBytes`, `TryWriteBytes`, `Oui`, `IsMulticast`, `IsLocallyAdministered`, parse `:`/`-`/12 hex, format `00:1A:2B:3C:4D:5E`, JSON via `DeviceValueJsonConverter<T>` | parse/format/equality/JSON come from the Devices generator → in core they are written by hand (⚑N3). Used by Devices tests (`AllocationTests.cs:19,45`, `JsonTests.cs:12,29`, `ValueObjectTests.cs:119–136`), AotSmoke `Program.cs:26`, benchmarks |
| `YFex.Devices/src/YFex.Devices/Serial/Tcp/TelnetChannel.cs` (366 lines) | `internal` `Telnet` constants, `ITelnetHandler`, `TelnetChannel` (IAC escaping, RFC 1143-subset option negotiation, sub-negotiation framing, ordered control queue) over `IByteTransport`; uses `ArrayPoolBufferWriter` (core) and `AsyncSignal` (Devices `Internal`) | consumers: `Rfc2217.cs:154–165`, `Rfc2217SerialPort.cs:43–401` (⚑N5) |
| `YFex.Devices/src/YFex.Devices/Serial/Tcp/` (`TcpByteTransport`, `TcpAcceptLoop`, `TcpBridge`, `Rfc2217Server`) | TCP transports and servers for serial sharing | stay in Devices (device sharing); may use `FreePorts`/`InterfaceSelector` later |
| `YFex.Security/YFex.Security.Windows/Interop/IphlpapiInterop.cs:48–54`, `Network/NetworkInspector.cs` | `GetExtendedTcpTable/UdpTable` → connections per process (`INetworkInspector`) | host security monitoring; **not** moved (different purpose) |
| `YFex.Security/.../Capture` (`EtwCaptureService`, SharpPcap + JA3) | passive DNS/TCP/TLS capture | packet capture stays here (⚑N9) |
| `YFex/src/System/YFex.System.Windows.Security/` | an older copy of `YFex.Security.Windows` (Interop, Network, …) | **not in `yfex.slnx`**; stale duplicate, out of scope (flag only) |

## 2. Network code that is only planned

| Plan | What | Now |
|---|---|---|
| Devices plan (`C:\Users\thiag\.claude\plans\tender-sauteeing-frog.md`) §1 table, §13, phase 11; `YFex.Devices/docs/01-architecture.md` layer 3 `Network`; `YFex.Devices/README.md:3` "LAN discovery" | mDNS/DNS-SD, SSDP/UPnP, ARP/ICMP sweep, OUI lookup, 9100 printer + Modbus TCP discovery | generic parts → here (⚑N2); 9100/Modbus discovery stays in Devices, built on Networking |
| Devices plan §12 Remoting | agents discovered through `Network` mDNS; `SslStream` + PSK/certificate | mDNS from here; identity/TLS trust → YFex.Installations (⚑N6) |
| Offline 04 §1 | core `IConnectivity { Mode, Reachability(Central, SiteBroker, SiteBrokerReachesCentral) }`, `PeerHealth` | composes `IServerConnection` + `INetworkConditions`; `PeerHealth` → core `EndpointHealth` (⚑N10) |
| Offline 04 §2 (⚑O6) | 120-byte signed UDP multicast beacon every 5 s; "if Devices LAN discovery lands first the beacon becomes one of its announcement kinds" | the beacon becomes a `Beacons` announcement kind here ([03](03-discovery.md) §2) |
| Offline 04 §4 | hysteresis table (3 failed probes 5 s apart, 30 s healthy to go up, doubling to 10 min, 60 s dwell) | `Hysteresis` (⚑N23) |
| Offline 10 rows 26–27 | firewall blocks the broker port; Wi-Fi client isolation blocks multicast | `FirewallRules` presence check + diagnosis hints ("multicast not received on Wi-Fi") |
| Logging 05 §6, 06, 11 | `OnlyOnUnmeteredFor(LogLevel.Debug)`, bandwidth limit, "metered aware" device profile | reads core `INetworkConditions.Current.Cost` |
| Time §8 / Offline Phase 4 | peers compared through LAN beacons (evidence only) | beacons carry `TimeReply` bytes as payload; Networking does not interpret them |

## 3. Live defects found while reading

| # | Defect | Where | Fix (phase) |
|---|---|---|---|
| D1 | `NetworkStatusChangedEvent.Previous` is **guessed** (`state == Connected ? Disconnected : Connected`): `Disconnected → Reconnecting` is reported as `Connected → Reconnecting` | `OutboxReplayer.cs:83–84` | `IServerConnection.Changed` carries `(Previous, Current)` (phase 1) |
| D2 | `SyncState.Syncing` is never produced | `INetworkStatus.cs:3` | dropped from `ServerConnectionState`; sync progress is `SyncStatus`'s job (phase 1) |
| D3 | `NetworkStatusProvider.Configure` is called but `Current` is never read | `RpcMessagingExtensions.cs:79,171` | `[Obsolete]`, no replacement (phase 1) |
| D4 | `FusionNetworkStatus.MonitorAsync` swallows every exception with a bare `catch` and loops: if `WhenNext` keeps throwing (disposed hub), it spins | `FusionNetworkStatus.cs:46–48` | catch, log once, delay before retrying; stop on `ObjectDisposedException` (phase 1) |
| D5 | Time resyncs only on `NetworkAvailabilityChanged(true)`: moving from cable to Wi-Fi/4G never makes availability false, so no resync on a new network | `TimeSynchronizationService.cs:116–119` | subscribe to `INetworkConditions.Changed` when registered, else also `NetworkAddressChanged` (phase 2) |
| D6 | MAC "pseudonym" is `SHA256(mac)` with no key; reversible by enumerating ~2⁴⁰ values per vendor prefix | `WindowsSystemInfoProvider.cs:51` | already in the privacy plan (05 §3); Networking provides `MacAddress` + mask, not the pseudonymizer (⚑N22) |

## 4. What .NET 11 gives us (ref pack `11.0.0-preview.4.26230.115`)

| Need | BCL | Gap we fill |
|---|---|---|
| adapters, addresses, gateways, DNS servers, DHCP, MTU, speed, type | `NetworkInterface`, `IPInterfaceProperties` | one immutable snapshot, primary adapter choice, diff, debounce |
| change events | `NetworkChange.NetworkAddressChanged/NetworkAvailabilityChanged` (Windows `NotifyAddrChange`, Linux netlink) | storms during DHCP → 500 ms debounce, typed differences |
| CIDR | `System.Net.IPNetwork` (.NET 8+: `Parse`, `Contains`) | scope classification (private, CGNAT, link-local, ULA, documentation…), broadcast address, host count |
| ICMP | `Ping` (Linux: unprivileged ICMP socket or `ping` binary fallback), `PingOptions.Ttl` | traceroute, rate limit, union results |
| DNS | `Dns.GetHostAddressesAsync` — **no SRV/TXT/PTR** (checked: `System.Net.NameResolution.xml`) | RFC 1035 wire client (also the mDNS codec) |
| TLS | `SslStream`, `SslStream.NegotiatedCipherSuite`, `X509Chain` | endpoint report + "your clock is wrong" explanation |
| proxy | `HttpClient.DefaultProxy` (Windows: WinHTTP incl. PAC/WPAD; Linux: environment variables) | per-URI route + 407 detection |
| multicast | `UdpClient.JoinMulticastGroup`, `Socket.SetSocketOption(MulticastInterface)` | beacons, mDNS, SSDP, WS-Discovery |
| pipelines | `System.IO.Pipelines` is in the shared framework | used by Telnet/SNMP framing if helpful |
| QUIC | `System.Net.Quic` (`QuicConnection.IsSupported`; msquic) | notes only (⚑N25) |
| metered/cost, NCSI, Wi-Fi, ARP table, firewall | **none** (0 matches for cost/metered in `System.Net.NetworkInformation.xml`) | `.Windows` (NLM COM, WLAN API, iphlpapi), Linux `/proc` |
| Wake-on-LAN, SNMP, mDNS, SSDP, Telnet, STUN | none | ours |

AOT notes: COM (NLM, firewall) through `[GeneratedComInterface]` + `StrategyBasedComWrappers`; P/Invoke through `LibraryImport` with
blittable signatures and `[assembly: DisableRuntimeMarshalling]` (same rules as `YFex.Devices/docs/01-architecture.md`). No
reflection, no `Enum.ToString`, JSON source generation only.
