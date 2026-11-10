# 05 — Consumers and migration: who has to change

> Read 2026-11-04 against code and `docs/internal`; **re-checked 2026-11-04** against every `YFex.*` sibling, including designed and
> planned ones. **Breaking** = callers must change code. Libraries never reference a consumer; bridges live with the consumer.
> Status column: **built** (code), **designed** (internal docs, no code), **planned** (plan proposed), **proposal**.

## 1. Changes by library

| Library | Status | Change | Breaking? | Phase |
|---|---|---|---|---|
| **YFex.Authentication** | built | (a) `DeliveryChannel`, `ContactDestination`, `MaskedDestination`, `DeliveryResult` family move to `YFex.Notifications.Public` (⚑N3). (b) `YFex.Authentication.Delivery` keeps `IChallengeSender` / `ISecurityNoticeSender` adapters, OTP texts and the challenge rules over Notifications channels and `.Verification` (code engine and Twilio Verify move there, ⚑N2); SMTP / HTTP e-mail / Twilio messaging / Meta clients move out as providers (⚑N2, ⚑N14). (c) `AddYFexAuthenticationDelivery` keeps its shape as a façade. (d) New bridge **`YFex.Authentication.Notifications`**: `IContactSource` over verified contacts (⚑N17) and an `Undeliverable` hook. (e) Step-up / inline-approval notices through Notifications. (f) Docs: README ⚑AN14, 05 §4 | **yes** for (a) (namespace; a `global using` eases it); no for (c)–(e) | N0 (a), N1 (b–c), N2 (d), N3 (e) |
| **YFex.Logging** (`.Alerts`) | designed | logging 08 §5.3: its `INotificationChannel`, `ISmsProvider`, `NotificationCapabilities` are **replaced** by Notifications' channel/provider/capabilities (⚑N14); its **Contacts** table moves to the Notifications contact book (⚑N17); Alerts keeps incidents, routing, **on-call rotations**, escalation, digests and registers the **ack** action handler (⚑N13); ⚑L10 reads "channels, providers and contacts from YFex.Notifications" | docs only | N3 |
| **YFex.Jobs** | **built** (D2a: `IJobNotifier`, `JobNotification`, ⚑J28 in `YFex.Jobs.Templates/JobNotifications.cs`) | New bridge **`YFex.Jobs.Templates.Notifications`** implements `IJobNotifier`: kinds `Failure` / `DeadLetter` / `Recovered` → keys `jobs.instance.failed` / `.dead-letter` / `.recovered`; `Code`, `Count`, `FirstUtc` as data; `Owner` + `Recipients` (app id strings) → `Recipient.Reference`; idempotency `{InstanceKey}:{Kind}:{Code}:{FirstUtc}:{Count}` (Jobs' streak is in memory and restarts). Plan D §6.3 mentions `Late`: the built enum has no `Late` — fix the plan text or add it in Jobs. The engine never references Notifications | no | after N0 |
| **YFex.Audit** | built (witness designed) | audit 06 "tenant witness" sent through Notifications (`audit.witness.daily`, e-mail or tenant webhook). Notifications emits its facts through `YFex.Audit.Public` | docs only | N2 |
| **YFex.Privacy** | designed | add Notifications to the participants (privacy **07 §4**): export / erase deliveries, inbox, contact book (04 §4). Marketing consent and opt-out withdrawals through the ⚑P9 ledger (**not built**: N2 refuses marketing until it exists). Incident notices (R15) and rights answers sent through Notifications | docs only | N2 (seam), N5 (participant) |
| **YFex.Offline** | built | offline 07 §3 row names `Notifications.Commands.Send` (`IQueueable`). Offline 08 reconcilers (`IFactReconciler<T>` "notifies the e-commerce order owner") send through Notifications at central. No Offline code change | no | N2 |
| **YFex.Messaging.Rpc** | built | `EnableServerPushedEvents()` exists but its flag is never read (`YFexMessagingRpcClientOptions.cs:76-83`): wiring `EventChannelHost` + the Wolverine bridge is a Messaging task, **prerequisite of N3** (also needed by the Jobs Hub, messaging-internals line 220) | no (additive) | before N3 |
| **YFex.Cqrs** | built | none. `IQueueable` commands and queries as they are | no | — |
| **YFex.Web** | built | none required. Providers use `IApiEndpointStore` / `ApiEndpoint` and `YFex.Web.Refit` endpoint clients (`UseResilience` with a rate-limited resource and no `Retry` — retries belong to the dispatcher, ⚑N5). Endpoint names get a `notifications.` prefix and name the provider (⚑N18); `authentication.*` names stay as aliases | no | N1 |
| **YFex.Resilience** | built | Dispatcher pipeline (timeout, retry, breaker per provider/scope resource) through `IResiliencePipelineFactory` (N0); rate limits per (scope, channel) as `PerScope` rate-limited resources; **new generic quota** (counter per key, Tenancy scope and period on `ITrustedClock`, durable store adapter) for daily budgets (⚑N9) | yes (quota) | N0, N1 |
| **YFex core — Localization** | built | none. Templates are message codes in `IMessageCatalog` layers (⚑N15); a generated module catalog per consumer | no | N0 |
| **YFex core — Time / YFex.Time** | built | none for the clock (`ITrustedClock`). **Proposal:** a shared `IScopeTimeZones` contract in core or Tenancy, adapted by Jobs (`IJobScopeTimeZones`) and used by Notifications (⚑N16) | no | N3 (quiet hours) |
| **YFex.Actors — actor types** | built (actors AC0–AC1b, 2026-11-05/09; ⚑RA8 became a separate package, ⚑AC1) | `ActorIdentity`, `ActorScope`, `ActorContext`, `ActorSnapshot` (sender capture for the outbox) come from YFex.Actors. N2 run-as sends still wait for the Authorization resolver | no for Notifications | before N2 |
| **YFex.Authorization** | built | none required. Notifications declares its own `[PermissionCatalog]` (⚑N12). New bridge **`YFex.Authorization.Notifications`**: approval requested/decided notices from `ApprovalEvents` (link opens the screen, never approves). Run-as caller from authorization-consolidation 10 | no | N2 (permissions), N3 (approvals) |
| **YFex.Installations** | built | (a) pairing waiting for admin approval (`PairingPending`, I5) → notice to the scope's admins (bridge in Installations); (b) push tokens per installation and device registration for `.Push` (N4) | no | N3 (a), N4 (b) |
| **YFex.UI** | built (`INotification`, `IToast`, hosts) | `YFex.Notifications.InApp.Client` shows inbox events through `INotification` / `IToast`; notification-centre view over the inbox query later. ⚑N20 renames `INotifications` → `INotificationSender` to avoid confusion with UI's `INotification` | no | N3; view after N3 |
| **YFex.Licensing** | designed, no code | optional seam: meters `notifications.{channel}.sent` (licensing 07), channel gating by module (licensing 06) → `NotLicensed`; license-expiry warnings (licensing 04, 7 days ahead) are an ordinary consumer | no | seam N1, gating N5 |
| **YFex.Devices** | built (serial stack) | `YFex.Notifications.Devices`: `ISmsProvider` over a GSM modem (AT commands), receipts/replies polled and forwarded to central | no | N4 |
| **YFex.CustomLogic** | built | none. A scope that needs its own provider or routing code registers it through CustomLogic; plain provider choice is endpoint data (⚑N18) | no | N1+ |
| **YFex.Orchestration** | built | none. A saga step sends and `WaitFor<ActionTaken>` with timeout (04 §2.2 scenario 8); Notifications publishes inbound actions as events | no | N3 |
| **Composer** | proposal | a "notify" step that calls the `notifications.send` capability (gated, Background side effect) | — | N5 |
| **YFex.Tenancy** | built | none. Scope chain for templates, lists, endpoints, limits. Not a contact source (⚑N17). Possible home of `IScopeTimeZones` (⚑N16) | no | — |
| **YFex.Cryptography** | built | none. HMAC for webhooks, signed links, protected fields | no | N2 |
| **YFex.Data** | built | none. `.Data` stores; dispatcher claim with `SKIP LOCKED` row claims (no Jobs lease) | no | N2 |
| **YFex.Persistence** | built | none. `.Persistence` outbox over `IDurableQueue<T>` | no | N2 |
| **YFex.Files** | planned (design proposed) | none. Attachments by Files reference once built; blob id until then | no | when Files exists |
| **YFex.Hosting** | planned | host module `notifications.dispatcher` for sites that send locally (add a row to hosting 05) | docs only | N2+ |
| **YFex.Capabilities** | built | capability source for `notifications.send` (`Background` side effect) so AI agents and Composer see it gated (capabilities §3A) | no | N5 |
| **YFex.Profiling, YFex.System** | built | none. Not contact sources (⚑N17); their consent levels are telemetry consent, not purpose consent | — | — |
| **YFex.Payment** | out of scope (to be rewritten) | none now; payment receipts become an app consumer after the rewrite | — | — |
| **Apps** (Fiscal DANFE e-mail, Farma reminders, Finance dunning, order messages) | — | ordinary consumers; customers through an app `IContactSource`; marketing needs consent (⚑N7); optional reply handler | — | any |
| Not related | — | Search, Security, Networking, EventSourcing, Domain, Brain, Axamly, Avalonia, LoggY (may show the delivery log later), Marketplace (empty) | — | — |

## 2. Authentication migration steps (N0–N2)

1. N0: create `.Public` with the moved types. `YFex.Authentication.Public` references it, and the old files are deleted. Every
   `using YFex.Authentication;` that needs those types adds `using YFex.Notifications;`. Golden tests for `MaskedDestination`
   formatting stay green.
2. N0: SMTP + HTTP e-mail become `IEmailProvider`s in `.Email`; `SmtpChallengeSender` becomes `ChallengeSender(INotificationChannel)`
   rendering `ChallengeTexts`.
3. N1: Twilio messaging (`ISmsProvider`, `IWhatsAppProvider`) and Meta WhatsApp (`IWhatsAppProvider`) move; the code engine and Twilio Verify move to `.Verification` (⚑N2).
4. N1: `AddYFexAuthenticationDelivery` registers the providers through `AddYFexNotifications` (no outbox needed for inline OTP sends).
   Apps keep their code.
5. N2: `YFex.Authentication.Notifications` contact source; bounce → `Undeliverable` hook.
6. Gates: Authentication tests and its AOT smoke are unchanged in count and green. Delivery tests run against `.Testing`'s capturing providers.

## 3. Prerequisites in other repos

| Before | Needs | Owner |
|---|---|---|
| N1 budgets | generic quota (key × Tenancy scope × period, trusted clock, durable shared-store adapter); Authentication `DeliveryBudget` moves onto it | YFex.Resilience |
| N2 run-as sends | actor types (YFex.Actors, built) → Authorization run-as resolver | YFex.Authorization |
| N2 marketing sends | Privacy ⚑P9 consent ledger (else refused / app seam) | YFex.Privacy |
| N3 in-app | `EventChannelHost` wiring behind `EnableServerPushedEvents()` | YFex Messaging |
| N3 quiet hours | `IScopeTimeZones` (else per-scope option) | YFex core / Tenancy + Jobs adapter |
| N4 push | push token registration per installation | YFex.Installations |
| attachments by reference | YFex.Files | YFex.Files |
| N5 gating | Licensing engine | YFex.Licensing |

## 4. Docs to update when each phase lands

`docs/agent/infrastructure-map.md` (row exists; add inbound + contacts), `docs/index.md` (internal plans list), authentication README
(⚑AN14) and 05 §4, logging 08 §5.3 and README ⚑L10, Jobs Plan D §6.3 (bridge mapping, `Late`), audit 06, privacy 07 §4, offline 07 §3,
hosting 05 module list, validation 10 (notifications as a catalog consumer), licensing 06/07 (meters, module), installations 08
(pairing notice), authorization-consolidation (approval notices), YFex.UI docs (in-app client).
