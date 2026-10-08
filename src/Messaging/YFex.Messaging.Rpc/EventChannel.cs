using System.Text;
using ActualLab.Fusion;
using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;
using YFex.Cryptography;
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
/// <see cref="IEventChannel{TEvent}.GetAudienceTokenAsync"/>; without one (or once it expired) a subscriber receives only global
/// (unscoped) events. The audience is never taken from a client-supplied value: the token is a YFex.Cryptography
/// <c>MessageToken</c> (key id, expiry, domain <c>yfex.event-audience.v1</c>, HMAC tag), so a Fusion-cached result keyed by it is
/// reachable only by callers the server issued it to, and only until it expires (cryptography C4).
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
    /// Key ring of the audience tokens (default <c>"messaging/event-audience"</c>). When the app configures it in
    /// <c>AddYFexCryptography</c> (see <see cref="EventChannelCryptographyExtensions.EventAudienceTokens"/>), every node shares it,
    /// keys rotate and a token issued by one node reads on all of them. Not configured: a key generated per process (clients
    /// re-fetch a token after a restart, as before).
    /// </summary>
    public string KeyRing { get; set; } = "messaging/event-audience";

    /// <summary>How long an audience token is valid (default 12 hours). Clients re-fetch it before it expires.</summary>
    public TimeSpan AudienceTokenLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>A fixed HMAC key shared by the nodes (16+ bytes). Superseded by the shared key ring <see cref="KeyRing"/>.</summary>
    [Obsolete("Configure the shared, rotating key ring instead: cryptography.EventAudienceTokens() in AddYFexCryptography (cryptography 09 I7).")]
    public byte[]? SigningKey { get; set; }
}

/// <summary>Key-ring configuration for <see cref="EventChannelHost{TEvent}"/> audience tokens.</summary>
public static class EventChannelCryptographyExtensions
{
    /// <summary>
    /// Configures the shared ring of audience-token keys (cryptography 06 §6): MAC keys rotated every 30 days, kept for one token
    /// lifetime after rotation, new keys propagated to every node before use.
    /// <code>
    /// services.AddYFexCryptography(cryptography => cryptography
    ///     .UseEnvironmentRootKey().StoreKeyRingsInFiles(keys)
    ///     .EventAudienceTokens());
    /// </code>
    /// </summary>
    public static CryptographyBuilder EventAudienceTokens(this CryptographyBuilder cryptography, string ring = "messaging/event-audience",
        TimeSpan? tokenLifetime = null, TimeSpan? rotateEvery = null)
    {
        ArgumentNullException.ThrowIfNull(cryptography);
        TimeSpan lifetime = tokenLifetime ?? TimeSpan.FromHours(12);
        return cryptography.KeyRing(ring, keys => keys
            .For(KeyPurpose.MessageAuthentication)
            .RotateEvery(rotateEvery ?? TimeSpan.FromDays(30))
            .PropagateFor(TimeSpan.FromMinutes(10))
            .KeepOpenOnlyFor(lifetime + TimeSpan.FromMinutes(10)));
    }
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
    private const int MaxTokenLength = 4096;
    private static readonly MessageDomain AudienceDomain = new("yfex.event-audience.v1");
    private static readonly MessageDomain LegacyKeyIdDomain = new("yfex.event-audience.key-id.v1");
    private readonly List<(long Seq, TEvent Evt, ScopeIdentity Audience)> _buffer = [];
    private readonly object _lock = new();
    private readonly IScopeContext? _scope;
    private readonly ScopePartitionOptions _audience;
    private readonly KeyRingProvider? _rings;
    private readonly KeyRingName _ringName;
    private readonly KeyRing? _localRing;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;
    private long _sequence;

