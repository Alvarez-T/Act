using System.Collections.Immutable;
using YFex.Validation;
using static YFex.Rules.Cells;

namespace YFex.Rules.Tests.HandWritten;

// Doc-12 rules written by hand, wired into the definition the way the generator will wire them.

public sealed class ProductSet(params string[] products)
{
    private readonly HashSet<string> _set = new(products, StringComparer.Ordinal);
    public static ProductSet Empty { get; } = new();
    public bool Contains(string product) => _set.Contains(product);
}

public readonly struct Leve3Pague2 : IRule<HSale, HSaleLine>
{
    public static readonly RuleParameter<ProductSet> Products = new("products", new ProductSet("coca"), ["Tenant", "Store"]);
    public static bool Applies(in HSale s, in HSaleLine l, in RuleContext c) => l.Quantity >= 3 && c.Get(Products).Contains(l.Product);
    public static void Evaluate(in HSale s, in HSaleLine l, ref DirectiveWriter d)
        => d.Decrease(AdjustTarget.Line, l.UnitPrice * Math.Floor(l.Quantity / 3));
}

public readonly struct BlackFriday : IRule<HSale, HSaleLine>
{
    public static bool Applies(in HSale s, in HSaleLine l) => s.BusinessDate.Month == 11;
    public static void Evaluate(in HSale s, in HSaleLine l, ref DirectiveWriter d)
        => d.Decrease(AdjustTarget.Line, Percentual.FromPercentage(10));
}

public readonly struct GoldLoyalty : IRule<HSale>
{
    public static readonly RuleParameter<Percentual> DiscountRate =
        new("rate", Percentual.FromPercentage(5), ["Tenant"], guard: new(Percentual.Zero, Percentual.FromPercentage(15)));
    public static bool Applies(in HSale s) => s.Tier == CustomerTier.Gold;
    public static void Evaluate(in HSale s, in RuleContext c, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, c.Get(DiscountRate));
}

public readonly struct BirthdayDiscount : IRule<HSale>
{
    public static bool Applies(in HSale s) => s.IsBirthdayMonth;
    public static void Evaluate(in HSale s, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, Percentual.FromPercentage(3));
}

public readonly struct ManualDiscountRule : IRule<HSale>
{
    public static bool Applies(in HSale s) => s.ManualDiscount > Percentual.Zero;
    public static void Evaluate(in HSale s, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, s.ManualDiscount);
}

public readonly struct PixDiscount : IRule<HSale>
{
    public static bool Applies(in HSale s) => s.Payment == PaymentMethod.Pix;
    public static void Evaluate(in HSale s, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, Percentual.FromPercentage(2));
}

public readonly struct BoletoSingleInstallment : IRule<HSale>
{
    public static bool Applies(in HSale s) => s.Payment == PaymentMethod.Boleto;
    public static void Evaluate(in HSale s, ref DirectiveWriter d) => d.Limit(HSale.Fields.Installments, max: 1);
}

public readonly struct InstallmentGuardrails : IRule<HSale>, IRowGuard<InstallmentTable.Row>
{
    public static bool Applies(in HSale s) => true;
    public static void Evaluate(in HSale s, ref DirectiveWriter d) => d.Limit(HandSaleRules.InstallmentValue, min: new Money(5));
    public static void Check(in InstallmentTable.Row r, ref ViolationWriter w)
    {
        if (r.MaxInstallments > 24) w.Add(FieldPath.Of("MaxInstallments"), "sale.installments.above-legal", RawValue.From(24));
    }
}

public sealed class InstallmentTable : IDecisionTable<HSale, InstallmentTable.Row>
{
    public readonly record struct Row(Match<PaymentMethod> Payment, Band<Money> Total, Match<CustomerTier> Tier, int MaxInstallments, Money MinInstallment);

    public static string Code => "sale.installments";

    public static TableSchema Schema { get; } = new("sale.installments", "sale", HitPolicy.First, ["Tenant", "Store"],
        RulesTestPermissions.InstallmentsPublish, isLineTable: false, complete: false,
    [
        new(0, "Payment", ColumnRole.When, CellShape.Match, "Payment", RawType.EnumCode),
        new(1, "Total", ColumnRole.When, CellShape.Band, "Total", Money.RawType),
        new(2, "Tier", ColumnRole.When, CellShape.Match, "Tier", RawType.EnumCode),
        new(3, "MaxInstallments", ColumnRole.Then, CellShape.Value, "Installments", RawType.Int32, DirectiveKind.LimitMax),
        new(4, "MinInstallment", ColumnRole.Then, CellShape.Value, "InstallmentValue", Money.RawType, DirectiveKind.LimitMin),
    ]);

