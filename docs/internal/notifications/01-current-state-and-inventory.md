# 01 — Current state and inventory

> Surveyed 2026-11-04 against the sibling repos and `docs/internal`; **re-checked 2026-11-04** (ecosystem review: every `YFex.*` sibling,
> built, designed and planned). `C:\Dev\YFex\YFex.Notifications` does not exist. File references are to the sibling repos under `C:\Dev\YFex`.

## 1. Built: YFex.Authentication.Delivery (893 lines) and its contracts

| Item | Where | Generic? | Plan |
|---|---|---|---|
| `DeliveryChannel` (interned value object: `Email`, `Sms`, `WhatsApp`, `Voice` + app-declared) | `YFex.Authentication/src/YFex.Authentication.Public/Names.cs:33` | yes | move to `.Public` (⚑N3) |
| `ContactDestination`, `MaskedDestination` | `YFex.Authentication.Public/Contacts.cs:18, 74` | yes | move (⚑N3) |
| `VerifiedContact` (destination, verified at, primary, usable for sign-in/recovery, **preferred channel**) | `Contacts.cs:77` | the type is account-specific | stays; read by the Authentication contact source (⚑N17) |
| `Delivered` / `DeliveryRefused` / `DeliveryFailed`, `union DeliveryResult`, `DeliveryRefusal` | `YFex.Authentication.Public/Delivery.cs:1-35` | yes (except `NotOnWhatsApp`, which is still a channel fact) | move (⚑N3) |
| `IChallengeSender`, `ChallengeMessage`, `IVerificationProvider`, `ISecurityNoticeSender`, `SecurityNotice(Kind)` | `Delivery.cs:12-80` | `IVerificationProvider` and code send/check: **yes**; challenge/security-notice semantics: no | `IVerificationProvider` → `.Verification`; the rest stays (⚑N2) |
| `MessageTemplate` (placeholders parsed once into a span plan), `ChallengeTexts`, `ChallengeTemplates` | `YFex.Authentication.Delivery/ChallengeTemplates.cs:9, 77, 84` | overlaps core `MessageCatalog.TryFormat` | OTP texts stay; Notifications renders through `IMessageCatalog` (⚑N15); `MessageTemplate` is retired unless the N0 benchmark shows the catalog slower |
| `IDeliveryEndpoints`, `ApiStoreDeliveryEndpoints` (over YFex.Web `IApiEndpointStore`, tenant then global), `StaticDeliveryEndpoints`, `DeliveryEndpointNames` | `DeliveryEndpoints.cs:12-62` | yes | move; names get a `notifications.` prefix with the old names as aliases |
| `DeliveryHttp` helper, `DeliveryHttpClient` (a bare `HttpClient`, **no** retry/rate handlers, no Resilience) | `Senders.cs:12`, `DeliveryServiceCollectionExtensions.cs:111` | yes | replaced by `YFex.Web.Refit` endpoint clients (⚑N5) |
| `SmtpDeliveryOptions`, `SmtpChallengeSender` (also `ISecurityNoticeSender`) | `Senders.cs:69, 86` | transport yes, sender no | transport → `.Email` (`IEmailProvider`); a thin OTP sender stays |
| `EmailApiFormat`, `HttpEmailChallengeSender` | `Senders.cs:138, 147` | same | same |
| `TwilioDeliveryOptions`, `TwilioChallengeSender`, `TwilioVerifyProvider` | `Senders.cs:230, 252, 332` | yes | messaging → `.Twilio` (`ISmsProvider`, `IWhatsAppProvider`); Verify → `.Twilio` as an `IVerificationProvider` of `.Verification` (⚑N2) |
| `MetaWhatsAppOptions`, `MetaWhatsAppChallengeSender` (authentication templates, copy-code) | `Senders.cs:376, 395` | the Cloud API client yes; OTP template rules no | client → `.Meta` (`IWhatsAppProvider`) |
| `DeliveryBuilder`, `EmailDeliveryBuilder`, `PhoneDeliveryBuilder`, `AddYFexAuthenticationDelivery` | `DeliveryServiceCollectionExtensions.cs:19-145` | — | becomes a façade over Notifications registration (05 §2) |

