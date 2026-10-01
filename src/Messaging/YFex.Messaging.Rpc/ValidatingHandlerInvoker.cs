using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;
using YFex.Cqrs.Runtime;
using YFex.Validation;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Server-side validation (D25): decorates the registered <see cref="IHandlerInvoker"/> so every message entering the
/// server through it — the generated Fusion <c>{Aggregate}RpcContractImpl</c>, Wolverine, outbox replay — passes
/// stage 1 (shape, <see cref="ObjectValidation"/>), stage 2 (authorize, D29) and stage 5 (context rules of its policy)
/// before the handler runs.
/// </summary>
/// <remarks>
/// <see cref="IHandlerInvoker"/> returns the handler's plain value, so a rejection is thrown as an
/// <see cref="ErrorResultException"/> carrying <see cref="Error.Validation"/>; the generated contract impl catches it
/// and returns the typed error as the RPC result (nothing is thrown across the wire). Only invalid messages pay for
/// the exception. <see cref="LocalDispatcher"/> runs the stages itself and unwraps <see cref="Inner"/>.
/// Stage 2 authorizes the <see cref="CallContext"/> caller (else the host's <see cref="YFex.Authorization.Public.IPrincipalAccessor"/>);
/// it is skipped for the exact message instance <see cref="CallContextMiddleware"/> already authorized outside Fusion's
/// compute cache (RPC calls).
/// </remarks>
public sealed class ValidatingHandlerInvoker : IHandlerInvoker
{
    private readonly CompiledMessagingRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly CallPrincipalSource _principal;

    public ValidatingHandlerInvoker(IHandlerInvoker inner, CompiledMessagingRegistry registry, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner is ValidatingHandlerInvoker nested ? nested.Inner : inner;
        _registry = registry;
        _services = services;
        _principal = new CallPrincipalSource(services);
    }

    /// <summary>The undecorated invoker.</summary>
    public IHandlerInvoker Inner { get; }

    public async Task<T> InvokeAsync<T>(object message, CancellationToken ct)
    {
        message = ObjectValidation.Coerce(message);   // old clients may send un-clamped paging (09 §7)
        var (stage, rules) = await ValidateAsync(message, ct).ConfigureAwait(false);
        using var decisionScope = DecisionContext.Enter(message, stage, rules);
        return await Inner.InvokeAsync<T>(message, ct).ConfigureAwait(false);
    }

    public async Task InvokeAsync(object message, CancellationToken ct)
    {
        var (stage, rules) = await ValidateAsync(message, ct).ConfigureAwait(false);
        using var decisionScope = DecisionContext.Enter(message, stage, rules);
        await Inner.InvokeAsync(message, ct).ConfigureAwait(false);
    }

    // Stages 1 → 2 → 3–4 → 5. Returns the rules stage + outcome so the caller publishes the decision around the handler
    // (an AsyncLocal set inside this async method would not flow back to the caller).
    private async ValueTask<(RulesStage? Stage, RulesOutcome Rules)> ValidateAsync(object message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!MessagePipeline.CheckShape(message, out var shape))
            throw new ErrorResultException(Error.Validation(shape));

        var type = message.GetType();
        MessageValidator? context;
        Func<System.Security.Claims.ClaimsPrincipal, object, bool>? authorize;
        RulesStage? stage;
        if (_registry.Commands.TryGetValue(type, out var command)) (context, authorize, stage) = (command.Validation, command.Authorize, command.Rules);
        else if (_registry.Queries.TryGetValue(type, out var query)) (context, authorize, stage) = (query.Validation, query.Authorize, query.Rules);
        else return default;

        // Stage 2 (D29) — unless the RPC middleware already authorized this very instance.
        if (authorize is not null && !ReferenceEquals(CallContext.Current?.AuthorizedMessage, message)
            && !_principal.Authorize(authorize, message))
            throw new ErrorResultException(new Error(ErrorType.Unauthorized, "Unauthorized."));

        // Stages 3–4 (09 §6): situation + rules.
        RulesOutcome rules = default;
        if (stage is not null)
        {
            rules = await stage.RunAsync(message, _services, ct).ConfigureAwait(false);
            if (rules.Error is { } rejected) throw new ErrorResultException(rejected);
        }

        if (context is not null)
        {
            var result = await context.ValidateAsync(message, _services, ct).ConfigureAwait(false);
            if (!result.IsValid) throw new ErrorResultException(Error.Validation(result));
        }
        return (stage, rules);
    }

    /// <summary>
    /// Replaces the current <see cref="IHandlerInvoker"/> registration with a validating decorator around it. Call
    /// after the invoker is registered (<c>UseYFexMessagingRpcServer</c> and the Wolverine adapter do).
    /// </summary>
    public static IServiceCollection Decorate(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ServiceDescriptor? current = null;
        for (int i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType != typeof(IHandlerInvoker)) continue;
            current = services[i];
            services.RemoveAt(i);
            break;
        }
        if (current is null) return services;
        if (current.ImplementationType == typeof(ValidatingHandlerInvoker)) { services.Add(current); return services; }

        services.Add(ServiceDescriptor.Describe(typeof(IHandlerInvoker), sp => new ValidatingHandlerInvoker(
            CreateInner(current, sp), sp.GetRequiredService<CompiledMessagingRegistry>(), sp), current.Lifetime));
        return services;
    }

    private static IHandlerInvoker CreateInner(ServiceDescriptor d, IServiceProvider sp)
    {
        if (d.ImplementationInstance is IHandlerInvoker instance) return instance;
        if (d.ImplementationFactory is { } factory) return (IHandlerInvoker)factory(sp);
        return (IHandlerInvoker)ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);
    }
}
