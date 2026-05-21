using Microsoft.Extensions.DependencyInjection;

namespace YFex.Messaging;

/// <summary>
/// DI registration helpers for YFex.Messaging.
/// </summary>
public static class MessagingServiceExtensions
{
    /// <summary>
    /// Registers the <see cref="DefaultEventBus"/> as a singleton <see cref="IEventBus"/>
    /// and wires it into <see cref="EventBusProvider"/> for static-facade access.
    /// </summary>
    public static IServiceCollection AddYFexMessaging(this IServiceCollection services)
    {
        var bus = new DefaultEventBus();
        EventBusProvider.Configure(bus);
        services.AddSingleton<IEventBus>(bus);
        return services;
    }
}
