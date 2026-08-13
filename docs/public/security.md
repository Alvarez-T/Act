# Security & Network Intelligence

**Libraries:** `YFex.Security`, `YFex.Windows.Security`, `YFex.Security.Storage`, `YFex.Security.Capture`, `YFex.Security.Analysis`, `YFex.Security.Service`, `YFex.Security.Cli`
**Personal-use add-ons:** `YFex.Security.Hooking`, `YFex.Security.Proxy`, `YFex.Security.ApiReverser`

## Overview

The Security layer is a host-based **network & process intelligence toolkit**. It passively observes what your machine is doing — DNS lookups, TCP connections, TLS handshakes, process and module activity — normalizes everything into a single typed event stream, persists it to SQLite, builds behavioural baselines, and raises anomalies when something deviates from normal.

Three add-on libraries (clearly isolated, **personal use only**) go a step further and capture *application-layer* traffic — plaintext HTTP, WebSocket frames, gRPC/protobuf — by hooking TLS functions (Frida) or running a TLS-terminating proxy (mitmproxy), then reverse-engineer the observed APIs into documentation and a generated C# client.

```
                       ┌──────────────────────────────────────────────┐
  capture sources ───► │   ISecurityPipeline  (bounded channel)        │ ───► EventPersister ──► SQLite
   ETW / pcap / hook /  └──────────────────────────────────────────────┘            │
   proxy / HAR import              ▲                     │                            ▼
                                   │                     └──► AnomalyDetector ──► anomalies table
                          ProcessSocketCorrelator                                     │
                                                                          BaselineBuilder / ReportGenerator
```

Everything is built on `net11.0` / `net11.0-windows`, targets AOT-friendly patterns, and reuses `YFex.System` (the `ObservableSource<T>` event primitive and config conventions). See [System & Platform](system.md) for that foundation.

> **Authorized use only.** Passive observation of your own machine is the default. The hooking, proxy, and API-reversing add-ons are powerful and intended strictly for software you own or are authorized to test. They are deliberately separated into their own libraries with no upstream dependents.

---

## Project Map

| Project | TFM | Responsibility |
|---|---|---|
| **YFex.Security** | `net11.0` | Abstractions: event model, pipeline, config, promoted platform interfaces |
| **YFex.Windows.Security** | `net11.0-windows` | Windows implementations: process/network inspectors, file scanner/watcher, AMSI, Defender, registry & persistence detection, integrity checks, input hooks |
| **YFex.Security.Storage** | `net11.0` | SQLite schema + migrations, Dapper repositories, the pipeline→DB persister |
| **YFex.Security.Capture** | `net11.0-windows` | ETW consumer, raw packet capture (SharpPcap) + JA3, process-socket correlation |
| **YFex.Security.Analysis** | `net11.0` | Baseline builder, anomaly detector, report generator |
| **YFex.Security.Service** | `net11.0-windows` | Generic-host composition root that wires everything together |
| **YFex.Security.Cli** | `net11.0-windows` | `yfex-security` command-line tool |
| **YFex.Security.Hooking** *(personal)* | `net11.0-windows` | Frida orchestrator + JS hook scripts (pre-TLS plaintext capture) |
| **YFex.Security.Proxy** *(personal)* | `net11.0-windows` | mitmproxy subprocess manager + Python capture addon |
| **YFex.Security.ApiReverser** *(personal)* | `net11.0` | HTTP/WS/protobuf analysis, auth-flow extraction, C# client codegen, HAR import |

---

## The Event Model

Every observation — regardless of source — implements `ISecurityEvent`:

```csharp
public interface ISecurityEvent
{
    long Id { get; }
    DateTimeOffset Timestamp { get; }
    int ProcessId { get; }
    string ProcessName { get; }
    SecurityEventType EventType { get; }
    string ToJson();
}
```

Concrete event records (all in `YFex.Security.Events`):

