using YFex.NavigatR;
using YFex.UI.Abstractions;

namespace YFex.Mvvm;

/// <summary>
/// Base class for all navigable page ViewModels.
/// Provides the full lifecycle contract required by <see cref="INavigable"/>:
/// navigation, suspend/resume, and disposal — with generated-code cascade hooks
/// at each transition point.
/// </summary>
/// <remarks>
/// Lifecycle order for a single page:
/// <list type="number">
///   <item>DI creates the instance, sets services via constructor.</item>
///   <item><see cref="INavigable.OnNavigation"/> — route parameters arrive; set initial state here.</item>
///   <item><see cref="YFex.State.StateObject.Activate"/> — called by the Navigator after navigation;
///         triggers <see cref="YFex.State.StateObject.OnActivateCascading"/> which wires
///         all <c>[Subscribe&lt;T&gt;]</c> adapters and creates <c>[Live]</c> states.</item>
///   <item><see cref="INavigable.OnSuspend"/> — page pushed to back-stack;
///         calls <see cref="OnSuspendCascading"/> which pauses <c>[Live]</c> fetching
///         according to <c>SuspendBehavior</c>.</item>
///   <item><see cref="INavigable.OnResume"/> — page restored from back-stack;
///         calls <see cref="OnResumeCascading"/> which restarts <c>[Live]</c> fetching
///         and re-fetches stale data.</item>
///   <item><see cref="Dispose"/> — Navigator pops the page entirely;
///         calls <see cref="YFex.State.StateObject.Deactivate"/> which triggers
///         <see cref="YFex.State.StateObject.OnDeactivateCascading"/> to unsubscribe
///         all handlers and dispose all live states.</item>
/// </list>
/// </remarks>
public abstract partial class PageViewModel : ViewModel, INavigable, IDisposable
{
    /// <summary>The navigator used to push and pop pages from this ViewModel.</summary>
    public Navigator Navigator { get; }

    /// <summary>DI constructor — used by the service container.</summary>
    protected PageViewModel(
        Navigator        navigator,
        INotification    notification,
        IDialog          dialog,
        IToast           toast)
        : base(notification, dialog, toast)
    {
        Navigator = navigator;
    }

    /// <summary>
    /// Parameterless constructor for test subclasses that do not use DI.
    /// <see cref="Navigator"/> and service properties will be <see langword="null"/>.
    /// </summary>
#pragma warning disable CS8618
    protected PageViewModel() { }
#pragma warning restore CS8618

    // ── INavigable ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public abstract Task OnNavigation(NavigationContext context, CancellationToken ct = default);

    /// <inheritdoc/>
    /// <remarks>
    /// Calls <see cref="OnSuspendCascading"/> before returning so that generated
    /// <c>[Live]</c> code can pause fetching according to its <c>SuspendBehavior</c>.
    /// </remarks>
    public virtual Task OnSuspend(CancellationToken ct = default)
    {
        OnSuspendCascading();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Calls <see cref="OnResumeCascading"/> so that generated <c>[Live]</c> code
    /// can restart fetching and refresh stale data.
    /// </remarks>
    public virtual Task OnResume(CancellationToken ct = default)
    {
        OnResumeCascading();
        return Task.CompletedTask;
    }

    // ── Cascade hooks for generated code ──────────────────────────────────────

    /// <summary>
    /// Called by <see cref="OnSuspend"/> before returning.
    /// Generated <c>[Live]</c> overrides unsubscribe from <c>Updated</c> and mark
    /// dirty flags here, according to each property's <c>SuspendBehavior</c>.
    /// Always call <c>base.OnSuspendCascading()</c> first.
    /// </summary>
    protected virtual void OnSuspendCascading() { }

    /// <summary>
    /// Called by <see cref="OnResume"/> before returning.
    /// Generated <c>[Live]</c> overrides re-subscribe to <c>Updated</c> and trigger
    /// a re-fetch when the dirty flag is set.
    /// Always call <c>base.OnResumeCascading()</c> first.
    /// </summary>
    protected virtual void OnResumeCascading() { }

    // ── Disposal ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Disposes this ViewModel. Calls <see cref="YFex.State.StateObject.Deactivate"/>
    /// if the VM is still active, which triggers <see cref="YFex.State.StateObject.OnDeactivateCascading"/>
    /// to unsubscribe all <c>[Subscribe&lt;T&gt;]</c> handlers, dispose all
    /// <c>[Live]</c> states, and release <c>FusionStateBinding</c> instances.
    /// </summary>
    public void Dispose()
    {
        if (IsActive) Deactivate();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A navigable ViewModel that produces a typed result.</summary>
public abstract partial class PageViewModel<TResult> : PageViewModel, INavigable<TResult>
{
    /// <summary>DI constructor.</summary>
    protected PageViewModel(
        Navigator     navigator,
        INotification notification,
        IDialog       dialog,
        IToast        toast)
        : base(navigator, notification, dialog, toast) { }

    /// <summary>Parameterless constructor for test subclasses.</summary>
    protected PageViewModel() { }

    /// <inheritdoc/>
    public abstract Task<NavigationResult<TResult>> WaitForResultAsync();
}

// ── Convenience base classes ─────────────────────────────────────────────────

/// <summary>
/// Handles the lifecycle of a strongly-typed edit form.
/// <typeparamref name="TModel"/> is the <c>StateObject</c> holding the editable data.
/// </summary>
public abstract class EditorViewModel<TModel> : PageViewModel
{
    protected EditorViewModel(
        Navigator navigator, INotification notification, IDialog dialog, IToast toast)
        : base(navigator, notification, dialog, toast) { }

    protected EditorViewModel() { }
}

/// <summary>
/// Base for ViewModels that orchestrate master/detail layouts without owning a full page.
/// Does not implement <see cref="INavigable"/> — embed in a <see cref="PageViewModel"/>.
/// </summary>
public abstract class MasterDetailViewModel : ViewModel
{
    protected MasterDetailViewModel(INotification notification, IDialog dialog, IToast toast)
        : base(notification, dialog, toast) { }

    protected MasterDetailViewModel() { }
}

/// <summary>Base for list ViewModels embedded in a page.</summary>
public abstract class ListViewModel : ViewModel
{
    protected ListViewModel(INotification notification, IDialog dialog, IToast toast)
        : base(notification, dialog, toast) { }

    protected ListViewModel() { }
}

/// <summary>Base for selector/picker ViewModels embedded in a page.</summary>
public abstract class SelectorViewModel : ViewModel
{
    protected SelectorViewModel(INotification notification, IDialog dialog, IToast toast)
        : base(notification, dialog, toast) { }

    protected SelectorViewModel() { }
}
