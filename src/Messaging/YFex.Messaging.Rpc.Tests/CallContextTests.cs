using ActualLab.Fusion;
using ActualLab.Rpc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Cqrs;
using YFex.Messaging;
using YFex.Persistence;
using YFex.Tenancy;
using CreateCustomer = YFex.Messaging.Rpc.Tests.ShopAggregate.Commands.CreateCustomer;
using SearchCustomers = YFex.Messaging.Rpc.Tests.ShopAggregate.Queries.SearchCustomers;
using WhoAmI = YFex.Messaging.Rpc.Tests.ShopAggregate.Queries.WhoAmI;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>Per-call token (and tenant) headers on a raw proxy call — what a client configured with <c>SendToken</c> sends.</summary>
internal static class As
{
    public static Task<T> Caller<T>(string? subject, ScopeIdentity tenant, Func<Task<T>> call)
    {
        RpcHeader[]? headers = null;
        if (subject is not null) headers = headers.With(new RpcHeader(CallContextHeaders.Authorization, "Bearer " + Users.Token(subject)));
        if (!tenant.IsEmpty) headers = headers.With(new RpcHeader(CallContextHeaders.Scope, tenant.ToString()));
        using (new RpcOutboundCallSetup { Headers = headers }.Activate())
            return call();
    }

    public static readonly CreateCustomer ValidCustomer = new("Ana", "ana@shop.test", "12345678901");
}

/// <summary>
/// D29 — DEFAULT connection-bound call context: the identity + tenant captured from the WebSocket upgrade request flow to
/// every call implicitly (no field on any message, no parameter on any handler).
/// </summary>
public class ConnectionCallContextTests(RpcFixture fx) : IClassFixture<RpcFixture>
{
    [Fact]
    public async Task Connection_identity_and_tenant_reach_the_handler_implicitly()
    {
        var r = await fx.Contract.WhoAmIAsync(new WhoAmI(1));

        r.OkValue.Should().Be("alice@acme");   // IPrincipalAccessor (decorated) + YFex.Tenancy effective scope
    }

    [Fact]
    public async Task FusionMessageBus_round_trips_with_the_connection_identity()
    {
        var bus = fx.CreateClientBus();

        (await bus.QueryAsync<WhoAmI, string>(new WhoAmI(2))).TryGetValue(out string? who).Should().BeTrue();
        who.Should().Be("alice@acme");
        (await bus.CommandAsync<CreateCustomer, int>(As.ValidCustomer)).OkValue.Should().Be(42);
    }

    [Fact]
    public async Task Header_is_ignored_when_the_connection_is_authenticated()
    {
        // FromConnection().OrFromHeader(): the authenticated connection wins; a header can't impersonate someone else.
        var r = await As.Caller("mallory", default, () => fx.Contract.WhoAmIAsync(new WhoAmI(3)));

        r.OkValue.Should().Be("alice@acme");
    }

    [Fact]
    public async Task Permissions_are_resolved_once_per_version()
    {
        await fx.Contract.CreateCustomerAsync(As.ValidCustomer);   // warm: identity resolved for this connection
        int loads = fx.Permissions.LoadsOf("alice");

        for (int i = 0; i < 3; i++)
            (await fx.Contract.CreateCustomerAsync(As.ValidCustomer)).OkValue.Should().Be(42);
        fx.Permissions.LoadsOf("alice").Should().Be(loads, "the versioned per-connection auth context is reused");

        fx.Signals.PermissionsChanged("alice");
        (await fx.Contract.CreateCustomerAsync(As.ValidCustomer)).OkValue.Should().Be(42);
        fx.Permissions.LoadsOf("alice").Should().Be(loads + 1, "a version bump reloads exactly once");
    }
}

/// <summary>
/// D29 — anonymous connection + per-call token header (<c>OrFromHeader</c>): server-side stage 2, tenant from the client
/// (validated), and compute-cache isolation per tenant/user.
/// </summary>
public class PerCallHeaderTests(AnonymousConnectionFixture fx) : IClassFixture<AnonymousConnectionFixture>
{
    private static readonly CreateCustomer TakenEmail = As.ValidCustomer with { Email = EmailNotRegistered.Taken };

