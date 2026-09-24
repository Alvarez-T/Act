using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Cqrs;

/// <summary>Tests #1â€“2: In-process dispatch and configuration resolution.</summary>
[Trait("Category", "Cqrs")]
[Collection("DispatcherTests")]
public sealed class InProcessCqrsTests
{
    public InProcessCqrsTests() => TestDispatcherFixture.ClearStore();

    // â”€â”€ Test #1: In-process CQRS â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Query_DispatchesToHandler_AndReturnsResult()
    {
        TestDispatcherFixture.Store[1] = new TestItem(1, "Alice");
        var fx = new TestDispatcherFixture();

        var result = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(
            new TestAggregate.Queries.GetByIdQuery(1));

        result.IsOk.Should().BeTrue();
        result.OkValue!.Name.Should().Be("Alice");
    }

    [Fact]
    public async Task Command_DispatchesToHandler_AndReturnsResult()
    {
        var fx = new TestDispatcherFixture();

        var result = await fx.Dispatcher.CommandAsync<TestAggregate.Commands.CreateCommand, TestItem>(
            new TestAggregate.Commands.CreateCommand(42, "Bob"));

        result.IsOk.Should().BeTrue("online create should succeed");
        result.OkValue!.Id.Should().Be(42);
        result.OkValue.Name.Should().Be("Bob");
        TestDispatcherFixture.Store.Should().ContainKey(42);
    }

    [Fact]
    public async Task VoidCommand_DispatchesToHandler_AndReturnsOk()
    {
        TestDispatcherFixture.Store[5] = new TestItem(5, "Eve");
        var fx = new TestDispatcherFixture();

        var result = await fx.Dispatcher.CommandAsync<TestAggregate.Commands.DeleteCommand>(
            new TestAggregate.Commands.DeleteCommand(5));

        result.IsOk.Should().BeTrue();
        TestDispatcherFixture.Store.Should().NotContainKey(5);
    }

    // â”€â”€ Test #2: Configuration resolution â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Registry_Builds_WithCommandPolicy_WhenConfigured()
    {
        var fx = new TestDispatcherFixture(configurations: [new TestAggregateConfiguration()]);

        fx.Registry.Commands.Should().ContainKey(typeof(TestAggregate.Commands.CreateCommand));
        fx.Registry.Queries.Should().ContainKey(typeof(TestAggregate.Queries.GetByIdQuery));
    }

    [Fact]
    public void Registry_CommandPolicy_HasInvalidationTargets_WhenConfigured()
    {
        var fx = new TestDispatcherFixture(configurations: [new TestAggregateConfiguration()]);

        var policy = fx.Registry.Commands[typeof(TestAggregate.Commands.CreateCommand)];
        policy.InvalidationTargets.Should().NotBeNullOrEmpty(
            "CreateCommand.Invalidates<GetByIdQuery, TestItem>() was registered");
    }

    [Fact]
    public void Registry_CommandPolicy_ComposesResilienceSpec_FromVerbs()
    {
        var fx = new TestDispatcherFixture(configurations: [new TestAggregateConfiguration()]);

        var policy = fx.Registry.Commands[typeof(TestAggregate.Commands.CreateCommand)];
        policy.Resilience.Should().NotBeNull(".UseResource(...) + .Timeout(...) fold into a ResilienceSpec (design §1 L2)");
        policy.Resilience!.Value.ResourceName.Should().Be("sefaz");
        policy.Resilience.Value.Timeout.Should().NotBeNull();
        policy.Resilience.Value.Timeout!.Value.Timeout.Should().Be(TimeSpan.FromSeconds(8));
    }

    // â”€â”€ Helper configuration (internal to this file â€” skipped by Pipeline B) â”€â”€

    private sealed class TestAggregateConfiguration : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
        {
            b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>()
                .Cacheable();

            b.Command<TestAggregate.Commands.CreateCommand, TestItem>()
                .Invalidates<TestAggregate.Queries.GetByIdQuery, TestItem>()
                .UseResource("sefaz")
                .Timeout(TimeSpan.FromSeconds(8));

            b.Command<TestAggregate.Commands.DeleteCommand>()
                .Invalidates<TestAggregate.Queries.GetByIdQuery, TestItem>()
                .Invalidates<TestAggregate.Queries.GetAllQuery, List<TestItem>>();
        }
    }
}
