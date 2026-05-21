using ActualLab.Fusion;
using Microsoft.Extensions.DependencyInjection;
using YFex.Messaging;

namespace YFex.Messaging.Fusion;

/// <summary>DI registration helpers for <c>YFex.Messaging.Fusion</c>.</summary>
public static class FusionMessagingExtensions
{
    /// <summary>
    /// Registers ActualLab.Fusion services and replaces the default task-based
    /// <see cref="ILiveStateFactory"/> with a Fusion-backed implementation that
    /// caches results and invalidates them through the Fusion dependency graph.
    /// </summary>
    public static IServiceCollection AddYFexFusion(this IServiceCollection services)
    {
        // Register Fusion core (StateFactory, ComputedRegistry, etc.)
        services.AddFusion();

        // Register and configure the Fusion-backed live-state factory
        services.AddSingleton<ILiveStateFactory>(sp =>
        {
            var stateFactory = sp.GetRequiredService<StateFactory>();
            var factory      = new FusionLiveStateFactory(stateFactory);
            LiveStateProvider.Configure(factory);
            return factory;
        });

        return services;
    }
}
