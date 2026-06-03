# YFex.Cqrs

**Libraries:** YFex.Cqrs, YFex.Cqrs.SourceGenerator, YFex.Cqrs.Analyzer

## 1. Overview

YFex.Cqrs is a CQRS framework built around fluent configuration inspired by EF Core's `IEntityTypeConfiguration<T>`. You declare message records (queries, commands, events) as nested types under domain aggregates, then configure their behavior -- validation, authorization, caching, invalidation, offline handling -- in dedicated configuration classes. At startup, all configurations are collected, merged across baseline/server/client tiers, and compiled into a `CompiledMessagingRegistry` backed by `FrozenDictionary<Type, Policy>` for ~5ns runtime lookups with zero reflection on hot paths. A Roslyn incremental source generator emits static call helpers so consuming code never touches the dispatcher directly.

---

## 2. Core Concepts & Mental Model

### Three-Phase Pipeline

Every message type flows through three phases, each running at a different time:

```
Registration (startup)           Compilation (startup)          Runtime (per-request)
IAggregateConfiguration          CompiledMessagingRegistry      IDispatcher
          |                              .Build()                    |
    Fluent builder            Expression.Compile()            FrozenDictionary
    accumulates               merges overrides,               lookup (~5ns) +
    descriptors               expands groups,                 pre-compiled
                              freezes into                    delegate calls
                              sealed records
```

**Registration** collects typed metadata from your configuration classes. **Compilation** merges baseline + server/client overrides, expands invalidation groups, compiles `Expression<Func<>>` predicates into delegates, and freezes everything into immutable `FrozenDictionary` instances. **Runtime** dispatches each message through a single dictionary lookup followed by pre-compiled delegate invocations -- no LINQ, no reflection, no allocations.

### Message Contracts

```csharp
using YFex.Cqrs;

// Queries: read operations that return data
public partial record GetCustomerQuery(Guid Id) : IQuery<Customer>;

// Commands: write operations, optionally returning a result
public partial record CreateCustomerCommand(string Name, string Email) : ICommand<Customer>;
public partial record DeleteCustomerCommand(Guid Id) : ICommand;

// Events: published notifications
public partial record CustomerCreated(Guid Id, string Name) : IEvent;
```

### Marker Interfaces

Marker interfaces opt a message into cross-cutting behavior without any configuration:

| Marker | Applies to | Effect |
|---|---|---|
| `IQueueable` | Commands | Enqueued to outbox when offline; result type becomes `QueueableResult<T>` |
| `ICacheable` | Queries | Served from `IClientCache` when disconnected |
| `IInvalidationGroup` | Interfaces | Group token for fan-out cache invalidation |
| `IEventGroup` | Interfaces | Group token for `[Subscribe<>]` fan-out |

```csharp
using YFex.Cqrs;

// This command will be queued when offline and replayed on reconnect
public partial record PlaceOrderCommand(OrderData Data) : ICommand<Order>, IQueueable;

// This query's result can be served from local cache when disconnected
public partial record GetProductQuery(Guid Id) : IQuery<Product>, ICacheable;

// Group marker: any command can invalidate all queries implementing this interface at once
public interface IProductReads : IInvalidationGroup { }
public partial record GetProductQuery(Guid Id) : IQuery<Product>, IProductReads;
public partial record ListProductsQuery(int Page) : IQuery<PagedResult<Product>>, IProductReads;
```

### Result Types

All results use zero-boxing discriminated unions via `[Union]`. No heap allocations for the success path:

```csharp
using YFex.Cqrs;

// Result<T> — for queries and result-bearing commands
Result<Customer> result = await Customer.Queries.GetById(id);

if (result.TryGetValue(out var customer))
{
    Console.WriteLine(customer.Name);
}
else if (result.TryGetValue(out Error error))
{
    Console.WriteLine($"{error.Type}: {error.Message}");
}

// Convenience properties for quick checks
if (result.IsOk)
    Console.WriteLine(result.OkValue!.Name);
```

**Factory methods on `Result` / `Result<T>`:**

```csharp
// Success
Result.Ok();
Result<Customer>.Ok(customer);

// Errors — each returns an Error struct that implicitly converts to Result/Result<T>
Result.Fail("Something went wrong");
Result.NotFound("Customer not found");
Result.Unauthorized("Access denied");
Result.Conflict("Duplicate email");
Result.ValidationProblem("Name is required");
```

