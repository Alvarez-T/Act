using YFex.Cqrs;
using YFex.Messaging.Tests.Fixtures;
using YFex.Rules;
using CloseCheckout = YFex.Messaging.Tests.Fixtures.CheckoutAggregate.Commands.CloseCheckout;

namespace YFex.Messaging.Tests.Cqrs;

/// <summary>Validation Phase 4 — stages 3–4 (situation + rules) in the dispatch pipeline (09 §6, §8, D33, D34).</summary>
public class RulesStageTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);
    private static CloseCheckout Card(decimal discount = 0, int installments = 3)
        => new("t1", Today, PayKind.Card, installments, Percentual.FromPercentage(discount), new Money(1000));

    [Fact]
    public async Task Allowed_decision_reaches_the_ruled_handler_and_stages_run_in_order()
    {
        var f = new CheckoutFixture();
        var result = await f.Dispatcher.CommandAsync<CloseCheckout, Money>(Card(discount: 2));

        result.TryGetValue(out Money final).Should().BeTrue();
        final.Should().Be(new Money(980));                                    // 2% of 1000 applied by the rules
        f.Log.Entries.Should().Equal("gather", "context", "ruled-handler");   // rules (3–4) before context rules (5)
    }

    [Fact]
    public async Task Denied_decision_returns_structured_violations()
    {
        var f = new CheckoutFixture();
        var result = await f.Dispatcher.CommandAsync<CloseCheckout, Money>(Card() with { Pay = PayKind.Cash });

        result.TryGetError(out var error).Should().BeTrue();
        error.Kind.Should().Be(ErrorKind.ValidationProblem);
        error.Violations[0].Code.Value.Should().Be("rules.limit.max");
        error.Violations[0].Path.ToString().Should().Be("Installments");
        f.Log.Entries.Should().Equal("gather");                               // context rules and handler never ran
    }

    [Fact]
    public async Task Situation_paths_are_renamed_to_the_command_fields()
    {
        var f = new CheckoutFixture(paths: m => m.Path("Installments", "Payment.InstallmentCount"));
        var result = await f.Dispatcher.CommandAsync<CloseCheckout, Money>(Card() with { Pay = PayKind.Cash });

        result.TryGetError(out var error).Should().BeTrue();
        error.Violations[0].Path.ToString().Should().Be("Payment.InstallmentCount");   // 04 §14 SituationPathMap
    }

    [Fact]
    public async Task Approval_required_then_a_token_bound_to_the_fingerprint_passes()
    {
        var f = new CheckoutFixture();
        var first = await f.Dispatcher.CommandAsync<CloseCheckout, Money>(Card(discount: 8));

        first.TryGetError(out var error).Should().BeTrue();
        error.Kind.Should().Be(ErrorKind.ApprovalRequired);
        error.TryGetValue(out ApprovalRequired approval).Should().BeTrue();
        approval.Approvals.Requires(CheckoutPermissions.DiscountOverride).Should().BeTrue();
        approval.DecisionFingerprint.Should().NotBe(0UL);

        // Manager approves THIS outcome (server-issued HMAC token), the client resends with the token.
        var token = f.Tokens.Issue("mgr42", "sales.discount.override", approval.DecisionFingerprint, DateTimeOffset.UtcNow);
        var approved = await f.Dispatcher.CommandAsync<CloseCheckout, Money>(Card(discount: 8) with
        {
            DecisionFingerprint = approval.DecisionFingerprint, Approvals = [token],
        });
        approved.TryGetValue(out Money final).Should().BeTrue();
        final.Should().Be(new Money(920));

        // Same token for a bigger discount (another outcome) is rejected.
        var abused = await f.Dispatcher.CommandAsync<CloseCheckout, Money>(Card(discount: 12) with { Approvals = [token] });
        abused.TryGetError(out var again).Should().BeTrue();
        again.Kind.Should().Be(ErrorKind.ApprovalRequired);
    }

    [Fact]
    public async Task Fingerprint_mismatch_follows_the_policy()
    {
        var reject = new CheckoutFixture(DecisionMismatchPolicy.Reject);
        var stale = await reject.Dispatcher.CommandAsync<CloseCheckout, Money>(Card() with { DecisionFingerprint = 12345 });
        stale.TryGetError(out var error).Should().BeTrue();
        error.Violations[0].Code.Value.Should().Be("rules.decision.mismatch");

        var serverFingerprint = CheckoutRules.Evaluate(new CheckoutSituation("t1", Today, false, PayKind.Card, 3, Percentual.Zero, new Money(1000))).Fingerprint;
        (await reject.Dispatcher.CommandAsync<CloseCheckout, Money>(Card() with { DecisionFingerprint = serverFingerprint }))
            .TryGetValue(out Money _).Should().BeTrue();

        var accept = new CheckoutFixture(DecisionMismatchPolicy.AcceptAndFlag);
        (await accept.Dispatcher.CommandAsync<CloseCheckout, Money>(Card() with { DecisionFingerprint = 12345 }))
            .TryGetValue(out Money _).Should().BeTrue();
    }

    [Fact]
    public async Task Query_exposes_the_decision_and_never_rejects_and_sanitizes_the_client_situation()
    {
        var f = new CheckoutFixture();
        var claimed = new CheckoutSituation("t1", Today, IsManager: true, PayKind.Cash, 3, Percentual.FromPercentage(10), new Money(1000));

        var result = await f.Dispatcher.QueryAsync<CheckoutAggregate.Queries.GetCheckoutOptions, int>(new(claimed));

        result.TryGetValue(out int max).Should().BeTrue();                    // requested 3 with Cash: not rejected
        max.Should().Be(1);
    }

    [Fact]
    public async Task Server_invoker_runs_the_stages_and_hands_the_decision_to_the_ruled_handler()
    {
        var f = new CheckoutFixture(decorateInvoker: true);

        var act = () => f.Invoker.InvokeAsync<Money>(Card(discount: 8), CancellationToken.None);
        var thrown = await act.Should().ThrowAsync<ErrorResultException>();
        thrown.Which.TypedError.Kind.Should().Be(ErrorKind.ApprovalRequired);

        (await f.Invoker.InvokeAsync<Money>(Card(discount: 1), CancellationToken.None)).Should().Be(new Money(990));
        f.Log.Entries.Should().EndWith("ruled-handler");
    }

    [Fact]
    public async Task Without_a_ruled_handler_the_classic_handler_runs()
    {
        var f = new CheckoutFixture(ruledHandler: false);
        (await f.Dispatcher.CommandAsync<CloseCheckout, Money>(Card())).TryGetValue(out Money final).Should().BeTrue();
        final.Should().Be(Money.Zero);                                          // ClassicCloseCheckoutHandler
    }

    [Fact]
    public void Enforce_without_UseSituation_fails_at_configuration()
    {
        var act = () => CompiledMessagingRegistry.Build(baseline: [new BrokenConfig()]);
        act.Should().Throw<Exception>().Which.GetBaseException().Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("UseSituation");
    }

    private sealed class BrokenConfig : YFex.Cqrs.Configuration.IAggregateConfiguration<CheckoutAggregate>
    {
        public void Configure(YFex.Cqrs.Configuration.AggregateConfigurationBuilder<CheckoutAggregate> b)
            => b.Command<CloseCheckout, Money>().Enforce(CheckoutRules.Set);
    }
}
