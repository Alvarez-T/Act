using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using YFex.Persistence;
using YFex.Persistence.FileSystem;
using YFex.System.Consent;
using YFex.System.Platform;
using YFex.System.Telemetry;

namespace YFex.System;

public static class TelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddYFexTelemetry(
        this IServiceCollection services,
        Action<TelemetryOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<ILocalStore>(_ => new FileSystemLocalStore(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YFex")));
        services.AddSingleton<IConsentStore, FileConsentStore>();
        services.AddSingleton<ITelemetryConsent, TelemetryConsent>();
        services.AddSingleton<ITelemetryTransport, HttpTelemetryTransport>();
        services.AddSingleton<IOfflineQueue, NullOfflineQueue>();
        services.AddSingleton<IUserBehaviourTracker, NullUserBehaviourTracker>();
        services.AddSingleton(sp => new TelemetryBatch(
            sp.GetRequiredService<ITelemetryTransport>(),
            sp.GetRequiredService<IOptions<TelemetryOptions>>().Value,
            sp.GetRequiredService<IOfflineQueue>()));
        services.AddSingleton<ITelemetryCollector, TelemetryCollector>();
        return services;
    }
}
