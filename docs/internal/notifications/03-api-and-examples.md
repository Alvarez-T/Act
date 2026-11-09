# 03 — API and examples (dev-side code first)

All `(sketch)`. Syntax follows the current ecosystem: builder lambdas on `Add…`, typed keys, records with generated accessors.

## 1. Register channels and providers (composition root)

```csharp
// central (ASP.NET Core app) — which provider a scope uses, and its credentials, come from YFex.Web's IApiEndpointStore (⚑N18)
builder.Services.AddYFexNotifications(n => n
    .UseDataOutbox()                                       // YFex.Notifications.Data (central); hosts: .UsePersistenceOutbox()
    .Email(e => e.Smtp().HttpApi(EmailApiFormat.SendGrid)) // IEmailProvider implementations
    .Sms(s => s.Twilio())                                  // ISmsProvider; later .Zenvia(), .GsmModem() (YFex.Notifications.Devices)
    .WhatsApp(w => w.Meta().Twilio())                      // IWhatsAppProvider
    .Chat(c => c.Telegram().Slack().Teams().Ntfy())        // phase N3
    .Webhook()                                             // HMAC-signed, phase N3
    .InApp()                                               // Messaging.Rpc events + inbox, phase N3
    .Contacts(c => c.FromAuthentication().From<CustomerContactSource>())   // ⚑N17, order = lookup order; contact book is always last
    .Limits(l => l.PerScope(DeliveryChannel.Sms, perMinute: 30, dailyBudget: 500))
    .Licensing(l => l.UseUsageMeter().GateModule(DeliveryChannel.WhatsApp, "messaging.whatsapp")));   // ⚑N19, optional

app.MapYFexNotificationsInbound();                         // YFex.Notifications.AspNetCore: /notifications/inbound/{provider}, /n/{signed}
```

## 2. Send

```csharp
[NotificationData]
public sealed partial record JobFailedData(string JobName, string Code, int Count, DateTimeOffset Since, string Link);

await notifications.SendAsync(new NotificationRequest<JobFailedData>(
    JobNotificationKeys.InstanceFailed,                    // NotificationKey("jobs.instance.failed")
    NotificationPurpose.Operational,
    [Recipient.Reference(notice.Owner!), Recipient.List(new("jobs.reports.recipients"))],
    new JobFailedData(notice.InstanceName, notice.Code, notice.Count, notice.FirstUtc, link),
    IdempotencyKey: $"{notice.InstanceKey}:{notice.Kind}:{notice.Code}:{notice.FirstUtc:O}:{notice.Count}"), ct);
```

## 3. Templates (message codes in core YFex.Localization, ⚑N15)

```csharp
// code defaults: messages.json / messages.pt-BR.json as AdditionalFiles → generated <Module>Messages : IMessageSource
// (today's generator: YFex.Validation.Generator/MessagesGenerator.cs, validation 10 §3; it may move to a core generator, ⚑N15)
// messages.pt-BR.json in YFex.Jobs.Templates.Notifications:
// { "jobs.instance.failed.email.subject": "Falha no job {0}",
//   "jobs.instance.failed.body": "O job {0} falhou {2} vezes desde {3:g} ({1}). Veja: {4}" }
n.ProviderTemplates(t => t.WhatsApp(JobNotificationKeys.InstanceFailed, "job_falhou", "pt_BR", args: [0, 2]));

// run-time override per scope (admin screen, permission notifications.templates.manage) = a dynamic catalog layer
await templates.SetAsync(JobNotificationKeys.InstanceFailed, DeliveryChannel.Email, "pt-BR", scope: site7, subject: "…", body: "…", caller, ct);
```

## 4. Contacts (⚑N17)

