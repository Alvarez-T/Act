# 06 — Offline authority: who decides what without central

## 1. The authority ladder

The **terminal** makes offline business decisions, using the app's `OnOffline<T>` handler. The **site broker** never runs app code. It
verifies people, answers permission questions from signed data, signs approvals, and coordinates what several terminals share
(budgets, unique claims, the site feed). **Central** is the only authority for everything else.

| Decision kind | Central | Site broker (central unreachable) | Terminal in `Site` mode | Terminal alone |
|---|---|---|---|---|
| Record something that already happened (sale, cash withdrawal, retained prescription) | decides | coordinates only (budget, claim, custody) | **decides provisionally** with its offline handler | **decides provisionally**, within its slice |
| Sign a user in | identity provider | **verifies** code + PIN/password against central's signed verifier bundle; issues a session | asks the broker | checks its own cached verifier (passwords only) |
| "May user U do X?" | authoritative | answers from the signed **site authorization** bundle | evaluates locally with the snapshot; the broker's session confirms U | evaluates with U's signed **snapshot** |
| Supervisor approval | HMAC token | **site-signed** token | requests it from the broker | **device-signed** token, capped (D34) |
| Spend a shared budget (customer credit, offline discount total) | ledger of record | reserves from the site budget; cuts terminal slices | asks the broker | spends its own slice |
| "Is this number already used here?" | authoritative | **unique claim** for the store | asks the broker | provisional claim; duplicates found at sync |
| Change that other terminals must see (store price override, a blocked lot, a registered customer) | publishes | **relays and orders** it on the site feed (never interprets it) | applies its own change, then the feed's | applies its own; shares it when the broker returns |
| Roles, grants, tenant prices, rule publishing, enrollment, data subject rights, detokenize, cross-site operations, settlements | **only here** | never (exception: provisional pairing, [03](03-trust-enrollment-pairing.md) §6) | never | never |

Everything a terminal or the broker signs carries `OfflineProvenance` and is re-validated at sync ([08](08-sync-reconciliation-conflicts.md) §3).

## 2. Declaring offline behaviour (⚑O13)

Configuration stays in the aggregate configuration (YFex rule: configuration over attributes for CQRS). Records keep only their
`IQueueable` marker. The existing `.OnOffline<THandler>()` stays **the** local handler. The new `.Offline(...)` only adds how central
treats the operation later and what the terminal needs from the broker.

```csharp
// Before — offline means "queue it, maybe run a local handler"; nothing coordinates terminals
public sealed partial record FinalizarVenda(Guid VendaId, …) : ICommand<Recibo>, IQueueable;
b.Command<FinalizarVenda, Recibo>()
    .RequirePermission(VendaPermissions.Values.Finalizar)
    .OnOffline<FinalizarVendaOffline>();                                // IOfflineHandler<FinalizarVenda, Recibo>

// After — same handler; .Offline(...) adds the coordination and the replay semantics
b.Command<FinalizarVenda, Recibo>()
    .RequirePermission(VendaPermissions.Values.Finalizar)
    .OnOffline<FinalizarVendaOffline>()                                 // runs on the terminal (Site or Standalone mode)
    .Offline(offline => offline
        .RecordsFact()                                                  // central accepts and reports findings, never rejects (§3)
        .Consumes(FarmaAllowances.ConvenioCredito,                      // reserved BEFORE the handler runs (§4)
                  subject: command => command.ConvenioId,
                  amount: command => command.TotalConvenio));
```

```csharp
// ✍ The handler is plain app code — no offline plumbing inside
public sealed class FinalizarVendaOffline(ILocalStock stock) : IOfflineHandler<FinalizarVenda, Recibo>
{
    // When this runs: permission checked (signed snapshot / broker session), budget reserved, approval verified.
    public async ValueTask<Recibo> HandleAsync(FinalizarVenda command, CancellationToken ct)
    {
        await stock.DecreaseAsync(command.Itens, ct);                  // local view only; central stays the record
        return Recibo.Provisorio(command.VendaId, OfflineContext.Current!.Provenance);   // the receipt can print "registrada offline"
    }
}
```

