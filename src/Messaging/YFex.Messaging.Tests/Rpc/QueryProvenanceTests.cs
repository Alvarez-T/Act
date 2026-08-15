using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Rpc;

/// <summary>
/// Provenance on <see cref="CacheableQueryResult{T}"/>: a query success now carries <i>where</i> the value
/// came from — <c>Fresh</c> (live), <c>Cached</c> (offline, still valid), or <c>Stale</c> (offline,
/// past an offline-command invalidation). Reproduces the reported gap: offline invalidation keeps the
/// entry and serves it, and the UI can now tell a stale read from a live one.
/// </summary>
[Trait("Category", "Rpc")]
[Collection("DispatcherTests")]
public sealed class QueryProvenanceTests
{
    public QueryProvenanceTests() => TestDispatcherFixture.ClearStore();

    [Fact]
    public async Task OnlineQuery_IsFresh_NotFromCache()
    {
        TestDispatcherFixture.Store[1] = new TestItem(1, "Live");
        var fx = new TestDispatcherFixture(configurations: [new CacheableConfig()]);

        var result = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(new(1));

        result.IsFresh.Should().BeTrue("an online, authoritative read is Fresh");
        result.FromCache.Should().BeFalse();
        result.IsStale.Should().BeFalse();
        result.OkValue!.Name.Should().Be("Live");
    }

    [Fact]
    public async Task OfflineQuery_WithinCache_IsCached_NotStale()
    {
        TestDispatcherFixture.Store[5] = new TestItem(5, "Cached");
        var fx = new TestDispatcherFixture(configurations: [new CacheableConfig()]);

        // Populate the cache online, then serve it offline with no invalidation.
        _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(new(5));
        fx.Network.GoOffline();
        var result = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(new(5));

        result.IsOk.Should().BeTrue("a valid cached entry is still served offline");
        result.IsCached.Should().BeTrue("served from cache, but not past any invalidation");
        result.FromCache.Should().BeTrue();
        result.IsStale.Should().BeFalse("no offline command invalidated it");
        result.IsFresh.Should().BeFalse();
        result.OkValue!.Name.Should().Be("Cached");
    }

    [Fact]
    public async Task OfflineQuery_AfterQueuedInvalidation_IsStale_ServesOldValue()
    {
        TestDispatcherFixture.Store[3] = new TestItem(3, "Original");
        var fx = new TestDispatcherFixture(configurations: [new InvalidationConfig()]);

        // 1. Cache the query online.
        _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(new(3));

        // 2. Go offline and enqueue a command that invalidates the query → marks the entry stale (kept).
        fx.Network.GoOffline();
        _ = await fx.Dispatcher.CommandAsync<TestAggregate.Commands.CreateCommand, TestItem>(new(3, "Updated"));

        // 3. Query again offline — served from cache, now flagged stale.
        var result = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(new(3));

        result.IsOk.Should().BeTrue("the stale entry is kept and still served offline");
        result.IsStale.Should().BeTrue("an offline queued invalidation marks the served value stale");
        result.FromCache.Should().BeTrue();
        result.IsFresh.Should().BeFalse();
        result.OkValue!.Name.Should().Be("Original",
            "offline serves the old cached value — the UI shows it, now knowing it is stale");
    }

    [Fact]
    public async Task OfflineQuery_NoCachedValue_IsError()
    {
        TestDispatcherFixture.Store[9] = new TestItem(9, "NeverCached");
        var fx = new TestDispatcherFixture(configurations: [new CacheableConfig()], startConnected: false);

        var result = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(new(9));

        result.IsError.Should().BeTrue("no cached value available offline");
        result.IsOk.Should().BeFalse();
        result.FromCache.Should().BeFalse();
    }

    // ── Configurations ────────────────────────────────────────────────────────

    private sealed class CacheableConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>().Cacheable();
    }

    private sealed class InvalidationConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
        {
            b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>().Cacheable();
            b.Command<TestAggregate.Commands.CreateCommand, TestItem>()
                .Invalidates<TestAggregate.Queries.GetByIdQuery, TestItem>();
        }
    }
}
