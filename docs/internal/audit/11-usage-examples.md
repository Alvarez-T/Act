# 11 — Usage examples (what the dev writes, what is generated, what you see)

Every example has up to three parts:

- **✍ Dev writes**: the code a developer actually types.
- **⚙ Generated**: what the generators emit (for review; never edited by hand).
- **👁 Result**: what ends up in the audit store, the history panel, the verifier.

**📦 Framework** marks code written once inside YFex libraries. Business names (Venda, Produto, Caixa, NF-e) are examples only; the library
knows none of them. API added by these examples beyond 02–09 is marked **(new proposal)**. App-side types and handler bodies (entities,
SQL helpers, `With(...)` copies) are illustrative; the YFex APIs they call are the ones specified in 02–09.

---

## 1. Setting it up (once per app)

### 1.1 Server (ASP.NET Core + Fusion RPC + Postgres)

**✍ Dev writes**

```csharp
builder.Services.AddYFexAudit(audit => audit                    // engine: scopes, encoder, actor sources, adapters for existing seams
    .Categories<FarmaAudit>());                                  // [AuditCategories] class (02 §2)
builder.Services.AddYFexAuditServer(server => server
    .UsePostgres(builder.Configuration.GetConnectionString("Audit"))
    .Retention(r => r.Class("standard", hot: TimeSpan.FromDays(365), keep: TimeSpan.FromDays(730))
                     .Class("fiscal-5y", hot: TimeSpan.FromDays(365), keep: TimeSpan.FromDays(1_855), requireExport: true)
                     .Map("fiscal", "fiscal-5y").Map("cash", "fiscal-5y"))
    .Archive(a => a.FileSystem(@"\\worm\audit")));
fusion.AddAuditRpcServer();
```

**📦 Framework** (what `AddYFexAudit` registers): `IAuditTrail` (real), `IAuditActorSource`s (CallContext, `IPrincipalAccessor`),
`AuditingTenantImpersonationAuditor`, the Cqrs `AuditStage` factory, the YFex.Data transaction participant, the Rules/Rpc/Jobs facts. 👁 A
fresh server already audits denials, impersonation, approvals, rule publishing, sync acknowledgements and governance changes.

### 1.2 Desktop (Avalonia, online-first)

```csharp
services.AddYFexAudit(a => a.Journal(j => j.Sqlite("audit.db")))     // for local executions + spill (05 §3)
        .AddYFexAuditClient(c => c.ShipOverRpc());                    // AuditShipper over the existing Fusion connection
```

### 1.3 POS terminal (offline for days, weak hardware)

```csharp
services.AddYFexAudit(a => a
    .Journal(j => j.Sqlite("audit.db").MaxSize(256 * 1024 * 1024).LocalRetention(TimeSpan.FromDays(30)))
    .DeviceKey(k => k.FromPlatformStore()))                           // shared device identity (⚑A17)
    .AddYFexAuditClient(c => c.ShipOverRpc());
```

### 1.4 Browser (Blazor WASM)

```csharp
services.AddYFexAudit(a => a.Journal(j => j.IndexedDb()))            // weaker durability (browser), ships eagerly (05 §3)
        .AddYFexAuditClient(c => c.ShipOverRpc());
```

---

## 2. An audited command: one line in the configuration

**✍ Dev writes**

```csharp
public static partial class Produto
{
    public static partial class Commands
    {
        public sealed partial record AlterarPreco(ProdutoId Id, Money NovoPreco, string? Motivo) : ICommand<Result>;
    }
}

public sealed class ProdutoConfiguration : IAggregateConfiguration<Produto>
{
    public void Configure(AggregateConfigurationBuilder<Produto> b)
    {
        b.Command<Produto.Commands.AlterarPreco, Result>()
            .RequirePermission(Permissions.ProdutoPreco)
            .Audit(a => a.Category("catalog").Reason(c => c.Motivo));
    }
}

public sealed class AlterarPrecoHandler(YFexConnectionFactory db) : ICommandHandler<Produto.Commands.AlterarPreco, Result>
{
    public async ValueTask<Result> HandleAsync(Produto.Commands.AlterarPreco c, CancellationToken ct)
    {
        await using var connection = db.Create();
        await using var tx = await connection.StartScopedTransaction(ct);
        var produto = await connection.QueryFirst<ProdutoEntity>(ProdutoSql.ById(c.Id), ct);
        var alterado = produto.With(preco: c.NovoPreco);
        await connection.UpdateAsync(produto, alterado, ct);           // YFex.Data stage 2; ProdutoEntity is [AuditChanges] (§3)
        await tx.CommitAsync(ct);
        return Result.Ok;
    }
}
```

**⚙ Generated**: `AuditDescriptorFor<AlterarPreco>` (03 §3: target `Produto` by the `ProdutoId` convention, reason `Motivo`); the stage is
installed for this message only. The handler contains **no audit code**.

**👁 Result** (decoded entry; canonical bytes ≈ 420 B)

