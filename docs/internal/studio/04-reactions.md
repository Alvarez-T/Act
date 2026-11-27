# 04 — Reactions: when something happens, do something

> Part of the [Studio design family](README.md). Status: **proposal** (2026-11-25). Decisions ⚑C8–⚑C14 **decided by
> the user on 2026-11-26**; ⚑C27, C29–C31 pending (README §8). Replaces the one-row "P6 Automations" of [02 §15](02-new-screens.md).
> Origin: the user, 2026-11-25: "notify me on WhatsApp when ACME orders more than 5k" is exactly the target.

## 1. What a reaction is (⚑C8, decided 2026-11-26)

A **reaction** is a flow (01 §3) with an event or schedule trigger, a condition, and steps, enacted for a scope and run
on the server. It needs no screen renderer, so it can come before composed screens (⚑C25).

Name: "Reactions", because "Automations" already means desktop automation (YFex.Automation) and "Workflows" sounds like
Orchestration's processes (⚑C8).

## 2. The worked example

The user says or builds: *Notify me on WhatsApp when ACME orders more than 5k.*

```
Trigger  ⚡ Order created                                        (sales.order.created, ⚑C13)
When     Customer is ACME  and  Total greater than R$ 5.000,00   (YConditionBuilder, badges look)
Do       Send message  to: me  by: WhatsApp  template: Big order (customer, total, salesperson)
Scope    ACME Group (tenant)     Runs as: Maria (her permissions)  Limit: 50 messages a day
```

Stored (canonical JSON, keys illustrative):

```json
{ "reaction": "big-acme-orders", "scope": "tenant:acme-group", "author": "user:maria",
  "trigger": { "event": "sales.order.created" },
  "when": "trigger.Customer.Code = \"ACME\" and trigger.Total > 5000",
  "steps": [ { "call": "notifications.send",
               "args": { "to": "author", "channel": "WhatsApp", "template": "big-order",
                         "values": { "customer": "trigger.Customer.Name", "total": "trigger.Total",
                                     "salesperson": "trigger.Salesperson.Name" } } } ] }
```

What happens when an order is created:

```
CreateOrder handler ── OrderCreated ──► Messaging outbox ──► reaction router (index: event key + scope chain)
                                                               │  idempotency key (event id, reaction id, revision)
                                                               │  loop guard (01 §7), quota (⚑C30)
                                                               ▼
                                          condition (Expressions over OrderCreated's [Readable] reader)
                                                               │ true
                                                               ▼
                                          notifications.send via the invoker (as Maria, real actor reaction:big-acme-orders, ⚑C11)
                                                               ▼
     Notifications: template → contact (Maria's verified WhatsApp) → quiet hours → consent → tenant's provider (Meta/Twilio)
```

The author sees each run in the reaction's history: fired, skipped (condition false, quiet hours, quota, loop), failed.

## 3. Variants

| User wants | Trigger | Condition | Steps |
|---|---|---|---|
| Only for ACME and only big orders | order created | `Customer.Code = "ACME" and Total > 5000` | send to me |
| Tell the order's salesperson | order created | `Total > 5000` | send to `trigger.Salesperson` (a field, ⚑C14) |
| Tell every manager of the store | order created | — | send to role `Store manager` in the order's store |
| A summary instead of one message each | schedule 08:00 | `count > 0` | query yesterday's orders → one message with count and total |
| Stock below minimum | stock changed | `Qty < Product.MinQty and Previous.Qty >= Product.MinQty` (crossing only) | create purchase suggestion → tell the buyer |
| Invoice rejected by the tax authority | invoice rejected | `Reason in (…)` | tell the accountant; create a task |
| Remind a customer 3 days before due | schedule daily | query due in 3 days | for each: WhatsApp the customer (transactional) |
| Not paid 2 days after reminder | invoice reminded | — | wait 2 days → if still open: tell collections (Orchestration, ⚑C10) |
| Update a legacy system without an API | order approved | — | run the Automation task "Register in legacy" (⚑C29, needs approval at enact) |

## 4. Recipients (⚑C14)

