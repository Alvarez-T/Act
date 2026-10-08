using System.Collections.Immutable;
using static YFex.Rules.Cells;

namespace YFex.Rules.Tests.Generated;

// The engine knows nothing about sales: the same attributes drive a freight weight (Mass), an HR entitlement
// (int days) and a listing eligibility check (strings / an app IRawValue type, no amount at all).

// ── Freight: Mass amount, Carrier → Hub tiers ────────────────────────────────────────────────────────────────

public readonly record struct CarrierId(string Value);

[Situation("shipment")]
public readonly partial record struct Shipment(
    [property: RuleTier] CarrierId Carrier,
    [property: RuleTier] string? Hub,
    [property: RuleEffectiveDate] DateOnly ShipDate,
    [property: Requested] Mass Declared,
    ImmutableArray<Parcel> Parcels);

[Situation("shipment.parcel")]
public readonly partial record struct Parcel(Mass Actual, bool Fragile);

[RuleSet<Shipment>("freight")]
public static partial class FreightRules
{
    public static readonly Stage Packing = Stage.First("packing");
    public static readonly Stage Limits = Stage.After(Packing, "limits");
    public static readonly RuleGroup Tare = new(Packing, Resolve.Stack(capAmount: Mass.FromGrams(40_000)));
    public static readonly RuleGroup Weight = new(Limits, Resolve.MostRestrictive);
    public static RoundingPolicy Rounding => new(0, MidpointRounding.AwayFromZero);       // whole grams

    public static Mass LineBase(in Shipment s, in Parcel p) => p.Actual;
}

[Rule("freight.pallet", Group = nameof(FreightRules.Tare))]
public readonly struct PalletTare : IRule<Shipment>
{
    public static readonly RuleParameter<Mass> PerPallet = new("per-pallet", Mass.FromGrams(25_000), [nameof(Shipment.Carrier)]);
    public static bool Applies(in Shipment s) => s.Parcels.Length >= 3;
    public static void Evaluate(in Shipment s, in RuleContext c, ref DirectiveWriter d) => d.Increase(AdjustTarget.Total, c.Get(PerPallet));
}

[DecisionTable<Shipment>("freight.fragile", Group = nameof(FreightRules.Tare), EditableAt = new[] { nameof(Shipment.Carrier), nameof(Shipment.Hub) })]
public partial class FragilePadding
{
    public readonly partial record struct Row(
        [property: When("Parcels[].Fragile")] Match<bool> Fragile,
        [property: Then(DirectiveKind.IncreasePercent)] Percentual Padding);

    public static readonly Row[] DefaultRows = [new(Is(true), Percentual.FromPercentage(10))];
}

[Sealed, Rule("freight.max-declared", Group = nameof(FreightRules.Weight))]
public readonly struct MaxDeclared : IRule<Shipment>
{
    public static bool Applies(in Shipment s) => true;
    public static void Evaluate(in Shipment s, ref DirectiveWriter d)
        => d.Limit(Shipment.Fields.Declared, max: Mass.FromGrams(70_000), onExceed: Exceed.RequireApproval, permission: RulesTestPermissions.FreightHeavy);
}

// ── HR: plain int amount (days), Company → Department tiers ─────────────────────────────────────────────────

[Situation("leave")]
public readonly partial record struct LeaveRequest(
    [property: RuleTier] string Company,
    [property: RuleTier] string? Department,
    int YearsOfService,
    [property: Requested] int DaysRequested);

[RuleSet<LeaveRequest>("hr.leave")]
public static partial class LeaveRules
{
    public static readonly Stage Entitlement = Stage.First("entitlement");
    public static readonly Stage Limits = Stage.After(Entitlement, "limits");
    public static readonly RuleGroup Bonuses = new(Entitlement, Resolve.Stack(capAmount: 10));
    public static readonly RuleGroup Caps = new(Limits, Resolve.MostRestrictive);

    public static int Base(in LeaveRequest r) => 30;

    public static readonly DerivedField<LeaveRequest, int> Entitled = DerivedField.After(Entitlement, "Entitled",
        static (in LeaveRequest r, in RunningTotals<int> t) => t.Final);
}

[Rule("leave.seniority", Group = nameof(LeaveRules.Bonuses))]
public readonly struct Seniority : IRule<LeaveRequest>
{
    public static bool Applies(in LeaveRequest r) => r.YearsOfService >= 5;
    public static void Evaluate(in LeaveRequest r, ref DirectiveWriter d) => d.Increase(AdjustTarget.Total, r.YearsOfService / 5 * 2);
}

