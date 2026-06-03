// Registration-phase descriptors — pure data carriers produced by fluent builders,
// consumed by CompiledMessagingRegistry.Build().  No behaviour, no LINQ, no methods.
using System.Linq.Expressions;
using System.Security.Claims;

namespace YFex.Cqrs.Registration;

// ── Validator ─────────────────────────────────────────────────────────────────

internal enum ValidatorKind { Inline, TypeRef, FluentTypeRef }

internal readonly struct ValidatorDescriptor
{
    public readonly ValidatorKind Kind;
    /// <summary>Set when Kind == Inline. Type-erased wrapper compiled from user lambda at registration time.</summary>
    public readonly Func<object, CancellationToken, ValueTask<ValidationResult>>? InlineDelegate;
    /// <summary>Set when Kind == TypeRef or FluentTypeRef. Resolved from DI at first call (Plan 3).</summary>
    public readonly Type? ValidatorType;

    private ValidatorDescriptor(ValidatorKind kind,
        Func<object, CancellationToken, ValueTask<ValidationResult>>? del,
        Type? type) { Kind = kind; InlineDelegate = del; ValidatorType = type; }

    public static ValidatorDescriptor Inline(Func<object, CancellationToken, ValueTask<ValidationResult>> del)
        => new(ValidatorKind.Inline, del, null);
    public static ValidatorDescriptor FromType(Type t, ValidatorKind kind = ValidatorKind.TypeRef)
        => new(kind, null, t);
}

// ── Authorizer ────────────────────────────────────────────────────────────────

internal enum AuthorizerKind { RequireAuthenticated, Roles, PolicyName, Inline, TypeRef }

internal readonly struct AuthorizerDescriptor
{
    public readonly AuthorizerKind Kind;
    public readonly Func<ClaimsPrincipal, object, bool>? InlinePredicate;
    public readonly Type?     AuthorizerType;
    public readonly string[]? Roles;
    public readonly string?   PolicyName;

    private AuthorizerDescriptor(AuthorizerKind kind,
        Func<ClaimsPrincipal, object, bool>? pred, Type? type, string[]? roles, string? policy)
    { Kind = kind; InlinePredicate = pred; AuthorizerType = type; Roles = roles; PolicyName = policy; }

    public static AuthorizerDescriptor MustBeAuthenticated()
        => new(AuthorizerKind.RequireAuthenticated, null, null, null, null);
    public static AuthorizerDescriptor MustHaveRoles(string[] roles)
        => new(AuthorizerKind.Roles, null, null, roles, null);
    public static AuthorizerDescriptor MustSatisfyPolicy(string name)
        => new(AuthorizerKind.PolicyName, null, null, null, name);
    public static AuthorizerDescriptor Inline(Func<ClaimsPrincipal, object, bool> pred)
        => new(AuthorizerKind.Inline, pred, null, null, null);
    public static AuthorizerDescriptor FromType(Type t)
        => new(AuthorizerKind.TypeRef, null, t, null, null);
}

// ── Invalidation rule ─────────────────────────────────────────────────────────

internal readonly struct InvalidationRuleDescriptor
{
    /// <summary>Concrete command/query type OR a marker interface / union type when IsGroup == true.</summary>
    public readonly Type TargetType;
    /// <summary>Match predicate expression (only for single-type rules). Compiled to delegate at Build time.</summary>
    public readonly LambdaExpression? MatchExpression;
    /// <summary>True when TargetType is a marker interface (IInvalidationGroup) or union type.</summary>
    public readonly bool IsGroup;

    public InvalidationRuleDescriptor(Type targetType, LambdaExpression? match, bool isGroup = false)
    { TargetType = targetType; MatchExpression = match; IsGroup = isGroup; }
}

// ── Optimistic update rule ────────────────────────────────────────────────────

internal readonly struct OptimisticRuleDescriptor
{
    public readonly Type QueryType;
    /// <summary>Match expression: Expression&lt;Func&lt;TQuery, TCommand, bool&gt;&gt;. Compiled at Build time.</summary>
    public readonly LambdaExpression MatchExpression;
    /// <summary>Apply expression: Expression&lt;Func&lt;TResult, TCommand, TResult&gt;&gt;. Compiled at Build time.
    /// NOTE: <c>with</c> expressions are not supported; use explicit constructors.</summary>
    public readonly LambdaExpression ApplyExpression;

    public OptimisticRuleDescriptor(Type queryType, LambdaExpression match, LambdaExpression apply)
    { QueryType = queryType; MatchExpression = match; ApplyExpression = apply; }
}

// ── Offline handler ───────────────────────────────────────────────────────────

internal readonly struct OfflineHandlerDescriptor
{
    public readonly Func<object, CancellationToken, ValueTask>? InlineDelegate;
    public readonly Type? HandlerType;

    private OfflineHandlerDescriptor(Func<object, CancellationToken, ValueTask>? del, Type? t)
    { InlineDelegate = del; HandlerType = t; }

    public static OfflineHandlerDescriptor Inline(Func<object, CancellationToken, ValueTask> del)
        => new(del, null);
    public static OfflineHandlerDescriptor FromType(Type t)
        => new(null, t);
}
