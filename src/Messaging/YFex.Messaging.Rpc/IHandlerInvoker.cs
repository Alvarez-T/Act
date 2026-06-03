namespace YFex.Messaging.Rpc;

/// <summary>
/// Decouples Fusion server-side contract implementations from the underlying handler runtime
/// (Wolverine, DI-direct, etc.). Swap the registered implementation to change the backend.
/// </summary>
public interface IHandlerInvoker
{
    /// <summary>Invokes a handler for <paramref name="message"/> that returns <typeparamref name="T"/>.</summary>
    Task<T> InvokeAsync<T>(object message, CancellationToken ct);

    /// <summary>Invokes a command handler that returns no result.</summary>
    Task InvokeAsync(object message, CancellationToken ct);
}
