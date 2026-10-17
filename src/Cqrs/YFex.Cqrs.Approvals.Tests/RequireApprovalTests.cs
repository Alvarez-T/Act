using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Authorization.Testing;
using YFex.Cqrs.Runtime;
using YFex.Cryptography;
using YFex.Installations;
using VoidSale = YFex.Cqrs.ApprovalsTests.Sales.Commands.VoidSale;
using RefundSale = YFex.Cqrs.ApprovalsTests.Sales.Commands.RefundSale;

namespace YFex.Cqrs.ApprovalsTests;

/// <summary>
/// Authorization-consolidation Phase 4b acceptance: <c>.RequireApproval</c> round trip through <c>Approvals.Commands.Approve</c>, the offline
/// device token (flagged), and the refusals of 06 (other bound value, other scope, expired, tampered, self-approval).
/// </summary>
public class RequireApprovalTests
{
    private static readonly Guid Sale = Guid.Parse("0199e3a0-1111-7000-8000-000000000001");

    private static ApprovalFixture Cashier(Action<ApprovalOptions>? options = null, Action<IServiceCollection>? services = null, string? scope = null)
    {
        var f = new ApprovalFixture(options, services);
        f.User.As("cashier", scope, SalesPermissions.SaleVoid);
        return f;
    }

    private static async Task<ApprovalRequired> RequiredAsync(ApprovalFixture f, VoidSale command)
    {
        var result = await f.Dispatcher.CommandAsync<VoidSale, int>(command);
        result.TryGetError(out var error).Should().BeTrue();
        error.Kind.Should().Be(ErrorKind.ApprovalRequired);
        error.TryGetValue(out ApprovalRequired approval).Should().BeTrue();
        return approval;
    }

    private static async Task<bool> PassesAsync(ApprovalFixture f, VoidSale command)
        => (await f.Dispatcher.CommandAsync<VoidSale, int>(command)).TryGetValue(out int _);

    [Fact]
    public async Task Cashier_gets_ApprovalRequired_supervisor_approves_and_the_resend_passes()
    {
        var f = Cashier();
        var approval = await RequiredAsync(f, new VoidSale(Sale));

        approval.Approvals.Requires(SalesPermissions.SaleVoidApprove).Should().BeTrue();
        approval.DecisionDigest.Should().Be(ApprovalDigest.Of(typeof(VoidSale), Sale));   // message type + bound value (A5)

        var token = await f.ApproveAsync(SalesPermissions.SaleVoidApprove, approval.DecisionDigest);
        token.Kind.Should().Be(ApprovalTokenKind.Server);
        token.Approver.Should().Be("sup");
        token.Validity.Duration.Should().Be(TimeSpan.FromMinutes(15));

        (await PassesAsync(f, new VoidSale(Sale, [token]))).Should().BeTrue();
        f.Audit.Flagged.Should().BeEmpty();                                                // online approvals are not flagged
    }

    [Fact]
    public async Task Approve_is_issued_only_to_holders_of_the_permission()
    {
        var f = Cashier();
        var digest = ApprovalDigest.Of(typeof(VoidSale), Sale);

        var denied = await f.Dispatcher.CommandAsync<Approvals.Commands.Approve, ApprovalToken>(new(SalesPermissions.SaleVoidApprove, digest));
        denied.TryGetError(out var error).Should().BeTrue();
        error.Kind.Should().Be(ErrorKind.Forbidden);
        error.TryGetValue(out Forbidden forbidden).Should().BeTrue();
        forbidden.Denial.Missing.Should().Be(SalesPermissions.SaleVoidApprove);

        f.User.Authenticated = false;
        (await f.Dispatcher.CommandAsync<Approvals.Commands.Approve, ApprovalToken>(new(SalesPermissions.SaleVoidApprove, digest)))
            .TryGetError(out var anonymous).Should().BeTrue();
        anonymous.Kind.Should().Be(ErrorKind.Unauthenticated);
    }

    [Fact]
    public async Task Impersonating_callers_cannot_issue_approvals()
    {
        var f = Cashier();
        f.User.As("sup", null, SalesPermissions.SaleVoidApprove).CurrentRelation = ActorRelation.TenantImpersonation;

        var result = await f.Dispatcher.CommandAsync<Approvals.Commands.Approve, ApprovalToken>(
            new(SalesPermissions.SaleVoidApprove, ApprovalDigest.Of(typeof(VoidSale), Sale)));

        result.TryGetError(out var error).Should().BeTrue();
        error.TryGetValue(out Forbidden forbidden).Should().BeTrue();
        forbidden.Denial.Kind.Should().Be(DenialKind.RestrictedWhileImpersonating);
    }

