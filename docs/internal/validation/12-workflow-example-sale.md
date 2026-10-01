# 12 — End-to-end example: POS sale (discounts + installments)

> **Names updated 2026-09-30 (D35–D38):** the engine is domain-agnostic — amounts are any `IAmount<T>`/number (not
> Money), adjustments are `Increase`/`Decrease`, publishing tiers are app-declared `[RuleTier]` fields (no built-in
> Tenant/Store), the effective-date field is `[RuleEffectiveDate]`. The sale examples below are just examples. See 13
> "Domain-agnostic refactor (D35–D38)" for the full rename map.

> Load for: **every phase** as the reference scenario; the acceptance tests of Phases 3–5 reproduce it.
> All names are normative for the example app (`Sales.*` assemblies are the example, not library code).

Assemblies of the example app:

```
Sales.Contracts   situations, commands/queries (markers only), baseline configuration, codes   (client + server)
Sales.Rules       rule set, code rules, tables, guardrails                                    (client + server)
Sales.Client      SaleVm, views                                                               (Avalonia)
Sales.Server      loaders, context rules, handlers, server-override configuration             (server)
```

---

## Step 0 — Contracts (`Sales.Contracts`)

```csharp
// ── Situation: everything the rules may read ──────────────────────────────────────────
[Situation("sale")]
public readonly partial record struct SaleSituation(
    [property: RuleTier] TenantId Tenant,
    [property: RuleTier]  StoreId Store,
    Channel Channel,
    [property: RuleEffectiveDate] DateOnly BusinessDate,
    CustomerTier Tier, bool IsBirthdayMonth,
    ActorLevel Actor,
    PaymentMethod Payment,
    [property: Requested] int Installments,
    [property: Requested] Percentual ManualDiscount,
    ImmutableArray<SaleLineSituation> Lines)
{
    [Derived] public Money Total => SaleMath.Gross(Lines);
}

[Situation("sale.line")]
public readonly partial record struct SaleLineSituation(ProductId Product, CategoryId Category, decimal Quantity, Money UnitPrice);

// ── Messages: marker interfaces only (YFex convention) ───────────────────────────────
public partial class Sale
{
    public static partial class Commands
    {
        public sealed partial record CloseSale(
            SaleId SaleId, CustomerId? CustomerId, StoreId Store, DateOnly BusinessDate,
            PaymentMethod Payment, int Installments, Percentual ManualDiscount,
            ImmutableArray<SaleLineDto> Lines,
            RulesetVersion RulesetVersion, ulong DecisionFingerprint, OverrideToken? Approval)
            : ICommand<Receipt>, IQueueable, IDecisionBound;

        public sealed partial record SaveSaleDraft(SaleId SaleId, CustomerId? CustomerId, ImmutableArray<SaleLineDto> Lines) : ICommand;
    }
    public static partial class Queries
    {
        public sealed partial record GetSaleOptions(SaleSituation Situation) : IQuery<SaleOptionsView>, ICacheable;
    }
}

public sealed partial record SaleLineDto(ProductId Product, decimal Quantity, Money UnitPrice);

// ── Baseline configuration (shared): shape rules + rules enforcement wiring ────────────
public sealed class SaleBaseline : IAggregateConfiguration<Sale>
{
    public void Configure(AggregateConfigurationBuilder<Sale> b)
    {
        b.Command<Sale.Commands.CloseSale, Receipt>()
         .Validation(v =>
         {
             v.Field(c => c.Installments).Range(1, 48);
             v.Field(c => c.ManualDiscount).Range(Percentual.Of(0), Percentual.Of(100));
             v.Field(c => c.Lines).NotEmpty();
             v.Each(c => c.Lines);                                   // SaleLineDto's own rules (Quantity > 0 …)
             v.Invariant(static (in Sale.Commands.CloseSale c, ref ViolationWriter w) =>
             { if (c.Payment == PaymentMethod.Boleto && c.CustomerId is null)
                   w.Add(Sale.Commands.CloseSale.Fields.CustomerId, "sale.boleto-needs-customer"); });
         })
         .RequirePermission(Perms.Sales.Close)
         .UseSituation<SaleSituation, SaleSituationLoader>()
         .Enforce(SaleRules.Set)
         .OnDecisionMismatch(DecisionMismatchPolicy.AcceptAndFlag);

        b.Command<Sale.Commands.SaveSaleDraft>()
         .Validation(v => v.Each(c => c.Lines));                      // draft: only line shape

        b.Query<Sale.Queries.GetSaleOptions, SaleOptionsView>()
         .UseSituation<SaleSituation, IdentitySituationLoader<SaleSituation>>()
         .Enforce(SaleRules.Set)
         .Cacheable();
    }
}

[Validatable]
public sealed partial record SaleLineDto
{
    // DTO (not a CQRS message) → attributes are fine here
    [Range("0.001", "99999")] public decimal Quantity { get; init; }
}
```

