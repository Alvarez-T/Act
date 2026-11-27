# 01 — Catalog and flows

> Part of the [Studio design family](README.md). Status: **proposal** (2026-11-25). Decisions in README §8 (⚑C10, C12,
> C13) and in YFex.UI [07-logic-flows](../../../../YFex.UI/docs/plans/data-and-logic/07-logic-flows.md) (⚑LB31–LB40).
> The editors (`YConditionBuilder`, `YFlowBuilder`) and the neutral `FlowModel` are designed there; this file holds what
> Studio owns: what the catalog contains, how a flow runs, and where.

## 1. What a user may pick

Everything a user can call, read or react to comes from one catalog, built from developer code and filtered by the
author's permissions and tenant before it reaches any editor (⚑LB32, LB33).

| Kind | Comes from | Example | Status |
|---|---|---|---|
| Command (step) | CQRS message + capability profile audience (⚑C13) | `sales.order.cancel` | capability ✅, audience ➕ |
| Query (step, data source) | same | `sales.customer.find` | same |
| Event (trigger) | Messaging / CQRS events + audience + payload shape (⚑C13) | `sales.order.created` | events listed ✅, audience and shape ➕ |
| Schedule (trigger) | YFex.Jobs (cron in the scope's time zone) | every day at 06:00 | ✅ |
| Page event (trigger, client) | the page's customization profile (03 §2) | `Saved`, `Loaded`, `RowSelected` | ➕ |
| Button click (trigger, client) | a button the user added to a slot (03 §4) | row menu "Send by WhatsApp" | ➕ |
| Send a message (step) | `notifications.send` capability (Notifications N5) | WhatsApp template to a contact | ➕ |
| Job (step) | `YFex.Jobs.Capabilities` | "Rebuild price list" | ✅ |
| Navigate, dialog, toast, set field (steps, client) | NavigatR routes, UI.Abstractions | open the customer | ✅ |
| Rule directive (step in a rule's THEN) | Rules.Dynamic | `Deny`, `Warn` | ✅ |
| Desktop task (step) | an Automation task exposed as a capability (⚑C29) | update the legacy system | ➕ |
| Functions in conditions | Expressions `FunctionCatalog` | `daysBetween`, `sum` | ✅ |

**Opt-in per audience is the security boundary (⚑C13, decided 2026-11-26).** YFex.Capabilities says, for each item,
*who* may use it: **users** building flows, screens and reactions, **AI** agents, and the **search** bar. Something can
be open to users and closed to AI (or the reverse). An item not open to an audience does not exist for it, whatever
the caller's permissions; permissions then still decide inside the audience. (`.AvailableTo(CapabilityUse.X)` with `UserBuilt`, `Ai` and `Search`, plus `AvailableToAll()`: ⚑C34, ⚑C35, decided 2026-11-26.)

| Who sets it | How | Can |
|---|---|---|
| developer | capability profile (code) | open or close per audience; set parameter value policies (below) |
| developer, per scope | existing `CapabilityScope` override descriptors (code) | narrow for one tenant or level |
| tenant admin | run-time setting through the dynamic overlay registry (02 §16) | **narrow only**: close an audience, forbid typed values |

**Parameter value policies (⚑C14, decided 2026-11-26).** A step's parameter takes either a reference (a field, a user, a
role, a list, a contact) or, where allowed, a **typed value** (a phone number, an e-mail, a custom text). The capability
declares per parameter and audience whether a typed value is accepted; a tenant admin can turn it off. The same switch
serves any step, not only messages (a custom text on a task, a typed amount on a command).

```csharp
// capability profile (YFex.Capabilities), next to the message, never inside it — illustrative, nothing here is built
public sealed class SalesCapabilities : ICapabilityProfile<SalesModule>
{
    public void Configure(ICapabilityBuilder<SalesModule> m)
    {
        m.Command<CancelOrder>().SideEffect(SideEffect.Mutating)
            .AvailableTo(CapabilityUse.UserBuilt);                               // users may put it in flows; AI may not
        m.Query<FindCustomer>().AvailableTo(CapabilityUse.UserBuilt | CapabilityUse.Ai | CapabilityUse.Search);
        m.Event<OrderCreated>().AvailableTo(CapabilityUse.UserBuilt | CapabilityUse.Ai);   // trigger; payload through .Readable() / [Readable]
        m.Query<ListProducts>().AvailableToAll();          // every CapabilityUse, including values added later
    }
}

public sealed class NotificationCapabilities : ICapabilityProfile<NotificationsModule>
{
    public void Configure(ICapabilityBuilder<NotificationsModule> m)
        => m.Command<SendNotification>().SideEffect(SideEffect.Mutating).AvailableTo(CapabilityUse.UserBuilt | CapabilityUse.Ai)
            .Parameter(x => x.To,   p => p.TypedValue(CapabilityUse.UserBuilt))     // users may type an address; AI only references
            .Parameter(x => x.Text, p => p.TypedValue(CapabilityUse.UserBuilt));    // custom text, or a template only
}
```

`CapabilityFlags.Hidden` ("not in the search bar") is removed: "search not in the list" says the same (⚑C32, decided 2026-11-26).

## 2. Triggers need a readable payload (⚑C13)

A condition such as `Customer.Code = "ACME" and Total > 5000` binds against the event's fields, and the runtime reads
them by field id. So an event that may trigger a reaction carries a generated shape, the same one validation and
JSON Schema use:

```csharp
[Readable]                                           // read-only: reactions read it, never build it
public sealed partial record OrderCreated(OrderId Id, CustomerRef Customer, Money Total, ActorRef Salesperson) : IEvent;
```

Rejected: binding conditions to raw JSON payloads. Field names would be checked only at run time, renames would break
enacted reactions silently, and amounts would lose their type (Money vs number).

> **Status 2026-11-26 (built, uncommitted, YFex.Capabilities.Cqrs + YFex.Cqrs):** `b.Event<T>().ReadablePayload()` in the
> aggregate configuration declares a readable event (`.Readable()` on the same builder or `[Readable]`) as a trigger (`.AvailableTo(…)` on the same builder): its capability carries `PayloadSchema`, and `EventPayloads` /
> `EventPayload` give its `TypeShape` (to list fields and type-check a condition when it is saved) and read a field of an
> event known only as `object` by path, without reflection (where this table lives: ⚑C41, pending). Opening an event to
> `UserBuilt` without a readable payload fails the build; `Ai` needs none. Not built: a `UserBuilt` table in
> `CompiledCapabilityRegistry` (it projects only the AI and search tables), and the condition check itself (Expressions).

## 3. A flow is a trigger plus steps

The model is YFex.UI's `FlowModel` (⚑LB31): a trigger, an ordered list of steps (call, branch, for-each, stop), outcome
lanes per step and a flow-level `OnError`. Values read the trigger, earlier steps or the model through `ScopeRef`
(⚑LB34). Studio stores its own IR (02 §5, `ActionSet`) and maps it to `FlowModel` without loss.

```
Trigger   ⚡ sales.order.created
When      trigger.Customer.Code = "ACME" and trigger.Total > 5000
Do        1  notifications.send   to = me   channel = WhatsApp   template = "big-order"
                                  args = { customer: trigger.Customer.Name, total: trigger.Total }
```

The same model holds a button's flow (`Confirm → sales.order.cancel → Toast`) and a page event's flow. **A button and
a reaction differ only in their trigger and in where they run.**

## 4. Two runners, one step meaning

| | Screen runner (client) | Server runner |
|---|---|---|
| Triggers | button click, page event, field change | events, schedules |
| Lives in | `RuntimeScreen` (02 §8) and the overlay runtime (03) | `YFex.Studio.Reactions` (⚑C9) |
| Identity | the signed-in user | the author's permissions, real actor `reaction:{key}` (⚑C11, ⚑RA2 pattern) |
| Client-only steps | navigate, dialog, toast, set field, confirm | not offered by the catalog |
| Commands | invoker by key → CQRS stage 1–4 on the server | the same invoker, in process |
| Offline | `Queued` outcome through the Messaging offline queue | ⚑C27 |

Both runners share the step catalog entries, the invoker (C3), the outcome kinds (`Ok`, `Error`, `Queued`, `Denied`,
`NeedsApproval`, ⚑LB38) and the expression evaluator. A step means the same thing wherever it runs.

## 5. Durable server flows (⚑C10, decided 2026-11-26)

| Shape | Runs as | Why |
|---|---|---|
| one step (the common case: notify, call one command) | a Messaging handler for the event, with Messaging's idempotency key (event id, reaction id, revision) | cheapest; at-least-once delivery becomes once per reaction |
| several steps, waits ("if not paid in 2 days, remind") | a YFex.Orchestration process (step ledger, resume after crash, durable waits, compensation) | the ecosystem owner of multi-step runs; no private ledger |
| schedule | the Jobs template `studio.flow.run` (02 §16) starting the same flow | Jobs owns cron, history and retries |

Retries per step come from YFex.Resilience with the step's `RetrySafety` from its side effect: `ReadOnly` retries freely,
`Mutating` only with an idempotency key, `Irreversible` never automatically.

## 6. Locked steps, approvals and test runs

- Studio inserts a locked `Confirm` before `Irreversible` steps in client flows and a locked `Approval` step when a live
  decision needs it (⚑LB39, 02 §8.5). A reaction cannot ask anyone to confirm, so an `Irreversible` step in a reaction
  needs an approval at enact time and is marked as such in the catalog.
- **Test run** (⚑LB40): the runner dry-runs the flow on a sample (queries run, commands are simulated) and returns a
  step trace that the builder shows ✓ / ✗ / skipped. For reactions, the sample can be recent events (04 §7).

## 7. Loop guard (⚑C12, decided 2026-11-26)

A reaction's step can raise an event that fires another reaction. The call context (the same `CallContext` that carries
auth and scope over RPC) gains two values: the **origin reaction id** and the **cascade depth**. A reaction does not fire
on an event its own steps caused unless the author allows it, and nothing fires beyond depth 3. Skips are counted and
shown to the author ("skipped 4 times: loop").
