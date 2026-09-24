# YFex.Resilience

**Libraries:** `YFex.Resilience`, `YFex.Resilience.Polly`

---

## 1. Overview

`YFex.Resilience` is one declarative **policy vocabulary** — retry, timeout, circuit breaker, rate limiter, bulkhead, fallback, and hedging — usable from CQRS commands, saga steps, and message handlers. You describe *what* protection an operation needs with a small value type (`ResilienceSpec`); the execution engine lives behind a seam (`IResiliencePipelineFactory`) so the actual runtime — Polly v8 — is an internal detail that **never appears on a YFex public surface**.

The abstractions library is AOT-first: `<IsAotCompatible>true>`, zero runtime reflection, no `Expression.Compile`, `MakeGenericType`, or `Activator`. Everything is plain `Func<>` delegates and `FrozenDictionary` lookups.

| Package | Role |
|---|---|
| `YFex.Resilience` | Abstractions only: policy structs, `ResilienceSpec` overlay, the factory/pipeline seam, named-resource registry, DI. **No Polly.** |
| `YFex.Resilience.Polly` | The `IResiliencePipelineFactory` implementation over Polly v8. Polly types stay inside this package. |

---

## 2. Core Concepts & Mental Model

### Two policy scopes

Policies fall into two groups, and the distinction drives the whole API:

| Scope | Policies | Declared | State |
|---|---|---|---|
| **Per-operation** | Retry, Timeout, Fallback, Hedging | inline on the command/step | stateless |
| **Per-resource** | Circuit breaker, Rate limiter, Bulkhead | a named resource, referenced via `.UseResource("name")` | **shared** across all callers |

A retry is meaningful per call. A circuit breaker is only meaningful when its state (open/closed) is *shared* by every caller hitting the same downstream — one `"sefaz"` breaker protects SEFAZ for the whole process, not per call site. So stateful policies are declared **once** against a named resource, and operations reference them by name.

### ResilienceSpec — the overlay value type

A `ResilienceSpec` is a `readonly record struct` where every per-operation knob is nullable, so "unset" falls through:

```csharp
public readonly record struct ResilienceSpec(
    RetryPolicy? Retry = null, TimeoutPolicy? Timeout = null,
    FallbackPolicy? Fallback = null, HedgingPolicy? Hedging = null,
    string? ResourceName = null);
```

`Overlay(in ResilienceSpec over)` merges two specs per-knob — the overlaid value wins, unset knobs inherit. This is how a saga step overrides a command's policy without repeating it (see [Orchestration](orchestration.md)) and how CQRS folds its `.Retry`/`.Timeout` verbs into one spec (see [CQRS](cqrs.md)).

### The execution seam

```csharp
public interface IResiliencePipelineFactory { IResiliencePipeline Get(in ResilienceSpec spec); }
public interface IResiliencePipeline { ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> op, CancellationToken ct); }
```

`Get` returns a cached pipeline per `(spec, resource)`; you wrap your operation in `ExecuteAsync`. If you never register a Polly factory, the seam is simply absent and callers fall back to their non-resilient path.

---

## 3. Integration Model & Lifecycle

### Registration

```csharp
services.AddYFexResilience(r =>
{
    // Stateful, shared policies live on a named resource.
    r.Resource("sefaz")
     .CircuitBreaker(failureRatio: 0.5, samplingDuration: TimeSpan.FromSeconds(30),
                     minThroughput: 20, breakDuration: TimeSpan.FromSeconds(60))
     .RateLimiter(permits: 5, per: TimeSpan.FromSeconds(1), queueLimit: 10)
     .Bulkhead(maxConcurrent: 10, queueLimit: 20);

    // A typed fallback returns a handle (token) referenced by FallbackPolicy — no boxing.
    int cachedFallback = r.Fallback<Invoice>(_ => new ValueTask<Invoice>(Invoice.Empty));
});

services.AddYFexResiliencePolly();   // provides IResiliencePipelineFactory over Polly v8
```

Named resources compile to a `FrozenDictionary<string, ResourcePolicies>`; the stateful strategies for each resource are built **once** and shared, so their circuit/limiter state is genuinely process-wide.

