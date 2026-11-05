# 06 — Phases, acceptance, log

> Gates for every phase: `dotnet build notifications.slnx` 0 warnings; tests green; AOT smoke 0 IL/AOT warnings (win-x64; linux-x64
> through `linuxKit`); render benchmark (04 §7); consumers' suites unchanged in count and green; docs updated (05 §4).
> Prerequisites owned by other repos are listed in 05 §3.

## Phase N0 — repo, contracts, e-mail

- Repo `YFex.Notifications` (template YFex.Networking): `.Public` (types moved from Authentication.Public, ⚑N3; `INotifications`,
  `INotificationChannel`, provider seams, `ChannelCapabilities`, `NotificationRequest`, `Recipient`, `IContactSource`), `YFex.Notifications`
  (pipeline, templates over `IMessageCatalog`, in-memory outbox, dispatcher on a YFex.Resilience pipeline and `ITrustedClock`),
  `.Generator` (`[NotificationData]` → `RawValue`), `.Email` (SMTP + HTTP API providers moved), `.Testing`, tests, AOT smoke, benchmarks.
- Authentication steps 05 §2.1–2.2.
- **Acceptance:**
  - a queued e-mail is rendered per scope and culture from catalog codes and sent once, even when `SendAsync` is called twice with the
    same idempotency key;
  - a retryable failure is retried by the dispatcher only (the HTTP client does not retry) and expires after its limit on the trusted clock;
  - an inline send returns the channel's `DeliveryResult`;
  - the render benchmark decides between `MessageCatalog.TryFormat` and `MessageTemplate`;
  - Authentication's OTP e-mail tests pass unchanged through the new channel.

## Phase N1 — Authentication on Notifications

- `.Twilio`, `.Meta` as SMS/WhatsApp providers; `Authentication.Delivery` = OTP rules over channels; provider per scope from the endpoint
  (⚑N18); limits (⚑N9); scope template overrides (dynamic catalog layer); usage-meter seam (⚑N19, no-op default).
- **Acceptance:**
  - WhatsApp → SMS fallback still reports "not on WhatsApp" inline;
  - the SMS budget refuses over-limit sends with `BudgetExhausted`;
  - two scopes send SMS through two different providers by endpoint data only;
  - a scope template overrides the default;
  - an app using `AddYFexAuthenticationDelivery` compiles unchanged.

## Phase N2 — durable, offline, audited, consented, contacts, receipts

- `.Persistence` (`IDurableQueue<T>`) and `.Data` outboxes (`SKIP LOCKED` claims), delivery log, Audit facts,
  `Notifications.Commands.Send` (`IQueueable`), consent/opt-out (⚑N7), permissions (⚑N12), protected destinations; contact book +
  `YFex.Authentication.Notifications` contact source (⚑N17); `.AspNetCore` inbound for **receipts, unsubscribe links and opt-out
  keywords** (⚑N13).
- **Acceptance:**
  - **restart:** a pending send survives a restart and is sent exactly once; two central nodes never send the same record;
  - **offline:** a terminal's send made offline is sent by central after replay, with the terminal's caller as actor;
  - **consent:** a marketing send without consent is refused with `NoConsent`;
  - **contacts:** an actor recipient resolves to the account's preferred verified contact; a contact-book entry is unusable until confirmed;
  - **receipts:** a recorded Twilio "undelivered" callback marks the log `Failed`; a SendGrid hard bounce flags the destination and the next send is `Undeliverable`;
  - **opt-out:** a "PARAR" reply and a one-click unsubscribe both stop marketing to that destination and are audited;
  - **masking:** the log shows only masked destinations; **audit:** every send and inbound event is audited with actor and result.

## Phase N3 — chat, webhook, in-app, lists, actions, approvals

