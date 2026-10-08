using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Claims;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Tenancy;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>Permissions of the shop test domain (declared once in the baseline, enforced client- AND server-side).</summary>
[PermissionCatalog]
public static partial class ShopPermissions
{
    [Permission("shop.customers.create", Name = "Cadastrar cliente")]
    public static partial Permission CreateCustomer { get; }

    [Permission("shop.customers.search", Name = "Pesquisar clientes")]
    public static partial Permission SearchCustomers { get; }

    [Permission("shop.customers.archive", Name = "Arquivar clientes")]
    public static partial Permission ArchiveCustomers { get; }

    [Permission("shop.customers.export", Name = "Exportar clientes")]
    public static partial Permission ExportCustomers { get; }
}

/// <summary>Test users and tenants.</summary>
public static class Users
{
    public const byte TenantRank = 1;
    public static readonly ScopeIdentity Acme = ScopeIdentity.Of(new ScopeValue(TenantRank, "acme"));
    public static readonly ScopeIdentity Globex = ScopeIdentity.Of(new ScopeValue(TenantRank, "globex"));

    public static ClaimsPrincipal Principal(string subject)
        => new(new ClaimsIdentity([new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, subject)], authenticationType: "test"));

    public static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    /// <summary>The per-call token of <paramref name="subject"/> understood by <see cref="TestTokenValidator"/>.</summary>
    public static string Token(string subject) => "t:" + subject;

    public static string TenantOf(ScopeIdentity scope) => scope.IsEmpty ? "-" : scope.Rungs[0].Value;
}

/// <summary>Mutable permission store keyed by subject; counts loads to prove the server caches per permissions version.</summary>
public sealed class TestPermissionSource : IPermissionSource
{
    private readonly ConcurrentDictionary<string, FrozenSet<PermissionKey>> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _loads = new(StringComparer.Ordinal);

    public TestPermissionSource Grant(string subject, params Permission[] permissions)
    {
        _grants.AddOrUpdate(subject,
            _ => permissions.Select(p => p.Key).ToFrozenSet(),
            (_, old) => old.Concat(permissions.Select(p => p.Key)).ToFrozenSet());
        return this;
    }

    public TestPermissionSource Revoke(string subject, params Permission[] permissions)
    {
        _grants.AddOrUpdate(subject, _ => FrozenSet<PermissionKey>.Empty,
            (_, old) => old.Except(permissions.Select(p => p.Key)).ToFrozenSet());
        return this;
    }

    public int LoadsOf(string subject) => _loads.TryGetValue(subject, out var n) ? n : 0;

    public ValueTask ContributeAsync(PermissionResolution request, PermissionSet.Builder permissions, CancellationToken cancellationToken)
    {
        if (request.Subject.IsAnonymous) return ValueTask.CompletedTask;
        var subject = request.Subject.Subject;
        _loads.AddOrUpdate(subject, 1, static (_, n) => n + 1);
        if (_grants.TryGetValue(subject, out var set))
            foreach (var key in set) permissions.Add(key);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Per-call token validator (header option): <c>"t:{subject}"</c> is valid for the subjects below; the requested tenant is
/// granted only when the subject belongs to it (client input is never trusted blindly).
/// </summary>
public sealed class TestTokenValidator : ICallTokenValidator
{
    private static readonly Dictionary<string, string[]> TenantsOf = new(StringComparer.Ordinal)
    {
        ["alice"] = ["acme"], ["bob"] = ["globex"], ["dave"] = ["acme"], ["mallory"] = ["acme"],
    };

    public int Validations;

    public ValueTask<CallIdentity?> ValidateAsync(AccessToken token, ScopeIdentity requestedScope, CancellationToken ct)
    {
        Interlocked.Increment(ref Validations);
        if (!token.Value.StartsWith("t:", StringComparison.Ordinal)) return new((CallIdentity?)null);
        var subject = token.Value[2..];
        if (!TenantsOf.TryGetValue(subject, out var tenants)) return new((CallIdentity?)null);
        if (!requestedScope.IsEmpty && Array.IndexOf(tenants, Users.TenantOf(requestedScope)) < 0) return new((CallIdentity?)null);
        return new(new CallIdentity(Users.Principal(subject), token, requestedScope));
    }
}

/// <summary>Client-side "logged-in user" for the UX-only stage 2 of <see cref="FusionMessageBus"/>.</summary>
public sealed class FixedPrincipalAccessor(IAuthorizationContext current) : IPrincipalAccessor
{
    public IAuthorizationContext Current { get; set; } = current;
}

/// <summary>Minimal request services of a fake WebSocket upgrade request: only the host's resolved tenant.</summary>
internal sealed class UpgradeRequestServices(ScopeIdentity tenant) : IServiceProvider, IScopeContext
{
    public object? GetService(Type serviceType) => serviceType == typeof(IScopeContext) ? this : null;
    public ScopeIdentity Current => ScopeOverride.TryGet(out var o) ? o : tenant;
    public int Resolve(Span<ScopeKey> buffer) => 0;
}
