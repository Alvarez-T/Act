using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using YFex.Tenancy;

namespace YFex.Cqrs.Tenancy.Tests;

public enum TestScope : byte { Org = 0, Tenant = 1, User = 2 }

public class TenancyCacheScopeContextTests
{
    private static readonly ScopeIdentity AcmeBob = ScopeIdentity.Of(
        ScopeValue.Of(TestScope.Org, "acme"), ScopeValue.Of(TestScope.Tenant, "store7"), ScopeValue.Of(TestScope.User, "bob"));

    private static TenancyCacheScopeContext Create(
        Action<QueryCacheScopeOptions> configure, ICacheScopeContext? inner = null, UnscopedBehavior whenUnscoped = UnscopedBehavior.Shared)
    {
        var options = new QueryCacheScopeOptions();
        configure(options);
        return new TenancyCacheScopeContext(options, scope: null, inner, whenUnscoped);
    }

    [Fact]
    public void TenantId_is_the_chain_up_to_the_tenant_level()
    {
        var ctx = Create(o => o.Tenant(TestScope.Tenant));
        using (ScopeOverride.Enter(AcmeBob))
            ctx.TenantId.Should().Be("0=acme/1=store7");
    }

    [Fact]
    public void Same_tenant_value_under_different_parents_stays_distinct()
    {
        var ctx = Create(o => o.Tenant(TestScope.Tenant));
        string? a, b;
        using (ScopeOverride.Enter(ScopeValue.Of(TestScope.Org, "acme"), ScopeValue.Of(TestScope.Tenant, "main")))
            a = ctx.TenantId;
        using (ScopeOverride.Enter(ScopeValue.Of(TestScope.Org, "globex"), ScopeValue.Of(TestScope.Tenant, "main")))
            b = ctx.TenantId;
        a.Should().NotBe(b);
    }

    [Fact]
    public void User_is_a_principal_named_by_the_chain_up_to_the_user_level()
    {
        var ctx = Create(o => o.Tenant(TestScope.Tenant).User(TestScope.User));
        using (ScopeOverride.Enter(AcmeBob))
        {
            ctx.User.Identity!.IsAuthenticated.Should().BeTrue();
            ctx.User.Identity.Name.Should().Be("0=acme/1=store7/2=bob");
        }
    }

    [Fact]
    public void Nothing_in_scope_yields_null_tenant_and_anonymous_user()
    {
        var ctx = Create(o => o.Tenant(TestScope.Tenant).User(TestScope.User));
        using (ScopeOverride.EnterUnscoped())
        {
            ctx.TenantId.Should().BeNull();
            ctx.User.Identity!.Name.Should().BeNull();
        }
    }

    [Fact]
    public void Fail_closed_throws_when_nothing_is_in_scope()
    {
        var ctx = Create(o => o.Tenant(TestScope.Tenant), whenUnscoped: UnscopedBehavior.Throw);
        using (ScopeOverride.EnterUnscoped())
            ctx.Invoking(c => c.TenantId).Should().Throw<ScopeIsolationException>();
    }

    [Fact]
    public void Values_are_escaped_so_they_cannot_forge_another_segment()
    {
        var ctx = Create(o => o.Tenant(TestScope.Tenant));
        using (ScopeOverride.Enter(ScopeValue.Of(TestScope.Tenant, "a/1=b")))
            ctx.TenantId.Should().Be("1=a%2F1=b");
    }

    [Fact]
    public void Unclaimed_values_come_from_the_host_context()
    {
        var host = new HostContext();
        var ctx = Create(o => o.Tenant(TestScope.Tenant), host);
        using (ScopeOverride.Enter(AcmeBob))
        {
            ctx.User.Should().BeSameAs(host.User);
            ctx.SessionId.Should().Be("sess-1");
            ctx.TenantId.Should().Be("0=acme/1=store7");
        }
    }

    [Fact]
    public void Requires_at_least_one_level()
    {
        var act = () => new TenancyCacheScopeContext(new QueryCacheScopeOptions());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsolateQueryCache_registers_the_context()
    {
        var services = new ServiceCollection();
        services.AddYFexTenancy(t => t.IsolateQueryCache(q => q.Tenant(TestScope.Tenant)));
        using var sp = services.BuildServiceProvider();

        var ctx = sp.GetRequiredService<ICacheScopeContext>();
        ctx.Should().BeOfType<TenancyCacheScopeContext>();
        using (ScopeOverride.Enter(AcmeBob))
            ctx.TenantId.Should().Be("0=acme/1=store7");
    }

    [Fact]
    public void IsolateQueryCache_decorates_a_host_context_and_keeps_its_lifetime()
    {
        var services = new ServiceCollection();
        services.AddScoped<ICacheScopeContext, HostContext>();
        services.AddYFexTenancy(t => t.IsolateQueryCache(q => q.Tenant(TestScope.Tenant)));

        services.Single(d => d.ServiceType == typeof(ICacheScopeContext)).Lifetime.Should().Be(ServiceLifetime.Scoped);
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICacheScopeContext>().SessionId.Should().Be("sess-1");
    }

    [Fact]
    public void IsolateQueryCache_follows_a_fail_closed_global_policy()
    {
        var services = new ServiceCollection();
        services.AddYFexTenancy(t => t
            .Isolation(o => o.FailClosed())
            .IsolateQueryCache(q => q.Tenant(TestScope.Tenant)));
        using var sp = services.BuildServiceProvider();

        var ctx = sp.GetRequiredService<ICacheScopeContext>();
        using (ScopeOverride.EnterUnscoped())
            ctx.Invoking(c => c.TenantId).Should().Throw<ScopeIsolationException>();
    }

    private sealed class HostContext : ICacheScopeContext
    {
        public ClaimsPrincipal User { get; } = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "host-user")], "test"));
        public string? TenantId => "host-tenant";
        public string? SessionId => "sess-1";
    }
}