**`QueueableResult` / `QueueableResult<T>`** extends `Result` with a `Queued` variant for offline-capable commands:

```csharp
using YFex.Cqrs;

QueueableResult<Order> result = await Order.Commands.Place(orderData);

if (result.IsOk)
    Console.WriteLine($"Order placed: {result.OkValue!.Id}");
else if (result.IsQueued && result.TryGetQueued(out Queued queued))
    Console.WriteLine($"Queued for sync: {queued.IdempotencyKey}");
else if (result.TryGetError(out Error error))
    Console.WriteLine($"Failed: {error.Message}");
```

**`Error` struct:**

```csharp
public readonly struct Error
{
    public ErrorType Type { get; }           // NotFound, Fail, Unauthorized, Conflict, ValidationProblem
    public string Message { get; }
    public List<ErrorDetail> Details { get; } // For multi-error validation results
}
```

---

## 3. Integration Model & Lifecycle

### DI Registration

The source generator emits `AddYFexConfigurations()` which registers all `IAggregateConfiguration<T>` types found at compile time:

```csharp
using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;

var builder = WebApplication.CreateBuilder(args);

// Registers all IAggregateConfiguration<T> discovered by the source generator
builder.Services.AddYFexConfigurations();
```

> **Note:** `AddYFexConfigurations()` is generated code -- you will not find it in the library source. It appears after building the project that references `YFex.Cqrs.SourceGenerator`.

### Dispatcher Setup

The dispatcher is the runtime entry point. It is set via `YFexDispatcherProvider` at startup by the hosting infrastructure:

```csharp
// Typically wired by AddYFexMessagingRpcClient (client) or UseYFexMessagingRpcServer (server).
// For tests or simple apps, you can set it manually:
YFexDispatcherProvider.Set(myDispatcher);

// Generated static helpers route through this provider:
// Customer.Queries.GetById(id) calls YFexDispatcherProvider.Current.QueryAsync<...>(...)
```

### Registry Build at Startup

```csharp
using YFex.Cqrs;
using YFex.Cqrs.Configuration;

// Manual build (tests or advanced scenarios):
var registry = CompiledMessagingRegistry.Build(
    baseline: new IAggregateConfiguration[] { new CustomerConfiguration() },
    serverOverrides: null,
    clientOverrides: null,
    scanForImplementers: new[] { typeof(CustomerConfiguration).Assembly });
```

---

## 4. Step-by-Step Usage

### Step 1: Declare Message Records

Records are nested under a domain aggregate. The source generator uses this nesting to scope the emitted static helpers:

```csharp
using YFex.Cqrs;

public partial class Customer
{
    public static partial class Queries
    {
        public partial record GetByIdQuery(Guid Id) : IQuery<Customer>;
        public partial record ListQuery(int Page, int PageSize) : IQuery<PagedResult<Customer>>;
    }

    public static partial class Commands
    {
        public partial record CreateCommand(string Name, string Email) : ICommand<Customer>;
        public partial record UpdateNameCommand(Guid Id, string NewName) : ICommand;
    }

    public static partial class Events
    {
        public partial record Created(Guid Id, string Name) : IEvent;
        public partial record NameUpdated(Guid Id, string OldName, string NewName) : IEvent;
    }
}
```

> **Warning:** Every record and every enclosing class must be `partial`. The source generator extends them with static call helpers.

### Step 2: Create a Configuration Class

One configuration class per aggregate. This is where all behavioral rules live -- not on the records themselves:

```csharp
using YFex.Cqrs;
using YFex.Cqrs.Configuration;

public class CustomerConfiguration : IAggregateConfiguration<Customer>
{
    public void Configure(AggregateConfigurationBuilder<Customer> b)
    {
        b.Query<Customer.Queries.GetByIdQuery, Customer>()
            .Cacheable(c => c.SlidingExpiration(TimeSpan.FromMinutes(5)))
            .ScopedByUser()
            .RequireAuthenticated();

        b.Query<Customer.Queries.ListQuery, PagedResult<Customer>>()
            .Cacheable()
            .Global()
            .StaleAfter(TimeSpan.FromMinutes(1));

        b.Command<Customer.Commands.CreateCommand, Customer>()
            .Validate<CreateCustomerValidator>()
            .RequireAuthorization("CanCreateCustomers")
            .Invalidates<Customer.Queries.GetByIdQuery, Customer>(
                (q, cmd) => q.Id == cmd.Id)
            .IdempotencyKey(cmd => $"customer:create:{cmd.Email}");

        b.Command<Customer.Commands.UpdateNameCommand>()
            .RequireAuthenticated()
            .Invalidates<Customer.Queries.GetByIdQuery, Customer>(
                (q, cmd) => q.Id == cmd.Id);
    }
}
```

