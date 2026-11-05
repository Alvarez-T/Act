# YFex.Notifications — one way to send messages to people (e-mail, SMS, WhatsApp, chat, webhooks, in-app, push) — Plan

> **Status:** PROPOSED 2026-11-04, **revised 2026-11-04** (ecosystem review, see 06 log). Docs only, no code. Decisions ⚑N1–⚑N20 (§5)
> wait for the user, except ⚑N14 and ⚑N17 (agreed in the review) and the UI integration (§3, ⚑N20 naming still open).
> **Repo:** `C:\Dev\YFex\YFex.Notifications` (sibling, template YFex.Networking / YFex.Time; own `notifications.slnx`). Not created.
> **Origin:** 2026-11-04. The Jobs ownership review (Plan D §11, O4) found that a Jobs notifier would be the **third** way the ecosystem
> sends messages. `YFex.Authentication.Delivery` is built (SMTP, HTTP e-mail, Twilio SMS/WhatsApp, Meta WhatsApp). `YFex.Logging.Alerts`
> designs its own `INotificationChannel` / `ISmsProvider` (logging 08 §5.3, ⚑L10). Jobs D2a built `IJobNotifier` with a no-op default and
> names the bridge to this library. The user asked for a separate plan that says who has to change. Authentication already said its
> delivery code would move to a future `YFex.Notifications` (authentication README ⚑AN14, §"later").

## Files

| File | What it covers |
|---|---|
| README (this file) | why, scope, shape, packages, decisions, phase summary |
| [01 — Inventory](01-current-state-and-inventory.md) | what is built (file:line), what is designed or planned, every sibling checked, the value objects and contracts that exist |
| [02 — Model and core types](02-model-and-core-types.md) | channel + provider, capabilities, destination, recipient, contacts, message, template, inbound events, outbox record |
| [03 — API and examples](03-api-and-examples.md) | **dev-side code first**: sending, registering channels/providers, templates, contacts, inbound, Jobs / Logging / Authentication / approvals usage |
| [04 — Delivery, inbound, offline, privacy, security, AOT](04-delivery-offline-privacy-security-aot.md) | outbox and retries, inbound scenarios, where sending runs, consent, personal data, limits, licensing, time, AOT gates |
| [05 — Consumers and migration](05-consumers-and-migration.md) | **who changes**, library by library (built, designed and planned): what, breaking or not, when; prerequisites in other repos |
| [06 — Phases, acceptance, log](06-phases-and-acceptance.md) | phases N0–N5 with acceptance, gates, implementation log |

Related: [authentication 05](../authentication/05-one-time-codes-and-delivery.md) (OTP delivery rules), [authentication 08 §2](../authentication/08-account-lifecycle-and-recovery.md)
(verified contacts), [logging 08 §5](../logging/08-server-alerts-viewer.md) (alerts, incidents, routing, channel plug-ins), Jobs Plan D §6.3
(`YFex.Jobs/YFex.Jobs.Templates-Development-Plan-D.md`), [offline 07 §3](../offline/07-what-runs-where.md) (e-mail/SMS go through central),
[validation 10 §2](../validation/10-messages-localization.md) (message catalogs in core), [licensing 06/07](../licensing/README.md) (gating, meters),
[authorization-consolidation 10](../authorization-consolidation/10-run-as-context.md) (run-as, actor types to core).

## 1. Why

| Today | Problem |
|---|---|
| `YFex.Authentication.Delivery` (built) sends codes and security notices through SMTP, an HTTP e-mail API, Twilio and Meta WhatsApp | the transports are good and generic, but they live in an authentication package and only accept `ChallengeMessage` / `SecurityNotice` |
| `YFex.Logging.Alerts` (designed) plans Telegram, Slack/Discord/Teams webhooks, e-mail, ntfy, a generic webhook, an `ISmsProvider` seam, "channels are plug-ins" | a second set of senders, e-mail written twice |
| `YFex.Jobs.Templates` (D2a, built) calls `IJobNotifier` (no-op default) | every app would write a third one |
| Audit (tenant witness e-mail), Privacy (incident notices, rights answers), Offline ("queued, sent later through central"), approvals (Authorization, Installations pairing), Offline reconcilers | each names "a notification" without saying which library sends it |

**One library owns the channels, providers, contacts and the delivery mechanics.** Each consumer keeps what is specific to it:

| Owner | Keeps |
|---|---|
| Authentication | OTP rules: codes inline, no links in WhatsApp OTP, country allow-list, SMS budget, Twilio Verify; verified account contacts |
| Logging alerts | incidents, routing, on-call rotations, escalation, digests |
| Jobs | which outcomes notify, repeat limiting (⚑J28) |
| Authorization / Installations | the approval itself (a notification only points to it, never approves) |

