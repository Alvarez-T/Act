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

// Allows or denies everything; a denial names the policy's first permission (like the real evaluator for a one-permission policy).
internal sealed class FakeEvaluator(bool allow) : IPolicyEvaluator
{
    public bool Allow { get; set; } = allow;
    public bool IsVisible(CompiledPolicy policy, IAuthorizationContext caller) => Allow;
    public AuthorizationVerdict Authorize(CompiledPolicy policy, IAuthorizationContext caller, object? resource = null)
        => Allow ? AuthorizationVerdict.Allowed : AuthorizationVerdict.Forbidden(Denial.MissingPermissionOf(policy.FirstRequiredPermission));
    public ValueTask<AuthorizationVerdict> AuthorizeAsync(CompiledPolicy policy, IAuthorizationContext caller, object? resource,
        CancellationToken cancellationToken = default) => new(Authorize(policy, caller, resource));
}

internal sealed class FakeSignals : IAuthorizationSignals
{
    public void PermissionsChanged(string subject) => Changed?.Invoke(new AuthorizationChange(AuthorizationChangeKind.PermissionsChanged, subject));
    public void AllPermissionsChanged() => Changed?.Invoke(new AuthorizationChange(AuthorizationChangeKind.AllPermissionsChanged, null));
    public ValueTask RevokedAsync(string subject, CancellationToken cancellationToken = default) => default;
    public event Action<AuthorizationChange>? Changed;
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
    private static readonly PermissionKey SettingsView = DeclareSettingsView();

    private static readonly CompiledPolicy SettingsPolicy = CompiledPolicy.Compile(
        PolicyKey.Route(typeof(SettingsViewModel)), [Requirement.Require(new Permission(SettingsView))]);

    // A catalog entry gives the permission its display name ("Requer: Abrir configurações").
    private static PermissionKey DeclareSettingsView()
    {
        var key = PermissionKey.Declare("settings.view");
        PermissionCatalogs.Register(new AuthorizationManifest([new PermissionEntry(key, "Abrir configurações", null, "Test.SettingsView")]));
        return key;
    }

    private static (Navigator navigator, FakeNavigation navPane) CreateGated(bool allow)
    {
        var (navigator, navPane, _, _) = CreateGated(allow, signals: null);
        return (navigator, navPane);
    }

    private static (Navigator navigator, FakeNavigation navPane, FakeEvaluator evaluator, FakeSignals? signals) CreateGated(
        bool allow, FakeSignals? signals)
    {
        var evaluator = new FakeEvaluator(allow);
        var (navigator, navPane, _) = NavigatorFactory.Create(services =>
        {
            services.AddSingleton<IViewModelPolicyProvider>(
                new FakePolicyProvider(typeof(SettingsViewModel), SettingsPolicy));
            services.AddSingleton<IPolicyEvaluator>(evaluator);
            services.AddSingleton<IPrincipalAccessor>(new FakePrincipalAccessor());
            if (signals is not null) services.AddSingleton<IAuthorizationSignals>(signals);
        });
        return (navigator, navPane, evaluator, signals);
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
        Assert.Equal("Requer: Abrir configurações", denied.Reason);          // catalog display name, from the verdict
    }

    [Fact]
    public async Task Denial_carries_the_verdict()
    {
        var (navigator, _) = CreateGated(allow: false);

        var denied = Assert.IsType<NavigationDenied>((await navigator.NavigateTo(new SettingsRoute())).Value);

        Assert.True(denied.IsAuthorizationDenial);
        Assert.True(denied.Verdict.TryGetValue(out Denial denial));
        Assert.Equal(DenialKind.MissingPermission, denial.Kind);
        Assert.Equal(SettingsView, denial.Missing.Key);
    }

    [Fact]
    public void Denial_text_falls_back_to_the_verdict_description()
    {
        Assert.Equal("Authentication required.", Navigator.DenialText(AuthorizationVerdict.Unauthenticated));
        Assert.Equal("Requer: undeclared.permission",
            Navigator.DenialText(AuthorizationVerdict.Forbidden(new Denial(DenialKind.MissingPermission, Permission.Parse("undeclared.permission")))));
    }

    [Fact]
    public async Task Recheck_closes_a_page_that_became_forbidden_and_returns_to_the_previous_one()
    {
        var (navigator, navPane, evaluator, _) = CreateGated(allow: true, signals: null);
        await navigator.NavigateTo(new HomeRoute());
        await navigator.NavigateTo(new SettingsRoute());
        NavigationDenied? raised = null;
        navigator.ActivePageDenied += d => raised = d;

        Assert.Null(await navigator.RecheckAuthorizationAsync());          // still allowed: nothing happens
        evaluator.Allow = false;                                           // permission revoked
        var denied = await navigator.RecheckAuthorizationAsync();

        Assert.NotNull(denied);
        Assert.Same(denied, raised);
        Assert.Equal("Requer: Abrir configurações", denied!.Reason);
        Assert.IsType<HomeViewModel>(navPane.LastView);                    // back on the page that has no policy
        Assert.Single(navigator.Breadcrumb);                               // the forbidden page is gone (no forward to it)
        Assert.Equal(1, navPane.DeniedCount);
    }

    [Fact]
    public async Task Recheck_clears_the_history_when_no_earlier_page_is_allowed()
    {
        var (navigator, _, evaluator, _) = CreateGated(allow: true, signals: null);
        await navigator.NavigateTo(new SettingsRoute());
        evaluator.Allow = false;

        Assert.NotNull(await navigator.RecheckAuthorizationAsync());
        Assert.Empty(navigator.Breadcrumb);
    }

    [Fact]
    public async Task A_signalled_permission_change_of_the_caller_rechecks_the_active_page()
    {
        var (navigator, _, evaluator, signals) = CreateGated(allow: true, signals: new FakeSignals());
        await navigator.NavigateTo(new HomeRoute());
        await navigator.NavigateTo(new SettingsRoute());
        var closed = new TaskCompletionSource<NavigationDenied>(TaskCreationOptions.RunContinuationsAsynchronously);
        navigator.ActivePageDenied += d => closed.TrySetResult(d);

        evaluator.Allow = false;
        signals!.PermissionsChanged("someone-else");                       // another subject: ignored
        Assert.Equal(2, navigator.Breadcrumb.Count);
        signals.PermissionsChanged("nav-test");                            // the caller of FakePrincipalAccessor

        var denied = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DenialKind.MissingPermission, denied.Verdict.Denial.Kind);
        Assert.Single(navigator.Breadcrumb);
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