References today: `YFex.Authentication.Delivery` → `YFex.Authentication.Public`, `YFex.Web` (`IApiEndpointStore`, `ApiEndpoint`).

## 2. Built elsewhere: contracts and engines to reuse (do not redefine)

| Type / engine | Where | Use here |
|---|---|---|
| `EmailAddress`, `PhoneNumber` | YFex core `src/YFex/Contacts/EmailAddress.cs:29`, `PhoneNumber.cs:30` | destinations |
| `RawValue` | core `src/YFex/Values/RawValue.cs:26` | template args without boxing |
| `IMessageSource`, `IMessageCatalog`, `MessageCatalog` (`Format` / `TryFormat` into a span, `RawValue` args, layered sources) | core `src/YFex/Localization/` (validation 10 §2 names notifications as a consumer) | templates (⚑N15) |
| `ITrustedClock`, `ITimeSource`, `TrustedInstant` | core `src/YFex/Time/`; engine in YFex.Time | expiry, budget days, signed links (⚑N16) |
| `ScopeIdentity`, `ScopeOverride` | YFex.Tenancy | scope chain (no time zone, no people) |
| `[Sensitive]`, `DataSensitivity`, `Masks` (incl. `{email}` preset) | core `src/YFex/Privacy/` | log masking |
| consent (`IConsentStore`, `ConsentRecord`, `ConsentState`, `ConsentLevel`) — profiling/telemetry consent levels; **purpose** consent is Privacy's ledger (⚑P9, not built) | core `src/YFex/Consent/` | ⚑N7 |
| `ApiEndpoint`, `IApiEndpointStore` (per-tenant credentials, protected secrets) | `YFex.Web/src/Web/YFex.Web/Stores/IApiEndpointStore.cs` | provider endpoint per scope (⚑N18) |
| `YFex.Web.Refit` endpoint builder: `UseResilience` + `HttpFailureClassifier` (2026-11-04 replaced `AddRetry`/`AddRateLimit`, web 07) | `YFex.Web/src/Web/YFex.Web.Refit/Handlers/ResilienceHandler.cs` | provider HTTP clients, rate-limited resource, no `Retry` (⚑N5) |
| `IResiliencePipelineFactory`, `ResilienceSpec` (+ `YFex.Resilience.Polly`) | `YFex.Resilience` (built) | dispatcher retries, breaker per provider |
| `IEventChannel<T>`, `EventChannelHost<T>`, `EnableServerPushedEvents()` (opt-in exists, **flag never read**: `YFexMessagingRpcClientOptions.cs:76-83`) | YFex `YFex.Messaging/src/YFex.Messaging.Rpc/` | in-app push; wiring is a Messaging prerequisite (05 §3) |
| `IOutbox`, `IOutboxCallerCapture` (offline RPC outbox, captures the caller) | `YFex.Messaging.Rpc/IOutbox.cs:61, 79` | terminal-queued sends carry the caller to central |
| `IDurableQueue<T>`, `SqliteDurableQueue<T>` | YFex `YFex.Persistence/` | host outbox (`.Persistence`) |
| `IQueueable` | YFex `YFex.Cqrs/src/YFex.Cqrs/Markers.cs` | `Notifications.Commands.Send` |
| `[PermissionCatalog]` | `YFex.Authorization.Public/Attributes.cs` | ⚑N12 |
| `ActorIdentity` and actor types | today `YFex.Authorization.Public/Actor.cs`; **moving to YFex core** (⚑RA8, decided 2026-11-04, not built) | `Recipient.Actor`, log actor |
| Approvals: `ApprovalEvent` / `ApprovalEvents`, `ServerApprovalTokens`, `DeviceApprovalTokenIssuer` | `YFex.Authorization/src/YFex.Authorization/Approvals/`, `YFex.Authorization.Installations` | approval notices (bridge) |
| Installations pairing admin approval (`PairingPending`), I5 | installations 08 §I5 (built 2026-10-27) | "pairing waits for you" notice |
| YFex.UI `INotification`, `IToast`, `NotificationContext`, `YNotificationHost`, `YToastHost` | `YFex.UI/src/Core/YFex.UI.Public/`, core `src/UI/YFex.UI.Abstractions/`, `YFex.UI/src/Avalonia/YFex.Avalonia/Controls/` | in-app display (`.InApp.Client`) |
| Jobs `IJobNotifier`, `JobNotification`, `JobNotificationKind` (`Failure`, `DeadLetter`, `Recovered`), `JobNotificationCodes` | `YFex.Jobs/src/YFex.Jobs.Templates/JobNotifications.cs:25-75` (D2a, built 2026-11-04) | Jobs bridge (05 §1) |
| Jobs `IJobScopeTimeZones` | `YFex.Jobs/src/YFex.Jobs.Abstractions/Policies/JobPlacements.cs:37` | model for the shared scope time-zone seam (⚑N16) |
| Orchestration `WaitFor` (durable waits, timeouts) | `YFex.Orchestration` | wait for an inbound action |
| CustomLogic `ICustomLogic`, `CustomLogicRegistry` | `YFex.CustomLogic` | scope-specific provider code (⚑N18) |
| YFex.Devices serial stack | `YFex.Devices` (docs 04) | GSM modem SMS provider (N4) |

