using ActualLab.Fusion;
using ActualLab.Rpc;
using ActualLab.Rpc.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging;
using YFex.Messaging.Fusion;

using YFex.Persistence;

namespace YFex.Messaging.Rpc;

/// <summary>DI registration helpers for <c>YFex.Messaging.Rpc</c>.</summary>
public static class RpcMessagingExtensions
{
    // ── Client ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers the full YFex client-side Rpc stack:
    /// Fusion client, ICache, IOutbox, ISyncFailureLog, SyncStatus,
    /// FusionMessageBus (IDispatcher), OutboxReplayer, and FusionServerConnection (IServerConnection).
    /// </summary>
    public static IServiceCollection AddYFexMessagingRpcClient(
        this IServiceCollection services,
        Action<YFexMessagingRpcClientOptions>? configure = null)
    {
        var opts = new YFexMessagingRpcClientOptions();
        configure?.Invoke(opts);
        services.AddSingleton(opts);
        services.AddSingleton(opts.OutboxOptions);

        // Fusion core + WebSocket client
        var fusion = services.AddFusion();
        fusion.Rpc.AddWebSocketClient(opts.WebSocketEndpoint);

        // Call context (D29): the caller's permissions pushed by the server (UX refresh) + optional per-call headers.
        fusion.AddClient<ICallPermissions>();
        if (opts.CallContextBuilder is { } callContext) services.AddSingleton(callContext);

        // Central anchor for the trusted clock (time T2): AddYFexTime(time => time.UseCentralAnchor()) samples it.
        fusion.Rpc.AddClient<ICentralTime>();

        // Live-state factory backed by Fusion
        services.AddSingleton<ILiveStateFactory>(sp =>
        {
            var stateFactory = sp.GetRequiredService<StateFactory>();
            var factory = new FusionLiveStateFactory(stateFactory);
            LiveStateProvider.Configure(factory);
            return factory;
        });

        // Storage + cache (in-memory; Plan 4 overrides with persistent backends)
        services.TryAddSingleton<IKeyValueStore, MemoryKeyValueStore>();
        services.TryAddSingleton<ICache, InMemoryCache>();

        // Outbox + failure log (cross-wired after construction). Offline design F14: the durable pair over the registered
        // IKeyValueStore was never wired, so queued commands were lost on restart even with SQLite registered. Now:
        // Storage = InMemory, or an in-memory store → in-memory pair; otherwise (Auto/Sqlite/IndexedDb with a durable store)
        // → StorageBackedOutbox + StorageBackedSyncFailureLog. Offline design F4: the outbox records who queued each command.
        services.TryAddSingleton<IOutboxCallerCapture, DefaultOutboxCallerCapture>();
        services.AddSingleton(sp => OutboxPair.Create(sp, opts));
        // Offline Phase 3: observers (YFex.Offline's envelope custody) see every queued entry.
        services.AddSingleton<IOutbox>(sp => ObservedOutbox.Wrap(sp.GetRequiredService<OutboxPair>().Outbox, sp.GetServices<IOutboxEnqueueObserver>()));
        services.AddSingleton<ISyncFailureLog>(sp => sp.GetRequiredService<OutboxPair>().FailureLog);
        services.AddSingleton<IWritableSyncFailureLog>(sp => sp.GetRequiredService<OutboxPair>().FailureLog);

        // Bindable sync status singleton
        services.AddSingleton<SyncStatus>();

        // Our RPC connection to the server (networking ⚑N4; Fusion default client peer).
        services.AddSingleton<IServerConnection>(sp => new FusionServerConnection(sp.GetRequiredService<RpcHub>()));

        // Inner event bus + RPC-forwarding composite bus
        services.AddSingleton<DefaultEventBus>();
        services.AddSingleton<IEventBus>(sp =>
        {
            var local = sp.GetRequiredService<DefaultEventBus>();
            var remote = sp.GetRequiredService<IRemoteEventBus>();
            var bus = new RpcEventBus(local, remote);
            EventBusProvider.Configure(bus);
            opts.ConfigureEventBus?.Invoke(bus);
            bus.StartServerListener();
            return bus;
        });

        // FusionMessageBus builder — generator-emitted code calls AddQuery/AddCommand on this.
        // The caller must register CompiledMessagingRegistry (via AddYFexConfigurations or manually).
        services.TryAddSingleton<CompiledMessagingRegistry>(_ =>
            CompiledMessagingRegistry.Build([])); // empty fallback; generator registration overrides
        services.AddYFexCqrsAuthorizationConfiguration();   // Cqrs requirements → the one message policy (04 §2)
        services.AddSingleton<FusionMessageBusBuilder>();
        services.AddSingleton<IDispatcher>(sp =>
        {
            var builder = sp.GetRequiredService<FusionMessageBusBuilder>();
            var bus = builder.Build(
                sp.GetRequiredService<CompiledMessagingRegistry>(),
                sp.GetRequiredService<IServerConnection>(),
                sp.GetRequiredService<ICache>(),
                sp.GetRequiredService<IOutbox>(),
                sp.GetRequiredService<IEventBus>(),
                sp);
            YFexDispatcherProvider.Set(bus);
            return bus;
        });

        // Source-generated command replay map. Empty fallback so OutboxReplayer always resolves;
        // the generated AddYFexOutboxCommands() (call at startup) replaces this with the populated map.
        services.TryAddSingleton(OutboxCommandRegistry.Empty);

        // OutboxReplayer (hosted service)
        services.AddHostedService<OutboxReplayer>();

        return services;
    }

