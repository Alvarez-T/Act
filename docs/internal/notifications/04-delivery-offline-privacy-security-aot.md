# 04 — Delivery, inbound, offline, privacy, security, AOT

## 1. Outbox, dispatcher, retries (⚑N5)

- `SendAsync` validates the request and resolves recipients through the contact sources (02 §4). Then, in order: consent and opt-out,
  the license gate (⚑N19), the limits. The request is written to the outbox **once per (recipient, channel)**, with the idempotency
  key. Same key in the same scope = the first record wins; the caller gets the same `NotificationId`.
- The dispatcher is a hosted service. It **claims** due records itself — no Jobs lease exists (Jobs' lease is single-process; Audit chose
  `pg_try_advisory_lock`). `.Data` claims with `UPDATE … WHERE id IN (SELECT … FOR UPDATE SKIP LOCKED)` setting `claimed by/until`;
  `.Persistence` is single-process over `IDurableQueue<T>`.
- **One retry owner:** the dispatcher calls the channel through a **YFex.Resilience** pipeline (`IResiliencePipelineFactory`): timeout
  10 s, retry on `DeliveryFailed(retryable)` with backoff 1 min → 6 h, expiry after 24 h (per key, measured on `ITrustedClock`), and a
  circuit breaker per (provider, scope) resource. Provider HTTP clients are `YFex.Web.Refit` endpoint clients whose `UseResilience` spec has
  **no** `Retry` (the dispatcher owns retries) and names a resource whose rate limiter is set from the provider's documented limit. Authentication's bare
  `DeliveryHttpClient` is retired.
- Inline sends (`Delivery.Inline`) call the channel directly (still through the breaker) and write the log. Authentication's OTP uses them.
- The dispatcher does **not** run as a YFex.Jobs job (a job would poll, add latency and fill the job history, A2 §10). A Jobs job does
  the **retention** of the log (phase N5).

## 2. Inbound (⚑N13)

### 2.1 What "inbound" is

Everything that comes **back** after a send. Four kinds, one receiver (`YFex.Notifications.AspNetCore`, central only):

| Kind | Comes from | Verified by | Handled by |
|---|---|---|---|
| **Delivery receipt** | provider status callback (Twilio status callback, Meta WhatsApp `statuses`, SendGrid/SES event webhook, FCM error) | the provider's own signature (Twilio `X-Twilio-Signature`, Meta `X-Hub-Signature-256`, SendGrid signed events) — protocol-mandated (cryptography K3 P-class), kept next to each provider's parser | Notifications: outbox/log state, `Undeliverable` flag on the destination |
| **Action** | a button in chat (Telegram `callback_query`, Slack/Teams interactivity) or a **signed link** (`/n/{token}`) in e-mail/SMS/in-app | provider signature / Telegram secret-token header; signed link = YFex.Cryptography token with purpose, notification id, destination hash, expiry on `ITrustedClock` | the consumer's `INotificationActionHandler` for that key |
| **Reply** | a person answers an SMS / WhatsApp / chat message | provider signature | the app's `INotificationReplyHandler`, else logged (masked) and dropped, optional auto-answer |
| **Opt-out** | keyword reply (`SAIR`, `PARAR`, `STOP`), unsubscribe link, `List-Unsubscribe` one-click (RFC 8058) | as above | Notifications: opt-out list + a withdrawal entry in Privacy's consent ledger (⚑P9) when built |

### 2.2 Scenarios

1. **Ack an incident from Telegram.** Logging sends "CPU 98 % on central-02" to the on-call chat with an **[Ack]** button. The on-call
   person taps it → Telegram calls the bot webhook → Notifications checks the secret-token header and the chat id against the contact
   book → `ActionTaken(key logging.incident.opened, action ack)` → Logging's handler acks the incident and **stops escalation** → Audit
   records "ack by Plantão TI via Telegram". The bot edits the message to "✔ acked by João 02:14".