| `.Offline(...)` member | Effect |
|---|---|
| `.RecordsFact()` | replay semantics = fact (§3). Default = request |
| `.Consumes(key, subject, amount)` | budget reservation before the handler (§4); only when the app declared the `AllowanceKey` (⚑O14) |
| `.WhenExhausted(Refuse \| RequireApproval \| AllowAndFlag)` | what happens when the budget of `.Consumes` runs out (§4.1, ⚑O14; default `Refuse`) |
| `.ClaimsUnique(selector)` | store-wide unique claim before the handler (§4.3) |
| `.SharedWithSite<TProjection>()` | marks the command as shareable; when the site's manifest also switches it on, the envelope goes on the site feed after the handler and other terminals run `TProjection` (§5, ⚑O23) |
| `.WhenRulesDiffer(RuleVersionPolicy.KeepAndReport \| Reevaluate)` / `.WhenRulesDiffer<TResolver>()` | how central treats a fact decided with another rule-data version than today's (§8, ⚑O18; default `KeepAndReport`) |
| `.Lane(OfflineLane.Deadline)` | upload priority at sync ([08](08-sync-reconciliation-conflicts.md) §6) |

| What the developer writes | Without central |
|---|---|
| nothing, and the record is **not** `IQueueable` | `Error.Unavailable("requires the central server")` (`CentralOnly`) |
| record is `IQueueable`, no handler | queued; executed only at central; request |
| `.OnOffline<T>()` (existing) | local handler on the terminal + queued; request (no breaking change) |
| `.OnOffline<T>().Offline(…)` | local handler + coordination + declared replay semantics |

- Handlers are concrete types resolved from DI (AOT-safe, no reflection).
- `.Offline(...)` on a non-`IQueueable` record is a startup configuration error. The marker stays the visible "may be accepted without
  central" signal on the record.
- Compiled per message into an `OfflinePolicy` table; the route decision is a table lookup ([04](04-connectivity-and-failover.md) §3).
  **2026-10-15 (⚑O18):** a source generator should later compile the `.Offline(...)` chains into that table (Phase 3), as the
  validation generator does for rules.

## 3. Facts versus requests (fixes F13)

| | **Request** (default) | **Fact** (`.RecordsFact()`) |
|---|---|---|
| Meaning | "please do X". It can still be refused | "X happened". Goods and money already changed hands |
| At central replay | full pipeline. A rejection goes to `ConflictPolicy` (`Escalate`/`RetryLater`/`Discard`/custom), as today | **always applied**. Every check that fails becomes a **finding** with severity. Optional compensation via `IFactReconciler<T>` |
| Expiry in the outbox | may expire into the failure log (today's TTL) | **never expires**. After the TTL it raises an alert and stays in custody (fixes F5's "a sale expired") |
| Pharmacy examples | update a customer's address, request a stock transfer | `FinalizarVenda`, `RegistrarSangria`, `FecharCaixa`, `RegistrarReceitaRetida`, `AlterarPrecoNaLoja` (§5), NFC-e emitted in contingency |

```csharp
// (sketch) optional compensation for facts — the app decides what "fixing" means
public interface IFactReconciler<TCommand> where TCommand : ICommand
{
    ValueTask ReconcileAsync(TCommand command, OfflineFindings findings, CancellationToken cancellationToken);
}

// ✍ Example: stock went negative because the e-commerce sold the same unit
public sealed class FinalizarVendaReconciler(ITarefas tarefas) : IFactReconciler<FinalizarVenda>
{
    public ValueTask ReconcileAsync(FinalizarVenda venda, OfflineFindings findings, CancellationToken ct)
        => findings.Has(FindingKind.NegativeStock)
            ? tarefas.CriarAsync(new ConferirEstoque(venda.Loja, venda.Itens), ct)
            : ValueTask.CompletedTask;
}
```

## 4. Coordination without double spending (⚑O14 ✅ 2026-10-15)

Time limits are soft ([05](05-protecting-offline-data.md) §4). **Budgets** and **unique claims** are the hard bounds that let several
terminals decide at the same time.

### 4.1 Budgets

