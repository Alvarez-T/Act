using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;
using YFex.Cqrs;
using YFex.Messaging;
using YFex.Messaging.Rpc;

namespace YFex.Messaging.Rpc.Wolverine;

/// <summary>DI registration helpers for the Wolverine adapter.</summary>
public static class WolverineRpcExtensions
{
    /// <summary>
    /// Convenience extension that:
    /// <list type="bullet">
    ///   <item>Calls <see cref="RpcMessagingExtensions.UseYFexMessagingRpcServer"/>.</item>
    ///   <item>Registers <see cref="WolverineHandlerInvoker"/> as <see cref="IHandlerInvoker"/>.</item>
    ///   <item>Replaces <see cref="IDispatcher"/> with <see cref="WolverineLocalDispatcher"/>.</item>
    ///   <item>Optionally registers the exception-to-result middleware and the event bridge.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection UseYFexMessagingRpcServerWithWolverine(
        this IServiceCollection services,
        Action<YFexMessagingRpcServerOptions>? configure = null)
    {
        var opts = new YFexMessagingRpcServerOptions();
        configure?.Invoke(opts);

        // Base Fusion server stack
        services.UseYFexMessagingRpcServer(_ => { });
        services.AddSingleton(opts);

        // Override the default LocalHandlerInvoker with the Wolverine invoker
        services.RemoveAll<IHandlerInvoker>();
        services.AddSingleton<IHandlerInvoker, WolverineHandlerInvoker>();

        // Override the IDispatcher with the Wolverine-direct dispatcher
        services.RemoveAll<IDispatcher>();
        services.AddSingleton<IDispatcher>(sp =>
        {
            var dispatcher = new WolverineLocalDispatcher(
                sp.GetRequiredService<IMessageBus>(),
                sp.GetRequiredService<IEventBus>());
            YFexDispatcherProvider.Set(dispatcher);
            return dispatcher;
        });

        return services;
    }
}
