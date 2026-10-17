using YFex.Authorization;
using YFex.Authorization.Public;
using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;
using YFex.Cqrs.Runtime;
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
        approval.DecisionDigest.IsZero.Should().BeFalse();

        // Manager approves THIS outcome (server-issued HMAC token over the digest), the client resends with the token.
        var token = await f.Tokens.IssueAsync(new ApprovalGrant("mgr42", null, CheckoutPermissions.DiscountOverride, approval.DecisionDigest));
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
    public async Task Offline_device_approval_of_a_rule_limit_is_capped_and_flagged()
    {
        var audit = new FlagRecorder();
        var pos = new YFex.Installations.InstallationId(Guid.Parse("0199e3a0-0000-7000-8000-000000000007"));
        var f = new CheckoutFixture(
            caps: [new OfflineApprovalCap(CheckoutPermissions.DiscountOverride, RawValue.From(Percentual.FromPercentage(10)))],
            approvals: a => a.AllowOffline(CheckoutPermissions.DiscountOverride, TimeSpan.FromHours(48)),
            configure: s =>
            {
                s.AddSingleton<IApprovalSignatureVerifier, AcceptingDeviceSignatures>();
                s.AddSingleton<IApprovalOriginAccessor>(new FixedOrigin(new ApprovalOrigin(pos)));
                s.AddSingleton<IPermissionDirectory>(new YFex.Authorization.Testing.FakePermissionDirectory().Grant("mgr42", CheckoutPermissions.DiscountOverride));
                s.AddSingleton<IDecisionAuditSink>(audit);
            });

        var within = await RequiredAsync(f, Card(discount: 8));
        var approved = await f.Dispatcher.CommandAsync<CloseCheckout, Money>(Card(discount: 8) with
        {
            DecisionFingerprint = within.DecisionFingerprint, Approvals = [Device(pos, within.DecisionDigest)],
        });
        approved.TryGetValue(out Money final).Should().BeTrue();
        final.Should().Be(new Money(920));
        audit.Count.Should().Be(1);                                                     // offline approvals are always reviewed (D34)

        var over = await RequiredAsync(f, Card(discount: 12));                          // above the 10 % offline cap
        var refused = await f.Dispatcher.CommandAsync<CloseCheckout, Money>(Card(discount: 12) with
        {
            DecisionFingerprint = over.DecisionFingerprint, Approvals = [Device(pos, over.DecisionDigest)],
        });
        refused.TryGetError(out var error).Should().BeTrue();
        error.Kind.Should().Be(ErrorKind.ApprovalRequired);
    }

    private static async Task<ApprovalRequired> RequiredAsync(CheckoutFixture f, CloseCheckout command)
    {
        (await f.Dispatcher.CommandAsync<CloseCheckout, Money>(command)).TryGetError(out var error).Should().BeTrue();
        error.TryGetValue(out ApprovalRequired approval).Should().BeTrue();
        return approval;
    }

    private static ApprovalToken Device(YFex.Installations.InstallationId pos, YFex.Cryptography.Hash256 digest)
        => new(ApprovalTokenKind.Device, "mgr42", null, CheckoutPermissions.DiscountOverride, digest,
            ValidityPeriod.For(DateTimeOffset.UtcNow.AddMinutes(-1), TimeSpan.FromMinutes(15)), YFex.Cryptography.KeyId.New(), pos, "ok"u8.ToArray());

    /// <summary>Stand-in for the device signature check (real one: YFex.Authorization.Installations.Central).</summary>
    private sealed class AcceptingDeviceSignatures : IApprovalSignatureVerifier
    {
        public ApprovalTokenKind Kind => ApprovalTokenKind.Device;
        public ValueTask<ApprovalCheck> VerifySignatureAsync(ApprovalToken token, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(token.Signature.Span.SequenceEqual("ok"u8) ? ApprovalCheck.Valid : ApprovalCheck.InvalidSignature);
    }

    private sealed class FixedOrigin(ApprovalOrigin origin) : IApprovalOriginAccessor
    {
        public ApprovalOrigin Current => origin;
    }

    private sealed class FlagRecorder : IDecisionAuditSink
    {
        public int Count;
        public ValueTask OnFlaggedAsync(object message, object decision, ulong clientFingerprint, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            return ValueTask.CompletedTask;
        }
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
