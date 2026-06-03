using ActualLab.Fusion;
using ActualLab.Rpc;
using ActualLab.Rpc.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YFex.Cqrs;
using YFex.Messaging;
using YFex.Messaging.Fusion;

namespace YFex.Messaging.Rpc;

/// <summary>DI registration helpers for <c>YFex.Messaging.Rpc</c>.</summary>
public static class RpcMessagingExtensions
{
    // ── Client ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers the full YFex client-side Rpc stack:
    /// Fusion client, IClientCache, IOutbox, ISyncFailureLog, SyncStatus,
    /// FusionMessageBus (IDispatcher), OutboxReplayer, and FusionNetworkStatus.
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

        // Live-state factory backed by Fusion
        services.AddSingleton<ILiveStateFactory>(sp =>
        {
            var stateFactory = sp.GetRequiredService<StateFactory>();
            var factory = new FusionLiveStateFactory(stateFactory);
            LiveStateProvider.Configure(factory);
            return factory;
        });

        // Storage + cache (in-memory; Plan 4 overrides with persistent backends)
        services.TryAddSingleton<IClientStorage, InMemoryClientStorage>();
        services.TryAddSingleton<IClientCache, InMemoryClientCache>();

        // Outbox + failure log (cross-wired after construction)
        services.AddSingleton<InMemoryOutbox>();
        services.AddSingleton<IOutbox>(sp =>
        {
            var outbox = sp.GetRequiredService<InMemoryOutbox>();
            if (sp.GetService<InMemorySyncFailureLog>() is { } log)
                outbox.SetFailureLog(log);
            return outbox;
        });

        services.AddSingleton<InMemorySyncFailureLog>();
        services.AddSingleton<ISyncFailureLog>(sp =>
        {
            var log = sp.GetRequiredService<InMemorySyncFailureLog>();
            if (sp.GetService<InMemoryOutbox>() is { } outbox)
                log.SetOutbox(outbox);
            return log;
        });
        services.AddSingleton<IWritableSyncFailureLog>(sp =>
            sp.GetRequiredService<InMemorySyncFailureLog>());

        // Bindable sync status singleton
        services.AddSingleton<SyncStatus>();

        // Network status (Fusion-backed)
        services.AddSingleton<INetworkStatus>(sp =>
        {
            var status = new FusionNetworkStatus(sp.GetRequiredService<RpcHub>());
            NetworkStatusProvider.Configure(status);
            return status;
        });

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
        services.AddSingleton<FusionMessageBusBuilder>();
        services.AddSingleton<IDispatcher>(sp =>
        {
            var builder = sp.GetRequiredService<FusionMessageBusBuilder>();
            var bus = builder.Build(
                sp.GetRequiredService<CompiledMessagingRegistry>(),
                sp.GetRequiredService<INetworkStatus>(),
                sp.GetRequiredService<IClientCache>(),
                sp.GetRequiredService<IOutbox>(),
                sp.GetRequiredService<IEventBus>(),
                sp);
            YFexDispatcherProvider.Set(bus);
            return bus;
        });

        // OutboxReplayer (hosted service)
        services.AddHostedService<OutboxReplayer>();

        return services;
    }

    // ── Server ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers server-side Fusion RPC infrastructure: Fusion core, IHandlerInvoker
    /// (defaults to <see cref="LocalHandlerInvoker"/>; override with Wolverine adapter),
    /// AlwaysConnectedNetworkStatus, and ASP.NET endpoint mapping helpers.
    /// </summary>
    public static IServiceCollection UseYFexMessagingRpcServer(
        this IServiceCollection services,
        Action<YFexMessagingRpcServerOptions>? configure = null)
    {
        var opts = new YFexMessagingRpcServerOptions();
        configure?.Invoke(opts);
        services.AddSingleton(opts);

        services.AddFusion();

        // Default invoker resolves handlers from DI; Wolverine adapter replaces this.
        services.TryAddSingleton<IHandlerInvoker, LocalHandlerInvoker>();

        // Server is always "connected" from its own perspective.
        services.TryAddSingleton<INetworkStatus>(AlwaysConnectedNetworkStatus.Instance);
        NetworkStatusProvider.Configure(AlwaysConnectedNetworkStatus.Instance);

        // No-op stubs for outbox/cache so LocalDispatcher can be injected on the server too.
        services.TryAddSingleton<IClientCache, InMemoryClientCache>();
        services.TryAddSingleton<IClientStorage, InMemoryClientStorage>();
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

        // LocalDispatcher: server uses this for in-process static helper calls.
        services.AddSingleton<IDispatcher>(sp =>
        {
            var dispatcher = new LocalDispatcher(
                sp.GetRequiredService<IHandlerInvoker>(),
                sp.GetRequiredService<CompiledMessagingRegistry>(),
                sp.GetRequiredService<INetworkStatus>(),
                sp.GetRequiredService<IClientCache>(),
                sp.GetRequiredService<IOutbox>(),
                sp.GetRequiredService<IEventBus>(),
                sp);
            YFexDispatcherProvider.Set(dispatcher);
            return dispatcher;
        });

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
