using System.Security.Claims;
using YFex.Validation;

namespace YFex.Cqrs.Runtime;

/// <summary>
/// Pre-compiled, immutable execution plan for one command type.
/// Stored in <see cref="CompiledMessagingRegistry.Commands"/> after Build().
///
/// Hot-path contract:
/// • Every delegate is a pre-compiled composite (no LINQ, no list enumeration).
/// • All fields are direct — no property-dispatch overhead on a sealed record.
/// • Zero per-call allocations when all validators complete synchronously.
/// Authorization is not here: every dispatcher asks <c>IMessageAuthorization</c> (authorization-consolidation 04 §2).
/// </summary>
public sealed record CommandPolicy(
    /// <summary>
    /// Composite of all registered validators, merged at Build time.
    /// Null if no validators. Internally uses sync-fast-path / async-fallback to
    /// avoid state-machine allocations when validators complete synchronously.
    /// </summary>
    /// <summary>Stage 5 (context rules); stage 1 runs through ValidationSlot&lt;T&gt; without a policy.</summary>
    MessageValidator? Validation,

    Func<object, string>? IdempotencyKey,
    ConflictPolicy        Conflict,
    Type?                 ConflictResolverType,
    RetryPolicy?          Retry,
    TimeSpan?             Timeout,

    /// <summary>Inline offline handler — takes priority over <see cref="OnOfflineHandlerType"/>.</summary>
    Func<object, CancellationToken, ValueTask>? OnOfflineHandler,
    /// <summary>
    /// Type-reference offline handler — resolved from DI at first dispatch (Plan 3 caches this).
    /// </summary>
    Type? OnOfflineHandlerType,

    /// <summary>Single pre-compiled optimistic update plan. Null if none configured.</summary>
    OptimisticPolicy? Optimistic,

    /// <summary>
    /// Flat array of pre-resolved invalidation targets with compiled match predicates.
    /// Null when no invalidation rules are declared.
    /// Each entry was expanded from group/union tokens at Build time.
    /// </summary>
    InvalidationTarget[]? InvalidationTargets,

    /// <summary>
    /// Single pre-compiled reverse (compensation) plan. Null when the command is not reversible.
    /// Read by YFex.Cqrs.Compensation to journal + dispatch the reverse command.
    /// </summary>
    ReversePolicy? Reverse = null,

    /// <summary>
    /// Strongly-typed resolve + invoke for the type-ref offline handler (<see cref="OnOfflineHandlerType"/>),
    /// captured at registration where the handler/command[/result] are compile-time generic parameters — so
    /// the offline dispatch path needs no reflection. Null for the inline form or when no offline handler is
    /// declared. When set, resolves the handler from the supplied provider and calls its typed <c>HandleAsync</c>.
    /// </summary>
    Func<IServiceProvider, object, CancellationToken, ValueTask>? OnOfflineHandlerInvoke = null,

    /// <summary>
    /// Unified resilience spec composed from the command builder's policy verbs (<c>.Retry</c>, <c>.Timeout</c>,
    /// <c>.UseResource</c>, <c>.Fallback</c>, <c>.Hedging</c>) at Build time (design §1 L2, §14.6). When an
    /// <c>IResiliencePipelineFactory</c> is registered, the dispatcher executes the command's invoke through the
    /// pipeline for this spec; otherwise the legacy <see cref="Timeout"/> path applies. Null when no verbs are set.
    /// </summary>
    global::YFex.Resilience.ResilienceSpec? Resilience = null,

    /// <summary>Stages 3–4 (<c>.UseSituation().Enforce()</c>): gather the situation, evaluate the rule set. Null when not configured.</summary>
    RulesStage? Rules = null,

    /// <summary>Typed settings of other libraries (e.g. YFex.Offline's <c>.Offline(...)</c>). Null when none are attached.</summary>
    MessageFeatures? Features = null);

