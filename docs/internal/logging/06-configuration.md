# 06 — Configuration

## 1. Fluent DI (composition root only; libraries never configure logging)

```csharp
services.AddYFexLogging(log => log
    // who am I
    .Node(n => n.App("farma-pdv").Version(ThisAssembly.Version).Environment(env.EnvironmentName)
                .Kind(NodeKind.Desktop).InstanceIdFile("%LOCALAPPDATA%/YFex/instance.id"))
    .Profile(LoggingProfile.Device)                     // Server | Desktop | Device | Development | Test  (§3)

    // what to keep
    .MinimumLevel(LogLevel.Information)
    .RecordLevel(LogLevel.Debug)                        // flight-recorder threshold (03 §5)
    .ForKind(OperationKind.Query, k => k.Level(LogLevel.Debug).SampleSuccess(0.10).SlowAfter(TimeSpan.FromMilliseconds(300)))
    .ForKind(OperationKind.Sql,   k => k.SlowAfter(TimeSpan.FromMilliseconds(200)))
    .ForModule("YFex.Data", LogLevel.Warning)
    .ForOperation("Vendas.FinalizarVenda", o => o.Level(LogLevel.Information))   // audit marking moved to YFex.Audit (L20)
    .RateLimit(perEventId: 200, per: TimeSpan.FromSeconds(1))

    // who / tenancy
    .Tenancy(t => t.FromScopeContext().MaxRungs(3).QuotaPerTopTenant(recordsPerMinute: 20_000))
    .Principal(p => p.From<CallContextPrincipalProbe>().Hash())       // hash user ids at rest (07 §3)

    // privacy
    .Redaction(r => r.Policy(DataSensitivity.Personal, Redact.Mask)
                     .Policy(DataSensitivity.SensitivePersonal, Redact.Hash)    // HMAC per tenant key → joinable, not reversible
                     .ScrubExceptionMessages())

    // memory / pressure
    .Buffers(b => b.Slots(2048).SlotBytes(512).OnExhausted(ExhaustionPolicy.DropNew))

    // where
    .WriteTo(w => w
        .Console(c => c.MinLevel(LogLevel.Information).Compact())
        .RollingFile("logs/{app}/{date}.ylog", f => f.MaxFileBytes(64 << 20).KeepDays(14).MaxTotalBytes(1L << 30))
        .Server(new Uri("https://logs.acme.com"), s => s
            .ApiKeyFrom("Logging:ApiKey")                 // per node or per tenant; never logged
            .Batch(maxRecords: 1024, maxDelay: TimeSpan.FromSeconds(2))
            .Spool("spool/", maxBytes: 512L << 20)
            .MaxBytesPerSecond(64 * 1024)
            .ControlChannel())                            // remote level overrides (§4)
        .Otlp(new Uri("http://collector:4318"), o => o.Logs().Traces().Metrics().MinLevel(LogLevel.Warning)))

    // audit
    .Audit(a => a.Durable("audit/").HashChain().Ship())    // 07 §2

    // bridges
    .Bridge(b => b.MicrosoftExtensionsLogging(minLevel: LogLevel.Warning)
                  .Activities("Microsoft.AspNetCore", "System.Net.Http", "Npgsql")
                  .Meters()));
```

- Every builder method has an `IConfiguration` equivalent (§2). Code sets defaults and config overrides them.
- Builders validate eagerly. Bad config throws at startup with a list of all problems (never at first log).
- `AddYFexLogging` calls `Operations.Install(engine)` once per process. A second call in the same process throws unless
  `.AllowReinstall()` is set (tests).

## 2. `appsettings.json` (AOT-safe)

Bound with the **configuration binding source generator** (`EnableConfigurationBindingGenerator=true`, BCL, no reflection) into
`LoggingOptions` (plain POCO). Reloads (`IOptionsMonitor`) recompile the threshold array (copy-on-write swap) without restart.

```json
{
  "YFex": {
    "Logging": {
      "MinimumLevel": "Information",
      "RecordLevel": "Debug",
      "Kinds":   { "cqrs.query": { "Level": "Debug", "SampleSuccess": 0.1, "SlowAfterMs": 300 } },
      "Modules": { "YFex.Data": "Warning" },
      "Operations":     { "Vendas.FinalizarVenda": { "Audit": true } },
      "Server":  { "Url": "https://logs.acme.com", "SpoolMaxBytes": 536870912 },
      "Redaction": { "SensitivePersonal": "Hash", "Personal": "Mask" }
    }
  }
}
```

Names in config are **operation/kind/module names**, never indexes, so they survive releases.

## 3. Profiles (sensible presets, every value overridable)

| Profile | Slots | Emit / record | Sinks | Exhaustion | Notes |
|---|---|---|---|---|---|
| `Server` | 16 384 × 512 B | Information / Debug | file + server + (OTLP) | DropBelow(Warning) | metrics on, recorder on |
| `Desktop` | 4 096 | Information / Debug | file + server (spool) | DropNew | crash hooks, breadcrumbs |
| `Device` (POS, tablet, low RAM/disk) | 2 048 | Warning / Information | spool only, tight caps | DropNew | bandwidth limit, metered aware |
| `Development` | 4 096 | Debug / Trace | console (verbose) + file + optional Aspire dashboard via OTLP | Allocate | templates rendered with colours |
| `Test` | 1 024 | Trace / Trace | Memory (`LogCapture`) | Allocate | synchronous mode: `Flush()` deterministic |

## 4. Dynamic and remote control

| Control | Scope | How | Safety |
|---|---|---|---|
| Level override | global / kind / module / operation | `IOptionsMonitor` reload, or server push | copy-on-write threshold swap |
| **Scoped verbose** ("debug tenant acme/loja7 for 30 min", "trace user joao", "trace session 3fa2") | tenant prefix / principal / session / trace | server push via control channel, or local API `engine.Verbose(OverrideTarget.Tenant, "1=acme/2=loja7", LogLevel.Trace, TimeSpan.FromMinutes(30))` (canonical `ScopeIdentity` text; also `Principal`, `Session`, `Trace`) | **mandatory TTL** (max 24 h), only lowers thresholds, every grant **audited** (who enabled debug for whom) |
| Upload flight recorder now | node | server push | rate-limited |
| Pause shipping / drain | node | server push (maintenance) | Audit lane cannot be paused |

## 5. Quotas and limits (the "limit of logs" requirement)

| Limit | Where | Behaviour when hit |
|---|---|---|
| Records per EventId per second | producer node (consumer thread) | summary record "suppressed N" |
| Records/min per top-level tenant | node and server | node: drop below Warning; server: 429 for that tenant's batches (spool keeps them) |
| Bytes/day per tenant | server | downgrade to Warning+ for that tenant, alert the tenant's admins and the dev team |
| Spool size / age | node | drop oldest Diagnostic below Warning, never Audit |
| File retention (count/age/total bytes) | node | delete oldest rolled files |
| Server retention per tier and per tenant plan | server | 08 §3 |
| Field size (default 1 KiB/string, 32 fields/record) | producer | `Truncated` flag |
