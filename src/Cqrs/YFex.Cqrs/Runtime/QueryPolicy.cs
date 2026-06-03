using System.Security.Claims;

namespace YFex.Cqrs.Runtime;

/// <summary>
/// Pre-compiled, immutable execution plan for one query type.
/// </summary>
public sealed record QueryPolicy(
    Func<object, CancellationToken, ValueTask<ValidationResult>>? Validate,
    Func<ClaimsPrincipal, object, bool>?                          Authorize,
    CachePolicy?                                                  Cache,
    CacheScope                                                    Scope,
    Func<ICacheScopeContext, string>?                             ScopeKey,
    TimeSpan?                                                     StaleAfter,
    TimeSpan?                                                     Timeout,
    /// <summary>
    /// Flat array of commands that invalidate this query, each with a compiled match predicate.
    /// Null when no InvalidatedBy rules exist.
    /// </summary>
    QueryInvalidator[]? InvalidatedBy);

/// <summary>
/// One command type that invalidates this query, with its compiled predicate.
/// Stored in a flat array — hot path iterates with a plain <c>for</c> loop.
/// </summary>
public readonly struct QueryInvalidator
{
    public readonly Type CommandType;
    /// <summary>Null = wildcard: any dispatch of this command type invalidates all cached variants.</summary>
    public readonly Func<object, object, bool>? Match;

    public QueryInvalidator(Type commandType, Func<object, object, bool>? match)
    { CommandType = commandType; Match = match; }
}
