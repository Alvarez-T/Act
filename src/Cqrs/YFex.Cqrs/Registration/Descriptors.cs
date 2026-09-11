// Registration-phase descriptors — pure data carriers produced by fluent builders,
// consumed by CompiledMessagingRegistry.Build().  No behaviour, no LINQ, no methods.
using System.Linq.Expressions;
using System.Security.Claims;
using YFex.Authorization.Public;

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

internal enum AuthorizerKind { RequireAuthenticated, Roles, PolicyName, Inline, TypeRef, Permission }

internal readonly struct AuthorizerDescriptor
{
    public readonly AuthorizerKind Kind;
    public readonly Func<ClaimsPrincipal, object, bool>? InlinePredicate;
    public readonly Type?       AuthorizerType;
    public readonly string[]?   Roles;
    public readonly string?     PolicyName;
    /// <summary>Set when Kind == Permission. Evaluated via YFex.Authorization's IPolicyEvaluator.</summary>
    public readonly Permission  Permission;

    private AuthorizerDescriptor(AuthorizerKind kind,
        Func<ClaimsPrincipal, object, bool>? pred, Type? type, string[]? roles, string? policy,
        Permission permission = default)
    { Kind = kind; InlinePredicate = pred; AuthorizerType = type; Roles = roles; PolicyName = policy; Permission = permission; }

    public static AuthorizerDescriptor MustBeAuthenticated()
        => new(AuthorizerKind.RequireAuthenticated, null, null, null, null);
    public static AuthorizerDescriptor MustHaveRoles(string[] roles)
        => new(AuthorizerKind.Roles, null, null, roles, null);
    public static AuthorizerDescriptor MustSatisfyPolicy(string name)
        => new(AuthorizerKind.PolicyName, null, null, null, name);
    public static AuthorizerDescriptor MustHavePermission(Permission permission)
        => new(AuthorizerKind.Permission, null, null, null, null, permission);
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
    /// <summary>Extracts the entity key from the source message for precise tag invalidation. Null = coarse.</summary>
    public readonly Func<object, string>? KeySelector;

    public InvalidationRuleDescriptor(Type targetType, LambdaExpression? match, bool isGroup = false)
    { TargetType = targetType; MatchExpression = match; IsGroup = isGroup; KeySelector = null; }

    public InvalidationRuleDescriptor(Type targetType, Func<object, string> keySelector)
    { TargetType = targetType; MatchExpression = null; IsGroup = false; KeySelector = keySelector; }
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

// ── Reverse (compensation) rule ───────────────────────────────────────────────

/// <summary>
/// Declares how a command is reversed/compensated. At most one per command (single value,
/// last-write-wins — mirrors <see cref="OptimisticRuleDescriptor"/>). Either a typed map to a
/// registered reverse command, or a custom <c>IReverseHandler</c> type.
/// </summary>
internal readonly struct ReverseRuleDescriptor
{
    /// <summary>The reverse command's CLR type. Set for the map form; must itself be a registered command.
    /// Null for the custom-handler form.</summary>
    public readonly Type? ReverseCommandType;
    /// <summary>Map expression: <c>Expression&lt;Func&lt;TCommand, TReverse&gt;&gt;</c> or
    /// <c>Expression&lt;Func&lt;TCommand, TResult, TReverse&gt;&gt;</c>. Compiled at Build time.
    /// Null for the custom-handler form. Parameter count distinguishes the two arities.</summary>
    public readonly LambdaExpression? MapExpression;
    /// <summary>Custom reverse handler type (<c>IReverseHandler&lt;TCommand&gt;</c> or
    /// <c>IReverseHandler&lt;TCommand, TResult&gt;</c>). Resolved from DI at compensation time.
    /// Null for the map form.</summary>
    public readonly Type? HandlerType;

    /// <summary>Strongly-typed reverse-command dispatch, captured in <c>.Reversible&lt;TReverse&gt;()</c>
    /// where <c>TReverse</c> is a generic parameter — dispatches the already-typed reverse command with
    /// no <c>MakeGenericMethod</c>. Set for the map form; null for the handler form.</summary>
    public readonly Func<IDispatcher, object, CancellationToken, ValueTask>? DispatchReverse;
    /// <summary>Strongly-typed handler invocation, captured in <c>.ReversibleWith&lt;THandler&gt;()</c>
    /// where the handler/command/result are generic parameters — resolves the handler from the provider
    /// and calls the typed <c>CompensateAsync</c> with no reflection. Set for the handler form.</summary>
    public readonly Func<IServiceProvider, object, object?, CancellationToken, ValueTask>? InvokeHandler;
    /// <summary>Declared result type (<c>typeof(TResult)</c>) of the reversed command; null for void
    /// commands. Threaded to <c>ReversePolicy</c> so the journal deserializes the result reflection-free.</summary>
    public readonly Type? ResultType;

    private ReverseRuleDescriptor(Type? reverseCommandType, LambdaExpression? map, Type? handlerType,
        Func<IDispatcher, object, CancellationToken, ValueTask>? dispatchReverse,
        Func<IServiceProvider, object, object?, CancellationToken, ValueTask>? invokeHandler,
        Type? resultType)
    {
        ReverseCommandType = reverseCommandType; MapExpression = map; HandlerType = handlerType;
        DispatchReverse = dispatchReverse; InvokeHandler = invokeHandler; ResultType = resultType;
    }

    public static ReverseRuleDescriptor FromMap(Type reverseCommandType, LambdaExpression map,
        Func<IDispatcher, object, CancellationToken, ValueTask> dispatchReverse, Type? resultType = null)
        => new(reverseCommandType, map, null, dispatchReverse, null, resultType);
    public static ReverseRuleDescriptor FromHandler(Type handlerType,
        Func<IServiceProvider, object, object?, CancellationToken, ValueTask> invokeHandler, Type? resultType = null)
        => new(null, null, handlerType, null, invokeHandler, resultType);
}

// ── Offline handler ───────────────────────────────────────────────────────────

internal readonly struct OfflineHandlerDescriptor
{
    public readonly Func<object, CancellationToken, ValueTask>? InlineDelegate;
    public readonly Type? HandlerType;
    /// <summary>Strongly-typed resolve + invoke captured in <c>.OnOffline&lt;THandler&gt;()</c> where
    /// <c>THandler</c>/<c>TCommand</c>[/<c>TResult</c>] are compile-time generic parameters — it resolves
    /// the handler from the supplied <see cref="IServiceProvider"/> and calls the typed
    /// <c>HandleAsync</c> with NO reflection on the offline path. Set for the type-ref form.</summary>
    public readonly Func<IServiceProvider, object, CancellationToken, ValueTask>? Invoke;

    private OfflineHandlerDescriptor(Func<object, CancellationToken, ValueTask>? del, Type? t,
        Func<IServiceProvider, object, CancellationToken, ValueTask>? invoke)
    { InlineDelegate = del; HandlerType = t; Invoke = invoke; }

    public static OfflineHandlerDescriptor Inline(Func<object, CancellationToken, ValueTask> del)
        => new(del, null, null);
    public static OfflineHandlerDescriptor FromType(Type t,
        Func<IServiceProvider, object, CancellationToken, ValueTask> invoke)
        => new(null, t, invoke);
}