## Step 1 — Rules (`Sales.Rules`)

```csharp
[RuleSet<SaleSituation>("sale")]
public static partial class SaleRules
{
    public static readonly Stage Items    = Stage.First("items");
    public static readonly Stage Subtotal = Stage.After(Items, "subtotal");
    public static readonly Stage Payment  = Stage.After(Subtotal, "payment");
    public static readonly Stage Limits   = Stage.After(Payment, "limits");

    public static readonly RuleGroup Promotions   = new(Items,    Resolve.Exclusive(Prefer.Largest));
    public static readonly RuleGroup Loyalty      = new(Subtotal, Resolve.Stack(cap: Percentual.Of(20)));
    public static readonly RuleGroup Manual       = new(Subtotal, Resolve.MostRestrictive);
    public static readonly RuleGroup PaymentDisc  = new(Payment,  Resolve.First);
    public static readonly RuleGroup Installments = new(Limits,   Resolve.MostRestrictive);

    public static RoundingPolicy Rounding => RoundingPolicy.Currency(2, MidpointRounding.AwayFromZero);

    [Requested]
    public static readonly DerivedField<SaleSituation, Money> InstallmentValue =
        DerivedField.After(Payment, "InstallmentValue",
            static (in SaleSituation s, in RunningTotals<Money> t) => s.Installments > 0 ? t.Final / s.Installments : t.Final);
}

// Line rule (Items stage) — code, sealed logic, promo product list is a parameter
[Rule("promo.l3p2", Group = nameof(SaleRules.Promotions), Version = 7)]
public readonly struct Leve3Pague2 : IRule<SaleSituation, SaleLineSituation>
{
    public static readonly RuleParameter<ProductSet> Products = new("products", ProductSet.Empty, ["Tenant", "Store"]);
    public static bool Applies(in SaleSituation s, in SaleLineSituation l, in RuleContext c) => l.Quantity >= 3 && c.Get(Products).Contains(l.Product);
    public static void Evaluate(in SaleSituation s, in SaleLineSituation l, ref DirectiveWriter d)
        => d.Decrease(AdjustTarget.Line, l.UnitPrice * Math.Floor(l.Quantity / 3));
}

[Rule("loyalty.gold", Group = nameof(SaleRules.Loyalty), Version = 3)]
public readonly struct GoldLoyalty : IRule<SaleSituation>
{
    public static readonly RuleParameter<Percentual> DiscountRate = new("rate", Percentual.Of(5), ["Tenant"], guard: new(Percentual.Of(0), Percentual.Of(15)));
    public static bool Applies(in SaleSituation s) => s.Tier == CustomerTier.Gold;
    public static void Evaluate(in SaleSituation s, in RuleContext c, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, c.Get(DiscountRate));
}

[Rule("loyalty.bday", Group = nameof(SaleRules.Loyalty))]
public readonly struct BirthdayDiscount : IRule<SaleSituation>
{
    public static bool Applies(in SaleSituation s) => s.IsBirthdayMonth;
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, Percentual.Of(3));
}

[Rule("payment.pix", Group = nameof(SaleRules.PaymentDisc), Version = 2)]
public readonly struct PixDiscount : IRule<SaleSituation>
{
    public static bool Applies(in SaleSituation s) => s.Payment == PaymentMethod.Pix;
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, Percentual.Of(2));
}

[Sealed, Rule("sale.installments.boleto", Group = nameof(SaleRules.Installments))]
public readonly struct BoletoSingleInstallment : IRule<SaleSituation>
{
    public static bool Applies(in SaleSituation s) => s.Payment == PaymentMethod.Boleto;
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d) => d.Limit(SaleSituation.Fields.Installments, max: 1);
}

// Tenant/store-editable table (05 §1) — InstallmentTable with InstallmentGuardrails (sealed ≤ 24x, ≥ R$5)

// Seller discretion: limit depends on the actor; above → manager approval (not deny)
[DecisionTable<SaleSituation>("sale.manual-discount", HitPolicy.First,
    Group = nameof(SaleRules.Manual), EditableAt = new[] { "Tenant" }, PublishPermission = "sales.rules.manual-discount.publish")]
public partial class ManualDiscountTable
{
    [Validatable]
    public readonly partial record struct Row(
        [property: When("Actor")] Match<ActorLevel> Actor,
        [property: Then(DirectiveKind.LimitMax, "ManualDiscount", OnExceed = Exceed.RequireApproval,
                        ApprovalPermission = "sales.discount.override"), Range("0", "100")] Percentual Max);

    public static ReadOnlySpan<Row> DefaultRows =>
    [
        new(Is(ActorLevel.Seller),  Percentual.Of(5)),
        new(Is(ActorLevel.Manager), Percentual.Of(15)),
        new(Any,                    Percentual.Of(0)),
    ];
}
```

