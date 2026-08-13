using Microsoft.Extensions.Hosting;
using YFex.Security.Service;

var config = SecurityConfigLoader.Load();

bool enablePcap = args.Contains("--pcap", StringComparer.OrdinalIgnoreCase);

var host = SecurityHostBuilder.Create(config, enablePcap).Build();

await host.RunAsync();
