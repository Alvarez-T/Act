# 09 — Usage examples: one pharmacy, end to end

Every example has up to three parts:

- **✍ Dev writes**: what a developer actually types.
- **⚙ Generated**: what generators emit (for review; nobody edits it).
- **👁 Result**: what comes out in logs, the database, the UI, the AI prompt, the subject's package.

**📦 Framework** marks code written once inside YFex.Privacy. API not yet defined in 02–08 is marked **(new proposal)**: the setup
builder names in §1 (`AddYFexPrivacy`, `AddYFexPrivacyRights` and their sub-builders) are proposals to fold into 04/05/07 once approved.

## 0. What the developer decides

| Decision | Where | If you do nothing |
|---|---|---|
| Which data types are personal | `[Sensitive(...)]` once on the value object (`Cpf`, `Email`…) | raw members classified by convention, listed in the snapshot for review |
| Which raw members are false positives | `[NotPersonalData(Reason = …)]` | flagged in the snapshot |
| Purposes and legal bases | `[PurposeCatalog]` per module | commands listed as `purpose: unspecified` in the ROPA |
| Which commands need consent | `.Privacy(p => p.RequireConsent())` in the aggregate configuration | no consent check |
| Retention | `p.Retain(...)` per purpose/category (fiscal hold ships with YFex.Fiscal) | no automatic deletion; listed as a ROPA gap |
| Encryption at rest per entity | `d.Entity<T>().Privacy(p => p.Encrypt(...))` | `SensitivePersonal`/`Financial` encrypted; others clear + blind index for identifiers |
| Who may see clear values | permissions from `PrivacyPermissions` assigned to roles | masked for everyone without the permission |
| AI providers and what they may receive | `.Ai(a => a.Provider(...))` | cloud providers get placeholders only |
| Sharing with third parties | `.SharedWith(...)` | ROPA shows no recipients (review item) |

## 1. Setup

**✍ Dev writes** (server, `Program.cs`)

```csharp
builder.Services
    .AddYFexPrivacy(p => p                                                           // (new proposal)
        .Keys(k => k.Root(new EnvironmentRootKeyProvider("YFEX_PRIVACY_ROOT_KEY")).Vault<SqlKeyVault>())
        .Purposes(FarmaPurposes.All, FiscalPurposes.All, TelemetryPurposes.All)
        .Notices<SqlPrivacyNoticeStore>()
        .Consent(c => c.Ledger<SqlConsentLedger>().VerifyDeviceSignatures())
        .Scrubber(s => s.AddBuiltIn(BuiltInDetectors.All).Add<Cpf>().Add<Telefone>().Add<Rg>())
        .Retention(r => r.AddFiscalRules()                                            // shipped by YFex.Fiscal
                         .Retain(FarmaPurposes.Values.LoyaltyProgram, DataCategory.Contact)
                         .For(days: 30).From(RetentionTrigger.ConsentWithdrawn).Then(ErasureAction.Delete))
        .SharedWith("sefaz", "Secretaria da Fazenda", LegalBasis.LegalObligation, DataCategory.Identification)
        .Ai(a => a.Provider("anthropic", cloud: true, region: "US").Provider("claude-desktop", cloud: false)))
    .AddYFexPrivacyRights(r => r
        .Store<SqlPrivacyRequestStore>()
        .Participants(x => x.Data().EventSourcing().ConsentLedger().TokenVault().Audit().Devices().Brain()));
```

**✍ Dev writes** (Avalonia desktop / POS)

```csharp
services.AddYFexPrivacy(p => p
    .Keys(k => k.Root(new DpapiRootKeyProvider()).Vault<FileKeyVault>("%LOCALAPPDATA%/YFex/privacy-keys.json"))   // device keys only
    .Purposes(FarmaPurposes.All, TelemetryPurposes.All)
    .Consent(c => c.LocalLedger().SyncWithServer())
    .Scrubber(s => s.AddBuiltIn(BuiltInDetectors.All).Add<Cpf>().Add<Telefone>()));
services.AddYFexStorageEncryption(EncryptionKeySource.PrivacyVault);   // device store sealed with envelope v2
```

