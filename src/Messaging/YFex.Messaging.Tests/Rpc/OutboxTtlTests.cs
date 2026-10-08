using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Validation;
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
        var network = new ManualServerConnection(connected: false);
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

        // TTL near-zero: entries expire almost immediately. Since offline Phase 0 expired entries are KEPT by default
        // (a queued sale must not silently become a dead letter); this test opts into the dead-letter behaviour.
        var options = new YFexMessagingRpcClientOptions
        {
            OutboxEntryTtl = TimeSpan.FromMilliseconds(1),
            OutboxOptions = new OutboxOptions { KeepExpiredEntries = false },
        };
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<OutboxReplayer>();
        var replayer = new OutboxReplayer(
            outbox, failureLog, dispatcher, network, syncStatus, bus,
            OutboxCommandRegistry.Empty, cache, registry, sp, options, logger);

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

    // ── Offline Phase 0 (F5): expired entries are kept by default ───────────────

    [Fact]
    public async Task OutboxReplayer_KeepsExpiredEntry_ByDefault()
    {
        var network = new ManualServerConnection(connected: false);
        var outbox = new InMemoryOutbox(new OutboxOptions());
        var failureLog = new InMemorySyncFailureLog();
        outbox.SetFailureLog(failureLog);
        var cache = new InMemoryCache();
        var bus = new DefaultEventBus();
        var registry = CompiledMessagingRegistry.Build(baseline: []);

        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var dispatcher = new LocalDispatcher(
            new LocalHandlerInvoker(sp, YFexLocalHandlerRegistrations.BuildLocalHandlerInvokerRegistry()),
            registry, network, cache, outbox, bus, sp);
        var options = new YFexMessagingRpcClientOptions { OutboxEntryTtl = TimeSpan.FromMilliseconds(1) };
        var replayer = new OutboxReplayer(
            outbox, failureLog, dispatcher, network, new SyncStatus(), bus,
            OutboxCommandRegistry.Empty, cache, registry, sp, options,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<OutboxReplayer>());

        await outbox.EnqueueAsync(new TestAggregate.Commands.CreateCommand(1, "Venda offline"));
        await Task.Delay(20);

        using var serviceCts = new CancellationTokenSource();
        await replayer.StartAsync(serviceCts.Token);
        network.GoOnline();
        await Task.Delay(200);
        await serviceCts.CancelAsync();
        replayer.Dispose();

        failureLog.Failures.Should().BeEmpty("an expired entry is kept and retried, not dead-lettered");
        outbox.PendingCount.Should().Be(1);
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

    // ── Tests #25–26: ConflictPolicy enforcement in OutboxReplayer ───────────────
    //
    // A replayed command that the server REJECTS (a failed QueueableResult carrying an Error —
    // here forced via a failing validator on the online replay path) is routed through the
    // command's conflict policy. A rejection is a permanent, business-level outcome; it is
    // distinct from a transient failure (a thrown exception), which stays on the retry/backoff path.
    // Discard/Escalate additionally UNDO the local optimistic effect by invalidating (→ refetch) the
    // caches the command declared as invalidation targets — never by dispatching a reverse command.

    [Fact]
    public async Task ConflictPolicy_Escalate_MovesToSyncFailureLog_AndInvalidatesOptimisticCache()
    {
        var (outbox, failureLog, cache, _) = await RunRejectedReplayAsync(
            new RejectingCommandConfig(ConflictPolicy.Escalate));

        outbox.PendingCount.Should().Be(0, "an escalated conflict removes the entry from the outbox");
        failureLog.Failures.Should().ContainSingle(f => f.Reason == "conflict",
            "an escalated server rejection must be dead-lettered to the sync failure log");
        (await GetByIdCacheKeysAsync(cache)).Should().BeEmpty(
            "escalating a rejection must undo the local optimistic effect by invalidating the command's caches");
    }

    [Fact]
    public async Task Escalated_rejection_keeps_structured_violations_in_the_failure_log()
    {
        // Validation Phase 3, G7: the "sync issues" screen needs code + path, not a flat reason string.
        var (_, failureLog, _, _) = await RunRejectedReplayAsync(new RejectingCommandConfig(ConflictPolicy.Escalate));

        var failure = failureLog.Failures.Should().ContainSingle().Subject;
        failure.Violations.Count.Should().Be(1);
        failure.Violations[0].Path.ToString().Should().Be("Id");
        failure.Violations[0].Code.Value.Should().Be("rejected");
        failure.Violations[0].Detail.Should().Be("rejected by server");

        // Persisted as JSON (StorageBackedSyncFailureLog): the converter is lossless.
        var json = System.Text.Json.JsonSerializer.Serialize(failure);
        var back = System.Text.Json.JsonSerializer.Deserialize<SyncFailure>(json)!;
        back.Violations[0].Path.Should().Be(failure.Violations[0].Path);
        back.Violations[0].Code.Should().Be(failure.Violations[0].Code);
        back.Violations[0].Detail.Should().Be("rejected by server");
    }

    [Fact]
    public async Task ConflictPolicy_Discard_RemovesEntry_AndInvalidatesOptimisticCache()
    {
        var (outbox, failureLog, cache, _) = await RunRejectedReplayAsync(
            new RejectingCommandConfig(ConflictPolicy.Discard));

        outbox.PendingCount.Should().Be(0, "a discarded conflict removes the entry from the outbox");
        failureLog.Failures.Should().BeEmpty("a discarded conflict is a no-op, not a sync failure");
        (await GetByIdCacheKeysAsync(cache)).Should().BeEmpty(
            "discarding a rejection must undo the local optimistic effect by invalidating the command's caches");
    }

    [Fact]
    public async Task ConflictPolicy_RetryLater_RetriesWithBackoff_AndKeepsOptimisticCache()
    {
        var (outbox, failureLog, cache, _) = await RunRejectedReplayAsync(
            new RejectingCommandConfig(ConflictPolicy.RetryLater), waitMs: 250);

        var pending = await outbox.ListPendingAsync();
        pending.Should().ContainSingle("RetryLater keeps the entry queued for a later drain");
        pending[0].AttemptCount.Should().Be(1, "the retry must record an attempt so the next backoff grows");
        failureLog.Failures.Should().BeEmpty("RetryLater must not dead-letter the command");
        (await GetByIdCacheKeysAsync(cache)).Should().NotBeEmpty(
            "RetryLater leaves the optimistic effect in place — the command may still succeed later");
    }

    [Fact]
    public async Task ConflictResolver_FromDI_OverridesStaticPolicy()
    {
        // Static policy says Escalate; the DI-registered IConflictResolver<CreateCommand> says Discard.
        // The typed resolver must win — proving reflection-free resolver resolution is wired in.
        var (outbox, failureLog, _, _) = await RunRejectedReplayAsync(
            new RejectingCommandConfig(ConflictPolicy.Escalate),
            registerResolver: true);

        outbox.PendingCount.Should().Be(0, "the DI resolver's Discard policy removes the entry");
        failureLog.Failures.Should().BeEmpty(
            "the DI IConflictResolver<CreateCommand> (Discard) must override the static Escalate policy");
    }

    [Fact]
    public async Task TransientFailure_Retries_NotTreatedAsConflict()
    {
        // The validator passes but the handler throws — a transient failure, NOT a business rejection.
        // Even though the conflict policy is Discard, a transient failure must stay on the retry path.
        var (outbox, failureLog, cache, _) = await RunReplayAsync(
            new TransientCommandConfig(), throwingHandler: true, waitMs: 250);

        var pending = await outbox.ListPendingAsync();
        pending.Should().ContainSingle("a transient failure keeps the entry for retry");
        pending[0].AttemptCount.Should().Be(1, "a transient failure records a retry attempt");
        failureLog.Failures.Should().BeEmpty("a transient failure must not be dead-lettered as a conflict");
        (await GetByIdCacheKeysAsync(cache)).Should().NotBeEmpty(
            "a transient failure must not undo the optimistic effect (the command has not been rejected)");
    }

    // ── Conflict-test harness ────────────────────────────────────────────────────

    private static ValueTask<IReadOnlyList<string>> GetByIdCacheKeysAsync(InMemoryCache cache)
        => cache.GetKeysWithPrefixAsync($"query:{typeof(TestAggregate.Queries.GetByIdQuery).FullName}");

    private static Task<(InMemoryOutbox outbox, InMemorySyncFailureLog failureLog, InMemoryCache cache, DefaultEventBus bus)>
        RunRejectedReplayAsync(IAggregateConfiguration config, bool registerResolver = false, int waitMs = 400)
        => RunReplayAsync(config, throwingHandler: false, registerResolver: registerResolver, waitMs: waitMs);

    // Seeds a cached GetByIdQuery(1), enqueues a CreateCommand(1) while offline (which marks that
    // cache entry stale — the local optimistic effect), then drains the outbox online through a real
    // OutboxReplayer and returns the resulting state for assertions.
    private static async Task<(InMemoryOutbox outbox, InMemorySyncFailureLog failureLog, InMemoryCache cache, DefaultEventBus bus)>
        RunReplayAsync(IAggregateConfiguration config, bool throwingHandler, bool registerResolver = false, int waitMs = 400)
    {
        TestDispatcherFixture.ClearStore();
        TestDispatcherFixture.Store[1] = new TestItem(1, "Seed");

        var network = new ManualServerConnection(connected: true);
        var cache = new InMemoryCache();
        var outbox = new InMemoryOutbox(new OutboxOptions());
        var failureLog = new InMemorySyncFailureLog();
        outbox.SetFailureLog(failureLog);
        var bus = new DefaultEventBus();
        var syncStatus = new SyncStatus();

        var registry = CompiledMessagingRegistry.Build(
            baseline: [config],
            scanForImplementers: [typeof(OutboxTtlTests).Assembly]);

        var services = new ServiceCollection();
        services.AddLogging();
        // The static conflict-policy fallback resolves CommandPolicy.Conflict from the registry in DI
        // (production registers it in AddYFexMessagingRpcClient); mirror that here.
        services.AddSingleton(registry);
        services.AddTransient<IQueryHandler<TestAggregate.Queries.GetByIdQuery, TestItem>,
            TestDispatcherFixture.GetByIdHandler>();
        if (throwingHandler)
            services.AddTransient<ICommandHandler<TestAggregate.Commands.CreateCommand, TestItem>, ThrowingCreateHandler>();
        else
            services.AddTransient<ICommandHandler<TestAggregate.Commands.CreateCommand, TestItem>,
                TestDispatcherFixture.CreateCommandHandler>();
        if (registerResolver)
            services.AddSingleton<IConflictResolver<TestAggregate.Commands.CreateCommand>>(
                DiscardConflictResolver<TestAggregate.Commands.CreateCommand>.Instance);
        var sp = services.BuildServiceProvider();

        var dispatcher = new LocalDispatcher(
            new LocalHandlerInvoker(sp, YFexLocalHandlerRegistrations.BuildLocalHandlerInvokerRegistry()),
            registry, network, cache, outbox, bus, sp);

        // Seed the cache online, then enqueue the command offline (marks the cache entry stale).
        _ = await dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(
            new TestAggregate.Queries.GetByIdQuery(1));
        network.GoOffline();
        _ = await dispatcher.CommandAsync<TestAggregate.Commands.CreateCommand, TestItem>(
            new TestAggregate.Commands.CreateCommand(1, "Offline"));
        outbox.PendingCount.Should().Be(1, "the queueable command must be enqueued while offline");

        var options = new YFexMessagingRpcClientOptions { OutboxEntryTtl = TimeSpan.FromDays(7) };
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<OutboxReplayer>();
        var replayer = new OutboxReplayer(
            outbox, failureLog, dispatcher, network, syncStatus, bus,
#pragma warning disable IL2026, IL3050 // reflection-JSON replay map is fine in tests (non-AOT)
            YFexOutboxCommandRegistrations.BuildOutboxCommandRegistry(),
#pragma warning restore IL2026, IL3050
            cache, registry, sp, options, logger);

        using var cts = new CancellationTokenSource();
        await replayer.StartAsync(cts.Token);
        network.GoOnline();
        await Task.Delay(waitMs);

        await cts.CancelAsync();
        replayer.Dispose();

        return (outbox, failureLog, cache, bus);
    }

    // ── Conflict-test configurations & handlers (private → skipped by Pipeline B) ─

    private sealed class RejectingCommandConfig : IAggregateConfiguration<TestAggregate>
    {
        private readonly ConflictPolicy _policy;
        public RejectingCommandConfig(ConflictPolicy policy) => _policy = policy;

        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
        {
            b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>().Cacheable();
            b.Command<TestAggregate.Commands.CreateCommand, TestItem>()
                // A failing validator makes the online replay return a rejected QueueableResult (an Error).
                .Validate((_, _) => new ValueTask<ValidationResult>(
                    ValidationResult.Of(new Violation(FieldPath.Of("Id"), "rejected", detail: "rejected by server"))))
                .Invalidates<TestAggregate.Queries.GetByIdQuery, TestItem>()
                .OnConflict(_policy);
        }
    }

    private sealed class TransientCommandConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
        {
            b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>().Cacheable();
            b.Command<TestAggregate.Commands.CreateCommand, TestItem>()
                // No validator → the handler runs and THROWS (a transient failure, not a rejection).
                .Invalidates<TestAggregate.Queries.GetByIdQuery, TestItem>()
                .OnConflict(ConflictPolicy.Discard);
        }
    }

    private sealed class ThrowingCreateHandler
        : ICommandHandler<TestAggregate.Commands.CreateCommand, TestItem>
    {
        public Task<TestItem> HandleAsync(TestAggregate.Commands.CreateCommand cmd, CancellationToken ct)
            => throw new TimeoutException("transient network failure");
    }
}
