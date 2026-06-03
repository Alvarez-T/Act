namespace YFex.Messaging;

/// <summary>
/// Base class for long-lived non-<c>StateObject</c> services that need event
/// subscriptions for the application's lifetime (e.g. singletons registered in DI).
/// The DI container disposes the derived class at shutdown; <see cref="DisposeAsync"/>
/// releases all subscriptions registered via <see cref="RegisterSubscription"/>.
/// </summary>
/// <remarks>
/// The source generator detects classes derived from <see cref="MessagingHost"/> that
/// contain <c>[Subscribe&lt;T&gt;]</c> methods and wires subscriptions into
/// <see cref="OnHostStarting"/>, which is called from the constructor.
///
/// The generated subscription token is passed to <see cref="RegisterSubscription"/>
/// so it is released during <see cref="DisposeAsync"/> without requiring the derived
/// class to implement <see cref="IAsyncDisposable"/> explicitly.
/// </remarks>
public abstract class MessagingHost : IAsyncDisposable
{
    private List<IDisposable>? _subscriptions;

    protected MessagingHost() => OnHostStarting();

    /// <summary>
    /// Called from the constructor. The source generator overrides this method
    /// to subscribe all <c>[Subscribe&lt;T&gt;]</c> handlers via
    /// <see cref="RegisterSubscription"/>.
    /// </summary>
    protected virtual void OnHostStarting() { }

    /// <summary>
    /// Registers a subscription token or disposable resource to be released
    /// during <see cref="DisposeAsync"/>. Called by generated code.
    /// </summary>
    protected void RegisterSubscription(IDisposable token)
        => (_subscriptions ??= new List<IDisposable>()).Add(token);

    /// <summary>
    /// Releases all subscriptions registered via <see cref="RegisterSubscription"/>
    /// in reverse order.
    /// </summary>
    public virtual ValueTask DisposeAsync()
    {
        var list = _subscriptions;
        if (list is not null)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                list[i].Dispose();
            _subscriptions = null;
        }
        return ValueTask.CompletedTask;
    }
}
