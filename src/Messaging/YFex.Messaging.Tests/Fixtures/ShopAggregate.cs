using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging;
using YFex.Messaging.Rpc;
using YFex.Persistence;
using YFex.Validation;

namespace YFex.Messaging.Tests.Fixtures;

/// <summary>Messages with shape rules declared ONLY through fluent <c>.Validation(...)</c> (validation Phase 3).</summary>
public partial class ShopAggregate
{
    public static partial class Commands
    {
        public sealed partial record CreateCustomer(string Name, string Email, string Document) : ICommand<int>, IQueueable;
    }

    public static partial class Queries
    {
        public sealed partial record SearchCustomers(string Term, int Take) : IQuery<List<string>>;

        /// <summary>Paging is clamped (coerce), never rejected; cacheable.</summary>
        public sealed partial record PagedCustomers(string Term, int Take) : IQuery<List<string>>, ICacheable;

        /// <summary>Plain range (rejects); cacheable.</summary>
        public sealed partial record CachedSearch(string Term, int Take) : IQuery<List<string>>, ICacheable;
    }
}

/// <summary>Baseline (client + server): shape rules only.</summary>
public sealed class ShopBaseline : IAggregateConfiguration<ShopAggregate>
{
    public void Configure(AggregateConfigurationBuilder<ShopAggregate> b)
    {
        b.Command<ShopAggregate.Commands.CreateCustomer, int>()
            .Validation(v =>
            {
                v.Field(c => c.Name).Required().Length(2, 80);
                v.Field(c => c.Email).Required().Email();
            });
        b.Query<ShopAggregate.Queries.SearchCustomers, List<string>>()
            .Validation(v => v.Field(q => q.Take).Range(1, 100));
        b.Query<ShopAggregate.Queries.PagedCustomers, List<string>>()
            .Validation(v => v.Field(q => q.Take).Range(1, 50, coerce: true))
            .Cacheable();
        b.Query<ShopAggregate.Queries.CachedSearch, List<string>>()
            .Validation(v => v.Field(q => q.Take).Range(1, 100))
            .Cacheable();
    }
}

/// <summary>Records the order in which the pipeline stages ran.</summary>
public sealed class StageLog
{
    public List<string> Entries { get; } = [];
}

/// <summary>Server-side context rule (stage 5): e-mail must not be registered yet.</summary>
public sealed class EmailNotRegistered(StageLog log) : IContextRule<ShopAggregate.Commands.CreateCustomer>
{
    public const string TakenEmail = "taken@shop.test";

    public ValueTask CheckAsync(ShopAggregate.Commands.CreateCustomer value, ViolationCollector violations, CancellationToken ct)
    {
        log.Entries.Add("context");
        if (value.Email == TakenEmail) violations.Add(FieldPath.Of("Email"), "customer.email-taken", default, Severity.Error);
        return ValueTask.CompletedTask;
    }
}

public sealed class CreateCustomerHandler(StageLog log) : ICommandHandler<ShopAggregate.Commands.CreateCustomer, int>
{
    public Task<int> HandleAsync(ShopAggregate.Commands.CreateCustomer command, CancellationToken ct)
    {
        log.Entries.Add("handler");
        return Task.FromResult(42);
    }
}

public sealed class PagedCustomersHandler(StageLog log) : IQueryHandler<ShopAggregate.Queries.PagedCustomers, List<string>>
{
    public Task<List<string>> HandleAsync(ShopAggregate.Queries.PagedCustomers query, CancellationToken ct)
    {
        log.Entries.Add("paged:" + query.Take);
        return Task.FromResult(new List<string> { query.Term + ":" + query.Take });
    }
}

public sealed class CachedSearchHandler(StageLog log) : IQueryHandler<ShopAggregate.Queries.CachedSearch, List<string>>
{
    public Task<List<string>> HandleAsync(ShopAggregate.Queries.CachedSearch query, CancellationToken ct)
    {
        log.Entries.Add("cached:" + query.Take);
        return Task.FromResult(new List<string> { query.Term });
    }
}

public sealed class SearchCustomersHandler : IQueryHandler<ShopAggregate.Queries.SearchCustomers, List<string>>
{
    public Task<List<string>> HandleAsync(ShopAggregate.Queries.SearchCustomers query, CancellationToken ct)
        => Task.FromResult(new List<string> { query.Term + ":" + query.Take });
}

