using P = YFex.UI.Public;

namespace YFex.Mvvm;

// The notification contract types are qualified through the `P` alias because this project also
// sees an older duplicate copy of them transitively (YFex.NavigatR -> YFex.UI.Abstractions).
// Without the alias, `INotificationHandle` binds to the wrong assembly and the explicit interface
// implementations below fail to match. (Dialogs happen to resolve cleanly, hence DialogViewModel
// stays unqualified.) The real fix is migrating INavigation off YFex.UI.Abstractions.

/// <summary>
/// Base for a ViewModel hosted in a result-less notification. Implements
/// <see cref="P.INotificationAware"/> and exposes <see cref="Dismiss"/>.
/// </summary>
public abstract class NotificationViewModel : ViewModel, P.INotificationAware
{
    /// <summary>The handle to this notification, available from <see cref="OnOpened"/> onward.</summary>
    protected P.INotificationHandle? Handle { get; private set; }

    void P.INotificationAware.OnNotificationOpened(P.INotificationHandle handle)
    {
        Handle = handle;
        OnOpened();
    }

    /// <summary>Runs once the notification is on screen.</summary>
    protected virtual void OnOpened() { }

    /// <summary>Dismisses the notification.</summary>
    protected void Dismiss() => Handle?.Dismiss();
}

/// <summary>
/// Base for a ViewModel hosted in a result-producing notification. Implements
/// <see cref="P.INotificationAware{TResult}"/> and exposes <see cref="Dismiss(TResult)"/> /
/// <see cref="Dismiss"/>.
/// </summary>
public abstract class NotificationViewModel<TResult> : ViewModel, P.INotificationAware<TResult>
{
    /// <summary>The handle to this notification, available from <see cref="OnOpened"/> onward.</summary>
    protected P.INotificationHandle<TResult>? Handle { get; private set; }

    void P.INotificationAware<TResult>.OnNotificationOpened(P.INotificationHandle<TResult> handle)
    {
        Handle = handle;
        OnOpened();
    }

    /// <summary>Runs once the notification is on screen.</summary>
    protected virtual void OnOpened() { }

    /// <summary>Dismisses the notification and returns <paramref name="result"/> to the caller.</summary>
    protected void Dismiss(TResult result) => Handle?.Dismiss(result);

    /// <summary>Dismisses the notification with the default result.</summary>
    protected void Dismiss() => Handle?.Dismiss();
}

/// <summary>
/// Base for a notification ViewModel that receives a typed <typeparamref name="TParameter"/> and
/// produces a <typeparamref name="TResult"/>. The host calls <see cref="OnOpening"/> (seed state;
/// optionally <c>context.Deny(...)</c>) before the card is shown, then injects the handle.
/// Complete the awaiting caller with <see cref="Returns"/> / <see cref="Cancel"/>.
/// </summary>
public abstract class NotificationViewModel<TParameter, TResult>
    : ViewModel, P.INotificationOpening<TParameter>, P.INotificationAware<TResult>
{
    /// <summary>The handle to this notification, available from <see cref="OnOpened"/> onward.</summary>
    protected P.INotificationHandle<TResult>? Handle { get; private set; }

    /// <summary>
    /// Called before the card is shown. Seed state from <paramref name="parameter"/>;
    /// call <c>context.Deny(...)</c> to refuse.
    /// </summary>
    public abstract void OnOpening(TParameter parameter, P.NotificationContext context);

    void P.INotificationAware<TResult>.OnNotificationOpened(P.INotificationHandle<TResult> handle)
    {
        Handle = handle;
        OnOpened();
    }

    /// <summary>Runs once the notification is on screen.</summary>
    protected virtual void OnOpened() { }

    /// <summary>Dismisses the notification and returns <paramref name="result"/> to the caller.</summary>
    protected void Returns(TResult result) => Handle?.Dismiss(result);

    /// <summary>Dismisses the notification with the default result (cancel).</summary>
    protected void Cancel() => Handle?.Dismiss();
}
