# 05 — Consumers and migration

Every existing file changed by this work is backed up first to `C:\Temp\networking-backup\<same relative path>`. Builds use
`--artifacts-path C:/Temp/networking-artifacts` (other sessions build the same projects).

## 1. YFex.Devices (⚑N2) — docs updated 2026-10-05

| Before (plan §1/§13/phase 11, `docs/01-architecture.md`) | After |
|---|---|
| core namespace `Network` (mDNS, SSDP, ARP/ICMP sweep, OUI, 9100 + Modbus TCP discovery) | **removed**. Generic parts are YFex.Networking; the subnet sweep is the survey package ([04](04-survey-and-safety.md)) |
| `YFex.System` referenced "for adapter snapshots" in `Network` | not needed: `LocalNetwork` (YFex.Networking) |
| phase 11 "Network, then Bluetooth" | phase 11 "**Device discovery on YFex.Networking** (9100 printers, Modbus TCP, remoting agents via mDNS), then Bluetooth" |
| Remoting §12 "agents are discovered through `Network` mDNS"; `SslStream` + PSK/certificate | mDNS from YFex.Networking; identity/trust from YFex.Installations (⚑N6) |
| `MacAddress` in `YFex.Devices` | YFex core `YFex.Networking.MacAddress` (§2) |
| `TelnetChannel` internal in `Serial/Tcp` | YFex.Networking `Telnet` (§4); `Rfc2217*` stay |

Devices keeps: `Rfc2217`, `Rfc2217SerialPort`, `Rfc2217Server`, `TcpByteTransport`, `TcpAcceptLoop`, `TcpBridge`; printer status over
9100 (ESC/POS DLE EOT); Modbus TCP; Printer-MIB/UPS-MIB helpers on the SNMP client (⚑N17). The layering test map loses `Network`.

## 2. `MacAddress` → YFex core (⚑N3, ⚑N16)

```csharp
// YFex/src/YFex/Networking/MacAddress.cs — hand-written (core has no Devices generator, no Privacy generator)
[Sensitive(DataSensitivity.Personal, Category = "identification", Mask = "##:##:##:**:**:**")]
[JsonConverter(typeof(MacAddressJsonConverter))]
public readonly struct MacAddress : IEquatable<MacAddress>, IComparable<MacAddress>, ISpanFormattable, IUtf8SpanFormattable,
    ISpanParsable<MacAddress>, IUtf8SpanParsable<MacAddress>, IClassifiedData<MacAddress>
{
    public const int MaxFormattedLength = 17;
    public MacAddress(ulong value);                    // low 48 bits; throws when larger (same as today's generated validation)
    public ulong Value { get; }
    public static MacAddress FromBytes(ReadOnlySpan<byte> bytes);
    public bool TryWriteBytes(Span<byte> destination);
    public static MacAddress FromPhysicalAddress(PhysicalAddress address);   // BCL interop (NetworkInterface)
    public PhysicalAddress ToPhysicalAddress();
    public uint VendorPrefix { get; }                  // was Oui (no abbreviations; docs say "OUI")
    public bool IsMulticast { get; }
    public bool IsBroadcast { get; }                   // FF:FF:FF:FF:FF:FF
    public bool IsLocallyAdministered { get; }         // randomized phone MACs, VMs
    // formats: "" / "C" = 00:1A:2B:3C:4D:5E, "D" = 00-1A-2B-3C-4D-5E (Windows), "N" = 001A2B3C4D5E; lower-case "c"/"d"/"n"
    // parse: ':' or '-' separated, 12 contiguous hex digits, Cisco "001a.2b3c.4d5e"; UTF-16 and UTF-8; never throws in TryParse
    // ==, !=, <, <=, >, >=, explicit (ulong) both ways — same surface as today's generated code
}
```

Devices changes: delete `MacAddress.cs`, add `global using YFex.Networking;` (or per-file usings) in the core project, tests,
AotSmoke and benchmarks; `.Oui` → `.VendorPrefix` (one test). JSON stays a string in the same format. Acceptance: Devices tests,
allocation tests (0 B parse/format) and AotSmoke unchanged.

## 3. `INetworkStatus` → `IServerConnection` (⚑N4, ⚑N13)

> **2026-10-07 (user):** the `[Obsolete]` names and bridges below were removed after Phase 1 — nothing used them. The table records
> what Phase 1 did.

**Coordination first:** the Claude session "Authorization consolidation phase 3" owns Messaging.Rpc `CallContext`. Before editing,
`ListAgents` + `SendMessage` with the file list below and wait for an answer.

```csharp
namespace YFex.Messaging.Rpc;

public enum ServerConnectionState : byte { Disconnected = 0, Reconnecting = 1, Connected = 2 }
public readonly record struct ServerConnectionChange(ServerConnectionState Previous, ServerConnectionState Current);

/// <summary>Is our RPC connection to the server up? (Not "is there internet": that is YFex.Networking's INetworkConditions.)</summary>
public interface IServerConnection
{
    ServerConnectionState State { get; }
    bool IsConnected => State == ServerConnectionState.Connected;
    event Action<ServerConnectionChange>? Changed;
}
public sealed class AlwaysConnectedServer : IServerConnection { public static AlwaysConnectedServer Instance { get; } }

[Obsolete("Use IServerConnection (YFex.Networking ⚑N4). Removed in the next release.")] public interface INetworkStatus { … unchanged … }
[Obsolete] public enum SyncState { … unchanged … }
[Obsolete] public sealed class AlwaysConnectedNetworkStatus;     // implements both
[Obsolete("Never read; no replacement.")] public static class NetworkStatusProvider;   // D3
```