### Step 3: Call via Static Helpers

The source generator emits static methods that route through `YFexDispatcherProvider.Current`:

```csharp
// Query
Result<Customer> result = await Customer.Queries.GetById(customerId);

// Command with result
QueueableResult<Customer> created = await Customer.Commands.Create("Alice", "alice@example.com");

// Void command
QueueableResult updated = await Customer.Commands.UpdateName(id, "Bob");

// Event
await Customer.Events.RaiseCreated(new Customer.Events.Created(id, "Alice"));
```

---

## 5. Deep Dive: Core API

### Query Builder — Full API

```csharp
b.Query<TQuery, TResult>()

    // ── Validation ──────────────────────────────────────────────────────
    .Validate(async (query, ct) => ValidationResult.Success())       // Inline delegate
    .Validate<MyQueryValidator>()                                     // IQueryValidator<TQuery>
    .UseFluentValidator<MyFluentValidator>()                          // FluentValidation integration

    // ── Authorization ───────────────────────────────────────────────────
    .RequireAuthenticated()                                           // Identity must be authenticated
    .RequireRoles("Admin", "Manager")                                 // At least one role must match
    .RequireAuthorization("PolicyName")                               // Named policy (resolved at runtime)
    .Authorize((user, query) => user.HasClaim("scope", "read"))      // Inline predicate
    .Authorize<MyQueryAuthorizer>()                                   // IQueryAuthorizer<TQuery>

    // ── Caching ─────────────────────────────────────────────────────────
    .Cacheable()                                                      // Enable with defaults
    .Cacheable(c => c                                                 // Configure expiration
        .SlidingExpiration(TimeSpan.FromMinutes(5))
        .AbsoluteExpiration(TimeSpan.FromHours(1))
        .StaleAfter(TimeSpan.FromSeconds(30)))
    .NotCacheable()                                                   // Explicitly disable (override baseline)
    .StaleAfter(TimeSpan.FromMinutes(1))                             // Top-level stale threshold

    // ── Cache Scope ─────────────────────────────────────────────────────
    .Global()                                                         // Shared across all users
    .ScopedByUser()                                                   // Keyed by authenticated identity
    .ScopedBySession()                                                // Keyed by browser/app session
    .ScopedByTenant(ctx => ctx.TenantId!)                            // Keyed by tenant from ICacheScopeContext
    .ScopedBy<string>(ctx => ctx.User.FindFirst("org")?.Value ?? "") // Custom key function

    // ── Reverse Invalidation ────────────────────────────────────────────
    .InvalidatedBy<TCommand>((query, cmd) => query.Id == cmd.Id)     // Predicate match
    .InvalidatedBy<TCommand>()                                        // Wildcard: all cached variants
    .InvalidatedBy<IMyGroup>()                                        // Group: expanded at Build() time

    // ── Runtime ─────────────────────────────────────────────────────────
    .Retry(r => r.MaxAttempts(3).Backoff(BackoffStrategy.Exponential))
    .Timeout(TimeSpan.FromSeconds(30))
    .Telemetry(t => t.TraceEnabled().SpanName("GetCustomer"));
```

### Command Builder -- Full API

