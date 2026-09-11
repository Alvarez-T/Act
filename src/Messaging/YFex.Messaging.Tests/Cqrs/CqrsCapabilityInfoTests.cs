using YFex.Authorization.Public;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Cqrs;

/// <summary>
/// Verifies the compiled <see cref="CompiledMessagingRegistry.Capabilities"/> table: builder-authored
/// enrichment (Describe / Keywords / Irreversible), the builder-declared required permission (otherwise
/// invisible behind the opaque authorizer delegate), reversibility, and the IQueueable/ICacheable markers.
/// </summary>
[Trait("Category", "Cqrs")]
public sealed class CqrsCapabilityInfoTests
{
    private static readonly Permission OrdersDelete = Permission.Parse("orders.delete");

    private sealed class CapabilityConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
        {
            // Reverse target must itself be registered (Build fail-fast). CreateCommand is IQueueable.
            b.Command<TestAggregate.Commands.CreateCommand, TestItem>();

            b.Command<TestAggregate.Commands.DeleteCommand>()
                .RequirePermission(OrdersDelete)
                .Describe("Deletes an item permanently")
                .Keywords("delete", "remove")
                .Irreversible()
                .Reversible<TestAggregate.Commands.CreateCommand>(
                    cmd => new TestAggregate.Commands.CreateCommand(cmd.Id, "restored"));

            b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>()
                .Describe("Gets an item by id")
                .Keywords("get", "fetch");
        }
    }

    private static CompiledMessagingRegistry Build() =>
        CompiledMessagingRegistry.Build(baseline: [new CapabilityConfig()]);

    private static readonly Type DeleteType = typeof(TestAggregate.Commands.DeleteCommand);
    private static readonly Type CreateType = typeof(TestAggregate.Commands.CreateCommand);
    private static readonly Type QueryType = typeof(TestAggregate.Queries.GetByIdQuery);

    [Fact]
    public void Describe_and_Keywords_land_in_capability_info()
    {
        var info = Build().Capabilities[DeleteType];
        info.Description.Should().Be("Deletes an item permanently");
        info.Keywords.Should().Contain(new[] { "delete", "remove" });
    }

    [Fact]
    public void RequirePermission_is_read_from_the_builder_declared_permission()
    {
        var info = Build().Capabilities[DeleteType];
        info.RequiredPermission.Should().Be(OrdersDelete);
    }

    [Fact]
    public void Irreversible_flag_is_captured()
    {
        Build().Capabilities[DeleteType].Irreversible.Should().BeTrue();
    }

    [Fact]
    public void Reversible_and_ReverseCommandType_are_populated_from_the_reverse_rule()
    {
        var info = Build().Capabilities[DeleteType];
        info.Reversible.Should().BeTrue();
        info.ReverseCommandType.Should().Be(CreateType);
    }

    [Fact]
    public void Queueable_and_Cacheable_markers_are_surfaced()
    {
        var reg = Build();
        reg.Capabilities[CreateType].IsQueueable.Should().BeTrue();   // CreateCommand : IQueueable
        reg.Capabilities[DeleteType].IsQueueable.Should().BeFalse();  // DeleteCommand not queueable
        reg.Capabilities[QueryType].IsCacheable.Should().BeTrue();    // GetByIdQuery : ICacheable
    }

    [Fact]
    public void Query_capability_carries_description_keywords_and_no_permission()
    {
        var info = Build().Capabilities[QueryType];
        info.Description.Should().Be("Gets an item by id");
        info.Keywords.Should().Contain(new[] { "get", "fetch" });
        info.RequiredPermission.Should().BeNull();
        info.Irreversible.Should().BeFalse();
        info.Reversible.Should().BeFalse();
    }

    [Fact]
    public void Unenriched_command_has_null_metadata_but_correct_markers()
    {
        // CreateCommand is registered without Describe/Keywords/permission/reverse.
        var info = Build().Capabilities[CreateType];
        info.Description.Should().BeNull();
        info.Keywords.Should().BeNull();
        info.RequiredPermission.Should().BeNull();
        info.Reversible.Should().BeFalse();
        info.Irreversible.Should().BeFalse();
    }
}
