using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace YFex.Cqrs.Compensation;

/// <summary>DI wiring for the reversible-command execution primitive.</summary>
public static class CompensationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the command journal, reverse executor, and the executed-command observer.
    ///
    /// Prerequisites (registered elsewhere): <see cref="YFex.Persistence.IKeyValueStore"/> (durable
    /// backend) and <see cref="CompiledMessagingRegistry"/> (from the YFex.Cqrs composition root).
    /// The dispatcher must invoke <see cref="ICommandExecutedObserver.OnExecutedAsync"/> after each
    /// successful command — see the seam contract on <see cref="ICommandExecutedObserver"/>.
    /// </summary>
    public static IServiceCollection AddYFexCompensation(this IServiceCollection services)
    {
        services.TryAddSingleton<ICommandJournal, StorageBackedCommandJournal>();
        services.TryAddSingleton<ICommandReverser, ReverseExecutor>();
        services.TryAddSingleton<ICommandExecutedObserver, CommandExecutedObserver>();
        return services;
    }
}
