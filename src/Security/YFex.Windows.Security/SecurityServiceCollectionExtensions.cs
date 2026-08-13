using Microsoft.Extensions.DependencyInjection;
using YFex.Security.FileSystem;
using YFex.Security.Network;
using YFex.Security.Process;
using YFex.Windows.Security.Antivirus;
using YFex.Windows.Security.FileSystem;
using YFex.Windows.Security.Input;
using YFex.Windows.Security.Integrity;
using YFex.Windows.Security.Network;
using YFex.Windows.Security.Process;
using YFex.Windows.Security.Registry;

namespace YFex.Windows.Security;

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
