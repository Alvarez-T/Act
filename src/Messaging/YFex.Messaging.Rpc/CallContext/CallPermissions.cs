using System.Collections.Concurrent;
using ActualLab.Fusion;
using YFex.Authorization.Public;

namespace YFex.Messaging.Rpc;

/// <summary>
/// The caller's effective permissions, pushed to the client (validation D29): a Fusion compute method the server
/// invalidates on <see cref="IAuthorizationSignals"/> changes, so a client UI bound to it refreshes (the client feed of
/// <c>UseServerPermissions()</c> applies it to <c>SnapshotPrincipalAccessor</c>, authorization-consolidation 05 §3). For UX
/// only — the server re-checks every call (stage 2) and never trusts the client.
/// </summary>
/// <remarks>
/// Proxies (<c>ActualLabProxies/ICallPermissionsProxy.cs</c>, <c>CallPermissionsServiceProxy.cs</c>) are ActualLab.Generators
/// 13.0.12 output checked in verbatim (the library doesn't reference the generator); <c>ProxyGoldenTests</c> regenerates
/// them from this file and fails on any difference. <see cref="PermissionSnapshot"/> travels through
/// <c>PermissionSnapshotFormatter</c> (YFex.Authorization.Public must not reference MemoryPack).
/// </remarks>
public interface ICallPermissions : IComputeService
{
    /// <param name="caller">Pass null. The server replaces it with the caller's key (tenant + subject) — the compute cache is
    /// keyed per caller and a client can't read someone else's permissions.</param>
    [ComputeMethod]
    Task<PermissionSnapshot> GetMyPermissionsAsync(string? caller = null, CancellationToken ct = default);
}

/// <summary>Server implementation of <see cref="ICallPermissions"/> (registered by <c>UseYFexMessagingRpcServer</c>).</summary>
public class CallPermissionsService : ICallPermissions
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _keysBySubject = new(StringComparer.Ordinal);

    [ComputeMethod]
    public virtual Task<PermissionSnapshot> GetMyPermissionsAsync(string? caller = null, CancellationToken ct = default)
    {
        // `caller` is only the compute-cache key: CallContextMiddleware overwrote it with the caller's own key. Anything else
        // (an in-process call, a key that isn't the caller's) gets the anonymous snapshot.
        var context = CallContext.Current;
        if (context is null || caller is null || !string.Equals(caller, CallerKey(context), StringComparison.Ordinal))
            return Task.FromResult(PermissionSnapshot.Anonymous);
        if (context.Subject is not { } subject)
            return Task.FromResult(PermissionSnapshot.Anonymous);

        _keysBySubject.GetOrAdd(subject, static _ => new(StringComparer.Ordinal)).TryAdd(caller, 0);
        // Ids, never process-local ordinals; the version is the one the caller was resolved at.
        return Task.FromResult(PermissionSnapshot.From(context.Caller));
    }

    /// <summary>The compute-cache key of <paramref name="context"/>'s caller: tenant scope + subject.</summary>
    public static string CallerKey(CallContext context)
        => (context.HasScope ? context.Scope.ToString() : "") + "|" + context.Subject;

    /// <summary>Invalidates every cached snapshot of <paramref name="subject"/> (all its tenants). Call on the proxy instance.</summary>
    internal void Invalidate(string subject)
    {
        if (!_keysBySubject.TryGetValue(subject, out var keys)) return;
        using (Invalidation.Begin())
            foreach (var key in keys.Keys)
                _ = GetMyPermissionsAsync(key, default);
    }

    internal void InvalidateAll()
    {
        using (Invalidation.Begin())
            foreach (var keys in _keysBySubject.Values)
                foreach (var key in keys.Keys)
                    _ = GetMyPermissionsAsync(key, default);
    }
}