    [Fact]
    public async Task A_token_for_another_bound_value_or_permission_is_refused()
    {
        var f = Cashier();
        var otherSale = await f.ApproveAsync(SalesPermissions.SaleVoidApprove, ApprovalDigest.Of(typeof(VoidSale), Guid.NewGuid()));
        var otherPermission = await f.ApproveAsync(SalesPermissions.RefundApprove, ApprovalDigest.Of(typeof(VoidSale), Sale));
        var otherMessage = await f.ApproveAsync(SalesPermissions.SaleVoidApprove, ApprovalDigest.Of(typeof(RefundSale), Sale));

        (await PassesAsync(f, new VoidSale(Sale, [otherSale]))).Should().BeFalse();
        (await PassesAsync(f, new VoidSale(Sale, [otherPermission]))).Should().BeFalse();
        (await PassesAsync(f, new VoidSale(Sale, [otherMessage]))).Should().BeFalse();
    }

    [Fact]
    public async Task A_supervisor_of_another_store_cannot_approve()
    {
        var f = Cashier(scope: "0=acme/1=loja8");
        var digest = ApprovalDigest.Of(typeof(VoidSale), Sale);

        var otherStore = await f.ApproveAsync(SalesPermissions.SaleVoidApprove, digest, supervisorScope: "0=acme/1=loja7");
        var tenantWide = await f.ApproveAsync(SalesPermissions.SaleVoidApprove, digest, supervisorScope: "0=acme");
        var sameStore = await f.ApproveAsync(SalesPermissions.SaleVoidApprove, digest, supervisorScope: "0=acme/1=loja8");

        (await PassesAsync(f, new VoidSale(Sale, [otherStore]))).Should().BeFalse();
        (await PassesAsync(f, new VoidSale(Sale, [tenantWide]))).Should().BeTrue();
        (await PassesAsync(f, new VoidSale(Sale, [sameStore]))).Should().BeTrue();
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var f = Cashier();
        var token = await f.ApproveAsync(SalesPermissions.SaleVoidApprove, ApprovalDigest.Of(typeof(VoidSale), Sale));

        f.Time.Now = ApprovalFixture.Start.AddMinutes(16);
        (await PassesAsync(f, new VoidSale(Sale, [token]))).Should().BeFalse();
    }

    [Fact]
    public async Task A_tampered_token_is_refused()
    {
        var f = Cashier();
        var token = await f.ApproveAsync(SalesPermissions.SaleVoidApprove, ApprovalDigest.Of(typeof(VoidSale), Sale));
        byte[] flipped = token.Signature.ToArray();
        flipped[0] ^= 1;

        (await PassesAsync(f, new VoidSale(Sale, [token with { Approver = "boss" }]))).Should().BeFalse();
        (await PassesAsync(f, new VoidSale(Sale, [token with { Validity = ValidityPeriod.For(ApprovalFixture.Start, TimeSpan.FromDays(1)) }])))
            .Should().BeFalse();
        (await PassesAsync(f, new VoidSale(Sale, [token with { Signature = flipped }]))).Should().BeFalse();
        (await PassesAsync(f, new VoidSale(Sale, [token]))).Should().BeTrue();
    }

    [Fact]
    public async Task The_requester_cannot_approve_their_own_operation_unless_the_policy_allows_it()
    {
        var f = Cashier();
        f.User.Granted.Add(SalesPermissions.SaleVoidApprove.Value);
        f.User.Granted.Add(SalesPermissions.RefundApprove.Value);
        var own = await f.ApproveAsync(SalesPermissions.SaleVoidApprove, ApprovalDigest.Of(typeof(VoidSale), Sale), supervisor: "cashier");
        (await PassesAsync(f, new VoidSale(Sale, [own]))).Should().BeFalse();                      // separation of duties (A7)

        var refund = await f.ApproveAsync(SalesPermissions.RefundApprove, ApprovalDigest.Of(typeof(RefundSale), 77L), supervisor: "cashier");
        (await f.Dispatcher.CommandAsync<RefundSale, int>(new RefundSale(77, [refund]))).TryGetValue(out int value).Should().BeTrue();
        value.Should().Be(2);                                                                      // opt-out per policy
    }