## 2. Classification (once, in YFex.Domain)

Shown in 02 §8. The Farma customer model changes type after the YFP0001 code fix (03 §4.2). The snapshot shows the result (03 §3).

## 3. Consent at the counter (offline)

**✍ Dev writes** (Caixa view model; the consent prompt control is phase 7)

```csharp
[StateCommand]
async Task AderirFidelidadeAsync(CancellationToken ct)
{
    var notice = await consent.GetNoticeAsync(FarmaPurposes.Values.LoyaltyProgram, ct);
    var answer = await dialogs.Show(new ConsentPrompt(notice, Cliente!.Value, confirmOn: ConsentConfirmation.PinPad), ct);
    if (answer is ConsentGiven given)
        await consent.RecordAsync(given.ToEntry(collectedBy: operador.Id), ct);   // local ledger + outbox; works offline
}
```

**👁 Result** (local ledger line, synced later; device signature verified by the server)

```json
{"entryId":"0b8f…","tenant":"farma-centro","subject":{"kind":"Person","subjectKind":"customer","value":"7f3a…"},
 "purpose":"marketing.loyalty","decision":"Granted","noticeId":"fidelidade","noticeVersion":3,
 "noticeHash":"5c1e…","channel":"PointOfSale","collectedBy":"op:joao","recordedAt":"2026-10-01T10:38:02-03:00",
 "deviceId":"caixa-03","evidence":"pinpad:sha256:91aa…","deviceSignature":"MEUCIQ…"}
```

```
AUDIT  10:38:02  Privacy.Consent.Granted  subject=#h:2b77e0… purpose=marketing.loyalty notice=fidelidade v3 channel=PointOfSale by=op:joao device=caixa-03
```

## 4. A sale with *CPF na nota*

**✍ Dev writes**: nothing privacy-related (`Venda.FinalizarCommand` in 03 §2.2; the aggregate's default purpose is `sale`, and the
fiscal step declares `fiscal.document`).

**👁 Result**

```
# diagnostic log (console)
10:40:16.300 INF Venda.Finalizar completed 48.0ms venda=V-88123 cliente=customer:#h:2b77e0… cpfNaNota=<cpf #h:9f2c41d07a3be1c2>
             total=152.40 itens=3 purpose=sale,fiscal.document consent=not-required

# SQL (venda)
id       | cpf_na_nota     | data_emissao
V-88123  | 12345678909     | 2026-10-01          (clear: ⚑P15 default for PersonalIdentifier; cliente.cpf_cnpj is encrypted + blind index by opt-in)

# event stream "venda"
EventRecord { Type: VendaFinalizada, Payload: { Id: V-88123, CpfNaNota: null, Total: 152.40, … },
              Sealed: 02 01 3e9a…(63 bytes, subject key 7f3a… v1), SubjectIndex: 2b77e0… }

# UI (Vendas grid, user without privacy.reveal.identifier)
V-88123   ***.456.789-**   R$ 152,40   👁
```

## 5. An exception that mentions a CPF

```
before (raw):   Npgsql.PostgresException: 23505: duplicate key value violates unique constraint "cliente_cpf_cnpj_key"
                DETAIL: Key (cpf_cnpj)=(12345678909) already exists.
after (log):    Npgsql.PostgresException: 23505: duplicate key value violates unique constraint "cliente_cpf_cnpj_key"
                DETAIL: Key (cpf_cnpj)=(<cpf #h:9f2c41d07a3be1c2>) already exists.
```

The pseudonym equals the typed one in §4, so "show me everything about this customer" finds both lines.

## 6. The assistant (cloud model)

**✍ Dev writes**: nothing. The capability `Vendas.Queries.Buscar` and the `RedactingAiAgent` decorator are configured in §1.

**👁 Result**: 08 §8.2. The model sees `[[CPF_1]]`, `[[EMAIL_1]]`, `[[PESSOA_1]]`. The tool call is restored before execution,
under the operator's authorization. Nothing above `Internal` leaves in clear.

## 7. Withdrawal from the portal

