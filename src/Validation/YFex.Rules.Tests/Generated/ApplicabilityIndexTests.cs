using YFex.Rules.Tests.HandWritten;

namespace YFex.Rules.Tests.Generated;

/// <summary>04 §12.1: the applicability index is a pure optimization — same decisions and traces with or without it.</summary>
// Toggling the static switch is safe next to parallel tests: both paths give identical decisions (that is the point).
public class ApplicabilityIndexTests
{
    [Fact]
    public void The_index_filters_rules_whose_equality_condition_is_false()
    {
        var def = SaleRules.Set;
        var mask = new ulong[def.Index!.Words];
        def.Index.Candidates(SaleBuilder.Default with { Payment = PaymentMethod.Cash, Tier = CustomerTier.Silver, IsBirthdayMonth = true }, mask);

        bool Candidate(string code)
        {
            def.TryGetRule(code, out var rule).Should().BeTrue();
            return (mask[rule.Index >> 6] & (1UL << (rule.Index & 63))) != 0;
        }
        Candidate("payment.pix").Should().BeFalse();                  // Payment == Pix
        Candidate("sale.installments.boleto").Should().BeFalse();     // Payment == Boleto
        Candidate("loyalty.gold").Should().BeFalse();                 // Tier == Gold
        Candidate("loyalty.bday").Should().BeTrue();                  // IsBirthdayMonth (bool test, indexed as rules.cond.true)
        Candidate("sale.manual").Should().BeTrue();                   // ManualDiscount > 0: not an equality → always a candidate

        def.Index.Candidates(SaleBuilder.Default with { Payment = PaymentMethod.Pix, IsBirthdayMonth = false }, mask);
        Candidate("payment.pix").Should().BeTrue();
        Candidate("loyalty.bday").Should().BeFalse();
    }

    [Fact]
    public void Decisions_and_full_traces_are_identical_with_and_without_the_index()
    {
        var random = new Random(12345);
        var tiers = Enum.GetValues<CustomerTier>();
        var payments = Enum.GetValues<PaymentMethod>();
        var actors = Enum.GetValues<ActorLevel>();
        int indexed = 0;
        for (int i = 0; i < 400; i++)
        {
            var s = SaleBuilder.Default with
            {
                Tier = tiers[random.Next(tiers.Length)],
                Payment = payments[random.Next(payments.Length)],
                Actor = actors[random.Next(actors.Length)],
                IsBirthdayMonth = random.Next(2) == 0,
                Installments = random.Next(1, 13),
                ManualDiscount = Percentual.FromPercentage(random.Next(0, 4) * 3),
                BusinessDate = new DateOnly(2026, random.Next(10, 12), 15),
            };

            var with = Evaluate(s, index: true);
            var without = Evaluate(s, index: false);

            with.Fingerprint.Should().Be(without.Fingerprint, $"situation {i}");
            with.Verdict.Kind.Should().Be(without.Verdict.Kind);
            Outcomes(with).Should().Equal(Outcomes(without), $"situation {i}");
            if (with.Trace!.Entries.ToArray().Any(e => e.Outcome.Kind == TraceOutcomeKind.NotApplicable)) indexed++;
        }
        indexed.Should().BeGreaterThan(0);                  // the index actually skipped rules
    }

    private static Decision<SaleSituation> Evaluate(in SaleSituation s, bool index)
    {
        bool before = ApplicabilityIndex<SaleSituation>.Enabled;
        ApplicabilityIndex<SaleSituation>.Enabled = index;
        try { return SaleRules.Evaluate(s, new() { Trace = TraceLevel.Full }); }
        finally { ApplicabilityIndex<SaleSituation>.Enabled = before; }
    }

    private static string[] Outcomes(Decision<SaleSituation> d)
        => d.Trace!.Entries.ToArray().Select(e => $"{e.Rule.Code}#{e.LineIndex}:{e.Outcome.Kind}:{e.Outcome.Value}").ToArray();
}
