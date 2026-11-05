using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Rpc;
using YFex.Messaging.Tests.Fixtures;
using YFex.Persistence;

namespace YFex.Messaging.Tests.Rpc;

/// <summary>
/// The Messaging.Rpc hooks of offline Phase 3 (docs/internal/offline/11): admission before an offline handler, observers of queued
/// entries (the envelope custody), and a replay route that replaces re-dispatching (central's offline intake).
/// </summary>
[Trait("Category", "Rpc")]
public sealed class OfflineHooksTests
{
    private sealed class OfflineConfig(bool handlerThrows) : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.CreateCommand, TestItem>()
                .OnOffline((command, _) => handlerThrows ? throw new InvalidOperationException("local stock unavailable") : ValueTask.CompletedTask);
    }

    private sealed class RecordingAdmission(Error refusal = default) : IOfflineAdmission
    {
        public List<object> Admitted { get; } = [];
        public List<object> Abandoned { get; } = [];

        public ValueTask<Error> AdmitAsync(object command, Type commandType, CancellationToken cancellationToken)
        {
            Admitted.Add(command);
            return ValueTask.FromResult(refusal);
        }

        public void Abandon(object command) => Abandoned.Add(command);
    }

    private sealed class RecordingObserver : IOutboxEnqueueObserver
    {
        public List<(object Command, Type Type, OutboxEntry Entry)> Seen { get; } = [];

        public ValueTask OnEnqueuedAsync(object command, Type commandType, OutboxEntry entry, CancellationToken cancellationToken)
        {
            Seen.Add((command, commandType, entry));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RoutingEverything : IOutboxReplayRoute
    {
        public List<Guid> Routed { get; } = [];

        public ValueTask<ReplayOutcome?> TryReplayAsync(OutboxEntry entry, CancellationToken cancellationToken)
        {
            Routed.Add(entry.IdempotencyKey);
            return ValueTask.FromResult<ReplayOutcome?>(ReplayOutcome.Success);
        }
    }

    private static (LocalDispatcher Dispatcher, InMemoryOutbox Outbox) Offline(bool handlerThrows, IOfflineAdmission admission)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(admission);
        ServiceProvider sp = services.BuildServiceProvider();
        var outbox = new InMemoryOutbox(new OutboxOptions());
        var dispatcher = new LocalDispatcher(new LocalHandlerInvoker(sp, YFexLocalHandlerRegistrations.BuildLocalHandlerInvokerRegistry()),
            CompiledMessagingRegistry.Build(baseline: [new OfflineConfig(handlerThrows)]), new ManualServerConnection(connected: false),
            new InMemoryCache(), outbox, new DefaultEventBus(), sp);
        return (dispatcher, outbox);
    }

    [Fact]
    public async Task A_refused_admission_runs_nothing_and_queues_nothing()
    {
        var admission = new RecordingAdmission(Error.Unavailable(UnavailableReason.AllowanceExhausted, "acima do limite offline"));
        var (dispatcher, outbox) = Offline(handlerThrows: false, admission);
        var command = new TestAggregate.Commands.CreateCommand(1, "Venda");

        QueueableResult<TestItem> result = await dispatcher.CommandAsync<TestAggregate.Commands.CreateCommand, TestItem>(command);

        result.TryGetError(out Error error).Should().BeTrue();
        error.TryGetValue(out Unavailable unavailable).Should().BeTrue();
        unavailable.Reason.Should().Be(UnavailableReason.AllowanceExhausted);
        admission.Admitted.Should().ContainSingle().Which.Should().BeSameAs(command);
        outbox.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task A_failing_offline_handler_abandons_the_admission()
    {
        var admission = new RecordingAdmission();
        var (dispatcher, outbox) = Offline(handlerThrows: true, admission);
        var command = new TestAggregate.Commands.CreateCommand(2, "Venda");

        Func<Task> act = async () => await dispatcher.CommandAsync<TestAggregate.Commands.CreateCommand, TestItem>(command);

        await act.Should().ThrowAsync<InvalidOperationException>();
        admission.Abandoned.Should().ContainSingle().Which.Should().BeSameAs(command);
        outbox.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Observers_see_every_queued_entry_of_the_client_outbox()
    {
        var observer = new RecordingObserver();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOutboxEnqueueObserver>(observer);
        services.AddYFexMessagingRpcClient();
        await using ServiceProvider sp = services.BuildServiceProvider();
        IOutbox outbox = sp.GetRequiredService<IOutbox>();
        var command = new TestAggregate.Commands.CreateCommand(3, "Venda");
        int enqueuedEvents = 0;
        outbox.Enqueued += _ => enqueuedEvents++;

        Queued queued = await outbox.EnqueueAsync(command);

        observer.Seen.Should().ContainSingle();
        observer.Seen[0].Command.Should().BeSameAs(command);
        observer.Seen[0].Type.Should().Be<TestAggregate.Commands.CreateCommand>();
        observer.Seen[0].Entry.IdempotencyKey.Should().Be(queued.IdempotencyKey);
        enqueuedEvents.Should().Be(1, "the wrapper forwards the inner outbox's events");
        (await outbox.ListPendingAsync()).Should().ContainSingle();
    }

    [Fact]
    public void Without_observers_the_client_outbox_is_not_wrapped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddYFexMessagingRpcClient();
        using ServiceProvider sp = services.BuildServiceProvider();
        sp.GetRequiredService<IOutbox>().Should().BeOfType<InMemoryOutbox>();
    }

    [Fact]
    public async Task A_replay_route_replaces_re_dispatching()
    {
        var network = new ManualServerConnection(connected: false);
        var outbox = new InMemoryOutbox(new OutboxOptions());
        var failureLog = new InMemorySyncFailureLog();
        outbox.SetFailureLog(failureLog);
        var registry = CompiledMessagingRegistry.Build(baseline: []);
        var route = new RoutingEverything();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOutboxReplayRoute>(route);
        ServiceProvider sp = services.BuildServiceProvider();
        var dispatcher = new LocalDispatcher(new LocalHandlerInvoker(sp, YFexLocalHandlerRegistrations.BuildLocalHandlerInvokerRegistry()),
            registry, network, new InMemoryCache(), outbox, new DefaultEventBus(), sp);
        var replayer = new OutboxReplayer(outbox, failureLog, dispatcher, network, new SyncStatus(), new DefaultEventBus(), OutboxCommandRegistry.Empty,
            new InMemoryCache(), registry, sp, new YFexMessagingRpcClientOptions(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<OutboxReplayer>());
        Queued queued = await outbox.EnqueueAsync(new TestAggregate.Commands.CreateCommand(4, "Venda"));

        using var cts = new CancellationTokenSource();
        await replayer.StartAsync(cts.Token);
        network.GoOnline();
        for (int i = 0; i < 100 && outbox.PendingCount > 0; i++) await Task.Delay(20);
        await cts.CancelAsync();
        replayer.Dispose();

        route.Routed.Should().Equal(queued.IdempotencyKey);
        outbox.PendingCount.Should().Be(0, "a routed success drains the entry (no replay handler was needed)");
        failureLog.Failures.Should().BeEmpty();
    }
}
