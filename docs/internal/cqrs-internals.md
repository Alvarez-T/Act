# CQRS Internals

This document covers the internal architecture of `YFex.Cqrs`: the three-phase registry build process, expression compilation strategy, group/union expansion, registration metadata model, runtime policy records, source generator pipelines, and analyzer implementation. Read this when you're modifying the configuration system, the compiled registry, or extending the CQRS source generator.

---

## Registry Build Process

`CompiledMessagingRegistry.Build()` is a three-phase pipeline that transforms fluent configuration declarations into frozen, pre-compiled lookup tables.

### Phase 1: Collect

For each `IAggregateConfiguration<T>` implementation:
1. Create `AggregateConfigurationBuilder<T>`
2. Call `Configure(builder)`
3. Extract `AggregateRegistrations` (queries, commands, events)

Server/client overrides replace baseline entries per message type (last wins).

> **📌 Note:** Uses `MakeGenericMethod` to invoke typed `Configure` methods from the untyped `IEnumerable<IAggregateConfiguration>`. This is the one place where reflection is acceptable — it runs once at startup, not on hot paths.

### Phase 2: Expand Groups

For each invalidation rule where the target is a group:

- **Union types**: Walk the union's case list (positional parameters from the `[Union]` declaration)
- **Marker interfaces**: Scan `scanForImplementers` assemblies for types implementing the marker (or any sub-interface)
- Each implementer becomes a wildcard invalidation entry (no predicate)

> **Why expand at startup?** Expansion is O(N) where N is the number of types in the scanned assemblies. Doing it once at startup amortizes the cost across all runtime dispatches. The alternative — scanning on every invalidation call — would be catastrophically slow.

### Phase 3: Compile

For each `QueryRegistrationMetadata` / `CommandRegistrationMetadata`:

1. **Validation** (see [validation 09](validation/09-cqrs-messaging.md)): shape rules come from `.Validation(...)` / attributes and run
   through `ValidationSlot<T>` (no policy involved); `.Validate(...)` / `.Validate<TRule>()` context rules are `ContextRuleFactory<T>`s,
   compiled into ONE typed `MessageValidator<T>` per message (baseline + overrides, in order). `MessagePipeline` runs the stages.

2. **Authorizers**: Fuse into single `Func<ClaimsPrincipal, object, bool>` predicate. Short-circuit: `RequireAuthenticated` is checked first (cheapest check).

3. **Invalidation predicates**: `Expression<Func<TQuery, TCommand, bool>>` → `.Compile()` → `Func<object, object, bool>` (with type-erased wrapper).

4. **Optimistic apply**: `Expression<Func<TResult, TCommand, TResult>>` → `.Compile()`.

5. Freeze all into `FrozenDictionary<Type, Policy>`.

> **💡 Tip:** The AOT path bypasses `Expression.Compile()` entirely — the CQRS source generator (Pipeline C, gated on `YFEX_RPC`) emits the delegates directly as static methods. On JIT runtimes, `Expression.Compile()` is fine; on NativeAOT, the generator path is required.

