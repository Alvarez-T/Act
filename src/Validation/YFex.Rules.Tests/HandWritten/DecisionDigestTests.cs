namespace YFex.Rules.Tests.HandWritten;

/// <summary>
/// The decision digest approval tokens bind (cryptography ⚑K12, authorization-consolidation Phase 4b) and the offline amount cap that
/// stays in Rules. Token signing and verification are tested in YFex.Authorization.
/// </summary>
public class DecisionDigestTests
{
    private static Decision<HSale> Evaluate(decimal discount)
        => HandSaleRules.Evaluate(HSaleBuilder.Default with { ManualDiscount = Percentual.FromPercentage(discount) });

    [Fact]
    public void Decisions_that_need_approval_carry_a_digest_of_their_outcome()
    {
        var first = Evaluate(8);
        var again = Evaluate(8);
        var other = Evaluate(12);                                             // seller raised the discount after the approval

        first.Verdict.IsNeedsApproval.Should().BeTrue();
        first.Digest.IsZero.Should().BeFalse();
        again.Digest.Should().Be(first.Digest);                               // deterministic
        again.Fingerprint.Should().Be(first.Fingerprint);
        other.Digest.Should().NotBe(first.Digest);                            // another outcome, another digest
    }

    [Fact]
    public void Decisions_without_approvals_have_no_digest_and_keep_their_fingerprint()
    {
        var allowed = Evaluate(0);

        allowed.Approvals.IsEmpty.Should().BeTrue();
        allowed.Digest.IsZero.Should().BeTrue();
        allowed.Fingerprint.Should().NotBe(0UL);
    }

    [Fact]
    public void Offline_cap_allows_only_requested_values_up_to_the_cap()
    {
        var within = Evaluate(8).Approvals[0];
        var over = Evaluate(12).Approvals[0];
        var cap = new OfflineApprovalCap(within.Permission, RawValue.From(Percentual.FromPercentage(10)));

        cap.Allows(in within).Should().BeTrue();
        cap.Allows(in over).Should().BeFalse();
        (cap with { MaxRequested = RawValue.None }).Allows(in within).Should().BeFalse();   // None = never offline
        OfflineApprovalCap.AllowedBy([], in within).Should().BeFalse();                      // no cap = never offline
        OfflineApprovalCap.AllowedBy([cap], in within).Should().BeTrue();
    }
}
