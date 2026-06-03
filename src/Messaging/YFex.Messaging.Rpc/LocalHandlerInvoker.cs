using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;

namespace YFex.Messaging.Rpc;

/// <summary>
/// <see cref="IHandlerInvoker"/> that resolves <see cref="IQueryHandler{TQuery,TResult}"/> /
/// <see cref="ICommandHandler{TCommand,TResult}"/> / <see cref="ICommandHandler{TCommand}"/>
/// from the DI container. Dispatch delegates are compiled once per message type and cached.
/// Intended for tests and in-process scenarios without Wolverine.
/// </summary>
public sealed class LocalHandlerInvoker : IHandlerInvoker
{
    private readonly IServiceProvider _sp;

    // (msgType, resultType) → compiled async invoker returning boxed result
    private readonly ConcurrentDictionary<(Type Msg, Type Result), Func<object, CancellationToken, Task<object?>>>
        _resultCache = new();

    // msgType → compiled void invoker
    private readonly ConcurrentDictionary<Type, Func<object, CancellationToken, Task>>
        _voidCache = new();

    public LocalHandlerInvoker(IServiceProvider sp) => _sp = sp;

    public Task<T> InvokeAsync<T>(object message, CancellationToken ct)
    {
        var key = (message.GetType(), typeof(T));
        var invoker = _resultCache.GetOrAdd(key, k => BuildResultInvoker(k.Msg, k.Result));

        return invoker(message, ct).ContinueWith(
            static t => (T)t.GetAwaiter().GetResult()!,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public Task InvokeAsync(object message, CancellationToken ct)
    {
        var msgType = message.GetType();
        var invoker = _voidCache.GetOrAdd(msgType, BuildVoidInvoker);
        return invoker(message, ct);
    }

    // ── Delegate factories (called once per type, cached in ConcurrentDictionary) ─

    private Func<object, CancellationToken, Task<object?>> BuildResultInvoker(Type msgType, Type resultType)
    {
        // Try ICommandHandler<TMsg, TResult> first (if TMsg is a command)
        Type? cmdHandlerType = null;
        if (typeof(ICommand).IsAssignableFrom(msgType))
        {
            try { cmdHandlerType = typeof(ICommandHandler<,>).MakeGenericType(msgType, resultType); }
            catch { /* msgType doesn't satisfy constraints → not a command handler */ }
        }

        // IQueryHandler<TMsg, TResult>
        Type? qHandlerType = null;
        try { qHandlerType = typeof(IQueryHandler<,>).MakeGenericType(msgType, resultType); }
        catch { /* msgType doesn't satisfy constraints */ }

        // Reflect once to get HandleAsync MethodInfo (null if the type couldn't be built)
        MethodInfo? cmdMethod = cmdHandlerType?.GetMethod("HandleAsync");
        MethodInfo? qMethod = qHandlerType?.GetMethod("HandleAsync");

        return async (msg, ct) =>
        {
            if (cmdHandlerType is not null && cmdMethod is not null)
            {
                var cmdHandler = _sp.GetService(cmdHandlerType);
                if (cmdHandler is not null)
                    return await AwaitTypedTask(cmdMethod.Invoke(cmdHandler, [msg, ct])!).ConfigureAwait(false);
            }
            if (qHandlerType is null || qMethod is null)
                throw new InvalidOperationException(
                    $"No handler registered for {msgType.Name} → {resultType.Name}.");
            var qHandler = _sp.GetRequiredService(qHandlerType);
            return await AwaitTypedTask(qMethod.Invoke(qHandler, [msg, ct])!).ConfigureAwait(false);
        };
    }

    // Task<TResult> → Task<object?> without requiring Task covariance.
    private static async Task<object?> AwaitTypedTask(object taskObj)
    {
        var task = (Task)taskObj;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private Func<object, CancellationToken, Task> BuildVoidInvoker(Type msgType)
    {
        // ICommandHandler<TMsg> — bypass compile-time constraint via MakeGenericType
        Type? handlerType = null;
        try { handlerType = typeof(ICommandHandler<>).MakeGenericType(msgType); }
        catch { /* msgType doesn't satisfy ICommand constraint */ }

        MethodInfo? method = handlerType?.GetMethod("HandleAsync");

        return (msg, ct) =>
        {
            if (handlerType is null || method is null)
                throw new InvalidOperationException(
                    $"No void command handler registered for {msgType.Name}.");
            var handler = _sp.GetRequiredService(handlerType);
            return (Task)method.Invoke(handler, [msg, ct])!;
        };
    }
}