- Prerequisites (05 §3): `EventChannelHost` wiring; `IScopeTimeZones` or the per-scope option.
- `.Chat` (Telegram, Slack, Discord, Teams, ntfy), `.Webhook` (HMAC), `.InApp` + `.InApp.Client` (YFex.UI `INotification` / `IToast`),
  recipient lists as data, quiet hours, **inbound actions** (buttons, signed links), approval bridges (Authorization, Installations
  pairing), Orchestration `WaitFor<ActionTaken>`.
- **Acceptance:**
  - a Logging incident reaches Telegram and a Teams webhook through one request; tapping **Ack** in Telegram acks the incident once;
  - a webhook body verifies with its secret;
  - an in-app notice appears as a YFex.UI notification on the owner's connected client and in the inbox query;
  - an operational send inside quiet hours waits for the window's end in the scope's time zone; a transactional one does not;
  - an approval notice opens the approval screen and cannot approve without sign-in and step-up;
  - a site-level recipient list overrides the tenant's.

## Phase N4 — mobile push, GSM modem

- `.Push` (FCM/APNs, tokens through YFex.Installations); `.Devices` GSM modem `ISmsProvider` with polled receipts/replies.
- **Acceptance:** a registered handheld receives a push; an unregistered token is dropped after the provider says so; an SMS goes out
  through a modem on a simulated serial port (`SimulatedDevice`) and its receipt reaches central.

## Phase N5 — privacy, retention, capabilities, Composer, licensing

- Privacy participant (privacy 07 §4), log retention job (YFex.Jobs), Capabilities source, Composer "notify" step, license gating (⚑N19).
- **Acceptance:**
  - a subject's export lists their deliveries and contact-book entries;
  - erasure removes their inbox, pending sends and contact-book entries;
  - retention purges log rows past the configured age;
  - an AI agent sees `notifications.send` as a gated background side effect;
  - a channel tied to an unlicensed module is refused with `NotLicensed`.

## Implementation log

### 2026-11-04 — Plan written (docs only, no code)

The user asked for a separate design plan that says who has to change, after the Jobs ownership review (Plan D §11, O4) found
three delivery paths: Authentication.Delivery (built), Logging.Alerts (designed) and Jobs D2 (designed). Inventory in 01, consumer
changes in 05. Open for the user: ⚑N1–⚑N12.

### 2026-11-04 — Ecosystem review and revision (docs only, no code)

Checked against every `YFex.*` sibling (built, designed, planned). Corrected: Jobs `IJobNotifier` is **built** (D2a) with string
recipients and kinds Failure/DeadLetter/Recovered; actor types move to core (⚑RA8); `EnableServerPushedEvents()` exists but its flag
is unused; resilient HTTP = `YFex.Web.Refit` `UseResilience` (YFex.Resilience pipeline; it replaced `AddRetry`/`AddRateLimit` on 2026-11-04)
while Authentication's `DeliveryHttpClient` uses none — retries now owned by the dispatcher's YFex.Resilience pipeline only; host outbox over `IDurableQueue<T>`; no Jobs lease
(row claims); Files is planned, not built; Privacy participants are 07 §4 and ⚑P9 is not built.
User decisions: **⚑N14 agreed** (channel + providers, replaces logging 08 §5.3 contracts), **⚑N17 agreed** (contact book in
Notifications; sources = Authentication verified contacts + app directory; not Tenancy, not Profiling), **YFex.UI in-app integration
agreed**; all designed/planned libraries are listed in 05. Added: ⚑N13 inbound (with scenarios, 04 §2), ⚑N15 templates over core
`YFex.Localization`, ⚑N16 trusted clock + shared scope time zone, ⚑N18 provider per scope (CustomLogic only for code), ⚑N19 Licensing
seam, ⚑N20 rename to `INotificationSender`; new consumers/integrations: Time, Localization, Licensing, approval flows (Authorization,
Installations pairing, Authentication step-up), Offline 08 reconcilers, Devices (GSM modem), CustomLogic, Orchestration, Composer.
Open: ⚑N1–⚑N13, ⚑N15, ⚑N16, ⚑N18–⚑N20.
