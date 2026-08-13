using System.Threading.Channels;
using YFex.Security.Events;
using YFex.Security.Pipeline;

namespace YFex.Security.Storage;

public sealed class EventPersister : IDisposable
{
    private readonly ISecurityPipeline _pipeline;
    private readonly IEventRepository _repository;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private CancellationTokenSource? _cts;
    private Task? _consumeTask;

    public EventPersister(
        ISecurityPipeline pipeline,
        IEventRepository repository,
        int batchSize = 100,
        TimeSpan? flushInterval = null)
    {
        _pipeline = pipeline;
        _repository = repository;
        _batchSize = batchSize;
        _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(500);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _consumeTask = ConsumeAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_consumeTask is not null)
        {
            try { await _consumeTask; } catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var buffer = new List<ISecurityEvent>(_batchSize);
        var reader = _pipeline.Reader;

        while (!ct.IsCancellationRequested)
        {
            buffer.Clear();

            try
            {
                if (await reader.WaitToReadAsync(ct))
                {
                    using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    flushCts.CancelAfter(_flushInterval);

                    try
                    {
                        while (buffer.Count < _batchSize && reader.TryRead(out var evt))
                            buffer.Add(evt);

                        if (buffer.Count < _batchSize)
                        {
                            try
                            {
                                await Task.Delay(_flushInterval, flushCts.Token);
                            }
                            catch (OperationCanceledException) { }

                            while (buffer.Count < _batchSize && reader.TryRead(out var evt))
                                buffer.Add(evt);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                while (reader.TryRead(out var evt))
                    buffer.Add(evt);
            }

            if (buffer.Count > 0)
            {
                try
                {
                    await _repository.InsertBatchAsync(buffer, CancellationToken.None);
                }
                catch (Exception)
                {
                    // TODO: dead-letter or retry
                }
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
