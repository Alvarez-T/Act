using YFex.Cqrs;

namespace YFex.Messaging.Rpc;

/// <summary>
/// <see cref="IHandlerInvoker"/> that resolves <see cref="IQueryHandler{TQuery,TResult}"/> /
/// <see cref="ICommandHandler{TCommand,TResult}"/> / <see cref="ICommandHandler{TCommand}"/>
/// from the DI container via a source-generated <see cref="LocalHandlerInvokerRegistry"/> — no
/// reflection (<c>MakeGenericType</c> / <c>MethodInfo.Invoke</c>), fully AOT / trim safe.
/// Intended for tests and in-process scenarios without Wolverine.
/// <para>
/// Populate the registry at startup via the generated
/// <c>YFexLocalHandlerRegistrations.AddYFexLocalHandlers()</c> (or build it manually with
/// <see cref="LocalHandlerInvokerRegistryBuilder"/>). A message type with no registered invoker throws.
/// </para>
/// </summary>
public sealed class LocalHandlerInvoker : IHandlerInvoker
{
    private readonly IServiceProvider _sp;
    private readonly LocalHandlerInvokerRegistry _registry;

    public LocalHandlerInvoker(IServiceProvider sp, LocalHandlerInvokerRegistry? registry = null)
    {
        _sp = sp;
        _registry = registry ?? LocalHandlerInvokerRegistry.Empty;
    }

    public Task<T> InvokeAsync<T>(object message, CancellationToken ct)
    {
        var msgType = message.GetType();
        if (!_registry.TryGetResultInvoker(msgType, out var invoker))
            throw new InvalidOperationException(
                $"No handler-invoke registered for {msgType.Name} → {typeof(T).Name}. " +
                "Ensure the generated AddYFexLocalHandlers() is called at startup so the message type is " +
                "included in the LocalHandlerInvokerRegistry.");

        return invoker(_sp, message, ct).ContinueWith(
            static t => (T)t.GetAwaiter().GetResult()!,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public Task InvokeAsync(object message, CancellationToken ct)
    {
        var msgType = message.GetType();
        if (!_registry.TryGetVoidInvoker(msgType, out var invoker))
            throw new InvalidOperationException(
                $"No void handler-invoke registered for {msgType.Name}. " +
                "Ensure the generated AddYFexLocalHandlers() is called at startup so the command type is " +
                "included in the LocalHandlerInvokerRegistry.");

        return invoker(_sp, message, ct);
    }
}
