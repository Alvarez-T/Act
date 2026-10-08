using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization.Public;

namespace YFex.Cqrs;

/// <summary>
/// Exports the authorization requirements recorded by the Cqrs builder verbs (<see cref="CompiledMessagingRegistry.AuthorizationRequirements"/>)
/// to the one authorization registry, where they merge (AND) with <c>ForMessage&lt;T&gt;()</c> rows and <c>[RequirePermission]</c>
/// attributes on the records into ONE policy per message type (authorization-consolidation 04 §2.2, ⚑Z5/⚑Z6).
/// </summary>
public sealed class CqrsAuthorizationConfiguration(CompiledMessagingRegistry registry) : IAuthorizationConfiguration
{
    public void Configure(AuthorizationBuilder builder)
    {
        foreach (var (messageType, requirements) in registry.AuthorizationRequirements)
            builder.ForMessage(messageType).Add(requirements);
    }
}

/// <summary>DI wiring of the Cqrs authorization export.</summary>
public static class CqrsAuthorizationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CqrsAuthorizationConfiguration"/> as an <see cref="IAuthorizationConfiguration"/> over the registered
    /// <see cref="CompiledMessagingRegistry"/> (idempotent). The Messaging.Rpc and Wolverine registrations call it; dispatchers fail
    /// at startup when the registry declares requirements that the authorization registry does not know.
    /// </summary>
    public static IServiceCollection AddYFexCqrsAuthorizationConfiguration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var descriptor in services)
            if (descriptor.ServiceType == typeof(ExportMarker)) return services;
        services.AddSingleton(new ExportMarker());
        services.AddSingleton<IAuthorizationConfiguration>(static sp => new CqrsAuthorizationConfiguration(sp.GetRequiredService<CompiledMessagingRegistry>()));
        return services;
    }

    private sealed class ExportMarker;
}
