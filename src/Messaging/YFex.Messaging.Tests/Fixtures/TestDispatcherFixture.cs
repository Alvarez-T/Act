using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Rpc;

namespace YFex.Messaging.Tests.Fixtures;

/// <summary>
/// Builds a fully-wired <see cref="LocalDispatcher"/> for integration tests.
/// All infrastructure is in-memory; network status is controllable via <see cref="Network"/>.
/// </summary>
public sealed class TestDispatcherFixture
{
    public ManualNetworkStatus Network { get; }
    public InMemoryClientCache Cache { get; }
    public InMemoryOutbox Outbox { get; }
    public InMemorySyncFailureLog FailureLog { get; }
    public DefaultEventBus EventBus { get; }
    public LocalDispatcher Dispatcher { get; }
    public CompiledMessagingRegistry Registry { get; }

    public TestDispatcherFixture(
        IEnumerable<IAggregateConfiguration>? configurations = null,
        IEnumerable<IAggregateConfiguration>? clientOverrides = null,
        bool startConnected = true,
        OutboxOptions? outboxOptions = null)
    {
        Network = new ManualNetworkStatus(startConnected);
        Cache = new InMemoryClientCache();
        Outbox = new InMemoryOutbox(outboxOptions ?? new OutboxOptions());
        FailureLog = new InMemorySyncFailureLog();
        Outbox.SetFailureLog(FailureLog);
        EventBus = new DefaultEventBus();

        Registry = CompiledMessagingRegistry.Build(
            baseline: configurations ?? [],
            clientOverrides: clientOverrides,
            scanForImplementers: [typeof(TestDispatcherFixture).Assembly]);

        // Build DI container with test handlers
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<IQueryHandler<TestAggregate.Queries.GetByIdQuery, TestItem>, GetByIdHandler>();
        services.AddTransient<IQueryHandler<TestAggregate.Queries.GetAllQuery, List<TestItem>>, GetAllHandler>();
        services.AddTransient<ICommandHandler<TestAggregate.Commands.CreateCommand, TestItem>, CreateCommandHandler>();
        services.AddTransient<ICommandHandler<TestAggregate.Commands.RenameCommand, TestItem>, RenameCommandHandler>();
        services.AddTransient<ICommandHandler<TestAggregate.Commands.DeleteCommand>, DeleteCommandHandler>();
        var sp = services.BuildServiceProvider();

        Dispatcher = new LocalDispatcher(
            new LocalHandlerInvoker(sp),
            Registry,
            Network,
            Cache,
            Outbox,
            EventBus,
            sp);
    }

    // ── In-memory store shared by all test handlers ───────────────────────────

    public static readonly Dictionary<int, TestItem> Store = new();

    public static void ClearStore() => Store.Clear();

    // ── Handler implementations ───────────────────────────────────────────────

    public sealed class GetByIdHandler : IQueryHandler<TestAggregate.Queries.GetByIdQuery, TestItem>
    {
        public Task<TestItem> HandleAsync(TestAggregate.Queries.GetByIdQuery q, CancellationToken ct)
        {
            Store.TryGetValue(q.Id, out var item);
            return Task.FromResult(item ?? new TestItem(q.Id, "unknown"));
        }
    }

    public sealed class GetAllHandler : IQueryHandler<TestAggregate.Queries.GetAllQuery, List<TestItem>>
    {
        public Task<List<TestItem>> HandleAsync(TestAggregate.Queries.GetAllQuery q, CancellationToken ct)
            => Task.FromResult(Store.Values.ToList());
    }

    public sealed class CreateCommandHandler : ICommandHandler<TestAggregate.Commands.CreateCommand, TestItem>
    {
        public Task<TestItem> HandleAsync(TestAggregate.Commands.CreateCommand cmd, CancellationToken ct)
        {
            var item = new TestItem(cmd.Id, cmd.Name);
            Store[cmd.Id] = item;
            return Task.FromResult(item);
        }
    }

    public sealed class RenameCommandHandler : ICommandHandler<TestAggregate.Commands.RenameCommand, TestItem>
    {
        public Task<TestItem> HandleAsync(TestAggregate.Commands.RenameCommand cmd, CancellationToken ct)
        {
            Store.TryGetValue(cmd.Id, out var existing);
            var updated = (existing ?? new TestItem(cmd.Id, "")) with { Name = cmd.NewName };
            Store[cmd.Id] = updated;
            return Task.FromResult(updated);
        }
    }

    public sealed class DeleteCommandHandler : ICommandHandler<TestAggregate.Commands.DeleteCommand>
    {
        public Task HandleAsync(TestAggregate.Commands.DeleteCommand cmd, CancellationToken ct)
        {
            Store.Remove(cmd.Id);
            return Task.CompletedTask;
        }
    }
}
