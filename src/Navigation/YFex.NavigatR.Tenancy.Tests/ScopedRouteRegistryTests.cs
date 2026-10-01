namespace YFex.NavigatR.Tenancy.Tests;

public sealed class ScopedRouteRegistryTests
{
    // The test app's OWN level enum — NavigatR knows none of these names (byte = specificity rank).
    private enum NavScope : byte { Organization = 0, Tenant = 1, User = 2 }

    private sealed record OrderRoute : IRoute;

    // Minimal INavigable stubs — only their TYPE matters to the registry.
    private abstract class Vm : INavigable
    {
        public Task OnNavigation(NavigationContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnResume(CancellationToken ct = default) => Task.CompletedTask;
        public Task OnSuspend(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }
    private sealed class DefaultOrderVm : Vm;
    private sealed class AcmeOrderVm : Vm;
    private sealed class AcmeWestOrderVm : Vm;
    private sealed class VipOrderVm : Vm;

    private static RouteRegistry BuildRegistry()
    {
        var r = new RouteRegistry();
        r.Register<OrderRoute, DefaultOrderVm>();
        r.RegisterFor<OrderRoute, AcmeOrderVm>(NavScope.Organization, "acme");
        r.RegisterFor<OrderRoute, AcmeWestOrderVm>(NavScope.Tenant, "acme-west");
        r.RegisterFor<OrderRoute, VipOrderVm>(NavScope.User, "u-42");
        return r;
    }

    private static ScopeIdentity Id(string? org = null, string? tenant = null, string? user = null)
    {
        var rungs = new List<ScopeValue>(3);
        if (org is not null) rungs.Add(ScopeValue.Of(NavScope.Organization, org));
        if (tenant is not null) rungs.Add(ScopeValue.Of(NavScope.Tenant, tenant));
        if (user is not null) rungs.Add(ScopeValue.Of(NavScope.User, user));
        return ScopeIdentity.Of(rungs.ToArray());
    }

    [Fact]
    public void No_identity_uses_base_mapping()
    {
        var r = BuildRegistry();
        r.ResolveViewModel(new OrderRoute(), ScopeIdentity.None.Rungs).Should().Be(typeof(DefaultOrderVm));
    }

    [Fact]
    public void User_scope_wins_over_tenant_and_org()
    {
        var r = BuildRegistry();
        r.ResolveViewModel(new OrderRoute(), Id("acme", "acme-west", "u-42").Rungs).Should().Be(typeof(VipOrderVm));
    }

    [Fact]
    public void Tenant_scope_wins_over_org()
    {
        var r = BuildRegistry();
        r.ResolveViewModel(new OrderRoute(), Id("acme", "acme-west").Rungs).Should().Be(typeof(AcmeWestOrderVm));
    }

    [Fact]
    public void Org_scope_used_when_no_more_specific_override()
    {
        var r = BuildRegistry();
        r.ResolveViewModel(new OrderRoute(), Id("acme", "tenant-no-override", "user-no-override").Rungs)
            .Should().Be(typeof(AcmeOrderVm));
    }

    [Fact]
    public void Unmatched_identity_falls_back_to_base()
    {
        var r = BuildRegistry();
        r.ResolveViewModel(new OrderRoute(), Id("globex", "globex-east", "u-99").Rungs).Should().Be(typeof(DefaultOrderVm));
    }

    [Fact]
    public void Base_resolution_unchanged_when_no_overlay_registered()
    {
        var r = new RouteRegistry();
        r.Register<OrderRoute, DefaultOrderVm>();
        r.ResolveViewModel(new OrderRoute(), Id("acme").Rungs).Should().Be(typeof(DefaultOrderVm));
    }
}
