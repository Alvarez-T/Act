using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using ActualLab.Fusion;
using YFex.Cqrs;
using YFex.Messaging;
using YFex.Tenancy;
using FusionStateBase = ActualLab.Fusion.State;

namespace YFex.Messaging.Rpc;

// ── Wire model ────────────────────────────────────────────────────────────────

/// <summary>A page of events with a monotonic sequence cursor.</summary>
public sealed record EventCursor<TEvent>(long Sequence, IReadOnlyList<TEvent> Items)
    where TEvent : IEvent;

/// <summary>
/// Subscription filter. <see cref="AudienceToken"/> is the <b>server-issued</b> token from
/// <see cref="IEventChannel{TEvent}.GetAudienceTokenAsync"/>; without one a subscriber receives only global
/// (unscoped) events. The audience is never taken from a client-supplied value: a token is HMAC-signed, so a
/// Fusion-cached result keyed by it is reachable only by callers the server issued it to.
/// </summary>
public sealed record EventFilter(string? AudienceToken = null);

/// <summary>Server options for <see cref="EventChannelHost{TEvent}"/>.</summary>
public sealed class EventChannelOptions
{
    /// <summary>
    /// Which rungs of the publisher's scope become an event's audience — e.g. <c>o => o.UpTo(MyScope.Tenant)</c> makes
    /// events reach every subscriber of that tenant. Null = the global isolation policy (<see cref="ScopePartitionOptions"/>)
    /// when registered, else every rung.
    /// </summary>
    public ScopePartitionOptions? Audience { get; set; }

    /// <summary>
    /// HMAC-SHA256 key signing audience tokens. Default: random per process (clients re-fetch a token after a restart).
    /// Set one shared key on every node of a multi-node server.
    /// </summary>
    public byte[] SigningKey { get; set; } = RandomNumberGenerator.GetBytes(32);
}

// ── Server-side interface ─────────────────────────────────────────────────────

/// <summary>
/// Server-side Fusion compute service that exposes an append-only event stream.
/// Invalidated by the Wolverine event bridge on every published <typeparamref name="TEvent"/>.
/// Register via <c>fusion.AddServer&lt;IEventChannel&lt;TEvent&gt;&gt;()</c>.
/// </summary>
public interface IEventChannel<TEvent> : IComputeService where TEvent : IEvent
{
    /// <summary>Events after <paramref name="sinceSequence"/> visible to <paramref name="filter"/>'s audience.</summary>
    [ComputeMethod]
    Task<EventCursor<TEvent>> GetEventsAsync(EventFilter filter, long sinceSequence,
        CancellationToken ct = default);

    /// <summary>The head sequence. Every <see cref="GetEventsAsync"/> result depends on it, so one invalidation reaches all subscribers.</summary>
    [ComputeMethod]
    Task<long> GetHeadAsync(CancellationToken ct = default);

    /// <summary>
    /// Issues the audience token for the <b>calling</b> flow's scope, resolved server-side (YFex.Tenancy
    /// <c>EffectiveIdentity()</c>). Null when the caller has no scope (then only global events are visible).
    /// Not a compute method — evaluated per call, never cached.
    /// </summary>
    Task<string?> GetAudienceTokenAsync(CancellationToken ct = default);
}

// ── Server-side implementation ────────────────────────────────────────────────

/// <summary>
/// Default in-memory server-side implementation of <see cref="IEventChannel{TEvent}"/>.
/// Stores events in a bounded ring buffer (10k entries). The Wolverine event bridge
/// calls <see cref="Append"/> which invalidates the compute method, triggering push
/// to all subscribed clients.
/// <para><b>Tenant isolation.</b> Each event is stamped with an <i>audience</i>: the publishing flow's scope inside
/// <see cref="EventChannelOptions.Audience"/>. A subscriber sees global events plus events whose audience its own scope
/// is at or beneath (<see cref="ScopeIdentity.IsWithin"/>). Without tenancy every audience is empty, so every event is
/// global — the single-tenant behavior.</para>
/// </summary>
public class EventChannelHost<TEvent> : IEventChannel<TEvent> where TEvent : IEvent
{
    private const int MaxBuffer = 10_000;
    private readonly List<(long Seq, TEvent Evt, ScopeIdentity Audience)> _buffer = [];
    private readonly object _lock = new();
    private readonly IScopeContext? _scope;
    private readonly ScopePartitionOptions _audience;
    private readonly byte[] _key;
    private long _sequence;