    static ReadOnlySpan<Row> IDecisionTable<HSale, Row>.DefaultRows => DefaultRows;

    public static readonly Row[] DefaultRows =
    [
        new(Is(PaymentMethod.CreditCard), From(new Money(1000)), Is(CustomerTier.Gold), 18, new Money(80)),
        new(Is(PaymentMethod.CreditCard), From(new Money(1000)), Any, 12, new Money(80)),
        new(Is(PaymentMethod.CreditCard), Between(new Money(300), new Money(1000)), Any, 6, new Money(50)),
        new(Is(PaymentMethod.CreditCard), Below(new Money(300)), Any, 3, new Money(50)),
        new(In(PaymentMethod.Pix, PaymentMethod.Boleto, PaymentMethod.Cash), Any, Any, 1, Money.Zero),
    ];

    public static int ConditionCount => 3;

    public static int Match(in Row row, in HSale s, int lineIndex)
    {
        if (!row.Payment.Matches(s.Payment)) return 0;
        if (!row.Total.Contains(s.Total)) return 1;
        if (!row.Tier.Matches(s.Tier)) return 2;
        return -1;
    }

    public static void Emit(in Row row, in HSale s, int lineIndex, ref DirectiveWriter d)
    {
        d.LimitRaw(HSale.Fields.Installments, RawValue.None, RawValue.From(row.MaxInstallments));
        d.LimitRaw(HandSaleRules.InstallmentValue, RawValue.From(row.MinInstallment), RawValue.None);
    }

    public static void CheckRow(in Row row, ref ViolationWriter w)
    {
        if (row.MaxInstallments < 1 || row.MaxInstallments > 48) w.Add(FieldPath.Of("MaxInstallments"), "range", RawValue.From(1), RawValue.From(48));
        InstallmentGuardrails.Check(in row, ref w);
    }

    public static bool Intersects(in Row a, in Row b, int column) => column switch
    {
        0 => a.Payment.Intersects(b.Payment),
        1 => a.Total.Intersects(b.Total),
        _ => a.Tier.Intersects(b.Tier),
    };

    public static bool Covers(in Row a, in Row b, int column) => column switch
    {
        0 => a.Payment.Covers(b.Payment),
        1 => a.Total.Covers(b.Total),
        _ => a.Tier.Covers(b.Tier),
    };

    public static void WriteRow(System.Text.Json.Utf8JsonWriter w, in Row row)
    {
        w.WritePropertyName("Payment"); TableJson.WriteMatch(w, row.Payment, EnumJson<PaymentMethod>.Write);
        w.WritePropertyName("Total"); TableJson.WriteBand(w, row.Total, JsonScalar.Write<Money>);
        w.WritePropertyName("Tier"); TableJson.WriteMatch(w, row.Tier, EnumJson<CustomerTier>.Write);
        w.WritePropertyName("MaxInstallments"); JsonScalar.Write(w, row.MaxInstallments);
        w.WritePropertyName("MinInstallment"); JsonScalar.Write(w, row.MinInstallment);
    }

    public static Row ReadRow(ref System.Text.Json.Utf8JsonReader r)
    {
        Row row = default;
        while (TableJson.NextProperty(ref r, out var name))
        {
            switch (name)
            {
                case "Payment": row = row with { Payment = TableJson.ReadMatch(ref r, EnumJson<PaymentMethod>.Read) }; break;
                case "Total": row = row with { Total = TableJson.ReadBand(ref r, JsonScalar.Read<Money>) }; break;
                case "Tier": row = row with { Tier = TableJson.ReadMatch(ref r, EnumJson<CustomerTier>.Read) }; break;
                case "MaxInstallments": row = row with { MaxInstallments = JsonScalar.Read<int>(ref r) }; break;
                case "MinInstallment": row = row with { MinInstallment = JsonScalar.Read<Money>(ref r) }; break;
                default: r.Skip(); break;
            }
        }
        return row;
    }
}

