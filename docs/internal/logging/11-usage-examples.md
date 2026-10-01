# 11 — Usage examples (what the dev writes, what is generated, what you see)

Every example has three parts:

- **✍ Dev writes**: the code a developer actually types.
- **⚙ Generated**: what the source generators emit (shown so you can review it; developers never edit it).
- **👁 Result**: what comes out in the console / file / viewer / alerts.

Library code that is written **once inside the framework** (not generated, not written by app devs) is marked **📦 Framework**.

Syntax follows the current ecosystem (`Customer.Commands.Create(...)` helpers, `IAggregateConfiguration<T>`, `[Saga]`, `[Job]`,
`[Route]`, `[AggregateCommandHandler]`). New attributes and APIs come from this design (02–08). Anything not yet in 02–08 is
marked **(new proposal)**.

**API added by these examples** (to fold into 02/03/06/08 once approved, ⚑L13): `[LogValue(DataSensitivity, Mask)]` + `LogMasks`;
`LogWriter.WriteNull` / `WriteSpanFormattable`; `Operations.MarkWith<T>`; `OperationRegistry.RegisterMessage`; `LogRecords.Begin/Commit`
(generated `[Log]` bodies); per-level handlers `LogInformationHandler`, …; `UseYFexLogging()` (ASP.NET Core); `.FromConfiguration`,
`.LiveTap()`; `AddYFexLoggingRelay`; `AddYFexLoggingAlerts` + `AlertSeverity`/`AlertGroup`; `Logging.Commands.GrantVerbose`;
`LogCapture`; diagnostic YFL0031 (secret-looking member captured).

---

## 0. What the developer actually decides

Most of the time the answer is "nothing". The defaults already log every operation with name, duration, outcome, tenant,
user and trace.

| Decision | Where | Default if you do nothing |
|---|---|---|
| App name, environment, profile (Server/Desktop/Device) | `AddYFexLogging` in `Program.cs` (once per app) | `App` = entry assembly name, profile from environment |
| Where logs go (file, server, OTLP, console) | `.WriteTo(...)` | console in Development, rolling file elsewhere |
| Which message fields are logged | nothing to do: decided by member **types** (04 §3); exceptions via `.Logging(l => l.Ignore(...))` or `[NoLog]` | all members, typed; strings scrubbed; collections as counts |
| Which data is personal/sensitive | `[Sensitive(...)]` **once on the value-object struct** (`Cpf`, `Email`…) | redacted everywhere that type appears |
| Which ids are searchable | `[LogValue(SearchKey = true)]` once on the id struct | not indexed |
| How screen state is logged | nothing to do: role inferred from State attributes (12) | user input → edit timeline, screen state → not logged |
| Which operations are audited | `[Audited]` on the message or `.Logging(l => l.Audit())` | denials, impersonation, rule approvals, payments, dead letters |
| Levels per kind/module/operation, sampling, slow thresholds | fluent config or `appsettings.json` | Information emit, Debug in the flight recorder, failures always kept |
| Manual logs in custom services | `ILog<T>` (`log.Info($"…")`), `[Log]` partial methods, `log.Operation(...)` | — |
| Alert rules and who gets notified | `AddYFexLoggingAlerts` on the log server (or edited at run time) | new issue in prod + any Critical → dev channel |
| Opt out | `[NoLog]` / `<YFexInstrumentation>false</YFexInstrumentation>` | instrumented |

---

## 1. Setting it up (once per app)

### 1.1 Server (ASP.NET Core + Fusion RPC)

**✍ Dev writes** (`Program.cs`)

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddYFexConfigurations()                                    // existing (generated)
    .UseYFexMessagingRpcServer(rpc => rpc.CallContext(c => c.FromConnection().OrFromHeader().WithTenant()))   // existing
    .AddYFexLogging(log => log
        .Node(n => n.App("farma-api").Kind(NodeKind.Server))
        .Profile(LoggingProfile.Server)
        .FromConfiguration(builder.Configuration.GetSection("YFex:Logging"))
        .WriteTo(w => w
            .RollingFile("logs/{app}/{date}.ylog")
            .Server(new Uri("https://logs.acme.com"), s => s.ApiKeyFrom("Logging:ApiKey").ControlChannel()))
        .Audit(a => a.Durable("audit/").HashChain().Ship())
        .Bridge(b => b.MicrosoftExtensionsLogging(minLevel: LogLevel.Warning)
                      .Activities("Microsoft.AspNetCore", "Npgsql")));