/// <summary>A LocalDispatcher over <see cref="ShopAggregate"/> with a spy log and optional server overrides.</summary>
public sealed class ShopFixture
{
    public ShopFixture(IEnumerable<IAggregateConfiguration>? serverOverrides = null,
        Func<System.Security.Claims.ClaimsPrincipal, ShopAggregate.Commands.CreateCustomer, bool>? authorize = null,
        bool connected = true, bool decorateInvoker = false,
        Action<IServiceCollection>? configureServices = null)
    {
        var overrides = new List<IAggregateConfiguration>(serverOverrides ?? []);
        if (authorize is not null) overrides.Add(new AuthorizeOverride(authorize, Log));
        Registry = CompiledMessagingRegistry.Build(baseline: [new ShopBaseline()], serverOverrides: overrides);

        var services = new ServiceCollection();
        services.AddSingleton(Log);
        services.AddTransient<ICommandHandler<ShopAggregate.Commands.CreateCustomer, int>, CreateCustomerHandler>();
        services.AddTransient<IQueryHandler<ShopAggregate.Queries.SearchCustomers, List<string>>, SearchCustomersHandler>();
        services.AddTransient<IQueryHandler<ShopAggregate.Queries.PagedCustomers, List<string>>, PagedCustomersHandler>();
        services.AddTransient<IQueryHandler<ShopAggregate.Queries.CachedSearch, List<string>>, CachedSearchHandler>();
        configureServices?.Invoke(services);
        Services = services.BuildServiceProvider();

        IHandlerInvoker invoker = new LocalHandlerInvoker(Services, new LocalHandlerInvokerRegistryBuilder()
            .AddCommand<ShopAggregate.Commands.CreateCustomer, int>()
            .AddQuery<ShopAggregate.Queries.SearchCustomers, List<string>>()
            .AddQuery<ShopAggregate.Queries.PagedCustomers, List<string>>()
            .AddQuery<ShopAggregate.Queries.CachedSearch, List<string>>()
            .Build());
        Invoker = decorateInvoker ? new ValidatingHandlerInvoker(invoker, Registry, Services) : invoker;

        Network = new ManualNetworkStatus(connected);
        Cache = new InMemoryCache();
        Outbox = new InMemoryOutbox(new OutboxOptions());
        Dispatcher = new LocalDispatcher(Invoker, Registry, Network, Cache, Outbox, new DefaultEventBus(), Services);
    }

    public StageLog Log { get; } = new();
    public CompiledMessagingRegistry Registry { get; }
    public IServiceProvider Services { get; }
    public IHandlerInvoker Invoker { get; }
    public ManualNetworkStatus Network { get; }
    public InMemoryCache Cache { get; }
    public InMemoryOutbox Outbox { get; }
    public LocalDispatcher Dispatcher { get; }

    private sealed class AuthorizeOverride(
        Func<System.Security.Claims.ClaimsPrincipal, ShopAggregate.Commands.CreateCustomer, bool> authorize, StageLog log)
        : IServerAggregateConfiguration<ShopAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<ShopAggregate> b)
            => b.Command<ShopAggregate.Commands.CreateCustomer, int>().Authorize((u, c) =>
            {
                log.Entries.Add("authorize");
                return authorize(u, c);
            });
    }
}

/// <summary>Server override: the I/O context rule (09 §3).</summary>
public sealed class ShopServer : IServerAggregateConfiguration<ShopAggregate>
{
    public void Configure(AggregateConfigurationBuilder<ShopAggregate> b)
        => b.Command<ShopAggregate.Commands.CreateCustomer, int>().Validate<EmailNotRegistered>();
}

/// <summary>Opt-in handler shape (D24): receives the command already validated.</summary>
public sealed class ValidatedCreateCustomerHandler(StageLog log) : IValidatedCommandHandler<ShopAggregate.Commands.CreateCustomer, int>
{
    public Task<int> HandleAsync(Valid<ShopAggregate.Commands.CreateCustomer> command, CancellationToken ct)
    {
        log.Entries.Add("validated-handler:" + command.Value.Name);
        return Task.FromResult(7);
    }
}