## 3. Designed or planned, not built: who else needs to send (or support) messages

| Library | Status | Design | What it needs |
|---|---|---|---|
| YFex.Logging (`.Alerts`) | designed | logging 08 §5.2–§5.3, ⚑L6, ⚑L10 | channels Telegram, Slack/Discord/Teams webhooks, e-mail, ntfy, HMAC webhook; `ISmsProvider` (Twilio, Zenvia, AWS SNS, GSM modem); capabilities (length, ack, rich text, cost); contacts/rotations/schedules as data; **ack by signed link or Telegram button**; every notification and ack audited |
| YFex.Audit | built engine, witness designed | audit 06 §"tenant witness" (line 168) | daily seal-hash list e-mailed or posted to a tenant webhook |
| YFex.Privacy | mostly designed | privacy 00 R15, 07 §4 (participants), ⚑P9 (consent ledger) | incident notices (ANPD, subjects), rights answers, consent and withdrawals |
| YFex.Offline | built (Phases 0–3) | offline 07 §3, 08 (reconcilers) | queued sends through central; `IFactReconciler<T>` "notifies the e-commerce order owner" |
| YFex.Hosting | planned (plan 2026-11-03) | hosting 05 module list (no Notifications row yet) | `notifications.dispatcher` module for sites that send locally; tray toasts stay local UI |
| YFex.Files | planned (design proposed 2026-10-29) | files README | attachments by reference |
| YFex.Licensing | designed, no code | licensing 06 (gating), 07 (meters), 04 (expiry warnings 7 days ahead) | meters, paid-channel gating, license-expiry notices as a consumer |
| Composer | proposal (composer-design) | flows scheduled through `composer.flow.run` | a "notify" step through the `notifications.send` capability |
| Authentication approvals / step-up | built (AN-P5) | authentication README | inline approval and step-up notices to approvers |
| Apps (Fiscal DANFE e-mail, Farma reminders, order messages) | — | privacy 07, authentication README "general notifications … not here" | attachments, WhatsApp templates, consent, customer replies |

## 4. Not found anywhere

- No `INotificationSender` / generic `INotificationChannel` in code (search 2026-11-04 over every sibling `src`). Logging's
  `INotificationChannel` exists only in its design.
- No delivery log, message outbox, contact book, opt-out list or inbound callback receiver for messages.
- No scope time zone outside Jobs (`IJobScopeTimeZones`); Tenancy and YFex.Time own none.
- No lease across processes in Jobs (`IJobStore.TryAcquireLeaseAsync` is single-process); Audit chose `pg_try_advisory_lock`.