    [Fact]
    public async Task An_offline_device_token_is_accepted_flagged_and_the_approver_is_rechecked()
    {
        var directory = new FakePermissionDirectory().Grant("sup", SalesPermissions.SaleVoidApprove);
        var f = Cashier(o => o.AllowOffline(SalesPermissions.SaleVoidApprove, TimeSpan.FromHours(48)), services =>
        {
            services.AddSingleton<IApprovalSignatureVerifier, FakeDeviceSignatures>();
            services.AddSingleton<IPermissionDirectory>(directory);
        });
        f.Origin.Current = new ApprovalOrigin(ApprovalFixture.Terminal);
        var device = Device(ApprovalFixture.Terminal);

        (await PassesAsync(f, new VoidSale(Sale, [device]))).Should().BeTrue();
        f.Audit.Flagged.Should().ContainSingle();                                                  // always reviewed (D34)
        directory.Questions.Should().Be(1);

        f.Origin.Current = new ApprovalOrigin(new InstallationId(Guid.NewGuid()));                 // another installation decided it (A6)
        (await PassesAsync(f, new VoidSale(Sale, [device]))).Should().BeFalse();

        f.Origin.Current = new ApprovalOrigin(ApprovalFixture.Terminal);
        directory.Revoke("sup", SalesPermissions.SaleVoidApprove);                                 // approver lost the permission meanwhile
        (await PassesAsync(f, new VoidSale(Sale, [device]))).Should().BeFalse();
    }

    [Fact]
    public async Task Offline_tokens_need_an_offline_policy_and_a_recheckable_approver()
    {
        var noPolicy = Cashier(services: s => s.AddSingleton<IApprovalSignatureVerifier, FakeDeviceSignatures>());
        noPolicy.Origin.Current = new ApprovalOrigin(ApprovalFixture.Terminal);
        (await PassesAsync(noPolicy, new VoidSale(Sale, [Device(ApprovalFixture.Terminal)]))).Should().BeFalse();

        var noDirectory = Cashier(o => o.AllowOffline(SalesPermissions.SaleVoidApprove, TimeSpan.FromHours(48)),
            s => s.AddSingleton<IApprovalSignatureVerifier, FakeDeviceSignatures>());
        noDirectory.Origin.Current = new ApprovalOrigin(ApprovalFixture.Terminal);
        (await PassesAsync(noDirectory, new VoidSale(Sale, [Device(ApprovalFixture.Terminal)]))).Should().BeFalse();   // WhenNoDirectory = Refuse

        var accept = Cashier(o =>
        {
            o.AllowOffline(SalesPermissions.SaleVoidApprove, TimeSpan.FromHours(48));
            o.WhenNoDirectory = UnverifiedApproverAction.AcceptAndFlag;
        }, s => s.AddSingleton<IApprovalSignatureVerifier, FakeDeviceSignatures>());
        accept.Origin.Current = new ApprovalOrigin(ApprovalFixture.Terminal);
        (await PassesAsync(accept, new VoidSale(Sale, [Device(ApprovalFixture.Terminal)]))).Should().BeTrue();
        accept.Audit.Flagged.Should().ContainSingle();
    }

    [Fact]
    public async Task A_policy_source_can_switch_the_approval_off_or_change_its_permission()
    {
        var off = Cashier(services: s => s.AddSingleton<IApprovalPolicySource>(new PolicySource(p => p with { IsRequired = false })));
        (await PassesAsync(off, new VoidSale(Sale))).Should().BeTrue();

        var other = Cashier(services: s => s.AddSingleton<IApprovalPolicySource>(new PolicySource(p => p with { Permission = SalesPermissions.RefundApprove })));
        var approval = await RequiredAsync(other, new VoidSale(Sale));
        approval.Approvals.Requires(SalesPermissions.RefundApprove).Should().BeTrue();
        var token = await other.ApproveAsync(SalesPermissions.RefundApprove, approval.DecisionDigest);
        (await PassesAsync(other, new VoidSale(Sale, [token]))).Should().BeTrue();
    }

    [Fact]
    public void RequireApproval_needs_an_approval_bound_message()
    {
        var act = () => ApprovalStages.Create<string>(SalesPermissions.SaleVoidApprove, _ => default);
        act.Should().Throw<InvalidOperationException>().WithMessage("*IApprovalBound*");
    }

    private static ApprovalToken Device(InstallationId installation)
        => new(ApprovalTokenKind.Device, "sup", null, SalesPermissions.SaleVoidApprove, ApprovalDigest.Of(typeof(VoidSale), Sale),
            ValidityPeriod.For(ApprovalFixture.Start.AddMinutes(-5), TimeSpan.FromMinutes(15)), KeyId.New(), installation, "ok"u8.ToArray());

    private sealed class PolicySource(Func<ApprovalPolicy, ApprovalPolicy> change) : IApprovalPolicySource
    {
        public ApprovalPolicy Resolve(Type messageType, ApprovalPolicy current) => change(current);
    }
}
