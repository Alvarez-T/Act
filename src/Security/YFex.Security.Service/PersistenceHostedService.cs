using Microsoft.Extensions.Hosting;
using YFex.Security.Storage;

namespace YFex.Security.Service;

/// <summary>Drives the <see cref="EventPersister"/> pipeline pump over the host lifetime.</summary>
public sealed class PersistenceHostedService : IHostedService
{
    private readonly EventPersister _persister;

    public PersistenceHostedService(EventPersister persister)
    {
        _persister = persister;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _persister.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _persister.StopAsync();
    }
}
