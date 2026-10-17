using ActualLab.Rpc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using YFex.Tenancy;

namespace YFex.Messaging.Rpc;

/// <summary>
/// The per-call headers of the client's call context (<see cref="CallContextClientBuilder"/>: the token, the tenant scope and the extra
/// headers such as the installation assertion), for plain RPC proxy calls that do not go through <see cref="FusionMessageBus"/> (which
/// uses this type itself). Without a call-context configuration there are no headers and calls are made as they are.
/// <code>
/// var headers = new CallContextClientHeaders(services);
/// byte[] answer = await headers.CallAsync(() => issuer.IssueAsync(ct), ct);   // carries token, scope, assertion
/// </code>
/// </summary>
public sealed class CallContextClientHeaders
{
    private readonly IServiceProvider _services;
    private readonly CallContextClientBuilder? _callContext;
    private readonly IScopeContext? _scope;

    public CallContextClientHeaders(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _callContext = services.GetService<CallContextClientBuilder>();
        _scope = _callContext?.Tenant == true ? services.GetService<IScopeContext>() : null;
    }

    /// <summary>The headers for one call (providers are asked now); null when there are none.</summary>
    public async ValueTask<RpcHeader[]?> GetAsync(CancellationToken cancellationToken = default, Guid? idempotencyKey = null)
    {
        RpcHeader[]? headers = null;
        if (idempotencyKey is { } key)
            headers = headers.With(new RpcHeader(IdempotencyHeaders.Key, key.ToString("N")));
        if (_callContext is null) return headers;
        if (_callContext.TokenProvider is { } provider
            && await provider(_services, cancellationToken).ConfigureAwait(false) is { Length: > 0 } token)
            headers = headers.With(new RpcHeader(_callContext.TokenHeader, _callContext.TokenScheme + " " + token));
        if (_callContext.Tenant)
        {
            // Offline design F4: a replay sends the scope captured when the command was queued, not the scope active now.
            var scope = OutboxReplayContext.Current?.Scope
                ?? (_scope.EffectiveIdentity() is { IsEmpty: false } current ? current.ToString() : null);
            if (scope is { Length: > 0 }) headers = headers.With(new RpcHeader(CallContextHeaders.Scope, scope));
        }
        foreach ((string name, var headerProvider) in _callContext.ExtraHeaders)
            if (await headerProvider(_services, cancellationToken).ConfigureAwait(false) is { Length: > 0 } value)
                headers = headers.With(new RpcHeader(name, value));
        return headers;
    }

    /// <summary>Makes a plain proxy call with this client's headers (Fusion compute-method calls cannot carry headers).</summary>
    public async Task<T> CallAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        RpcHeader[]? headers = await GetAsync(cancellationToken).ConfigureAwait(false);
        return await Send(call, headers).ConfigureAwait(false);
    }

    // The proxy call is made synchronously inside the setup scope: Fusion's RpcInterceptor consumes it for the outbound call.
    private static Task<T> Send<T>(Func<Task<T>> call, RpcHeader[]? headers)
    {
        if (headers is null) return call();
        using (new RpcOutboundCallSetup { Headers = headers }.Activate())
            return call();
    }
}