| Record | Captures | Key fields |
|---|---|---|
| `TcpConnectEvent` | TCP connect/disconnect | local/remote address & port, protocol |
| `DnsQueryEvent` | DNS lookups | query name, type, resolved addresses |
| `TlsHandshakeEvent` | TLS metadata | SNI, cipher, version, **JA3/JA3S**, cert subject/issuer/expiry |
| `HttpCaptureEvent` | full HTTP request+response | method, url, headers, bodies, status, duration, `CaptureSource` |
| `WebSocketFrameEvent` | WS frames | url, direction, opcode, text/binary payload |
| `GrpcCallEvent` | gRPC calls | service, method, request/response protobuf + decoded |
| `ProcessSecurityEvent` | process/module lifecycle | parent PID, image path, command line, signed flag, signer |
| `AnomalyEvent` | analysis output | `AnomalyType`, severity, description, related event JSON |

`SecurityEventType` enumerates every category (network, TLS, pre-encryption capture, process, file I/O, and generated anomaly types).

### The pipeline

All sources push into one bounded channel; all consumers read from it.

```csharp
public interface ISecurityPipeline
{
    ChannelWriter<ISecurityEvent> Writer { get; }
    ChannelReader<ISecurityEvent> Reader { get; }
}
```

The default `SecurityPipeline` is a bounded channel (capacity 10 000) in `DropOldest` mode — under sustained overload it sheds the oldest events rather than blocking a capture thread. `ObservablePipelineBridge<T>` adapts any `IObservable<T>` (e.g. the Windows watchers) into the pipeline with a converter delegate.

---

## What Information Can You Get?

### Passive, always-on (no add-ons)

| Source | Produces | Mechanism | Requires |
|---|---|---|---|
| **ETW** (`EtwCaptureService`) | DNS queries, TCP connects, process/module events | 4 ETW providers (DNS-Client, Kernel-Network, Kernel-Process, TCPIP) | **Admin** |
| **Packet capture** (`PcapCaptureService`) | TLS handshakes with **JA3 fingerprints** + SNI | SharpPcap/Npcap, parses TLS ClientHello | **Npcap installed**, opt-in |
| **Process inspector** | running processes, modules, signing | `YFex.Windows.Security` | — |
| **Network inspector** | TCP connections & UDP endpoints with owning PID | `GetExtendedTcpTable`/`UdpTable` | — |
| **File scanner/watcher** | file hashes, signature verification, threat level, FS change events | `YFex.Windows.Security` | — |
| **Registry/persistence** | autostart entries, registry change events | `YFex.Windows.Security` | — |
| **System integrity** | Secure Boot, BitLocker, UAC, firewall, Windows Update posture + score | `SystemIntegrityChecker` | — |
| **AMSI / Defender** | on-demand malware scan, AV product status | `YFex.Windows.Security` | — |

### Application-layer (personal-use add-ons)

| Source | Produces | Mechanism |
|---|---|---|
| **Frida hooks** | plaintext HTTP requests, WebSocket frames, raw pre-TLS buffers | hooks SChannel/OpenSSL `Encrypt/Decrypt`/`SSL_write/read` |
| **mitmproxy** | full HTTP flows + WS messages (TLS-terminated) | `mitmdump` subprocess + Python addon |
| **HAR import** | HTTP/WS events from a browser DevTools export | `HarImporter` |
| **API reverser** | endpoint catalog, auth-flow record, protobuf trees, generated C# client | analyzers over captured events |

### Generated by analysis

