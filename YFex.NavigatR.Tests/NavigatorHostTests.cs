using Microsoft.Extensions.DependencyInjection;
using YFex.NavigatR;
using Xunit;

namespace YFex.NavigatR.Tests;

public sealed class NavigatorHostTests : IDisposable
{
    private readonly NavigatorHost _host;
    private readonly RouteRegistry _registry;
    private readonly IServiceProvider _provider;

    public NavigatorHostTests()
    {
        var services = new ServiceCollection();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<OrderViewModel>();
        services.AddTransient<PickerViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddSingleton<RouteRegistry>();

        _provider = services.BuildServiceProvider();
        _registry = _provider.GetRequiredService<RouteRegistry>();
        _registry.Register<HomeRoute, HomeViewModel>();
        _registry.Register<OrderRoute, OrderViewModel>();
        _registry.Register<PickerRoute, PickerViewModel>();
        _registry.Register<SettingsRoute, SettingsViewModel>();
        _registry.Register<SearchRoute, HomeViewModel>();

        _host = new NavigatorHost(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _registry);
    }

    public void Dispose()
    {
        _host.Dispose();
        NavigatorLocator.Clear();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CreateContext
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CreateContext_ReturnsNewNavigator()
    {
        var ctx = _host.CreateContext();

        Assert.NotNull(ctx);
        Assert.Single(_host.Contexts);
    }

    [Fact]
    public void CreateContext_AssignsNavPane()
    {
        var navPane = new FakeNavigation();
        var ctx = _host.CreateContext(navPane: navPane);

        Assert.Same(navPane, ctx.NavPane);
    }

    [Fact]
    public void CreateContext_FirstContext_AutomaticallyBecomesActive()
    {
        var ctx = _host.CreateContext();

        Assert.Same(ctx, _host.ActiveContext);
    }

    [Fact]
    public void CreateContext_SubsequentContext_DoesNotBecomeActiveByDefault()
    {
        var ctxA = _host.CreateContext();
        var ctxB = _host.CreateContext();

        Assert.Same(ctxA, _host.ActiveContext);
        Assert.NotSame(ctxB, _host.ActiveContext);
    }

    [Fact]
    public async Task CreateContext_WithSetActiveTrue_BecomesActive()
    {
        var ctxA = _host.CreateContext();
        var ctxB = _host.CreateContext(setActive: true);

        await _host.SwitchContextAsync(ctxB.Id);

        Assert.Same(ctxB, _host.ActiveContext);
    }

    [Fact]
    public void CreateContext_InheritsHistoryPolicyFromActive()
    {
        var ctxA = _host.CreateContext();
        ctxA.HistoryPolicy = NavigationHistoryPolicy.PreserveForwardOnBranch;

        var ctxB = _host.CreateContext();

        Assert.Equal(NavigationHistoryPolicy.PreserveForwardOnBranch, ctxB.HistoryPolicy);
    }

    [Fact]
    public void CreateContext_MultipleContexts_EachHasUniqueId()
    {
        var ctxA = _host.CreateContext();
        var ctxB = _host.CreateContext();

        Assert.NotEqual(ctxA.Id, ctxB.Id);
        Assert.Equal(2, _host.Contexts.Count);
    }

    [Fact]
    public void CreateContext_AfterDispose_ThrowsObjectDisposedException()
    {
        _host.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _host.CreateContext());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // RegisterContext
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RegisterContext_AddsExistingNavigatorToHost()
    {
        var scope = _provider.CreateScope();
        var navigator = new Navigator(scope, _registry) { NavPane = new FakeNavigation() };

        _host.RegisterContext(navigator);

        Assert.Single(_host.Contexts);
        Assert.Contains(navigator, _host.Contexts);
    }

    [Fact]
    public void RegisterContext_FirstRegistered_BecomesActive()
    {
        var scope = _provider.CreateScope();
        var navigator = new Navigator(scope, _registry) { NavPane = new FakeNavigation() };

        _host.RegisterContext(navigator);

        Assert.Same(navigator, _host.ActiveContext);
    }

    [Fact]
    public void RegisterContext_SameNavigatorTwice_Throws()
    {
        var scope = _provider.CreateScope();
        var navigator = new Navigator(scope, _registry) { NavPane = new FakeNavigation() };

        _host.RegisterContext(navigator);

        Assert.Throws<InvalidOperationException>(() => _host.RegisterContext(navigator));
    }

    [Fact]
    public void RegisterContext_AfterDispose_ThrowsObjectDisposedException()
    {
        var scope = _provider.CreateScope();
        var navigator = new Navigator(scope, _registry) { NavPane = new FakeNavigation() };

        _host.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _host.RegisterContext(navigator));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SwitchContext
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SwitchContextAsync_SetsActiveContext()
    {
        var ctxA = _host.CreateContext(new FakeNavigation());
        var ctxB = _host.CreateContext(new FakeNavigation());

        await _host.SwitchContextAsync(ctxB.Id);

        Assert.Same(ctxB, _host.ActiveContext);
    }

    [Fact]
    public async Task SwitchContextAsync_SuspendsTopOfPreviousContext()
    {
        var navPaneA = new FakeNavigation();
        var ctxA = _host.CreateContext(navPaneA);
        NavigatorLocator.Set(ctxA);
        await ctxA.NavigateTo(new HomeRoute());

        var homeVm = (HomeViewModel)ctxA.Breadcrumb[0].NavigableInstance!;

        var ctxB = _host.CreateContext(new FakeNavigation());
        await _host.SwitchContextAsync(ctxB.Id);

        Assert.Contains("OnSuspend", homeVm.Calls);
    }

    [Fact]
    public async Task SwitchContextAsync_ResumesTopOfTargetContext()
    {
        var ctxA = _host.CreateContext(new FakeNavigation());
        var ctxB = _host.CreateContext(new FakeNavigation());

        NavigatorLocator.Set(ctxB);
        await ctxB.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)ctxB.Breadcrumb[0].NavigableInstance!;

        // Switch away then back
        await _host.SwitchContextAsync(ctxA.Id);
        await _host.SwitchContextAsync(ctxB.Id);

        Assert.Contains("OnResume", homeVm.Calls);
    }