## Step 2 — View model (`Sales.Client`)

```csharp
[ValidatesAs<Sale.Commands.SaveSaleDraft>(Profile = "draft")]
[ValidatesAs<Sale.Commands.CloseSale>(Profile = "final")]
public partial class SaleVm(IDispatcher dispatcher, TimeProvider clock, ISession session, INavigator navigator, IToast toast)
    : MvvmStateObject
{
    // input
    [Observable] public partial CustomerId? CustomerId { get; set; }
    [Observable] public partial PaymentMethod Payment { get; set; }
    [Observable] public partial int Installments { get; set; }
    [Observable] public partial Percentual ManualDiscount { get; set; }
    [Observable] public partial StateList<SaleLineVm> Lines { get; set; }

    // async facts
    [Observable] public partial CustomerTier Tier { get; set; }
    [Observable] public partial bool IsBirthdayMonth { get; set; }

    // context captured once
    private readonly SaleId _saleId = SaleId.New();
    private readonly DateOnly _businessDate = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
    private OverrideToken? _approval;

    [Observable, ConstraintsFrom] public partial Decision<SaleSituation> Policy { get; set; }

    // 1. GATHER (async)
    [ReactsTo(nameof(CustomerId), CancelPrevious = true), Debounce(300)]
    async Task LoadCustomerFactsAsync(CancellationToken ct)
    {
        if (CustomerId is not { } id) { using (BeginUpdate()) { Tier = CustomerTier.None; IsBirthdayMonth = false; } return; }
        var r = await dispatcher.QueryAsync<Customer.Queries.GetLoyalty, LoyaltyInfo>(new(id), ct);
        if (r.TryGetValue(out var info))
            using (BeginUpdate()) { Tier = info.Tier; IsBirthdayMonth = info.BirthMonth == _businessDate.Month; }
    }

    // 2. BUILD
    SaleSituation BuildSituation() => new(session.Tenant, session.Store, Channel.Pos, _businessDate,
        Tier, IsBirthdayMonth, session.ActorLevel, Payment, Installments, ManualDiscount, Lines.ToSituations());

    // 3. DECIDE (sync)
    [ReactsTo(nameof(Tier), nameof(IsBirthdayMonth), nameof(Payment), nameof(Installments), nameof(ManualDiscount), nameof(Lines))]
    void RecomputePolicy() => Policy = SaleRules.Evaluate(BuildSituation(), new() { Trace = TraceLevel.Full });

    [Computed] public Money Total => Policy.Totals.Final;
    [Computed] public bool NeedsApproval => Policy.Verdict.IsNeedsApproval && _approval is null;
    [Computed] public bool CanClose => Policy.Verdict.IsAllowed && !Validation.HasErrors;

    // 4. SUBMIT
    [StateCommand, Gate(nameof(CanClose))]
    async Task CloseAsync(CancellationToken ct)
    {
        if (!ValidateAll("final").IsValid) return;
        var cmd = new Sale.Commands.CloseSale(_saleId, CustomerId, session.Store, _businessDate, Payment, Installments,
            ManualDiscount, Lines.ToDtos(), Policy.RulesetVersion, Policy.Fingerprint, _approval);
        var result = await dispatcher.CommandAsync<Sale.Commands.CloseSale, Receipt>(cmd, ct);
        switch (ApplyServer(in result))
        {
            case ServerOutcome.Ok:     await navigator.NavigateTo(new ReceiptRoute(result.OkValue!.Id)); break;
            case ServerOutcome.Queued: toast.Info("sale.saved-offline"); break;
        }
    }

    [StateCommand] async Task RequestApprovalAsync(CancellationToken ct) { /* 08 §8 → _approval = token; RecomputePolicy() */ }
}
```

