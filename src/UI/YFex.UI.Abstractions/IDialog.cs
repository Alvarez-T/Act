namespace YFex.UI.Abstractions;

public interface IDialogOptions;

/// <summary>
/// Represents an open dialog with no result.
/// Use Close() to dismiss programmatically or UntilClosedAsync() to await user dismissal.
/// </summary>
public interface IDialogHandle
{
    void Close();
    Task UntilClosedAsync();
}

/// <summary>
/// Represents an open dialog that produces a result when closed.
/// </summary>
public interface IDialogHandle<TResult> : IDialogHandle
{
    new Task<TResult> UntilClosedAsync();
}

public interface IDialog
{
    /// <summary>Fire and forget. Returns a handle to close or await later.</summary>
    IDialogHandle Show<TView>(IDialogOptions? options = null);

    /// <summary>Fire and forget. Returns a handle to close or await the result later.</summary>
    IDialogHandle<TResult> Show<TView, TResult>(IDialogOptions? options = null);

    /// <summary>Shows the dialog and awaits dismissal.</summary>
    Task ShowAsync<TView>(IDialogOptions? options = null);

    /// <summary>Shows the dialog and awaits the result.</summary>
    Task<TResult> ShowAsync<TView, TResult>(IDialogOptions? options = null);
}