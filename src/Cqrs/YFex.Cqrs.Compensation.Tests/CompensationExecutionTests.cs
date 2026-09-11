using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;

namespace YFex.Cqrs.Compensation.Tests;

/// <summary>Task 2: journal + reverse dispatch execution primitive (YFex.Cqrs.Compensation).</summary>
[Trait("Category", "Compensation")]
public sealed class CompensationExecutionTests
{
    private static CompiledMessagingRegistry Registry() =>
        CompiledMessagingRegistry.Build(baseline: [new ReversibleConfig()]);

    [Fact]
    public async Task Observer_Appends_OnlyReversibleCommands()
    {
        var store = new MemoryKeyValueStore();
        var journal = new StorageBackedCommandJournal(store);
        var observer = new CommandExecutedObserver(journal, Registry());

        // Reversible → appended.
        await observer.OnExecutedAsync(new ApproveInvoiceCommand(1), null, CancellationToken.None);
        // Registered but NOT reversible → skipped.
        await observer.OnExecutedAsync(new UnapproveInvoiceCommand(1), null, CancellationToken.None);
        // Unknown to the registry → skipped.
        await observer.OnExecutedAsync(new CancelInvoiceCommand(1), null, CancellationToken.None);

        var keys = await store.GetKeysWithPrefixAsync("journal:");
        keys.Should().HaveCount(1, "only the reversible command should be journaled");

        var newest = await journal.TryPopNewestUnreversedAsync();
        newest!.CommandTypeName.Should().Contain(nameof(ApproveInvoiceCommand));
    }

    [Fact]
    public async Task UndoLast_Materializes_Dispatches_AndMarksReversed()
    {
        var store = new MemoryKeyValueStore();
        var journal = new StorageBackedCommandJournal(store);
        var registry = Registry();
        var observer = new CommandExecutedObserver(journal, registry);
        var reverser = new ReverseExecutor(journal, registry);
        var dispatcher = new RecordingDispatcher();

        await observer.OnExecutedAsync(new ApproveInvoiceCommand(7), null, CancellationToken.None);

        var undone = await reverser.UndoLastAsync(dispatcher);

        undone.Should().BeTrue();
        dispatcher.Dispatched.Should().ContainSingle()
            .Which.Should().BeOfType<UnapproveInvoiceCommand>()
            .Which.InvoiceId.Should().Be(7, "the reverse command must be materialized from the original");

        (await journal.TryPopNewestUnreversedAsync()).Should().BeNull("the entry must be marked reversed");
    }

    [Fact]
    public async Task DoubleUndo_IsNoOp()
    {
        var store = new MemoryKeyValueStore();
        var journal = new StorageBackedCommandJournal(store);
        var registry = Registry();
        var observer = new CommandExecutedObserver(journal, registry);
        var reverser = new ReverseExecutor(journal, registry);
        var dispatcher = new RecordingDispatcher();

        await observer.OnExecutedAsync(new ApproveInvoiceCommand(3), null, CancellationToken.None);

        (await reverser.UndoLastAsync(dispatcher)).Should().BeTrue();
        (await reverser.UndoLastAsync(dispatcher)).Should().BeFalse("nothing left to reverse");

        dispatcher.Dispatched.Should().ContainSingle("the second undo must not dispatch anything");
    }

    [Fact]
    public async Task MarkReversed_IsIdempotent()
    {
        var store = new MemoryKeyValueStore();
        var journal = new StorageBackedCommandJournal(store);

        var seq = await journal.AppendAsync(new ApproveInvoiceCommand(11), null,
            typeof(UnapproveInvoiceCommand), principal: null);

        await journal.MarkReversedAsync(seq);
        await journal.MarkReversedAsync(seq); // second call must be a safe no-op
        await journal.MarkReversedAsync(9999); // unknown seq must be a safe no-op

        (await journal.TryPopNewestUnreversedAsync()).Should().BeNull();
    }

    [Fact]
    public async Task DurableRoundTrip_ThroughFreshJournalInstance()
    {
        var store = new MemoryKeyValueStore();

        // Write with one instance...
        var writer = new StorageBackedCommandJournal(store);
        await writer.AppendAsync(new ApproveInvoiceCommand(9), null,
            typeof(UnapproveInvoiceCommand), principal: "alice");

        // ...read back through a completely separate instance over the same store.
        var reader = new StorageBackedCommandJournal(store);
        var entry = await reader.TryPopNewestUnreversedAsync();

        entry.Should().NotBeNull();
        entry!.Principal.Should().Be("alice");
        entry.ReverseCommandTypeName.Should().Contain(nameof(UnapproveInvoiceCommand));
        entry.Reversed.Should().BeFalse();

        // The stored command payload round-trips back to the original type + value.
        var originalType = Type.GetType(entry.CommandTypeName)!;
        var original = (ApproveInvoiceCommand)System.Text.Json.JsonSerializer.Deserialize(entry.Command, originalType)!;
        original.InvoiceId.Should().Be(9);
    }

