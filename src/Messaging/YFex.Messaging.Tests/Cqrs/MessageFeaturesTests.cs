using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Cqrs;

/// <summary>
/// The typed feature slot other libraries attach to a command (offline ⚑O13): one value per feature type, the last configuration wins,
/// absent features cost nothing.
/// </summary>
[Trait("Category", "Cqrs")]
public sealed class MessageFeaturesTests
{
    private sealed record AuditFeature(string Category);
    private sealed record OtherFeature(int Value);

    private sealed class Baseline : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
        {
            var create = b.Command<TestAggregate.Commands.CreateCommand, TestItem>().Feature(new AuditFeature("first"));
            create.GetFeature<AuditFeature>().Should().Be(new AuditFeature("first"));
            create.Feature(new AuditFeature("second")).Feature(new OtherFeature(7));
            b.Command<TestAggregate.Commands.DeleteCommand>();
        }
    }

    private sealed class ServerOverride : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.CreateCommand, TestItem>().Feature(new AuditFeature("server"));
    }

    [Fact]
    public void Features_reach_the_compiled_policy_one_value_per_type()
    {
        var policy = CompiledMessagingRegistry.Build(baseline: [new Baseline()]).Commands[typeof(TestAggregate.Commands.CreateCommand)];

        policy.Features.Should().NotBeNull();
        policy.Features!.Count.Should().Be(2);
        policy.Features.Get<AuditFeature>().Should().Be(new AuditFeature("second"));
        policy.Features.TryGet(out OtherFeature? other).Should().BeTrue();
        other!.Value.Should().Be(7);
    }

    [Fact]
    public void A_later_configuration_replaces_a_feature_and_keeps_the_others()
    {
        var policy = CompiledMessagingRegistry.Build(baseline: [new Baseline()], serverOverrides: [new ServerOverride()])
            .Commands[typeof(TestAggregate.Commands.CreateCommand)];

        policy.Features!.Get<AuditFeature>().Should().Be(new AuditFeature("server"));
        policy.Features.Get<OtherFeature>().Should().Be(new OtherFeature(7));
    }

    [Fact]
    public void A_command_without_features_has_none()
        => CompiledMessagingRegistry.Build(baseline: [new Baseline()]).Commands[typeof(TestAggregate.Commands.DeleteCommand)]
            .Features.Should().BeNull();
}