/// <summary>
/// Pre-compiled reverse (compensation) plan for one command type. Either a typed map to a
/// registered reverse command, or a custom handler type resolved from DI.
/// </summary>
public sealed record ReversePolicy(
    /// <summary>The reverse command's CLR type. Non-null for the map form; null for the handler form.</summary>
    Type? ReverseCommandType,
    /// <summary>Compiled map: (erased original command, erased result-or-null) → erased reverse command.
    /// For no-result / command-only maps the second argument is ignored. Null for the handler form.</summary>
    Func<object, object?, object>? Map,
    /// <summary>Custom reverse handler type (IReverseHandler&lt;TCommand&gt; / &lt;TCommand,TResult&gt;),
    /// resolved from DI at compensation time. Null for the map form.</summary>
    Type? HandlerType,
    /// <summary>Strongly-typed dispatch of the materialized reverse command through the
    /// <see cref="IDispatcher"/>. Captured at registration where <c>TReverse</c> is a compile-time
    /// generic parameter, so the undo path needs no <c>MakeGenericMethod</c>. Non-null for the map form.</summary>
    Func<IDispatcher, object, CancellationToken, ValueTask>? DispatchReverse = null,
    /// <summary>Strongly-typed invocation of the custom <c>IReverseHandler</c> resolved from the supplied
    /// <see cref="IServiceProvider"/>. Captured at registration where the handler/command/result are
    /// compile-time generic parameters, so the undo path needs no reflection. Non-null for the handler form.</summary>
    Func<IServiceProvider, object, object?, CancellationToken, ValueTask>? InvokeHandler = null,
    /// <summary>Declared result type (<c>typeof(TResult)</c>) of the reversed command; null for void
    /// commands. Lets the compensation journal deserialize the stored result without <c>Type.GetType</c>.</summary>
    Type? ResultType = null);

/// <summary>
/// A value-type reverse (compensation) declaration usable <b>outside</b> <see cref="CommandPolicy"/>: by a
/// saga step (<c>.CompensateWith&lt;TReverse&gt;</c>) or a standalone reverse registry
/// (<c>AddYFexReversals</c>). Sits beside <see cref="ReversePolicy"/> (design §11/§14, decision 1) so the
/// same shape is reused by command config, the registry, and saga steps.
///
/// Both delegates are captured where the reverse command type is a compile-time generic parameter, so the
/// reverse path stays reflection-free and AoT-clean (no <c>MakeGenericMethod</c>, no <c>Activator</c>).
/// </summary>
public readonly record struct ReverseSpec(
    /// <summary>The reverse command's CLR type (the map form).</summary>
    Type ReverseCommandType,
    /// <summary>Compiled map: (erased original command, erased result-or-null) → erased reverse command.
    /// When null, the supplied command instance is dispatched as-is.</summary>
    Func<object, object?, object>? Map,
    /// <summary>Strongly-typed dispatch of the materialized reverse command through the
    /// <see cref="IDispatcher"/>. Captured where <c>TReverse</c> is a compile-time generic parameter.</summary>
    Func<IDispatcher, object, CancellationToken, ValueTask>? Dispatch,
    /// <summary>Custom reverse handler type, resolved from DI when the map form is not used. Optional.</summary>
    Type? HandlerType = null);

/// <summary>Pre-compiled optimistic update plan for one (command, query) pairing.</summary>
public sealed record OptimisticPolicy(
    Type TargetQueryType,
    /// <summary>Compiled: (erased-TQuery, erased-TCommand) → bool.</summary>
    Func<object, object, bool> Match,
    /// <summary>Compiled: (erased-TResult, erased-TCommand) → erased-TResult.</summary>
    Func<object, object, object> Apply);

/// <summary>
/// One resolved invalidation target with its compiled match predicate.
/// Stored in a flat array — hot path iterates with a plain <c>for</c> loop.
/// </summary>
public readonly struct InvalidationTarget
{
    public readonly Type QueryType;
    /// <summary>Null = wildcard: invalidate all cached variants of this query type.</summary>
    public readonly Func<object, object, bool>? Match;

    /// <summary>
    /// Extracts the entity key from the dispatched command for precise, tag-based invalidation:
    /// only the query variant tagged with the same key is dropped. Null = coarse (drop all variants).
    /// </summary>
    public readonly Func<object, string>? TagKey;

    public InvalidationTarget(Type queryType, Func<object, object, bool>? match, Func<object, string>? tagKey = null)
    { QueryType = queryType; Match = match; TagKey = tagKey; }
}
