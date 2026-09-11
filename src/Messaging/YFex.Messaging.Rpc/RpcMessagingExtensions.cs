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
        services.TryAddSingleton<IKeyValueStore, MemoryKeyValueStore>();
        services.TryAddSingleton<ICache, InMemoryCache>();

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

        // Source-generated typed handler-invoke map. Empty fallback so LocalHandlerInvoker always
        // resolves; the generated AddYFexLocalHandlers() (call at startup) replaces this with the
        // populated map so handler resolution is reflection-free (no MakeGenericType).
        services.TryAddSingleton(LocalHandlerInvokerRegistry.Empty);

        // Default invoker resolves handlers from DI via the typed map; Wolverine adapter replaces this.
        services.TryAddSingleton<IHandlerInvoker, LocalHandlerInvoker>();

        // Server is always "connected" from its own perspective.
        services.TryAddSingleton<INetworkStatus>(AlwaysConnectedNetworkStatus.Instance);
        NetworkStatusProvider.Configure(AlwaysConnectedNetworkStatus.Instance);

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

        // LocalDispatcher: server uses this for in-process static helper calls.
        services.AddSingleton<IDispatcher>(sp =>
        {
            var dispatcher = new LocalDispatcher(
                sp.GetRequiredService<IHandlerInvoker>(),
                sp.GetRequiredService<CompiledMessagingRegistry>(),
                sp.GetRequiredService<INetworkStatus>(),
                sp.GetRequiredService<ICache>(),
                sp.GetRequiredService<IOutbox>(),
                sp.GetRequiredService<IEventBus>(),
                sp);
            YFexDispatcherProvider.Set(dispatcher);
            return dispatcher;
        });

        return services;
    }

    // ── CQRS authorization wiring (Gate 1) ─────────────────────────────────────

    /// <summary>
    /// Rebuilds the <see cref="CompiledMessagingRegistry"/> with YFex.Authorization plugged in, so
    /// command/query authorizers declared via <c>RequirePermission</c> / <c>RequireRoles</c> /
    /// <c>RequireAuthorization</c> actually evaluate through <see cref="IPolicyEvaluator"/> instead of
    /// failing closed. Without this call, any command carrying such an authorizer is denied.
    ///
    /// <para>
    /// Call this in the composition root <b>after</b> <c>AddYFexAuthorization(...)</c> (which registers
    /// the evaluator + <see cref="IAuthContextFactory"/>) and after the generated
    /// <c>AddYFexConfigurations()</c> (which registers the <see cref="IAggregateConfiguration"/> classes).
    /// Aggregate configurations are resolved from the container; the registry is (re)registered as the
    /// authoritative singleton regardless of call order relative to the RPC client/server helpers.
    /// </para>
    /// </summary>
    public static IServiceCollection AddYFexCqrsAuthorization(this IServiceCollection services)
    {
        services.RemoveAll<CompiledMessagingRegistry>();
        services.AddSingleton(sp => CompiledMessagingRegistry.Build(
            baseline: sp.GetServices<IAggregateConfiguration>(),
            policyEvaluator: sp.GetRequiredService<IPolicyEvaluator>(),
            principalFactory: principal => sp.GetRequiredService<IAuthContextFactory>().Create(principal)));
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