## 2. Scope

**In:**
- channels (e-mail, SMS, WhatsApp, chat, webhook, in-app, push) and **providers** under them (⚑N14);
- channel capabilities (length, rich text, actions, attachments, receipts, replies, cost);
- destinations, recipients and a **contact book** with preferences and quiet hours (⚑N17);
- messages and templates over core `YFex.Localization` catalogs (culture, scope, channel variant) (⚑N15);
- a durable outbox with retries and idempotency;
- **inbound**: delivery receipts, actions (ack, unsubscribe), replies and opt-out keywords (⚑N13);
- per-scope configuration (provider, sender, endpoints, templates);
- abuse and cost limits (rate, budget), usage meters and optional license gating (⚑N19);
- opt-out for non-transactional messages, through Privacy's consent;
- an in-app channel over Messaging's server-pushed events, shown by YFex.UI's `INotification` / `IToast`;
- audit of every send and every inbound action.

**Out:**

| Excluded | Where it lives instead |
|---|---|
| Deciding *when* to notify | the consumer: alert rules, job outcomes, login flows, approval requests |
| Incident dedupe, on-call rotations, escalation | Logging alerts |
| Approving anything from a message | Authorization / Installations screens (a link opens the screen; sign-in and step-up still apply) |
| Marketing campaigns, segments, A/B tests | an app |
| Conversational support inbox (free-text replies) | an app; Notifications only routes the reply to a registered handler |
| A notification-centre view | YFex.UI (after N3); this plan ships the in-app channel, the inbox query and the client bridge to `INotification` |
| Device registration itself | YFex.Installations; push is only a channel here (N4) |

## 3. Shape

```
 consumer (Authentication, Logging.Alerts, Jobs bridge, Audit, Privacy, approval bridges, Orchestration step, Composer step, app)
   │  INotifications.SendAsync(NotificationRequest)            ← YFex.Notifications.Public (leaf, references YFex core only)
   ▼
 YFex.Notifications ── resolve recipients (contact sources) → consent/opt-out → license gate → limits
   │                   → outbox (durable, idempotency key) → dispatcher (YFex.Resilience pipeline, ITrustedClock)
   │                   → render (IMessageCatalog layers: module → tenant/scope) → channel
   ▼
 INotificationChannel per DeliveryChannel ──► provider for the scope (endpoint from YFex.Web's IApiEndpointStore)
   ├─ Email    → IEmailProvider    (.Email: SMTP, HTTP e-mail API)
   ├─ Sms      → ISmsProvider      (.Twilio; later Zenvia, AWS SNS; .Devices GSM modem)
   ├─ WhatsApp → IWhatsAppProvider (.Meta Cloud API, .Twilio)
   ├─ Chat     → IChatProvider     (.Chat: Telegram, Slack, Discord, Teams, ntfy)
   ├─ Webhook  (.Webhook, HMAC-signed)          ├─ Push → IPushProvider (.Push: FCM, APNs; tokens from Installations)
   └─ InApp    (.InApp: inbox + IEventChannel<InboxChanged>) ──► client: YFex.UI INotification / IToast

 provider callbacks, signed links, chat buttons, replies
   ──► .AspNetCore inbound endpoints → verify (provider signature / signed link) → InboundEvent
       → receipt: outbox/log │ opt-out: contact book + Privacy ledger │ action / reply: consumer handler by key
```

## 4. Packages

| Package | TFM | Content | References |
|---|---|---|---|
| `YFex.Notifications.Public` | net11.0 | `DeliveryChannel`, `ContactDestination`, `MaskedDestination`, `DeliveryResult` family (moved from `YFex.Authentication.Public`, ⚑N3), `NotificationRequest`, `Recipient`, `INotifications`, `INotificationChannel`, provider seams, `ChannelCapabilities`, `IContactSource`, `InboundEvent`, `INotificationActionHandler` | YFex core (`EmailAddress`, `PhoneNumber`, actor types after ⚑RA8, `RawValue`) |
| `YFex.Notifications` | net11.0 | pipeline, templates over `IMessageCatalog`, contact book, outbox contract + in-memory outbox, dispatcher, limits, recipient lists, inbound router, audit events | `.Public`, YFex.Tenancy, YFex.Resilience, YFex.Audit.Public, YFex core (`Localization`, `Time`) |
| `YFex.Notifications.Generator` | netstandard2.0 | `[NotificationData]` accessors (fields → `RawValue` span, no boxing) | Roslyn |
| `YFex.Notifications.Email` / `.Twilio` / `.Meta` / `.Chat` / `.Webhook` | net11.0 | providers (moved/extended from Authentication.Delivery) + provider callback parsers | `.Public`, YFex.Web (endpoints, `YFex.Web.Refit` handlers) |
| `YFex.Notifications.InApp` | net11.0 | in-app channel + inbox (server) | `.Public`, YFex.Messaging.Rpc |
| `YFex.Notifications.InApp.Client` | net11.0 | subscribes to `InboxChanged`, shows it through YFex.UI `INotification` / `IToast` | `.Public`, YFex.Messaging.Rpc, YFex.UI.Abstractions |
| `YFex.Notifications.AspNetCore` | net11.0 | inbound endpoints (provider callbacks, signed links, chat buttons, replies) | `.Public`, ASP.NET Core, YFex.Cryptography |
| `YFex.Notifications.Push` | net11.0 | FCM / APNs providers (N4) | `.Public`, YFex.Installations.Public |
| `YFex.Notifications.Devices` | net11.0 | SMS over a GSM modem (AT commands) for sites without a provider (N4) | `.Public`, YFex.Devices |
| `YFex.Notifications.Data` | net11.0 | outbox + log + inbox + contact book over YFex.Data (central); claims with row locks | YFex.Data |
| `YFex.Notifications.Persistence` | net11.0 | outbox over YFex.Persistence `IDurableQueue<T>` (host, tests) | YFex.Persistence |
| `YFex.Notifications.Testing` | net11.0 | capturing channel/providers, fake inbound, fake-clock dispatcher | `.Public` |

