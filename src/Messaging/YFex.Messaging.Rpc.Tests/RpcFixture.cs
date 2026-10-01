using System.Security.Claims;
using ActualLab.Channels;
using ActualLab.Collections;
using ActualLab.Fusion;
using ActualLab.Rpc;
using ActualLab.Rpc.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Cqrs;
using YFex.Messaging;
using YFex.Persistence;
using YFex.Tenancy;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>
/// Two DI containers = two RpcHubs, like a real client and server. The client's GENERATED <see cref="IShopAggregateRpcClient"/>
/// proxy calls the server's GENERATED <see cref="ShopAggregateRpcContractImpl"/> (registered by the generated
/// <c>AddShopAggregateRpcServer</c>) over an in-memory channel pair, so every argument and result is serialized with the
/// real wire format (MemoryPack <c>mempack6</c>, the Fusion 13 default). Every Fusion proxy involved is emitted by the CQRS
/// generator (validation D30) — the project has no hand-written contract.
/// </summary>
/// <remarks>
/// Call context (D29): each connection is established through the server's real <see cref="RpcPeerOptions.ServerConnectionFactory"/>
/// with a fake WebSocket upgrade <see cref="HttpContext"/> (user = <see cref="ConnectionUser"/>, host-resolved tenant =
/// <see cref="ConnectionTenant"/>), exactly what ActualLab's WebSocket server passes. Server config is the D29 syntax:
/// <c>ctx.FromConnection().OrFromHeader().WithTenant()</c>. Default connection: alice@acme with both shop permissions.
/// </remarks>
public class RpcFixture : IAsyncDisposable
{
    private readonly ServiceProvider _server;
    private readonly ServiceProvider _client;

    public RpcFixture() : this("alice", Users.Acme) { }

    protected RpcFixture(string? connectionSubject, ScopeIdentity connectionTenant)
    {
        ConnectionUser = () => connectionSubject is null ? Users.Anonymous() : Users.Principal(connectionSubject);
        ConnectionTenant = connectionTenant;
        Permissions
            .Grant("alice", ShopPermissions.CreateCustomer, ShopPermissions.SearchCustomers)
            .Grant("bob", ShopPermissions.CreateCustomer, ShopPermissions.SearchCustomers);

        // ── Server ──
        var server = new ServiceCollection();
        server.AddLogging(b => b.AddProvider(Logs).SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug));
        server.AddYFexAuthorization(a => a.UsePermissionProvider(Permissions));
        server.AddSingleton<ICallTokenValidator>(Tokens);
        // Registered before UseYFexMessagingRpcServer, whose TryAdds keep these.
        server.AddSingleton(sp => CompiledMessagingRegistry.Build(
            baseline: [new ShopBaseline()], serverOverrides: [new ShopServer()],
            policyEvaluator: sp.GetRequiredService<IPolicyEvaluator>(),
            principalFactory: p => CallAuthorization.ContextFor(p, sp.GetRequiredService<IAuthContextFactory>())));
        server.AddSingleton(new LocalHandlerInvokerRegistryBuilder()
            .AddCommand<ShopAggregate.Commands.CreateCustomer, int>()
            .AddQuery<ShopAggregate.Queries.SearchCustomers, List<string>>()
            .AddQuery<ShopAggregate.Queries.WhoAmI, string>()
            .Build());
        server.AddSingleton(Handler);
        server.AddSingleton<ICommandHandler<ShopAggregate.Commands.CreateCustomer, int>>(Handler);
        server.AddSingleton<IQueryHandler<ShopAggregate.Queries.SearchCustomers, List<string>>, SearchCustomersHandler>();
        server.AddSingleton<WhoAmIHandler>();
        server.AddSingleton<IQueryHandler<ShopAggregate.Queries.WhoAmI, string>>(sp => sp.GetRequiredService<WhoAmIHandler>());
        // D25 validation decorator + D29 call context (the approved syntax).
        server.UseYFexMessagingRpcServer(rpc => rpc.CallContext(ctx => ctx.FromConnection().OrFromHeader("Authorization").WithTenant()));
        server.AddFusion().AddShopAggregateRpcServer();
        _server = server.BuildServiceProvider();

