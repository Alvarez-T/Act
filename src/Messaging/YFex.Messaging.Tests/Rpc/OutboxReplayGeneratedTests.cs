using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YFex.Cqrs;
using YFex.Messaging.Rpc;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Rpc;

/// <summary>
/// Verifies the source-generated, reflection-free outbox replay path (OutboxCommandRegistry)
/// that replaced the old Type.GetType + MakeGenericMethod dispatch in OutboxReplayer.
/// </summary>
[Trait("Category", "Rpc")]
[Collection("DispatcherTests")]
public sealed class OutboxReplayGeneratedTests
{
    public OutboxReplayGeneratedTests() => TestDispatcherFixture.ClearStore();

    [Fact]
    public void GeneratedRegistry_Includes_DiscoveredCommands()
    {
        var registry = YFexOutboxCommandRegistrations.BuildOutboxCommandRegistry();

        registry.Count.Should().BeGreaterThan(0, "the generator must discover TestAggregate commands");
        registry.TryGetHandler(
                typeof(TestAggregate.Commands.CreateCommand).AssemblyQualifiedName!, out var byAqn)
            .Should().BeTrue("commands must be keyed by AssemblyQualifiedName (what the outbox stores)");
        byAqn.Should().NotBeNull();
        registry.TryGetHandler(
                typeof(TestAggregate.Commands.CreateCommand).FullName!, out _)
            .Should().BeTrue("commands must also be keyed by FullName as a fallback");
    }

    [Fact]
    public async Task GeneratedHandler_MaterializesPayload_AndDispatchesTyped()
    {
        var fx = new TestDispatcherFixture(startConnected: false);

        // Offline enqueue produces a real OutboxEntry (CommandTypeName + JSON payload).
        _ = await fx.Dispatcher.CommandAsync<TestAggregate.Commands.CreateCommand, TestItem>(
            new TestAggregate.Commands.CreateCommand(42, "Replayed"));
        var entry = (await fx.Outbox.ListPendingAsync()).Single();

        var registry = YFexOutboxCommandRegistrations.BuildOutboxCommandRegistry();
        registry.TryGetHandler(entry.CommandTypeName, out var handler).Should().BeTrue();

        // Reconnect so the dispatcher runs the handler instead of re-enqueuing.
        fx.Network.GoOnline();
        var replayServices = new ServiceCollection().BuildServiceProvider();
        var outcome = await handler!(entry.Payload, fx.Dispatcher, replayServices, CancellationToken.None);
        outcome.IsRejected.Should().BeFalse("the handler accepted the command");

        TestDispatcherFixture.Store.Should().ContainKey(42, "the replayed command must reach its handler");
        TestDispatcherFixture.Store[42].Name.Should().Be("Replayed",
            "payload must round-trip through JSON to the concrete command type");
    }

    [Fact]
    public async Task OutboxReplayer_DrainsQueuedCommand_ViaGeneratedRegistry()
    {
        var network = new ManualNetworkStatus(connected: false);
        var outbox = new InMemoryOutbox(new OutboxOptions());
        var failureLog = new InMemorySyncFailureLog();
        outbox.SetFailureLog(failureLog);
        var cache = new InMemoryCache();
        var bus = new DefaultEventBus();
        var registry = CompiledMessagingRegistry.Build(baseline: []);
        var syncStatus = new SyncStatus();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<ICommandHandler<TestAggregate.Commands.CreateCommand, TestItem>,
            TestDispatcherFixture.CreateCommandHandler>();
        var sp = services.BuildServiceProvider();
        var dispatcher = new LocalDispatcher(
            new LocalHandlerInvoker(sp, YFexLocalHandlerRegistrations.BuildLocalHandlerInvokerRegistry()),
            registry, network, cache, outbox, bus, sp);

        var commandRegistry = YFexOutboxCommandRegistrations.BuildOutboxCommandRegistry();
        var options = new YFexMessagingRpcClientOptions { OutboxEntryTtl = TimeSpan.FromHours(1) };
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<OutboxReplayer>();
        var replayer = new OutboxReplayer(
            outbox, failureLog, dispatcher, network, syncStatus, bus, commandRegistry,
            cache, registry, sp, options, logger);

        network.GoOffline();
        await outbox.EnqueueAsync(new TestAggregate.Commands.CreateCommand(7, "Drained"));
        outbox.PendingCount.Should().Be(1);

        using var cts = new CancellationTokenSource();
        await replayer.StartAsync(cts.Token);
        network.GoOnline();

        // Poll for the background drain to finish (deterministic bound, no fixed sleep dependency).
        for (int i = 0; i < 100 && outbox.PendingCount > 0; i++)
            await Task.Delay(20);

        await cts.CancelAsync();
        replayer.Dispose();

        outbox.PendingCount.Should().Be(0, "the queued command must be replayed and removed");
        TestDispatcherFixture.Store.Should().ContainKey(7);
        TestDispatcherFixture.Store[7].Name.Should().Be("Drained");
    }
}
