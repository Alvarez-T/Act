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

- `.Twilio`, `.Meta` as SMS/WhatsApp providers; `.Verification` (generic code send/check, `IVerificationProvider`, Twilio Verify; ⚑N2); `Authentication.Delivery` = challenge rules over channels and `.Verification`; provider per scope from the endpoint
  (⚑N18); limits (⚑N9); scope template overrides (dynamic catalog layer); usage-meter seam (⚑N19, no-op default).
- **Acceptance:**
  - WhatsApp → SMS fallback still reports "not on WhatsApp" inline;
  - the SMS budget refuses over-limit sends with `BudgetExhausted`;
  - two scopes send SMS through two different providers by endpoint data only;
  - a scope template overrides the default;
  - a code is sent and checked through `.Verification` for a destination with no account (attempt limit, expiry on the trusted clock);
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
  - **contacts:** an actor recipient resolves to the account's preferred verified contact; a contact-book entry is unusable until confirmed by a `.Verification` code;
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

### 2026-11-09 — Decisions (docs only, no code)

User agreed ⚑N1 (new sibling repo), ⚑N3, ⚑N4, ⚑N5, ⚑N15, ⚑N16 (clock = core `ITrustedClock`, engine YFex.Time; scope time zone
seam still proposed for N3), ⚑N20 (`INotificationSender`; it only sends — inbound goes to `INotificationActionHandler`, the in-app
inbox is a separate query; YFex.UI `INotification` keeps its name). ⚑N2 = **option b**: the generic code send/check and Twilio
Verify move to `YFex.Notifications.Verification` (phase N1) so codes work without an account (contact-book confirmation, POS
customer phone, pickup codes, consent double opt-in); Authentication keeps only its challenge rules. Open: ⚑N6–⚑N13, ⚑N18, ⚑N19.

### 2026-11-09 — Phase N0 BUILT (repo `YFex.Notifications`, not committed)

- **Repo** `C:\Dev\YFex\YFex.Notifications` (`notifications.slnx`, `AGENTS.md`, README): `.Public` (leaf: core + YFex.Actors),
  `YFex.Notifications` (engine), `.Generator`, `.Email`, `.Testing`, tests, AOT smoke, benchmarks.
- **Moved (⚑N3):** `DeliveryChannel` (+ `Chat`, `Webhook`, `InApp` = `in-app`, `Push`; `Voice` kept), `ContactKind`, `ContactDestination`,
  `MaskedDestination`, `Delivered` / `DeliveryRefused` / `DeliveryFailed` / `union DeliveryResult`, `DeliveryRefusal` (+ 6–11). Removed from
  `YFex.Authentication.Public`; Authentication's `Directory.Build.props` adds `global using YFex.Notifications`, so its code and tests are unchanged
  apart from the ArchitectureTests allow-list.
- **Contracts:** `INotificationSender.SendAsync<TData>` (⚑N20) → `union SendResult(NotificationAccepted(Ids, Refused), InlineSent(Id, Result))`;
  `NotificationRequest<TData> where TData : INotificationData` with `Scope` (`TenancyPath`) and `Culture`; `Recipient.To/Actor/Reference`;
  `IContactSource` + `ContactLookup`; `INotificationChannel`, `IEmailProvider`, `RenderedMessage`, `ChannelCapabilities`, `ProviderContext`.
- **Engine:** sender (contact sources in registration order, `NoContact` / `NoSender` / `UnsupportedDestination` per recipient; inline = exactly
  one recipient and channel); renderer over core `MessageCatalog` layers with per-(key, channel) cached codes; public `INotificationOutbox` +
  in-memory outbox (idempotency key + recipient index + channel, per scope); hosted `NotificationDispatcher` (stored retries via
  `RetryDelay` 1 min → 6 h exponential, expiry 24 h measured on `GetTrustedNow()`; optional `IResiliencePipelineFactory` for timeout +
  breaker/limiter resource, never a retry); public `EmailChannel`; `INotificationEndpoints` (`ApiStoreNotificationEndpoints`, static), endpoint
  `notifications.email`.
- **Email:** `SmtpEmailProvider`, `HttpApiEmailProvider` (SendGrid v3 / SES v2, `text/html` part when `HtmlBody`), `NotificationsHttpClient`
  (no retry handler, no own timeout).
- **Authentication (05 §2.1–2.2):** `EmailChallengeSender(INotificationChannel, ChallengeTemplates)` renders OTP texts and sends inline;
  `SmtpChallengeSender` / `HttpEmailChallengeSender` keep their constructors on top of it (façade types `SmtpDeliveryOptions`,
  `EmailApiFormat` kept for apps). Twilio, Meta and Verify are untouched until N1.
- **Benchmark (⚑N15 decision):** `MessageCatalog.Render` 206 ns / 200 B (result string only) vs `MessageTemplate.Render` 102 ns / 584 B;
  full `NotificationRenderer` path 766 ns / 200 B. **MessageCatalog stays** (meets ≤ 1 µs and no allocation beyond the result).
- **Gates:** `notifications.slnx` Release 0 warnings; 11 tests green; AOT smoke win-x64 0 IL warnings, exe exits 0; Authentication 212 tests
  green (count unchanged), Authentication AOT smoke 0 IL warnings and green. **Not run:** linux-x64 AOT through `linuxKit`.
- **Environment:** the .NET 11 RC3 SDK (installed 2026-11-06) prunes `Microsoft.Extensions.*.Abstractions` package references (NU1510 as an
  error) in every repo, including untouched ones; builds above used `-p:WarningsNotAsErrors=NU1510`. Ecosystem-wide fix still open.
- **Deferred from the N0 list:** `Recipient.List` / `.Contact` (N2/N3), `ContactDestination` chat/webhook cases (N3), raw-text requests (N3),
  provider templates and actions in `RenderedMessage` (N1/N3), per-scope template overrides (N1), delivery log and diagnostics (N2).