[Rule("leave.cap", Group = nameof(LeaveRules.Caps))]
public readonly struct EntitlementCap : IRule<LeaveRequest>
{
    public static bool Applies(in LeaveRequest r, in RuleContext c) => true;
    public static void Evaluate(in LeaveRequest r, in RuleContext c, ref DirectiveWriter d)
        => d.Limit(LeaveRequest.Fields.DaysRequested, max: c.Base.As<int>());   // the running total = days entitled
}

// ── Listing eligibility: no amount, strings and an app IRawValue type ──────────────────────────────────────

public readonly record struct Sku(string Value) : IRawValue<Sku>
{
    public static RawType RawType { get; } = RawType.Define<Sku>("tests.sku", ValueKind.Text);
    public RawValue ToRaw() => RawValue.Typed(RawType, Value);
    public static bool TryFromRaw(in RawValue raw, out Sku value)
    {
        bool ok = raw.TryRead(RawType, out string? s);
        value = new(s ?? "");
        return ok;
    }
}

[Situation("listing")]
public readonly partial record struct Listing(
    [property: RuleTier] string Marketplace,
    [property: RuleTier] string? Seller,
    Sku Sku,
    string Category,
    [property: Requested] string ShipTo);

[RuleSet<Listing>("listing")]
public static partial class ListingRules
{
    public static readonly Stage Checks = Stage.First("checks");
    public static readonly RuleGroup Eligibility = new(Checks, Resolve.All);
}

[Rule("listing.countries", Group = nameof(ListingRules.Eligibility))]
public readonly struct AllowedCountries : IRule<Listing>
{
    public static readonly RuleParameter<string> Countries = new("countries", "BR,AR,UY", [nameof(Listing.Marketplace), nameof(Listing.Seller)]);
    public static bool Applies(in Listing l) => true;
    public static void Evaluate(in Listing l, in RuleContext c, ref DirectiveWriter d)
    {
        var codes = c.Get(Countries).Split(',');
        var options = new RawValue[codes.Length];
        for (int i = 0; i < codes.Length; i++) options[i] = RawValue.From(codes[i]);
        d.Grant(Listing.Fields.ShipTo, OptionSet.Of(options));
    }
}

[Sealed, Rule("listing.restricted", Group = nameof(ListingRules.Eligibility))]
public readonly struct RestrictedCategory : IRule<Listing>
{
    public static bool Applies(in Listing l) => l.Category is "weapons";
    public static void Evaluate(in Listing l, ref DirectiveWriter d)
        => d.Deny(FieldPath.Of("Category"), "listing.category.restricted", RawValue.From(l.Category));
}

[Rule("listing.bad-fee", Group = nameof(ListingRules.Eligibility))]
public readonly struct MisplacedFee : IRule<Listing>
{
    public static bool Applies(in Listing l) => l.Category is "fee-test";
    public static void Evaluate(in Listing l, ref DirectiveWriter d) => d.Increase(AdjustTarget.Total, 5m);
}

public class DomainNeutralTests
{
    private static Parcel P(int kg, bool fragile = false) => new(Mass.FromGrams(kg * 1000), fragile);

    private static Shipment Ship(string? hub = null, params Parcel[] parcels)
        => new(new CarrierId("acme"), hub, new DateOnly(2026, 9, 30), Mass.FromGrams(10_000), [.. parcels]);

    [Fact]
    public void Freight_adjusts_a_Mass_amount_with_Increase_line_tables_and_a_stack_cap()
    {
        var d = FreightRules.Evaluate(Ship(null, P(10, fragile: true), P(20), P(30)));

        d.Totals.Initial.As<Mass>().Should().Be(Mass.FromGrams(60_000));
        // pallet +25 kg on the total first (split by weight: parcel 0 gets 4 166 g), then fragile +10% of parcel 0's
        // running base 14 166 g = 1 417 g (whole grams); the 40 kg stack cap is not reached
        d.Totals.Final.As<Mass>().Should().Be(Mass.FromGrams(86_417));
        d.Totals.Change.As<Mass>().Should().Be(Mass.FromGrams(26_417));
        d.Adjustments.AsSpan().ToArray().Should().OnlyContain(a => a.Direction == AdjustDirection.Increase);
        var pallet = d.Adjustments.AsSpan().ToArray().Single(a => a.Rule.Code == "freight.pallet");
        pallet.Allocations.ToArray().Sum(x => x.Amount.As<Mass>().Grams).Should().Be(25_000);        // split by weight, exact
        d.Totals.Lines.ToArray().Sum(l => l.Final.As<Mass>().Grams).Should().Be(86_417);
    }