```csharp
b.Command<TCommand, TResult>()         // or b.Command<TCommand>() for void commands

    // ── Validation (same as queries) ────────────────────────────────────
    .Validate(async (cmd, ct) => ValidationResult.Success())
    .Validate<MyCommandValidator>()
    .UseFluentValidator<MyFluentValidator>()

    // ── Authorization (same as queries) ─────────────────────────────────
    .RequireAuthenticated()
    .RequireRoles("Admin")
    .RequireAuthorization("PolicyName")
    .Authorize((user, cmd) => user.HasClaim("tenant", cmd.TenantId))
    .Authorize<MyCommandAuthorizer>()

    // ── Forward Invalidation ────────────────────────────────────────────
    .Invalidates<TQuery, TResult>((q, cmd) => q.Id == cmd.Id)       // Predicate match
    .Invalidates<TQuery, TResult>()                                   // Wildcard: all cached variants
    .InvalidatesGroup<IMyGroup>()                                     // Group fan-out via IInvalidationGroup

    // ── Idempotency & Conflict Resolution ───────────────────────────────
    .IdempotencyKey(cmd => $"order:create:{cmd.Email}")
    .OnConflict(ConflictPolicy.Escalate)                             // or RetryLater, Discard
    .OnConflict<MyConflictResolver>()                                 // IConflictResolver<TCommand>

    // ── Offline Handling ────────────────────────────────────────────────
    .OnOffline((cmd, ct) => ValueTask.CompletedTask)                 // Inline local-effects handler
    .OnOffline<MyOfflineHandler>()                                    // IOfflineHandler<TCommand>

    // ── Optimistic Updates ──────────────────────────────────────────────
    .Optimistic<TQuery, TQueryResult>(
        match: (result, cmd) => result.Id == cmd.Id,
        apply: (result, cmd) => new TQueryResult(cmd.Id, cmd.NewName)) // Must use ctor, not `with`

    // ── Runtime ─────────────────────────────────────────────────────────
    .Retry(r => r.MaxAttempts(5).Backoff(BackoffStrategy.Linear)
                 .InitialDelay(TimeSpan.FromMilliseconds(200))
                 .MaxDelay(TimeSpan.FromSeconds(10)))
    .Timeout(TimeSpan.FromSeconds(60))
    .Telemetry(t => t.TraceEnabled());
```

> **Warning:** The `apply` expression in `.Optimistic()` must use explicit constructors. C# `with` expressions are not supported in expression trees and will fail at runtime.

### Three-Tier Configuration Override

Configurations compose across assemblies. Server and client overrides replace entries for the same message type:

```csharp
using YFex.Cqrs.Configuration;

// Shared contracts assembly: baseline rules applied everywhere
public class CustomerConfig : IAggregateConfiguration<Customer>
{
    public void Configure(AggregateConfigurationBuilder<Customer> b)
    {
        b.Query<Customer.Queries.GetByIdQuery, Customer>()
            .Cacheable()
            .RequireAuthenticated();
    }
}

// Server project: override with server-specific behavior
public class CustomerServerConfig : IServerAggregateConfiguration<Customer>
{
    public void Configure(AggregateConfigurationBuilder<Customer> b)
    {
        b.Query<Customer.Queries.GetByIdQuery, Customer>()
            .Cacheable(c => c.AbsoluteExpiration(TimeSpan.FromMinutes(10)))
            .RequireAuthorization("ReadCustomers");
        // This replaces the baseline entry for GetByIdQuery
    }
}

// Client project: override with client-specific behavior
public class CustomerClientConfig : IClientAggregateConfiguration<Customer>
{
    public void Configure(AggregateConfigurationBuilder<Customer> b)
    {
        b.Query<Customer.Queries.GetByIdQuery, Customer>()
            .Cacheable(c => c.SlidingExpiration(TimeSpan.FromMinutes(30)))
            .ScopedByUser();
    }
}
```

Merge order: baseline first, then server **or** client overrides replace per-message-type entries. Validators and authorizers from the override replace those from the baseline for the same message type.

### Event Grouping

Group related events for fan-out subscriptions:

```csharp
public class CustomerConfig : IAggregateConfiguration<Customer>
{
    public void Configure(AggregateConfigurationBuilder<Customer> b)
    {
        // Group 1-4 event types under a union marker
        b.Events<Customer.Events.Created, Customer.Events.Updated, Customer.Events.Deleted>()
            .GroupAs<CustomerLifecycle>();
    }
}
```

### Invalidation -- How It Works

Invalidation connects commands to cached queries. It runs in two directions:

1. **Forward**: Command declares `.Invalidates<TQuery>(match)` -- "when I execute, invalidate matching cached queries"
2. **Reverse**: Query declares `.InvalidatedBy<TCommand>(match)` -- "when that command executes, invalidate me"
3. **Group**: `.InvalidatesGroup<IMyGroup>()` -- expands to all `IQuery<T>` types implementing the marker interface
4. **Wildcard**: Omit the match lambda to invalidate **all** cached variants of the target query

