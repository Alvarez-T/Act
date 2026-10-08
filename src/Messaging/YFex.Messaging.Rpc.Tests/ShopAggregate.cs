using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization.Public;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Tenancy;
using YFex.Validation;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>Contracts shared by client and server. Shape rules come ONLY from fluent <c>.Validation(...)</c>.</summary>
public partial class ShopAggregate
{
    public static partial class Commands
    {
        [MemoryPackable]
        public sealed partial record CreateCustomer(string Name, string Email, string Document) : ICommand<int>;

        /// <summary>Requirement declared ONLY by the attribute on the record (⚑Z6) — enforced by the server gate (D1).</summary>
        [MemoryPackable, RequirePermission(ShopPermissions.Ids.ArchiveCustomers)]
        public sealed partial record ArchiveCustomer(int Id) : ICommand<int>;
    }

    public static partial class Queries
    {
        [MemoryPackable]
        public sealed partial record SearchCustomers(string Term, int Take) : IQuery<List<string>>;

        /// <summary>"{subject}@{tenant}" of the caller — no identity field anywhere (D29: the context is implicit).</summary>
        [MemoryPackable]
        public sealed partial record WhoAmI(int Nonce) : IQuery<string>;

        /// <summary>Requirement declared ONLY by a server <c>ForMessage&lt;T&gt;()</c> row (D1).</summary>
        [MemoryPackable]
        public sealed partial record ExportCustomers(int Take) : IQuery<int>;
    }
}

public sealed class ShopBaseline : IAggregateConfiguration<ShopAggregate>
{
    public void Configure(AggregateConfigurationBuilder<ShopAggregate> b)
    {
        // Authorization declared ONCE here: FusionMessageBus checks it client-side (UX), the server enforces it (D29).
        b.Command<ShopAggregate.Commands.CreateCustomer, int>()
            .RequirePermission(ShopPermissions.CreateCustomer)
            .Validation(v =>
            {
                v.Field(c => c.Name).Required().Length(2, 80);
                v.Field(c => c.Email).Required().Email();
                v.Field(c => c.Document).Required().Length(11, 14);
            });
        b.Query<ShopAggregate.Queries.SearchCustomers, List<string>>()
            .RequirePermission(ShopPermissions.SearchCustomers)
            .Validation(v => v.Field(q => q.Take).Range(1, 100));
        b.Query<ShopAggregate.Queries.WhoAmI, string>()
            .RequireAuthenticated()
            .ScopedByUser();
    }
}

/// <summary>Server-only: I/O context rule (09 §3).</summary>
public sealed class ShopServer : IServerAggregateConfiguration<ShopAggregate>
{
    public void Configure(AggregateConfigurationBuilder<ShopAggregate> b)
        => b.Command<ShopAggregate.Commands.CreateCustomer, int>().Validate<EmailNotRegistered>();
}

public sealed class EmailNotRegistered : IContextRule<ShopAggregate.Commands.CreateCustomer>
{
    public const string Taken = "taken@shop.test";

    public ValueTask CheckAsync(ShopAggregate.Commands.CreateCustomer value, ViolationCollector violations, CancellationToken ct)
    {
        if (value.Email == Taken) violations.Add(FieldPath.Of("Email"), "customer.email-taken", RawValue.From(value.Email), Severity.Error);
        return ValueTask.CompletedTask;
    }
}

public sealed class CreateCustomerHandler : ICommandHandler<ShopAggregate.Commands.CreateCustomer, int>
{
    public int Calls;

    public Task<int> HandleAsync(ShopAggregate.Commands.CreateCustomer command, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(42);
    }
}

/// <summary>Reads the caller only through the ambient APIs every library already uses: <see cref="IPrincipalAccessor"/>
/// (decorated by the RPC server) and YFex.Tenancy's effective scope.</summary>
public sealed class WhoAmIHandler(IServiceProvider services) : IQueryHandler<ShopAggregate.Queries.WhoAmI, string>
{
    public int Calls;

    /// <summary>The ambient actor the last call ran under (authorization-consolidation 06 §4: the RPC actor writer).</summary>
    public ActorScope? LastActor;

    public Task<string> HandleAsync(ShopAggregate.Queries.WhoAmI query, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        LastActor = ActorContext.Current;
        var user = services.GetRequiredService<IPrincipalAccessor>().Current;
        var tenant = services.GetService<IScopeContext>().EffectiveIdentity();
        return Task.FromResult((CallContext.SubjectOf(user.Principal) ?? "anonymous") + "@" + Users.TenantOf(tenant));
    }
}

public sealed class ArchiveCustomerHandler : ICommandHandler<ShopAggregate.Commands.ArchiveCustomer, int>
{
    public Task<int> HandleAsync(ShopAggregate.Commands.ArchiveCustomer command, CancellationToken ct) => Task.FromResult(command.Id);
}

public sealed class ExportCustomersHandler : IQueryHandler<ShopAggregate.Queries.ExportCustomers, int>
{
    public Task<int> HandleAsync(ShopAggregate.Queries.ExportCustomers query, CancellationToken ct) => Task.FromResult(query.Take);
}

public sealed class SearchCustomersHandler : IQueryHandler<ShopAggregate.Queries.SearchCustomers, List<string>>
{
    public Task<List<string>> HandleAsync(ShopAggregate.Queries.SearchCustomers query, CancellationToken ct)
        => Task.FromResult(new List<string> { query.Term + ":" + query.Take });
}