    public EventChannelHost(
        EventChannelOptions? options = null, IScopeContext? scope = null, ScopePartitionOptions? isolation = null)
    {
        _scope = scope;
        _audience = options?.Audience ?? isolation?.Clone() ?? new ScopePartitionOptions();
        _key = options?.SigningKey ?? RandomNumberGenerator.GetBytes(32);
    }

    public virtual async Task<EventCursor<TEvent>> GetEventsAsync(
        EventFilter filter, long sinceSequence, CancellationToken ct = default)
    {
        await GetHeadAsync(ct).ConfigureAwait(false); // dependency: Append invalidates every filter/cursor at once

        var subscriber = TryReadToken(filter.AudienceToken, out var audience) ? audience : ScopeIdentity.None;
        lock (_lock)
        {
            List<TEvent> items = [];
            for (int i = 0; i < _buffer.Count; i++)
            {
                var (seq, evt, eventAudience) = _buffer[i];
                if (seq > sinceSequence && (eventAudience.IsEmpty || (!subscriber.IsEmpty && subscriber.IsWithin(eventAudience))))
                    items.Add(evt);
            }
            return new EventCursor<TEvent>(_sequence, items);
        }
    }

    public virtual Task<long> GetHeadAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_sequence);
    }

    public virtual Task<string?> GetAudienceTokenAsync(CancellationToken ct = default)
    {
        var audience = _audience.Apply(_scope.EffectiveIdentity());
        return Task.FromResult(audience.IsEmpty ? null : IssueToken(audience));
    }

    /// <summary>
    /// Called by the Wolverine bridge to publish a new event, from the publishing flow — its scope becomes the audience.
    /// Wrap deliberate broadcasts in <c>ScopeOverride.EnterUnscoped()</c>.
    /// </summary>
    public void Append(TEvent evt)
    {
        var audience = _audience.Apply(_scope.EffectiveIdentity());
        lock (_lock)
        {
            _sequence++;
            _buffer.Add((_sequence, evt, audience));
            if (_buffer.Count > MaxBuffer) _buffer.RemoveAt(0);
        }

        // Invalidate the shared dependency so all [ComputeMethod] subscribers (any filter/cursor) are notified.
        using (Invalidation.Begin())
            _ = GetHeadAsync();
    }

    public Task AppendAsync(TEvent evt, CancellationToken ct = default)
    {
        Append(evt);
        return Task.CompletedTask;
    }

    // Token = "{scope text}.{base64url HMAC-SHA256(scope text)}"; the scope text may contain '.', so split on the last one.
    private string IssueToken(in ScopeIdentity audience)
    {
        var text = audience.ToString();
        return text + "." + Base64Url.EncodeToString(Sign(text));
    }

    private bool TryReadToken(string? token, out ScopeIdentity audience)
    {
        audience = ScopeIdentity.None;
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.LastIndexOf('.');
        if (dot <= 0) return false;

        var text = token[..dot];
        byte[] mac;
        try { mac = Base64Url.DecodeFromChars(token.AsSpan(dot + 1)); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(mac, Sign(text)) && ScopeIdentity.TryParse(text, out audience);
    }

    private byte[] Sign(string text) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(text));
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
    private EventFilter _filter = new();
    private readonly StateFactory _stateFactory;
    private long _lastSequence;
    private ComputedState<EventCursor<TEvent>>? _state;
    private bool _disposed;

    public FusionEventStream(
        IEventChannel<TEvent> channel,
        IEventBus eventBus,
        StateFactory stateFactory)
    {
        _channel = channel;
        _eventBus = eventBus;
        _stateFactory = stateFactory;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        // The audience comes from the server (a signed token for this connection's scope), never from the client.
        _filter = new EventFilter(await _channel.GetAudienceTokenAsync(ct).ConfigureAwait(false));
        _state = _stateFactory.NewComputed<EventCursor<TEvent>>(
            cct => _channel.GetEventsAsync(_filter, _lastSequence, cct));
        _state.Updated += OnUpdated;
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
