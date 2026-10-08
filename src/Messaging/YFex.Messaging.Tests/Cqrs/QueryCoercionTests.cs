using YFex.Cqrs;
using YFex.Messaging.Tests.Fixtures;
using CachedSearch = YFex.Messaging.Tests.Fixtures.ShopAggregate.Queries.CachedSearch;
using PagedCustomers = YFex.Messaging.Tests.Fixtures.ShopAggregate.Queries.PagedCustomers;

namespace YFex.Messaging.Tests.Cqrs;

/// <summary>Validation Phase 3, step 5 (09 §7): coerce before the cache key; invalid queries never cached.</summary>
public sealed class QueryCoercionTests
{
    [Fact]
    public void Generated_Coerce_clamps_range_members()
    {
        PagedCustomers.Coerce(new PagedCustomers("a", 0)).Take.Should().Be(1);
        PagedCustomers.Coerce(new PagedCustomers("a", 500)).Take.Should().Be(50);
        PagedCustomers.Coerce(new PagedCustomers("a", 20)).Take.Should().Be(20);
    }

    [Fact]
    public async Task Coerced_query_reaches_the_handler_clamped_and_shares_the_cache_entry()
    {
        var fx = new ShopFixture();

        var first = await fx.Dispatcher.QueryAsync<PagedCustomers, List<string>>(new PagedCustomers("a", 500));
        var second = await fx.Dispatcher.QueryAsync<PagedCustomers, List<string>>(new PagedCustomers("a", 50));

        first.OkValue.Should().Equal("a:50");
        second.OkValue.Should().Equal("a:50");
        fx.Log.Entries.Should().Equal(["paged:50"], "Take=500 was clamped BEFORE the cache key → the second call is a hit");
    }

    [Fact]
    public async Task Invalid_query_never_touches_the_cache()
    {
        var fx = new ShopFixture();

        var r = await fx.Dispatcher.QueryAsync<CachedSearch, List<string>>(new CachedSearch("a", 0));

        r.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.ValidationProblem);
        (await fx.Cache.GetKeysWithPrefixAsync("query:")).Should().BeEmpty();
        fx.Log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Offline_invalid_query_is_rejected_not_served_from_cache()
    {
        var fx = new ShopFixture();
        (await fx.Dispatcher.QueryAsync<CachedSearch, List<string>>(new CachedSearch("a", 5))).IsOk.Should().BeTrue();
        fx.Network.GoOffline();

        var r = await fx.Dispatcher.QueryAsync<CachedSearch, List<string>>(new CachedSearch("a", 0));

        r.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.ValidationProblem, "shape is checked before the offline cache path");
    }
}
