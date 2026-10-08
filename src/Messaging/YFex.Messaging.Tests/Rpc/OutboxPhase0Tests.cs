using Microsoft.Extensions.DependencyInjection;
using YFex.Messaging.Rpc;
using YFex.Messaging.Tests.Fixtures;
using YFex.Persistence;

namespace YFex.Messaging.Tests.Rpc;

/// <summary>
/// Offline Phase 0 (docs/internal/offline/11 §3): the outbox records who queued a command and where (F4), orders replays by a
/// persisted sequence instead of the wall clock (F5), and the client really uses the durable outbox when a durable store is
/// registered (F14).
/// </summary>
[Trait("Category", "Rpc")]
public sealed class OutboxPhase0Tests
{
    private const string LojaCentro = "0=farmaviva/1=loja-centro";

    [Fact]
    public async Task In_memory_outbox_records_caller_scope_and_sequence()
    {
        var capture = new FixedCallerCapture { Caller = new("ana", LojaCentro) };
        var outbox = new InMemoryOutbox(new OutboxOptions(), callerCapture: capture);

        await outbox.EnqueueAsync(new TestAggregate.Commands.CreateCommand(1, "Venda 1"));
        capture.Caller = new("bruno", LojaCentro);   // shift change before the connection returns
        await outbox.EnqueueAsync(new TestAggregate.Commands.CreateCommand(2, "Venda 2"));

        var pending = await outbox.ListPendingAsync();
        pending.Select(e => (e.Subject, e.Scope, e.Sequence)).Should().Equal(("ana", LojaCentro, 1L), ("bruno", LojaCentro, 2L));
    }

    [Fact]
    public async Task Storage_outbox_sequence_survives_a_restart_and_orders_the_replay()
    {
        var store = new MemoryKeyValueStore();   // stands in for the SQLite file
        var beforeRestart = new StorageBackedOutbox(store, new OutboxOptions(), callerCapture: new FixedCallerCapture { Caller = new("ana", LojaCentro) });
        await beforeRestart.EnqueueAsync(new TestAggregate.Commands.CreateCommand(1, "Abrir caixa"));
        await beforeRestart.EnqueueAsync(new TestAggregate.Commands.CreateCommand(2, "Venda"));

        var afterRestart = new StorageBackedOutbox(store, new OutboxOptions());
        await afterRestart.EnqueueAsync(new TestAggregate.Commands.CreateCommand(3, "Fechar caixa"));

        var pending = await afterRestart.ListPendingAsync();
        pending.Select(e => e.Sequence).Should().Equal(1, 2, 3);
        pending[0].Subject.Should().Be("ana");
        pending[0].Scope.Should().Be(LojaCentro);
        pending[2].Subject.Should().BeNull("no capture was configured for the third enqueue");
    }

    [Fact]
    public void Replay_order_is_by_sequence_never_by_wall_clock_and_legacy_entries_go_first()
    {
        var now = DateTimeOffset.UtcNow;
        OutboxEntry Entry(long sequence, TimeSpan offset) => new(Guid.NewGuid(), "T", [], now + offset, 0, null, null, Sequence: sequence);

        var clockSetBack = Entry(2, TimeSpan.FromHours(-3));   // enqueued after #1, but the clock was moved back
        var first = Entry(1, TimeSpan.Zero);
        var legacyOld = Entry(0, TimeSpan.FromDays(-2));
        var legacyNew = Entry(0, TimeSpan.FromDays(-1));

        var ordered = new[] { clockSetBack, legacyNew, first, legacyOld }.Order(Comparer<OutboxEntry>.Create(OutboxEntry.CompareReplayOrder)).ToArray();

        ordered.Should().Equal(legacyOld, legacyNew, first, clockSetBack);
    }

    [Fact]
    public void Client_uses_the_durable_outbox_when_a_durable_store_is_registered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IKeyValueStore>(new DurableStoreStandIn());
        services.AddYFexMessagingRpcClient();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IOutbox>().Should().BeOfType<StorageBackedOutbox>();
        sp.GetRequiredService<ISyncFailureLog>().Should().BeOfType<StorageBackedSyncFailureLog>();
        sp.GetRequiredService<IWritableSyncFailureLog>().Should().BeSameAs(sp.GetRequiredService<ISyncFailureLog>());
    }

    [Fact]
    public void Client_keeps_the_in_memory_outbox_for_an_in_memory_store_or_when_asked()
    {
        var defaults = new ServiceCollection();
        defaults.AddLogging();
        defaults.AddYFexMessagingRpcClient();
        using (var sp = defaults.BuildServiceProvider())
            sp.GetRequiredService<IOutbox>().Should().BeOfType<InMemoryOutbox>();

        var forced = new ServiceCollection();
        forced.AddLogging();
        forced.AddSingleton<IKeyValueStore>(new DurableStoreStandIn());
        forced.AddYFexMessagingRpcClient(o => o.Storage = ClientStorageBackend.InMemory);
        using (var sp = forced.BuildServiceProvider())
            sp.GetRequiredService<IOutbox>().Should().BeOfType<InMemoryOutbox>();
    }

    private sealed class FixedCallerCapture : IOutboxCallerCapture
    {
        public OutboxCaller Caller { get; set; }
        public OutboxCaller Capture() => Caller;
    }

    /// <summary>Any store that is not <see cref="MemoryKeyValueStore"/> counts as durable (SQLite, IndexedDB, encrypted…).</summary>
    private sealed class DurableStoreStandIn : IKeyValueStore
    {
        private readonly MemoryKeyValueStore _inner = new();
        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default) => _inner.GetAsync(key, ct);
        public ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default) => _inner.SetAsync(key, value, ttl, ct);
        public ValueTask DeleteAsync(string key, CancellationToken ct = default) => _inner.DeleteAsync(key, ct);
        public ValueTask<bool> ExistsAsync(string key, CancellationToken ct = default) => _inner.ExistsAsync(key, ct);
        public ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default) => _inner.GetKeysWithPrefixAsync(prefix, ct);
        public ValueTask ClearAsync(CancellationToken ct = default) => _inner.ClearAsync(ct);
    }
}