## Step 3 — View (Avalonia fluent markup)

```csharp
YComboBox().Bind(vm.Payment)
YIntegerInput().Bind(vm.Installments)            // Maximum ← Decision (e.g. 6 or 1 for Pix), static Range(1,48)
YPercentInput().Bind(vm.ManualDiscount)          // over seller limit → warning + approval state
YRuleBreakdown().Explain(vm.Policy)              // 06 §5 breakdown
YButton("Solicitar aprovação").Bind(vm.RequestApprovalCommand).IsVisible(vm.NeedsApproval)
YButton("Fechar venda").Bind(vm.CloseCommand)    // disabled by Gate(CanClose)
```

## Step 4 — Server (`Sales.Server`)

```csharp
public sealed class SaleServerOnly : IAggregateConfiguration<Sale>
{
    public void Configure(AggregateConfigurationBuilder<Sale> b)
        => b.Command<Sale.Commands.CloseSale, Receipt>()
            .Validate<StockAvailable>()
            .Validate<CustomerNotBlocked>();
}

public sealed class SaleSituationLoader(ICustomerStore customers, IActorContext actor, ITenantContext tenant)
    : ISituationLoader<Sale.Commands.CloseSale, SaleSituation>
{
    public async ValueTask<SaleSituation> LoadAsync(Sale.Commands.CloseSale c, CancellationToken ct)
    {
        var info = c.CustomerId is { } id ? await customers.GetLoyaltyAsync(id, ct) : LoyaltyInfo.None;
        return new(tenant.Id, c.Store, Channel.Pos, c.BusinessDate, info.Tier, info.BirthMonth == c.BusinessDate.Month,
                   actor.Level, c.Payment, c.Installments, c.ManualDiscount, c.Lines.ToSituations());
    }
}

public sealed class StockAvailable(IStockQueries stock) : IContextRule<Sale.Commands.CloseSale>
{
    public async ValueTask CheckAsync(Sale.Commands.CloseSale c, ViolationCollector v, CancellationToken ct)
    {
        foreach (var m in await stock.MissingFor(c.Lines, ct))
            v.Add(FieldPath.Of("Lines[].Quantity", m.Index), "stock.out", RawValue.From(m.Available));
    }
}

public sealed class CloseSaleHandler(ISaleStore store) : IRuledCommandHandler<Sale.Commands.CloseSale, Receipt, SaleSituation>
{
    public async Task<Receipt> HandleAsync(Valid<Sale.Commands.CloseSale> cmd, Decision<SaleSituation> d, CancellationToken ct)
    {
        var sale = SaleAggregate.Create(cmd.Value, d.Totals);
        sale.RecordAdjustments(d.Adjustments);     // rule code + version + base + amount + per-line allocation (vDesc)
        using var situationBytes = new ArrayPoolBufferWriter<byte>();       // YFex core (YFex.Collections)
        SaleSituation.WriteTo(d.Situation, situationBytes);          // canonical bytes for replay/audit (04 §1.3)
        sale.RecordDecision(d.Fingerprint, d.RulesetVersion, situationBytes.WrittenSpan);
        await store.SaveAsync(sale, ct);
        return new Receipt(sale.Id, d.Totals.Final);
    }
}
```