```
#4f1c9a2e  2026-10-01 10:41:02.118Z  acme/loja7   user joao
  Produto.AlterarPreco  catalog → standard   ok
  target   Produto/7891234567895  Updated
           Preco   R$ 10,00 → R$ 12,00
  fields   NovoPreco = R$ 12,00
  reason   "reajuste fornecedor"
  trace    4bf92f35…/00f067aa   node srv-b   sealed acme·standard·2026-10-01 #18234
```

The entry was inserted in the **same transaction** as the `UPDATE`. A crash before `CommitAsync` leaves neither.

---

## 3. Field-level history of an entity (`[AuditChanges]`)

**✍ Dev writes** (once, on the entity)

```csharp
[AuditChanges, Table("PRODUTO")]
public sealed partial class ProdutoEntity { /* Id, Descricao, Preco, DescontoMaximo, Situacao, Ncm, AlteradoEm, Versao */ }
```

**⚙ Generated**: `IAuditChanges<ProdutoEntity>` (04 §2.2) + `AuditSchema.verified.txt` lines (04 §2.4).

**👁 Result**: any audited command that updates a product gets field changes (§2). A nightly job that deactivates products with no stock
(no audited command) produces implicit entries:

```
#77ab01c3  2026-10-02 01:00:13Z  acme   job estoque.reprocessar   Produto.Changed (implicit)   ok
  target Produto/7891234567895  Updated   Situacao  Ativo → Inativo
```

---

## 4. A fact that is not a message (`[Audit]` partial method)

**✍ Dev writes**

```csharp
public static partial class CaixaAuditoria
{
    [Audit("Caixa.Sangria", Category = "cash")]
    public static partial ValueTask<AuditEntryId> Sangria(this IAuditTrail audit, CaixaId caixa, Money valor, string motivo,
        CancellationToken ct);
}

// in the cash service
await audit.Sangria(caixa.Id, valor, motivo, ct);
```

**👁 Result**

```
#a0c4e911  2026-10-01 10:43:10Z  acme/loja7/caixa3  user joao   Caixa.Sangria  cash → fiscal-5y  ok
  target Caixa/3   fields valor = R$ 500,00   reason "depósito bancário"
```

---

## 5. Rule decisions and approvals (replaces `IDecisionAuditSink`)

**✍ Dev writes**: nothing new. The sale command already uses rules (`.UseSituation<…>().Enforce(Desconto.Set)`) and is audited
(`.Audit(a => a.Category("sales"))`).

**📦 Framework**: `RulesStage` writes `AuditDecision` + `AuditApproval` into `AuditScope.Current`; the offline device approval sets `NeedsReview`.

**👁 Result**

```
#c2d7f0b8  2026-09-30 18:15:44Z  acme/loja7/caixa3  user caixa-3   Vendas.FinalizarVenda  sales  ok   ⚑ NeedsReview  Offline
  target Venda/V-88123
  decision desconto v12 fp=77ac…  verdict NeedsApproval→Allowed  adjustments 2  flagged (offline approval)
  approval ger-01  vendas.desconto.override  Device key pos-0003  fp=77ac…  issued 18:15:02
```

The approval's issuance is a separate fact recorded by the device when the manager approved (`Rules.ApprovalIssued`, 01 §3.3).

---

## 6. Impersonation

**Tenant impersonation** (platform admin opens a tenant's data through YFex.Web's header):

```
#5e1f2a90  2026-10-01 11:02Z  acme   user admin-7 (home: platform)   Tenancy.ImpersonationStarted   security  ok
#5e1f2b11  2026-10-01 11:03Z  acme   user admin-7 (home: platform) [TenantImpersonation]   Produto.AlterarPreco  ok …
```

**User impersonation** (support acts as `joao`, new feature suggested for YFex.Web, 10 §3):

```
#9b0e11d4  2026-10-01 09:02Z  acme/loja7   user joao ← real sup-02 [UserImpersonation]   Produto.AlterarDescricao  ok
```

Nothing to write in handlers: `ActorContext` (02 §3) carries both identities.

---

## 7. Offline POS

**✍ Dev writes**: nothing beyond §1.3. `FinalizarVenda` is `IQueueable` and audited.

**👁 Result** — the POS loses connectivity at 10:30 and comes back at 12:40:

```
POS journal (local view, 09 §7)
10:41  Vendas.FinalizarVenda   queued  (offline)                         ⏳ not shipped
10:43  Caixa.Sangria           ok      (executed locally)                ⏳ not shipped
12:40  ── reconnect: batch seq 1203–1219 shipped, signed head h=9e…, receipt received 1219 / sealed 1219
12:40  Vendas.FinalizarVenda   ok      (server, same idempotency key)    ⛓ sealed
```

Server history shows one line: `Vendas.FinalizarVenda  requested offline 10:41 · executed 12:40  ok`.

If the replay had failed permanently and the cashier acknowledged it on the sync-issues screen:

```
#e4410aa7  12:52  acme/loja7/caixa3  user joao   Sync.FailureAcknowledged  synchronization  ok
  fields command = Vendas.FinalizarVenda  idempotencyKey = 0b6f…  reason = "estoque insuficiente"
```

---

## 8. Fiscal two-phase (irreversible SEFAZ call)

