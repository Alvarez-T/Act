using YFex.Security.Events;

namespace YFex.Security.Pipeline;

public sealed class ObservablePipelineBridge<T> : IDisposable
{
    private readonly ISecurityPipeline _pipeline;
    private readonly Func<T, ISecurityEvent> _converter;
    private IDisposable? _subscription;

    public ObservablePipelineBridge(
        IObservable<T> source,
        ISecurityPipeline pipeline,
        Func<T, ISecurityEvent> converter)
    {
        _pipeline = pipeline;
        _converter = converter;
        _subscription = source.Subscribe(new BridgeObserver(this));
    }

    private sealed class BridgeObserver(ObservablePipelineBridge<T> bridge) : IObserver<T>
    {
        public void OnNext(T value)
        {
            var evt = bridge._converter(value);
            bridge._pipeline.Writer.TryWrite(evt);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
