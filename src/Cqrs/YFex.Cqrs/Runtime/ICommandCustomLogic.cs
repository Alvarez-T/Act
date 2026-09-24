namespace YFex.Cqrs.Runtime;

/// <summary>
/// Optional dispatch-time seam for scope-specific command overrides (YFex.CustomLogic). Implemented
/// by the <c>YFex.CustomLogic.Cqrs</c> bridge and resolved lazily by <c>LocalDispatcher</c> — when no
/// implementation is registered the dispatcher behaves exactly as before (mirrors how
/// <see cref="ICommandExecutedObserver"/> plugs in the compensation journal). Kept in
/// <c>YFex.Cqrs</c> so neither Cqrs nor Messaging.Rpc references the CustomLogic engine.
/// </summary>
public interface ICommandCustomLogic
{
    /// <summary>
    /// Consults the most-specific registered override for the current scope. Returns
    /// <see cref="CommandOverrideResult.Handled"/> to short-circuit the default handler with a result,
    /// or <see cref="CommandOverrideResult.Fallthrough"/> to run the default handler.
    /// </summary>
    ValueTask<CommandOverrideResult> TryHandleAsync(Type commandType, object command, CancellationToken ct);
}

/// <summary>Outcome of an override consult: handled (with a boxed result) or fall through.</summary>
public readonly struct CommandOverrideResult
{
    /// <summary>True when an override produced the result; false to run the default handler.</summary>
    public bool IsHandled { get; }

    /// <summary>The boxed command result when handled (null for void commands); otherwise null.</summary>
    public object? Result { get; }

    private CommandOverrideResult(bool handled, object? result)
    {
        IsHandled = handled;
        Result = result;
    }

    /// <summary>An override handled the command, producing <paramref name="result"/>.</summary>
    public static CommandOverrideResult Handled(object? result) => new(true, result);

    /// <summary>No override applied — run the default handler.</summary>
    public static CommandOverrideResult Fallthrough => default;
}
