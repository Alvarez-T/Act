using ActualLab.Fusion;
using Microsoft.Extensions.DependencyInjection;
using YFex.Messaging;
using YFex.Messaging.Fusion;

namespace YFex.Messaging.Rpc;

/// <summary>
/// DI registration helpers for <c>YFex.Messaging.Rpc</c>.
/// </summary>
public static class RpcMessagingExtensions
{
    /// <summary>
    /// Registers the full YFex messaging stack with RPC transport support:
    /// <list type="bullet">
    ///   <item>Fusion core (StateFactory, ComputedRegistry)</item>
    ///   <item><see cref="FusionLiveStateFactory"/> as <see cref="ILiveStateFactory"/></item>
    ///   <item><see cref="RpcEventBus"/> as <see cref="IEventBus"/> (wraps local bus + remote proxy)</item>
    /// </list>
    /// <para>
    /// You must additionally register the <see cref="IRemoteEventBus"/> yourself:
    /// <list type="bullet">
    ///   <item><b>Server:</b> <c>services.AddRpc().AddServer&lt;IRemoteEventBus, RemoteEventBusServer&gt;()</c></item>
    ///   <item><b>Client:</b> <c>services.AddRpc().AddClient&lt;IRemoteEventBus&gt;()</c></item>
    /// </list>
    /// </para>
    /// </summary>
    public static IServiceCollection AddYFexRpc(this IServiceCollection services)
    {
        // 1. Fusion core + live-state factory
        services.AddFusion();
        services.AddSingleton<ILiveStateFactory>(sp =>
        {
            var stateFactory = sp.GetRequiredService<StateFactory>();
            var factory      = new FusionLiveStateFactory(stateFactory);
            LiveStateProvider.Configure(factory);
            return factory;
        });

        // 2. Local DefaultEventBus (inner bus)
        services.AddSingleton<DefaultEventBus>();

        // 3. Composite RpcEventBus — depends on IRemoteEventBus registered by the caller
        services.AddSingleton<IEventBus>(sp =>
        {
            var local  = sp.GetRequiredService<DefaultEventBus>();
            var remote = sp.GetRequiredService<IRemoteEventBus>();
            var bus    = new RpcEventBus(local, remote);
            EventBusProvider.Configure(bus);
            // AOT note: call bus.RegisterEventType<T>() for each expected server-push
            // event type before or after this point to avoid the reflection-based fallback.
            bus.StartServerListener();
            return bus;
        });

        return services;
    }
}