2. **SMS not delivered → escalate.** The SMS to the lead is accepted by Twilio (`Sent`), then Twilio's status callback says
   `undelivered 30003` → log becomes `Failed` with the provider code; the destination is not flagged (temporary). Logging sees the receipt
   (query or event) and escalates to the next channel without waiting for its own timeout.
3. **E-mail hard bounce.** The DANFE e-mail to `cliente@exemplo.com` bounces (SendGrid event `bounce`, type hard) → the destination is
   flagged `Undeliverable`; the next send to it is refused at once with `DeliveryRefused(Undeliverable)`, and the app can ask the
   cashier to correct the address. If the address is an account contact, the Authentication bridge is told (it may mark it unusable).
4. **Customer opts out on WhatsApp.** A pharmacy sends a refill reminder (`Marketing`). The customer answers "PARAR" → `OptOutRequested`
   → opt-out for (destination, WhatsApp, Marketing) + consent withdrawal entry → the next reminder is refused with `OptedOut`. Order and
   fiscal messages (`Transactional`) still go.
5. **Unsubscribe link in an e-mail.** Every `Marketing` e-mail carries `List-Unsubscribe` + a signed link. Gmail's one-click POST hits
   `/n/{token}` → same as scenario 4 for e-mail, no sign-in needed (the token is single-purpose and expires in 60 days).
6. **Approval request.** A cashier asks for a discount above the limit; Authorization raises an approval request; its bridge sends an
   in-app + push notice to the managers list. The manager taps **Open** → the app opens the approval screen; **signing in and step-up still
   apply**. The message never approves by itself (an approval token is issued only by `ServerApprovalTokens`).
