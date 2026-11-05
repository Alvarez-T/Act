using MemoryPack;
using YFex.Cqrs;
using YFex.Messaging.Rpc.Serialization;
using YFex.Validation;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>MemoryPack (Fusion's default <c>mempack6</c> wire format) round-trips of the result unions (D26).</summary>
public class WireFormatTests
{
    static WireFormatTests()
    {
        RpcResultFormatters.Register<int>();
        RpcResultFormatters.Register<List<string>>();
    }

    private static T RoundTrip<T>(T value) => MemoryPackSerializer.Deserialize<T>(MemoryPackSerializer.Serialize(value))!;

    private static readonly Error Rich = Error.Validation(ValidationResult.Of(ViolationList.From(
    [
        new Violation(FieldPath.Of("Lines[].Quantity", 3), "stock.out", Severity.Error,
            new RuleArgs(RawValue.From(2), RawValue.From(new Money(10.5m))),
            new ViolationOrigin(ViolationSource.Server, 7), detail: "only 2 left"),
        new Violation(FieldPath.Of("Email"), "email", Severity.Warning, new RuleArgs(RawValue.From("x@y"))),
        new Violation(FieldPath.Empty, "form.incomplete"),
    ])));

    [Fact]
    public void Error_with_violations_round_trips_losslessly()
    {
        var back = RoundTrip(Rich);

        back.Kind.Should().Be(ErrorKind.ValidationProblem);
        back.Message.Should().Be(Rich.Message);
        back.Violations.Count.Should().Be(3);
        for (int i = 0; i < 3; i++)
        {
            var (a, b) = (Rich.Violations[i], back.Violations[i]);
            b.Path.Should().Be(a.Path);
            b.Code.Should().Be(a.Code);
            b.Severity.Should().Be(a.Severity);
            b.Args.Should().Be(a.Args);
            b.Origin.Should().Be(a.Origin);
            b.Detail.Should().Be(a.Detail);
        }
        back.Violations[0].Path.ToString().Should().Be("Lines[3].Quantity");
        back.Violations[0].Args[1].TryGet(out Money m).Should().BeTrue();
        m.Should().Be(new Money(10.5m));
    }

    [Fact]
    public void Unavailable_error_round_trips_reason_and_message()   // v6, offline ⚑O13
    {
        QueueableResult<int> offline = Error.Unavailable(UnavailableReason.CentralRequired);
        var back = RoundTrip(offline);

        back.TryGetError(out var error).Should().BeTrue();
        error.Kind.Should().Be(ErrorKind.Unavailable);
        error.TryGetValue(out Unavailable unavailable).Should().BeTrue();
        unavailable.Reason.Should().Be(UnavailableReason.CentralRequired);
        unavailable.Message.Should().Be("Requires the central server.");
        RoundTrip(Error.Unavailable(UnavailableReason.AllowanceExhausted, "Convênio sem saldo offline")).Message
            .Should().Be("Convênio sem saldo offline");
    }

    [Fact]
    public void ReauthenticationRequired_round_trips_max_age_and_minimum()   // v7, authentication P5 (R2)
    {
        var requirement = new YFex.Authorization.Public.RecentAuthenticationRequirement(TimeSpan.FromMinutes(5),
            YFex.Authorization.Public.AssuranceLevel.MultiFactor);
        // A step-up denial from the gate becomes its own error kind, not a plain Forbidden.
        Error error = Error.From(YFex.Authorization.Public.AuthorizationVerdict.Forbidden(
            YFex.Authorization.Public.Denial.ReauthenticationRequiredOf(requirement)));
        error.Kind.Should().Be(ErrorKind.ReauthenticationRequired);
        error.TryGetValue(out Forbidden _).Should().BeFalse();

        var back = RoundTrip<QueueableResult<int>>(error);
        back.TryGetError(out var received).Should().BeTrue();
        received.Kind.Should().Be(ErrorKind.ReauthenticationRequired);
        received.TryGetValue(out ReauthenticationRequired step).Should().BeTrue();
        step.Requirement.Should().Be(requirement);
        received.Message.Should().Be(error.Message);
        received.Value.Should().Be(new ReauthenticationRequired(requirement));
    }

    [Fact]
    public void SitePresenceRequired_round_trips_max_age()   // v8, ⚑Z20 (authentication R8)
    {
        var requirement = new YFex.Authorization.Public.SitePresenceRequirement(TimeSpan.FromMinutes(10));
        Error error = Error.From(YFex.Authorization.Public.AuthorizationVerdict.Forbidden(
            YFex.Authorization.Public.Denial.SitePresenceRequiredOf(requirement)));
        error.Kind.Should().Be(ErrorKind.SitePresenceRequired);
        error.TryGetValue(out Forbidden _).Should().BeFalse();

        var back = RoundTrip<QueueableResult<int>>(error);
        back.TryGetError(out var received).Should().BeTrue();
        received.Kind.Should().Be(ErrorKind.SitePresenceRequired);
        received.TryGetValue(out SitePresenceRequired presence).Should().BeTrue();
        presence.Requirement.Should().Be(requirement);
        received.Message.Should().Be(error.Message);
        received.Value.Should().Be(new SitePresenceRequired(requirement));
    }

    [Fact]
    public void RestrictedWhileImpersonating_round_trips_as_Forbidden_with_the_permission()   // Phase 6
    {
        var permission = YFex.Authorization.Public.Permission.Parse("shop.customers.export");
        Error error = Error.From(YFex.Authorization.Public.AuthorizationVerdict.Forbidden(
            YFex.Authorization.Public.Denial.RestrictedWhileImpersonatingOf(permission.Key)));
        var back = RoundTrip<QueueableResult<int>>(error);
        back.TryGetError(out var received).Should().BeTrue();
        received.TryGetValue(out Forbidden forbidden).Should().BeTrue();
        forbidden.Denial.Kind.Should().Be(YFex.Authorization.Public.DenialKind.RestrictedWhileImpersonating);
        forbidden.Denial.Missing.Value.Should().Be("shop.customers.export");
    }

    [Fact]
    public void Approval_required_error_round_trips_approvals_fingerprint_and_digest()
    {
        var reason = new Violation(FieldPath.Of("Discount"), "rules.limit.max", Severity.Warning,
            new RuleArgs(RawValue.From(Percentual.FromPercentage(5)), RawValue.From(Percentual.FromPercentage(8))),
            new ViolationOrigin(ViolationSource.Rule, 3));
        var rowId = Guid.NewGuid();
        var error = Error.ApprovalRequired(
            YFex.Rules.ApprovalRequestList.From([new YFex.Rules.ApprovalRequest(YFex.Authorization.Public.Permission.Parse("sales.discount.override"), reason,
                new YFex.Rules.RuleRef("sale.manual-discount", 2, 1, 42, rowId))]),
            0xDEADBEEFCAFEUL, Digest, ViolationList.From([reason]));

        var back = RoundTrip<QueueableResult<int>>(error);

        back.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.ApprovalRequired);
        e.TryGetValue(out ApprovalRequired a).Should().BeTrue();
        a.DecisionFingerprint.Should().Be(0xDEADBEEFCAFEUL);
        a.DecisionDigest.Should().Be(Digest);                                    // wire v5 (authorization-consolidation A5)
        e.Violations.Count.Should().Be(1);
        a.Approvals.Count.Should().Be(1);
        a.Approvals[0].Permission.Value.Should().Be("sales.discount.override");   // the id travels unchanged
        a.Approvals[0].Rule.Should().Be(new YFex.Rules.RuleRef("sale.manual-discount", 2, 1, 42, rowId));
        a.Approvals[0].Reason.Args[1].Should().Be(RawValue.From(Percentual.FromPercentage(8)));
    }

    private static readonly YFex.Cryptography.Hash256 Digest =
        YFex.Authorization.Public.ApprovalDigest.Of(typeof(WireFormatTests), "sale-42");

    [Fact]
    public void Approval_tokens_permissions_and_digests_round_trip()
    {
        var token = new YFex.Authorization.Public.ApprovalToken(YFex.Authorization.Public.ApprovalTokenKind.Device, "sup", "0=acme/1=loja7",
            YFex.Authorization.Public.Permission.Parse("sales.discount.override"), Digest,
            new ValidityPeriod(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 16, 12, 15, 0, TimeSpan.Zero)),
            YFex.Cryptography.KeyId.New(), new YFex.Installations.InstallationId(Guid.NewGuid()), new byte[] { 1, 2, 3, 4 });

        var back = RoundTrip(token)!;

        back.Should().BeEquivalentTo(token, o => o.Excluding(t => t.Signature));
        back.Signature.ToArray().Should().Equal(1, 2, 3, 4);
        back.Validity.Should().Be(token.Validity);
        RoundTrip<YFex.Authorization.Public.ApprovalToken?>(null).Should().BeNull();
        RoundTrip(token.Permission).Should().Be(token.Permission);
        RoundTrip(Digest).Should().Be(Digest);
        RoundTrip<IReadOnlyList<YFex.Authorization.Public.ApprovalToken>>([token]).Single().Approver.Should().Be("sup");
    }

    [Fact]
    public void Forbidden_error_round_trips_the_denial()
    {
        var denial = new YFex.Authorization.Public.Denial(YFex.Authorization.Public.DenialKind.MissingPermission,
            ShopPermissions.CreateCustomer, YFex.Authorization.Public.PolicyName.Parse("cashier-open"), "code-1");
        var back = RoundTrip<Error>(new Forbidden(denial));

        back.Kind.Should().Be(ErrorKind.Forbidden);
        back.TryGetValue(out Forbidden f).Should().BeTrue();
        f.Denial.Should().Be(denial);
        back.Message.Should().Be("Requires permission 'shop.customers.create'.");
    }

    [Fact]
    public void Unauthenticated_not_found_failed_and_conflict_round_trip()
    {
        RoundTrip<Error>(new Unauthenticated("login again")).Should().Match<Error>(e => e.Kind == ErrorKind.Unauthenticated && e.Message == "login again");
        RoundTrip<Error>(new NotFound("gone")).Kind.Should().Be(ErrorKind.NotFound);
        RoundTrip<Error>(new Failed("boom")).Message.Should().Be("boom");
        var conflict = RoundTrip<Error>(new Conflict("stale", ViolationList.From([new Violation(FieldPath.Empty, "rules.decision.mismatch")])));
        conflict.TryGetValue(out Conflict c).Should().BeTrue();
        c.Message.Should().Be("stale");
        c.Violations.Count.Should().Be(1);
    }

    [Fact]
    public void Custom_message_survives_the_round_trip()
        => RoundTrip<Error>(new Error(new ValidationProblem(default)).WithMessage("custom")).Message.Should().Be("custom");

    [Fact]
    public void Default_error_round_trips()
        => RoundTrip(default(Error)).Kind.Should().Be(ErrorKind.None);

    [Fact]
    public void Result_unions_round_trip_every_case()
    {
        RoundTrip(QueueableResult<int>.Ok(42)).OkValue.Should().Be(42);
        RoundTrip<QueueableResult<int>>(Rich).TryGetError(out var e).Should().BeTrue();
        e.Violations.Count.Should().Be(3);
        var key = Guid.NewGuid();
        RoundTrip(QueueableResult<int>.Queue(key)).TryGetQueued(out var q).Should().BeTrue();
        q.IdempotencyKey.Should().Be(key);

        RoundTrip(QueueableResult.Ok()).IsSuccess.Should().BeTrue();
        RoundTrip<QueueableResult>(Rich).IsError.Should().BeTrue();

        RoundTrip(Result<List<string>>.Ok(["a", "b"])).OkValue.Should().Equal("a", "b");
        RoundTrip<Result<List<string>>>(Rich).IsError.Should().BeTrue();
        RoundTrip(Result.Ok()).IsOk.Should().BeTrue();
    }

    [Fact]
    public void ValidationResult_round_trips()
        => RoundTrip(ValidationResult.Of(Rich.Violations)).Violations.Count.Should().Be(3);
}
