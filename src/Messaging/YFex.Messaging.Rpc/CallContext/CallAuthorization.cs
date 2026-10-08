using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Cqrs;

namespace YFex.Messaging.Rpc;

/// <summary>
/// The caller and the ONE message gate of a dispatcher (validation D29, authorization-consolidation 04 §2):
/// <see cref="LocalDispatcher"/>, <see cref="ValidatingHandlerInvoker"/>, <see cref="CallContextMiddleware"/> and the Wolverine
/// dispatcher run the full check (<see cref="AuthorizeAsync"/>); <see cref="FusionMessageBus"/> runs the quick check
/// (<see cref="IsVisible"/>, UX only — the server re-checks).
/// </summary>
/// <remarks>
/// The caller is the ambient <see cref="CallContext"/> (remote call), else the registered <see cref="IPrincipalAccessor"/> (host /
/// desktop login), else anonymous. The gate is <see cref="IMessageAuthorization"/>; when the messaging registry declares
/// authorization requirements and no gate (or a gate that does not know them) is registered, construction fails — never a
/// silent allow or a silent deny at run time.
/// </remarks>
public sealed class CallPrincipalSource
{
    private readonly IServiceProvider? _services;
    private IPrincipalAccessor? _accessor;
    private bool _accessorResolved;

    public CallPrincipalSource(IServiceProvider? services, CompiledMessagingRegistry? registry = null)
    {
        _services = services;
        Authorization = services?.GetService<IMessageAuthorization>();
        if (registry is not null) EnsureConfigured(registry, Authorization);
    }

    /// <summary>The message gate, or null when authorization is not wired (allowed only when nothing declares requirements).</summary>
    public IMessageAuthorization? Authorization { get; }

    /// <summary>The current caller.</summary>
    public IAuthorizationContext Current => CallContext.Current?.Caller ?? Accessor?.Current ?? CallContext.Anonymous;

    // Lazy: a host whose accessor is scoped (ASP.NET Core) never resolves it for remote calls — the CallContext wins.
    private IPrincipalAccessor? Accessor
    {
        get
        {
            if (_accessorResolved) return _accessor;
            _accessor = _services?.GetService<IPrincipalAccessor>();
            _accessorResolved = true;
            return _accessor;
        }
    }

    /// <summary>Full (authoritative) check of <paramref name="message"/> as the current caller. No gate = allowed.</summary>
    public ValueTask<AuthorizationVerdict> AuthorizeAsync(object message, CancellationToken cancellationToken)
        => Authorization is { } gate
            ? gate.AuthorizeAsync(message.GetType(), message, Current, cancellationToken)
            : new(AuthorizationVerdict.Allowed);

    /// <summary>Quick check (client UX): undecidable parts hide. No gate = visible.</summary>
    public bool IsVisible(Type messageType) => Authorization?.IsVisible(messageType, Current) ?? true;

    /// <summary>Quick check with the reason (client UX). No gate = allowed.</summary>
    public AuthorizationVerdict QuickCheck(Type messageType)
        => Authorization?.QuickCheck(messageType, Current) ?? AuthorizationVerdict.Allowed;

    /// <summary>
    /// Fail-closed startup check: every message type of <paramref name="registry"/> that declares authorization requirements must be
    /// known to <paramref name="gate"/> (i.e. exported by <see cref="CqrsAuthorizationConfiguration"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Requirements are declared but no gate is registered, or the gate misses some.</exception>
    public static void EnsureConfigured(CompiledMessagingRegistry registry, IMessageAuthorization? gate)
    {
        if (!registry.HasAuthorizationRequirements) return;
        if (gate is null)
            throw new InvalidOperationException(
                $"YFex.Cqrs: {registry.AuthorizationRequirements.Count} message type(s) declare authorization requirements " +
                $"(e.g. {registry.AuthorizationRequirements.Keys.First().Name}) but no IMessageAuthorization is registered. " +
                "Call services.AddYFexAuthorization(...).");
        List<string>? missing = null;
        foreach (var type in registry.AuthorizationRequirements.Keys)
            if (!gate.HasPolicy(type)) (missing ??= []).Add(type.Name);
        if (missing is not null)
            throw new InvalidOperationException(
                "YFex.Cqrs: authorization requirements of " + string.Join(", ", missing) + " are not in the authorization registry. " +
                "Call services.AddYFexCqrsAuthorizationConfiguration() (the Messaging.Rpc and Wolverine registrations do it).");
    }
}

/// <summary>
/// Decorates the registered <see cref="IPrincipalAccessor"/> so every consumer (Capabilities, NavigatR, custom code) sees
/// the remote caller during an RPC call; outside of one it delegates to the host's accessor.
/// </summary>
public sealed class CallContextPrincipalAccessor(IPrincipalAccessor? inner) : IPrincipalAccessor
{
    public IAuthorizationContext Current => CallContext.Current?.Caller ?? inner?.Current ?? CallContext.Anonymous;

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
            // A type registration stays a container registration of the concrete type (no ActivatorUtilities reflection).
            if (d.ImplementationType is { } type && type != typeof(IPrincipalAccessor))
                services.Add(ServiceDescriptor.Describe(type, type, d.Lifetime));
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
        return (IPrincipalAccessor)sp.GetRequiredService(d.ImplementationType!);
    }
}
