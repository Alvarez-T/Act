using System.Collections.Frozen;
using System.Security.Claims;
using ActualLab.Rpc;
using ActualLab.Rpc.Infrastructure;
using ActualLab.Rpc.Middlewares;
using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Cqrs;
using YFex.Cqrs.Runtime;
using YFex.Messaging.Rpc.Serialization;
using YFex.Tenancy;
using YFex.Validation;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Resolves the <see cref="CallContext"/> of an inbound call from the configured sources and caches the caller's
/// <see cref="IAuthContext"/> on its <see cref="CallIdentity"/> until <see cref="IPermissionsVersions"/> changes.
/// </summary>
internal sealed class CallContextResolver
{
    private readonly ICallIdentitySource[] _sources;
    private readonly bool _tenant;
    private readonly IAuthContextFactory? _factory;
    private readonly IPermissionsVersions _versions;
    private readonly TimeProvider _time;

    public CallContextResolver(CallContextServerBuilder options, IServiceProvider services)
    {
        _sources = new ICallIdentitySource[options.Sources.Count];
        for (int i = 0; i < _sources.Length; i++) _sources[i] = options.Sources[i](services);
        _tenant = options.Tenant;
        _factory = services.GetService<IAuthContextFactory>();
        _versions = services.GetRequiredService<IPermissionsVersions>();
        _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
    }

    public async ValueTask<CallContext> ResolveAsync(RpcInboundCall call, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        CallIdentity? chosen = null, fallback = null;
        for (int i = 0; i < _sources.Length; i++)
        {
            var identity = await _sources[i].ResolveAsync(call, ct).ConfigureAwait(false);
            if (identity is null) continue;
            if (ReferenceEquals(identity, CallIdentity.Rejected)) { fallback = identity; chosen = null; break; }
            if (identity.IsExpired(now)) continue;                            // expired token → as if absent
            if (identity.IsAuthenticated) { chosen = identity; break; }
            fallback ??= identity;                                            // anonymous but may carry the tenant
        }

        var caller = chosen ?? fallback;
        var auth = chosen is null ? CallContext.Anonymous : AuthFor(chosen);
        return new CallContext(auth, caller?.Scope ?? default, _tenant, caller?.Culture);
    }

    private IAuthContext AuthFor(CallIdentity identity)
    {
        long version = identity.Subject is { } subject ? _versions.Get(subject) : 0;
        if (identity.Resolved is { } resolved && resolved.Version == version) return resolved.Auth;

        IAuthContext auth;
        // Tenant-scoped grants (IScopedPermissionProvider) are resolved inside the caller's tenant.
        using (_tenant ? ScopeOverride.Enter(identity.Scope) : default)
            auth = _factory?.Create(identity.Principal, identity.Token)
                ?? new AuthContext(identity.Principal, FrozenSet<PermissionKey>.Empty, identity.Token);
        identity.Resolved = new CallIdentity.ResolvedAuth(auth, version);
        return auth;
    }
}

/// <summary>
/// Server RPC middleware of validation D29: makes the caller's <see cref="CallContext"/> ambient for every inbound call and
/// runs stage 2 (authorize) for YFex messages BEFORE Fusion's compute/command handlers.
/// </summary>
/// <remarks>
/// <para><b>Why here and not in the handler invoker:</b> queries are Fusion compute methods — cached by their arguments.
/// Authorizing inside the method would let a second caller get the first caller's cached value. This middleware runs per
/// call, outside the cache; a rejection is returned as the method's typed result (<see cref="RpcErrorResults{T}"/>).</para>
/// <para><b>Order</b> stays 1 → 2 → 5: the message is coerced and shape-checked first; a malformed message skips stage 2 and
/// the invoker reports the shape error (inside the method, so it is cached like any deterministic result).
/// <see cref="ValidatingHandlerInvoker"/> then runs stage 5 and skips its own stage 2 for the authorized instance.</para>
/// <para>For generated compute queries the <c>variant</c> argument is overwritten with the caller's cache variant (tenant,
/// + user for <c>ScopedByUser</c>/<c>ScopedBySession</c>/custom scopes) so a cached result is never shared across tenants
/// or users; for <see cref="ICallPermissions"/> the <c>caller</c> argument becomes the caller's key (anti-spoofing).</para>
/// <para>A rejected compute call has no computed; Fusion sends an invalidation after one tick, so a client that keeps a
/// denied query live re-asks — fine for one-shot dispatch (<see cref="FusionMessageBus"/>), avoid live-binding denied queries.</para>
/// </remarks>
public sealed class CallContextMiddleware : IRpcMiddleware
{
    /// <summary>After argument validation / Fusion's session replacer (1000 / 999), before compute and command handlers (0).</summary>
    public const double DefaultPriority = RpcInboundMiddlewarePriority.ArgumentValidation - 10;

