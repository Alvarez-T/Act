using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization;
using YFex.Authorization.Public;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Validation stage 2 (authorize) as run by every dispatcher (validation D29): <see cref="LocalDispatcher"/>,
/// <see cref="ValidatingHandlerInvoker"/>, <see cref="CallContextMiddleware"/>, the Wolverine dispatcher and — client side,
/// for UX only — <see cref="FusionMessageBus"/>.
/// </summary>
public static class CallAuthorization
{
    [ThreadStatic] private static IAuthContext? t_authorizing;

    /// <summary>
    /// The caller: the ambient <see cref="CallContext"/> (remote call), else <paramref name="accessor"/>'s current principal
    /// (host / desktop login), else anonymous.
    /// </summary>
    public static IAuthContext CurrentAuth(IPrincipalAccessor? accessor)
        => CallContext.Current?.Auth ?? accessor?.Current ?? CallContext.Anonymous;

    /// <summary>
    /// Runs a compiled policy authorizer for <paramref name="message"/> as <paramref name="auth"/>. While it runs,
    /// <see cref="ContextFor"/> hands the evaluator this already-resolved context (permissions included) instead of
    /// rebuilding one from the principal. No authorizer = allowed.
    /// </summary>
    public static bool Authorize(Func<ClaimsPrincipal, object, bool>? authorize, object message, IAuthContext auth)
    {
        if (authorize is null) return true;
        var previous = t_authorizing;
        t_authorizing = auth;
        try { return authorize(auth.Principal, message); }
        finally { t_authorizing = previous; }
    }

    /// <summary>
    /// Principal → auth context for the Cqrs registry (<c>AddYFexCqrsAuthorization</c>): reuses the context being
    /// authorized when it is for the same principal (the server's versioned per-connection cache), else builds one.
    /// </summary>
    public static IAuthContext ContextFor(ClaimsPrincipal principal, IAuthContextFactory factory)
        => t_authorizing is { } current && ReferenceEquals(current.Principal, principal) ? current : factory.Create(principal);
}

/// <summary>
/// Lazily resolved <see cref="IPrincipalAccessor"/> of a singleton dispatcher: resolved on first need only (a host whose
/// accessor is scoped, e.g. the ASP.NET adapter, never hits it for remote calls — the <see cref="CallContext"/> wins).
/// </summary>
public sealed class CallPrincipalSource(IServiceProvider? services)
{
    private IPrincipalAccessor? _accessor;
    private bool _resolved;

    /// <summary>See <see cref="CallAuthorization.CurrentAuth"/>.</summary>
    public IAuthContext Current => CallContext.Current?.Auth ?? Accessor?.Current ?? CallContext.Anonymous;

    private IPrincipalAccessor? Accessor
    {
        get
        {
            if (_resolved) return _accessor;
            _accessor = services?.GetService<IPrincipalAccessor>();
            _resolved = true;
            return _accessor;
        }
    }

    /// <summary>Stage 2 for <paramref name="message"/> as the current caller.</summary>
    public bool Authorize(Func<ClaimsPrincipal, object, bool>? authorize, object message)
        => authorize is null || CallAuthorization.Authorize(authorize, message, Current);
}

/// <summary>
/// Decorates the registered <see cref="IPrincipalAccessor"/> so every consumer (Capabilities, NavigatR, custom code) sees
/// the remote caller during an RPC call; outside of one it delegates to the host's accessor.
/// </summary>
public sealed class CallContextPrincipalAccessor(IPrincipalAccessor? inner) : IPrincipalAccessor
{
    public IAuthContext Current => CallContext.Current?.Auth ?? inner?.Current ?? CallContext.Anonymous;

    /// <summary>Replaces the current <see cref="IPrincipalAccessor"/> registration (keeping its lifetime) with the decorator.</summary>
    public static IServiceCollection Decorate(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        for (int i = services.Count - 1; i >= 0; i--)
        {
            var d = services[i];
            if (d.ServiceType != typeof(IPrincipalAccessor)) continue;
            if (IsDecorated(services)) return services;
            services.RemoveAt(i);
            services.Add(ServiceDescriptor.Describe(typeof(IPrincipalAccessor),
                sp => new CallContextPrincipalAccessor(Create(d, sp)), d.Lifetime));
            services.Add(ServiceDescriptor.Singleton(new DecoratedMarker()));
            return services;
        }
        // Nothing registered yet (AddYFexAuthorization may come later: its TryAdd keeps this one) → fall back to the ambient one.
        services.Add(ServiceDescriptor.Singleton<IPrincipalAccessor>(sp =>
            new CallContextPrincipalAccessor(sp.GetService<AmbientPrincipalAccessor>())));
        services.Add(ServiceDescriptor.Singleton(new DecoratedMarker()));
        return services;
    }

    private sealed class DecoratedMarker;

    private static bool IsDecorated(IServiceCollection services)
    {
        for (int i = 0; i < services.Count; i++)
            if (services[i].ServiceType == typeof(DecoratedMarker)) return true;
        return false;
    }

    private static IPrincipalAccessor Create(ServiceDescriptor d, IServiceProvider sp)
    {
        if (d.ImplementationInstance is IPrincipalAccessor instance) return instance;
        if (d.ImplementationFactory is { } factory) return (IPrincipalAccessor)factory(sp);
        return (IPrincipalAccessor)ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);
    }
}
