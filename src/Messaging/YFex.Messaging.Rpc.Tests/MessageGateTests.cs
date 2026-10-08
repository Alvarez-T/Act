using YFex.Cqrs;
using static YFex.Messaging.Rpc.Tests.ShopAggregate.Commands;
using static YFex.Messaging.Rpc.Tests.ShopAggregate.Queries;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>
/// D1 over a REAL RPC round-trip (authorization-consolidation 04 §2, Phase 2a): a requirement declared through the Cqrs
/// configuration, through a server <c>ForMessage&lt;T&gt;()</c> row, or only through <c>[RequirePermission]</c> on the record is
/// enforced by the server gate — the client contract is called directly, so no client-side check can mask the server.
/// </summary>
public class MessageGateTests(GateFixture fx) : IClassFixture<GateFixture>
{
    private static void ShouldBeDenied<T>(QueueableResult<T> result, string naming)
    {
        result.TryGetError(out var error).Should().BeTrue();
        error.Kind.Should().Be(ErrorKind.Forbidden);
        error.Message.Should().Contain(naming);
    }

    [Fact]
    public async Task Forbidden_with_the_missing_permission_round_trips_over_rpc()
    {
        var denied = await As.Caller("mallory", Users.Acme, () => fx.Contract.ArchiveCustomerAsync(new ArchiveCustomer(1)));
        denied.TryGetError(out var error).Should().BeTrue();
        error.TryGetValue(out Forbidden forbidden).Should().BeTrue();
        forbidden.Denial.Kind.Should().Be(YFex.Authorization.Public.DenialKind.MissingPermission);
        forbidden.Denial.Missing.Should().Be(ShopPermissions.ArchiveCustomers);
        YFex.Authorization.Public.PermissionCatalogs.DisplayText(forbidden.Denial.Missing.Key).Should().Be("Arquivar clientes");
    }

    [Fact]
    public async Task Unauthenticated_round_trips_over_rpc()
    {
        var anonymous = await fx.Contract.ArchiveCustomerAsync(new ArchiveCustomer(1));   // no token on an anonymous connection
        anonymous.TryGetError(out var error).Should().BeTrue();
        error.Kind.Should().Be(ErrorKind.Unauthenticated);
        error.TryGetValue(out Unauthenticated _).Should().BeTrue();
    }

    [Fact]
    public async Task Cqrs_configuration_requirement_is_enforced_by_the_server()
    {
        var denied = await As.Caller("mallory", Users.Acme, () => fx.Contract.CreateCustomerAsync(As.ValidCustomer));
        ShouldBeDenied(denied, "shop.customers.create");
        (await As.Caller("dave", Users.Acme, () => fx.Contract.CreateCustomerAsync(As.ValidCustomer))).OkValue.Should().Be(42);
    }

    [Fact]
    public async Task RequirePermission_attribute_on_a_record_is_enforced_by_the_server()
    {
        var denied = await As.Caller("mallory", Users.Acme, () => fx.Contract.ArchiveCustomerAsync(new ArchiveCustomer(7)));
        ShouldBeDenied(denied, "shop.customers.archive");
        (await As.Caller("dave", Users.Acme, () => fx.Contract.ArchiveCustomerAsync(new ArchiveCustomer(7)))).OkValue.Should().Be(7);
    }

    [Fact]
    public async Task ForMessage_row_is_enforced_by_the_server()
    {
        var denied = await As.Caller("mallory", Users.Acme, () => fx.Contract.ExportCustomersAsync(new ExportCustomers(5)));
        denied.TryGetValue(out YFex.Cqrs.Error error).Should().BeTrue();
        error.Kind.Should().Be(ErrorKind.Forbidden);
        error.Message.Should().Contain("shop.customers.export");
        (await As.Caller("dave", Users.Acme, () => fx.Contract.ExportCustomersAsync(new ExportCustomers(5)))).OkValue.Should().Be(5);
    }
}
