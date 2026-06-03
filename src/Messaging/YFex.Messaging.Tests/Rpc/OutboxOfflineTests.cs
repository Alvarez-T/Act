using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Rpc;
using YFex.Messaging.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace YFex.Messaging.Tests.Rpc;

/// <summary>Tests #17â€“22: Offline enqueue, cache serving, invalidation markers, and OnOffline handlers.</summary>
[Trait("Category", "Rpc")]
[Collection("DispatcherTests")]
public sealed class OutboxOfflineTests
{
    public OutboxOfflineTests() => TestDispatcherFixture.ClearStore();

    // â”€â”€ Test #17: IQueueable offline write â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task QueueableCommand_Offline_EnqueuesAndReturnsQueued()
    {
        var fx = new TestDispatcherFixture(startConnected: false);

        var result = await fx.Dispatcher.CommandAsync<TestAggregate.Commands.CreateCommand, TestItem>(
            new TestAggregate.Commands.CreateCommand(1, "Alice"));

        result.IsQueued.Should().BeTrue("IQueueable command offline must return Queued");
        fx.Outbox.PendingCount.Should().Be(1, "command must be stored in the outbox");
    }

    [Fact]
    public async Task NonQueueableCommand_Offline_ReturnsFail()
    {
        var fx = new TestDispatcherFixture(startConnected: false);

        var result = await fx.Dispatcher.CommandAsync<TestAggregate.Commands.DeleteCommand>(
            new TestAggregate.Commands.DeleteCommand(99));

        result.IsOk.Should().BeFalse("non-IQueueable command must fail when offline");
    }

    // â”€â”€ Test #18: ICacheable offline read â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task CacheableQuery_Offline_ServesFromCache()
    {
        TestDispatcherFixture.Store[5] = new TestItem(5, "Cached");
        var fx = new TestDispatcherFixture(configurations: [new CacheableConfig()]);

        // Online fetch populates the cache
        _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(
            new TestAggregate.Queries.GetByIdQuery(5));

        fx.Network.GoOffline();
        var offlineResult = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(
            new TestAggregate.Queries.GetByIdQuery(5));

        offlineResult.IsOk.Should().BeTrue("ICacheable query served from cache offline");
        offlineResult.OkValue!.Name.Should().Be("Cached");
    }

    [Fact]
    public async Task NonCacheableQuery_Offline_ReturnsFail()
    {
        var fx = new TestDispatcherFixture(startConnected: false);

        var result = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetAllQuery, List<TestItem>>(
            new TestAggregate.Queries.GetAllQuery());

        result.IsOk.Should().BeFalse("non-ICacheable query fails offline with no cached value");
    }

    // â”€â”€ Test #19: Invalidate-on-enqueue (IsStale) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task QueueableCommand_Offline_MarksInvalidationTargetsStale()
    {
        TestDispatcherFixture.Store[3] = new TestItem(3, "ToMark");
        var fx = new TestDispatcherFixture(configurations: [new InvalidationConfig()]);

        // Cache the query online
        _ = await fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(
            new TestAggregate.Queries.GetByIdQuery(3));

        // Go offline and enqueue command that invalidates the query
        fx.Network.GoOffline();
        _ = await fx.Dispatcher.CommandAsync<TestAggregate.Commands.CreateCommand, TestItem>(
            new TestAggregate.Commands.CreateCommand(3, "Updated"));

        // The cache entry should now be marked stale
        var prefix = $"query:{typeof(TestAggregate.Queries.GetByIdQuery).FullName}";
        var keys = await fx.Cache.GetKeysWithPrefixAsync(prefix);
        keys.Should().NotBeEmpty("stale entry must still exist in cache (not deleted)");
        keys.Any(k => fx.Cache.IsStale(k)).Should().BeTrue(
            "queuing an offline command must mark invalidation-target cache entries as stale");
    }