`AnomalyEvent`s from 8 detection rules (see [Analysis](#analysis--baselines--anomalies--reports)), plus Markdown reports (daily summary, process profile, service API report, security audit).

---

## Storage — What Gets Persisted

`YFex.Security.Storage` owns a SQLite database (WAL mode, default `%LocalAppData%\YFex\security.db`). Migrations are embedded SQL resources applied at startup. Tables:

`events` (the canonical JSON log) plus denormalized, indexed tables: `dns_queries`, `connections`, `http_captures`, `ws_frames`, `tls_handshakes`, `process_events`, `anomalies`, `baselines`, `api_endpoints`, `ws_protocols`.

Three repositories provide typed access:

```csharp
public interface IEventRepository
{
    Task InsertEventAsync(ISecurityEvent evt, CancellationToken ct = default);
    Task InsertBatchAsync(IReadOnlyList<ISecurityEvent> events, CancellationToken ct = default);
    Task<IReadOnlyList<ISecurityEvent>> QueryEventsAsync(EventQuery query, CancellationToken ct = default);
    Task PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
// + IBaselineRepository, IApiCatalogRepository
```

`EventQuery` filters by time range, process, event type, remote address, or domain, with paging. The `EventPersister` drains the pipeline and writes in batches (100 events or every 500 ms), fanning out to the denormalized tables by event type.

---

## Analysis — Baselines, Anomalies & Reports

### Baselines (`BaselineBuilder`)

Builds 5 behavioural baselines from the last *N* days (default 14):

1. **domain_per_process** — which domains each process normally contacts
2. **ja3_per_process** — TLS fingerprints normally seen per process (a change can mean injection)
3. **hourly_volume_per_process** — typical activity per hour
4. **listening_ports** — ports normally in use
5. **network_processes** — which processes normally touch the network

### Anomaly detection (`AnomalyDetector`, a `BackgroundService`)

Reads the live pipeline and applies 8 rules, emitting `AnomalyEvent`s back onto the pipeline:

| # | Rule | Severity |
|---|---|---|
| 1 | **NewDomain** — known process contacts an unseen domain | Medium |
| 2 | **UnexpectedNetworkAccess** — a non-network process opens a connection | High |
| 3 | **Ja3Mismatch** — process's TLS fingerprint changed | High |
| 4 | **UnsignedModuleLoad** — unsigned DLL into a signed process | Medium |
| 5 | **RemoteThreadInjection** — remote thread created | Critical |
| 6 | **UnusualHour** — activity at an hour the process is never active | Low |
| 7 | **ByteVolumeSpike** — traffic far above baseline | Medium |
| 8 | **DnsExfiltration** — over-long / base64-ish / deeply-nested DNS names | High |

Allowlists (`AllowedDomains`, `KnownNetworkProcesses` in config) suppress rules 1 and 2.

### Reports (`ReportGenerator`)

Four Markdown reports:

```csharp
Task<string> GenerateDailySummaryAsync(DateOnly date);
Task<string> GenerateProcessProfileAsync(string processName);
Task<string> GenerateServiceApiReportAsync(string serviceName);
Task<string> GenerateSecurityAuditAsync(DateTimeOffset from, DateTimeOffset to);
```

---

## Configuration

`SecurityConfig` (in `YFex.Security.Config`) controls paths, capture toggles, retention, and allowlists:

```csharp
public sealed class SecurityConfig
{
    public string DatabasePath { get; set; } = @"%LocalAppData%\YFex\security.db";
    public string LogDirectory { get; set; } = @"%LocalAppData%\YFex\logs";
    public string FridaPath { get; set; } = "frida";
    public string MitmdumpPath { get; set; } = "mitmdump";
    public int ProxyPort { get; set; } = 8080;
    public int RetentionDays { get; set; } = 90;
    public bool EnableDnsCapture { get; set; } = true;
    public bool EnableProcessCapture { get; set; } = true;
    public bool EnableFileIoCapture { get; set; } = false;
    public bool EnableTlsCapture { get; set; } = true;
    public int BaselineWindowDays { get; set; } = 14;
    public HashSet<string> KnownNetworkProcesses { get; set; } = [];
    public HashSet<string> AllowedDomains { get; set; } = [];
}
```

The service loads it from `%LocalAppData%\YFex\security-config.json` via `SecurityConfigLoader` (falls back to defaults if absent). Logs go through Serilog to a daily rolling file in `LogDirectory`.

---

## Using It — The CLI

`YFex.Security.Cli` builds the `yfex-security` executable. It shares the exact same composition root (`SecurityHostBuilder`) as the background service, so the CLI and service resolve identical singletons.

```text
# Service control
yfex-security start [--pcap]        # run the capture service (admin; --pcap adds packet capture)
yfex-security status                # events & anomalies captured today

# Query the database
yfex-security query events  [--process N] [--type T] [--after DT] [--before DT] [--limit N]
yfex-security query connections [--process N] [--remote IP] [--limit N]
yfex-security query dns     [--domain PATTERN] [--process N] [--limit N]

# Reports (Markdown to stdout or --output FILE)
yfex-security report daily   [--date YYYY-MM-DD]
yfex-security report process <name>
yfex-security report api     <service>
yfex-security report security [--from DT] [--to DT]

# Baselines & anomalies
yfex-security baseline rebuild [--type domain_per_process|ja3_per_process|all]
yfex-security anomalies [--severity critical|high|medium|low] [--limit N]

# Allowlists & maintenance
yfex-security allowlist domain  add <domain>     |  list
yfex-security allowlist process add <name>       |  list
yfex-security purge [--older-than 90d]
```

> **Note:** read commands (`query`, `report`, `baseline`, `anomalies`, `purge`) run database migrations on entry, so they work against a fresh DB without first starting the service.

### Quick start

```bash
# 1. Start capturing (run an elevated terminal — ETW needs admin)
yfex-security start --pcap

# 2. ...use your machine for a while, then in another terminal:
yfex-security status
yfex-security query dns --limit 20
yfex-security baseline rebuild
yfex-security report daily
yfex-security anomalies --severity high
```

---

## Using It — Embedding in Your Own Host

`SecurityHostBuilder.Create` returns a configured `IHostBuilder` you can run directly or extend:

```csharp
using YFex.Security.Service;

var config = SecurityConfigLoader.Load();
var host = SecurityHostBuilder.Create(config, enablePcap: false).Build();
await host.RunAsync();
```

It registers, in order: the config + pipeline singletons, storage (`AddYFexSecurityStorage`), the Windows inspectors (`AddYFexSecurity`), capture (`AddYFexSecurityCapture`, plus `AddYFexPcapCapture` when `enablePcap`), analysis (`AddYFexSecurityAnalysis`), the persistence pump, and the startup migration runner.

To compose a subset yourself, the DI extension methods are public:

```csharp
services.AddSingleton(config);
services.AddSingleton<ISecurityPipeline>(new SecurityPipeline());
services.AddYFexSecurityStorage();    // repositories + EventPersister
services.AddYFexSecurity();           // Windows inspectors/watchers (from YFex.Windows.Security)
services.AddYFexSecurityCapture();    // ETW + ProcessSocketCorrelator
services.AddYFexSecurityAnalysis();   // BaselineBuilder, AnomalyDetector, ReportGenerator
```

### Querying directly

```csharp
var repo = host.Services.GetRequiredService<IEventRepository>();
var recent = await repo.QueryEventsAsync(new EventQuery
{
    EventType = SecurityEventType.DnsQuery,
    After     = DateTimeOffset.UtcNow.AddHours(-1),
    Limit     = 100
});
```

---

## Personal-Use Add-Ons

These capture **application-layer plaintext** and are isolated by design. Use them only against software you own or are authorized to test.

### Hooking (Frida)

`FridaOrchestrator` spawns `frida -p <pid> -l <script>` and reads JSON lines from the hook script's stdout, converting them into `HttpCaptureEvent` / `WebSocketFrameEvent` (source = `Hook`).

```csharp
var orchestrator = new FridaOrchestrator(pipeline, config);
orchestrator.Attach(targetPid, HookScript.Auto);   // auto-detects SChannel vs OpenSSL
// ... events now flow onto the pipeline ...
orchestrator.Detach(targetPid);
```

Bundled scripts live in `tools/frida-scripts/`:
- `hook-schannel.js` — Windows SChannel (`EncryptMessage`/`DecryptMessage`) → .NET, Edge, native apps
- `hook-openssl.js` — OpenSSL/BoringSSL (`SSL_write`/`SSL_read`) → Electron/Chromium, Python, curl
- `hook-websocket.js` — parses RFC 6455 frames at the TLS layer
- `strip-pinning.js` — disables cert pinning so a proxy can intercept (the hooked process only)

Requires `pip install frida-tools`.

### Proxy (mitmproxy)

`MitmproxyService` (a `BackgroundService`) runs `mitmdump` with a bundled Python addon (`tools/mitmproxy-addons/capture_addon.py`) that emits one JSON line per HTTP flow / WS message, converted to events (source = `Proxy`). Captures any app that uses the system proxy; cert-pinned apps need `strip-pinning.js` first. Requires `pip install mitmproxy`.

### API Reverser

Turns captured `HttpCaptureEvent`/`WebSocketFrameEvent`s into documentation and code:

| Component | Does |
|---|---|
| `HttpFlowAnalyzer` | clusters concrete paths into `{param}` templates, infers required headers & auth, merges JSON body schemas → `api_endpoints` |
| `WebSocketAnalyzer` | detects framing (Socket.IO/MessagePack/protobuf/JSON), clusters frame types, flags auth/keepalive → `ws_protocols` |
| `ProtobufDecoder` | decodes raw protobuf without a `.proto` into a JSON tree by field number |
| `AuthFlowExtractor` | detects OAuth2/OIDC, cookie, API-key, or custom-token flows |
| `ClientCodeGenerator` | emits a dependency-free C# `HttpClient` wrapper from the catalog |
| `HarImporter` | imports a browser DevTools `.har` into events (source = `DevTools`) |

```csharp
var analyzer = new HttpFlowAnalyzer(apiCatalog);
await analyzer.AnalyzeAndCatalogAsync("my-service", capturedHttpEvents);

var generator = new ClientCodeGenerator(apiCatalog);
await generator.WriteClientFileAsync("my-service", "MyServiceClient.g.cs");
```

---

## External Dependencies

| Tool | Used by | Install |
|---|---|---|
| **Npcap** | `PcapCaptureService` | driver from npcap.com |
| **Frida** | `FridaOrchestrator` | `pip install frida-tools` |
| **mitmproxy** | `MitmproxyService` | `pip install mitmproxy` |

NuGet: `Microsoft.Diagnostics.Tracing.TraceEvent` (ETW), `SharpPcap` + `PacketDotNet` (capture), `Microsoft.Data.Sqlite` + `Dapper` (storage), `Microsoft.Extensions.Hosting` + `Serilog` (service).

---

## Operational Notes & Gotchas

- **Admin is required** for ETW capture; the packet-capture path additionally needs Npcap. Without elevation, `start` will fail to open the ETW session.
- **`YFex.System` shadows `System`.** Inside these assemblies, fully-qualified BCL types need `global::` (e.g. `global::System.Diagnostics.Process`). Prefer top-of-file `using System.X;` directives instead.
- **Retention** is enforced by `purge` / `PurgeOlderThanAsync`; schedule it or run it manually. Default cutoff is `RetentionDays` (90).
- **Sensitive captures.** HTTP/WS captures can contain tokens and cookies. Treat the SQLite file as sensitive; restrict its directory and purge regularly.
- **Build per project.** Build individual `src/Security/<project>.csproj` rather than the whole solution when iterating.

### See also

- [System & Platform](system.md) — the consent, telemetry, and platform foundation this layer builds on.
- [Core Utilities](core.md) — the `[Union]` mechanism used by the event and result types.