| File | Change |
|---|---|
| `FusionNetworkStatus.cs` → `FusionServerConnection.cs` | implements `IServerConnection`; reports the real previous state (D1); stops spinning on repeated errors (D4) |
| `RpcMessagingExtensions.cs` | registers `IServerConnection`, and `INetworkStatus` as an `[Obsolete]` adapter over it (old consumers keep working) |
| `LocalDispatcher.cs`, `OutboxReplayer.cs`, `FusionMessageBus.cs`, `Time/CentralTimeSource.cs`, Rules.Admin `RuleSetSync` | take `IServerConnection`; public constructors taking `INetworkStatus` stay one release as `[Obsolete]` overloads (Rules.Admin, EventSourcing and tests construct them) |
| `SyncEvents.cs` | new `ServerConnectionChangedEvent(Previous, Current)`; `NetworkStatusChangedEvent` `[Obsolete]`, still published (correct previous) |
| test fakes (Messaging.Tests `ManualNetworkStatus`, Rpc.Tests ×3, Rules.Admin.Tests, EventSourcing.Tests, EventSourcing AotSmoke) | move to `IServerConnection` (`ManualServerConnection`); the obsolete path keeps one test |
| docs: `docs/internal/messaging-internals.md`, `docs/public/messaging.md`, `src/Messaging/docs/03-rpc-and-offline.md`, offline 04 §1, validation 13 | rename + one line on `INetworkConditions` |

Acceptance: Messaging.Tests, Messaging.Rpc.Tests, Rules.Admin.Tests, EventSourcing.Tests counts unchanged + new tests for D1/D4;
0 new warnings except the intended `CS0618` in the one obsolete-path test (suppressed locally).

## 4. Telnet (⚑N5, ⚑N12)

Move `Telnet`/`ITelnetHandler`/`TelnetChannel` to `YFex.Networking.Telnet` (public), constructor takes `Stream` instead of
`IByteTransport`; `AsyncSignal` copied as internal; constants become `TelnetCommands`/`TelnetOptions` (Devices keeps `OptionComPort = 44`
in `Rfc2217`). Devices: `Rfc2217SerialPort` creates `new TelnetChannel(transport.AsStream(leaveOpen: false), this, …)` and keeps
its own `IByteTransport` reference where it used `_channel.Transport`; `YFex.Devices` references `YFex.Networking`. Acceptance:
Devices RFC 2217 tests (in-memory stream, ser2net-compatible) pass unchanged; Telnet golden tests (IAC escaping, negotiation loops,
sub-negotiation limits 1 024 bytes) move with the code.

**Done 2026-10-14 (phase 6).** Devices had no Telnet or RFC 2217 tests to move: the golden Telnet tests were written in
YFex.Networking.Tests and a new Devices `Serial/Rfc2217Tests` runs client ↔ server over loopback TCP. Types stayed in namespace
`YFex.Networking` (the repo's convention), and `Rfc2217SerialPort` handles the telnet callbacks through a private class.

## 5. YFex.System `NetworkSnapshot` (⚑N22)

No change in this plan: `CaptureNetworkSnapshot()` keeps its shape and Profiling keeps working. Two follow-ups: (1) the unsalted
`SHA256(mac)` (D6) is replaced by the Privacy pseudonymizer when it exists (privacy 05 §3), using `MacAddress` for parsing;
(2) optionally the Windows provider builds its adapters from `LocalNetwork` later (then the code also works on Linux).

## 6. YFex.Time (D5)

`TimeSynchronizationService` takes an optional `INetworkConditions` (core) from DI: resync when `Internet` becomes `Internet` or
`Medium` changes. Without it, it also subscribes to `NetworkChange.NetworkAddressChanged` (debounced) in addition to today's
availability event. YFex.Time does **not** reference the YFex.Networking repo.

## 7. YFex.Offline `IConnectivity`

Offline 04 §1 stays as designed, composed of:

```csharp
Reachability(
    Central:    serverConnection.IsConnected ? EndpointHealth.Healthy : endpointMonitor["central"].Health,
    SiteBroker: brokerHysteresis.State,                                  // Hysteresis from YFex.Networking (⚑N23)
    SiteBrokerReachesCentral: lastHealthReply.BrokerReachesCentral);
// + network.Current (core INetworkConditions) explains WHY: NoNetwork / LocalNetworkOnly / CaptivePortal
```

`PeerHealth` in offline 03/04 becomes core `EndpointHealth` (⚑N10). The site broker beacon becomes the `yfex.installation` beacon kind
(role in the payload, owned by YFex.Installations; [03](03-discovery.md) §2). Offline 10 rows 26–27 gain two diagnosis steps (firewall rule present, multicast received on this adapter).
These doc edits happen when the offline Phase 1 starts (that design is still a proposal).

## 8. Logging / Audit relays

Logging's `OnlyOnUnmeteredFor(level)` and bandwidth limits read `INetworkConditions.Current.Cost` (core). Relays (offline ⚑O19: the
broker is the relay) find the broker through Offline; nothing network-specific is needed in Logging/Audit.

## 9. Messaging.Rpc (optional, later)

- Reconnect hint: when `INetworkConditions` changes to `Internet`, nudge the Fusion client to reconnect now instead of waiting for its
  back-off.
- Multi-homing: `InterfaceSelector.CreateConnectCallback` through `WebSocketOwnerFactory` for terminals with Ethernet + 4G.