public sealed class ManualDiscountTable : IDecisionTable<HSale, ManualDiscountTable.Row>
{
    public readonly record struct Row(Match<ActorLevel> Actor, Percentual Max);

    public static string Code => "sale.manual-discount";

    public static TableSchema Schema { get; } = new("sale.manual-discount", "sale", HitPolicy.First, ["Tenant"],
        RulesTestPermissions.ManualDiscountPublish, isLineTable: false, complete: false,
    [
        new(0, "Actor", ColumnRole.When, CellShape.Match, "Actor", RawType.EnumCode),
        new(1, "Max", ColumnRole.Then, CellShape.Value, "ManualDiscount", Percentual.RawType, DirectiveKind.LimitMax),
    ]);

    static ReadOnlySpan<Row> IDecisionTable<HSale, Row>.DefaultRows => DefaultRows;

    public static readonly Row[] DefaultRows =
    [
        new(Is(ActorLevel.Seller), Percentual.FromPercentage(5)),
        new(Is(ActorLevel.Manager), Percentual.FromPercentage(15)),
        new(Any, Percentual.Zero),
    ];

    public static int ConditionCount => 1;
    public static int Match(in Row row, in HSale s, int lineIndex) => row.Actor.Matches(s.Actor) ? -1 : 0;

    public static void Emit(in Row row, in HSale s, int lineIndex, ref DirectiveWriter d)
        => d.LimitRaw(HSale.Fields.ManualDiscount, RawValue.None, RawValue.From(row.Max), Exceed.RequireApproval, RulesTestPermissions.DiscountOverride);

    public static void CheckRow(in Row row, ref ViolationWriter w) { }
    public static bool Intersects(in Row a, in Row b, int column) => a.Actor.Intersects(b.Actor);
    public static bool Covers(in Row a, in Row b, int column) => a.Actor.Covers(b.Actor);

    public static void WriteRow(System.Text.Json.Utf8JsonWriter w, in Row row)
    {
        w.WritePropertyName("Actor"); TableJson.WriteMatch(w, row.Actor, EnumJson<ActorLevel>.Write);
        w.WritePropertyName("Max"); JsonScalar.Write(w, row.Max);
    }

    public static Row ReadRow(ref System.Text.Json.Utf8JsonReader r)
    {
        Row row = default;
        while (TableJson.NextProperty(ref r, out var name))
        {
            if (name == "Actor") row = row with { Actor = TableJson.ReadMatch(ref r, EnumJson<ActorLevel>.Read) };
            else if (name == "Max") row = row with { Max = JsonScalar.Read<Percentual>(ref r) };
            else r.Skip();
        }
        return row;
    }
}

