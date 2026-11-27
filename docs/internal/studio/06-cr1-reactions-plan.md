# 06 — CR1 Reactions: phase plan

> Part of the [Studio design family](README.md). Status: **plan** (2026-11-27), asked by the user ("Reactions CR1"). Design:
> [04-reactions](04-reactions.md), [01-catalog-and-flows](01-catalog-and-flows.md). Decisions ⚑C51–⚑C55 (README §8), pending; requests to other
> libraries in §4 (**CR1 is blocked on four of them**). Built so far (CP0, all uncommitted on 2026-11-27): record access (`[Readable]`/`[Buildable]`), the Cqrs invoker
> table, `CapabilityUse`, event payloads (`o.Event<T>()`, `EventPayloads`), `CapabilityGate` + `CqrsCapabilityInvoker`, the
> `UserBuilt` table, the `YFex.Studio` skeleton.

## 1. Done when

**CR1a:** a user enacts the reaction "notify me when ACME orders more than 5 000" for a tenant. When `OrderCreated` is
published, the reaction fires **once per order** (not for other customers, not twice on redelivery), runs its step **with
the author's permissions** through `CqrsCapabilityInvoker` (`CapabilityUse.UserBuilt`), the message is sent through
YFex.Notifications, the run appears in the reaction's history, another tenant is unaffected, and a loop is stopped.
The channel is e-mail (or the Notifications testing channel) because WhatsApp providers come with Notifications N1.

**CR1b:** schedules (Jobs), multi-step and waits (Orchestration), quotas, personal-data rules, WhatsApp, the
recent-events test run.

## 2. What exists and what is missing (checked 2026-11-27)

| Needed | State | Evidence |
|---|---|---|
| Event payload read by path, payload `TypeShape` | ✅ built | `YFex.Capabilities.Cqrs/EventPayloads.cs` |
| Run a command by capability key, gated per use | ✅ built | `CqrsCapabilityInvoker`, `CapabilityGate` |
| What users may build with | ✅ built | `CompiledCapabilityRegistry.UserBuilt` / `UserBuiltFor` |
| Expression engine with a runtime schema | ✅ built | `YFex.Expressions/Schema/ExprSchema.cs` |
| **`TypeShape` → `ExprSchema` + a field reader over an event payload** | ❌ | no `TypeShape` use in YFex.Expressions (⚑C53) |
| **Run-as resolution at run time** (author's roles in the reaction's scope, ⚑RA1 directory source) | ❌ | Authorization has `AuthorizationContext.FromSnapshot` + `SnapshotPrincipalAccessor` only; Jobs D2b waits on the same resolver (⚑C51) |
| **Revision store** for reactions (enact, versions, audit diff) | ❌ | no `YFex.Revisions`; Rules.Admin keeps its own (⚑C25) |
| **A `notifications.send` command** in the catalog | ❌ | Notifications N0 has `INotificationSender`, outbox, e-mail; the capability is planned for N5 (⚑C52) |
| Duplicate filter | ✅ built | Messaging `IIdempotencyStore.ClaimAsync(key)` (`Messaging.Rpc/Idempotency.cs`) |
| Typed event subscription | ✅ built, placement open | Messaging `IEventBus.PublishAsync<T>` + per-type handler buckets; Cqrs `IDispatcher.PublishAsync<T>` (⚑C54) |
| Quotas | ❌ | the Resilience quota of ⚑N9 is not built (⚑C30, CR1b) |
| WhatsApp | ❌ | Notifications N1 (Meta/Twilio providers) not built (CR1b) |
| Durable multi-step | ✅ built | `YFex.Orchestration/ProcessManager` (CR1b) |
| Schedules | ✅ built | YFex.Jobs Plan D (CR1b) |

## 3. Steps (CR1a; each step builds)

