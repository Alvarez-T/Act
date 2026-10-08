using System.Collections.Immutable;
using YFex.Rules.Tests.HandWritten;
using YFex.Validation;
using static YFex.Rules.Cells;

namespace YFex.Rules.Tests.Generated;

// The doc-12 sale as an app writes it — every ISituation / Set / table member below is generated.

[Situation("sale")]
public readonly partial record struct SaleSituation(
    [property: RuleTier] TenantId Tenant,
    [property: RuleTier] StoreId Store,
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
public readonly partial record struct SaleLineSituation(string Product, string Category, decimal Quantity, Money UnitPrice);

public static class SaleMath
{
    public static Money Gross(ImmutableArray<SaleLineSituation> lines)
    {
        Money sum = Money.Zero;
        if (!lines.IsDefault) foreach (var l in lines) sum += l.UnitPrice * l.Quantity;
        return sum;
    }
}

[RuleSet<SaleSituation>("sale")]
public static partial class SaleRules
{
    public static readonly Stage Items = Stage.First("items");
    public static readonly Stage Subtotal = Stage.After(Items, "subtotal");
    public static readonly Stage Payment = Stage.After(Subtotal, "payment");
    public static readonly Stage Limits = Stage.After(Payment, "limits");

    public static readonly RuleGroup Promotions = new(Items, Resolve.Exclusive(Prefer.Largest));
    public static readonly RuleGroup Loyalty = new(Subtotal, Resolve.Stack(cap: Percentual.FromPercentage(20)));
    public static readonly RuleGroup Manual = new(Subtotal, Resolve.MostRestrictive);
    public static readonly RuleGroup PaymentDisc = new(Payment, Resolve.First);
    public static readonly RuleGroup Installments = new(Limits, Resolve.MostRestrictive);

    public static RoundingPolicy Rounding => RoundingPolicy.Currency(2, MidpointRounding.AwayFromZero);

    /// <summary>Line base: declarative (the engine never assumes Quantity × UnitPrice).</summary>
    public static Money LineBase(in SaleSituation s, in SaleLineSituation line) => line.UnitPrice * line.Quantity;

    [Requested]
    public static readonly DerivedField<SaleSituation, Money> InstallmentValue = DerivedField.After(Payment, "InstallmentValue",
        static (in SaleSituation s, in RunningTotals<Money> t) => s.Installments > 0 ? t.Final / s.Installments : t.Final);
}

[Rule("promo.l3p2", Group = nameof(SaleRules.Promotions), Version = 7)]
public readonly struct Leve3Pague2 : IRule<SaleSituation, SaleLineSituation>
{
    public static readonly RuleParameter<ProductSet> Products = new("products", new ProductSet("coca"), [nameof(SaleSituation.Tenant), nameof(SaleSituation.Store)]);
    public static bool Applies(in SaleSituation s, in SaleLineSituation l, in RuleContext c) => l.Quantity >= 3 && c.Get(Products).Contains(l.Product);
    public static void Evaluate(in SaleSituation s, in SaleLineSituation l, ref DirectiveWriter d)
        => d.Decrease(AdjustTarget.Line, l.UnitPrice * Math.Floor(l.Quantity / 3));
}

[Rule("promo.blackfriday", Group = nameof(SaleRules.Promotions))]
public readonly struct BlackFriday : IRule<SaleSituation, SaleLineSituation>
{
    public static bool Applies(in SaleSituation s, in SaleLineSituation l) => s.BusinessDate.Month == 11 && l.Category != "services";
    public static void Evaluate(in SaleSituation s, in SaleLineSituation l, ref DirectiveWriter d)
        => d.Decrease(AdjustTarget.Line, Percentual.FromPercentage(10));
}

[Rule("loyalty.gold", Group = nameof(SaleRules.Loyalty), Priority = 100, Version = 3)]
public readonly struct GoldLoyalty : IRule<SaleSituation>
{
    public static readonly RuleParameter<Percentual> DiscountRate =
        new("rate", Percentual.FromPercentage(5), [nameof(SaleSituation.Tenant)], guard: new(Percentual.Zero, Percentual.FromPercentage(15)));
    public static bool Applies(in SaleSituation s) => s.Tier == CustomerTier.Gold;
    public static void Evaluate(in SaleSituation s, in RuleContext c, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, c.Get(DiscountRate));
}

[Rule("loyalty.bday", Group = nameof(SaleRules.Loyalty))]
public readonly struct BirthdayDiscount : IRule<SaleSituation>
{
    public static bool Applies(in SaleSituation s) => s.IsBirthdayMonth;
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, Percentual.FromPercentage(3));
}

[Rule("sale.manual", Group = nameof(SaleRules.Manual))]
public readonly struct ManualDiscountRule : IRule<SaleSituation>
{
    public static bool Applies(in SaleSituation s) => s.ManualDiscount > Percentual.Zero;
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, s.ManualDiscount);
}

