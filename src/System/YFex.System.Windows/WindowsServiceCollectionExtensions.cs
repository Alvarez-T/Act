using Microsoft.Extensions.DependencyInjection;
using YFex.System.Platform;
using YFex.System.Telemetry;

namespace YFex.System.Windows;

public static class WindowsServiceCollectionExtensions
{
    public static IServiceCollection AddYFexWindows(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformProvider, WindowsProvider>();
        services.AddSingleton<IIdleDetector, WindowsIdleDetector>();
        services.AddSingleton<ISessionMonitor, WindowsSessionMonitor>();
        services.AddSingleton<IInstalledSoftwareReader, WindowsInstalledSoftwareReader>();
        services.AddSingleton<IBrowserDataReader, WindowsBrowserDataReader>();
        services.AddSingleton<IHardwareFingerprintProvider, WindowsHardwareFingerprint>();
        services.AddSingleton<IActiveWindowDetector, WindowsActiveWindowDetector>();
        services.AddSingleton<IDisplayInfoProvider, WindowsDisplayInfoProvider>();
        services.AddSingleton<IOfflineQueue, SqliteOfflineQueue>();
        services.AddSingleton<IUserBehaviourTracker, WindowsUserBehaviourTracker>();
        return services;
    }
}