    [Fact]
    public void Freight_limit_on_a_Mass_field_requires_approval()
    {
        var heavy = Ship(null, P(1)) with { Declared = Mass.FromGrams(80_000) };
        var d = FreightRules.Evaluate(heavy);

        d.Verdict.Kind.Should().Be(VerdictKind.NeedsApproval);
        d.Approvals[0].Permission.Should().Be(RulesTestPermissions.FreightHeavy);
        d.TryGetMax(Shipment.Fields.Declared, out Mass max).Should().BeTrue();
        max.Should().Be(Mass.FromGrams(70_000));
    }

    [Fact]
    public void Freight_data_published_at_app_tiers_respects_EditableAt()
    {
        var host = FreightRules.CreateHost();
        var carrier = RuleTierPath.Of(RawValue.From("acme"));
        var hub = RuleTierPath.Of(RawValue.From("acme"), RawValue.From("recife"));
        host.Publish(new ParameterSnapshot("freight.pallet", carrier, 1, DateOnly.MinValue,
            ImmutableDictionary<string, object?>.Empty.Add("per-pallet", Mass.FromGrams(20_000))));
        host.Publish(new ParameterSnapshot("freight.pallet", hub, 2, DateOnly.MinValue,
            ImmutableDictionary<string, object?>.Empty.Add("per-pallet", Mass.FromGrams(1_000))));   // Hub may not edit it

        var d = host.Evaluate(Ship("recife", P(10), P(10), P(10)));
        d.Adjustments.AsSpan().ToArray().Single(a => a.Rule.Code == "freight.pallet").Amount.As<Mass>().Should().Be(Mass.FromGrams(20_000));
        d.Adjustments.AsSpan().ToArray().Single(a => a.Rule.Code == "freight.pallet").Rule.Tier.Should().Be(0);
        host.Resolve(hub, new DateOnly(2026, 9, 30)).Notes.ToArray()
            .Should().Contain(n => n.Kind == CompileNoteKind.NotEditableAtTier && n.Tier == 2);
        Shipment.Schema.TierName(2).Should().Be("Hub");
        Shipment.TierPath(Ship("recife")).Should().Be(hub);
        Shipment.TierPath(Ship(null)).Should().Be(carrier);                                   // no hub → carrier-level evaluation
    }

    [Fact]
    public void Leave_days_are_a_plain_int_amount()
    {
        var junior = LeaveRules.Evaluate(new LeaveRequest("acme", null, 2, 30));
        junior.Verdict.Kind.Should().Be(VerdictKind.Allowed);
        junior.Totals.Final.As<int>().Should().Be(30);

        var senior = LeaveRules.Evaluate(new LeaveRequest("acme", "it", 12, 36));     // +2 days per 5 years → 34
        senior.Totals.Final.As<int>().Should().Be(34);
        senior.DerivedValue(LeaveRules.Entitled).As<int>().Should().Be(34);
        senior.Verdict.Kind.Should().Be(VerdictKind.Denied);
        senior.Violations[0].Code.Value.Should().Be("rules.limit.max");

        LeaveRules.Evaluate(new LeaveRequest("acme", null, 40, 1)).Totals.Final.As<int>().Should().Be(40);   // stack cap: +10 at most
    }

    [Fact]
    public void Listing_rules_need_no_amount_and_work_on_text_and_app_types()
    {
        var ok = ListingRules.Evaluate(new Listing("mkt", null, new Sku("COCA-2L"), "drinks", "BR"));
        ok.Verdict.Kind.Should().Be(VerdictKind.Allowed);
        ok.Totals.Final.IsNone.Should().BeTrue();
        ok.Options(Listing.Fields.ShipTo).Count.Should().Be(3);
        Listing.Read(new Listing("mkt", null, new Sku("X"), "c", "BR"), Listing.FieldIds.Sku).As<Sku>().Should().Be(new Sku("X"));
        Listing.Schema[Listing.FieldIds.Sku].Type.Should().BeSameAs(Sku.RawType);

        ListingRules.Evaluate(new Listing("mkt", null, new Sku("X"), "drinks", "US")).Verdict.Kind.Should().Be(VerdictKind.Denied);
        ListingRules.Evaluate(new Listing("mkt", null, new Sku("X"), "weapons", "BR")).Verdict.Kind.Should().Be(VerdictKind.Denied);

        FluentActions.Invoking(() => ListingRules.Evaluate(new Listing("mkt", null, new Sku("X"), "fee-test", "BR")))
            .Should().Throw<InvalidOperationException>().WithMessage("*has no amount*listing.bad-fee*");
    }
}