        // ── Client ──
        var client = new ServiceCollection();
        client.AddLogging(b => b.AddProvider(Logs).SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug));
        var fusion = client.AddFusion();
        fusion.Rpc.AddShopAggregateRpcRemoteClient();   // generated client view under the contract's wire name
        fusion.AddClient<ICallPermissions>();            // what AddYFexMessagingRpcClient registers (permission refresh)
        client.AddYFexAuthorization(a => a.UsePermissionProvider(Permissions));
        client.AddSingleton<IPrincipalAccessor>(ClientUser);
        var serverHub = _server.GetRequiredService<RpcHub>();
        client.AddSingleton<RpcClient>(c => new InMemoryBridgeClient(c, serverHub, this));
        _client = client.BuildServiceProvider();

        ClientUser.Current = _client.GetRequiredService<IAuthContextFactory>().Create(Users.Principal("alice"));
        Contract = _client.GetRequiredService<IShopAggregateRpcClient>();
    }

    public CreateCustomerHandler Handler { get; } = new();
    public CaptureLoggerProvider Logs { get; } = new();
    public TestPermissionProvider Permissions { get; } = new();
    public TestTokenValidator Tokens { get; } = new();
    public IServiceProvider ServerServices => _server;
    public IServiceProvider ClientServices => _client;
    public IShopAggregateRpcClient Contract { get; }

    /// <summary>Principal of the NEXT connection (the WebSocket upgrade request's authenticated user).</summary>
    public Func<ClaimsPrincipal> ConnectionUser { get; set; }

    /// <summary>Tenant the host resolves for the next upgrade request.</summary>
    public ScopeIdentity ConnectionTenant { get; set; }

    /// <summary>The client app's logged-in user (UX-only stage 2 in <see cref="FusionMessageBus"/>).</summary>
    public FixedPrincipalAccessor ClientUser { get; } = new(CallContext.Anonymous);

    public CallContextSignals Signals => _server.GetRequiredService<CallContextSignals>();
    public WhoAmIHandler WhoAmI => _server.GetRequiredService<WhoAmIHandler>();

    /// <summary>Client-side dispatcher over the GENERATED routes (what an app calls).</summary>
    public FusionMessageBus CreateClientBus(bool connected = true, InMemoryOutbox? outbox = null, IServiceProvider? services = null)
    {
        var builder = new FusionMessageBusBuilder();
        ShopAggregateRpcClientRegistration.RegisterRoutes(builder, Contract);   // generated routes (typed results, D26)
        var sp = services ?? _client;
        var factory = sp.GetRequiredService<IAuthContextFactory>();
        return builder.Build(
            CompiledMessagingRegistry.Build(baseline: [new ShopBaseline()],
                policyEvaluator: sp.GetRequiredService<IPolicyEvaluator>(),
                principalFactory: p => CallAuthorization.ContextFor(p, factory)),
            new TestNetworkStatus(connected),
            new InMemoryCache(),
            outbox ?? new InMemoryOutbox(new OutboxOptions()),
            new DefaultEventBus(),
            sp);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    /// <summary>
    /// Connects each client peer to a server peer of the other hub through a twisted in-memory channel pair — via the
    /// server's ServerConnectionFactory with a fake upgrade request, like ActualLab's RpcWebSocketServer does.
    /// </summary>
    private sealed class InMemoryBridgeClient(IServiceProvider services, RpcHub serverHub, RpcFixture fixture) : RpcClient(services)
    {
        public override async Task<RpcConnection> ConnectRemote(RpcClientPeer clientPeer,
            RpcPeerConnectionState connectionState, CancellationToken cancellationToken)
        {
            var serverPeer = serverHub.GetServerPeer(RpcPeerRef.NewServer(
                clientPeer.ClientId, clientPeer.SerializationFormat.Key, clientPeer.Ref.IsBackend));
            var channels = ChannelPair.CreateTwisted<ArrayOwner<byte>>(LocalChannelOptions);
            var upgrade = new DefaultHttpContext
            {
                User = fixture.ConnectionUser(),
                RequestServices = new UpgradeRequestServices(fixture.ConnectionTenant),
            };
            var properties = PropertyBag.Empty.KeylessSet((RpcPeer)serverPeer).KeylessSet<HttpContext>(upgrade);
            var peerOptions = serverHub.Services.GetRequiredService<RpcPeerOptions>();
            var serverConnection = await peerOptions.ServerConnectionFactory(serverPeer,
                new RpcSimpleChannelTransport(serverPeer, channels.Channel2), properties, cancellationToken).ConfigureAwait(false);
            await serverPeer.SetNextConnection(serverConnection, cancellationToken).ConfigureAwait(false);
            return new RpcConnection(new RpcSimpleChannelTransport(clientPeer, channels.Channel1));
        }
    }

    private sealed class TestNetworkStatus(bool connected) : INetworkStatus
    {
        public bool IsConnected => connected;
        public SyncState State => connected ? SyncState.Connected : SyncState.Disconnected;
        public event Action<SyncState>? Changed { add { } remove { } }
    }
}

/// <summary>Anonymous connection (no upgrade-request user, no tenant): identities come only from per-call token headers.</summary>
public sealed class AnonymousConnectionFixture() : RpcFixture(null, ScopeIdentity.None);

/// <summary>Carol@acme — for permission-change tests (own fixture: they mutate grants).</summary>
public sealed class CarolFixture : RpcFixture
{
    public CarolFixture() : base("carol", Users.Acme)
        => Permissions.Grant("carol", ShopPermissions.CreateCustomer, ShopPermissions.SearchCustomers);
}

/// <summary>Erin@acme — for revocation (own fixture: it closes the connection).</summary>
public sealed class ErinFixture : RpcFixture
{
    public ErinFixture() : base("erin", Users.Acme)
        => Permissions.Grant("erin", ShopPermissions.CreateCustomer, ShopPermissions.SearchCustomers);
}