    [Fact]
    public async Task Anonymous_caller_gets_Unauthorized_over_the_wire_and_context_rules_do_not_run()
    {
        // Raw proxy = a client that skipped every client-side check: the server alone decides (client never trusted).
        int calls = fx.Handler.Calls;

        var r = await fx.Contract.CreateCustomerAsync(TakenEmail);

        r.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.Unauthenticated, "stage 2 runs before the context rule (stage 5) that would report 'email taken'");
        fx.Handler.Calls.Should().Be(calls);
    }

    [Fact]
    public async Task Stage2_runs_outside_the_compute_cache()
    {
        // mallory and alice are both in acme and SearchCustomers is Global → SAME compute-cache variant: only the per-call
        // authorization in CallContextMiddleware separates them (inside the compute method it would be cached).
        Task<Result<List<string>>> Search(string subject, int take)
            => As.Caller(subject, Users.Acme, () => fx.Contract.SearchCustomersAsync(new SearchCustomers("shared", take)));

        (await Search("mallory", 5)).TryGetValue(out Error denied).Should().BeTrue();
        denied.Kind.Should().Be(ErrorKind.Forbidden);
        (await Search("alice", 5)).OkValue.Should().Equal(new[] { "shared:5" }, "the rejection was never cached");

        (await Search("alice", 6)).OkValue.Should().Equal("shared:6");
        (await Search("mallory", 6)).TryGetValue(out Error leaked).Should().BeTrue("alice's cached value must not leak");
        leaked.Kind.Should().Be(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Shape_is_checked_before_authorization()
    {
        var r = await fx.Contract.CreateCustomerAsync(new CreateCustomer("", "a@b.co", "12345678901"));

        r.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.ValidationProblem);   // stage 1 → 2: a malformed message never reaches the authorizer
    }

    [Fact]
    public async Task Caller_without_the_permission_is_Unauthorized_and_one_with_it_passes()
    {
        var denied = await As.Caller("mallory", Users.Acme, () => fx.Contract.CreateCustomerAsync(As.ValidCustomer));
        var allowed = await As.Caller("alice", Users.Acme, () => fx.Contract.CreateCustomerAsync(As.ValidCustomer));

        denied.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.Forbidden);
        allowed.OkValue.Should().Be(42);
    }

    [Fact]
    public async Task Invalid_token_runs_as_anonymous()
    {
        var r = await As.Caller("nobody", default, () => fx.Contract.WhoAmIAsync(new WhoAmI(10)));

        r.TryGetValue(out Error e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.Unauthenticated);
    }

    [Fact]
    public async Task Tenant_flows_from_the_client_only_when_the_validator_grants_it()
    {
        var granted = await As.Caller("bob", Users.Globex, () => fx.Contract.WhoAmIAsync(new WhoAmI(11)));
        var forged = await As.Caller("bob", Users.Acme, () => fx.Contract.WhoAmIAsync(new WhoAmI(11)));

        granted.OkValue.Should().Be("bob@globex");
        forged.TryGetValue(out Error e).Should().BeTrue();   // bob isn't in acme → token rejected → anonymous
        e.Kind.Should().Be(ErrorKind.Unauthenticated);
    }

    [Fact]
    public async Task Compute_cache_is_keyed_per_tenant_and_user()
    {
        var query = new WhoAmI(12);
        int calls = fx.WhoAmI.Calls;

        var alice = await As.Caller("alice", Users.Acme, () => fx.Contract.WhoAmIAsync(query));
        var bob = await As.Caller("bob", Users.Globex, () => fx.Contract.WhoAmIAsync(query));
        var aliceAgain = await As.Caller("alice", Users.Acme, () => fx.Contract.WhoAmIAsync(query));

        alice.OkValue.Should().Be("alice@acme");
        bob.OkValue.Should().Be("bob@globex", "same arguments, other caller: never alice's cached value");
        aliceAgain.OkValue.Should().Be("alice@acme");
        fx.WhoAmI.Calls.Should().Be(calls + 2, "alice's second call is served by her own cached computed");
    }

    [Fact]
    public async Task FusionMessageBus_sends_the_token_and_tenant_headers()
    {
        var callContext = new CallContextClientBuilder()
            .SendToken((_, _) => new ValueTask<string?>(Users.Token("bob")))
            .SendTenant();
        var clientTenant = new UpgradeRequestServices(Users.Globex);   // the client app's ambient IScopeContext
        var services = new ServiceOverlay(fx.ClientServices, (typeof(CallContextClientBuilder), callContext),
            (typeof(IScopeContext), clientTenant));
        var bus = fx.CreateClientBus(services: services);

        (await bus.QueryAsync<WhoAmI, string>(new WhoAmI(13))).TryGetValue(out string? who).Should().BeTrue();
        who.Should().Be("bob@globex");
        (await bus.CommandAsync<CreateCustomer, int>(As.ValidCustomer)).OkValue.Should().Be(42);
    }

    [Fact]
    public async Task FusionMessageBus_sends_extra_headers_without_taking_the_token_slot()
    {
        int calls = 0;
        var callContext = new CallContextClientBuilder()
            .SendToken((_, _) => new ValueTask<string?>(Users.Token("bob")))
            .SendTenant()
            .SendHeader("yfex-installation", (_, _) => { Interlocked.Increment(ref calls); return new ValueTask<string?>("YFex-Installation abc"); })
            .SendHeader("yfex-empty", (_, _) => new ValueTask<string?>((string?)null));
        var services = new ServiceOverlay(fx.ClientServices, (typeof(CallContextClientBuilder), callContext),
            (typeof(IScopeContext), new UpgradeRequestServices(Users.Globex)));
        var bus = fx.CreateClientBus(services: services);

        (await bus.QueryAsync<WhoAmI, string>(new WhoAmI(14))).TryGetValue(out string? who).Should().BeTrue();

        who.Should().Be("bob@globex", "the token and the tenant still flow");
        calls.Should().Be(1, "the provider is called per call");
        FluentActions.Invoking(() => new CallContextClientBuilder().SendHeader(CallContextHeaders.Authorization, (_, _) => default))
            .Should().Throw<ArgumentException>("reserved");
        FluentActions.Invoking(() => callContext.SendHeader("YFEX-INSTALLATION", (_, _) => default)).Should().Throw<ArgumentException>("twice");
    }
}