var app = builder.Build();
app.UseYFexLogging();              // HTTP server spans + incoming traceparent trust policy (must come first)
app.UseYFexMultiTenant<Tenant>();  // existing
app.Run();
```

### 1.2 Desktop (Avalonia)

```csharp
services
    .AddYFexMessagingRpcClient(...)                            // existing
    .AddYFexLogging(log => log
        .Node(n => n.App("farma-desktop").Kind(NodeKind.Desktop).InstanceIdFile("%LOCALAPPDATA%/YFex/instance.id"))
        .Profile(LoggingProfile.Desktop)
        .WriteTo(w => w
            .RollingFile("%LOCALAPPDATA%/YFex/logs/{app}/{date}.ylog")
            .Server(new Uri("https://logs.acme.com"), s => s.Spool("%LOCALAPPDATA%/YFex/spool/"))
            .LiveTap()));                                       // in-app diagnostics panel / LoggY local attach
```

### 1.3 POS terminal (weak hardware, unstable 4G), sending through a store relay

```csharp
services.AddYFexLogging(log => log
    .Node(n => n.App("farma-pdv").Kind(NodeKind.Device))
    .Profile(LoggingProfile.Device)                            // 1 MiB of buffers, Warning emit / Information recorder
    .WriteTo(w => w.Server(new Uri("http://loja-relay.local:5080"), s => s
        .Spool("spool/", maxBytes: 128L << 20)
        .MaxBytesPerSecond(16 * 1024)
        .OnlyOnUnmeteredFor(LogLevel.Debug))));
```

The store relay is one line: `services.AddYFexLoggingRelay(r => r.Upstream(new Uri("https://logs.acme.com")));`

### 1.4 What "done" means

After §1.1–§1.3 **no other logging code is required**. Every command, query, event, RPC call, navigation, state command, job,
saga step, SQL statement, rule evaluation and authorization denial is recorded, correlated and shipped.

---

## 2. CQRS: zero code, then opting in

### 2.1 Zero code

**✍ Dev writes** (unchanged from today)

```csharp
public partial class Venda
{
    public static partial class Commands
    {
        public partial record FinalizarCommand(VendaId Id, Cpf? Cliente, Money Total, int Itens) : ICommand<VendaFinalizada>, IQueueable;
    }
    public static partial class Queries
    {
        public partial record GetByIdQuery(VendaId Id) : IQuery<VendaDto>, ICacheable;
    }
}

