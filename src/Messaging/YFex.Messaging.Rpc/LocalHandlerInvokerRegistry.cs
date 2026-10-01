using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;
using YFex.Cqrs.Runtime;

using YFex.Validation;

namespace YFex.Messaging.Rpc;

/// <summary>Resolves the handler for a result-bearing message and invokes it, returning a boxed result.</summary>
public delegate Task<object?> HandlerResultInvoke(IServiceProvider sp, object message, CancellationToken ct);

/// <summary>Resolves the handler for a void command and invokes it.</summary>
public delegate Task HandlerVoidInvoke(IServiceProvider sp, object message, CancellationToken ct);

/// <summary>
/// Compile-time replacement for the old reflection-based handler resolution in
/// <see cref="LocalHandlerInvoker"/> (<c>MakeGenericType</c> + <c>MethodInfo.Invoke</c>). Maps a message
/// type to a source-generated, strongly-typed resolve+invoke delegate — no reflection, no dynamic code,
/// fully AOT / trim safe.
/// <para>
/// Built by the source-generated <c>YFexLocalHandlerRegistrations.AddYFexLocalHandlers()</c> (one entry
/// per command / query type discovered in the consuming assembly), or manually via
/// <see cref="LocalHandlerInvokerRegistryBuilder"/>.
/// </para>
/// </summary>
public sealed class LocalHandlerInvokerRegistry
{
    private readonly FrozenDictionary<Type, HandlerResultInvoke> _resultInvokers;
    private readonly FrozenDictionary<Type, HandlerVoidInvoke> _voidInvokers;

    /// <summary>An empty registry (no handlers registered). Used as the DI fallback.</summary>
    public static LocalHandlerInvokerRegistry Empty { get; } = new(
        new Dictionary<Type, HandlerResultInvoke>(0),
        new Dictionary<Type, HandlerVoidInvoke>(0));

    internal LocalHandlerInvokerRegistry(
        IReadOnlyDictionary<Type, HandlerResultInvoke> resultInvokers,
        IReadOnlyDictionary<Type, HandlerVoidInvoke> voidInvokers)
    {
        _resultInvokers = resultInvokers.ToFrozenDictionary();
        _voidInvokers = voidInvokers.ToFrozenDictionary();
    }

    /// <summary>Gets the typed result-invoker for a command/query type.</summary>
    public bool TryGetResultInvoker(Type messageType, [NotNullWhen(true)] out HandlerResultInvoke? invoker)
        => _resultInvokers.TryGetValue(messageType, out invoker);

    /// <summary>Gets the typed void-invoker for a void command type.</summary>
    public bool TryGetVoidInvoker(Type messageType, [NotNullWhen(true)] out HandlerVoidInvoke? invoker)
        => _voidInvokers.TryGetValue(messageType, out invoker);

    /// <summary>Total number of registered invokers (result + void).</summary>
    public int Count => _resultInvokers.Count + _voidInvokers.Count;
}

/// <summary>
/// Accumulates typed handler invokers, then freezes them into a <see cref="LocalHandlerInvokerRegistry"/>.
/// Every delegate resolves the closed generic handler via <c>typeof</c> (no <c>MakeGenericType</c>) and
/// calls <c>HandleAsync</c> directly (no <c>MethodInfo.Invoke</c>), so the whole path is AOT / trim safe.
/// </summary>
public sealed class LocalHandlerInvokerRegistryBuilder
{
    private readonly Dictionary<Type, HandlerResultInvoke> _resultInvokers = new();
    private readonly Dictionary<Type, HandlerVoidInvoke> _voidInvokers = new();

    /// <summary>Registers a result-bearing command (<see cref="ICommandHandler{TCommand,TResult}"/>).</summary>
    public LocalHandlerInvokerRegistryBuilder AddCommand<TCommand, TResult>()
        where TCommand : class, ICommand<TResult>
    {
        _resultInvokers[typeof(TCommand)] = static async (sp, msg, ct) =>
        {
            var command = (TCommand)msg;
            // Rules-enforced command with a registered IRuledCommandHandler (Phase 4): it receives the server decision.
            if (DecisionContext.For(command) is { } decision
                && decision.Stage.TryInvokeRuled<TCommand, TResult>(sp, command, decision.Decision, ct) is { } ruled)
                return await ruled.ConfigureAwait(false);
            // Opt-in validated handler (D24): the dispatch pipeline validated the command before invoking.
            if (sp.GetService<IValidatedCommandHandler<TCommand, TResult>>() is { } validated)
                return await validated.HandleAsync(Validator.AssumeValid(command), ct).ConfigureAwait(false);
            return await sp.GetRequiredService<ICommandHandler<TCommand, TResult>>()
                    .HandleAsync(command, ct).ConfigureAwait(false);
        };
        return this;
    }

    /// <summary>Registers a void command (<see cref="ICommandHandler{TCommand}"/>).</summary>
    public LocalHandlerInvokerRegistryBuilder AddCommand<TCommand>()
        where TCommand : class, ICommand
    {
        _voidInvokers[typeof(TCommand)] = static (sp, msg, ct) =>
            DecisionContext.For(msg) is { } decision && decision.Stage.TryInvokeRuled(sp, (TCommand)msg, decision.Decision, ct) is { } ruled
                ? ruled
                : sp.GetService<IValidatedCommandHandler<TCommand>>() is { } validated
                ? validated.HandleAsync(Validator.AssumeValid((TCommand)msg), ct)
                : sp.GetRequiredService<ICommandHandler<TCommand>>().HandleAsync((TCommand)msg, ct);
        return this;
    }

    /// <summary>Registers a query (<see cref="IQueryHandler{TQuery,TResult}"/>).</summary>
    public LocalHandlerInvokerRegistryBuilder AddQuery<TQuery, TResult>()
        where TQuery : class, IQuery<TResult>
    {
        _resultInvokers[typeof(TQuery)] = static async (sp, msg, ct) =>
            await sp.GetRequiredService<IQueryHandler<TQuery, TResult>>()
                    .HandleAsync((TQuery)msg, ct).ConfigureAwait(false);
        return this;
    }

    /// <summary>Freezes the accumulated invokers into an immutable registry.</summary>
    public LocalHandlerInvokerRegistry Build() => new(_resultInvokers, _voidInvokers);
}
