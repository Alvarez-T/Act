# Resilience Internals

This document covers the internal architecture of `YFex.Resilience` and `YFex.Resilience.Polly`: the overlay value type, the frozen resource/fallback registries, the pipeline factory and its cache, the Polly v8 composition, and the two deliberate deviations from a naive Polly mapping. Read this when modifying the policy vocabulary, the Polly translation, or the caching strategy. For the public API, see [Resilience](../public/resilience.md).

---

## Why a seam at all

The abstractions library (`YFex.Resilience`) sets `<IsAotCompatible>true>` and contains **no Polly reference**. All execution goes through `IResiliencePipelineFactory` / `IResiliencePipeline`, implemented in `YFex.Resilience.Polly`. This keeps Polly types off every public YFex surface and lets consumers that don't need resilience avoid the Polly dependency entirely.

> **Why not just expose Polly?** Two reasons. (1) Polly v8 types (`ResiliencePipeline`, `ResiliencePipelineBuilder`) would leak into CQRS/orchestration public APIs, coupling the whole framework to a Polly major version. (2) A `ResilienceSpec` is a `readonly record struct` that can be stored on a `CommandPolicy` / `StepDefinition`, overlaid, and cached by value — a Polly pipeline can't.

---

## ResilienceSpec & Overlay

`ResilienceSpec` carries only the **per-operation** knobs (retry, timeout, fallback, hedging) plus a `ResourceName` string. The stateful per-resource policies (breaker/limiter/bulkhead) are *not* on the spec — they live in the registry keyed by name.

```csharp
public ResilienceSpec Overlay(in ResilienceSpec over) => new(
    over.Retry ?? Retry, over.Timeout ?? Timeout,
    over.Fallback ?? Fallback, over.Hedging ?? Hedging, over.ResourceName ?? ResourceName);
```

`Overlay` is per-knob last-wins with null fall-through — the mechanism behind saga step overrides (R2) and CQRS's `ResilienceMapping.Compose`. `IsEmpty` (all knobs null + no resource) lets callers skip the pipeline entirely.

> **📌 Note:** Because the stateful policies aren't on the spec, `ResilienceSpec` equality is cheap and total — which is exactly what makes it a safe `FrozenDictionary` / cache key (see below).

---

## The registry

`AddYFexResilience(r => ...)` builds two frozen structures:

| Structure | Contents |
|---|---|
| `FrozenDictionary<string, ResourcePolicies>` | Per named resource: its `CircuitBreakerPolicy?`, `RateLimiterPolicy?`, `BulkheadPolicy?`. |
| Typed fallback table | `r.Fallback<T>(...)` appends a delegate and returns its `int` index; `FallbackPolicy(token)` references it. |

`ResourcePolicies` is the "equivalent frozen resource descriptor" the design sanctions, since `ResilienceSpec` per §3.2 can't carry the stateful knobs.

> **Why a token instead of the delegate on the spec?** The fallback delegate is generic in `T` (`Func<CancellationToken, ValueTask<T>>`). Putting it on the non-generic `ResilienceSpec` would box or require a per-`T` spec. An `int` token into a typed table keeps the spec non-generic and the result path boxing-free — the table casts the stored delegate reference back to `Func<…ValueTask<T>>` at execution.

---

## PollyResiliencePipelineFactory

`Get(in ResilienceSpec)` caches one `PollyResiliencePipeline` per spec (a `ConcurrentDictionary<ResilienceSpec, …>`), so the same spec returns the same instance — verified by `ReferenceEquals`. The **stateful per-resource** Polly pipeline (breaker + limiter + bulkhead) is built once per resource name (a second `ConcurrentDictionary<string, ResiliencePipeline>`) and shared, so its circuit/limiter state is process-wide.

### Composition order

Per Polly guidance, strategies compose outermost → innermost:

```
rate limiter → bulkhead → circuit breaker → timeout → retry → hedging → fallback
```

The per-resource pipeline (rate limiter → bulkhead → CB) executes as the **outer** pipeline wrapping the per-spec pipeline (timeout → retry). This is why a breaker tripped through one spec fails fast through another spec that names the same resource — the outer resource pipeline instance is shared.

### Retry mapping