// somewhere in a view model / service
QueueableResult<VendaFinalizada> r = await Venda.Commands.Finalizar(id, cliente, total, itens);
```

**⚙ Generated** by the CQRS generator, inside the module initializer it already emits:

```csharp
// <auto-generated/>  Farma.Vendas.Venda.Diagnostics.g.cs
file static class __VendaOps
{
    [global::System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
    {
        global::YFex.Diagnostics.OperationDescriptorFor<global::Farma.Vendas.Venda.Commands.FinalizarCommand>.Descriptor =
            global::YFex.Diagnostics.OperationRegistry.Register(
                new global::YFex.Diagnostics.OperationDescriptor<global::Farma.Vendas.Venda.Commands.FinalizarCommand>(
                    kind:   global::YFex.Diagnostics.OperationKind.Command,
                    name:   "Venda.Finalizar",
                    module: "Farma.Vendas",
                    level:  global::YFex.Diagnostics.LogLevel.Information,
                    flags:  global::YFex.Diagnostics.OperationDescriptorFlags.Boundary,
                    fields: [],
                    capture: null));                                        // shape only: nothing opted in

        global::YFex.Diagnostics.OperationDescriptorFor<global::Farma.Vendas.Venda.Queries.GetByIdQuery>.Descriptor =
            global::YFex.Diagnostics.OperationRegistry.Register(
                new global::YFex.Diagnostics.OperationDescriptor<global::Farma.Vendas.Venda.Queries.GetByIdQuery>(
                    kind: global::YFex.Diagnostics.OperationKind.Query, name: "Venda.GetById", module: "Farma.Vendas",
                    level: global::YFex.Diagnostics.LogLevel.Debug,                // queries default to Debug
                    flags: global::YFex.Diagnostics.OperationDescriptorFlags.None, fields: [], capture: null));
    }
}
```

**📦 Framework**: the CQRS `LoggingStage` (written once in YFex.Cqrs; shown so the flow is clear)

```csharp
// (sketch) outermost stage of MessagePipeline, used by LocalDispatcher and FusionMessageBus
static async ValueTask<QueueableResult<TResult>> CommandAsync<TCommand, TResult>(
    TCommand cmd, CommandNext<TCommand, TResult> next, CancellationToken ct) where TCommand : ICommand<TResult>
{
    var operation = Operations.Start(in cmd);                                   // OperationDescriptorFor<TCommand>: one static load; inactive if no engine
    try
    {
        var r = await next(cmd, ct).ConfigureAwait(false);       // validation → authorization → rules → handler / outbox
        operation.Complete(r.ToOutcome(), r.OutcomeCode(), r.IsQueued ? OperationFlags.Offline : OperationFlags.None);
        return r;
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { operation.Complete(OperationOutcome.Cancelled); throw; }
    catch (Exception ex) { operation.Fail(ex); throw; }
}
```

**👁 Result** (console, compact layout)

```
10:41:02.118 INF [cmd] Venda.Finalizar  client  ok        42.3ms  acme/loja7 u:joao  t:4bf92f35
10:41:02.120 INF [cmd] Venda.Finalizar  server  ok        37.9ms  acme/loja7 u:joao  t:4bf92f35  node:srv-b
10:41:05.502 INF [cmd] Venda.Finalizar  client  rejected   1.1ms  acme/loja7 u:joao  code=VAL.RANGE violations=1
10:52:40.009 INF [cmd] Venda.Finalizar  client  queued     0.8ms  acme/loja7 u:joao  offline
10:41:03.004 DBG [qry] Venda.GetById    client  ok         0.2ms  cache=hit
```

### 2.2 Fields: automatic, decided by the member types (no member attributes)

**✍ Dev writes**: the same record as §2.1, untouched. The only thing that matters is that personal data uses the existing value
objects (`Cpf`, `Email`, `Telefone`), which are marked once on their own struct (§3).

```csharp
public partial record FinalizarCommand(VendaId Id, Cpf? Cliente, Money Total, List<ItemVenda> Itens, string? Observacao)
    : ICommand<VendaFinalizada>, IQueueable;
```

**⚙ Generated**: the generator picks each member's writer from its type (04 §3):

```csharp
fields: [ new("id",          global::YFex.Privacy.DataSensitivity.Internal),           // VendaId  [LogValue(SearchKey = true)]
          new("cliente",     global::YFex.Privacy.DataSensitivity.SensitivePersonal),  // Cpf      [Sensitive] on the struct
          new("total",       global::YFex.Privacy.DataSensitivity.Internal),           // Money    IRawValue<Money>
          new("itens.count", global::YFex.Privacy.DataSensitivity.Public),             // List<T>  → count only
          new("observacao",  global::YFex.Privacy.DataSensitivity.Internal) ],         // string   → truncated + scrubbed
capture: static (in global::Farma.Vendas.Venda.Commands.FinalizarCommand c, ref global::YFex.Diagnostics.LogWriter w) =>
{
    w.Write<global::Farma.Vendas.VendaId>(0, c.Id);
    if (c.Cliente is { } __v1) w.Write<global::YFex.Entity.Cpf>(1, __v1); else w.WriteNull(1);   // redacted on the consumer
    w.WriteRawValue<global::YFex.Money>(2, c.Total);                                             // no boxing
    w.Write(3, c.Itens?.Count ?? 0);
    w.WriteScrubbed(4, c.Observacao, maxChars: 256);                                             // scrubbed on the consumer
}
```

**👁 Result**

```
10:41:02.118 INF [cmd] Venda.Finalizar client ok 42.3ms  id=V-88123 total=R$ 149,90 itens.count=4 cliente=#h:9f2c41d07a3be1c2
                                                         observacao="cliente pediu nota com cpf ***.***.***-**"
```

`#h:` is an HMAC with the tenant's key. Support can search "every operation of this customer" by pasting the CPF into the
viewer (it hashes it the same way), but the CPF never touches disk or network. A CPF typed into a free-text field is caught by
the scrubber.

**The review step is a snapshot, not attributes** (committed `LogSchema.verified.txt`, 04 §3):

```
Venda.Finalizar (cqrs.command)
  id           VendaId     Internal           value      search-key
  cliente      Cpf         SensitivePersonal  hash
  total        Money       Internal           value
  itens.count  List        Public             count
  observacao   string      Internal           scrubbed, max 256
```

**What the generator flags** (compile-time diagnostics):

```csharp
public partial record LoginCommand(string Usuario, string Senha) : ICommand<Sessao>;
//                                                  ~~~~~~~~~~~~
// YFL0031 info: 'Senha' looks like a secret (name match); it is ERASED from logs.
//               Consider a value object marked [Sensitive(DataSensitivity.Secret)].

public partial record CadastrarClienteCommand(string Nome, string Cpf) : ICommand;
//                                                          ~~~~~~~~~~
// YFL0032 info: 'Cpf' is a raw string with a personal name; treated as Personal (masked).
//               Use YFex.Entity.Cpf for validation, masking and logging in one place.
```

### 2.3 Behaviour in the aggregate configuration (no attributes)

**✍ Dev writes**

```csharp
public sealed class VendaConfiguration : IAggregateConfiguration<Venda>
{
    public void Configure(AggregateConfigurationBuilder<Venda> b)
    {
        b.Command<Venda.Commands.FinalizarCommand, VendaFinalizada>()
            .Validation(v => v.Field(c => c.Total).Positive())                     // existing
            .RequirePermission("vendas.finalizar")                                  // existing
            .Logging(l => l
                .Audit()                                                            // also written to the Audit channel
                .SlowAfter(TimeSpan.FromMilliseconds(500))                          // > 500 ms → Warning + flight recorder flush
                .Ignore(c => c.Observacao));                                        // the rare exception

        b.Query<Venda.Queries.GetByIdQuery, VendaDto>()
            .Cacheable()
            .Logging(l => l.Level(LogLevel.Debug).SampleSuccess(0.05));            // keep 5% of hits, 100% of failures
    }
}
```

**⚙ Generated**: the CQRS generator parses `.Logging(...)` **syntactically** (same mechanism as `.Validation`, D27). `Ignore`
removes the member from the compiled capture code. `Audit`/`SlowAfter`/`SampleSuccess`/`Level` become a static descriptor
override applied at startup. No `Expression<>` exists at run time.

---

## 3. Domain value objects: logged as themselves, safely

### 3.1 Option A — attributes on the struct (normal case)

**✍ Dev writes** (once, in `YFex.Entity.Public`)

```csharp
[Sensitive(DataSensitivity.SensitivePersonal, Mask = "***.###.###-**")]   // YFex.Privacy: logs, UI, persistence, AI, LGPD
[LogValue]
public readonly partial record struct Cpf : ISpanFormattable { /* existing type */ }

[LogValue(SearchKey = true)]                                               // indexed: search a NF-e by its key (13 §4)
public readonly partial record struct ChaveNFe : ISpanFormattable { /* existing type */ }

[LogValue(SearchKey = true)]
public readonly partial record struct VendaId : ISpanFormattable { /* app type */ }
```

**⚙ Generated**

```csharp
partial record struct Cpf : global::YFex.Diagnostics.ILogValue<Cpf>
{
    static void global::YFex.Diagnostics.ILogValue<Cpf>.Write(in Cpf value, ref global::YFex.Diagnostics.LogWriter w, ushort field)
        => w.WriteSpanFormattable(field, value);                  // formats straight into the slot (no string)
    static global::YFex.Privacy.DataSensitivity global::YFex.Diagnostics.ILogValue<Cpf>.Sensitivity
        => global::YFex.Privacy.DataSensitivity.SensitivePersonal;                 // read from [Sensitive]

    [global::System.Runtime.CompilerServices.ModuleInitializer]
    internal static void __RegisterLogMask() => global::YFex.Diagnostics.LogMasks.Register<Cpf>("***.###.###-**");
}
```

### 3.2 Option B — implement it by hand (full control, e.g. log a CNPJ root only)

```csharp
public readonly partial record struct Cnpj : ILogValue<Cnpj>
{
    public static void Write(in Cnpj v, ref LogWriter w, ushort field) => w.Write(field, v.Raiz);   // 8-digit root only
    public static DataSensitivity Sensitivity => DataSensitivity.Public;
}
```

From then on, **any** message, `[Log]` parameter or interpolated hole of type `Cpf`/`Cnpj`/`ChaveNFe` is captured correctly,
with no boxing, and redacted according to its class.

---

## 4. View models: UI actions become the trace root

**✍ Dev writes**

```csharp
[Route(typeof(CaixaRoute))]
public partial class CaixaViewModel : PageViewModel
{
    [Observable, LogChanges] public partial decimal Desconto { get; set; }
    [Observable]             public partial Cpf? Cliente { get; set; }

    [StateCommand]
    async Task FinalizarAsync(CancellationToken ct)
    {
        var r = await Venda.Commands.Finalizar(VendaId, Cliente, Total, Itens.Count);
        if (r.IsOk) await Navigator.NavigateTo(new ReciboRoute(), VendaId);
    }
}
```

**⚙ Generated** by the State generator (it already emits a wrapper for `IsExecuting`/`CanExecute`; the logging lines are new):

```csharp
private async global::System.Threading.Tasks.Task __Execute_FinalizarAsync(global::System.Threading.CancellationToken ct)
{
    var __operation = global::YFex.Diagnostics.Operations.Start(__CaixaViewModelOps.Finalizar);   // kind StateCommand, Boundary, Breadcrumb
    try
    {
        await FinalizarAsync(ct).ConfigureAwait(true);
        __operation.Complete();
    }
    catch (global::System.OperationCanceledException) when (ct.IsCancellationRequested) { __operation.Complete(global::YFex.Diagnostics.OperationOutcome.Cancelled); throw; }
    catch (global::System.Exception __ex) { __operation.Fail(__ex); throw; }
}

// [LogChanges] → inside the generated setter, after the change is committed:
if (global::YFex.Diagnostics.Operations.IsEnabled(__CaixaViewModelOps.Desconto_Changed, global::YFex.Diagnostics.LogLevel.Debug))
    global::YFex.Diagnostics.Operations.MarkWith(__CaixaViewModelOps.Desconto_Changed, value);   // decimal → RawValue, no boxing
```

The NavigatR generator registers a descriptor per route (`Route:Caixa`, `Route:Recibo`), and the navigator records each
navigation as a breadcrumb operation.

**👁 Result** (LoggY trace waterfall for one click)

```
Caixa.Finalizar (ui)                         ████████████████████████████████  48.0ms   desktop PDV-03
 ├ Venda.Finalizar (cmd, client)              ██████████████████████████████    44.9ms
 │  └ IVendaRpc.Finalizar (rpc)               █████████████████████████████     43.7ms
 │     └ Venda.Finalizar (cmd, server)          ████████████████████████        37.9ms   srv-b
 │        ├ RuleSet desconto v12 (rules)        █                                0.4ms   verdict=Allow fp=77ac…
 │        ├ INSERT venda (sql)                    ██                             3.2ms   rows=1
 │        └ UPDATE estoque (sql)                    ████                         6.0ms   rows=4
 └ Navigate Caixa → Recibo (nav)                                          ██     2.1ms
Breadcrumbs: Route:Vendas → Route:Caixa → Desconto=5 → Desconto=10 → Caixa.Finalizar
```

---

## 5. Manual logging in your own services

### 5.1 `ILog<T>`: interpolated, zero-alloc

**✍ Dev writes**

```csharp
public sealed class CatalogoSync(ILog<CatalogoSync> log, ICatalogoApi api)
{
    public async Task RunAsync(CancellationToken ct)
    {
        using var operation = log.Operation("Catalogo.Sync");                     // a span with duration + outcome
        var sw = Stopwatch.StartNew();
        var itens = await api.PullAsync(ct);
        log.Info($"Recebidos {itens.Count} itens de {api.Fonte} em {sw.Elapsed}");
        if (itens.Count == 0) log.Warn($"Catálogo vazio vindo de {api.Fonte}");
        operation.Complete(OperationOutcome.Ok);
    }
}
```

**⚙ What the C# compiler lowers `log.Info($"…")` to** (no generator involved, standard interpolated-string handler):

```csharp
var __h = new LogInformationHandler(literalLength: 22, formattedCount: 3, log, out bool __enabled);
if (__enabled)                                       // disabled → arguments are NOT evaluated, nothing allocated
{
    __h.AppendLiteral("Recebidos ");                 // stores the literal's reference (no copy)
    __h.AppendFormatted(itens.Count, "itens.Count"); // int → raw bytes in the slot; hole name for free
    __h.AppendLiteral(" itens de ");
    __h.AppendFormatted(api.Fonte, "api.Fonte");     // string → UTF-16 memcpy (capped)
    __h.AppendLiteral(" em ");
    __h.AppendFormatted(sw.Elapsed, "sw.Elapsed");   // TimeSpan → ticks
}
log.Info(ref __h);                                   // commit slot → queue
```

**👁 Result**

```
11:02:17.430 INF CatalogoSync  Recebidos 1824 itens de erp-matriz em 00:00:02.114  itens.Count=1824 api.Fonte=erp-matriz
```

In `.jsonl` and on the server the holes are real fields (`"itens.Count": 1824`), so you can filter by them.

### 5.2 `[Log]` partial methods: stable, reusable, reviewed messages

Use them when a message matters (alerts, dashboards, support docs depend on it).

**✍ Dev writes**

```csharp
public static partial class SefazLog
{
    [Log(LogLevel.Warning, "SEFAZ {Uf} rejeitou {Chave}: {Codigo} {Motivo}", Kind = "fiscal.sefaz")]
    public static partial void Rejeitada(this ILog log, UF uf, ChaveNFe chave, int codigo, string motivo);

    [Log(LogLevel.Error, "SEFAZ {Uf} indisponível após {Tentativas} tentativas")]
    public static partial void Indisponivel(this ILog log, Exception erro, UF uf, int tentativas);
}

// usage
log.Rejeitada(nfe.UF, nfe.Chave, ret.CStat, ret.XMotivo);
```

**⚙ Generated**

```csharp
public static partial class SefazLog
{
    private static readonly global::YFex.Diagnostics.OperationDescriptor __Rejeitada =
        global::YFex.Diagnostics.OperationRegistry.RegisterMessage(
            kind: global::YFex.Diagnostics.OperationKind.Register("fiscal.sefaz"),
            name: "SefazLog.Rejeitada", module: "YFex.NFe",
            template: "SEFAZ {Uf} rejeitou {Chave}: {Codigo} {Motivo}",
            level: global::YFex.Diagnostics.LogLevel.Warning,
            fields: [new("Uf", DataSensitivity.Public), new("Chave", DataSensitivity.Public), new("Codigo", DataSensitivity.Public), new("Motivo", DataSensitivity.Public)]);

    public static partial void Rejeitada(this global::YFex.Logging.ILog log, UF uf, ChaveNFe chave, int codigo, string motivo)
    {
        if (!log.IsEnabled(global::YFex.Diagnostics.LogLevel.Warning)) return;
        var w = global::YFex.Logging.LogRecords.Begin(log, __Rejeitada, out var __slot);
        if (__slot is null) return;                                   // pool exhausted → counted drop
        w.WriteEnum(0, uf);                                           // underlying byte + enum name table id
        w.Write<ChaveNFe>(1, chave);
        w.Write(2, codigo);
        w.Write(3, motivo);
        global::YFex.Logging.LogRecords.Commit(__slot, ref w);
    }
}
```

Compile-time checks: YFL0001 placeholder ↔ parameter mismatch, YFL0002 duplicate EventId in the module, YFL0011 parameter type
not capturable without boxing.

**👁 Result + alert**: `fiscal.sefaz` with `Codigo` as a field means an alert rule such as "> 20 rejections of code 539 per UF
in 10 min" needs no parsing (§10).

---

## 6. Audit

### 6.1 Declarative (preferred)

`[Audited]` on the message, or `.Logging(l => l.Audit())` (§2.3). Nothing else is needed. Every end record of that operation is also
written to the Audit channel with actor, real actor (if impersonating), tenant, outcome and captured fields, hash-chained.

### 6.2 Manual, for facts that are not a message

**✍ Dev writes**

```csharp
log.Audit("Caixa.Sangria", $"retirada {valor} do caixa {caixa.Numero} motivo {motivo}");
```

### 6.3 Existing audit seams: nothing to change

Rules decisions (`IDecisionAuditSink`), rule edits (`IRuleAuditSink`), impersonation (`ITenantImpersonationAuditor`) and
authorization denials are forwarded by adapters that `AddYFexLogging(...).Audit(...)` registers.

**👁 Result** (LoggY → Audit explorer)

```
#18234  2026-10-01 10:41:02  acme/loja7  joao             Venda.Finalizar   ok      total=R$ 149,90 itens=4      ✔ chain
#18235  2026-10-01 10:43:10  acme/loja7  joao             Caixa.Sangria     ok      valor=R$ 500,00 caixa=3      ✔ chain
#18236  2026-10-01 10:44:51  acme/loja7  sup-02 as joao   Venda.Cancelar    denied  code=AUTH.PERMISSION         ✔ chain
#18237  2026-10-01 10:45:30  acme/loja7  ger-01            RuleSet desconto  ok      override 15% (approval A-77) ✔ chain
```

---

## 7. Events, sagas, jobs, event sourcing, SQL: nothing to write

**✍ Dev writes** (all existing syntax, no logging code)

```csharp
public partial class EstoqueViewModel : PageViewModel
{
    [Subscribe<Venda.Events.Finalizada>] void OnVendaFinalizada(in Venda.Events.Finalizada e) => Recarregar();
}

[Saga]
public sealed partial class CheckoutSaga : Saga<CheckoutState>
{
    protected override void Configure(ISagaBuilder<CheckoutState> saga)
    {
        saga.Step<ReservarEstoqueCommand>(s => new(s.VendaId, s.Itens)).CompensateWith<LiberarEstoqueCommand>((c, _) => new(c.VendaId));
        saga.Step<CobrarCommand>(s => new(s.VendaId, s.Total)).Timeout(TimeSpan.FromSeconds(8)).Retry(1)
            .CompensateWith<EstornarCommand>((c, _) => new(c.VendaId, c.Total));
    }
}

public sealed class Fechamento
{
    [Job("fechamento.diario")]
    [Cron("0 1 * * *", TimeZone = "America/Sao_Paulo")]
    public Task RunAsync(JobContext ctx, CancellationToken ct) => ...;
}

[AggregateCommandHandler]
public static class EnviarPedido
{
    public static PedidoEnviado Decide(EnviarPedidoCommand cmd, Pedido pedido) => pedido.Enviar(cmd.Rastreio);
}
```

**⚙ Generated**: the existing generators add the §04-2 shape to the wrappers they already emit (`[Subscribe]` adapter, saga
step table runner, `[Job]` invoker, `[AggregateCommandHandler]` handler) plus one descriptor each. `IJobObserver` gets the
`LoggingJobObserver` adapter. SQL goes through YFex.Data's QueryBuilder executor or `TracingDbConnection`.

**👁 Result**

```
01:00:00.004 INF [job ] fechamento.diario           begin   attempt=1 schedule=17                     t:aa01…
01:00:00.311 DBG [sql ] SELECT venda                ok       88.0ms rows=1931
01:00:03.920 WRN [sql ] UPDATE caixa_resumo         ok      612.4ms rows=12  slow(>200ms)
01:00:04.002 INF [job ] fechamento.diario           ok        4.0s

10:41:02.200 INF [saga] CheckoutSaga #V-88123       begin                                              t:c3d0…  ⇠ follows t:4bf92f35
10:41:02.231 INF [step] ReservarEstoque             ok       30.1ms
10:41:10.240 WRN [step] Cobrar                      timedout  8.0s   retry 1/1
10:41:18.251 ERR [step] Cobrar                      timedout  8.0s
10:41:18.270 INF [comp] LiberarEstoque              ok       19.0ms  compensated
10:41:18.271 WRN [saga] CheckoutSaga #V-88123       cancelled         compensation=AllReversed

10:41:02.240 DBG [evt ] Venda.Finalizada → EstoqueViewModel  ok 0.3ms
10:55:12.002 INF [es  ] Pedido/P-551 append          ok        2.1ms  v41→42 events=1
10:55:12.010 WRN [es  ] Pedido/P-551 append          conflict  1.0ms  expected v41 actual v42
```

---

## 8. Third-party libraries and plain `HttpClient`

**✍ Dev writes**: only the bridge line from §1.1 (`.Bridge(b => b.MicrosoftExtensionsLogging().Activities(...))`).

**👁 Result**: Fusion, ASP.NET Core and Npgsql logs/spans land in the same stream and the same traces:

```
10:41:02.121 WRN [mel ] ActualLab.Rpc.RpcPeer     Peer srv-b reconnecting (attempt 2)   t:4bf92f35
10:41:02.130 DBG [http] GET https://api.erp/precos ok 18.2ms status=200                  t:4bf92f35   (Activity bridge)
```

---

## 9. Live control: "debug this store for 30 minutes"

**✍ Dev / support writes** (CQRS command exposed by the log server; generated helper; or a button in LoggY)

```csharp
await Logging.Commands.GrantVerbose(
    scope: "acme/loja7", level: LogLevel.Trace, duration: TimeSpan.FromMinutes(30), reason: "chamado #4411");
```

**👁 Result**: the nodes of `acme/loja7` receive the override through the control channel within seconds, log at Trace (other
stores are unaffected), and revert on their own after 30 minutes. The grant is audited:

```
AUD #90012  ger-sup  Logging.GrantVerbose  scope=acme/loja7 level=Trace ttl=30m reason="chamado #4411"
```

---

## 10. Alerts and notifications (log server)

**✍ Dev writes** (server `Program.cs`; the same rules are editable at run time in LoggY)

```csharp
services.AddYFexLoggingAlerts(a => a
    .Rule("prod-new-issue",   r => r.NewIssue().Where("env:prod").Severity(AlertSeverity.High))
    .Rule("prod-critical",    r => r.Immediate("level:critical env:prod").Severity(AlertSeverity.Critical))
    .Rule("vendas-erro",      r => r.ErrorRate("kind:cmd operation:Venda.*", above: 0.05, over: TimeSpan.FromMinutes(10), minCalls: 50)
                                    .GroupBy(AlertGroup.Tenant).Severity(AlertSeverity.High))
    .Rule("sefaz-539",        r => r.Threshold("kind:fiscal.sefaz Codigo:539", count: 20, over: TimeSpan.FromMinutes(10))
                                    .GroupBy("Uf").Severity(AlertSeverity.Medium))
    .Rule("pdv-offline",      r => r.Absence("node.kind:device", for: TimeSpan.FromMinutes(5)).GroupBy(AlertGroup.Node))
    .Rule("fechamento",       r => r.JobDidNotRun("fechamento.diario", by: new TimeOnly(2, 0)))
    .Route(rt => rt
        .When(AlertSeverity.Critical).To("telegram-devs", "email-oncall").EscalateAfter(TimeSpan.FromMinutes(10), "telegram-lead")
        .When(AlertSeverity.High).To("telegram-devs")
        .When(AlertSeverity.Medium).Digest(TimeSpan.FromHours(1), "email-oncall")
        .QuietHours(new TimeOnly(23, 0), new TimeOnly(7, 0), except: AlertSeverity.Critical))
    .Channels(c => c
        .Telegram("telegram-devs", botTokenFrom: "Alerts:Telegram:Token", chatId: "-1001234567")
        .Telegram("telegram-lead", botTokenFrom: "Alerts:Telegram:Token", chatId: "88112233")
        .Email("email-oncall", "oncall@acme.com")));
```

**👁 Result** (Telegram)

```
🔴 HIGH · vendas-erro · acme/loja7
Venda.Finalizar failing 11.2% (37/330) in the last 10 min
Top issue: DbUpdateException @ Estoque.Baixar (#3 since 1.4.2, new)
Nodes: srv-b · First seen 10:41 · Trace 4bf92f35
[Open in LoggY]  [Ack]  [Silence 1h]
```

One incident per dedupe key: 300 POS terminals hitting the same bug produce **one** message with "×300", not 300 messages.

---

## 11. Tests

**✍ Dev writes**

```csharp
public sealed class FinalizarVendaTests
{
    [Fact]
    public async Task Finalizar_is_audited_traced_and_never_leaks_cpf()
    {
        using var capture = LogCapture.Start();                     // Test profile, synchronous, scoped to this test's trace

        var r = await Venda.Commands.Finalizar(VendaId.New(), Cpf.Parse("123.456.789-09"), Money.Of(149.90m), 4);

        capture.Op<Venda.Commands.FinalizarCommand>()
               .ShouldEnd(OperationOutcome.Ok)
               .ShouldHaveField("total", Money.Of(149.90m))
               .ShouldHaveRedacted("cliente");
        capture.Audit.ShouldContain("Venda.Finalizar");
        capture.Trace.ShouldContainKinds(OperationKind.Command, OperationKind.Sql);
        capture.ShouldNotContainText("123.456.789-09");             // anywhere: fields, messages, exceptions
    }
}
```

---

## 12. Opting out and overriding (cheat sheet)

```csharp
[NoLog] public partial record PingQuery : IQuery<Pong>;                 // no descriptor, counted only in metrics
[LogLevel(LogLevel.Trace)] public partial record HeartbeatCommand : ICommand;
[LogName("Vendas.Finalizar"), LogId(0x5A1E0013)] public partial record FinalizarCommand(...);   // keep name/id after a rename
```

```xml
<!-- whole project: generators emit no descriptors (e.g. a tiny test helper library) -->
<PropertyGroup><YFexInstrumentation>false</YFexInstrumentation></PropertyGroup>
```

```json
{ "YFex": { "Logging": { "Operations": { "Venda.GetById": { "Level": "None" } }, "Modules": { "YFex.Persistence": "Warning" } } } }
```