| Step | What | Where |
|---|---|---|
| R1 | Reaction description types: key, scope, author, trigger (event capability key), condition (`Expr`), steps (capability key + arguments as `ScopeRef`/literal), revision; canonical JSON; check on save (event is `UserBuilt`, steps are `UserBuilt` and invocable, condition binds, arguments match the step's `ParametersSchema`) | `YFex.Studio.Public` |
| R2 | Condition over a payload: `ExprSchema` from the event's `TypeShape`, `IFieldReader` over `EventPayload<T>` (`trigger.Customer.Code`) | per ⚑C53 |
| R3 | Store: reactions enacted per scope (in-memory + one durable store) with revision-shaped contracts | per ⚑C25 |
| R4 | Router: a typed handler per opted-in event forwards to the router; the router finds enacted reactions for (event key, scope chain), claims `(event id, reaction, revision)`, applies the loop guard (origin + depth on the call context, ⚑C12), evaluates the condition | `YFex.Studio.Reactions` (+ hook per ⚑C54) |
| R5 | Steps: each step through `CqrsCapabilityInvoker.InvokeAsync(key, CapabilityUse.UserBuilt, author, …)`; outcomes to the history; `Irreversible` steps need approval at enact (no one to confirm at run time) | `YFex.Studio.Reactions` |
| R6 | Run-as: the author's caller for each run | per ⚑C51 |
| R7 | History query (fired, skipped and why, failed) and a dry run on an edited sample event | `YFex.Studio.Reactions` |
| R8 | Tests (router, condition, once-only, loop, run-as refusal, other tenant), AOT smoke, gates | `tests/` |

## 4. Blocked on other projects (user rule, 2026-11-27)

> The user: when a plan needs a change in another library, explain it, put the request into **that library's own plan**
> (it may already have its own phases), and keep the Studio step **pending, blocked on that project**. Studio does not
> build a stop-gap in its place.

| CR1 step | Needs | Owner and where it is recorded | State |
|---|---|---|---|
| R2 condition over a payload | `ExprSchema.FromShape(TypeShape)` + a reader over `IRecordReader<T>` | YFex.Expressions, [expressions/README](../expressions/README.md) requests (Studio ⚑C53) | ⛔ blocked |
| R3 store | `YFex.Revisions` (extracted from Rules.Admin, C4/C5) | Rules.Admin, [validation/13](../validation/13-phases-and-acceptance.md) "Requests from YFex.Studio"; timing ⚑C25 | ⛔ blocked unless ⚑C25 picks the copy-shaped store |
| R5 notify step | `SendNotification` command + `notifications.send` capability | YFex.Notifications ⚑N31 ([notifications README §5](../notifications/README.md), [06 N5](../notifications/06-phases-and-acceptance.md)) | ⛔ blocked |
| R6 run-as | the run-as resolver (author's roles in the scope, at every run) | YFex.Authorization ⚑RA12 ([10](../authorization-consolidation/10-run-as-context.md) §7, [09 Phase 7](../authorization-consolidation/09-phases-and-acceptance.md)) | ⛔ blocked |
| CR1b quotas | the Resilience quota | YFex.Resilience ([resilience-internals](../resilience-internals.md) "Pending requests"), already asked by Notifications ⚑N9 | ⛔ blocked |
| CR1b WhatsApp | `.Meta` / `.Twilio` providers | YFex.Notifications N1 (planned, needs the Resilience quota first) | ⛔ blocked |
| R1, R4 (router, once-only, loop guard), R5 command steps, R7, R8 | only CP0 pieces (built) and Studio itself | YFex.Studio | can start |

## 5. Studio-own decisions this plan needs

| ⚑ | Topic | Proposal | Alternatives |
|---|---|---|---|
| C25 | `YFex.Revisions` timing (touches Rules.Admin) | extract after CR1a; CR1a uses a store whose contracts copy the planned Revisions shapes (02 §11.1 allows it until a second consumer exists) | extract now, before CR1a |
| C54 | Where the router hooks events | a typed handler per event registered by `o.Event<T>()` on the bus apps publish through (inspect Cqrs `IDispatcher.PublishAsync` vs Messaging `IEventBus`) | a Cqrs pipeline behavior over every event |
| C55 | CR1 split | CR1a as in §3, CR1b with schedules, Orchestration, quotas, privacy, WhatsApp, recent-events test | one phase |

⚑C27 (where reactions run): CR1 runs on central only, as proposed; the site option comes later.