`RetryPolicy.MaxAttempts` is the **total** attempt count → Polly's `MaxRetryAttempts = MaxAttempts - 1`. A `MaxAttempts <= 1` (or a non-positive timeout) means "no strategy" and the segment is skipped — this avoids Polly's `MaxRetryAttempts >= 1` validation throw. `BackoffStrategy` maps `Constant → Constant`, `Linear → Linear`, `Exponential → Exponential`; `Jitter` toggles Polly's `UseJitter`.

---

## Two deviations from a naive Polly mapping

> **⚠️ Deviation 1 — hedging & fallback are `Func<>` wrappers, not Polly strategies.** Polly v8's non-generic `ResiliencePipeline` cannot host hedging or fallback (those are generic-`<T>`-only strategies). Building a per-`T` generic pipeline would break the single shared/cached non-generic pipeline and the per-resource state sharing. So hedging and fallback are implemented as plain `Func<>` wrappers **inside** `ExecuteAsync<T>`, applied innermost — preserving the documented compose order and staying AOT-clean. Polly still owns retry / timeout / circuit-breaker / rate-limiter / bulkhead.

> **⚠️ Deviation 2 — resource sharing via nested execution, not `AddPipeline`.** The per-resource stateful pipeline runs as the outer Polly pipeline wrapping the per-spec timeout+retry pipeline, rather than composing both into one builder. This keeps the resource pipeline instance cached per name (genuinely shared state) while the per-spec part stays per-spec.

---

## AOT verification

`YFex.Resilience.AotSmoke` sets `<IsAotCompatible>true>` + `<TreatWarningsAsErrors>true>` and exercises every policy through the Polly factory. It builds with **0 IL2xxx/IL3xxx** because the whole path is plain `Func<>` delegates, `FrozenDictionary` lookups, and closed-generic instantiation — no `Expression.Compile`, `MakeGenericMethod`, or `Activator`. Treat a new IL warning there as a regression.

---

## Package boundary

`YFex.Resilience.Polly` references `Polly.Core` **and** `Polly.RateLimiting` (the rate limiter and concurrency limiter aren't in `Polly.Core`). No Polly package is referenced anywhere else in the framework — CQRS and orchestration depend only on `YFex.Resilience` abstractions.

## Stored retries (F04, decided and built 2026-11-05)

Retries that are stored and scheduled for a later time (Jobs runs, Jobs Hub reconnect, the Notifications dispatcher) can't run inside a Polly pipeline. They use the same vocabulary with a Polly-free calculator. Decisions:

1. **Calculator in `YFex.Resilience`:** `RetryDelay.For(in RetryPolicy, attempt, Random?)` returns the delay after attempt N (1-based) failed; `RetryDelay.TryGetNext(in policy, RetryBudget?, attempt, elapsed, out delay, Random?)` adds the `MaxAttempts` and budget checks. Pure, allocation-free; jitter is applied before the `MaxDelay` cap, so a delay never exceeds `MaxDelay`. Jobs `RetryCalculator`, Hub `ReconnectBackoff` and Notifications call it; none keeps its own backoff code.
2. **`RetryBudget(MaxTotalElapsed)`** is a separate type for the total-time cap. It is not added to `RetryPolicy`, because `RetryPolicy` is part of the `ResilienceSpec` pipeline cache key and Polly has no use for it. `Jitter: true` means a fixed ±20% in the calculator; there is no jitter-fraction setting.
3. **No custom multiplier:** `BackoffStrategy` Constant / Linear / Exponential (×2), as Polly does. `[Retry(BackoffMultiplier = …)]` and `JitterFraction` go away.
4. **Clean break in Jobs:** the Jobs `RetryPolicy` class is replaced by `YFex.Resilience.RetryPolicy` + `RetryBudget` (settings layers, `JobDescriptor`, `IJobStore`, `[Retry]` source generator). Stored retry records in the old `StoredRetry` shape are not read; the job's default applies.

Jobs references `YFex.Resilience` (base package only, a leaf with just DI abstractions, so there is no cycle; never `YFex.Resilience.Polly`).


## Pending requests from other designs (2026-11-27)

| Request | From | State | Blocks |
|---|---|---|---|
| **Quota**: a counter per (quota key, Tenancy scope, period) on `ITrustedClock` in the scope's time zone, refusing when exhausted, with a durable shared-store adapter so restarts and several central nodes count once | Notifications ⚑N9 (agreed 2026-11-09); also YFex.Studio ⚑C30 (reaction runs and messages per scope per day) | not designed here, not built | Notifications N1 (WhatsApp/SMS budgets), Studio CR1b |
