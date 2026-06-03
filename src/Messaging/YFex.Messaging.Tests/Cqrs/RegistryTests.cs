using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Cqrs;

/// <summary>Tests #3–6: Registry group expansion, predicates, ValidateOnStart, and YFINV001.</summary>
[Trait("Category", "Cqrs")]
public sealed class RegistryTests
{
    // ── Test #3: Group expansion via marker interface ─────────────────────────

    [Fact]
    public void Invalidates_MarkerGroup_ExpandsToImplementers()
    {
        var registry = CompiledMessagingRegistry.Build(
            baseline: [new GroupExpansionConfig()],
            scanForImplementers: [typeof(RegistryTests).Assembly]);

        var policy = registry.Commands[typeof(TestAggregate.Commands.CreateCommand)];
        policy.InvalidationTargets.Should().NotBeNullOrEmpty(
            "InvalidatesGroup<ITestItemViews>() must expand to query types implementing the interface");
    }

    // ── Test #4: Match lambda predicate ──────────────────────────────────────

    [Fact]
    public void Invalidates_WithMatchPredicate_PredicateIsCompiledAndFunctional()
    {
        var registry = CompiledMessagingRegistry.Build(baseline: [new MatchPredicateConfig()]);

        var policy = registry.Commands[typeof(TestAggregate.Commands.RenameCommand)];
        policy.InvalidationTargets.Should().NotBeNullOrEmpty();

        var target = policy.InvalidationTargets![0];
        target.Match.Should().NotBeNull("predicate lambda must be compiled to a delegate");

        var query = new TestAggregate.Queries.GetByIdQuery(7);
        var cmdMatch = new TestAggregate.Commands.RenameCommand(7, "New");
        var cmdNoMatch = new TestAggregate.Commands.RenameCommand(99, "Other");

        target.Match!(query, cmdMatch).Should().BeTrue("q.Id==cmd.Id → match");
        target.Match(query, cmdNoMatch).Should().BeFalse("q.Id != cmd.Id → no match");
    }

    // ── Test #5: ValidateOnStart(Strict) ─────────────────────────────────────

    [Fact]
    public void ValidateOnStart_Strict_ThrowsWhenCommandLacksConfiguration()
    {
        var registry = CompiledMessagingRegistry.Build(baseline: []);

        var act = () => registry.Validate(
            ConfigurationValidationLevel.Strict,
            scanForMessages: [typeof(TestAggregate).Assembly],
            logWarning: _ => { });

        act.Should().Throw<InvalidOperationException>(
            "Strict mode must throw when message types have no configuration entry");
    }

    [Fact]
    public void ValidateOnStart_Off_DoesNotThrow()
    {
        var registry = CompiledMessagingRegistry.Build(baseline: []);

        var act = () => registry.Validate(
            ConfigurationValidationLevel.Off,
            scanForMessages: [typeof(TestAggregate).Assembly],
            logWarning: _ => { });

        act.Should().NotThrow();
    }

    // ── Test #38: YFINV001 – both Invalidates and InvalidatedBy for same pair ──

    [Fact(Skip = "YFINV001 conflict detection in CompiledMessagingRegistry.Build not yet implemented — " +
                 "IConflictResolver infrastructure is in place, runtime enforcement is a follow-up")]
    public void Build_Throws_WhenBothInvalidatesAndInvalidatedByDeclared()
    {
        var act = () => CompiledMessagingRegistry.Build(baseline: [new ConflictingInvalidationConfig()]);

        act.Should().Throw<InvalidOperationException>(
            "YFINV001: same (command, query) pair declared in both directions must error at build time");
    }

    // ── Test #39: Three-tier configuration merge ──────────────────────────────

    [Fact]
    public void Build_ClientOverride_WinsOverBaseline()
    {
        var registry = CompiledMessagingRegistry.Build(
            baseline: [new BaselineConfig()],
            clientOverrides: [new ClientOverrideConfig()]);

        var policy = registry.Queries[typeof(TestAggregate.Queries.GetByIdQuery)];
        policy.Cache.Should().NotBeNull("client override adds cache policy the baseline omitted");
    }

    // ── Configurations (private — skipped by Pipeline B generator) ───────────

    private sealed class GroupExpansionConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.CreateCommand, TestItem>()
                .InvalidatesGroup<ITestItemViews>();
    }

    private sealed class MatchPredicateConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.RenameCommand, TestItem>()
                .Invalidates<TestAggregate.Queries.GetByIdQuery, TestItem>(
                    (q, cmd) => q.Id == cmd.Id);
    }

    private sealed class ConflictingInvalidationConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
        {
            b.Command<TestAggregate.Commands.CreateCommand, TestItem>()
                .Invalidates<TestAggregate.Queries.GetByIdQuery, TestItem>();
            b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>()
                .InvalidatedBy<TestAggregate.Commands.CreateCommand>();
        }
    }

    private sealed class BaselineConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>();
    }

    private sealed class ClientOverrideConfig : IClientAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>()
                .Cacheable(c => c.SlidingExpiration(TimeSpan.FromMinutes(5)));
    }
}