```csharp
// contact book: someone without an account (admin screen, notifications.contacts.manage)
var id = await contacts.AddAsync(scope: tenant, name: "Plantão TI", ContactDestination.Telegram(chatId), caller, ct);
// → a confirmation message goes to the chat; the contact is usable after the code is entered (or the bot button pressed)

// app directory: customers are the app's data
public sealed class CustomerContactSource(ICustomers customers) : IContactSource
{
    public bool CanResolve(in Recipient r) => r.IsReference("customer:");
    public async ValueTask<ContactLookup> FindAsync(Recipient r, ScopeIdentity scope, NotificationPurpose p, CancellationToken ct) => …;
}
```

## 5. Inbound (⚑N13)

```csharp
// Logging registers what an "ack" does for its key; Notifications verifies the link/button and calls it
n.OnAction(LoggingNotificationKeys.IncidentOpened, "ack", sp => sp.GetRequiredService<IncidentAckHandler>());
// an app that wants customer replies (support inbox); without it, replies are logged and dropped
n.OnReply<SupportInboxReplyHandler>(DeliveryChannel.WhatsApp);
```

## 6. Authentication after migration (N1)

```csharp
// unchanged for apps: the façade registers Notifications providers and keeps OTP rules
builder.Services.AddYFexAuthenticationDelivery(d => d
    .Email(e => e.Smtp(smtp))                 // YFex.Notifications.Email underneath
    .Sms(s => s.Twilio(twilio))               // YFex.Notifications.Twilio (ISmsProvider)
    .WhatsApp(w => w.Meta(meta)));            // YFex.Notifications.Meta (IWhatsAppProvider)
// IChallengeSender adapters render OTP texts and call the channel with Delivery.Inline; codes come from YFex.Notifications.Verification (Twilio Verify is one of its providers, ⚑N2).
```

## 7. Jobs bridge (D2a seam, built)

```csharp
// YFex.Jobs.Templates.Notifications — implements IJobNotifier, nothing else
jobs.AddTemplates(t => t.UseNotifications());
// JobNotificationKind.Failure / DeadLetter / Recovered → jobs.instance.failed / .dead-letter / .recovered;
// Code, Count, FirstUtc, InstanceName are data; Owner + Recipients (app ids) → Recipient.Reference; Jobs keeps ⚑J28.
```

## 8. Logging alerts (phase N3)

```csharp
// YFex.Logging.Alerts — routing + on-call rotation decide who and which channels; Notifications sends
await notifications.SendAsync(new NotificationRequest<IncidentData>(LoggingNotificationKeys.IncidentOpened, NotificationPurpose.Operational,
    route.Recipients, data, IdempotencyKey: $"{incident.Id}:{incident.Level}:{escalationStep}", Channels: route.Channels,
    Actions: [NotificationAction.Signed("ack", expires: TimeSpan.FromHours(4))]), ct);
```

## 9. Approvals (bridge in the approval owner, phase N3)

```csharp
// YFex.Authorization.Notifications — listens to ApprovalEvents; the link opens the approval screen, it never approves
await notifications.SendAsync(new NotificationRequest<ApprovalRequestedData>(ApprovalNotificationKeys.Requested, NotificationPurpose.Transactional,
    [Recipient.List(new("approvals.discount.approvers"))], data, IdempotencyKey: request.Id.ToString(),
    Channels: [DeliveryChannel.InApp, DeliveryChannel.Push]), ct);
```

## 10. Orchestration: wait for an answer

```csharp
// a saga step sends, then durably waits for the ack action (or escalates on timeout)
await ctx.Send(notifyStoreManager);
var ack = await ctx.WaitFor<ActionTaken>(a => a.Id == sent.Id, timeout: TimeSpan.FromMinutes(30));   // Orchestration WaitFor
```

## 11. From a terminal (offline, ⚑N6)

```csharp
// a POS app: same call; the terminal's INotifications is the queuing client — the request becomes an IQueueable command to central
await notifications.SendAsync(receiptRequest, ct);   // returns NotificationAccepted; central sends when the Offline outbox replays
// IOutboxCallerCapture carries the terminal's caller; central records it as actor
```
