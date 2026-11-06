# 03 — Scenarios and examples (dev-side code first)

Every example uses the API of 02. "Today" lines quote the current code or design; nothing below exists yet.

## 1. PDV / POS (Farma, retail)

### 1.1 Shift: the operator signs in, the terminal knows who sells

```csharp
// sign-in (Authentication) → the desktop session actor (only Authorization's accessor writes it)
ActorContext.SetSession(ActorScope.Of(ActorIdentity.User(account.Id, account.Home), session.Id) with { Origin = installation.Id });

// POS view-model: no more hard-coded "Operador 01" (POSViewModel.cs:78)
OperatorName = (await actors.DescribeAsync(ActorContext.Session!.Actor, ct)).Name;   // "Maria Souza"

// cash register opening/closing record
public sealed record CashShift(ShiftId Id, ActorStamp Opened, ActorStamp? Closed, Money OpeningFloat);
```

### 1.2 Supervisor override (cancel item, discount above limit, cash withdrawal)

The cashier stays the actor; the supervisor is the **approver**, recorded as an actor, not as a string (approvals today:
`string Approver`).

```csharp
ApprovalToken token = await approvals.RequestAsync(Permission.Of("sales.cancel-item"), digest, ct);   // PIN/badge of the supervisor
// token.Approver is ActorIdentity.User("2b91…") after AC1
audit.Record("sale.item-cancelled", target: sale.Id, approvedBy: token.Approver);
// history screen: "Maria Souza cancelou o item 3 — aprovado por Carlos Lima (supervisor)"
```

### 1.3 Self-checkout or kiosk: the device is the actor

```csharp
using (ActorContext.Enter(ActorScope.Of(ActorIdentity.Device(installation.Id, installation.Home)) with { Origin = installation.Id }))
    await sales.FinalizeAsync(cart, ct);
// Licensing counts device actors separately from user seats; Audit shows "Autoatendimento 2 — Loja 7"
```

### 1.4 Offline sale, replayed hours later at central

```csharp
// at the terminal: the envelope carries the actor in canonical bytes (one encoder, 02 §5)
var envelope = OfflineEnvelope.Create(command, ActorContext.Capture(), ...);

// at central, replay restores the original actor — not the replaying service
using (ActorContext.Restore(envelope.Actor))
    await dispatcher.SendAsync(envelope.Command, ct);
// Audit: actor = Maria (offline at 14:02), real actor = none; replay is metadata (Offline provenance), not an actor
```

### 1.5 Pharmacy specifics: the pharmacist who dispensed a controlled drug

```csharp
public sealed record ControlledDispensation(PrescriptionId Prescription, ActorReference Pharmacist, ActorStamp Recorded);
// Pharmacist may differ from the cashier who rang the sale; both are actors, names come from the directory on the SNGPC report
```

## 2. ERP / back office

### 2.1 Created / changed by on every document (⚑AC10)

```csharp
[DataRow("purchase_orders")]
public sealed partial class PurchaseOrderRow
{
    public required PurchaseOrderId Id { get; init; }
    [ActorStamps] public ActorStamp Created { get; set; }      // YFex.Data generator fills on insert
    [ActorStamps] public ActorStamp Changed { get; set; }      // … and on every update, from ActorContext.Current
}
// grid column "Alterado por": DescribeManyAsync over the page's 50 rows — one batched lookup, cached
```

### 2.2 Workflow: requested by, approved by, assigned to

```csharp
public sealed record ExpenseClaim(ClaimId Id, ActorReference RequestedBy, ActorReference? AssignedTo, ActorStamp? Approved);

// "my inbox": claims assigned to whoever is signed in
var mine = await claims.Where(c => c.AssignedTo == ActorContext.Current!.Actor).ToListAsync(ct);

// reassign to a service actor (an integration closes it automatically)
claim = claim with { AssignedTo = ActorIdentity.Service("expense-ocr") };
```

### 2.3 Month-end close run by a scheduled job as the controller

```csharp
// Jobs D2b: instance created by the controller, runs nightly as them (authorization 10 §2.3)
using (ActorContext.Enter(ActorScope.Delegated(actor: owner, by: instanceKey.ToActor(instance.Home))))   // JobKey (⚑AC14)
    await closing.RunAsync(period, ct);
// every entry posted: Created = (owner, real actor Job:month-close#7); Audit relation = Delegation
```

### 2.4 Support fixes a tenant's data while impersonating

```csharp
using (ActorContext.Enter(ActorScope.Impersonated(actor: tenantAdmin, by: ActorIdentity.User(supportId, TenancyPath.Parse("0=yfex")))))
    await orders.FixStatusAsync(id, ct);
// stamp shows the admin; Audit and the record's RealActor show "Suporte YFex (Ana) como Admin Farmaviva"
```

