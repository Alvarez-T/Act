using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs.Runtime;

namespace YFex.Cqrs.Compensation;

/// <summary>
/// A standalone catalog of reverse (compensation) declarations, keyed by original command type. This is
/// the second-most-specific source in the saga reverse resolver — <c>step.Reverse ?? registry.Get(cmdType)
/// ?? commandPolicy.Reverse</c> (design §11/§14, R3). It lets a command be made reversible for
/// orchestration <b>without</b> touching its CQRS <c>CommandPolicy</c>.
/// </summary>
public interface IReverseRegistry
{
    /// <summary>The reverse spec registered for <paramref name="commandType"/>, or <see langword="null"/>.</summary>
    ReverseSpec? Get(Type commandType);
}

/// <summary>Fluent builder handed to <c>AddYFexReversals</c>.</summary>
public interface IReverseRegistryBuilder
{
    /// <summary>Declares that <typeparamref name="TCommand"/> is reversed by <typeparamref name="TReverse"/>,
    /// produced from the original command via <paramref name="map"/>. Both the map and the typed dispatch
    /// are captured here where <typeparamref name="TReverse"/> is a compile-time generic parameter — no
    /// reflection on the reverse path.</summary>
    IReverseRegistryBuilder Reverse<TCommand, TReverse>(Func<TCommand, TReverse> map)
        where TCommand : ICommand
        where TReverse : ICommand;
}

/// <summary>
/// Mutable-at-startup, read-only-at-runtime reverse registry. Populated only during DI composition
/// (single-threaded), then read on the compensation path. Kept as a plain dictionary rather than a frozen
/// one because entries accumulate across multiple <c>AddYFexReversals</c> calls; the read is a single
/// dictionary lookup off the hot path.
/// </summary>
internal sealed class MutableReverseRegistry : IReverseRegistry, IReverseRegistryBuilder
{
    internal readonly Dictionary<Type, ReverseSpec> Specs = new();

    public ReverseSpec? Get(Type commandType)
        => Specs.TryGetValue(commandType, out var s) ? s : null;

    public IReverseRegistryBuilder Reverse<TCommand, TReverse>(Func<TCommand, TReverse> map)
        where TCommand : ICommand
        where TReverse : ICommand
    {
        ArgumentNullException.ThrowIfNull(map);
        Specs[typeof(TCommand)] = new ReverseSpec(
            ReverseCommandType: typeof(TReverse),
            Map: (c, _) => map((TCommand)c),
            // Captured where TReverse is a compile-time generic parameter — dispatches the already-typed
            // reverse command through IDispatcher.CommandAsync<TReverse>, no MakeGenericMethod.
            Dispatch: static async (d, rev, ct) => { await d.CommandAsync((TReverse)rev, ct).ConfigureAwait(false); });
        return this;
    }
}

/// <summary>DI wiring for standalone reverse declarations.</summary>
public static class ReverseRegistryServiceCollectionExtensions
{
    /// <summary>
    /// Registers (or extends) the standalone <see cref="IReverseRegistry"/>. Multiple calls accumulate into
    /// one registry instance. Example:
    /// <code>services.AddYFexReversals(r => r.Reverse&lt;ArrangeShipmentCommand, CancelShipmentCommand&gt;(c => new(c.SaleId)));</code>
    /// </summary>
    public static IServiceCollection AddYFexReversals(
        this IServiceCollection services, Action<IReverseRegistryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IReverseRegistry));
        MutableReverseRegistry registry;
        if (descriptor?.ImplementationInstance is MutableReverseRegistry existing)
        {
            registry = existing;
        }
        else
        {
            registry = new MutableReverseRegistry();
            services.AddSingleton<IReverseRegistry>(registry);
        }

        configure(registry);
        return services;
    }
}
