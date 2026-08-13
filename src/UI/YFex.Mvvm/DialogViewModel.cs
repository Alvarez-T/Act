using YFex.UI.Public;

namespace YFex.Mvvm;

/// <summary>
/// Base for a ViewModel hosted in a result-less dialog. Implements <see cref="IDialogAware"/>,
/// captures the handle when the dialog opens, and exposes <see cref="Close"/> so subclasses
/// never touch the handle plumbing.
/// </summary>
public abstract class DialogViewModel : ViewModel, IDialogAware
{
    /// <summary>The handle to this dialog, available from <see cref="OnOpened"/> onward.</summary>
    protected IDialogHandle? Handle { get; private set; }

    void IDialogAware.OnDialogOpened(IDialogHandle handle)
    {
        Handle = handle;
        OnOpened();
    }

    /// <summary>Runs once the dialog is on screen. Override for open-time logic (focus, load, ...).</summary>
    protected virtual void OnOpened() { }

    /// <summary>Closes the dialog.</summary>
    protected void Close() => Handle?.Close();
}

/// <summary>
/// Base for a ViewModel hosted in a result-producing dialog. Implements
/// <see cref="IDialogAware{TResult}"/> and exposes <see cref="Close(TResult)"/> /
/// <see cref="Cancel"/> to complete the awaiting caller.
/// </summary>
public abstract class DialogViewModel<TResult> : ViewModel, IDialogAware<TResult>
{
    /// <summary>The handle to this dialog, available from <see cref="OnOpened"/> onward.</summary>
    protected IDialogHandle<TResult>? Handle { get; private set; }

    void IDialogAware<TResult>.OnDialogOpened(IDialogHandle<TResult> handle)
    {
        Handle = handle;
        OnOpened();
    }

    /// <summary>Runs once the dialog is on screen.</summary>
    protected virtual void OnOpened() { }

    /// <summary>Closes the dialog and returns <paramref name="result"/> to the caller.</summary>
    protected void Close(TResult result) => Handle?.Close(result);

    /// <summary>Closes the dialog with the default result (e.g. cancel / dismiss).</summary>
    protected void Cancel() => Handle?.Close();
}

/// <summary>
/// Base for a dialog ViewModel that receives a typed <typeparamref name="TParameter"/> and
/// produces a <typeparamref name="TResult"/> — the dialog analogue of a parameterised, result-
/// producing navigable. The host calls <see cref="OnOpening"/> (seed state; optionally
/// <c>context.Deny(...)</c>) before showing, then injects the handle. Complete the awaiting
/// caller with <see cref="Returns"/> / <see cref="Cancel"/>.
/// </summary>
public abstract class DialogViewModel<TParameter, TResult>
    : ViewModel, IDialogOpening<TParameter>, IDialogAware<TResult>
{
    /// <summary>The handle to this dialog, available from <see cref="OnOpened"/> onward.</summary>
    protected IDialogHandle<TResult>? Handle { get; private set; }

    /// <summary>
    /// Called before the dialog is shown. Seed state from <paramref name="parameter"/>;
    /// call <c>context.Deny(...)</c> to refuse (the caller then receives the default result).
    /// </summary>
    public abstract Task OnOpening(TParameter parameter, DialogContext context, CancellationToken ct = default);

    void IDialogAware<TResult>.OnDialogOpened(IDialogHandle<TResult> handle)
    {
        Handle = handle;
        OnOpened();
    }

    /// <summary>Runs once the dialog is on screen (after <see cref="OnOpening"/> and the show).</summary>
    protected virtual void OnOpened() { }

    /// <summary>Closes the dialog and returns <paramref name="result"/> to the caller.</summary>
    protected void Returns(TResult result) => Handle?.Close(result);

    /// <summary>Closes the dialog with the default result (cancel).</summary>
    protected void Cancel() => Handle?.Close();
}