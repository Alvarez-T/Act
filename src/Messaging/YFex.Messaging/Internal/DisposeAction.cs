namespace YFex.Messaging.Internal;

internal sealed class DisposeAction : IDisposable
{
    private Action? _action;

    public DisposeAction(Action action) => _action = action;

    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}
