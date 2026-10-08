using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Cqrs;
using CreateCustomer = YFex.Messaging.Rpc.Tests.ShopAggregate.Commands.CreateCustomer;
using WhoAmI = YFex.Messaging.Rpc.Tests.ShopAggregate.Queries.WhoAmI;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>Frank@acme — own fixture: the tests edit his grants and the client feed follows them.</summary>
public sealed class FrankFixture : RpcFixture
{
    public FrankFixture() : base("frank", Users.Acme)
        => Permissions.Grant("frank", ShopPermissions.CreateCustomer, ShopPermissions.SearchCustomers);
}

/// <summary>
/// Authorization-consolidation Phase 3a: the server snapshot drives the client (05 §3), signals live in YFex.Authorization
/// (05 §4), and the RPC call context writes the actor (06 §4).
/// </summary>
public class ServerPermissionsTests(FrankFixture fx) : IClassFixture<FrankFixture>
{
    private static async Task Eventually(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Client_sees_the_server_grants_and_follows_a_signalled_change()
    {
        var accessor = new SnapshotPrincipalAccessor();
        var feed = new ServerPermissionsFeed(fx.ClientServices.GetRequiredService<ICallPermissions>(), accessor)
        {
            RetryDelay = TimeSpan.FromMilliseconds(50),
        };
        await feed.StartAsync(default);
        try
        {
            await Eventually(() => accessor.Snapshot.Subject == "frank", "the first snapshot arrives");
            accessor.Current.HasPermission(ShopPermissions.CreateCustomer).Should().BeTrue();
            accessor.Snapshot.Scope.Should().NotBeNullOrEmpty("the snapshot names the tenant the grants were resolved in");

            // The client bus quick-checks with the server's grants (UX), the server re-checks (authoritative).
            var bus = fx.CreateClientBus(services: new ServiceOverlay(fx.ClientServices, (typeof(IPrincipalAccessor), accessor)));
            (await bus.CommandAsync<CreateCustomer, int>(As.ValidCustomer)).OkValue.Should().Be(42);

            fx.Permissions.Revoke("frank", ShopPermissions.CreateCustomer);
            fx.Signals.PermissionsChanged("frank");

            await Eventually(() => !accessor.Current.HasPermission(ShopPermissions.CreateCustomer), "the pushed invalidation refreshes the client");
            accessor.Current.HasPermission(ShopPermissions.SearchCustomers).Should().BeTrue();
            accessor.Snapshot.Version.Should().BeGreaterThan(0);
            var denied = await bus.CommandAsync<CreateCustomer, int>(As.ValidCustomer);
            denied.TryGetError(out var error).Should().BeTrue();
            error.Kind.Should().Be(ErrorKind.Forbidden, "the client blocks it without a round trip");
        }
        finally
        {
            await feed.StopAsync(default);
            accessor.Clear();
            fx.Permissions.Grant("frank", ShopPermissions.CreateCustomer);
            fx.Signals.PermissionsChanged("frank");
        }
    }

    [Fact]
    public async Task The_handler_runs_inside_the_callers_actor_scope()
    {
        (await fx.Contract.WhoAmIAsync(new WhoAmI(100))).OkValue.Should().Be("frank@acme");

        fx.WhoAmI.LastActor!.Actor.Subject.Should().Be("frank");
        fx.WhoAmI.LastActor.Actor.HomeScope.Should().NotBeNull("the actor carries the scope it was resolved in");
        fx.WhoAmI.LastActor.Relation.Should().Be(ActorRelation.None);
    }

    [Fact]
    public void Snapshot_round_trips_through_the_wire_formatter()
    {
        var snapshot = new PermissionSnapshot("frank", true, "0=acme", 9, ["shop.customers.create", "a.first"], ["Caixa"],
            ActorRelation.TenantImpersonation);

        var bytes = MemoryPackSerializer.Serialize(snapshot);
        var back = MemoryPackSerializer.Deserialize<PermissionSnapshot>(bytes);

        back.Should().Be(snapshot);
        back!.Permissions.Should().Equal("a.first", "shop.customers.create");
        MemoryPackSerializer.Deserialize<PermissionSnapshot>(MemoryPackSerializer.Serialize(PermissionSnapshot.Anonymous))
            .Should().Be(PermissionSnapshot.Anonymous);
    }

    [Fact]
    public void UseServerPermissions_makes_the_snapshot_the_client_caller()
    {
        var services = new ServiceCollection();
        services.AddYFexAuthorization();
        services.UseServerPermissions();
        services.UseServerPermissions();   // idempotent

        services.Count(static d => d.ServiceType == typeof(IPrincipalAccessor)).Should().Be(1);
        services.Count(static d => d.ServiceType == typeof(ServerPermissionsFeed)).Should().Be(1);
        services.Should().Contain(static d => d.ServiceType == typeof(IHostedService));
        services.Should().Contain(static d => d.ServiceType == typeof(IAuthorizationSignals));
    }

    private sealed class HostAccessor : IPrincipalAccessor
    {
        public IAuthorizationContext Current { get; } = new AuthorizationContext(Users.Principal("host"), PermissionSet.Empty);
    }

    [Fact]
    public void Call_context_decorator_wraps_a_type_registration_without_reflection()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPrincipalAccessor, HostAccessor>();
        CallContextPrincipalAccessor.Decorate(services);

        using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IPrincipalAccessor>();

        accessor.Should().BeOfType<CallContextPrincipalAccessor>();
        accessor.Current.Actor.Subject.Should().Be("host", "outside an RPC call the host accessor answers");
        provider.GetRequiredService<HostAccessor>().Should().NotBeNull("the concrete type stays a container registration");
    }
}