| Recipient | Meaning | Resolved by |
|---|---|---|
| me / the author | the user who enacted the reaction | Actors + Authentication verified contacts |
| a user | a picked user | same |
| a role in a scope | every user with the role in the event's scope (store of the order) | Authorization + Tenancy |
| a field | an actor or customer referenced by the payload (`Order.Salesperson`, `Order.Customer`) | Actors, or the app's contact source |
| a contact | an entry of the Notifications contact book (on-call phone, partner e-mail, Telegram chat) | Notifications |
| a recipient list | a list stored per scope ("store managers of site 7") | Notifications |
| **a typed address** | a phone or e-mail written into the reaction, **only where allowed** (below) | Notifications `Recipient.To(destination)` |

These map one to one onto Notifications' existing `Recipient` (`Actor`, `Reference`, `List`, `Contact`, `To`).

**Typed values are allowed only when configured (⚑C14, decided 2026-11-26).** Free values are not only addresses: a custom
message text instead of a template, a typed note on a task. The `notifications.send` capability declares per parameter and
audience whether a typed value is accepted (01 §1); a tenant admin can turn it off. References are preferred because
Notifications resolves them at send time (changed phone, opt-out, quiet hours). A typed address still passes Notifications'
opt-out list, consent for marketing and quiet hours; a typed text on WhatsApp is only sent inside the 24-hour session
window, otherwise the send is refused and a template is needed. Typed addresses are personal data stored in the reaction
revision, so Privacy classifies them and erasure finds them (⚑C31).

## 5. Who it runs as (⚑C11, decided 2026-11-26)

A reaction uses the run-as pattern Authorization already decided for user jobs (⚑RA2): **actor = the author** (their
permissions, re-checked at every run, inside the reaction's scope), **real actor = `reaction:{key}`** with the existing
`ActorKind.Job`, relation `Delegation`. No new actor kind, and Audit's mapping stays as it is. If the author loses a permission or leaves, the reaction pauses
and the scope's admins are told. Audit records both: "reaction big-acme-orders (by Maria) sent message 812".

Authoring rights: `studio.reaction.enact` for the scope; the catalog offered to the author is already filtered by their
permissions, so a reaction can never do what its author could not do by hand.

## 6. Limits and safety (⚑C12 decided; ⚑C30, ⚑C31 pending)

- **Loops:** origin reaction id and cascade depth on the call context (01 §7).
- **Quotas:** runs per reaction and per scope per day, and messages per channel per scope, through the Resilience quota
  (Notifications ⚑N9). A reaction that hits its quota stops for the day and tells the author.
- **Personal data:** fields classified Personal (Privacy) may go to internal users or to the data subject on
  transactional channels only; marketing messages need consent (Notifications ⚑N7). Test-run traces are masked.
- **Duplicates:** Messaging's idempotency key (event id, reaction id, revision) makes at-least-once delivery act once.
- **Failures:** retries per step side effect (01 §5); after the last retry the run is marked failed and the author is
  told once per day, not per failure.

## 7. Test before enacting

- **Dry run** on a sample event the author edits: the trace shows the condition result and each step (sends simulated).
- **Recent events:** where the source keeps them (EventSourcing streams, Audit, the Messaging journal when one exists),
  "would have fired 12 times in the last 7 days" with the list. Where none is kept, only the dry run is offered.

## 8. Where reactions run (⚑C27)

By default on central, where events from every site arrive. A reaction marked **site** also runs on a site broker
(Hosting module) while central is unreachable, for local needs such as "tell the store manager when the till is short";
when central returns, the inbox filter keeps a run from repeating. With several central nodes, one worker per (event
partition, tenant) comes from YFex.Cluster (design).

## 9. Packages (⚑C9, decided 2026-11-26)

| Package | Holds | References |
|---|---|---|
| `YFex.Studio.Public` | reaction IR (shared with 02's flows), keys, diagnostics | core, Validation, Expressions |
| `YFex.Studio.Reactions` | router (event key + scope index), condition evaluation, server runner (Messaging handler / Orchestration process / Jobs template), quota and loop guard, history query, revision kind for reactions | Public, Cqrs, Messaging, Orchestration, Jobs, Resilience, Revisions, Actors, Authorization.Public |
| `YFex.Studio.Designer` | reaction editor view models over `YConditionBuilder` / `YFlowBuilder` | State, UI.Public |

Licensing: the reactions module and its daily run quota may be license features (⚑C28).
