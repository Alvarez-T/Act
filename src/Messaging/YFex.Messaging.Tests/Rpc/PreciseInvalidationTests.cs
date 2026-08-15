using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Rpc;

/// <summary>
/// Entity-tag (precise) invalidation: a command that declares <c>Invalidates&lt;TQuery&gt;(cmd =&gt; cmd.Id)</c>
/// against a query tagged with <c>TaggedBy(q =&gt; q.Id)</c> drops only the matching cached variant,
/// leaving other entities' entries intact. Contrasts with the coarse (type-tag) default.
/// </summary>
[Trait("Category", "Rpc")]
[Collection("DispatcherTests")]
public sealed class PreciseInvalidationTests
{
    public PreciseInvalidationTests() => TestDispatcherFixture.ClearStore();

    [Fact]
    public async Task CommandWithEntityKey_InvalidatesOnlyMatchingVariant()
    {
        TestDispatcherFixture.Store[2] = new TestItem(2, "Two");
        TestDispatcherFixture.Store[3] = new TestItem(3, "Three");
        var fx = new TestDispatcherFixture(configurations: [new PreciseConfig()]);

        // Cache two variants of the same query type.
        _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(new(2));
        _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(new(3));
        (await fx.Cache.GetKeysWithPrefixAsync("query:")).Should().HaveCount(2);

        // Rename #2 → should drop ONLY the id=2 entry (en:GetByIdQuery:2), not id=3.
        _ = await fx.Dispatcher.CommandAsync<TestAggregate.Commands.RenameCommand, TestItem>(new(2, "Renamed"));

        var remaining = await fx.Cache.GetKeysWithPrefixAsync("query:");
        remaining.Should().HaveCount(1, "precise invalidation must remove only the matching entity's variant");
    }

    [Fact]
    public async Task CommandWithoutEntityKey_InvalidatesAllVariants()
    {
        TestDispatcherFixture.Store[2] = new TestItem(2, "Two");
        TestDispatcherFixture.Store[3] = new TestItem(3, "Three");
        var fx = new TestDispatcherFixture(configurations: [new CoarseConfig()]);

        _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(new(2));
        _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(new(3));
        (await fx.Cache.GetKeysWithPrefixAsync("query:")).Should().HaveCount(2);

        _ = await fx.Dispatcher.CommandAsync<TestAggregate.Commands.RenameCommand, TestItem>(new(2, "Renamed"));

        var remaining = await fx.Cache.GetKeysWithPrefixAsync("query:");
        remaining.Should().BeEmpty("coarse invalidation drops every variant of the query type");
    }

    private sealed class PreciseConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
        {
            b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>()
                .Cacheable()
                .TaggedBy(q => q.Id);
            b.Command<TestAggregate.Commands.RenameCommand, TestItem>()
                .Invalidates<TestAggregate.Queries.GetByIdQuery, TestItem>(cmd => cmd.Id);
        }
    }

    private sealed class CoarseConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
        {
            b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>()
                .Cacheable();
            b.Command<TestAggregate.Commands.RenameCommand, TestItem>()
                .Invalidates<TestAggregate.Queries.GetByIdQuery, TestItem>();
        }
    }
}
