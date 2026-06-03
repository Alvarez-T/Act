using System.Security.Claims;

namespace YFex.Cqrs.Runtime;

/// <summary>
/// Pre-compiled, immutable execution plan for one command type.
/// Stored in <see cref="CompiledMessagingRegistry.Commands"/> after Build().
///
/// Hot-path contract:
/// • Every delegate is a pre-compiled composite (no LINQ, no list enumeration).
/// • All fields are direct — no property-dispatch overhead on a sealed record.
/// • Zero per-call allocations when all validators/authorizers complete synchronously.
/// </summary>
public sealed record CommandPolicy(
    /// <summary>
    /// Composite of all registered validators, merged at Build time.
    /// Null if no validators. Internally uses sync-fast-path / async-fallback to
    /// avoid state-machine allocations when validators complete synchronously.
    /// </summary>
    Func<object, CancellationToken, ValueTask<ValidationResult>>? Validate,

    /// <summary>
    /// Composite AND-predicate of all authorizers, merged at Build time.
    /// Null if no authorization rules.
    /// </summary>
    Func<ClaimsPrincipal, object, bool>? Authorize,

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
    InvalidationTarget[]? InvalidationTargets);

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

    public InvalidationTarget(Type queryType, Func<object, object, bool>? match)
    { QueryType = queryType; Match = match; }
}
