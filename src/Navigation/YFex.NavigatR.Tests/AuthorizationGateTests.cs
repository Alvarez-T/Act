using Microsoft.Extensions.DependencyInjection;
using Xunit;
using YFex.Authorization.Public;

namespace YFex.NavigatR.Tests;

// ─── Fakes over the .Public authorization seam ───────────────────────────────

internal sealed class FakeAuthContext : IAuthorizationContext
{
    public System.Security.Claims.ClaimsPrincipal Principal { get; } = new();
    public ActorIdentity Actor => new("nav-test");
    public ActorIdentity? RealActor => null;
    public ActorRelation Relation => ActorRelation.None;
    public string? Scope => null;
    public PermissionSet Permissions => PermissionSet.Empty;
    public long Version => 0;
    public AccessToken? Token => null;
    public bool IsAuthenticated => true;
    public bool IsInRole(in Role role) => false;
    public bool HasPermission(in Permission permission) => false;
    public bool HasClaim(in Claim claim) => false;
}

internal sealed class FakePrincipalAccessor : IPrincipalAccessor
{
    public IAuthorizationContext Current { get; } = new FakeAuthContext();
}

internal sealed class FakeEvaluator(bool allow) : IPolicyEvaluator
{
    public bool IsVisible(CompiledPolicy policy, IAuthorizationContext caller) => allow;
    public AuthorizationVerdict Authorize(CompiledPolicy policy, IAuthorizationContext caller, object? resource = null)
        => allow ? AuthorizationVerdict.Allowed : AuthorizationVerdict.Forbidden(new Denial(DenialKind.PolicyDenied));
    public ValueTask<AuthorizationVerdict> AuthorizeAsync(CompiledPolicy policy, IAuthorizationContext caller, object? resource,
        CancellationToken cancellationToken = default) => new(Authorize(policy, caller, resource));
}

internal sealed class FakePolicyProvider(Type vmType, CompiledPolicy vmPolicy) : IViewModelPolicyProvider
{
    public bool TryGetPolicy(Type viewModelType, out CompiledPolicy policy)
    {
        if (viewModelType == vmType) { policy = vmPolicy; return true; }
        policy = null!;
        return false;
    }
}

// ─── Gate 2 — NavigatR whole-screen navigation authorization ─────────────────

public sealed class AuthorizationGateTests
{
    private static readonly CompiledPolicy SettingsPolicy = CompiledPolicy.Compile(
        PolicyKey.Route(typeof(SettingsViewModel)), [Requirement.Require(new Permission(PermissionKey.Declare("settings.view")))]);

    private static (Navigator navigator, FakeNavigation navPane) CreateGated(bool allow)
    {
        var (navigator, navPane, _) = NavigatorFactory.Create(services =>
        {
            services.AddSingleton<IViewModelPolicyProvider>(
                new FakePolicyProvider(typeof(SettingsViewModel), SettingsPolicy));
            services.AddSingleton<IPolicyEvaluator>(new FakeEvaluator(allow));
            services.AddSingleton<IPrincipalAccessor>(new FakePrincipalAccessor());
        });
        return (navigator, navPane);
    }

    [Fact]
    public async Task Denied_route_returns_NavigationDenied_and_never_constructs_the_vm()
    {
        var (navigator, navPane) = CreateGated(allow: false);

        var result = await navigator.NavigateTo(new SettingsRoute());

        Assert.IsType<NavigationDenied>(result.Value);
        Assert.Empty(navPane.NavigatedViews);           // VM never resolved / shown
        Assert.Empty(navigator.Breadcrumb);             // nothing pushed onto history
        Assert.Equal(1, navPane.DeniedCount);
    }

    [Fact]
    public async Task Denied_reason_names_the_required_permission()
    {
        var (navigator, _) = CreateGated(allow: false);

        var result = await navigator.NavigateTo(new SettingsRoute());

        var denied = Assert.IsType<NavigationDenied>(result.Value);
        Assert.Contains("settings.view", denied.Reason);
    }

    [Fact]
    public async Task Allowed_route_navigates_normally()
    {
        var (navigator, navPane) = CreateGated(allow: true);

        var result = await navigator.NavigateTo(new SettingsRoute());

        Assert.IsType<NavigationSuccess>(result.Value);
        Assert.IsType<SettingsViewModel>(navPane.LastView);
    }

    [Fact]
    public async Task Route_without_a_policy_is_never_gated()
    {
        // Evaluator denies everything, but HomeViewModel has no policy → allowed.
        var (navigator, navPane) = CreateGated(allow: false);

        var result = await navigator.NavigateTo(new HomeRoute());

        Assert.IsType<NavigationSuccess>(result.Value);
        Assert.IsType<HomeViewModel>(navPane.LastView);
    }

    [Fact]
    public async Task Navigation_is_not_gated_when_authorization_is_not_wired()
    {
        // Default factory registers no authorization services.
        var (navigator, navPane, _) = NavigatorFactory.Create();

        var result = await navigator.NavigateTo(new SettingsRoute());

        Assert.IsType<NavigationSuccess>(result.Value);
        Assert.IsType<SettingsViewModel>(navPane.LastView);
    }
}