Bridges live with the consumer (e.g. `YFex.Jobs.Templates.Notifications`, `YFex.Authentication.Notifications` contact source,
`YFex.Authorization.Notifications` approval notices), never here: Notifications references no consumer. Licensing is reached through an
optional seam (⚑N19), so Notifications does not wait for it.

## 5. Decisions

| # | Question | Recommendation | Alternative | Where |
|---|---|---|---|---|
| N1 | Where does the library live? | **New sibling repo `YFex.Notifications`** (server parts, several adapters, consumed by sibling repos) | inside YFex.Authentication; inside YFex core | §4 |
| N2 | What moves out of Authentication? | **the transports** (SMTP, HTTP e-mail, Twilio messaging, Meta WhatsApp, the HTTP helper, endpoint resolution); Authentication keeps `IChallengeSender`, OTP templates and rules, Twilio **Verify** (OTP-only) as `YFex.Authentication.Delivery` over Notifications channels | move everything (OTP rules would leak into a generic library); move nothing (third copy) | 05 §1 |
| N3 | Where do `DeliveryChannel`, `ContactDestination`, `MaskedDestination`, `DeliveryResult` live? | **`YFex.Notifications.Public`** (leaf); `YFex.Authentication.Public` references it (**breaking**: namespace change) | YFex core; or keep in Authentication (wrong direction) | 02 §1 |
| N4 | Message content: text or template + data? | **template key + typed data** rendered by Notifications (culture, scope, channel variant); raw text allowed for operator/chat channels | consumers render text themselves | 02 §5 |
| N5 | Durability and retries | **outbox first**, dispatched by a hosted service with a **YFex.Resilience** pipeline (the only retry owner); provider HTTP calls go through `YFex.Web.Refit` endpoints with **no** handler retry (or one fast retry for connection resets), so retries never stack. **Inline** only when the caller asks (`Delivery.Inline`, OTP) | always inline; always queued | 04 §1 |
| N6 | Where does sending run? | **central and hosts with internet**; a terminal **queues** a send as an offline command and central sends it (offline 07 §3). Inbound endpoints run only at central | each terminal sends directly | 04 §3 |
| N7 | Opt-out / consent | **transactional** and **operational** need no consent; **marketing** requires Privacy's purpose consent (⚑P9) and honours opt-outs. Until the ⚑P9 ledger is built, marketing sends are refused (`NoConsent`) unless the app supplies an `IMarketingConsent` seam | one rule for everything | 04 §4 |
| N8 | Who decides recipients? | the consumer passes **recipients**: a destination, an actor, an app reference (`Recipient.Reference`, e.g. Jobs' string ids) or a **recipient list** stored as data per scope | only raw destinations | 02 §3 |
| N9 | Rate and spend limits | per (scope, channel) rate + daily budget, generalized from Authentication's SMS budget; the day is the scope's day (⚑N16); Authentication keeps its stricter OTP limits on top | only Authentication's | 04 §5 |
| N10 | In-app channel | **yes, phase N3**, over Messaging.Rpc server-pushed events + a stored per-recipient inbox; the client shows it through YFex.UI | leave to apps | 02 §8 |
| N11 | Repeat limiting / digests | **not in Notifications**: Jobs (⚑J28) and Logging keep theirs; Notifications only drops exact duplicates by idempotency key | a generic throttle here | §2 |
| N12 | Who may send / edit templates, contacts and lists? | permissions `notifications.send`, `notifications.templates.manage`, `notifications.contacts.manage`, `notifications.recipients.manage`, `notifications.log.view` (`[PermissionCatalog]`); background sends run as the stored actor (authorization 10, run-as) | no permissions | 04 §6 |
| N13 | Inbound (receipts, actions, replies, opt-out keywords) | **yes**: `YFex.Notifications.AspNetCore` receives provider callbacks, signed links and chat buttons, verifies them, turns them into `InboundEvent`s; receipts and opt-outs are handled here, actions and replies go to a consumer handler registered **by notification key**; unknown → logged and dropped | no inbound (no ack, no STOP, no receipts) | 02 §7, 04 §2 |
| N14 | Channel vs provider | **agreed 2026-11-04:** one channel per `DeliveryChannel` (Email, Sms, WhatsApp, Chat, Webhook, InApp, Push) owns rendering limits and capabilities; **providers** under it (`IEmailProvider`, `ISmsProvider`, `IWhatsAppProvider`, `IChatProvider`, `IPushProvider`) talk to Twilio, Zenvia, Meta, a GSM modem… Replaces logging 08 §5.3's `INotificationChannel`/`ISmsProvider` | one package per provider acting as a channel | 02 §2 |
| N15 | Template texts | **core `YFex.Localization`**: a notification's texts are message codes (`{key}.{channel}.subject` / `.body`) in `IMessageCatalog` layers (generated module catalogs → tenant/scope dynamic layers); args are `RawValue`s from the generated accessor. Notifications adds only HTML bodies and provider template mapping (WhatsApp) | a second template store in Notifications | 02 §5 |
| N16 | Clock and scope time zone | **`ITrustedClock`** (core `YFex.Time` contracts, YFex.Time engine) for signed-link expiry, budget days and outbox expiry; `TimeProvider` only for dispatcher wake-ups. The scope's time zone (quiet hours, budget day) comes from **one shared seam** — propose `IScopeTimeZones` in core/Tenancy, adapted from Jobs' `IJobScopeTimeZones`; until then a Notifications option per scope | each library its own zone setting | 04 §5 |
| N17 | Where do contacts come from? | **agreed 2026-11-04:** Notifications owns the **contact book** (people and endpoints without an account: on-call phones, Telegram chat ids, chat webhooks, partner/authority e-mails), preferences, opt-outs and quiet hours. **Contact sources** resolve actors: Authentication's **verified contacts** (`VerifiedContact`, built) through a bridge, and the app's own directory (customers). Not Tenancy (scopes, no people), not Profiling (consent-scoped signals; using them as addresses breaks purpose limitation) | Logging keeps its own contact table | 02 §4 |
| N18 | Provider per scope | the scope's endpoint (`ApiEndpoint`) names the provider; switching provider is data. **YFex.CustomLogic** only when a scope needs its own code (a custom provider implementation or routing rule) | CustomLogic for every provider choice | 02 §2 |
| N19 | Licensing | optional seam: usage meters `notifications.{channel}.sent` through `IUsageMeter` (licensing 07) and optional module gating of paid channels (licensing 06) → `DeliveryRefused(NotLicensed)`; default seam = no meter, no gate | hard reference to Licensing | 04 §5 |
| N20 | Name of the send interface | rename `INotifications` → **`INotificationSender`** so it does not read like YFex.UI's `INotification` (a displayed notice) | keep `INotifications` | 02 §2 |

## 6. Phase summary

| Phase | Content | Needed by |
|---|---|---|
| **N0** | repo, `.Public` (types moved from Authentication.Public), pipeline with in-memory outbox, channel/provider split, templates over `IMessageCatalog`, `.Generator`, `.Email` (SMTP + HTTP API moved), `.Testing`, AOT smoke | Jobs bridge |
| **N1** | Authentication migration (`.Twilio`, `.Meta` as providers; `Authentication.Delivery` = OTP rules over channels); scope templates; limits; meter seam | Authentication stays green |
| **N2** | durable outbox (`.Persistence`, `.Data`), delivery log, audit, offline queuing, consent/opt-out, contact book + Authentication contact source, **inbound receipts, unsubscribe links, STOP keywords** (`.AspNetCore`), permissions | first production sends outside login |
| **N3** | `.Chat`, `.Webhook`, `.InApp` + `.InApp.Client` (YFex.UI), recipient lists, quiet hours, **inbound actions** (ack), approval bridges, Orchestration wait-for-action | Logging alerts, approvals, Jobs owners in-app |
| **N4** | `.Push` (FCM/APNs, tokens through Installations), `.Devices` GSM modem SMS | mobile clients, sites without an SMS provider |
| **N5** | Privacy participant, retention job, Capabilities source, Composer step, license gating | Privacy rights phase, AI agents |
