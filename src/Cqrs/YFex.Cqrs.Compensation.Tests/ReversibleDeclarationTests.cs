using YFex.Cqrs;

namespace YFex.Cqrs.Compensation.Tests;

/// <summary>Task 1: reversibility DECLARATION in YFex.Cqrs (mirrors the Optimistic descriptor→policy path).</summary>
[Trait("Category", "Cqrs")]
public sealed class ReversibleDeclarationTests
{
    [Fact]
    public void Reversible_CompiledPolicy_ExposesReverse()
    {
        var registry = CompiledMessagingRegistry.Build(baseline: [new ReversibleConfig()]);

        var policy = registry.Commands[typeof(ApproveInvoiceCommand)];
        policy.Reverse.Should().NotBeNull("declaring .Reversible must compile a ReversePolicy");
        policy.Reverse!.ReverseCommandType.Should().Be(typeof(UnapproveInvoiceCommand));
        policy.Reverse.Map.Should().NotBeNull("the map expression must be compiled to a delegate");
    }

    [Fact]
    public void Reversible_Map_MaterializesReverseCommand_WithoutReflection()
    {
        var registry = CompiledMessagingRegistry.Build(baseline: [new ReversibleConfig()]);
        var reverse = registry.Commands[typeof(ApproveInvoiceCommand)].Reverse!;

        var materialized = reverse.Map!(new ApproveInvoiceCommand(42), null);

        materialized.Should().BeOfType<UnapproveInvoiceCommand>()
            .Which.InvoiceId.Should().Be(42, "the compiled map must project the original command's field");
    }

    [Fact]
    public void Reversible_ResultBearingMap_ReceivesResult()
    {
        var registry = CompiledMessagingRegistry.Build(baseline: [new ResultReversibleConfig()]);
        var reverse = registry.Commands[typeof(CreateInvoiceCommand)].Reverse!;

        // Create returned id 99 → reverse is Delete(99).
        var materialized = reverse.Map!(new CreateInvoiceCommand("Acme"), 99);

        materialized.Should().BeOfType<DeleteInvoiceCommand>().Which.Id.Should().Be(99);
    }

    [Fact]
    public void Reversible_DeclaredTwice_LastWins()
    {
        var registry = CompiledMessagingRegistry.Build(baseline: [new OverrideReverseConfig()]);

        var reverse = registry.Commands[typeof(ApproveInvoiceCommand)].Reverse!;
        reverse.ReverseCommandType.Should().Be(typeof(CancelInvoiceCommand),
            "single-value field: the last .Reversible call must overwrite the previous one");
        reverse.Map!(new ApproveInvoiceCommand(5), null).Should().BeOfType<CancelInvoiceCommand>();
    }

    [Fact]
    public void Build_Throws_WhenReverseCommandNotRegistered()
    {
        var act = () => CompiledMessagingRegistry.Build(baseline: [new UnregisteredReverseConfig()]);

        act.Should().Throw<InvalidOperationException>(
            "the reverse command type must itself be a registered command (fail-fast at Build)");
    }

    // ── Reflection-free wiring: the map/handler forms compile typed delegates ─────

    [Fact]
    public void Reversible_MapForm_CompilesTypedDispatchDelegate()
    {
        var registry = CompiledMessagingRegistry.Build(baseline: [new ReversibleConfig()]);
        var reverse = registry.Commands[typeof(ApproveInvoiceCommand)].Reverse!;

        reverse.DispatchReverse.Should().NotBeNull(
            "the map form must dispatch via a delegate captured where TReverse is a generic parameter — no MakeGenericMethod");
        reverse.InvokeHandler.Should().BeNull("the map form has no custom handler");
    }

    [Fact]
    public void ReversibleWith_HandlerForm_CompilesTypedInvokeDelegate()
    {
        var registry = CompiledMessagingRegistry.Build(baseline: [new HandlerReversibleConfig()]);
        var reverse = registry.Commands[typeof(ArchiveInvoiceCommand)].Reverse!;

        reverse.InvokeHandler.Should().NotBeNull(
            "the handler form must invoke via a delegate captured where THandler/TCommand are generic parameters — no reflection");
        reverse.DispatchReverse.Should().BeNull("the handler form does not dispatch a reverse command");
        reverse.Map.Should().BeNull("the handler form has no map");
        reverse.HandlerType.Should().Be(typeof(RecordingReverseHandler));
    }

    [Fact]
    public void ReversibleWith_ResultBearingHandler_CapturesResultType()
    {
        var registry = CompiledMessagingRegistry.Build(baseline: [new HandlerResultReversibleConfig()]);
        var reverse = registry.Commands[typeof(RegisterInvoiceCommand)].Reverse!;

        reverse.InvokeHandler.Should().NotBeNull();
        reverse.ResultType.Should().Be(typeof(int),
            "the declared result type is captured so the journal deserializes the result without Type.GetType");
    }
}