1. The customer opens the privacy portal (Blazor), logs in (`Authenticated`), and switches off "Programa de fidelidade".
2. A ledger entry `Withdrawn` is appended. The snapshot version increments. `IConsentQueries.GetSnapshot` is invalidated, and connected
   POS terminals refetch within seconds.
3. `AcumularPontosCommand` for this customer now returns `ConsentRequired(marketing.loyalty, Withdrawn)`. The cashier sees "Cliente
   não participa do programa de fidelidade".
4. The retention rule schedules deletion of the loyalty contact data in 30 days (`ConsentWithdrawn` trigger). The audit records both.

## 8. Access request

**👁 Result** (`subject.json`, excerpt)

```json
{
  "controller": { "name": "Farmácia Centro Ltda", "cnpj": "12.345.678/0001-95", "dpo": "privacidade@farmaciacentro.com.br" },
  "subject": { "kind": "customer", "identifiers": { "cpf": "123.456.789-09", "email": "joao.silva@acme.com.br" } },
  "stores": [
    { "store": "sql:farma", "type": "ClienteEntity", "records": [ { "nome": "João da Silva", "email": "joao.silva@acme.com.br", "telefone": "(11) 98765-4321" } ] },
    { "store": "sql:farma", "type": "VendaEntity", "records": 37, "sample": [ { "id": "V-88123", "data": "2026-10-01", "total": 152.40 } ] },
    { "store": "sql:farma", "type": "HistoricoMedicamentoEntity", "records": [ { "medicamento": "…", "data": "…", "prescritor": "Dr. ***" } ] }
  ],
  "consents": "see consents.json",
  "sharedWith": [ { "recipient": "Secretaria da Fazenda", "basis": "LegalObligation", "categories": ["identification"] } ]
}
```

Note `"prescritor": "Dr. ***"`: the prescriber is another data subject (07 §5).

## 9. Deletion request

The dry-run plan is in 07 §7.3. After execution the report says, in plain Portuguese generated from the plan: deleted (loyalty,
medication history), anonymized (customer record), **kept under legal obligation until 2031-01-01** (fiscal documents, with the
legal basis), consent proof kept until 2031-10-01, diagnostic logs expire by 2026-10-15, payment provider informed (manual task
closed by `op:maria`).

## 10. Tests

```csharp
[Fact] public void Privacy_schema_is_reviewed()                                     // 03 §3
    => PrivacySchemaSnapshot.Verify(YFexFarma.PrivacySchema.Text, YFexFarma.PrivacySchema.InventoryJson);

[Fact] public async Task Sale_never_logs_or_stores_clear_identifiers_outside_allowed_places()
{
    using var logs = LogCapture.Start();                                              // YFex.Logging.Testing
    var cpf = Cpf.Parse("123.456.789-09");
    await Venda.Commands.FinalizarAsync(new(VendaId.New(), cliente, CpfCnpj.From(cpf), itens, total, "cliente pediu nota"));

    logs.Records.ShouldNotContainPersonalData(cpf);                                   // YFex.Privacy.Testing: clear, formatted, digits-only
    logs.Records.ShouldContainPseudonymOf(cpf, PseudonymDomain.Log);
}

[Fact] public async Task Withdrawal_wins_over_an_older_grant_from_an_offline_device()
{
    var clock = new FakeTimeProvider(At("2026-10-01T10:00Z"));
    await ledger.AppendAsync(Grant(purpose: Loyalty, recordedAt: "10:04Z", device: "caixa-03"));    // device clock 3 min ahead
    await ledger.AppendAsync(Withdraw(purpose: Loyalty, recordedAt: "10:02Z", device: "portal"));
    (await ledger.GetSnapshotAsync(subject)).StatusOf(Loyalty).Should().Be(ConsentStatus.Withdrawn);   // 04 §5.2 rule 2
}

[Fact] public async Task Shredded_events_read_back_as_default()
{
    await vault.DestroySubjectKeyAsync(scope, subject, "PR-1");
    var events = await store.ReadAsync(stream).ToListAsync();
    events.Single().Event.CpfNaNota.Should().BeNull();
    events.Single().Flags.Should().HaveFlag(CommittedEventFlags.Shredded);
}
```