> **⚠️ Warning:** `Expression<Func<...>>` does not support `with` expressions (C# record copy-and-modify). Users who write `.Optimistic(match, (cached, cmd) => cached with { IsRead = true })` will get a compile error because `with` doesn't lower to expression trees. They must use explicit construction: `new Message(cached.Id, true, ...)`. Document this limitation.

---

## Runtime Policy Records

After Phase 3, the registry stores pre-compiled policy records:

```csharp
record CommandPolicy(
    Validation?,            // MessageValidator<T>: stage-5 context rules (N factories → 1 typed validator); stage 1 needs no policy
    Authorize?,             // Fused authorizer predicate (N checks → 1 delegate)
    IdempotencyKey?,        // Key factory: command → string
    Conflict,               // ConflictPolicy enum
    ConflictResolverType?,  // Type for DI resolution
    Retry?,                 // RetryPolicy (max attempts, backoff) — legacy, declarative
    Timeout?,               // TimeSpan — consumed by the dispatcher's per-attempt CTS
    OnOfflineHandler?,      // Inline lambda for offline handling
    OnOfflineHandlerType?,  // Type for DI resolution (mutually exclusive with lambda)
    Optimistic?,            // OptimisticPolicy (match + apply delegates)
    InvalidationTargets?,   // InvalidationTarget[] (query types + optional predicates)
    Reverse?,               // ReversePolicy (map/handler + typed dispatch) — read by YFex.Cqrs.Compensation
    OnOfflineHandlerInvoke?,// Typed resolve+invoke for the offline handler (reflection-free)
    Resilience?             // YFex.Resilience.ResilienceSpec — the unified policy spec (see below)
);

record QueryPolicy(
    Validation?,        // MessageValidator<T> (stage-5 context rules)
    Authorize?,         // Fused authorizer
    Cache?,             // CachePolicy (sliding/absolute expiration, stale threshold)
    Scope,              // CacheScope enum (Global, User, Tenant, Session, Custom)
    ScopeKey?,          // Custom scope key factory (for CacheScope.Custom)
    StaleAfter?,        // TimeSpan
    Timeout?,           // TimeSpan
    InvalidatedBy?      // QueryInvalidator[] (command types + optional predicates)
);
```

> **Why `Func<object, ...>` instead of generic delegates?** The registry is type-erased (`FrozenDictionary<Type, Policy>`) for O(1) lookup by message type. Generic delegates would require per-type dictionaries, multiplying the number of frozen collections. The one-time boxing cost of wrapping typed lambdas in `object`-based delegates is negligible compared to the per-call cost of maintaining multiple dispatch tables.

---

## Resilience Wiring

`YFex.Cqrs` references `YFex.Resilience` (design §1 L2). The builder verbs `.Retry` / `.Timeout` / `.UseResource` / `.Fallback` / `.Hedging` are folded at `BuildMetadata` time by `ResilienceMapping.Compose` into a single `ResilienceSpec` stored on `CommandRegistrationMetadata.Resilience`, then compiled onto `CommandPolicy.Resilience`. The legacy `RetryPolicy`/`Timeout` fields are kept alongside for backward compatibility.

- **Name shadowing.** `YFex.Cqrs` has its own `RetryPolicy` and `BackoffStrategy`, which shadow the `YFex.Resilience` ones. `ResilienceMapping` fully-qualifies every resilience type (`YFex.Resilience.RetryPolicy`, etc.) to disambiguate. The legacy `RetryPolicy(MaxAttempts, Backoff, InitialDelay?, MaxDelay?)` maps to `YFex.Resilience.RetryPolicy` with `InitialDelay`/`MaxDelay` defaulted (100ms / 5s) and `Jitter: true`.
- **Execution.** `LocalDispatcher` takes an optional `IResiliencePipelineFactory?`. When it's registered and `policy.Resilience` is non-empty, the online command invoke runs through `factory.Get(spec).ExecuteAsync(...)`; otherwise the legacy per-attempt `Timeout` CTS path applies. This makes resilience opt-in and zero-behaviour-change without a factory. See [Messaging Internals](messaging-internals.md#localdispatcher-pipeline).

> **📌 Note:** `ReverseSpec` (a `readonly record struct`) also lives in `YFex.Cqrs` beside `ReversePolicy`, reused by the command builder, the standalone `IReverseRegistry` (`AddYFexReversals`), and saga steps — see [Orchestration Internals](orchestration-internals.md). `ReverseExecutor.ReverseAsync(cmd, result, in ReverseSpec, dispatcher, ct)` in `YFex.Cqrs.Compensation` reverses from an explicit spec.

---

## Registration Metadata

Mutable metadata collected during the `Configure()` call, before compilation:

| Descriptor | Variants | Purpose |
|---|---|---|
| `ContextRuleFactory` | `ContextRuleFactory<T>` (`Shared` instance, `FromServices<TRule>` = DI or `ActivatorUtilities`) | Stage-5 context rules (FluentValidation lives in `YFex.Cqrs.FluentValidation`) |
| `AuthorizerDescriptor` | `RequireAuthenticated`, `Roles(string[])`, `PolicyName(string)`, `Inline(predicate)`, `TypeRef(Type)` | Records authorization requirements |
| `InvalidationRuleDescriptor` | `TargetType` + optional `MatchExpression` + `IsGroup` flag | Records which queries to invalidate |
| `OptimisticRuleDescriptor` | `QueryType` + `MatchExpression` + `ApplyExpression` | Records optimistic update logic |
| `OfflineHandlerDescriptor` | `Inline(delegate)`, `FromType(Type)` | Records offline handling strategy |

> **📌 Note:** These descriptors are mutable and short-lived — they exist only during the `Build()` call. After compilation, they're consumed and discarded. The runtime uses only the compiled `Policy` records.

---

## Source Generator Pipelines

The CQRS generator (`CQRSGenerator : IIncrementalGenerator`) runs three independent pipelines:

### Pipeline A: Static Call Helpers

```
ForAttributeWithMetadataName → detect nested records implementing IQuery<>/ICommand/IEvent
  → ClassToGenerate model (namespace, className, queries[], commands[], events[])
  → CodeBuilder.GenerateSource()
```

**Naming convention:**
- `GetCustomerByIdQuery` → method `GetCustomerById` (strips `Query` suffix)
- `CreateCustomerCommand` → method `CreateCustomer` (strips `Command` suffix)
- Events → `Raise{EventName}` (prefixes `Raise`)

Method parameters = record's positional constructor parameters + `CancellationToken ct = default`.

> **💡 Tip:** If the user already declared a method with the same generated name, the generator skips emission silently. This lets users override the generated helper without conflicts.

### Pipeline B: Configuration Registration

```
Scan for non-abstract classes implementing IAggregateConfiguration<> / IServer.../IClient...
  → ConfigRegistration model (FQN, interface list)
  → CodeBuilder.GenerateConfigRegistration()
  → Emits: AddYFexConfigurations() extension method
```

> **Why generate registration?** This avoids `Assembly.GetTypes()` reflection at startup, making the registration AOT-friendly and trimming-safe.

### Pipeline C: RPC Contracts (YFEX_RPC)

Gated on the `YFEX_RPC` preprocessor symbol (injected by `YFex.Messaging.Rpc.targets` MSBuild import):

```
Same ClassToGenerate model
  → RpcContractCodeBuilder.GenerateInterface()      → I{Aggregate}RpcContract : IComputeService (+ I{Aggregate}RpcClient view)
  → RpcContractCodeBuilder.GenerateServerImpl()      → {Aggregate}RpcContractImpl
  → RpcContractCodeBuilder.GenerateRegistrations()   → fusion.AddServer<>() / rpc.AddClient<IClient>(nameof(IContract)) / FusionMessageBusBuilder
  → RpcProxyCodeBuilder.Generate*Proxy()             → {Ns}.ActualLabProxies.{Type}Proxy for the contract, the client view and the impl
```

Server impl wraps each method with `IHandlerInvoker.InvokeAsync<T>()` and checks `Invalidation.IsActive` for Fusion cache invalidation. Record types are fully qualified; `YFex.Cqrs.ErrorResultException` (server validation rejections, Wolverine's
`ResultConversionException`) is caught for commands and queries and returned as the typed error. A `{Aggregate}RpcFormatters`
module initializer registers the MemoryPack formatters of every result type (AOT). `RegisterRoutes` uses the typed-result routes.

Query methods: `Task<Result<T>> Name(TQuery q, string? variant = null, CancellationToken ct = default)` — `variant` is filled by
the server's `CallContextMiddleware` (per-tenant/user compute-cache key, validation D29); routes pass null. Command methods are
regular RPC methods — NOT CommandR `[CommandHandler]`s (CommandR rejects arguments that aren't ActualLab `ICommand<T>`); the impl
invalidates with `Invalidation.Begin()` + `OnInvalidate(cmd)` after success.

> **⚠️ Fusion 13 proxies (validation D30):** ActualLab.Generators is syntax-only and cannot see generated types, and Fusion finds
> proxies only by convention (`{Namespace}.ActualLabProxies.{Name}Proxy`, no runtime fallback). So `RpcProxyCodeBuilder` emits them
> with ActualLab 13.0.12's exact template: `I{Aggregate}RpcContractProxy`, `I{Aggregate}RpcClientProxy` (interface proxies) and
> `{Aggregate}RpcContractImplProxy` (class proxy). The model carries result types in ActualLab's display formats
> (`ProxyReturnType`/`KeepReturnType`). `YFex.Messaging.Rpc.Tests/ProxyGoldenTests` runs the real ActualLab generator on a source
> copy of the generated contract and must stay green — re-run it on every Fusion upgrade.

> **📌 Helper names:** a record without the `Query`/`Command` suffix gets a `{Name}Async` helper (its own name would clash).

> **⚠️ Warning:** The RPC pipeline depends on ActualLab.Fusion types (`IComputeService`, `[ComputeMethod]`, `[CommandHandler]`). If the Fusion package isn't referenced in the consumer project, the gating on `YFEX_RPC` prevents the pipeline from emitting — but if someone manually defines the symbol without the package, they'll get compile errors in the generated code.

---

## Analyzer Implementation

All analyzers use `RegisterSymbolAction(SymbolKind.NamedType)`:

| Analyzer | Diagnostics | Strategy |
|---|---|---|
| `CacheAnalyzer` | YFCACHE001, YFCACHE002 | Walks base type list for `ICacheable` / `IQuery<>` presence mismatch |
| `QueueAnalyzer` | YFQUE001 | Walks base type list for `IQueueable` / `ICommand` presence mismatch |
| `InvalidationAnalyzer` | YFINV001, YFINV002, YFINV003 | Requires semantic model inspection of configuration builder calls |

> **📌 Note:** YFINV001 (duplicate invalidation declaration) is a cross-file check — both `.Invalidates<TQuery>()` on a command's configuration and `.InvalidatedBy<TCommand>()` on the query's configuration must be detected. The analyzer can only flag this at edit time when both files are in the same compilation. At startup, `CompiledMessagingRegistry.Build()` performs the definitive check and throws if `ConfigurationValidationLevel.Strict`.

---

## EquatableArray\<T\>

`IEquatable<EquatableArray<T>>` wrapper around `T[]` providing structural equality for incremental generator caching.

> **Why is this necessary?** Without it, the generator would re-emit on every keystroke. Arrays use reference equality — two `new[] { a, b }` are never equal even if contents match. `EquatableArray<T>` wraps the array with element-wise comparison, so the incremental pipeline correctly determines "nothing changed" and skips re-emission. Every generator in the solution defines its own copy because `netstandard2.0` analyzer projects can't share code.