    // â”€â”€ Test #21: OnOffline lambda override â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task OnOffline_Lambda_RunsLocally_ThenEnqueues()
    {
        bool sideEffectRan = false;
        var fx = new TestDispatcherFixture(
            configurations: [new OnOfflineLambdaConfig((_, _) => { sideEffectRan = true; return ValueTask.CompletedTask; })],
            startConnected: false);

        var result = await fx.Dispatcher.CommandAsync<TestAggregate.Commands.CreateCommand, TestItem>(
            new TestAggregate.Commands.CreateCommand(10, "Offline"));

        sideEffectRan.Should().BeTrue("OnOffline lambda must execute when offline");
        result.IsQueued.Should().BeTrue("IQueueable command still enqueues after OnOffline runs");
        fx.Outbox.PendingCount.Should().Be(1);
    }

    // â”€â”€ Test #22: OnOffline<THandler> type-form â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task OnOffline_TypeForm_ResolvesHandlerFromDI_AndEnqueues()
    {
        RecordingOfflineHandler.Reset();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<IQueryHandler<TestAggregate.Queries.GetByIdQuery, TestItem>,
            TestDispatcherFixture.GetByIdHandler>();
        services.AddTransient<IQueryHandler<TestAggregate.Queries.GetAllQuery, List<TestItem>>,
            TestDispatcherFixture.GetAllHandler>();
        services.AddTransient<ICommandHandler<TestAggregate.Commands.CreateCommand, TestItem>,
            TestDispatcherFixture.CreateCommandHandler>();
        services.AddTransient<ICommandHandler<TestAggregate.Commands.RenameCommand, TestItem>,
            TestDispatcherFixture.RenameCommandHandler>();
        services.AddTransient<ICommandHandler<TestAggregate.Commands.DeleteCommand>,
            TestDispatcherFixture.DeleteCommandHandler>();
        services.AddTransient<IOfflineHandler<TestAggregate.Commands.CreateCommand, TestItem>,
            RecordingOfflineHandler>();
        var sp = services.BuildServiceProvider();

        var network = new ManualNetworkStatus(connected: false);
        var cache = new InMemoryClientCache();
        var outbox = new InMemoryOutbox(new OutboxOptions());
        var failLog = new InMemorySyncFailureLog();
        outbox.SetFailureLog(failLog);
        var bus = new DefaultEventBus();
        var registry = CompiledMessagingRegistry.Build(
            baseline: [new OnOfflineTypeConfig()],
            scanForImplementers: [typeof(OutboxOfflineTests).Assembly]);

        var dispatcher = new LocalDispatcher(
            new LocalHandlerInvoker(sp), registry, network, cache, outbox, bus, sp);

        var result = await dispatcher.CommandAsync<TestAggregate.Commands.CreateCommand, TestItem>(
            new TestAggregate.Commands.CreateCommand(77, "TypeHandler"));

        RecordingOfflineHandler.WasCalled.Should().BeTrue(
            "IOfflineHandler<TCommand, TResult> must be resolved from DI and called");
        result.IsQueued.Should().BeTrue("IQueueable command still enqueues after handler runs");
    }

    // â”€â”€ Private configurations (skipped by Pipeline B) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private sealed class CacheableConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>().Cacheable();
    }

    private sealed class InvalidationConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
        {
            b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>().Cacheable();
            b.Command<TestAggregate.Commands.CreateCommand, TestItem>()
                .Invalidates<TestAggregate.Queries.GetByIdQuery, TestItem>();
        }
    }

    private sealed class OnOfflineLambdaConfig : IAggregateConfiguration<TestAggregate>
    {
        private readonly Func<TestAggregate.Commands.CreateCommand, CancellationToken, ValueTask> _h;
        public OnOfflineLambdaConfig(Func<TestAggregate.Commands.CreateCommand, CancellationToken, ValueTask> h) => _h = h;
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.CreateCommand, TestItem>().OnOffline(_h);
    }

    private sealed class OnOfflineTypeConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Command<TestAggregate.Commands.CreateCommand, TestItem>()
                .OnOffline<RecordingOfflineHandler>();
    }

    private sealed class RecordingOfflineHandler
        : IOfflineHandler<TestAggregate.Commands.CreateCommand, TestItem>
    {
        public static bool WasCalled { get; private set; }
        public static void Reset() => WasCalled = false;

        public ValueTask<TestItem> HandleAsync(TestAggregate.Commands.CreateCommand cmd, CancellationToken ct)
        {
            WasCalled = true;
            return new ValueTask<TestItem>(new TestItem(cmd.Id, cmd.Name));
        }
    }
}
