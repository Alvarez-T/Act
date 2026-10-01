namespace YFex.Rules.Tests.Generated;

/// <summary>04 §12.2 / §13: evaluation allocates the Decision + exact-size arrays only (pooled scratch, no trace).</summary>
public class AllocationTests
{
    [Fact]
    public void Evaluation_stays_within_its_allocation_budget()
    {
        var s = SaleBuilder.Default with { Tier = HandWritten.CustomerTier.Gold, Payment = HandWritten.PaymentMethod.CreditCard, Installments = 6 };
        for (int i = 0; i < 50; i++) SaleRules.Evaluate(s);                                   // warm up (JIT, pools, lazy set)

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int runs = 100;
        for (int i = 0; i < runs; i++) SaleRules.Evaluate(s);
        long perEvaluation = (GC.GetAllocatedBytesForCurrentThread() - before) / runs;

        // Decision + limits/derived/line/adjustment/allocation arrays for 3 lines, 2 adjustments, 3 limits ≈ 1.5 KB.
        perEvaluation.Should().BeLessThan(2_500, "evaluation must not allocate beyond the decision's own arrays");
    }
}