/// <summary>D29 — permission changes: versioned reload on the server + client refresh through the ICallPermissions compute method.</summary>
public class PermissionChangeTests(CarolFixture fx) : IClassFixture<CarolFixture>
{
    [Fact]
    public async Task Permission_change_reloads_on_the_next_call_and_refreshes_the_client()
    {
        var permissions = fx.ClientServices.GetRequiredService<ICallPermissions>();
        var computed = await Computed.TryCapture(() => permissions.GetMyPermissionsAsync());
        computed.Should().NotBeNull();
        computed!.Value.Subject.Should().Be("carol");
        computed.Value.Has(ShopPermissions.CreateCustomer).Should().BeTrue();
        (await fx.Contract.CreateCustomerAsync(As.ValidCustomer)).OkValue.Should().Be(42);

        // The store changes, but nobody signalled it: the server keeps the cached grants (reload only on version change).
        fx.Permissions.Revoke("carol", ShopPermissions.CreateCustomer);
        (await fx.Contract.CreateCustomerAsync(As.ValidCustomer)).OkValue.Should().Be(42);

        fx.Signals.PermissionsChanged("carol");

        await computed.WhenInvalidated().WaitAsync(TimeSpan.FromSeconds(10));
        computed = (Computed<PermissionSnapshot>)await computed.Update();
        computed.Value.Has(ShopPermissions.CreateCustomer).Should().BeFalse("the client UI refreshes from the pushed invalidation");
        computed.Value.Has(ShopPermissions.SearchCustomers).Should().BeTrue();
        (await fx.Contract.CreateCustomerAsync(As.ValidCustomer)).TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.Forbidden);