```csharp
// ✍ App declares budget keys (plain constants, no attributes — like permission ids)
public static class FarmaAllowances
{
    public static readonly AllowanceKey ConvenioCredito = new("convenio.credito");            // per agreement customer
    public static readonly AllowanceKey DescontoOffline = new("desconto.aprovado.offline");   // total approved discount per site per day
}

// (sketch) YFex.Offline — what central signs (BundleKind.Allowance)
public sealed record OfflineAllowance(
    AllowanceKey Key, string Subject,            // "*" or a subject id (agreement, customer, product…)
    string Site, InstallationId Holder,          // the broker, or a terminal for a slice
    decimal Amount, int Count,                   // either or both; computed in decimal (IAmount-friendly)
    long Version, DateTimeOffset ValidUntil,
    Hash256? ParentBundle);                      // slices point to the site-level grant they were cut from
```

```
 central ──signs──► site budget: convenio.credito / Empresa Alfa = R$ 600 (24 h)
 broker ──cuts slices, signed with its evidence key──► CAIXA-01: R$ 100 · CAIXA-02: R$ 100 · (keeps R$ 400)
 CAIXA-01 in Site mode: sale on account R$ 120 → the broker reserves it from its R$ 400
 CAIXA-02 standalone:   R$ 80 → own slice (R$ 20 left); R$ 250 more → refused "acima do limite offline"
 at sync: central sums consumption per budget version; consumption above a grant = High finding (tampering)
```

- **Cost control:** granting a per-customer budget to every site does not scale (5 000 agreement customers × 40 stores). Central grants
  per-subject budgets only for subjects active at that site in the last N days (default 90). Everyone else gets a small default per
  installation (`Subject = "*"`, e.g., R$ 100 per customer per day). Worst-case exposure is then explicit and configurable.

**Confirmed 2026-10-15 (⚑O14, amended by the user).** Budgets are an **optional** mechanism: nothing is granted, signed or reserved
unless the app declares an `AllowanceKey`. Central grants per site; the broker cuts signed slices per terminal (Phase 4).
`.Consumes(key, subject, amount)` reserves before the handler, and `.WhenExhausted(...)` decides what happens when the budget runs out
(Phase 3):

| `.WhenExhausted(...)` | Effect when the reservation does not fit |
|---|---|
| `Refuse` (default) | `Error.Unavailable("acima do limite offline")`; the handler never runs |
| `RequireApproval` | the operation needs a site or device approval (§6) for the excess |
| `AllowAndFlag` | the handler runs; the envelope carries a finding for central's review |

Where the amount of a grant comes from (first one with a value narrows the one before it):

```csharp
// ✍ Central composition (built in Phase 2: YFex.Offline.Central)
services.AddYFexOfflineCentral(central => central
    .Allowance(FarmaAllowances.ConvenioCredito, allowance => allowance
        .DefaultPerSubject(amount: 100m)                                   // 1. code default
        .FromRules(sp => sp.GetRequiredService<LimiteConvenioRegras>())    // 2. IAllowanceAmountSource: admin-edited rule data per tenant or store
        .CappedBy(sp => sp.GetRequiredService<SaldoConvenioCap>())));      // 3. IAllowanceCap: never more than the customer's real balance
// The Phase 3 command-side spelling (.Offline(o => o.Consumes(...).FromRules<T>().CappedBy(...).DefaultPerSubject(...))) maps onto it.
```

Phase 2 builds `AllowanceKey`, the grant computation at central and the signed `offline.allowance` bundle (one per key and site,
golden-pinned); Phase 3 adds `.Consumes(...)`/`.WhenExhausted(...)` and the per-terminal grants; Phase 4 the broker's slices.
**As built (Phase 3e, 2026-10-19):** `AllowancePublisher.GrantTerminalsAsync(key, site, holders, subjects)` splits the site amount evenly
(rounded down to 4 decimals; counts with the remainder to the first holders), one bundle per holder (stream `key/site/holder`, audience the
holder) that only that installation pulls with its roster. Central keeps every published version and checks spending against the version an
envelope cited (`AllowancePublisher.IsOverGrantAsync`, wired by `AddYFexOfflineIntake`).

### 4.2 Caps without budgets

