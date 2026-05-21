using System.Threading;
using System.Threading.Tasks;

namespace YFex.Messaging.Tests.Generator;

// ── [Subscribe<T>] test ViewModels ──────────────────────────────────────────

/// <summary>Basic: one handler, no filter. Generator emits sync IEventRecipient adapter.</summary>
public partial class SimpleSubscribeVm : StateObject
{
    public int CallCount { get; private set; }
    public CounterEvent? LastEvent { get; private set; }

    [Subscribe<CounterEvent>]
    private void OnCounterEvent(in CounterEvent e)
    {
        CallCount++;
        LastEvent = e;
    }
}

/// <summary>Filter: handler only fires when e.MatchId == this.MatchId.</summary>
public partial class FilteredSubscribeVm : StateObject
{
    [Observable] public partial int MatchId { get; set; }

    public int CallCount { get; private set; }

    [Subscribe<FilteredEvent>(FilterBy = nameof(MatchId))]
    private void OnFilteredEvent(in FilteredEvent e) => CallCount++;
}

/// <summary>Async handler: returns ValueTask — generator emits IAsyncEventRecipient adapter.</summary>
public partial class AsyncSubscribeVm : StateObject
{
    public int CallCount { get; private set; }
    public TaskCompletionSource<string> Received { get; } = new();

    [Subscribe<AsyncEvent>]
    private async ValueTask OnAsyncEvent(AsyncEvent e, CancellationToken ct)
    {
        CallCount++;
        Received.TrySetResult(e.Message);
        await Task.CompletedTask;
    }
}

/// <summary>KeepAlive = true: strong ref — subscription survives GC of transient outer scope.</summary>
public partial class KeepAliveSubscribeVm : StateObject
{
    public int CallCount { get; private set; }

    [Subscribe<CounterEvent>(KeepAlive = true)]
    private void OnEvent(in CounterEvent e) => CallCount++;
}

// ── [Live] test ViewModels ───────────────────────────────────────────────────

/// <summary>
/// Live VM backed by an injected computation so tests control what gets returned.
/// The generated Counter property exposes the task result; IsCounterLoading and
/// CounterError are companions.
/// </summary>
public partial class LiveTestVm : StateObject
{
    private readonly Func<CancellationToken, Task<int>> _computation;

    public LiveTestVm(Func<CancellationToken, Task<int>> computation)
    {
        _computation = computation;
    }

    [Live]
    private Task<int> CounterAsync(CancellationToken ct) => _computation(ct);
}

/// <summary>Live VM with polling enabled (10 ms for fast test iteration).</summary>
public partial class PollingLiveVm : StateObject
{
    public int FetchCount { get; private set; }

    [Live(PollMs = 10)]
    private Task<int> TickAsync(CancellationToken ct)
    {
        FetchCount++;
        return Task.FromResult(FetchCount);
    }
}
