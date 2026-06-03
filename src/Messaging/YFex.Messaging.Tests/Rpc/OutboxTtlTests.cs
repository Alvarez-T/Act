using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YFex.Cqrs;
using YFex.Messaging.Rpc;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Rpc;

/// <summary>Tests #27–28: Outbox TTL expiry and overflow handling.</summary>
[Trait("Category", "Rpc")]
public sealed class OutboxTtlTests
{
    public OutboxTtlTests() => TestDispatcherFixture.ClearStore();

    // ── Test #27: Outbox TTL expiry (via OutboxReplayer) ─────────────────────

    [Fact]
    public async Task OutboxReplayer_MovesToFailureLog_WhenEntryExceedsTtl()
    {
        var network = new ManualNetworkStatus(connected: false);
        var outbox = new InMemoryOutbox(new OutboxOptions());
        var failureLog = new InMemorySyncFailureLog();
        outbox.SetFailureLog(failureLog);
        var cache = new InMemoryClientCache();
        var bus = new DefaultEventBus();
        var registry = CompiledMessagingRegistry.Build(baseline: []);
        var syncStatus = new SyncStatus();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<ICommandHandler<TestAggregate.Commands.CreateCommand, TestItem>,
            TestDispatcherFixture.CreateCommandHandler>();
        var sp = services.BuildServiceProvider();
        var dispatcher = new LocalDispatcher(
            new LocalHandlerInvoker(sp), registry, network, cache, outbox, bus, sp);

        // TTL near-zero: entries expire almost immediately
        var options = new YFexMessagingRpcClientOptions { OutboxEntryTtl = TimeSpan.FromMilliseconds(1) };
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<OutboxReplayer>();
        var replayer = new OutboxReplayer(
            outbox, failureLog, dispatcher, network, syncStatus, bus, options, logger);

        network.GoOffline();
        await outbox.EnqueueAsync(new TestAggregate.Commands.CreateCommand(1, "Test"));

        // Wait briefly so the entry crosses the 1 ms TTL
        await Task.Delay(20);

        // Start the background service and trigger a drain by going online
        using var serviceCts = new CancellationTokenSource();
        await replayer.StartAsync(serviceCts.Token);
        network.GoOnline();
        await Task.Delay(200); // give the background service time to drain

        await serviceCts.CancelAsync();
        replayer.Dispose();

        failureLog.Failures.Should().ContainSingle(f => f.Reason == "expired",
            "outbox entries older than OutboxEntryTtl must move to the failure log with reason 'expired'");
        outbox.PendingCount.Should().Be(0, "expired entry must be removed from outbox");
    }

    // ── Test #28: Outbox overflow ──────────────────────────────────────────────

    [Fact]
    public async Task Outbox_ExceedingMaxEntries_MovesOldestToFailureLog()
    {
        var outbox = new InMemoryOutbox(new OutboxOptions { MaxEntries = 2 });
        var failureLog = new InMemorySyncFailureLog();
        outbox.SetFailureLog(failureLog);

        await outbox.EnqueueAsync(new TestAggregate.Commands.CreateCommand(1, "First"));
        await outbox.EnqueueAsync(new TestAggregate.Commands.CreateCommand(2, "Second"));
        // Third enqueue exceeds MaxEntries=2; oldest entry must overflow to failure log
        await outbox.EnqueueAsync(new TestAggregate.Commands.CreateCommand(3, "Third"));

        outbox.PendingCount.Should().Be(2, "outbox stays at MaxEntries after overflow");
        failureLog.Failures.Should().ContainSingle(f => f.Reason == "outbox-overflow",
            "oldest entry must be moved to failure log with reason 'outbox-overflow'");
    }

    // ── Tests #25–26: ConflictPolicy (infrastructure ready; runtime wiring deferred) ─

    [Fact(Skip = "ConflictPolicy enforcement in OutboxReplayer is planned; infrastructure types (EscalateConflictResolver etc.) exist")]
    public Task ConflictPolicy_Escalate_MovesToSyncFailureLog() => Task.CompletedTask;

    [Fact(Skip = "ConflictPolicy enforcement in OutboxReplayer is planned; infrastructure types exist")]
    public Task ConflictPolicy_RetryLater_RetriesWithBackoff() => Task.CompletedTask;
}