No new mechanism is needed. The rules engine already does limits. **2026-10-15 (⚑O11):** the optional package `YFex.Offline.Rules`
exposes the offline situation as one situation field of type `OfflineFacts` (mode, hours without central, snapshot age, coordinator
present, flags), filled by the view model's `[SituationSource]` from `IOfflineFactsSource`; YFex.Offline itself never references
YFex.Rules:

```csharp
// ✍ the app's situation gains one field; the view model fills it (private partial OfflineFacts __Situation_Offline() => facts.Current;)
[Situation("venda")]
public readonly partial record struct VendaSituation(/* … */ OfflineFacts Offline);

[Rule("venda.offline.limite", Group = nameof(VendaRules.Limites))]
public readonly struct LimiteVendaOffline : IRule<VendaSituation>
{
    public static bool Applies(in VendaSituation s) => s.Offline.IsOffline && s.Total > 2_000m;
    public static void Evaluate(in VendaSituation s, ref DirectiveWriter d)
        => d.RequireApproval(VendaPermissions.AprovarVendaOffline, VendaSituation.Paths.Total, "venda.offline.limite");
}
```

### 4.3 Unique claims

```csharp
// ✍ the same prescription cannot be used at two counters of the store
b.Command<RegistrarReceitaRetida, Unit>()
    .RequirePermission(FarmaPermissions.Values.ReceitaReter)
    .OnOffline<RegistrarReceitaOffline>()
    .Offline(offline => offline.RecordsFact().ClaimsUnique(command => command.NumeroReceita));
```

The broker keeps `(message type, claimed value)` for the store until central confirms. A second claim gets
`Error.Conflict("já registrada nesta loja")`. A standalone terminal records a **provisional** claim, and a duplicate becomes a High
finding at sync. Central is always the final authority on uniqueness.

## 5. Changes other terminals must see: the site feed (⚑O23 ✅ 2026-10-15)

Real case: during the outage the manager lowers the price of dipyrone from R$ 12,90 to R$ 9,90 because the shelf label is wrong. The
other checkouts must charge the same price.

```csharp
// ✍ Declaration — runs on the manager's terminal, then shared with the store
b.Command<AlterarPrecoNaLoja, Unit>()
    .RequirePermission(PrecoPermissions.Values.AlterarNaLoja)           // must be in the manager's signed snapshot / broker session
    .OnOffline<AlterarPrecoNaLojaOffline>()
    .Offline(offline => offline
        .RecordsFact()
        .SharedWithSite<PrecoNaLojaProjection>());                      // every other terminal applies it when the feed delivers it

// ✍ Origin terminal: checks central's signed guardrails, then applies a store-tier override locally
public sealed class AlterarPrecoNaLojaOffline(IPrecos precos) : IOfflineHandler<AlterarPrecoNaLoja, Unit>
{
    public ValueTask<Unit> HandleAsync(AlterarPrecoNaLoja c, CancellationToken ct)
    {
        var limite = precos.Guardrails(c.ProdutoId);                    // pharmacy: never above the regulated maximum consumer price (PMC)
        if (c.NovoPreco > limite.Maximo) throw Error.Validation("preco.acima.pmc").AsException();
        precos.ApplyStoreOverride(c.ProdutoId, c.NovoPreco, OfflineContext.Current!.Provenance);
        return ValueTask.FromResult(Unit.Value);
    }
}

// ✍ Every other terminal: the same effect, with the guardrail checked again
public sealed class PrecoNaLojaProjection(IPrecos precos) : ISiteFeedProjection<AlterarPrecoNaLoja>
{
    public ValueTask ApplyAsync(AlterarPrecoNaLoja c, SiteFeedEntry entry, CancellationToken ct)
    {
        precos.ApplyStoreOverride(c.ProdutoId, c.NovoPreco, entry.Provenance);
        return ValueTask.CompletedTask;
    }
}
```

`IPrecos.ApplyStoreOverride` is app code (sketch). Underneath, it is a **provisional rule-data version at the store tier**, using the
edit layers the rules engine already has (`RuleTierPath`, `EditableAt`). Central's tenant-level data stays untouched.

**Who sees the new price, and when** (store offline since 14:00, change at 14:30 on CAIXA-01):