public static class HandSaleRules
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

    public static readonly DerivedField<HSale, Money> InstallmentValue = DerivedField.After(Payment, "InstallmentValue",
        static (in HSale s, in RunningTotals<Money> t) => s.Installments > 0 ? t.Final / s.Installments : t.Final);

    private static RuleSetDefinition<HSale>? s_set;
    public static RuleSetDefinition<HSale> Set => s_set ??= Build();

    public static Decision<HSale> Evaluate(in HSale s, in EvaluateOptions options = default) => Set.Evaluate(in s, in options);

    private static RuleSetDefinition<HSale> Build() => new RuleSetDefinitionBuilder<HSale>("sale", codeVersion: 0x1234)
        .Group(nameof(Promotions), Promotions)
        .Group(nameof(Loyalty), Loyalty)
        .Group(nameof(Manual), Manual)
        .Group(nameof(PaymentDisc), PaymentDisc)
        .Group(nameof(Installments), Installments)
        .Rounding(Rounding)
        .Amount<Money>()
        .LineBase(static (in HSale s, int i) => Amounts.ToDecimal(s.Lines[i].UnitPrice * s.Lines[i].Quantity))
        .Derived(InstallmentValue, requested: true)
        .Rule(RuleDescriptor<HSale>.CreateLine("promo.l3p2", nameof(Promotions),
            static (in HSale s, int i, in RuleContext c) => Leve3Pague2.Applies(in s, in s.Lines.ItemRef(i), in c),
            static (in HSale s, int i, in RuleContext c, ref DirectiveWriter d) => Leve3Pague2.Evaluate(in s, in s.Lines.ItemRef(i), ref d),
            version: 7, parameters: [Leve3Pague2.Products]))
        .Rule(RuleDescriptor<HSale>.CreateLine("promo.blackfriday", nameof(Promotions),
            static (in HSale s, int i, in RuleContext c) => BlackFriday.Applies(in s, in s.Lines.ItemRef(i)),
            static (in HSale s, int i, in RuleContext c, ref DirectiveWriter d) => BlackFriday.Evaluate(in s, in s.Lines.ItemRef(i), ref d),
            explain: static (in HSale s, int i) => s.BusinessDate.Month == 11 ? byte.MaxValue : (byte)0,
            conditions: [new(0, "BusinessDate.Month == 11", "rules.cond.eq", "BusinessDate", RawValue.From(11))]))
        .Rule(RuleDescriptor<HSale>.Create("loyalty.gold", nameof(Loyalty),
            static (in HSale s, in RuleContext c) => GoldLoyalty.Applies(in s),
            static (in HSale s, in RuleContext c, ref DirectiveWriter d) => GoldLoyalty.Evaluate(in s, in c, ref d),
            priority: 100, version: 3, parameters: [GoldLoyalty.DiscountRate],
            explain: static (in HSale s, int i) => s.Tier == CustomerTier.Gold ? byte.MaxValue : (byte)0,
            conditions: [new(0, "Tier == Gold", "rules.cond.eq", "Tier", RawValue.FromEnum(CustomerTier.Gold))]))
        .Rule(RuleDescriptor<HSale>.Create("loyalty.bday", nameof(Loyalty),
            static (in HSale s, in RuleContext c) => BirthdayDiscount.Applies(in s),
            static (in HSale s, in RuleContext c, ref DirectiveWriter d) => BirthdayDiscount.Evaluate(in s, ref d)))
        .Rule(RuleDescriptor<HSale>.Create("sale.manual", nameof(Manual),
            static (in HSale s, in RuleContext c) => ManualDiscountRule.Applies(in s),
            static (in HSale s, in RuleContext c, ref DirectiveWriter d) => ManualDiscountRule.Evaluate(in s, ref d)))
        .Rule(RuleDescriptor<HSale>.Create("payment.pix", nameof(PaymentDisc),
            static (in HSale s, in RuleContext c) => PixDiscount.Applies(in s),
            static (in HSale s, in RuleContext c, ref DirectiveWriter d) => PixDiscount.Evaluate(in s, ref d), version: 2,
            explain: static (in HSale s, int i) => s.Payment == PaymentMethod.Pix ? byte.MaxValue : (byte)0,
            conditions: [new(0, "Payment == Pix", "rules.cond.eq", "Payment", RawValue.FromEnum(PaymentMethod.Pix))]))
        .Rule(RuleDescriptor<HSale>.Create("sale.installments.boleto", nameof(Installments),
            static (in HSale s, in RuleContext c) => BoletoSingleInstallment.Applies(in s),
            static (in HSale s, in RuleContext c, ref DirectiveWriter d) => BoletoSingleInstallment.Evaluate(in s, ref d), isSealed: true))
        .Rule(RuleDescriptor<HSale>.Create("sale.installments.legal", nameof(Installments),
            static (in HSale s, in RuleContext c) => InstallmentGuardrails.Applies(in s),
            static (in HSale s, in RuleContext c, ref DirectiveWriter d) => InstallmentGuardrails.Evaluate(in s, ref d), isSealed: true))
        .Table(new TableBinding<HSale, InstallmentTable, InstallmentTable.Row>(nameof(Installments)))
        .Table(new TableBinding<HSale, ManualDiscountTable, ManualDiscountTable.Row>(nameof(Manual)))
        .Build();
}

/// <summary>Default situation for scenario tests (doc 12 "SaleSituationBuilder.Default").</summary>
public static class HSaleBuilder
{
    public static readonly HSaleLine Coca = new("coca", "drinks", 3, new Money(10));       // 30.00 → L3P2 −10.00
    public static readonly HSaleLine Tv = new("tv", "electronics", 1, new Money(1100));    // 1100.00
    public static readonly HSaleLine Cable = new("cable", "electronics", 2, new Money(35));// 70.00

    public static HSale Default => new(new TenantId("t1"), new StoreId("s1"), Channel.Pos, new DateOnly(2026, 9, 29),
        CustomerTier.None, false, ActorLevel.Seller, PaymentMethod.Cash, 1, Percentual.Zero, [Coca, Tv, Cable]);
}