## Step 5 — What the user sees for each failure

| Situation | Caught at | User sees |
|---|---|---|
| Installments = 60 | client, shape (Range 1–48) | error on the installments field |
| 12x with Pix | client, `Policy` live | picker/input max 1; message "Pix: somente 1x" |
| Seller types 8% (limit 5%) | client, `Policy` (ManualDiscountTable) | warning + "Solicitar aprovação" |
| Boleto without customer | client, invariant | error on customer field |
| Out of stock line 3 | **server only**, context rule | `ApplyServer` puts it on line 3 quantity |
| Offline sale, table changed meanwhile | server replay | accepted + audit flag (AcceptAndFlag) |
| No approval token for 8% | server, stage 4 | `ApprovalRequired` → approval dialog |

## Step 6 — Tenant edits installments (runtime, first release)

Admin → "Parcelamento" editor (08 §7) → changes CreditCard ≥ R$1000 from 12x to 10x → publish with
EffectiveFrom tomorrow (permission `sales.rules.installments.publish`) → version 43 stored → event →
POS caches v43 → sales dated tomorrow evaluate with v43; today's with v42; receipts keep their
`RuleRef` versions. Trying to publish 36x → blocked in the editor by `InstallmentGuardrails` (sealed 24x).

## Step 7 — Offline

POS offline at 09:00 (has v42). 09:30 sale → queued with fingerprint F1 (shape validated before enqueue).
11:00 reconnect → replay → server evaluates as of 09:30 → F1 → accepted. See 11 §2.3 for the stale case.

## Step 8 — Tests

```csharp
[Fact] public void Gold_birthday_stacks_under_cap()
{
    var s = SaleSituationBuilder.Default with { Tier = CustomerTier.Gold, IsBirthdayMonth = true };
    var d = SaleRules.Evaluate(s, new() { Trace = TraceLevel.Full });
    d.ShouldApply("loyalty.gold").ShouldApply("loyalty.bday");
    d.ShouldNotApply("promo.blackfriday", because: TraceOutcome.Superseded);
}

[Fact] public void Pix_limits_installments_to_one()
    => SaleRules.Evaluate(SaleSituationBuilder.Default with { Payment = PaymentMethod.Pix, Installments = 3 })
                .Verdict.IsDenied.ShouldBeTrue();

[Fact] public void Seller_above_limit_needs_approval()
    => SaleRules.Evaluate(SaleSituationBuilder.Default with { Actor = ActorLevel.Seller, ManualDiscount = Percentual.Of(8) })
                .Verdict.IsNeedsApproval.ShouldBeTrue();
```

---

## Companion example — customer form borrowing contract rules

See 07 §4.1 (`CustomerFormVm` with `[ValidatesAs<CreateCustomer>]`, `[MapsTo("Document")]`, extra
`ConfirmEmail` rule and invariant) and 09 §2.1 (the contract's fluent rules).