Match predicates are real C# lambda expressions -- they have IntelliSense, refactor-rename, and are debug-steppable. They are compiled into delegates at startup via `Expression.Compile()`.

```csharp
b.Command<UpdateEmailCommand>()
    // Forward: "when UpdateEmail runs, invalidate GetById where IDs match"
    .Invalidates<GetByIdQuery, Customer>((q, cmd) => q.Id == cmd.CustomerId)
    // Wildcard: "also invalidate all cached ListQuery results"
    .Invalidates<ListQuery, PagedResult<Customer>>()
    // Group: "also invalidate everything implementing ICustomerReads"
    .InvalidatesGroup<ICustomerReads>();
```

### Optimistic Updates

Apply predicted changes to the client cache immediately, before the server responds:

```csharp
b.Command<MarkAsReadCommand>()
    .Optimistic<GetMessageQuery, Message>(
        match: (msg, cmd) => msg.Id == cmd.MessageId,
        apply: (msg, cmd) => new Message(msg.Id, msg.Content, IsRead: true));
```

If the server rejects the command, the cache entry reverts to its pre-update value.

### Offline Handlers

Run local effects before a command is enqueued to the outbox:

```csharp
using YFex.Cqrs;

b.Command<CreateDraftCommand, Draft>()
    .OnOffline<CreateDraftOfflineHandler>()
    .IdempotencyKey(cmd => $"draft:{cmd.TempId}");

// Handler class
public class CreateDraftOfflineHandler : IOfflineHandler<CreateDraftCommand, Draft>
{
    public async ValueTask HandleAsync(CreateDraftCommand cmd, CancellationToken ct)
    {
        // Save draft to local database for immediate UI display
        await _localDb.SaveDraftAsync(cmd.TempId, cmd.Content, ct);
    }
}
```

---

## 6. Common Patterns & Recipes

### Simple Query with Caching

```csharp
using YFex.Cqrs;
using YFex.Cqrs.Configuration;

public partial class Product
{
    public static partial class Queries
    {
        public partial record GetByIdQuery(Guid Id) : IQuery<Product>, ICacheable;
    }
}

public class ProductConfig : IAggregateConfiguration<Product>
{
    public void Configure(AggregateConfigurationBuilder<Product> b)
    {
        b.Query<Product.Queries.GetByIdQuery, Product>()
            .Cacheable(c => c.SlidingExpiration(TimeSpan.FromMinutes(10)))
            .Global();
    }
}

// Usage
var result = await Product.Queries.GetById(productId);
if (result.IsOk)
    Console.WriteLine(result.OkValue!.Name);
```

### Command with Invalidation Cascade

```csharp
using YFex.Cqrs;
using YFex.Cqrs.Configuration;

public class OrderConfig : IAggregateConfiguration<Order>
{
    public void Configure(AggregateConfigurationBuilder<Order> b)
    {
        b.Command<Order.Commands.CancelCommand>()
            .RequireAuthenticated()
            .Invalidates<Order.Queries.GetByIdQuery, Order>(
                (q, cmd) => q.Id == cmd.OrderId)
            .Invalidates<Order.Queries.ListByCustomerQuery, PagedResult<Order>>(
                (q, cmd) => q.CustomerId == cmd.CustomerId)
            .IdempotencyKey(cmd => $"order:cancel:{cmd.OrderId}");
    }
}
```

### Group Invalidation

```csharp
using YFex.Cqrs;
using YFex.Cqrs.Configuration;

// All product-related queries implement this marker
public interface IProductReads : IInvalidationGroup { }

public partial class Product
{
    public static partial class Queries
    {
        public partial record GetByIdQuery(Guid Id) : IQuery<Product>, IProductReads;
        public partial record SearchQuery(string Term) : IQuery<List<Product>>, IProductReads;
        public partial record CountQuery() : IQuery<int>, IProductReads;
    }
}

public class ProductConfig : IAggregateConfiguration<Product>
{
    public void Configure(AggregateConfigurationBuilder<Product> b)
    {
        b.Command<Product.Commands.ImportBatchCommand>()
            .InvalidatesGroup<IProductReads>(); // Invalidates GetById, Search, and Count at once
    }
}
```