        fx.Permissions.Grant("carol", ShopPermissions.CreateCustomer);
        fx.Signals.PermissionsChanged("carol");
        (await fx.Contract.CreateCustomerAsync(As.ValidCustomer)).OkValue.Should().Be(42);
    }

    [Fact]
    public async Task A_client_cannot_read_someone_elses_permissions()
    {
        var permissions = fx.ClientServices.GetRequiredService<ICallPermissions>();

        var spoofed = await permissions.GetMyPermissionsAsync("|alice");   // the argument is overwritten by the server

        spoofed.Subject.Should().Be("carol");
    }
}

/// <summary>D29 — logout / revocation closes the user's connections; the reconnect is authenticated again.</summary>
public class RevocationTests(ErinFixture fx) : IClassFixture<ErinFixture>
{
    [Fact]
    public async Task Revocation_closes_the_connection_and_the_reconnect_is_authenticated_again()
    {
        (await fx.Contract.CreateCustomerAsync(As.ValidCustomer)).OkValue.Should().Be(42);
        var connections = fx.ServerServices.GetRequiredService<CallContextConnections>();
        connections.CountFor("erin").Should().Be(1);

        fx.ConnectionUser = Users.Anonymous;           // the identity provider revoked erin's token
        await fx.Signals.RevokedAsync("erin").AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        connections.CountFor("erin").Should().Be(0);
        var r = await fx.Contract.CreateCustomerAsync(As.ValidCustomer).WaitAsync(TimeSpan.FromSeconds(20));
        r.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.Unauthenticated);
    }
}

/// <summary>D29 — FusionMessageBus runs stage 2 client-side (UX): a denied command never leaves the client.</summary>
public class ClientSideAuthorizationTests
{
    [Fact]
    public async Task Denied_command_is_not_sent_to_the_server()
    {
        var provider = new TestPermissionSource().Grant("alice", ShopPermissions.CreateCustomer);
        var user = new FixedPrincipalAccessor(CallContext.Anonymous);
        var registry = CompiledMessagingRegistry.Build([new ShopBaseline()]);
        var services = new ServiceCollection()
            .AddYFexAuthorization(a => a.AddPermissionSource(provider))
            .AddSingleton(registry)
            .AddYFexCqrsAuthorizationConfiguration()
            .AddSingleton<IPrincipalAccessor>(user)
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IAuthorizationContextFactory>();
        int sent = 0;
        var bus = new FusionMessageBusBuilder()
            .AddCommand<CreateCustomer, int>((_, _) => { sent++; return Task.FromResult(QueueableResult<int>.Ok(1)); })
            .Build(
                registry,
                new AlwaysConnected(), new InMemoryCache(), new InMemoryOutbox(new OutboxOptions()), new DefaultEventBus(), services);

        var denied = await bus.CommandAsync<CreateCustomer, int>(As.ValidCustomer);
        user.Current = factory.Create(Users.Principal("alice"));
        var allowed = await bus.CommandAsync<CreateCustomer, int>(As.ValidCustomer);

        denied.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.Unauthenticated);
        allowed.OkValue.Should().Be(1);
        sent.Should().Be(1);
    }

    private sealed class AlwaysConnected : IServerConnection
    {
        public ServerConnectionState State => ServerConnectionState.Connected;
        public event Action<ServerConnectionChange>? Changed { add { } remove { } }
    }
}

/// <summary>Serves a few extra services over an existing provider (per-test client configuration).</summary>
internal sealed class ServiceOverlay(IServiceProvider inner, params (Type Type, object Instance)[] extra) : IServiceProvider
{
    public object? GetService(Type serviceType)
    {
        foreach (var (type, instance) in extra)
            if (type == serviceType) return instance;
        return inner.GetService(serviceType);
    }
}