### Lifecycle

- The registry (resources + fallback table) is frozen at DI build.
- Pipelines are built lazily on first `Get(spec)` and cached; the same spec returns the same instance.
- Stateful per-resource strategies are singletons — a breaker tripped through one operation fails fast for every other operation sharing that resource.

---

## 4. Step-by-Step Usage

### Directly, via the factory

```csharp
var factory = provider.GetRequiredService<IResiliencePipelineFactory>();

// Retry up to 3 attempts with exponential backoff + jitter.
var spec = new ResilienceSpec(
    Retry: new RetryPolicy(MaxAttempts: 3, BackoffStrategy.Exponential,
        InitialDelay: TimeSpan.FromMilliseconds(100), MaxDelay: TimeSpan.FromSeconds(5), Jitter: true),
    Timeout: new TimeoutPolicy(TimeSpan.FromSeconds(8)));

var pipeline = factory.Get(spec);
var result = await pipeline.ExecuteAsync(
    ct => new ValueTask<Invoice>(FetchInvoiceAsync(id, ct)), cancellationToken);
```

### Through a named resource

```csharp
var spec = new ResilienceSpec(ResourceName: "sefaz");   // breaker + limiter + bulkhead
await factory.Get(spec).ExecuteAsync(ct => SubmitToSefazAsync(nfe, ct), cancellationToken);
```

### The usual way — declaratively

You rarely call the factory yourself. You declare policy on a **CQRS command** or a **saga step**, and the dispatcher / orchestrator executes through the pipeline for you:

```csharp
// CQRS command builder (see the CQRS guide)
b.Command<SubmitNfeCommand>()
 .UseResource("sefaz")
 .Retry(r => r.MaxAttempts(3))
 .Timeout(TimeSpan.FromSeconds(8));
```

---

## 5. Deep Dive: Core API

### Policy value types

All `readonly record struct` — zero heap allocation:

```csharp
public enum BackoffStrategy : byte { Constant, Linear, Exponential }

public readonly record struct RetryPolicy(int MaxAttempts, BackoffStrategy Backoff,
    TimeSpan InitialDelay, TimeSpan MaxDelay, bool Jitter);
public readonly record struct TimeoutPolicy(TimeSpan Timeout);
public readonly record struct HedgingPolicy(int MaxAttempts, TimeSpan Delay);
public readonly record struct FallbackPolicy(int Token);                 // handle into the typed fallback table
public readonly record struct CircuitBreakerPolicy(double FailureRatio, TimeSpan SamplingDuration,
    int MinThroughput, TimeSpan BreakDuration);
public readonly record struct RateLimiterPolicy(int Permits, TimeSpan Window, int QueueLimit);
public readonly record struct BulkheadPolicy(int MaxConcurrent, int QueueLimit);
```

### The six/seven policies

- **Retry** — re-attempt transient failures; constant/linear/exponential backoff + optional jitter; caps attempts and total delay. `MaxAttempts` is the **total** attempt count (initial + retries). Per-operation.
- **Timeout** — cap a single attempt via a cooperative `CancellationToken`. Per-operation.
- **Circuit breaker** — after `FailureRatio` over `SamplingDuration` (with `MinThroughput`), open for `BreakDuration` and fail fast; a half-open probe closes it. **Per-resource.**
- **Rate limiter** — cap throughput (token/window) to a downstream; optional queue. **Per-resource.**
- **Bulkhead** — cap concurrent in-flight calls to protect a slow dependency; optional queue. **Per-resource.**
- **Fallback** — substitute a degraded result on failure (cached / last-known / contingency). The delegate is registered in a typed table and referenced by a `FallbackPolicy` token — no boxing of the result. Per-operation.
- **Hedging** — fire parallel attempts after a delay; take the first success (latency-sensitive reads). Per-operation.

### Composition order

When a spec combines policies, the pipeline composes them outermost → innermost:

```
rate limiter → bulkhead → circuit breaker → timeout → retry → hedging → fallback
```

### `AddYFexResilience` builder

