using YFex.Cqrs;
using YFex.Messaging.Rpc;

namespace YFex.Messaging.Rpc.Wolverine;

/// <summary>
/// Wolverine cascading-handler base that invalidates the Fusion
/// <see cref="EventChannelHost{TEvent}"/> for any published <typeparamref name="TEvent"/>.
/// Inherit and register as a Wolverine handler for each event type you want server-pushed.
/// </summary>
public abstract class WolverineEventBridgeHandler<TEvent> where TEvent : IEvent
{
    private readonly EventChannelHost<TEvent> _channel;

    protected WolverineEventBridgeHandler(EventChannelHost<TEvent> channel) =>
        _channel = channel;

    /// <summary>Wolverine discovers this method via its naming convention.</summary>
    public Task HandleAsync(TEvent evt, CancellationToken ct = default)
    {
        _channel.Append(evt);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Concrete bridge that auto-registers for <typeparamref name="TEvent"/>.
/// Add one of these per event type in the Wolverine handler assembly.
/// </summary>
public sealed class EventBridge<TEvent> : WolverineEventBridgeHandler<TEvent>
    where TEvent : IEvent
{
    public EventBridge(EventChannelHost<TEvent> channel) : base(channel) { }
}