[Rule("payment.pix", Group = nameof(SaleRules.PaymentDisc), Version = 2)]
public readonly struct PixDiscount : IRule<SaleSituation>
{
    public static bool Applies(in SaleSituation s) => s.Payment == PaymentMethod.Pix;
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, Percentual.FromPercentage(2));
}

[Sealed, Rule("sale.installments.boleto", Group = nameof(SaleRules.Installments))]
public readonly struct BoletoSingleInstallment : IRule<SaleSituation>
{
    public static bool Applies(in SaleSituation s) => s.Payment == PaymentMethod.Boleto;
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d) => d.Limit(SaleSituation.Fields.Installments, max: 1);
}

[Sealed, Rule("sale.installments.legal", Group = nameof(SaleRules.Installments))]
public readonly struct InstallmentGuardrails : IRule<SaleSituation>, IRowGuard<InstallmentTable.Row>
{
    public static bool Applies(in SaleSituation s) => true;
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d) => d.Limit(SaleRules.InstallmentValue, min: new Money(5));
    public static void Check(in InstallmentTable.Row r, ref ViolationWriter w)
    {
        if (r.MaxInstallments > 24) w.Add(InstallmentTable.Row.Fields.MaxInstallments, "sale.installments.above-legal", RawValue.From(24));
    }
}

[DecisionTable<SaleSituation>("sale.installments", HitPolicy.First,
    Group = nameof(SaleRules.Installments), EditableAt = new[] { nameof(SaleSituation.Tenant), nameof(SaleSituation.Store) },
    PublishPermission = RulesTestPermissions.Ids.InstallmentsPublish, Guardrail = typeof(InstallmentGuardrails))]
public partial class InstallmentTable
{
    [Validatable]
    public readonly partial record struct Row(
        [property: When("Payment")] Match<PaymentMethod> Payment,
        [property: When("Total")] Band<Money> Total,
        [property: When("Tier")] Match<CustomerTier> Tier,
        [property: Then(DirectiveKind.LimitMax, "Installments"), Range(1, 48)] int MaxInstallments,
        [property: Then(DirectiveKind.LimitMin, "InstallmentValue")] Money MinInstallment);

    public static readonly Row[] DefaultRows =
    [
        new(Is(PaymentMethod.CreditCard), From(new Money(1000)), Is(CustomerTier.Gold), 18, new Money(80)),
        new(Is(PaymentMethod.CreditCard), From(new Money(1000)), Any, 12, new Money(80)),
        new(Is(PaymentMethod.CreditCard), Between(new Money(300), new Money(1000)), Any, 6, new Money(50)),
        new(Is(PaymentMethod.CreditCard), Below(new Money(300)), Any, 3, new Money(50)),
        new(In(PaymentMethod.Pix, PaymentMethod.Boleto, PaymentMethod.Cash), Any, Any, 1, Money.Zero),
    ];
}

[DecisionTable<SaleSituation>("sale.manual-discount", HitPolicy.First,
    Group = nameof(SaleRules.Manual), EditableAt = new[] { nameof(SaleSituation.Tenant) }, PublishPermission = RulesTestPermissions.Ids.ManualDiscountPublish)]
public partial class ManualDiscountTable
{
    public readonly partial record struct Row(
        [property: When("Actor")] Match<ActorLevel> Actor,
        [property: Then(DirectiveKind.LimitMax, "ManualDiscount", OnExceed = Exceed.RequireApproval, ApprovalPermission = RulesTestPermissions.Ids.DiscountOverride)] Percentual Max);

    public static readonly Row[] DefaultRows =
    [
        new(Is(ActorLevel.Seller), Percentual.FromPercentage(5)),
        new(Is(ActorLevel.Manager), Percentual.FromPercentage(15)),
        new(Any, Percentual.Zero),
    ];
}

/// <summary>Line table: category discounts (Items stage), competing with the promotions.</summary>
[DecisionTable<SaleSituation>("sale.category-discount", HitPolicy.First, Group = nameof(SaleRules.Promotions), EditableAt = new[] { nameof(SaleSituation.Store) })]
public partial class CategoryDiscountTable
{
    public readonly partial record struct Row(
        [property: When("Lines[].Category")] Match<string> Category,
        [property: Then(DirectiveKind.DecreasePercent)] Percentual Discount);

    public static readonly Row[] DefaultRows = [new(Is("cables"), Percentual.FromPercentage(50))];
}

public static class SaleBuilder
{
    public static readonly SaleLineSituation Coca = new("coca", "drinks", 3, new Money(10));
    public static readonly SaleLineSituation Tv = new("tv", "electronics", 1, new Money(1100));
    public static readonly SaleLineSituation Cable = new("cable", "electronics", 2, new Money(35));

    public static SaleSituation Default => new(new TenantId("t1"), new StoreId("s1"), Channel.Pos, new DateOnly(2026, 9, 29),
        CustomerTier.None, false, ActorLevel.Seller, PaymentMethod.Cash, 1, Percentual.Zero, [Coca, Tv, Cable]);
}
