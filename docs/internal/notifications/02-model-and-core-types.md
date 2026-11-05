# 02 — Model and core types

All sketches are `(sketch)`: names and members are proposals until N0 is built. Hot paths are the render and the dispatcher loop
(no LINQ, pooled buffers, no boxing); the API surface is cold. `INotifications` below may become `INotificationSender` (⚑N20).

## 1. Moved from YFex.Authentication.Public (⚑N3)

`DeliveryChannel`, `ContactDestination`, `MaskedDestination`, `Delivered` / `DeliveryRefused` / `DeliveryFailed` /
`union DeliveryResult`, `DeliveryRefusal` move **unchanged** to `YFex.Notifications.Public` (namespace `YFex.Notifications`). Changes
that come with the move:
- `DeliveryRefusal` gains `OptedOut = 6`, `RateLimited = 7`, `NoConsent = 8`, `NotLicensed = 9`, `Undeliverable = 10` (hard bounce /
  number not in service, set by inbound receipts), `NoContact = 11` (no contact source could resolve the recipient);
- `DeliveryChannel` gains the well-known `Webhook`, `Chat`, `InApp`, `Push`; apps still declare their own;
- `ContactDestination` gains a chat case (provider + chat id, e.g. Telegram) and a webhook case (endpoint name, never a raw URL).

`YFex.Authentication.Public` references `.Public` and uses the types from their new namespace (05 §2).

## 2. Channel, provider, capabilities (⚑N14, ⚑N18)

```csharp
// (sketch) YFex.Notifications.Public
public interface INotificationChannel                          // one per DeliveryChannel; built into YFex.Notifications
{
    DeliveryChannel Channel { get; }
    ValueTask<DeliveryResult> SendAsync(RenderedMessage message, CancellationToken cancellationToken);   // never throws for provider errors
}

public interface ISmsProvider                                   // Twilio, Zenvia, AWS SNS, GSM modem (.Devices)
{
    ProviderName Name { get; }
    ChannelCapabilities Capabilities { get; }
    ValueTask<DeliveryResult> SendAsync(PhoneNumber to, ReadOnlyMemory<char> text, ProviderContext context, CancellationToken ct);
}
// IEmailProvider (SMTP, HTTP API), IWhatsAppProvider (Meta, Twilio), IChatProvider (Telegram, Slack, Discord, Teams, ntfy),
// IPushProvider (FCM, APNs): same shape with their own message type.

public readonly record struct ChannelCapabilities(
    int MaxLength, bool RichText, bool Actions, bool Attachments, bool Receipts, bool Replies, decimal CostPerMessage);

public readonly record struct RenderedMessage(
    DeliveryChannel Channel, ContactDestination Destination, string ScopeText, string Culture,
    string? Subject, string Body, string? HtmlBody,
    ProviderTemplate? Provider,                                 // WhatsApp template name + language + parameters
    ImmutableArray<NotificationAction> Actions,                 // buttons / signed links (ack, open, unsubscribe), if Capabilities.Actions
    ImmutableArray<Attachment> Attachments,                    // references, not bytes (04 §4)
    NotificationId Id, string? IdempotencyKey);
```

- The **channel** owns what is the same for every provider: length limits and splitting, fallback text when a provider has no actions,
  WhatsApp 24-hour session rules, receipts mapping.
- The **provider** for a (scope, channel) is named by the scope's `ApiEndpoint` (`notifications.sms` → provider `twilio`, credentials,
  `From`). Switching provider is data. A scope that needs its own code registers a provider through **YFex.CustomLogic** (⚑N18).
- Logging 08 §5.3's `INotificationChannel` / `ISmsProvider` / `NotificationCapabilities` are replaced by these (05 §1).

## 3. Recipients (⚑N8)

```csharp
// (sketch)
public readonly record struct Recipient                         // [Union]-style tagged struct; cold path
{
    public static Recipient To(ContactDestination destination);  // a concrete e-mail / phone / chat id / webhook endpoint
    public static Recipient Actor(ActorIdentity actor);          // resolved through the contact sources (§4)
    public static Recipient Reference(string reference);        // an app id, e.g. Jobs' "user:42", "group:managers" → contact sources
    public static Recipient List(RecipientListKey list);         // stored per scope as data ("store managers of site 7")
    public static Recipient Contact(ContactId contact);          // a contact-book entry (§4)
}
```

A recipient list row is `(scope, list key, recipient, channels)`. Lists are edited at run time through `notifications.recipients.manage`.
The most specific scope wins, as Jobs per-scope settings and Hosting profiles do. Logging's on-call rotations resolve to recipients
**before** calling Notifications (Logging owns who is on call; Notifications owns how to reach them).

## 4. Contacts and contact sources (⚑N17)

Where an address comes from:

| Source | Who | Owner | How it gets here |
|---|---|---|---|
| **Account contacts** | people who sign in (staff, shoppers) | YFex.Authentication (`VerifiedContact`, verified by challenge) | bridge `YFex.Authentication.Notifications` implements `IContactSource` for `ActorKind.User` |
| **Contact book** | people or endpoints without an account, or channels Authentication does not verify: on-call phone, Telegram chat id, Slack/Teams webhook, accountant's e-mail, ANPD address | **YFex.Notifications** (`notifications.contacts.manage`) | edited in an admin screen; a new address is confirmed by a code/link sent through Notifications before use |
| **App directory** | customers, suppliers, patients (the app's own records) | the app (Farma customer, Fiscal NF-e recipient) | app implements `IContactSource`, or passes `Recipient.To(...)` directly for one-off sends (a receipt) |
| ~~Tenancy~~ | — | holds scopes, not people | not a source |
| ~~Profiling~~ | — | consent-scoped behaviour signals | not a source: using them as addresses breaks purpose limitation |

```csharp
// (sketch)
public interface IContactSource
{
    bool CanResolve(in Recipient recipient);                    // Actor of a kind, or a Reference prefix ("user:", "customer:")
    ValueTask<ContactLookup> FindAsync(Recipient recipient, ScopeIdentity scope, NotificationPurpose purpose, CancellationToken ct);
}
// ContactLookup = the reachable destinations + preferred channel; no source answers → DeliveryRefused(NoContact)
```

Per destination, **Notifications** keeps the preferences whatever the source: preferred channel (default: the source's, e.g.
`VerifiedContact.PreferredChannel`), quiet hours (in the scope's time zone, ⚑N16), opt-outs per purpose and channel, and the
`Undeliverable` flag set by inbound receipts. Preferences are keyed by the destination's keyed hash, so they survive a source change.

## 5. Messages and templates (⚑N4, ⚑N15)

```csharp
// (sketch)
public readonly record struct NotificationKey(string Value);    // app-declared, e.g. "jobs.instance.failed", "logging.incident.opened"
public enum NotificationPurpose : byte { Transactional = 0, Operational = 1, Marketing = 2 }   // ⚑N7: only Marketing needs consent

public sealed record NotificationRequest<TData>(
    NotificationKey Key, NotificationPurpose Purpose, ImmutableArray<Recipient> Recipients, TData Data,
    string? IdempotencyKey = null, Delivery Delivery = Delivery.Queued, ImmutableArray<DeliveryChannel> Channels = default,
    ImmutableArray<NotificationAction> Actions = default);

public enum Delivery : byte { Queued = 0, Inline = 1 }         // ⚑N5: Inline = send now and return the result (OTP SMS/WhatsApp)
```

- **Texts are message codes** in core `IMessageCatalog` (validation 10 §2): `{key}.{channel}.subject`, `{key}.{channel}.body`,
  falling back to `{key}.body`. Code defaults (pt-BR/en/es) are generated module catalogs; per-scope overrides are a dynamic catalog
  layer stored by Notifications (`notifications.templates.manage`). Rendering is `MessageCatalog.TryFormat` into a pooled span.
- Notifications adds only what a catalog does not hold: the HTML body and WhatsApp/provider template mapping.
- **Data** is read through a generated accessor (`[NotificationData]`, `YFex.Notifications.Generator`) that writes the members as a
  `RawValue` span — no reflection, no boxing, AOT-safe. `[Sensitive]` members are masked in the log (04 §4), never in the message.
- **Raw text** (`NotificationRequest.Text(...)`) is allowed for operator channels (chat, webhook). It is refused for `Marketing`.

## 6. Outbox record and result

```
outbox:  id (UUIDv7), scope, key, purpose, channel, provider, destination (protected), template + data (protected) or rendered message,
         idempotency key (unique per scope), attempt, next attempt at, claimed by + until, state {Pending, Sending, Sent, Delivered,
         Refused, Failed, Expired}, provider message id, created by (actor, real actor), created at
log:     id, scope, key, channel, provider, MaskedDestination, destination hash, state, provider code, timestamps, actor   ← no body
```

`INotifications.SendAsync` returns `NotificationAccepted(id)` for queued sends, or the `DeliveryResult` for inline sends.
`Sent` = the provider accepted it; `Delivered` / `Failed` / `Undeliverable` may follow from an inbound receipt (§7).

## 7. Inbound events (⚑N13)

```csharp
// (sketch) produced by YFex.Notifications.AspNetCore after verifying the provider signature or the signed link
public union InboundEvent(DeliveryReceipt, ActionTaken, ReplyReceived, OptOutRequested);

public sealed record DeliveryReceipt(NotificationId Id, string ProviderMessageId, ReceiptState State, string? ProviderCode);
public sealed record ActionTaken(NotificationId Id, NotificationKey Key, ActionId Action, MaskedDestination From, ActorIdentity? SignedInActor);
public sealed record ReplyReceived(DeliveryChannel Channel, MaskedDestination From, NotificationId? InReplyTo, string Text);
public sealed record OptOutRequested(DeliveryChannel Channel, MaskedDestination From, NotificationPurpose Purpose, OptOutOrigin Origin);

public interface INotificationActionHandler                      // registered by the consumer, per NotificationKey
{
    ValueTask<ActionOutcome> OnActionAsync(ActionTaken action, CancellationToken ct);    // e.g. Logging: ack the incident
}
public interface INotificationReplyHandler                       // optional, app; none → reply logged and dropped (auto-answer text configurable)
{
    ValueTask OnReplyAsync(ReplyReceived reply, CancellationToken ct);
}
```

Receipts and opt-outs are handled by Notifications itself; actions and replies go to the handler registered for the key. An action
is never authority on its own: a handler that changes state (ack, approve) checks the actor (signed link subject, or a signed-in
session) through Authorization; approvals always open the approval screen instead of approving from the message.

## 8. In-app (⚑N10, phase N3)

The in-app channel writes an inbox row `(recipient actor, scope, key, rendered title/body, actions, read at)` and pushes an
`IEventChannel<InboxChanged>` event to the actor's connected clients. `Notifications.Queries.Inbox` and `MarkRead` are Cqrs messages.
On the client, `YFex.Notifications.InApp.Client` turns `InboxChanged` into YFex.UI `INotification` (persistent, with actions) or
`IToast` (transient) by purpose and key. The notification-centre view over the inbox query is YFex.UI work after N3.