7. **Customer replies with a question.** "Quando chega meu pedido?" on the store's WhatsApp → `ReplyReceived(InReplyTo = order-shipped
   notice)` → the app's support-inbox handler creates a ticket; the reply also opens WhatsApp's 24-hour window, so the answer may be free
   text instead of a template. Without a handler: logged masked, auto-answer "Este número não recebe mensagens" if configured.
8. **Saga waits for confirmation.** An Orchestration step sends "confirm receipt of transfer #81" to the destination store manager and
   `WaitFor<ActionTaken>` 30 min; on timeout the saga escalates to the regional manager.

### 2.3 Rules

- Inbound runs **only at central** (public HTTPS endpoint); sites and terminals never expose one. A site that sends through a local GSM
  modem polls the modem for receipts and replies and forwards them to central as `IQueueable` commands.
- Every inbound event is idempotent by (provider, provider event id); providers retry callbacks.
- An unknown or unverifiable callback → `401`, counted on the meter, no detail in the response.
- An action changes state only through the consumer's handler, which checks the actor with Authorization. A signed link proves
  "someone holding this message", not "this user": state-changing actions on a link require sign-in unless the handler declares the
  action safe for link holders (ack yes, approve never).

## 3. Where sending runs (⚑N6, offline 07 §3)

| Place | Behaviour |
|---|---|
| central | outbox + dispatcher + inbound endpoints; credentials in the endpoint store |
| a host with internet and the module `notifications.dispatcher` in its profile (YFex.Hosting, planned; phase N2+) | outbox + dispatcher for a site that sends locally (e.g. the store's own WhatsApp number or a GSM modem); receipts forwarded to central |
| terminal / broker | `INotifications` is a **client**: the request is an `IQueueable` Cqrs command (`Notifications.Commands.Send`) through the Offline outbox (`IOutboxCallerCapture` carries the caller); never credentials on the PC |

## 4. Privacy (YFex.Privacy, core `[Sensitive]`)

- Destinations are personal data. The outbox **protects** them (`AesGcm` through YFex.Privacy, like sealed fields). The log keeps only
  `MaskedDestination` + a keyed hash for lookups (authentication's `DestinationKey`, 12 §2). Rendered bodies and reply texts are not logged.
- `[Sensitive]` members of the data record are masked in any diagnostic output. Attachments are **references** (a YFex.Files path once
  Files exists — planned; until then a Persistence/Data blob id), read at send time, never copied into the outbox.
- **Privacy participant** (privacy 07 §4, phase N5): export lists a subject's deliveries, contact-book entries and preferences (masked).
  Erasure deletes their inbox rows, pending outbox rows and contact-book entries, and pseudonymizes log rows. Recipient lists drop the contact.
- **Consent (⚑N7):** `Marketing` needs a purpose consent from Privacy's ledger (⚑P9, **not built**) and no opt-out. Until the ledger
  exists, marketing is refused (`NoConsent`) unless the app registers an `IMarketingConsent` seam. `Transactional` / `Operational`
  bypass consent but honour a channel opt-out where the law allows (never for security notices).

## 5. Limits, licensing, time (⚑N9, ⚑N16, ⚑N19)

- **Rate** per (scope, channel): a YFex.Resilience resource (`RateLimiterPolicy`, `PerScope = true`, built) → `RateLimited`.
- **Budget** per (scope, channel): the generic YFex.Resilience **quota** (⚑N9; counter per key, Tenancy scope and period on
  `ITrustedClock`, durable shared store) → `BudgetExhausted`. Notifications only declares the quotas; `CostPerMessage` from the
  provider's capabilities is the amount consumed. The same quota serves AI spend and Authentication's OTP budget.
- Authentication's stricter OTP rules (country allow-list, per-destination cooldowns, SMS budget) stay in Authentication and run
  **before** the channel call. Generic limits run after.
- **Time:** "day", "quiet hours" and every expiry use `ITrustedClock` (core contract, YFex.Time engine) — a wrong PC clock must not
  reset a budget or revive an expired link. The scope's time zone comes from a shared `IScopeTimeZones` seam (proposed for core/Tenancy,
  adapted from Jobs' `IJobScopeTimeZones`); until it exists, a per-scope Notifications option. Quiet hours delay `Operational` and
  `Marketing` sends to the window's end; `Transactional` and critical alerts ignore them.
- **Licensing (optional seam; concepts owned by YFex.Licensing, ⚑N19):** each send adds to meter `notifications.{channel}.sent` (`IUsageMeter`, licensing 07: totals only, no
  person); a channel can be tied to a license module (licensing 06) → `DeliveryRefused(NotLicensed)`. Service actors are masked by the
  license like any other (licensing 06 §1). Default seam: no meter, no gate. Meters never block; cost blocking is the quota above.
- Webhook channel: HMAC-SHA256 signature over the body with a per-endpoint secret (YFex.Cryptography); no redirects; an allow-list of hosts.

## 6. Security and permissions (⚑N12)

- Permissions (`[PermissionCatalog]`): `notifications.send` (direct API use by people; library consumers call with their own service
  actor), `notifications.templates.manage`, `notifications.contacts.manage`, `notifications.recipients.manage`, `notifications.log.view`.
- Background senders (Jobs, alerts) send as the stored or service actor (authorization 10, run-as; actor types in core after ⚑RA8). The
  log and Audit record actor and real actor.
- Every send, inbound action, opt-out and template/contact/list change is a YFex.Audit fact (`notifications.sent`,
  `notifications.action`, `notifications.opted-out`, `notifications.template.changed`, …). Bodies are never audited.
- Signed links (ack, open, unsubscribe) use YFex.Cryptography with expiry on `ITrustedClock` and a single purpose (§2.3).

## 7. Performance and AOT

- Render: `MessageCatalog.TryFormat` into a pooled span with `RawValue` args from the generated accessor; no `string.Format`, no
  reflection. Benchmark: render ≤ 1 µs and 0 allocations beyond the result string for a 5-placeholder template (compared with
  Authentication's `MessageTemplate`; the faster one stays, ⚑N15).
- Adapters and inbound parsers use `System.Text.Json` source-generated contexts. `SmtpClient` (BCL) for SMTP; HTTP through
  `YFex.Web.Refit` endpoint clients.
- Gates: AOT smoke (win-x64, linux-x64 through `linuxKit`) with 0 IL/AOT warnings: send through each provider against a fake server and
  replay a recorded callback of each provider through the inbound endpoint.