    // ── Server ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers server-side Fusion RPC infrastructure: Fusion core, IHandlerInvoker
    /// (defaults to <see cref="LocalHandlerInvoker"/>; override with Wolverine adapter),
    /// AlwaysConnectedServer, and ASP.NET endpoint mapping helpers.
    /// </summary>
    public static IServiceCollection UseYFexMessagingRpcServer(
        this IServiceCollection services,
        Action<YFexMessagingRpcServerOptions>? configure = null)
    {
        var opts = new YFexMessagingRpcServerOptions();
        configure?.Invoke(opts);
        services.AddSingleton(opts);

        var fusion = services.AddFusion();
        AddCallContextServer(services, fusion, opts.CallContextBuilder ?? new CallContextServerBuilder().FromConnection());

        // The central anchor of every client's trusted clock (time T2). Answers with the registered TimeProvider: central registers
        // AddYFexTime(time => time.TrustDeviceClock()); without it the reply is "unsynchronized" and no client anchors to it.
        // Signed replies (time T4) need AddCentralTimeSigning(); without it GetSignedTimeAsync answers "not signed".
        if (!services.Any(static d => d.ServiceType == typeof(CentralTimeService)))
        {
            services.AddSingleton(sp => new CentralTimeService(sp.GetService<TimeProvider>(), sp.GetService<CentralTimeSigner>()));
            fusion.Rpc.AddServer<ICentralTime, CentralTimeService>();
        }

        // Source-generated typed handler-invoke map. Empty fallback so LocalHandlerInvoker always
        // resolves; the generated AddYFexLocalHandlers() (call at startup) replaces this with the
        // populated map so handler resolution is reflection-free (no MakeGenericType).
        services.TryAddSingleton(LocalHandlerInvokerRegistry.Empty);

        // Default invoker resolves handlers from DI via the typed map; Wolverine adapter replaces this.
        services.TryAddSingleton<IHandlerInvoker, LocalHandlerInvoker>();

        // Validation stages 1/5 for every message entering the server through the invoker (D25).
        ValidatingHandlerInvoker.Decorate(services);

        // At-most-once command execution by idempotency key (offline design F3); ValidatingHandlerInvoker consults it first.
        // Replace IIdempotencyStore with a shared store for farms and restarts.
        services.TryAddSingleton<IIdempotencyStore>(_ => new InMemoryIdempotencyStore());
        services.TryAddSingleton(sp => new IdempotentExecution(
            sp.GetRequiredService<IIdempotencyStore>(), sp.GetRequiredService<CompiledMessagingRegistry>()));

        // Server is always "connected" from its own perspective.
        services.TryAddSingleton<IServerConnection>(AlwaysConnectedServer.Instance);

        // No-op stubs for outbox/cache so LocalDispatcher can be injected on the server too.
        services.TryAddSingleton<ICache, InMemoryCache>();
        services.TryAddSingleton<IKeyValueStore, MemoryKeyValueStore>();
        services.TryAddSingleton<IOutbox, InMemoryOutbox>();

        // Default server-side event bus (in-process; no RPC wrapping)
        services.TryAddSingleton<IEventBus>(sp =>
        {
            var bus = new DefaultEventBus();
            EventBusProvider.Configure(bus);
            return bus;
        });

        // Same fallback registry as on the client side.
        services.TryAddSingleton<CompiledMessagingRegistry>(_ =>
            CompiledMessagingRegistry.Build([]));
        services.AddYFexCqrsAuthorizationConfiguration();   // Cqrs requirements → the one message policy (04 §2)

        // LocalDispatcher: server uses this for in-process static helper calls.
        services.AddSingleton<IDispatcher>(sp =>
        {
            var dispatcher = new LocalDispatcher(
                sp.GetRequiredService<IHandlerInvoker>(),
                sp.GetRequiredService<CompiledMessagingRegistry>(),
                sp.GetRequiredService<IServerConnection>(),
                sp.GetRequiredService<ICache>(),
                sp.GetRequiredService<IOutbox>(),
                sp.GetRequiredService<IEventBus>(),
                sp);
            YFexDispatcherProvider.Set(dispatcher);
            return dispatcher;
        });

        return services;
    }

