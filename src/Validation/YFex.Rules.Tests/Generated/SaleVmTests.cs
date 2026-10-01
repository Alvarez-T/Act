using YFex.Rules.Tests.HandWritten;
using YFex.State;
using YFex.Validation;

namespace YFex.Rules.Tests.Generated;

// 07 §9 / D31: a POS view model that builds its situation by name convention and projects the decision onto its fields.

[SituationSource<SaleSituation>]
public partial class SaleVm : StateObject
{
    [Observable] public partial CustomerTier Tier { get; set; }
    [Observable] public partial bool IsBirthdayMonth { get; set; }
    [Observable] public partial ActorLevel Actor { get; set; }
    [Observable] public partial PaymentMethod Payment { get; set; }
    [Observable] public partial int Installments { get; set; }
    [Observable] public partial Percentual ManualDiscount { get; set; }
    public List<SaleLineVm> Lines { get; } = [];

    /// <summary>Latest decision: limits → constraints of same-named properties, violations → bag (source Rule).</summary>
    [Observable, ConstraintsFrom] public partial Decision<SaleSituation>? Policy { get; set; }

    public void Recompute() => Policy = SaleRules.Evaluate(BuildSituation());

    // Context the VM doesn't hold as same-named members (captured once when the sale opened).
    private partial TenantId __Situation_Tenant() => new("t1");
    private partial StoreId __Situation_Store() => new("s1");
    private partial Channel __Situation_Channel() => Channel.Pos;
    private partial DateOnly __Situation_BusinessDate() => new(2026, 9, 29);
}

[SituationSource<SaleLineSituation>]
public partial class SaleLineVm
{
    public string Product { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Quantity { get; set; }
    public Money UnitPrice { get; set; }
}

public class SaleVmTests
{
    private static SaleVm NewVm()
    {
        var vm = new SaleVm { Payment = PaymentMethod.Cash, Installments = 1 };
        vm.Lines.Add(new SaleLineVm { Product = "coca", Category = "drinks", Quantity = 3, UnitPrice = new Money(10) });
        vm.Lines.Add(new SaleLineVm { Product = "tv", Category = "electronics", Quantity = 1, UnitPrice = new Money(1100) });
        return vm;
    }

    [Fact]
    public void BuildSituation_maps_by_name_lines_and_partial_methods()
    {
        var vm = NewVm();
        vm.Tier = CustomerTier.Gold;
        var s = vm.BuildSituation();

        s.Tenant.Should().Be(new TenantId("t1"));
        s.BusinessDate.Should().Be(new DateOnly(2026, 9, 29));
        s.Tier.Should().Be(CustomerTier.Gold);
        s.Payment.Should().Be(PaymentMethod.Cash);
        s.Lines.Length.Should().Be(2);
        s.Lines[1].Should().Be(new SaleLineSituation("tv", "electronics", 1, new Money(1100)));
        s.Total.Should().Be(new Money(1130));
    }

    [Fact]
    public void Decision_constraints_and_violations_reach_the_view_model()
    {
        var vm = NewVm();
        var source = (IValidationSource)vm;
        var changed = new List<string>();
        source.ConstraintsChanged += changed.Add;

        vm.Payment = PaymentMethod.Pix;
        vm.Installments = 3;
        vm.Recompute();

        source.TryGetConstraints("Installments", out var c).Should().BeTrue();
        c.Max.Should().Be(RawValue.From(1));                                  // Pix row of the installment table
        changed.Should().Contain("");
        vm.Validation.HasErrorsFor("Installments").Should().BeTrue();
        vm.Validation.Get("Installments")[0].Code.Value.Should().Be("rules.limit.max");
        vm.Validation.Get("Installments")[0].Origin.Source.Should().Be(ViolationSource.Rule);

        // A new decision replaces the previous one's violations.
        vm.Installments = 1;
        vm.Recompute();
        vm.Validation.HasErrorsFor("Installments").Should().BeFalse();
    }

    [Fact]
    public void Seller_above_limit_shows_a_warning_and_the_verdict_needs_approval()
    {
        var vm = NewVm();
        vm.ManualDiscount = Percentual.FromPercentage(8);
        vm.Recompute();

        vm.Policy!.Verdict.IsNeedsApproval.Should().BeTrue();
        vm.Validation.Get("ManualDiscount")[0].Severity.Should().Be(Severity.Warning);
        ((IValidationSource)vm).TryGetConstraints("ManualDiscount", out var c).Should().BeTrue();
        c.Max.Should().Be(RawValue.From(Percentual.FromPercentage(5)));
    }
}
