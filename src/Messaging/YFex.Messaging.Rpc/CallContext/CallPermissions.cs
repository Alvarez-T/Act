using System.Collections.Concurrent;
using ActualLab.Fusion;
using MemoryPack;
using YFex.Authorization;
using YFex.Authorization.Public;

namespace YFex.Messaging.Rpc;

/// <summary>
/// The caller's effective permissions, pushed to the client (validation D29): a Fusion compute method the server
/// invalidates on <see cref="CallContextSignals.PermissionsChanged"/>, so a client UI bound to it (e.g. a computed state
/// enabling buttons) refreshes. For UX only — the server re-checks every call (stage 2) and never trusts the client.
/// </summary>
/// <remarks>
/// Proxies (<c>ActualLabProxies/ICallPermissionsProxy.cs</c>, <c>CallPermissionsServiceProxy.cs</c>) are ActualLab.Generators
/// 13.0.12 output checked in verbatim (the library doesn't reference the generator); <c>ProxyGoldenTests</c> regenerates
/// them from this file and fails on any difference.
/// </remarks>
public interface ICallPermissions : IComputeService
{
    /// <param name="caller">Pass null. The server replaces it with the caller's key (tenant + subject) — the compute cache is
    /// keyed per caller and a client can't read someone else's permissions.</param>
    [ComputeMethod]
    Task<CallPermissions> GetMyPermissionsAsync(string? caller = null, CancellationToken ct = default);
}

/// <summary>Snapshot of the caller's permissions.</summary>
[MemoryPackable]
public sealed partial record CallPermissions(string? Subject, bool IsAuthenticated, long Version, string[] Permissions)
{
    public static CallPermissions Anonymous { get; } = new(null, false, 0, []);

    public bool Has(Permission permission) => Array.IndexOf(Permissions, permission.Key.Value) >= 0;
}

/// <summary>Server implementation of <see cref="ICallPermissions"/> (registered by <c>UseYFexMessagingRpcServer</c>).</summary>
public class CallPermissionsService : ICallPermissions
{
    private readonly IPermissionsVersions _versions;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _keysBySubject = new(StringComparer.Ordinal);

    public CallPermissionsService(IPermissionsVersions versions) => _versions = versions;

    [ComputeMethod]
    public virtual Task<CallPermissions> GetMyPermissionsAsync(string? caller = null, CancellationToken ct = default)
    {
        // `caller` is only the compute-cache key: CallContextMiddleware overwrote it with the caller's own key. Anything else
        // (an in-process call, a key that isn't the caller's) gets the anonymous snapshot.
        var context = CallContext.Current;
        if (context is null || caller is null || !string.Equals(caller, CallerKey(context), StringComparison.Ordinal))
            return Task.FromResult(CallPermissions.Anonymous);
        if (context.Subject is not { } subject)
            return Task.FromResult(CallPermissions.Anonymous);

        _keysBySubject.GetOrAdd(subject, static _ => new(StringComparer.Ordinal)).TryAdd(caller, 0);
        var granted = (context.Auth as AuthContext)?.Permissions;
        var permissions = new string[granted?.Count ?? 0];
        int i = 0;
        if (granted is not null)
            foreach (var key in granted) permissions[i++] = key.Value;
        Array.Sort(permissions, StringComparer.Ordinal);
        return Task.FromResult(new CallPermissions(subject, true, _versions.Get(subject), permissions));
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
