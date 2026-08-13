using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using YFex.Security.Analysis;
using YFex.Security.Capture;
using YFex.Security.Config;
using YFex.Security.Pipeline;
using YFex.Security.Storage;
using YFex.Windows.Security;

namespace YFex.Security.Service;

/// <summary>
/// Central composition root. Wires every security component into a single
/// generic host. Shared by the service entry point and the CLI so both
/// resolve identical singletons.
/// </summary>
public static class SecurityHostBuilder
{
    public static IHostBuilder Create(SecurityConfig config, bool enablePcap = false)
    {
        return Host.CreateDefaultBuilder()
            .UseSerilog((context, services, configuration) =>
            {
                string logDir = Environment.ExpandEnvironmentVariables(config.LogDirectory);
                Directory.CreateDirectory(logDir);

                configuration
                    .MinimumLevel.Information()
                    .WriteTo.Console()
                    .WriteTo.File(
                        Path.Combine(logDir, "yfex-security-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14);
            })
            .ConfigureServices(services =>
            {
                // Config + pipeline (singletons shared across all components)
                services.AddSingleton(config);
                services.AddSingleton<ISecurityPipeline>(new SecurityPipeline());

                // Storage
                services.AddYFexSecurityStorage();

                // Windows-specific inspectors/watchers
                services.AddYFexSecurity();

                // Capture (ETW auto-started; pcap optional)
                services.AddYFexSecurityCapture();
                if (enablePcap)
                    services.AddYFexPcapCapture();

                // Analysis (anomaly detector auto-started)
                services.AddYFexSecurityAnalysis();

                // Persistence pump: pipeline → storage
                services.AddHostedService<PersistenceHostedService>();

                // Migration runner on startup
                services.AddHostedService<MigrationHostedService>();
            });
    }
}
