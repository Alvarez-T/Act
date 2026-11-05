using YFex.Authorization.Public;
using YFex.Cqrs;
using YFex.Tenancy;
using ExportCustomers = YFex.Messaging.Rpc.Tests.ShopAggregate.Queries.ExportCustomers;
using WhoAmI = YFex.Messaging.Rpc.Tests.ShopAggregate.Queries.WhoAmI;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>
/// Sam and Sid are support staff of acme (per-call tokens). Sam holds <c>tenant.impersonate</c> and the export permission; the server
/// honours impersonation (<c>AllowImpersonation()</c>) and withholds export while impersonating.
/// </summary>
public sealed class ImpersonationFixture : RpcFixture
{
    public ImpersonationFixture() : base(null, ScopeIdentity.None,
        a => a.AddConfiguration(new ExportRequiresPermission()).RestrictWhileImpersonating(ShopPermissions.ExportCustomers),
        callContext: ctx => ctx.FromConnection().OrFromHeader("Authorization").AllowImpersonation())
    {
        Permissions.Grant("sam", AuthorizationPermissions.TenantImpersonate, ShopPermissions.ExportCustomers, ShopPermissions.SearchCustomers);
        Permissions.Grant("sid", ShopPermissions.ExportCustomers, ShopPermissions.SearchCustomers);
    }

    private sealed class ExportRequiresPermission : IAuthorizationConfiguration
    {
        public void Configure(AuthorizationBuilder builder)
            => builder.ForMessage<ExportCustomers>().RequirePermission(ShopPermissions.ExportCustomers);
    }
}

/// <summary>Phase 6 (⚑Z14) RPC parity with HTTP: the gate, the real actor in <see cref="ActorContext"/>, the restrictions.</summary>
public class RpcImpersonationTests(ImpersonationFixture fx) : IClassFixture<ImpersonationFixture>
{
    [Fact]
    public async Task Permitted_support_runs_in_the_target_tenant_with_the_real_actor_for_the_whole_call()
    {
        var who = await As.Caller("sam", Users.Globex, () => fx.Contract.WhoAmIAsync(new WhoAmI(601)));

        who.OkValue.Should().Be("sam@globex");
        var actor = fx.WhoAmI.LastActor!;
        actor.Relation.Should().Be(ActorRelation.TenantImpersonation);
        actor.Actor.Subject.Should().Be("sam");
        actor.RealActor!.Value.Subject.Should().Be("sam");
        actor.RealActor.Value.HomeScope.Should().Be(Users.Acme.ToString());
    }

    [Fact]
    public async Task Restricted_permission_is_denied_with_RestrictedWhileImpersonating_and_allowed_at_home()
    {
        var denied = await As.Caller("sam", Users.Globex, () => fx.Contract.ExportCustomersAsync(new ExportCustomers(3)));
        denied.TryGetValue(out Error error).Should().BeTrue();
        error.Kind.Should().Be(ErrorKind.Forbidden);
        error.TryGetValue(out Forbidden forbidden).Should().BeTrue();
        forbidden.Denial.Kind.Should().Be(DenialKind.RestrictedWhileImpersonating);
        forbidden.Denial.Missing.Should().Be(ShopPermissions.ExportCustomers);

        (await As.Caller("sam", Users.Acme, () => fx.Contract.ExportCustomersAsync(new ExportCustomers(3)))).OkValue.Should().Be(3);
    }

    [Fact]
    public async Task Support_without_tenant_impersonate_stays_in_the_home_tenant_as_the_plain_user()
    {
        var who = await As.Caller("sid", Users.Globex, () => fx.Contract.WhoAmIAsync(new WhoAmI(602)));

        who.OkValue.Should().Be("sid@acme");
        fx.WhoAmI.LastActor!.Relation.Should().Be(ActorRelation.None);
    }
}

/// <summary>Without <c>AllowImpersonation()</c> an impersonating identity runs in its home tenant (fail closed).</summary>
public class RpcImpersonationNotAllowedTests(GateFixture fx) : IClassFixture<GateFixture>
{
    [Fact]
    public async Task Impersonation_is_ignored_when_the_server_does_not_allow_it()
    {
        fx.Permissions.Grant("sam", AuthorizationPermissions.TenantImpersonate);

        var who = await As.Caller("sam", Users.Globex, () => fx.Contract.WhoAmIAsync(new WhoAmI(603)));

        who.OkValue.Should().Be("sam@acme");
        fx.WhoAmI.LastActor!.Relation.Should().Be(ActorRelation.None);
    }
}
