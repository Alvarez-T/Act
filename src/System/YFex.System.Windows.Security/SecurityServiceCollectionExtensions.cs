using Microsoft.Extensions.DependencyInjection;
using YFex.System.Windows.Security.Antivirus;
using YFex.System.Windows.Security.FileSystem;
using YFex.System.Windows.Security.Input;
using YFex.System.Windows.Security.Integrity;
using YFex.System.Windows.Security.Network;
using YFex.System.Windows.Security.Process;
using YFex.System.Windows.Security.Registry;

namespace YFex.System.Windows.Security;

public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddYFexSecurity(this IServiceCollection services)
    {
        services.AddSingleton<ISystemInputMonitor, SystemInputMonitor>();
        services.AddSingleton<IProcessInspector, ProcessInspector>();
        services.AddSingleton<IProcessWatcher, ProcessWatcher>();
        services.AddSingleton<INetworkInspector, NetworkInspector>();
        services.AddSingleton<IAmsiScanner, AmsiScanner>();
        services.AddSingleton<IAntivirusProvider, WindowsDefenderProvider>();
        services.AddSingleton<IFileScanner, FileScanner>();
        services.AddSingleton<IFileWatcher, SecurityFileWatcher>();
        services.AddSingleton<IRegistryMonitor, RegistryMonitor>();
        services.AddSingleton<IPersistenceDetector, PersistenceDetector>();
        services.AddSingleton<ISystemIntegrityChecker, SystemIntegrityChecker>();
        return services;
    }
}