**✍ Dev writes** (fiscal module configuration)

```csharp
b.Command<NFe.Commands.Emitir, ResultadoEmissao>().Audit(a => a.Category("fiscal").TwoPhase());
```

**👁 Result — normal run**

```
#11aa…  10:41:05.002  Intent   NFe.Emitir  fiscal → fiscal-5y   target NFe/3526…0019 (chave)   fields modelo=65 serie=1 numero=1234
#11ab…  10:41:06.880  Outcome  NFe.Emitir  ok  code SEFAZ.100   intent #11aa…   fields protocolo=1352… xml.sha256=6d1f…
```

**👁 Result — the process crashed after SEFAZ answered**: only the intent exists. At 10:56 the review queue shows
`⚠ NFe.Emitir #11aa… resultado desconhecido`. The fiscal module's reconciliation (status query to SEFAZ) records the outcome with the same
`IntentId` (new proposal: `IAuditTrail.CompleteIntentAsync(intentId, outcome, code, ct)`).

---

## 9. Personal data in an entry

**✍ Dev writes**: nothing. `Cpf` is classified once on its struct (YFex.Privacy); `ClienteId` is `[DataSubjectKey("customer")]`.

**👁 Result** (stored)

```
fields  cliente = ⟨Encrypted subject=customer:#h:9f2c… key=v3⟩  pseudonym #h:9f2c41d07a3be1c2
```

**👁 Result** (history panel, user without `audit.reveal`): `cliente ***.456.789-**`. With `audit.reveal`, clicking "revelar" shows the CPF and
writes:

```
#f0e2…  14:20  acme  user ana   Audit.Revealed  access → security  ok   entry #c2d7f0b8  paths [cliente]
```

---

## 10. Erasure request with a fiscal hold

**👁 Result** (YFex.Privacy rights request for `customer:#h:9f2c…`, audit participant's part)

```
Audit participant
  located   37 entries (12 sales, 25 fiscal)
  erased    keys for classes [standard] shredded → 12 entries now read "erased on 2026-10-03"
  held      class fiscal-5y: legal hold "fiscal retention" until 2031-09-30 (basis: legal obligation) → shredded automatically then
  recorded  #b1c0… Privacy.SubjectErased  privacy  ok
```

Chains are untouched; `yfex-audit verify` still passes.

---

## 11. Verification and tampering

**👁 Result** (someone ran `UPDATE audit.field_changes SET after_text='11,00' …` and, with the retention role, edited one entry's bytes)

```
GetVerificationStatus  2026-09-25 … 2026-10-01   tenant acme
  standard   2026-09-25 … 09-30   ✔ Verified
  standard   2026-10-01           ✖ Broken at index 18234 (hash mismatch; anchor #18000 ok, anchor #19000 mismatch)
  catalog projection check        ✖ field_changes row of #4f1c9a2e differs from canonical bytes (Preco after)
  → Critical: Audit.VerificationFailed (security chain), alert sent, review queue item opened
```

---

## 12. Export for an auditor (one product's history, with proofs)

```csharp
await AuditCommands.Export(new AuditExportRequest(Entity: new AuditTarget("Produto", "7891234567895"),
    From: new(2026, 1, 1), To: new(2026, 10, 1), Proofs: true, Reveal: false));
```

```
$ yfex-audit verify produto-7891234567895.yaudit
manifest        ok (signed by anchor key k-2026, valid)
entries         14 (partial export)
inclusion       14/14 proofs verify against 11 day seals
day seals       11/11 signatures ok
result          VERIFIED
```

---

## 13. Jobs and AI agents

```
#3c3d…  02:00  acme   job estoque.reprocessar ← real maria [Delegation]   Jobs.ScheduleOverridden  administration  ok
        fields schedule=estoque.reprocessar  before="0 2 * * *"  after="0 3 * * *"
#8e70…  15:12  acme   agent pricing-assistant ← real maria [AgentOnBehalf]   Produto.AlterarPreco  ok
        flags NeedsReview (destructive tool call, confirmed by maria at 15:11:58)
```

---

## 14. Cheat sheet: defaults and overrides

| Want | Do |
|---|---|
| Audit a command | `.Audit()` in its configuration (or `b.Audit(a => a.AllCommands())`) |
| Audit field changes of an entity | `[AuditChanges]` on the entity type |
| Exclude one member | `.Audit(a => a.Ignore(c => c.X))` / `[NotAudited]` on the entity member (not allowed in `requireComplete` classes) |
| Change the target | `.Audit(a => a.Target(c => c.Chave))`; `[AuditEntity("X")]` on the id struct |
| Record an external effect safely | `.Audit(a => a.TwoPhase())` |
| Audit reads of sensitive data | `.Audit(a => a.Access())` on the query |
| Record a manual fact | `[Audit("Area.Fact")] static partial … (this IAuditTrail audit, …)` |
| Act on behalf of someone | `using var _ = ActorContext.Enter(actor, realActor, relation)` |
| Turn audit off in a test | don't call `AddYFexAudit` (`NullAuditTrail`), or `AuditCapture.Install` to assert |
