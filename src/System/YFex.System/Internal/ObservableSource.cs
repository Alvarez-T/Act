namespace YFex.System.Internal;

internal sealed class ObservableSource<T> : IObservable<T>
{
    private readonly List<IObserver<T>> _observers = [];

    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (_observers) _observers.Add(observer);
        return new Unsubscriber(this, observer);
    }

    internal void Emit(T value)
    {
        IObserver<T>[] snapshot;
        lock (_observers) snapshot = [.. _observers];
        foreach (var obs in snapshot)
        {
            try { obs.OnNext(value); }
            catch { }
        }
    }

    internal void Complete()
    {
        IObserver<T>[] snapshot;
        lock (_observers) snapshot = [.. _observers];
        foreach (var obs in snapshot)
        {
            try { obs.OnCompleted(); }
            catch { }
        }
    }

    private sealed class Unsubscriber(ObservableSource<T> source, IObserver<T> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (source._observers) source._observers.Remove(observer);
        }
    }
}