    private readonly IServiceProvider _services;
    private CallContextResolver? _resolver;
    private CompiledMessagingRegistry? _registry;

    public CallContextMiddleware(IServiceProvider services) => _services = services;

    public double Priority { get; init; } = DefaultPriority;

    // Lazy: the hub builds middlewares while the container is still resolving; a racing duplicate is discarded (one
    // instance wins, so the sources' per-token/session caches stay unique).
    private CallContextResolver Resolver => LazyInitializer.EnsureInitialized(ref _resolver,
        () => new CallContextResolver(_services.GetRequiredService<CallContextServerBuilder>(), _services));

    private CompiledMessagingRegistry Registry => LazyInitializer.EnsureInitialized(ref _registry,
        () => _services.GetRequiredService<CompiledMessagingRegistry>());

    public Func<RpcInboundCall, Task<T>> Create<T>(RpcMiddlewareContext<T> context, Func<RpcInboundCall, Task<T>> next)
    {
        var method = context.MethodDef;
        if (method.IsSystem) return next;

        var parameters = method.Parameters;
        Func<ClaimsPrincipal, object, bool>? authorize = null;
        int variantIndex = -1;
        CacheScope scope = CacheScope.Global;
        if (parameters.Length > 0)
        {
            var messageType = parameters[0].ParameterType;
            if (Registry.Commands.TryGetValue(messageType, out var command)) authorize = command.Authorize;
            else if (Registry.Queries.TryGetValue(messageType, out var query))
            {
                authorize = query.Authorize;
                scope = query.Scope;
            }
            // Generated compute queries: Task<Result<T>> Name(TQuery q, string? variant, CancellationToken ct).
            if (parameters.Length == 3 && parameters[1].ParameterType == typeof(string) && parameters[1].Name == "variant")
                variantIndex = 1;
        }
        bool isPermissions = method.Service.Type == typeof(ICallPermissions);

        return call => InvokeAsync(call, next, authorize, variantIndex, scope, isPermissions);
    }

    private async Task<T> InvokeAsync<T>(RpcInboundCall call, Func<RpcInboundCall, Task<T>> next,
        Func<ClaimsPrincipal, object, bool>? authorize, int variantIndex, CacheScope scope, bool isPermissions)
    {
        var context = await Resolver.ResolveAsync(call, call.CallCancelToken).ConfigureAwait(false);
        using var active = context.Enter();

        var arguments = call.Arguments;
        if (arguments is not null)
        {
            if (authorize is not null && arguments.Get0Untyped() is { } message)
            {
                // Stage 1 → 2 (09 §1). Coercion replaces the argument so the compute-cache key is the clamped query too.
                var coerced = ObjectValidation.Coerce(message);
                if (!ReferenceEquals(coerced, message)) arguments.SetUntyped(0, message = coerced);
                if (MessagePipeline.CheckShape(message, out _))
                {
                    if (!CallAuthorization.Authorize(authorize, message, context.Auth))
                        return RpcErrorResults<T>.From(new Error(ErrorType.Unauthorized, "Unauthorized."));
                    context.AuthorizedMessage = message;
                }
            }
            if (variantIndex >= 0) arguments.SetUntyped(variantIndex, VariantOf(context, scope, call));
            if (isPermissions) arguments.SetUntyped(0, CallPermissionsService.CallerKey(context));
        }
        return await next(call).ConfigureAwait(false);
    }

    /// <summary>The compute-cache variant of a query for this caller. Never shared across tenants, even for Global.</summary>
    private static string VariantOf(CallContext context, CacheScope scope, RpcInboundCall call)
    {
        var tenant = context.HasScope ? context.Scope.ToString() : "";
        return scope switch
        {
            CacheScope.Global or CacheScope.Tenant => tenant,
            CacheScope.Session => tenant + "|c:" + call.Context.Peer.Ref,
            _ => tenant + "|u:" + context.Subject,   // User, Custom (conservative: per user)
        };
    }
}
