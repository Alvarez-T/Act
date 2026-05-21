namespace YFex.Messaging.Internal;

/// <summary>
/// Wraps an <see cref="Action{T}"/> as an <see cref="IEventRecipient{T}"/> so static
/// lambda handlers can be registered via <see cref="EventBusExtensions.On{T}"/>.
/// </summary>
internal sealed class DelegateAdapter<T>(Action<T> handler) : IEventRecipient<T>
{
    void IEventRecipient<T>.Receive(in T @event) => handler(@event);
}
