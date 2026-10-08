using ActualLab.Fusion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YFex.Authorization;
using YFex.Authorization.Public;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Client feed of <c>UseServerPermissions()</c> (authorization-consolidation 05 §3): keeps a live <see cref="ICallPermissions"/>
/// computed and applies every value to <see cref="SnapshotPrincipalAccessor"/>. The server invalidates the computed on
/// <see cref="IAuthorizationSignals"/> changes, Fusion pushes the invalidation, and the feed reads the new snapshot.
/// </summary>
public sealed class ServerPermissionsFeed(
    ICallPermissions permissions,
    SnapshotPrincipalAccessor accessor,
    ILogger<ServerPermissionsFeed>? logger = null) : BackgroundService
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Delay before the feed asks again after a failure (no connection, server error).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var computed = await Computed.TryCapture(() => permissions.GetMyPermissionsAsync(null, stoppingToken), stoppingToken)
                    .ConfigureAwait(false);
                while (computed is not null && !stoppingToken.IsCancellationRequested)
                {
                    accessor.Apply(computed.Value);
                    await computed.WhenInvalidated(stoppingToken).ConfigureAwait(false);
                    computed = (Computed<PermissionSnapshot>)await computed.Update(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Server permissions could not be read; retrying in {RetryDelay}.", RetryDelay);
            }

            try { await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
