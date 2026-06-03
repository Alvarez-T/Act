using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Cqrs;

/// <summary>Tests #7â€“8: Optimistic update apply and cache behavior after command dispatch.</summary>
[Trait("Category", "Cqrs")]
[Collection("DispatcherTests")]
public sealed class OptimisticTests
{
    public OptimisticTests() => TestDispatcherFixture.ClearStore();

    // â”€â”€ Test #7: Optimistic apply â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Optimistic_AppliesUpdateToCache_AfterCommandSucceeds()
    {
        TestDispatcherFixture.Store[10] = new TestItem(10, "Original");
        var fx = new TestDispatcherFixture(configurations: [new OptimisticConfig()]);

        // First query populates the cache
        _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(
            new TestAggregate.Queries.GetByIdQuery(10));

        // Rename command â€” optimistic rule applies the cache update after success
        _ = await fx.Dispatcher.CommandAsync<TestAggregate.Commands.RenameCommand, TestItem>(
            new TestAggregate.Commands.RenameCommand(10, "Renamed"));

        // Re-query should serve the optimistically-updated cached value
        var after = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(
            new TestAggregate.Queries.GetByIdQuery(10));

        after.IsOk.Should().BeTrue();
        after.OkValue!.Name.Should().Be("Renamed",
            "optimistic apply must mutate the cached value after the command succeeds");
    }

    // â”€â”€ Test #8: Cache unchanged when command not dispatched â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Optimistic_CacheRemainsOriginal_WhenCommandNotRun()
    {
        TestDispatcherFixture.Store[11] = new TestItem(11, "Stable");
        var fx = new TestDispatcherFixture(configurations: [new OptimisticConfig()]);

        _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(
            new TestAggregate.Queries.GetByIdQuery(11));

        // No command dispatched
        var second = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(
            new TestAggregate.Queries.GetByIdQuery(11));

        second.OkValue!.Name.Should().Be("Stable", "cache must not change without a command");
    }

    // â”€â”€ Configuration (private â€” skipped by Pipeline B) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private sealed class OptimisticConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
        {
            b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>()
                .Cacheable();

            b.Command<TestAggregate.Commands.RenameCommand, TestItem>()
                .Optimistic<TestAggregate.Queries.GetByIdQuery, TestItem>(
                    // match operates on the cached RESULT (TestItem) and the command
                    match: (cached, cmd) => cached.Id == cmd.Id,
                    apply: (cached, cmd) => new TestItem(cmd.Id, cmd.NewName, cached.IsDeleted));
        }
    }
}