## 3. Files and partner Exchange

### 3.1 A partner drops a file on SFTP

The partner is an organisation, not a person: a `Service` actor named after the store (⚑AC5).

```csharp
var partner = ActorIdentity.Service("partner/acme");                       // directory name: "ACME Distribuidora"
using (ActorContext.Enter(ActorScope.Of(partner)))
    await exchange.ProcessAsync(file, ct);
// imported price list rows: Created = partner; real actor = Job:files.exchange.poll when polled by a job (Delegated)
```

### 3.2 A user uploads a file in the app

```csharp
var stored = await files.PutAsync(store, path, stream, new FileMetadata { UploadedBy = ActorContext.Current!.Actor }, ct);
// listing: "Enviado por João Silva há 2 h"; erased uploader: "removido (#h:3f2a)"
```

## 4. Notifications

### 4.1 Send to an actor, not to an address

```csharp
await notifications.SendAsync(NotificationKey.Of("approval.requested"),
    recipients: [Recipient.Actor(approver)],                   // was Recipient.Reference("user:42")
    data: new { Requester = ActorContext.Current!.Actor, Amount = claim.Total }, ct);
// contact sources resolve the actor's e-mail/WhatsApp; the template renders the requester through the directory
```

### 4.2 "Sent by" and replies

```csharp
// outbox row keeps the sender as a snapshot (sent later, from another process)
outbox.Add(message with { Sender = ActorContext.Capture() });
// an inbound reply "APROVO" from the approver's phone is mapped back to the approver actor by the contact source
```

## 5. Integrations, webhooks, marketplaces

```csharp
// marketplace webhook: a service actor, masked by Licensing (licensing 06 §1) and limited by its own rate
app.MapPost("/hooks/marketplace", async (Order o, CancellationToken ct) =>
{
    using (ActorContext.Enter(ActorScope.Of(ActorIdentity.Service("marketplace/mercado-livre", tenant))))
        await orders.ImportAsync(o, ct);
});
```

## 6. AI agents

```csharp
// the stock assistant reorders on behalf of the buyer who asked
using (ActorContext.Enter(ActorScope.OnBehalf(person: buyer, agent: ActorIdentity.Agent("stock-assistant"))))
    await capabilities.InvokeAsync("purchase.create", args, ct);
// Audit: "Assistente de estoque, para Paulo Lima, criou o pedido 881"; a NeedsReview flag can key on Kind == Agent (audit Q13)
```

## 7. Platform work

```csharp
using (ActorContext.Enter(ActorScope.Of(ActorIdentity.System("migrations"))))
    await migrator.RunAsync(ct);
// never anonymous: rows touched by a migration say "Sistema (migrações)"
```

## 8. Privacy: erasure of a person

```csharp
// Privacy erases account 8f3a: Authentication's name source then returns ActorState.Erased
ActorDisplay d = await actors.DescribeAsync(ActorIdentity.User("8f3a…"), ct);   // ("removido (#h:3f2a)", Erased)
// no record, stamp, audit chain or log line held the name, so nothing else changes (audit 09 §2)
```

## 9. Logs and traces

```csharp
log.SaleFinalized(sale.Id, ActorContext.Current!.Actor);   // ILogValue: "user:8f3a@0=farmaviva/1=loja7"
// the log viewer shows the name through the same directory, so logs stay free of personal data
```

## 10. Tests

```csharp
using var _ = TestActors.As(ActorIdentity.User("u1"), sessionScope: TenancyPath.Parse("0=t1"));     // YFex.Actors.Testing
using var __ = TestActors.Directory(("user:u1", "Test User"));
await sut.CancelAsync(saleId);
audit.Last.Actor.Should().Be(ActorIdentity.User("u1"));
// no xUnit collection needed: TestActors.As enters an AsyncLocal scope instead of the process session
```

## 11. What needs which part

| Scenario | Types | Text/bytes | Context capture | Stamp | Directory |
|---|:-:|:-:|:-:|:-:|:-:|
| PDV shift, operator name | ✅ | | | ✅ | ✅ |
| Supervisor override | ✅ | ✅ (token) | | | ✅ |
| Kiosk device | ✅ | | | | ✅ |
| Offline sale replay | ✅ | ✅ | ✅ | | |
| ERP stamps, inbox, workflow | ✅ | ✅ (column) | | ✅ | ✅ |
| Month-end job run-as | ✅ | | ✅ | ✅ | ✅ |
| Files partner / upload | ✅ | ✅ | | ✅ | ✅ |
| Notifications | ✅ | ✅ | ✅ | | ✅ |
| Webhooks, agents, platform | ✅ | | | ✅ | ✅ |
| Erasure, logs | ✅ | ✅ | | | ✅ |