    /// <param name="rings">The app's key rings: used when <see cref="EventChannelOptions.KeyRing"/> is configured there (shared by nodes).</param>
    /// <param name="time">Token expiry clock (default system time).</param>
    public EventChannelHost(
        EventChannelOptions? options = null, IScopeContext? scope = null, ScopePartitionOptions? isolation = null,
        KeyRingProvider? rings = null, TimeProvider? time = null)
    {
        options ??= new EventChannelOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.AudienceTokenLifetime, TimeSpan.Zero);
        _scope = scope;
        _audience = options.Audience ?? isolation?.Clone() ?? new ScopePartitionOptions();
        _ringName = new KeyRingName(options.KeyRing);
        _lifetime = options.AudienceTokenLifetime;
        _time = time ?? TimeProvider.System;
#pragma warning disable CS0618 // the obsolete fixed key still works for apps that set it
        if (options.SigningKey is { } fixedKey) _localRing = FixedKeyRing(_ringName, fixedKey);
#pragma warning restore CS0618
        else if (rings is not null && rings.IsConfigured(_ringName)) _rings = rings;
        else _localRing = KeyRing.FromKey(_ringName, SecretKey.Generate(KeyPurpose.MessageAuthentication));
    }

    public virtual async Task<EventCursor<TEvent>> GetEventsAsync(
        EventFilter filter, long sinceSequence, CancellationToken ct = default)
    {
        await GetHeadAsync(ct).ConfigureAwait(false); // dependency: Append invalidates every filter/cursor at once

        // An expired token reads as "no audience": only global events. No new scoped event can reach it, because every Append
        // invalidates this result and the recomputation sees the expiry.
        var subscriber = await ReadTokenAsync(filter.AudienceToken, ct).ConfigureAwait(false);
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

    public virtual async Task<string?> GetAudienceTokenAsync(CancellationToken ct = default)
    {
        var audience = _audience.Apply(_scope.EffectiveIdentity());
        if (audience.IsEmpty) return null;
        KeyRing ring = await RingAsync(ct).ConfigureAwait(false);
        return MessageToken.Issue(ring, AudienceDomain, Encoding.UTF8.GetBytes(audience.ToString()), _time.GetUtcNow() + _lifetime);
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

    // Token = MessageToken v1 (cryptography 05 §6): version ‖ key id ‖ expiry ‖ UTF-8 scope text ‖ tag, base64url.
    private async ValueTask<ScopeIdentity> ReadTokenAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) return ScopeIdentity.None;
        KeyRing ring = await RingAsync(ct).ConfigureAwait(false);
        byte[] payload = new byte[MessageToken.GetMaxPayloadLength(token.Length)];
        TokenCheck check = MessageToken.Read(ring, AudienceDomain, token, _time.GetUtcNow(), payload, out int written);
        if (check == TokenCheck.UnknownKey && _rings is not null)
        {
            // Another node rotated first: reload the shared ring once (rate-limited by the provider) and retry.
            ring = await _rings.ReloadAsync(_ringName, ct).ConfigureAwait(false);
            check = MessageToken.Read(ring, AudienceDomain, token, _time.GetUtcNow(), payload, out written);
        }
        return check == TokenCheck.Valid && ScopeIdentity.TryParse(Encoding.UTF8.GetString(payload, 0, written), out var audience)
            ? audience
            : ScopeIdentity.None;
    }

    private ValueTask<KeyRing> RingAsync(CancellationToken ct)
    {
        if (_localRing is not null) return ValueTask.FromResult(_localRing);
        return _rings!.TryGetCached(_ringName, out KeyRing? cached) ? ValueTask.FromResult(cached) : _rings.GetAsync(_ringName, ct);
    }

    // The obsolete shared key: the same bytes give the same key id on every node, so tokens read across nodes.
    private static KeyRing FixedKeyRing(KeyRingName name, byte[] material)
    {
        if (material.Length < 16) throw new ArgumentException("EventChannelOptions.SigningKey needs at least 16 bytes.", nameof(material));
        using SecretKey probe = SecretKey.Import(KeyId.New(), KeyPurpose.MessageAuthentication, material);
        Span<byte> id = stackalloc byte[MessageAuthentication.TagLength];
        MessageAuthentication.ComputeTag(probe, LegacyKeyIdDomain, ReadOnlySpan<byte>.Empty, id);
        return KeyRing.FromKey(name, SecretKey.Import(KeyId.Read(id), KeyPurpose.MessageAuthentication, material));
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
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    private readonly IEventChannel<TEvent> _channel;
    private readonly IEventBus _eventBus;
    private EventFilter _filter = new();
    private readonly StateFactory _stateFactory;
    private readonly CancellationTokenSource _stop = new();
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
        await SubscribeAsync(ct).ConfigureAwait(false);
        _ = RefreshBeforeExpiryAsync(_stop.Token);
    }

    // The audience comes from the server (a signed token for this connection's scope), never from the client.
    private async Task SubscribeAsync(CancellationToken ct)
    {
        var filter = new EventFilter(await _channel.GetAudienceTokenAsync(ct).ConfigureAwait(false));
        var state = _stateFactory.NewComputed<EventCursor<TEvent>>(cct => _channel.GetEventsAsync(filter, _lastSequence, cct));
        state.Updated += OnUpdated;
        _filter = filter;
        var previous = Interlocked.Exchange(ref _state, state);
        if (previous is not null)
        {
            previous.Updated -= OnUpdated;
            previous.Dispose();
        }
    }

    // Audience tokens expire (cryptography C4): fetch a new one when 90 % of the remaining lifetime has passed. The server
    // re-resolves the caller's scope, so a user who lost access to a store stops receiving its events.
    private async Task RefreshBeforeExpiryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_filter.AudienceToken is not { } token || !MessageToken.TryReadHeader(token, out _, out DateTimeOffset expiresAt)) return;
            TimeSpan wait = (expiresAt - DateTimeOffset.UtcNow) * 0.9;
            wait = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait;
            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
                await SubscribeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Server unreachable: keep the current subscription and try again shortly.
                try { await Task.Delay(RetryDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
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
        _stop.Cancel();
        _stop.Dispose();
        if (Interlocked.Exchange(ref _state, null) is { } state)
        {
            state.Updated -= OnUpdated;
            state.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