| Who | Sees R$ 9,90 | Why |
|---|---|---|
| CAIXA-01 | immediately | its own handler applied it |
| CAIXA-02, BALCAO-01 (connected to the broker) | within seconds | the broker orders it in the site sequence and pushes the feed entry |
| CAIXA-03 (standalone, cable out) | when it reconnects to the broker | feed catch-up in site-sequence order. Until then it sells at R$ 12,90, and those sales cite that price version |
| Central, e-commerce, other stores | after sync | central confirms it as an official store-tier version, or rejects it ([08](08-sync-reconciliation-conflicts.md) §5) |

**Why a cashier cannot fake a feed entry.** Before applying one, every terminal checks all of these:

1. The envelope is signed by an enrolled installation of **this store**.
2. The actor's session (issued by the broker after it verified the manager) is valid and holds the message's permission.
3. The broker signed the entry's place in the site sequence. This also settles two managers changing the same price at once: the later
   one in the sequence wins, and both are recorded.
4. The projection's own checks pass (e.g., central's signed price guardrails).

**Which messages may be shared** (⚑O23, confirmed 2026-10-15, amended by the user) — three layers, each only narrowing the one before:

| Layer | Who | What it decides |
|---|---|---|
| 1. mark | developer | `.SharedWithSite<TProjection>()` in the aggregate configuration: the command **may** be shared. Nothing marked means no feed |
| 2. switch | admin, per site | `Sites.Commands.Configure(SiteFeed: [...])`, signed into the manifest (`SiteManifest.SiteFeed`): on or off for that site; central refuses to switch on an unmarked command |
| 3. rules | the command itself | its normal validation and rules run before the envelope exists, so a change they refuse is never shared |

Recommended start: store-tier price within guardrails, blocking a product or lot for sale (recall), customers registered at the
counter. Anything that changes things for other stores or for central (tenant-level prices, permissions, stock transfers) stays
central-only, because nobody marks it.

**Readability:** feed payloads are signed and readable by the store's terminals (they need them to project). Personal data fields are
already sealed for central with the intake key (privacy 05 §5.5), so neither the broker nor other terminals can read them.

## 6. Approvals at each level (fixes F7; ⚑O15)

| Kind | Issuer | Approver authenticated by | Signature | Caps |
|---|---|---|---|---|
| `Server` (today) | central | identity provider | HMAC (key ring) | rules only |
| **`Site`** (new) | site broker | broker, against the site's credential verifiers (PIN allowed) | ECDsa, broker evidence key | per-token cap + daily cumulative budget (`DescontoOffline`) kept by the broker |
| `Device` (D34) | terminal | terminal, against its **cached** verifier of that approver (passwords only) | ECDsa, terminal evidence key | `OfflineApprovalCap` (smaller), always flagged |

```csharp
// ✍ The same view-model code in every mode — only the token kind differs
async Task PedirAprovacaoAsync(ApprovalRequest request, CancellationToken ct)
{
    var credenciais = await dialog.AskSupervisorAsync();                  // code + PIN/password typed at the checkout
    var token = await approvals.RequestAsync(request, credenciais, ct);   // Central: Server · Site: broker-signed · alone: device-signed
    await Venda.Commands.FinalizarVendaAsync(venda with { Approvals = [token] }, ct);
}
```

`ApprovalTokenKind` gains `Site = 2`. The signed payload layout is unchanged (the kind byte already exists), so existing tokens keep
verifying (the golden test in ⚑Z12 covers it). Device approvals additionally require that the approver's own signed snapshot is cached
on that terminal (the supervisor logged in there online within 72 h). Otherwise a device approval is impossible, which is the safe
answer.

**Verification at replay uses the operation's time, not the replay's (F7a/F7b):**

```csharp
// Before — RulesStage.cs:148: a Friday approval replayed on Monday is "expired"; the approver is checked as of now
var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
var v = verifier.Verify(in token, in request, d.Fingerprint, now);

// After — offline envelopes carry their trusted decision time; checks are "as of" that moment
var at = OfflineContext.Current?.Provenance.DecidedAt.Value ?? clock.GetUtcNow();     // corrected by central (08 §3)
var v = verifier.Verify(in token, in request, d.Fingerprint, at);
var approverHeld = await permissions.HeldAtAsync(token.ApproverId, token.Permission, at, ct);   // as-of, from version history
// held then, revoked since → Fact: finding (low); Request: unchanged rules
```

