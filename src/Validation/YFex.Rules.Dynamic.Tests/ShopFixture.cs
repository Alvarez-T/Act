using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using YFex.Localization;

namespace YFex.Rules.Dynamic.Tests;

// A small shop: lines priced in Money, a payment enum, a member flag, requested installments, two rule tiers.

public enum PayMethod { Cash, Card, Pix }

[Situation("shop.line")]
public readonly partial record struct ShopLine(string Sku, string Category, int Quantity, Money UnitPrice);

[Situation("shop")]
public readonly partial record struct ShopSituation(
    [property: RuleTier] string Tenant,
    [property: RuleTier] string Store,
    [property: RuleEffectiveDate] DateOnly Date,
    PayMethod Payment,
    bool Member,
    [property: Requested] int Installments,
    Money Total,
    ImmutableArray<ShopLine> Lines);

[RuleSet<ShopSituation>("shop")]
public static partial class ShopRules
{
    public static readonly Stage Items = Stage.First("items");
    public static readonly Stage Totals = Stage.After(Items, "totals");
    public static readonly Stage Limits = Stage.After(Totals, "limits");

    /// <summary>Per-line discounts: tenants may add them (decrease only, ≤ 20%).</summary>
    public static readonly RuleGroup LineDisc = new(Items, Resolve.All)
        { Dynamic = new([nameof(ShopSituation.Tenant)], DynamicDirectives.Decrease, maxPercent: Percentual.FromPercentage(20)) };

    /// <summary>Promotions on the total: tenant or store; ≤ 10% or ≤ 100.</summary>
    public static readonly RuleGroup Promo = new(Totals, Resolve.All)
    {
        Dynamic = new([nameof(ShopSituation.Tenant), nameof(ShopSituation.Store)],
            DynamicDirectives.Decrease | DynamicDirectives.Increase | DynamicDirectives.Warn | DynamicDirectives.Deny,
            maxPercent: Percentual.FromPercentage(10), maxAmount: 100m),
    };

    public static readonly RuleGroup Installments = new(Limits, Resolve.MostRestrictive)
        { Dynamic = new([nameof(ShopSituation.Tenant)], DynamicDirectives.Limit | DynamicDirectives.Grant | DynamicDirectives.RequireApproval) };

    /// <summary>Closed to runtime rules.</summary>
    public static readonly RuleGroup Legal = new(Limits, Resolve.MostRestrictive);

    public static Money LineBase(in ShopSituation s, in ShopLine l) => l.UnitPrice * l.Quantity;
}

[Sealed, Rule("shop.legal", Group = nameof(ShopRules.Legal))]
public readonly struct LegalInstallments : IRule<ShopSituation>
{
    public static bool Applies(in ShopSituation s) => true;
    public static void Evaluate(in ShopSituation s, ref DirectiveWriter d) => d.Limit(ShopSituation.Fields.Installments, max: 12);
}

public static class Shop
{
    public static readonly DateOnly Today = new(2026, 9, 30);
    public static readonly RuleTierPath Tenant = RuleTierPath.Of(RawValue.From("t1"));
    public static readonly RuleTierPath Store = RuleTierPath.Of(RawValue.From("t1"), RawValue.From("s1"));

    public static readonly ShopLine Beer = new("beer", "drinks", 6, new Money(10));
    public static readonly ShopLine Tv = new("tv", "electronics", 1, new Money(900));

    public static ShopSituation Sale(PayMethod pay = PayMethod.Pix, int installments = 1, bool member = false, params ShopLine[] lines)
    {
        ImmutableArray<ShopLine> l = lines.Length == 0 ? [Beer, Tv] : [.. lines];
        decimal total = 0;
        foreach (var x in l) total += x.UnitPrice.Amount * x.Quantity;
        return new("t1", "s1", Today, pay, member, installments, new Money(total), l);
    }

    public static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);

    public static long NextVersion;

    /// <summary>Publishes a spec to <paramref name="host"/> like the store/sync would (validation first).</summary>
    public static void Publish(RuleSetHost<ShopSituation> host, string json, RuleTierPath? tier = null, DateOnly? from = null)
    {
        var spec = RuleSpecJson.Read(Json(json));
        var path = tier ?? Tenant;
        var check = host.ValidateRule(spec.Code, path, Json(json));
        if (!check.IsValid) throw new InvalidOperationException(check.ToString());
        var info = new RuleDataInfo(new RuleDataKey("shop", RuleDataKind.Rule, spec.Code, path), Interlocked.Increment(ref NextVersion),
            from ?? DateOnly.MinValue, "ana", default, null);
        host.Apply(new RuleDataRecord(info, host.Canonicalize(RuleDataKind.Rule, spec.Code, Json(json))));
    }

    public static (RuleSetHost<ShopSituation> Host, DynamicRules<ShopSituation> Dynamic) Host(FunctionCatalog? functions = null)
    {
        var dynamic = new DynamicRules<ShopSituation>(functions);
        return (ShopRules.CreateHost().UseRuntimeRules(dynamic), dynamic);
    }

    public static IMessageCatalog Catalog(DynamicRules<ShopSituation> d)
        => new MessageCatalog(d.Messages, RuleMessages.Instance, YFex.Validation.BuiltIn.BuiltInMessages.Instance);

    public static readonly CultureInfo En = CultureInfo.InvariantCulture;
}