### Optimistic Update

```csharp
using YFex.Cqrs;
using YFex.Cqrs.Configuration;

public class CartConfig : IAggregateConfiguration<Cart>
{
    public void Configure(AggregateConfigurationBuilder<Cart> b)
    {
        b.Command<Cart.Commands.SetQuantityCommand>()
            .Optimistic<Cart.Queries.GetCartQuery, CartView>(
                match: (cart, cmd) => cart.Items.Any(i => i.ProductId == cmd.ProductId),
                apply: (cart, cmd) => new CartView(
                    cart.Id,
                    cart.Items.Select(i => i.ProductId == cmd.ProductId
                        ? new CartItem(i.ProductId, cmd.NewQuantity)
                        : i).ToList()));
    }
}
```

---

## 7. Testing & Mocking

### Test with LocalDispatcher

`LocalDispatcher` resolves handlers from DI, executes the full validation/authorization/cache pipeline, and works without any network:

```csharp
using YFex.Cqrs;
using YFex.Messaging.Rpc;
using Microsoft.Extensions.DependencyInjection;

[Fact]
public async Task GetById_returns_customer()
{
    var services = new ServiceCollection();
    services.AddSingleton<IQueryHandler<Customer.Queries.GetByIdQuery, Customer>,
        GetCustomerHandler>();

    var sp = services.BuildServiceProvider();
    var registry = CompiledMessagingRegistry.Build(
        new IAggregateConfiguration[] { new CustomerConfiguration() });
    var dispatcher = new LocalDispatcher(
        new LocalHandlerInvoker(sp),
        registry,
        AlwaysConnectedNetworkStatus.Instance,
        new InMemoryClientCache(),
        new InMemoryOutbox(),
        new DefaultEventBus(),
        sp);

    YFexDispatcherProvider.Set(dispatcher);

    var result = await Customer.Queries.GetById(Guid.NewGuid());
    Assert.True(result.IsOk);
}
```

### Verify Registry Build

```csharp
[Fact]
public void Registry_compiles_without_errors()
{
    var registry = CompiledMessagingRegistry.Build(
        new IAggregateConfiguration[] { new CustomerConfiguration(), new OrderConfiguration() },
        scanForImplementers: new[] { typeof(CustomerConfiguration).Assembly });

    Assert.True(registry.Queries.Count > 0);
    Assert.True(registry.Commands.Count > 0);
}

[Fact]
public void All_message_types_are_covered()
{
    var registry = CompiledMessagingRegistry.Build(
        new IAggregateConfiguration[] { new CustomerConfiguration() },
        scanForImplementers: new[] { typeof(Customer).Assembly });

    // Throws if any IQuery/ICommand/IEvent in the assembly has no configuration
    registry.Validate(
        ConfigurationValidationLevel.Strict,
        new[] { typeof(Customer).Assembly });
}
```

---

## 8. Troubleshooting & Gotchas

### Duplicate Invalidation (YFINV001)

Declaring both `.Invalidates` on the command and `.InvalidatedBy` on the query for the same pair produces a warning. Pick one direction:

```csharp
// BAD: both directions configured for the same pair
b.Command<CreateCommand, Customer>()
    .Invalidates<GetByIdQuery, Customer>((q, cmd) => q.Id == cmd.Id);
b.Query<GetByIdQuery, Customer>()
    .InvalidatedBy<CreateCommand>((q, cmd) => q.Id == cmd.Id);
// YFINV001: Both Invalidates and InvalidatedBy for the same pair

// GOOD: pick one direction
b.Command<CreateCommand, Customer>()
    .Invalidates<GetByIdQuery, Customer>((q, cmd) => q.Id == cmd.Id);
```

### ICacheable without IQuery

`ICacheable` only makes sense on query records. Applying it to a command or event produces YFCACHE002:

```csharp
// BAD: ICacheable on a command
public partial record CreateCommand(string Name) : ICommand, ICacheable; // YFCACHE002
```

### Expression Tree Limitations

The `apply` lambda in `.Optimistic()` is an `Expression<Func<>>`, not a plain delegate. C# `with` expressions, LINQ method chains, and local function calls are not supported in expression trees:

```csharp
// BAD: `with` expression in expression tree
.Optimistic<GetQuery, Item>(
    match: (item, cmd) => item.Id == cmd.Id,
    apply: (item, cmd) => item with { Name = cmd.NewName }) // Runtime exception

// GOOD: explicit constructor
.Optimistic<GetQuery, Item>(
    match: (item, cmd) => item.Id == cmd.Id,
    apply: (item, cmd) => new Item(cmd.Id, cmd.NewName, item.Price))
```

### YFexDispatcherProvider Not Configured

If static helpers throw `InvalidOperationException("YFex dispatcher is not configured")`, the dispatcher was not set before the first call. Ensure your DI setup calls `AddYFexMessagingRpcClient()` or `UseYFexMessagingRpcServer()`, or set it manually via `YFexDispatcherProvider.Set(dispatcher)`.

### IQueueable on Non-Command Types

`IQueueable` only makes sense on command records. Applying it to a query or event produces YFQUE001.

---

## 9. Reference Summary

### Dispatcher Interface

```csharp
public interface IDispatcher
{
    ValueTask<Result<TResult>> QueryAsync<TQuery, TResult>(TQuery query, CancellationToken ct = default)
        where TQuery : IQuery<TResult>;

    ValueTask<QueueableResult<TResult>> CommandAsync<TCommand, TResult>(TCommand cmd, CancellationToken ct = default)
        where TCommand : ICommand<TResult>;

    ValueTask<QueueableResult> CommandAsync<TCommand>(TCommand cmd, CancellationToken ct = default)
        where TCommand : ICommand;

    ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : IEvent;
}
```

### Runtime Dispatcher Selection

| Host | Dispatcher | Path |
|---|---|---|
| Client (Blazor/MAUI/WPF) | `FusionMessageBus` | Fusion proxy --> WebSocket --> Server |
| Server (Wolverine) | `LocalDispatcher` | Direct to `IHandlerInvoker` |
| Tests / Simple apps | `LocalDispatcher` | DI-resolved handlers |

### Analyzer Diagnostics

| Code | Severity | Description |
|---|---|---|
| YFCACHE001 | Error | `LiveCache.ClientPersistent` on a query not implementing `ICacheable` |
| YFCACHE002 | Error | `ICacheable` on a non-query type |
| YFQUE001 | Error | `IQueueable` on a non-command type |
| YFINV001 | Warning | Both `Invalidates` and `InvalidatedBy` for the same pair |
| YFINV002 | Error | Match predicate on union/group invalidation |
| YFINV003 | Error | Optimistic apply references invalid properties |

### ConflictPolicy Values

| Value | Behavior |
|---|---|
| `Escalate` | Treat the conflict as a sync failure; move to `ISyncFailureLog` |
| `RetryLater` | Re-schedule the command for later replay with exponential backoff |
| `Discard` | Silently discard the conflicting command |

### CacheScope Values

| Value | Key Source |
|---|---|
| `Global` | Shared across all users and sessions |
| `User` | Scoped to the authenticated user identity |
| `Tenant` | Scoped to the current tenant via `ICacheScopeContext.TenantId` |
| `Session` | Scoped to the current browser/app session |
| `Custom` | Custom key derived from `ICacheScopeContext` via `ScopedBy<TKey>()` |

### ErrorType Values

| Value | Meaning |
|---|---|
| `None` | Unspecified |
| `NotFound` | Resource not found |
| `Fail` | General failure |
| `Unauthorized` | Authentication/authorization failure |
| `ValidationProblem` | Input validation failed |
| `Conflict` | Concurrency or idempotency conflict |

### BackoffStrategy Values

| Value | Behavior |
|---|---|
| `Linear` | Fixed delay between retries |
| `Exponential` | Doubling delay between retries |

### All Marker Interfaces

| Interface | Purpose |
|---|---|
| `IQuery<TResult>` | Read operation returning `TResult` |
| `ICommand` | Write operation, no return value |
| `ICommand<TResult>` | Write operation returning `TResult` |
| `IEvent` | Published notification |
| `IQueueable` | Enables offline outbox queuing |
| `ICacheable` | Enables persistent client-side caching |
| `IInvalidationGroup` | Group marker for fan-out cache invalidation |
| `IEventGroup` | Group marker for fan-out event subscriptions |
