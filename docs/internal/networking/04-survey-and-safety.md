# 04 — The survey package and safety

**Goal:** benign code stays benign and is shipped to every terminal; the few features that security products reasonably treat as
reconnaissance live in a separate, opt-in package that only admin/support tools reference, where scanning is authorized. This is
**not** about hiding anything from antivirus/EDR: the survey package is loud by design (documented, rate-limited, logged, audited,
signed, identifiable on the wire).

## 1. Name (⚑N8)

| Option | For | Against |
|---|---|---|
| **`YFex.Networking.Survey`** + `YFex.Networking.Windows.Survey` (recommended) | neutral; matches the real use ("site survey" by support before installing a store: which printers, PIN pads, router, PCs are on this LAN) | — |
| `Probing` | literal | reads like attack tooling; the main package also "probes" |
| `Elevated` | honest when everything needs admin | most of it does **not** need admin (TCP connect, SNMP, NetBIOS) |
| `Inventory` | asset inventory | clashes with the Devices `Inventory` namespace |

## 2. What stays in the main package (and why it is safe there) — ⚑N24

| Feature | Why benign |
|---|---|
| Ping one host or a short list (≤ 32, local scope for lists) | what `ping` does; within the process budget (10 probes/s) |
| TCP connect to known host:port pairs | what any client does before talking; full handshake, then close |
| ARP/NDP table **read** | reads the OS cache; sends nothing (`populate` = one ping to one host) |
| mDNS/SSDP/WS-Discovery browse, beacons | the same multicast the OS and every printer already send |
| Reachability/quality probes, traceroute to one host | standard diagnostics, budgeted |
| SNMP get/walk on **one** host | what printer drivers do |
| Wake-on-LAN | one broadcast datagram |
| Firewall-rule presence read | read-only |

The budget is enforced in code (one token bucket per process), so a bug or a misuse in an app cannot turn the main package into a
scanner. Multi-target calls reject non-local addresses (`ArgumentException` naming the rule).

## 3. What goes to the survey package

| Feature | Package | Notes |
|---|---|---|
| Subnet sweep: ICMP echo + TCP connect to a short port set, over a local subnet | `.Survey` | ≤ /22 (1 024 hosts), local scope only |
| Port-list scan of one or more hosts (> 8 ports per host) | `.Survey` | full connect only, never half-open/SYN |
| SNMP get/walk across a subnet (sysDescr, sysName, printer/UPS identification) | `.Survey` | v2c community list given by the operator, no guessing lists |
| NetBIOS name query (NBSTAT, UDP 137) across a subnet | `.Survey` | names of old Windows PCs/printers |
| Site survey report: sweep + ARP + vendors + mDNS/SSDP/WSD + SNMP + TLS of discovered HTTPS → `SiteSurveyReport` (assets, kinds, vendors, open services) | `.Survey` | the support tool's main use |
| ARP sweep (`SendARP` per address) | `.Windows.Survey` | finds hosts that block ICMP |
| SMB share enumeration (`NetShareEnum`) of discovered hosts | `.Windows.Survey` | read-only listing, current user's credentials only |

### 3.1 Guardrails (all enforced in code)

```csharp
// support tool only (never a POS app)
services.AddYFexNetworkSurvey(survey => survey.RecordWith<MyAuditSink>());   // the package's .targets fails the build unless
                                                                             // <YFexNetworkSurvey>true</YFexNetworkSurvey> is set
SurveyAuthorization authorization = SurveyAuthorization.Create(
    requestedBy: "suporte.ana", reason: "chamado 48213 — instalar 2 caixas novos", scope: IPNetwork.Parse("192.168.0.0/24"),
    validFor: TimeSpan.FromHours(2));
SiteSurveyReport report = await survey.RunAsync(authorization, SurveyPlan.Standard, progress, cancellationToken);
```

| Guardrail | Rule |
|---|---|
| Explicit opt-in | the package refuses to build without the MSBuild property; `AddYFexNetworkSurvey` must be called |
| Authorization object | every run needs `SurveyAuthorization` (who, why, scope, expiry); expired or out-of-scope → refused |
| Scope | local scope only (private, link-local, CGNAT, ULA) and only networks this machine is on-link to, ≤ /22; public ranges always refused |
| Rate | default 20 probes/s, max 100; ≤ 4 concurrent per host; randomized order **not** used (no evasion), steady pacing |
| Audit | `SurveyStarted`/`SurveyFinished` records (authorization, plan, counts, duration) to an `ISurveyAuditSink` (YFex.Audit adapter later) and to the log; a run without a sink is refused |
| Identification | HTTP probes send `User-Agent: YFex-Survey/1 (+authorized site survey)`; SNMP/NetBIOS are standard requests; no spoofed source, no decoys, no fragmentation tricks, no timing evasion |
| Credentials | only what the operator types for this run (SNMP communities, SMB uses the current Windows user); no brute force, no default-password lists |
| Distribution | separate NuGet packages, signed; never referenced by YFex.Farma/Fiscal POS apps (architecture test in their solutions) |

## 4. Packet capture stays in YFex.Security (⚑N9)

`YFex.Security.Capture` already has ETW capture, raw packet capture (SharpPcap) and JA3, with its own authorized-use rules
(`docs/public/security.md`). A `Networking.Capture` package would duplicate it. Networking never opens raw sockets or promiscuous mode.
If a support tool needs "capture 30 s of traffic to the printer", it references YFex.Security.

## 5. Never, in any package

ARP/NDP/DHCP/DNS spoofing or poisoning; credential brute force or default-credential lists; exploits or vulnerability checks that
send attack payloads; stealth scans (SYN half-open, FIN/XMAS/NULL, idle scans), decoys, randomized timing to avoid IDS, fragmentation
to evade inspection; anything that disables, evades or tests antivirus/EDR; changing firewall, proxy, DNS, adapters or Wi-Fi settings.
