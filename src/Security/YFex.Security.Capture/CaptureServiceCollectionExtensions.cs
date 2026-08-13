using Microsoft.Extensions.DependencyInjection;
using YFex.Security.Capture.Correlation;
using YFex.Security.Capture.Etw;
using YFex.Security.Capture.Pcap;

namespace YFex.Security.Capture;

public static class CaptureServiceCollectionExtensions
{
    public static IServiceCollection AddYFexSecurityCapture(this IServiceCollection services)
    {
        services.AddSingleton<ProcessSocketCorrelator>();
        services.AddHostedService<EtwCaptureService>();
        return services;
    }

    public static IServiceCollection AddYFexPcapCapture(this IServiceCollection services)
    {
        services.AddHostedService<PcapCaptureService>();
        return services;
    }
}