| Method | Purpose |
|---|---|
| `r.Resource(name)` | Begins a named, stateful resource. |
| `.CircuitBreaker(failureRatio, samplingDuration, minThroughput, breakDuration)` | Adds a breaker to the resource. |
| `.RateLimiter(permits, per, queueLimit)` | Adds a rate limiter. |
| `.Bulkhead(maxConcurrent, queueLimit)` | Adds a concurrency limiter. |
| `r.Fallback<T>(Func<CancellationToken, ValueTask<T>>)` | Registers a typed fallback; returns the `int` token for `FallbackPolicy`. |

---

## 6. Common Patterns & Recipes

### Protect a flaky third-party call

```csharp
services.AddYFexResilience(r =>
    r.Resource("payments").CircuitBreaker(0.5, TimeSpan.FromSeconds(30), 20, TimeSpan.FromMinutes(1)));

// per operation, layer a retry + timeout on top of the shared breaker
var spec = new ResilienceSpec(
    Retry: new RetryPolicy(2, BackoffStrategy.Exponential, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(3), true),
    Timeout: new TimeoutPolicy(TimeSpan.FromSeconds(10)),
    ResourceName: "payments");
```

### Degrade gracefully with a fallback

```csharp
int lastKnown = -1;
services.AddYFexResilience(r => lastKnown = r.Fallback<int>(_ => new ValueTask<int>(ReadLastKnownRate())));
// ...
var spec = new ResilienceSpec(Fallback: new FallbackPolicy(lastKnown));
```

### Overlay a step policy onto a command policy

```csharp
var effective = commandSpec.Overlay(stepSpec);   // step knobs win; unset inherit from the command
```

---

## 7. Testing & Mocking

`AddYFexResilience` + `AddYFexResiliencePolly` are enough for real behavior in tests:

```csharp
var services = new ServiceCollection();
services.AddYFexResilience(r => r.Resource("x").CircuitBreaker(0.5, TimeSpan.FromSeconds(1), 2, TimeSpan.FromSeconds(1)));
services.AddYFexResiliencePolly();
var factory = services.BuildServiceProvider().GetRequiredService<IResiliencePipelineFactory>();

var attempts = 0;
var result = await factory.Get(new ResilienceSpec(Retry: new RetryPolicy(3, BackoffStrategy.Constant,
    TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), false)))
    .ExecuteAsync(_ => { attempts++; if (attempts < 3) throw new InvalidOperationException(); return new ValueTask<int>(attempts); }, default);
// attempts == 3
```

To assert pipeline reuse: `ReferenceEquals(factory.Get(spec), factory.Get(spec))` is `true`.

---

## 8. Troubleshooting & Gotchas

- **`IResiliencePipelineFactory` not registered.** You called `AddYFexResilience` but not `AddYFexResiliencePolly`. The abstractions package has no engine; the Polly package supplies it.
- **`MaxAttempts` semantics.** It's the *total* number of attempts, not the number of retries. `MaxAttempts: 3` = one initial + two retries. `MaxAttempts <= 1` means "no retry strategy" and is skipped.
- **Circuit-breaker state feels global.** It is — that's the point. Every operation with the same `ResourceName` shares one breaker instance. A tripped breaker fails fast for all of them until `BreakDuration` elapses.
- **A fallback token from a different registration.** `FallbackPolicy(token)` is only valid for the registry that produced the token. Capture the return value of `r.Fallback<T>(...)` at registration.

---

## 9. Reference Summary

### DI entry points

| Call | Package | Effect |
|---|---|---|
| `AddYFexResilience(r => ...)` | `YFex.Resilience` | Registers named resources + fallback table (frozen). |
| `AddYFexResiliencePolly()` | `YFex.Resilience.Polly` | Registers the Polly-backed `IResiliencePipelineFactory`. |

### `BackoffStrategy` values

`Constant` · `Linear` · `Exponential`

### Seam

```csharp
IResiliencePipeline Get(in ResilienceSpec spec);              // IResiliencePipelineFactory
ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> op, CancellationToken ct); // IResiliencePipeline
```

For how commands and saga steps declare policy, see [CQRS](cqrs.md) and [Orchestration](orchestration.md). For internals (Polly composition, the fallback table, caching), see [Resilience Internals](../internal/resilience-internals.md).