    // ── Call context (D29) ────────────────────────────────────────────────────

    // Server: identity sources + RPC middleware (stage 2 outside the compute cache), connection capture, the authorization
    // signals (YFex.Authorization) + their RPC observer (invalidate the pushed snapshot, disconnect on revoke), the
    // ICallPermissions compute service, and the IPrincipalAccessor decorator.
    private static void AddCallContextServer(IServiceCollection services, FusionBuilder fusion, CallContextServerBuilder options)
    {
        if (services.Any(static d => d.ServiceType == typeof(CallContextServerBuilder))) return;   // idempotent
        services.AddSingleton(options);
        services.AddYFexAuthorizationSignals();
        var connections = new CallContextConnections(options);
        services.AddSingleton(connections);
        services.AddSingleton<IAuthorizationChangeObserver>(sp => new RpcAuthorizationChangeObserver(connections, sp));

        fusion.Rpc.AddMiddleware<CallContextMiddleware>();
        fusion.AddServer<ICallPermissions, CallPermissionsService>();

        // Connection-bound capture: wrap Fusion's ServerConnectionFactory (it receives the upgrade HttpContext).
        for (int i = services.Count - 1; i >= 0; i--)
        {
            var d = services[i];
            if (d.ServiceType != typeof(RpcPeerOptions)) continue;
            services[i] = ServiceDescriptor.Singleton(sp => connections.Wrap(d.ImplementationInstance as RpcPeerOptions
                ?? (RpcPeerOptions)d.ImplementationFactory!(sp)));
            break;
        }

        CallContextPrincipalAccessor.Decorate(services);
    }

    /// <summary>
    /// Client: the UI sees the permissions the server resolved for this user (authorization-consolidation 05 §3). Registers
    /// <see cref="SnapshotPrincipalAccessor"/> as the <see cref="IPrincipalAccessor"/> (NavigatR, Capabilities, view-model
    /// commands and the client bus's quick check read it) and a hosted feed that keeps it in sync with
    /// <see cref="ICallPermissions"/> (refreshed by the server's pushed invalidations).
    /// <code>
    /// services.AddYFexMessagingRpcClient(…).UseServerPermissions();
    /// </code>
    /// Online only (Phase 3a): until the first answer the caller is anonymous; the signed offline copy comes with Phase 3b.
    /// </summary>
    public static IServiceCollection UseServerPermissions(this IServiceCollection services)
    {
        services.AddYFexAuthorizationSignals();
        services.TryAddSingleton(sp => new SnapshotPrincipalAccessor(sp.GetService<IAuthorizationSignals>()));
        services.RemoveAll<IPrincipalAccessor>();
        services.AddSingleton<IPrincipalAccessor>(sp => sp.GetRequiredService<SnapshotPrincipalAccessor>());
        if (!services.Any(static d => d.ServiceType == typeof(ServerPermissionsFeed)))
        {
            services.AddSingleton<ServerPermissionsFeed>();
            services.AddHostedService(sp => sp.GetRequiredService<ServerPermissionsFeed>());
        }
        return services;
    }

    // ── ASP.NET mapping ───────────────────────────────────────────────────────

    /// <summary>
    /// Maps the Fusion WebSocket RPC endpoint. Call this inside <c>app.MapYFexMessagingRpc()</c>
    /// alongside your other ASP.NET endpoint mappings.
    /// </summary>
    public static void MapYFexMessagingRpc(
        this IEndpointRouteBuilder app,
        string pattern = "/rpc/ws")
        => app.MapRpcWebSocketServer();

    // ── Legacy helper (kept for backward compat) ──────────────────────────────

    /// <summary>
    /// Registers the minimal RPC event bus stack (no command dispatcher).
    /// Use <see cref="AddYFexMessagingRpcClient"/> for the full client stack.
    /// </summary>
    public static IServiceCollection AddYFexRpc(this IServiceCollection services)
    {
        services.AddFusion();
        services.AddSingleton<ILiveStateFactory>(sp =>
        {
            var stateFactory = sp.GetRequiredService<StateFactory>();
            var factory = new FusionLiveStateFactory(stateFactory);
            LiveStateProvider.Configure(factory);
            return factory;
        });
        services.AddSingleton<DefaultEventBus>();
        services.AddSingleton<IEventBus>(sp =>
        {
            var local = sp.GetRequiredService<DefaultEventBus>();
            var remote = sp.GetRequiredService<IRemoteEventBus>();
            var bus = new RpcEventBus(local, remote);
            EventBusProvider.Configure(bus);
            bus.StartServerListener();
            return bus;
        });
        return services;
    }
}
