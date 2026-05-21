using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace YFex.State.Commands;

/// <summary>
/// Bounded FIFO queue for serialised async command execution.
/// One drain loop per queue; work items implement <see cref="ICommandWorkItem"/> so they are
/// both type-safely channelled and allocation-free when dispatched to the thread pool.
/// Named queues are looked up from a single <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// keyed by queue name; the dictionary is the only lock-bearing structure for this concern.
/// </summary>
internal sealed class CommandQueue
{
    private static readonly ConcurrentDictionary<string, CommandQueue> s_named = new();

    private readonly Channel<ICommandWorkItem> _channel;
    private readonly CancellationTokenSource _cts = new();

    public CommandQueue(int capacity, BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait)
    {
        _channel = Channel.CreateBounded<ICommandWorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = fullMode,
            SingleReader = true,
            SingleWriter = false,
        });
        _ = Task.Run(DrainAsync);
    }

    public static CommandQueue GetOrCreate(string name, int capacity, BoundedChannelFullMode mode) =>
        s_named.GetOrAdd(name, static (_, args) => new CommandQueue(args.capacity, args.mode),
            (capacity, mode));

    public bool TryEnqueue(ICommandWorkItem work) => _channel.Writer.TryWrite(work);

    public int PendingCount => _channel.Reader.Count;

    public void Complete() => _channel.Writer.TryComplete();

    private async Task DrainAsync()
    {
        var ct = _cts.Token;
        await foreach (var item in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await item.ExecuteAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Individual item errors are handled by the item itself (ErrorBucket etc.)
                // The drain loop must never die.
            }
        }
    }
}
