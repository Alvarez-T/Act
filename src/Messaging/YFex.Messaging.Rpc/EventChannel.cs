using ActualLab.Fusion;
using YFex.Cqrs;
using YFex.Messaging;
using FusionStateBase = ActualLab.Fusion.State;

namespace YFex.Messaging.Rpc;

// ── Wire model ────────────────────────────────────────────────────────────────

/// <summary>A page of events with a monotonic sequence cursor.</summary>
public sealed record EventCursor<TEvent>(long Sequence, IReadOnlyList<TEvent> Items)
    where TEvent : IEvent;

/// <summary>Client-supplied filter for event channel subscriptions.</summary>
public sealed record EventFilter(string? TenantId = null, string? UserId = null);

// ── Server-side interface ─────────────────────────────────────────────────────

/// <summary>
/// Server-side Fusion compute service that exposes an append-only event stream.
/// Invalidated by the Wolverine event bridge on every published <typeparamref name="TEvent"/>.
/// Register via <c>fusion.AddServer&lt;IEventChannel&lt;TEvent&gt;&gt;()</c>.
/// </summary>
public interface IEventChannel<TEvent> : IComputeService where TEvent : IEvent
{
    [ComputeMethod]
    Task<EventCursor<TEvent>> GetEventsAsync(EventFilter filter, long sinceSequence,
        CancellationToken ct = default);
}

// ── Server-side implementation ────────────────────────────────────────────────

/// <summary>
/// Default in-memory server-side implementation of <see cref="IEventChannel{TEvent}"/>.
/// Stores events in a bounded ring buffer (10k entries). The Wolverine event bridge
/// calls <see cref="Append"/> which invalidates the compute method, triggering push
/// to all subscribed clients.
/// </summary>
public class EventChannelHost<TEvent> : IEventChannel<TEvent> where TEvent : IEvent
{
    private const int MaxBuffer = 10_000;
    private readonly List<(long Seq, TEvent Evt)> _buffer = [];
    private readonly object _lock = new();
    private long _sequence;

    public virtual Task<EventCursor<TEvent>> GetEventsAsync(
        EventFilter filter, long sinceSequence, CancellationToken ct = default)
    {
        lock (_lock)
        {
            List<TEvent> items = [];
            for (int i = 0; i < _buffer.Count; i++)
                if (_buffer[i].Seq > sinceSequence)
                    items.Add(_buffer[i].Evt);
            return Task.FromResult(new EventCursor<TEvent>(_sequence, items));
        }
    }

    /// <summary>Called by the Wolverine bridge to publish a new event to all subscribers.</summary>
    public void Append(TEvent evt)
    {
        lock (_lock)
        {
            _sequence++;
            _buffer.Add((_sequence, evt));
            if (_buffer.Count > MaxBuffer) _buffer.RemoveAt(0);
        }

        // Invalidate via Fusion so all [ComputeMethod] subscribers are notified.
        using (Invalidation.Begin())
            _ = GetEventsAsync(new EventFilter(), 0);
    }

    public Task AppendAsync(TEvent evt, CancellationToken ct = default)
    {
        Append(evt);
        return Task.CompletedTask;
    }
}

// ── Client-side consumer ──────────────────────────────────────────────────────

/// <summary>
/// Client-side bridge that subscribes to <see cref="IEventChannel{TEvent}"/> via a Fusion
/// <see cref="ComputedState{T}"/> and forwards newly-arrived events into the local
/// <see cref="IEventBus"/>. Deduplicates via the monotonic sequence cursor.
/// </summary>
internal sealed class FusionEventStream<TEvent> : IAsyncDisposable where TEvent : IEvent
{
    private readonly IEventChannel<TEvent> _channel;
    private readonly IEventBus _eventBus;
    private readonly EventFilter _filter;
    private readonly StateFactory _stateFactory;
    private long _lastSequence;
    private ComputedState<EventCursor<TEvent>>? _state;
    private bool _disposed;

    public FusionEventStream(
        IEventChannel<TEvent> channel,
        IEventBus eventBus,
        EventFilter filter,
        StateFactory stateFactory)
    {
        _channel = channel;
        _eventBus = eventBus;
        _filter = filter;
        _stateFactory = stateFactory;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _state = _stateFactory.NewComputed<EventCursor<TEvent>>(
            cct => _channel.GetEventsAsync(_filter, _lastSequence, cct));
        _state.Updated += OnUpdated;
        return Task.CompletedTask;
    }

    private void OnUpdated(FusionStateBase s, StateEventKind kind)
    {
        if (_disposed || s is not ComputedState<EventCursor<TEvent>> typedState) return;
        if (!typedState.HasValue) return;

        var cursor = typedState.Value;
        if (cursor is null || cursor.Sequence <= _lastSequence) return;

        // Forward only events newer than the last sequence we have seen.
        for (int i = 0; i < cursor.Items.Count; i++)
            _eventBus.Publish(cursor.Items[i]);

        _lastSequence = cursor.Sequence;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_state is not null)
        {
            _state.Updated -= OnUpdated;
            _state.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
