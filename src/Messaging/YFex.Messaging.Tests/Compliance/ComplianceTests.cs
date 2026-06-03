using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Compliance;

/// <summary>Tests #36â€“39: No cache without ICacheable, YFINV001 detection, three-tier merge.</summary>
[Trait("Category", "Compliance")]
[Collection("DispatcherTests")]
public sealed class ComplianceTests
{
    public ComplianceTests() => TestDispatcherFixture.ClearStore();

    // â”€â”€ Test #36: Nothing cached without ICacheable â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task QueryWithoutICacheable_DoesNotWriteToCache()
    {
        TestDispatcherFixture.Store[1] = new TestItem(1, "A");
        var fx = new TestDispatcherFixture();

        _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetAllQuery, List<TestItem>>(
            new TestAggregate.Queries.GetAllQuery());

        var keys = await fx.Cache.GetKeysWithPrefixAsync("query:");
        keys.Should().BeEmpty("non-ICacheable queries must never write to IClientCache");
    }

    [Fact]
    public async Task QueryWithICacheable_WritesToCache()
    {
        TestDispatcherFixture.Store[2] = new TestItem(2, "Cached");
        var fx = new TestDispatcherFixture(configurations: [new CacheableQueryConfig()]);

        _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(
            new TestAggregate.Queries.GetByIdQuery(2));

        var keys = await fx.Cache.GetKeysWithPrefixAsync("query:");
        keys.Should().NotBeEmpty("ICacheable query must write its result to IClientCache");
    }

    // â”€â”€ Test #38: YFINV001 â€“ conflicting Invalidates + InvalidatedBy â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Skip = "YFINV001 conflict detection not yet implemented in CompiledMessagingRegistry.Build")]
    public void Build_Throws_WhenInvalidatesAndInvalidatedByConflict()
    {
        var act = () => CompiledMessagingRegistry.Build(baseline: [new DualInvalidationConfig()]);

        act.Should().Throw<InvalidOperationException>(
            "YFINV001: declaring both Invalidates<TQuery> on a command AND InvalidatedBy<TCommand> " +
            "on the same query is a configuration error");
    }

    // â”€â”€ Test #39: Three-tier configuration merge â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void ThreeTier_ClientOverride_WinsOverBaseline()
    {
        var registry = CompiledMessagingRegistry.Build(
            baseline: [new BaselineOnlyConfig()],
            clientOverrides: [new ClientCacheOverrideConfig()]);

        registry.Queries.Should().ContainKey(typeof(TestAggregate.Queries.GetByIdQuery));
        registry.Queries[typeof(TestAggregate.Queries.GetByIdQuery)].Cache.Should().NotBeNull(
            "client override must supersede the baseline â€” it adds a cache policy");
    }

    [Fact]
    public void ThreeTier_ServerOverride_AddsValidator()
    {
        var registry = CompiledMessagingRegistry.Build(
            baseline: [new BaselineOnlyConfig()],
            serverOverrides: [new ServerOnlyConfig()]);

        registry.Commands[typeof(TestAggregate.Commands.CreateCommand)].Validate
            .Should().NotBeNull("server override must apply its validator");
    }

    // â”€â”€ Private configurations (skipped by Pipeline B) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private sealed class CacheableQueryConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>().Cacheable();
    }

    private sealed class DualInvalidationConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
        {
            b.Command<TestAggregate.Commands.CreateCommand, TestItem>()
                .Invalidates<TestAggregate.Queries.GetByIdQuery, TestItem>();
            b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>()
                .InvalidatedBy<TestAggregate.Commands.CreateCommand>();
        }
    }

    private sealed class BaselineOnlyConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
        {
            b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>();
            b.Command<TestAggregate.Commands.CreateCommand, TestItem>();
        }
    }

    private sealed class ClientCacheOverrideConfig : IClientAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>()
                .Cacheable(c => c.SlidingExpiration(TimeSpan.FromMinutes(10)));
    }

    private sealed class ServerOnlyConfig : IServerAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.CreateCommand, TestItem>()
                .Validate((cmd, ct) => ValueTask.FromResult(
                    cmd.Name.Length > 0
                        ? ValidationResult.Success()
                        : ValidationResult.Failure("Name", "Name is required")));
    }
}