    [Fact]
    public async Task SwitchContextAsync_PinnedTopEntry_NotSuspended()
    {
        var ctxA = _host.CreateContext(new FakeNavigation());
        var ctxB = _host.CreateContext(new FakeNavigation());

        NavigatorLocator.Set(ctxA);
        await ctxA.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)ctxA.Breadcrumb[0].NavigableInstance!;

        // Start UntilReturns — Home becomes Pinned after OnNavigation confirms
        var resultTask = ctxA.NavigateTo(new PickerRoute()).UntilReturns<PickerResult>();

        // Switch away — Home is Pinned, OnSuspend must NOT be called
        await _host.SwitchContextAsync(ctxB.Id);

        Assert.DoesNotContain("OnSuspend", homeVm.Calls);

        // Cleanup
        var pickerVm = (PickerViewModel)ctxA.Breadcrumb.Last().NavigableInstance!;
        pickerVm.Returns(new PickerResult("x"));
        await resultTask;
    }

    [Fact]
    public async Task SwitchContextAsync_PinnedTopEntry_RestoresViewOnReturn()
    {
        var navPaneA = new FakeNavigation();
        var ctxA = _host.CreateContext(navPaneA);
        var ctxB = _host.CreateContext(new FakeNavigation());

        NavigatorLocator.Set(ctxA);
        await ctxA.NavigateTo(new HomeRoute());

        var resultTask = ctxA.NavigateTo(new PickerRoute()).UntilReturns<PickerResult>();

        // Switch away and back
        await _host.SwitchContextAsync(ctxB.Id);
        var viewCountBeforeReturn = navPaneA.NavigatedViews.Count;

        await _host.SwitchContextAsync(ctxA.Id);

        // View restored on return even though Pinned
        Assert.True(navPaneA.NavigatedViews.Count > viewCountBeforeReturn);

        // Cleanup
        var pickerVm = (PickerViewModel)ctxA.Breadcrumb.Last().NavigableInstance!;
        pickerVm.Returns(new PickerResult("x"));
        await resultTask;
    }

    [Fact]
    public async Task SwitchContextAsync_SameContext_DoesNothing()
    {
        var ctxA = _host.CreateContext(new FakeNavigation());
        NavigatorLocator.Set(ctxA);
        await ctxA.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)ctxA.Breadcrumb[0].NavigableInstance!;

        await _host.SwitchContextAsync(ctxA.Id);

        // Already active — no suspend/resume
        Assert.DoesNotContain("OnSuspend", homeVm.Calls);
        Assert.DoesNotContain("OnResume", homeVm.Calls);
    }

    [Fact]
    public async Task SwitchContextAsync_NonExistentId_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _host.SwitchContextAsync(Guid.NewGuid()));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CloseContext
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CloseContext_RemovesFromContexts()
    {
        var ctxA = _host.CreateContext(new FakeNavigation());
        var ctxB = _host.CreateContext(new FakeNavigation());

        _host.CloseContext(ctxA.Id);

        Assert.Single(_host.Contexts);
        Assert.DoesNotContain(ctxA, _host.Contexts);
    }

    [Fact]
    public async Task CloseContext_DisposesNavigator()
    {
        var ctxA = _host.CreateContext(new FakeNavigation());
        NavigatorLocator.Set(ctxA);
        await ctxA.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)ctxA.Breadcrumb[0].NavigableInstance!;

        _host.CloseContext(ctxA.Id);

        Assert.Contains("Dispose", homeVm.Calls);
    }

    [Fact]
    public void CloseContext_ActiveContext_SetsActiveToNull()
    {
        var ctxA = _host.CreateContext(new FakeNavigation());

        _host.CloseContext(ctxA.Id);

        Assert.Null(_host.ActiveContext);
    }

    [Fact]
    public void CloseContext_NonActiveContext_ActiveContextUnchanged()
    {
        var ctxA = _host.CreateContext(new FakeNavigation());
        var ctxB = _host.CreateContext(new FakeNavigation());

        _host.CloseContext(ctxB.Id);

        Assert.Same(ctxA, _host.ActiveContext);
    }

    [Fact]
    public void CloseContext_NonExistentId_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => _host.CloseContext(Guid.NewGuid()));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Dispose
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_DisposesAllContexts()
    {
        _host.CreateContext(new FakeNavigation());
        _host.CreateContext(new FakeNavigation());

        _host.Dispose();

        Assert.Empty(_host.Contexts);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        _host.Dispose();
        var ex = Record.Exception(() => _host.Dispose());
        Assert.Null(ex);
    }
}

// Extra ViewModel needed for host tests
internal class SearchViewModel : INavigable
{
    public Task OnNavigation(NavigationContext ctx, CancellationToken ct) => Task.CompletedTask;
    public Task OnResume(CancellationToken ct) => Task.CompletedTask;
    public Task OnSuspend(CancellationToken ct) => Task.CompletedTask;
    public void Dispose() { }
}