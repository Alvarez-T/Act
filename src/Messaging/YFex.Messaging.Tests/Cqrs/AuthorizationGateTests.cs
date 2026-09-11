using System.Security.Claims;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Tests.Fixtures;
using SClaim = System.Security.Claims.Claim;

namespace YFex.Messaging.Tests.Cqrs;

/// <summary>
/// Gate 1 — CQRS authorization rerouted through YFex.Authorization's IPolicyEvaluator.
/// Verifies the PolicyName/TypeRef enforcement fix (previously silently unenforced) and the new
/// Permission/Role[] builder overloads, while proving Authenticated/Roles/inline are unchanged.
/// </summary>
[Trait("Category", "Cqrs")]
public sealed class AuthorizationGateTests
{
    private static readonly Permission OrdersEdit = Permission.Parse("orders.edit");

    private static ClaimsPrincipal Principal(bool authenticated, string[]? roles = null, string[]? permissions = null)
    {
        var claims = new List<SClaim>();
        foreach (var r in roles ?? []) claims.Add(new SClaim(ClaimTypes.Role, r));
        foreach (var p in permissions ?? []) claims.Add(new SClaim("permission", p));
        var identity = authenticated ? new ClaimsIdentity(claims, "test") : new ClaimsIdentity(claims);
        return new ClaimsPrincipal(identity);
    }

    // Wires a real evaluator + a principal factory that resolves permissions from "permission" claims.
    private static (IPolicyEvaluator, Func<ClaimsPrincipal, IAuthContext>) Authz()
    {
        var provider = new DefaultPermissionProvider();
        Func<ClaimsPrincipal, IAuthContext> factory = p => new AuthContext(p, provider.PermissionsFor(p));
        return (new PolicyEvaluator(), factory);
    }

    private sealed class PermissionConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.DeleteCommand>().RequirePermission(OrdersEdit);
    }

    private sealed class PolicyNameConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.DeleteCommand>().RequireAuthorization("orders-policy");
    }

    private sealed class DenyAuthorizer : ICommandAuthorizer<TestAggregate.Commands.DeleteCommand>
    {
        public bool Authorize(ClaimsPrincipal user, TestAggregate.Commands.DeleteCommand command) => true; // never actually invoked
    }

    private sealed class TypeRefConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.DeleteCommand>().Authorize<DenyAuthorizer>();
    }

    private sealed class AuthenticatedConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.DeleteCommand>().RequireAuthenticated();
    }

    private sealed class RolesValueObjectConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.DeleteCommand>().RequireRoles((Role)"Manager");
    }

    private static Func<ClaimsPrincipal, object, bool>? Gate(
        IAggregateConfiguration config,
        bool wireAuthz)
    {
        var (eval, factory) = wireAuthz ? Authz() : (null, null);
        var registry = CompiledMessagingRegistry.Build(
            baseline: [config], policyEvaluator: eval, principalFactory: factory);
        return registry.Commands[typeof(TestAggregate.Commands.DeleteCommand)].Authorize;
    }

    private static readonly TestAggregate.Commands.DeleteCommand Cmd = new(1);

    // ── Permission ─────────────────────────────────────────────────────────────

    [Fact]
    public void RequirePermission_allows_when_granted()
    {
        var gate = Gate(new PermissionConfig(), wireAuthz: true)!;
        gate(Principal(true, permissions: ["orders.edit"]), Cmd).Should().BeTrue();
    }

    [Fact]
    public void RequirePermission_denies_when_missing()
    {
        var gate = Gate(new PermissionConfig(), wireAuthz: true)!;
        gate(Principal(true), Cmd).Should().BeFalse();
    }

    [Fact]
    public void RequirePermission_fails_closed_without_evaluator()
    {
        var gate = Gate(new PermissionConfig(), wireAuthz: false)!;
        gate.Should().NotBeNull();
        gate(Principal(true, permissions: ["orders.edit"]), Cmd).Should().BeFalse();
    }

    // ── The security fix: PolicyName / TypeRef now enforce (deny) ────────────────

    [Fact]
    public void RequireAuthorization_policyName_now_denies_instead_of_silently_allowing()
    {
        var gate = Gate(new PolicyNameConfig(), wireAuthz: true);
        gate.Should().NotBeNull("PolicyName must no longer compile to a null (unenforced) predicate");
        gate!(Principal(true), Cmd).Should().BeFalse();
    }

    [Fact]
    public void Authorize_typeRef_now_denies_instead_of_silently_allowing()
    {
        var gate = Gate(new TypeRefConfig(), wireAuthz: true);
        gate.Should().NotBeNull();
        gate!(Principal(true), Cmd).Should().BeFalse();
    }

    [Fact]
    public void PolicyName_fails_closed_without_evaluator()
    {
        var gate = Gate(new PolicyNameConfig(), wireAuthz: false);
        gate.Should().NotBeNull();
        gate!(Principal(true), Cmd).Should().BeFalse();
    }

    // ── Unchanged behaviours ─────────────────────────────────────────────────────

    [Fact]
    public void RequireAuthenticated_unchanged()
    {
        var gate = Gate(new AuthenticatedConfig(), wireAuthz: true)!;
        gate(Principal(true), Cmd).Should().BeTrue();
        gate(Principal(false), Cmd).Should().BeFalse();
    }

    [Fact]
    public void RequireRoles_valueobject_overload_works()
    {
        var gate = Gate(new RolesValueObjectConfig(), wireAuthz: true)!;
        gate(Principal(true, roles: ["Manager"]), Cmd).Should().BeTrue();
        gate(Principal(true, roles: ["Clerk"]), Cmd).Should().BeFalse();
    }
}