`IPermissionDirectory.HeldAtAsync` needs the permission-version history that Authorization's `IPermissionVersions` already counts. The
store keeps `(subject, version, changed at)` rows ([08](08-sync-reconciliation-conflicts.md) §3.2).

## 7. Authorization while offline

- **The same evaluator runs everywhere.** On terminals and in the broker, `IMessageAuthorization` (⚑Z5) gets an
  `IAuthorizationContext` built from signed data (`PermissionSnapshot.ToContext()` or the site bundle). This is YFex.Authorization
  library code, not app server code. No second offline authorization code path exists.
- **Offline restrictions** (`OfflineRestrictions`, app-extensible, same shape as `ImpersonationRestrictions` ⚑Z14). These are
  permissions that are never effective offline even if held. Default: `authorization.grants.issue`, role administration,
  `privacy.detokenize`, `privacy.reveal.sensitive` (devices hold no subject keys anyway, privacy 05 §5.5), `installations.enroll`.
  Denied with `DenialKind.UnavailableOffline`.
- **Temporary grants** (⚑Z13) already active when the snapshot was signed apply until their `Until` (trusted clock). New grants
  cannot be issued offline.
- **Impersonation** is never available offline.

## 8. Rules offline (⚑O18 ✅ 2026-10-15)

- A terminal evaluates with the **latest verified rule-data version it holds**, plus any store-tier overrides received on the feed.
  Envelopes carry the `RulesetVersion` (including the override version) and decision fingerprint (`IDecisionBound`), as today.
- At central, for a **fact**: the decision is re-evaluated with the version the terminal cited. Central checks that this version was the
  newest one **delivered** to that installation before the operation (delivery receipts). If a newer version existed but had not
  reached it because it was offline, that is normal: no finding unless `MaxOfflineAge` was exceeded (`StaleRuleData`). Citing a version
  older than one it had acknowledged is a rollback finding. **Facts are never re-priced.** A difference against the current table is a
  finding ("vendido a R$ 10,00; tabela vigente R$ 11,00").
- For a **request**: today's `DecisionMismatchPolicy` (`EnforceOptions.OnMismatch`) applies unchanged.

**Confirmed 2026-10-15 (⚑O18).** "Delivered" is proven by **delivery receipts**: when an installation accepts a bundle (rule data,
manifest, allowance), it signs `(installation, kind, stream, version, accepted at)` with its evidence key and sends it to central
(relay item `offline.delivery`, or RPC); central keeps them (`IDeliveryReceiptStore`) and asks "the newest version of this stream
delivered to this installation before T". A per-command override changes how a difference is treated:

```csharp
b.Command<FinalizarVenda, Recibo>()
    .OnOffline<FinalizarVendaOffline>()
    .Offline(offline => offline
        .RecordsFact()
        .WhenRulesDiffer(RuleVersionPolicy.KeepAndReport));          // default: keep the terminal's decision, report the difference
//      .WhenRulesDiffer(RuleVersionPolicy.Reevaluate)               // re-run with today's version (requests, never money already taken)
//      .WhenRulesDiffer<ConvenioVersionResolver>()                  // IRuleVersionResolver<FinalizarVenda> decides per case
```

Phase 2 builds the delivery receipts and `MaxOfflineAge` (`OfflineLimitKeys.MaxOfflineAge`, 05 §7); `.WhenRulesDiffer(...)` and the
resolver arrive with the `.Offline(...)` verb in Phase 3.

## 9. Tenancy scope

- An installation is **pinned** to its site scope at enrollment (certificate SAN + manifest). The broker serves only its site.
  Envelopes carry the scope they were created in, and central refuses an envelope whose scope is not within its installation's scope
  (fixes the F4 scope half).
- A **roaming installation** (the regional manager's laptop) is enrolled at a higher scope with role `Terminal`. It can use a site's
  broker only at sites whose manifest lists it. Outside, it is `Standalone` in its own scope.
- Device storage stays partitioned by scope (`ScopedKeyValueStore`). One storage key per installation ([05](05-protecting-offline-data.md) §3).