    [Fact]
    public async Task UndoLast_ResultBearingCommand_UsesResultInReverse()
    {
        var store = new MemoryKeyValueStore();
        var journal = new StorageBackedCommandJournal(store);
        var registry = CompiledMessagingRegistry.Build(baseline: [new ResultReversibleConfig()]);
        var observer = new CommandExecutedObserver(journal, registry);
        var reverser = new ReverseExecutor(journal, registry);
        var dispatcher = new RecordingDispatcher();

        // Create returned id 55 → journal captures the result → reverse must be Delete(55).
        await observer.OnExecutedAsync(new CreateInvoiceCommand("Acme"), 55, CancellationToken.None);

        (await reverser.UndoLastAsync(dispatcher)).Should().BeTrue();
        dispatcher.Dispatched.Should().ContainSingle()
            .Which.Should().BeOfType<DeleteInvoiceCommand>()
            .Which.Id.Should().Be(55);
    }

    // ── Custom-handler reversal (ReversibleWith<THandler>) — end-to-end via DI ────

    [Fact]
    public async Task UndoLast_CustomHandler_InvokesCompensate_WithoutDispatching()
    {
        var store = new MemoryKeyValueStore();
        var journal = new StorageBackedCommandJournal(store);
        var registry = CompiledMessagingRegistry.Build(baseline: [new HandlerReversibleConfig()]);
        var handler = new RecordingReverseHandler();
        using var services = new ServiceCollection().AddSingleton(handler).BuildServiceProvider();
        var observer = new CommandExecutedObserver(journal, registry);
        var reverser = new ReverseExecutor(journal, registry, services);
        var dispatcher = new RecordingDispatcher();

        await observer.OnExecutedAsync(new ArchiveInvoiceCommand(21), null, CancellationToken.None);

        (await reverser.UndoLastAsync(dispatcher)).Should().BeTrue();

        handler.Compensated.Should().ContainSingle().Which.Should().Be(21,
            "the custom handler must receive the deserialized original command");
        dispatcher.Dispatched.Should().BeEmpty("handler reversal must not dispatch a reverse command");
        (await journal.TryPopNewestUnreversedAsync()).Should().BeNull("the entry must be marked reversed");
    }

    [Fact]
    public async Task UndoLast_CustomHandler_ResultBearing_ReceivesJournaledResult()
    {
        var store = new MemoryKeyValueStore();
        var journal = new StorageBackedCommandJournal(store);
        var registry = CompiledMessagingRegistry.Build(baseline: [new HandlerResultReversibleConfig()]);
        var handler = new RecordingResultReverseHandler();
        using var services = new ServiceCollection().AddSingleton(handler).BuildServiceProvider();
        var observer = new CommandExecutedObserver(journal, registry);
        var reverser = new ReverseExecutor(journal, registry, services);
        var dispatcher = new RecordingDispatcher();

        await observer.OnExecutedAsync(new RegisterInvoiceCommand("Acme"), 77, CancellationToken.None);

        (await reverser.UndoLastAsync(dispatcher)).Should().BeTrue();

        handler.Compensated.Should().ContainSingle();
        handler.Compensated[0].Name.Should().Be("Acme");
        handler.Compensated[0].Result.Should().Be(77, "the result-bearing handler must receive the journaled result");
    }

    [Fact]
    public async Task UndoLast_CustomHandler_ThrowsWhenHandlerNotRegistered()
    {
        var store = new MemoryKeyValueStore();
        var journal = new StorageBackedCommandJournal(store);
        var registry = CompiledMessagingRegistry.Build(baseline: [new HandlerReversibleConfig()]);
        using var services = new ServiceCollection().BuildServiceProvider(); // handler NOT registered
        var observer = new CommandExecutedObserver(journal, registry);
        var reverser = new ReverseExecutor(journal, registry, services);
        var dispatcher = new RecordingDispatcher();

        await observer.OnExecutedAsync(new ArchiveInvoiceCommand(5), null, CancellationToken.None);

        var act = async () => await reverser.UndoLastAsync(dispatcher);
        await act.Should().ThrowAsync<InvalidOperationException>(
            "a handler-form reversal fails loudly (no Activator fallback) when the handler is absent");
    }
}
