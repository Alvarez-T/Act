# 12 — Use-case catalog: every scenario, what you write, what you get

This is the catalog of situations YFex.Privacy is built for, grouped by who meets them. Each entry is short:

- **Situation**: who does what, and why privacy matters there.
- **✍ Dev writes**: the code a developer types (often nothing).
- **⚙ Generated / 📦 Framework**: what generators emit or what the library does (only when it helps to see it).
- **👁 Result**: what the user, operator, auditor, log reader or data subject sees.

The running example is a pharmacy chain (YFex.Farma) on the multi-tenant server, with Avalonia POS terminals that work offline. The
long end-to-end walkthrough is in [09-usage-examples.md](09-usage-examples.md). Here every scenario stands alone. Audit entries are
written by **YFex.Audit** (`../audit/`), which consumes the privacy primitives (audit 08). API names follow 02–08. Builder names marked
*(new proposal)* in 09 §1 apply here too.

> **Tiers (00 §3):** groups A2–A5, A8–A10 (analyzers, conventions, CLI) are **Tier 3** (optional). A1, A6, B, D1–D2 are **Tier 1**.
> C, E, F, G, H are **Tier 2** (built with the feature that needs them). For examples grounded in real pharmacy features and their legal
> sources, see [13-pharmacy-usage-examples.md](13-pharmacy-usage-examples.md).

## Index

| # | Scenario | Who | Parts used |
|---|---|---|---|
| **A** | **Development time** | developer, reviewer | |
| A1 | Classify a new value object | developer | `[Sensitive]`, generator |
| A2 | A raw `string Cpf` slips into a command | developer | YFP0001 + code fix |
| A3 | Someone adds `string Senha` to a record | developer, reviewer | YFP0002/0005, snapshot diff |
| A4 | Silence a false positive | developer | `[NotPersonalData]` |
| A5 | Classify a type you do not own | developer | `[assembly: SensitiveType<T>]` |
| A6 | Health data needs a special category | developer | YFP0012/0013 |
| A7 | One member, two sensitivities (CPF or CNPJ) | developer | unions |
| A8 | Hand-rolled hashing of personal data | developer | YFP0007 |
| A9 | CPF interpolated into an exception message | developer | YFP0006 |
| A10 | "Where do we store CPF?" and the ROPA in CI | DPO, architect | CLI, inventory |
| **B** | **Seeing data on screen** | operators | |
| B1 | Cashier sees a masked CPF; manager reveals it | cashier, manager | presenter, reveal audit |
| B2 | A user without permission queries customers | back-office user | server-side omission |
| B3 | Pharmacist opens medication history (sensitive) | pharmacist | justification + audit |
| B4 | Support staff impersonate a tenant | vendor support | Tenancy + reveal policy |
| **C** | **Consent** | customer, operator, app user | |
| C1 | Customer joins the loyalty program at the counter, offline | cashier, customer | ledger, PIN pad, device signature |
| C2 | A command needs consent the customer never gave | cashier | `RequireConsent`, `ConsentRequired` |
| C3 | The legal-obligation path never asks (CPF na nota) | cashier | `LegalBasis.LegalObligation` |
| C4 | Privacy notice changes materially | customer | `MaterialChange` → re-consent |
| C5 | Customer withdraws consent in the portal | customer | withdrawal propagation |
| C6 | Marketing consent expires after 24 months | system | `ConsentValidity` |
| C7 | A minor joins the program | guardian | `Guardian` entry |
| C8 | Customer objects to a legitimate-interest purpose | customer | objection |
| C9 | Desktop app first run: telemetry choices | app user | Profiling purposes + presets |
| C10 | Employer enables device monitoring | tenant admin | Security purposes, basis override |
| **D** | **Logs, audit, telemetry** | developers, support, auditors | |
| D1 | A command with a CPF is logged | developer, support | pseudonyms |
| D2 | An exception message contains a CPF and a token | support | `TextScrubber` |
| D3 | Support searches all logs of one customer | support | pseudonym search |
| D4 | An auditor needs the CPF on a cancelled NF-e | auditor | audit encryption + reveal |
| D5 | Telemetry property bag leaves the device | app | `PropertyRedactor` |
| D6 | Behaviour and operations joined only with consent | log server | session `PurposeSet` |
| **E** | **Storage and keys** | developers, ops | |
| E1 | Encrypt a column and still search by CPF | developer | Data `.Privacy(...)`, blind index |
| E2 | A sale event is sealed; later the customer is erased | developer | ES two-part payload, shredding |
| E3 | A view model persists a CPF on the device | developer | startup encryption check |
| E4 | POS creates a customer offline | cashier | intake sealing → server re-seal |
| E5 | Store API credentials of an integration | developer | field key, credential protector |
| E6 | Share a phone number with a delivery partner | developer | tokenization |
| E7 | Yearly key rotation | ops | `RotateAsync` |
| E8 | Turn on encryption for an existing table | ops | backfill job |
| **F** | **AI** | developers, operators | |
| F1 | Assistant answers about a customer (cloud model) | operator | `PseudonymizationSession` |
| F2 | Local model may see names | operator | `AiDataPolicy` |
| F3 | Model forges a placeholder | — | injection rejected |
| F4 | AI tool schema tells the host what is sensitive | AI host | `x-sensitivity` |
| F5 | Data migration asks an LLM to classify columns | migration engineer | shapes, not samples |
| **G** | **Data subject rights (LGPD art. 18)** | customer, request desk | |
| G1 | "Do you have my data?" | customer | confirmation |
| G2 | "Send me my data" | customer | export package |
| G3 | "Move my data to another pharmacy" | customer | portability |
| G4 | "My e-mail is wrong" | customer | correction via app command |
| G5 | "Delete my data" (with fiscal hold) | customer | erasure plan, scheduled shred |
| G6 | "Who did you share my data with?" | customer | `SharedWith` |
| G7 | "Why was I refused credit?" | customer | automated decision review |
| G8 | Someone asks for another person's data | attacker | identity verification |
| G9 | A request is about to miss its deadline | DPO | overdue alert |
| **H** | **Retention, holds, operations** | ops, DPO, legal | |
| H1 | Inactive loyalty members are purged | system | retention sweeper |
| H2 | Fiscal hold ends five years later | system | scheduled anonymize + shred |
| H3 | A lawsuit freezes a customer's data | legal | case legal hold |
| H4 | Database restored from backup | ops | erasure-log replay |
| H5 | Legacy ERP import brings back an erased customer | migration engineer | import check |
| H6 | Security incident: whose data was exposed? | DPO | inventory + audit |
| H7 | Same CPF in two pharmacies (tenants) | — | per-tenant subjects and keys |
| **I** | **Tests** | developers | |
| I1–I5 | Snapshot, no-clear-data, consent fold, shredding, scrubber corpus | developers | YFex.Privacy.Testing |

---

## A. Development time

### A1. Classify a new value object

**Situation:** the app starts storing the customer's driver's licence (CNH) for home delivery of controlled drugs.

**✍ Dev writes** (once, on the type)

```csharp
[Sensitive(DataSensitivity.PersonalIdentifier, Mask = "{last:3}", Identifier = true)]
public readonly partial struct Cnh : ISpanFormattable, IEquatable<Cnh> { /* parse + check digits, as usual */ }
```

**⚙ Generated**: `IClassifiedData<Cnh>` (classification constant, mask code, canonical form), a row in `PrivacySchema.verified.txt`.

**👁 Result**: every command, entity, event, view model and AI tool that uses `Cnh` is now masked on screen (`****123`),
pseudonymized in logs, encrypted in audit and listed by `yfex-privacy where Cnh`. Nothing else changes in the code.

### A2. A raw `string Cpf` slips into a command

**✍ Dev writes**

```csharp
public partial record CadastrarDependenteCommand(CustomerId Titular, string Nome, string Cpf) : ICommand;
```

**👁 Result** (IDE)

```
⚠ YFP0001  'Cpf' is personal data (convention: cpf). A classified value object exists: YFex.Entity.Public.Cpf.  [Change type to Cpf]
⚠ YFP0001  'Nome' is personal data (convention: nome in a person-like type). A classified value object exists: FullName. [Change type to FullName]
```

The code fix changes both types. If the warning is ignored, the member is still treated as `PersonalIdentifier` by convention
(pseudonymized in logs, masked on screen), and it shows in the snapshot as `convention(cpf)` for the reviewer.

### A3. Someone adds `string Senha` to a record

**✍ Dev writes**

```csharp
public sealed record IntegracaoFarmaciaPopular(string Usuario, string Senha);
```

**👁 Result**

```
⚠ YFP0005  'Senha' is Secret (convention: senha) and the compiler-generated ToString() of this record prints it.  [Generate PrintMembers]
```

```diff
# PrivacySchema.verified.txt (pull-request diff)
+IntegracaoFarmaciaPopular                    dto
+  Usuario    string   Personal   convention(usuario)
+  Senha      string   Secret     convention(senha)      YFP0002 → use a Secret value object
```

The reviewer sees the secret in the diff even if the author ignored the warning. Logs erase it (`<redacted:Secret>`) in all cases.

### A4. Silence a false positive

**Situation:** a report type has a `Nome` member that is the report's name, inside a type called `RelatorioCliente`.

**✍ Dev writes**

```csharp
public sealed record RelatorioCliente([NotPersonalData(Reason = "nome do relatório, não da pessoa")] string Nome, DateOnly Inicio);
```

**👁 Result**: the snapshot lists `Nome  not personal: "nome do relatório, não da pessoa"`. The reason is reviewable.

### A5. Classify a type you do not own

**✍ Dev writes** (any file of the assembly)

```csharp
[assembly: SensitiveType<System.Net.Mail.MailAddress>(DataSensitivity.Personal, Category = "contact", Mask = "{email}", Identifier = true)]
```

**👁 Result**: members of type `MailAddress` are classified like `Email`. Masks use the preset at run time (no generated code on a BCL type).

### A6. Health data needs a special category

**✍ Dev writes**

```csharp
[Sensitive(DataSensitivity.Personal)]                       // ✗
public readonly partial struct Prescricao { … }
```

**👁 Result**

```
✗ YFP0012  Prescricao looks like health data (convention: prescricao). Health is a special category (LGPD art. 5 II):
           use [Sensitive(DataSensitivity.SensitivePersonal, Category = "health")].
```

After the fix, everything that holds a `Prescricao` is encrypted at rest by default, never sent to cloud AI, revealed only with
`privacy.reveal.sensitive` + justification, and any purpose that processes it must have an art. 11 basis (YFP0022).

### A7. One member, two sensitivities (CPF or CNPJ)

**Situation:** `Cliente: CpfCnpj` holds a CPF for consumers and a CNPJ for companies.

**✍ Dev writes**: `[Sensitive(DataSensitivity.PersonalIdentifier)]` on `CpfCnpj` (02 §8). Nothing on the members.

**👁 Result**

| Value | Screen | Log |
|---|---|---|
| `123.456.789-09` | `***.456.789-**` | `<cpf #h:9f2c41d07a3be1c2>` |
| `12.345.678/0001-95` | `12.345.678/0001-95` | `12.345.678/0001-95` |

### A8. Hand-rolled hashing of personal data

**✍ Dev writes**

```csharp
var hash = SHA256.HashData(Encoding.UTF8.GetBytes(cliente.Email.Value + salt));
```

**👁 Result**

```
⚠ YFP0007  Unkeyed hash of personal data (Email). Low-entropy values can be enumerated.  [Use Pseudonymizer]
```

```csharp
var pseudonym = pseudonymizer.Pseudonymize(in cliente.Email, PseudonymDomain.Analytics);   // after the fix
```

### A9. CPF interpolated into an exception message

**✍ Dev writes**

```csharp
throw new InvalidOperationException($"Cliente {cpf} bloqueado");
```

**👁 Result**: `⚠ YFP0006 Classified value 'cpf' (PersonalIdentifier) interpolated into an exception message`. Suggested: throw a
typed error with the `CustomerId`, or keep the message and rely on the scrubber (D2). The warning makes the choice explicit.

### A10. "Where do we store CPF?" and the ROPA in CI

**✍ Dev writes** (CI pipeline)

```bash
yfex-privacy verify --root .                       # fails if a snapshot is stale or an assembly has none
yfex-privacy where Cpf --root . --format md > artifacts/where-cpf.md
yfex-privacy ropa --root . --config privacy-ropa.yaml --format md > artifacts/ropa.md
```

**👁 Result**: 03 §5.4 (`where`) and 03 §5.5 (ROPA table). The DPO gets an always-current record of processing, and an architect
answers "where is CPF stored, displayed, logged, sent to AI" without reading code.

---

## B. Seeing data on screen

### B1. Cashier sees a masked CPF; manager reveals it

**✍ Dev writes**: nothing. The grid binds `Cliente.CpfCnpj` as before.

**👁 Result**

```
Clientes                                         (cashier: no privacy.reveal.identifier)
João da Silva      ***.456.789-**     (11) 98765-4321
Maria Souza        ***.111.222-**     (11) 91234-0000

Clientes                                         (manager: has privacy.reveal.identifier)
João da Silva      ***.456.789-**  👁   → click → 123.456.789-09   (re-masks after 30 s or on navigation)
```

```
AUDIT  Privacy.Revealed  member=Cliente.CpfCnpj subject=#h:2b77e0… screen=ClienteList actor=gerente:ana tenant=farma-centro
```

### B2. A user without permission queries customers

**Situation:** a back-office report user calls `Clientes.Queries.Listar` over RPC. Masking on the client would not help, because the
full CPF would already be on their machine.

**📦 Framework** (server, `PrivacyStage` on results, ⚑P17)

```
result.Items[0].CpfCnpj = default
result.PrivacyMetadata  = [ { path: "Items[0].CpfCnpj", display: "***.456.789-**", reason: "missing privacy.reveal.identifier" } ]
```

**👁 Result**: the client grid shows `***.456.789-**` from the metadata, and the clear value never left the server.

### B3. Pharmacist opens medication history (sensitive)

**👁 Result**

```
Histórico de medicamentos — João da Silva          [ 🔒 Dados de saúde ]
Motivo do acesso: ( ) Atendimento farmacêutico  ( ) Solicitação do titular  ( ) Outro: ____     [Revelar]
```

The reveal is audited with the chosen justification. Without `privacy.reveal.sensitive` the button does not exist.

### B4. Support staff impersonate a tenant

**Situation:** vendor support enters tenant `farma-centro` to debug a screen (`ITenantImpersonationAuditor`).

**👁 Result**: screens work, but personal values stay masked. Impersonation grants no reveal permission (08 §9). If the tenant gave
support a reveal permission explicitly, reveals are audited with the **real** actor (`ActorContext`, audit 02 §3):
`actor=farma-centro:suporte realActor=vendor:carlos`.

---

## C. Consent

### C1. Customer joins the loyalty program at the counter, offline

Full flow in 09 §3. In short:

**✍ Dev writes**

```csharp
var notice = await consent.GetNoticeAsync(FarmaPurposes.Values.LoyaltyProgram, ct);
var answer = await dialogs.Show(new ConsentPrompt(notice, Cliente!.Value, confirmOn: ConsentConfirmation.PinPad), ct);
if (answer is ConsentGiven given) await consent.RecordAsync(given.ToEntry(collectedBy: operador.Id), ct);
```

**👁 Result**: the customer confirms on the PIN pad. The entry is stored in the device's local ledger, signed by the device key, synced
when the connection returns, verified by the server, and audited. The proof holds the notice version and the SHA-256 of the exact text shown.

### C2. A command needs consent the customer never gave

**✍ Dev writes** (aggregate configuration)

```csharp
b.Command<AcumularPontosCommand, Saldo>().RequirePermission(Permissions.FidelidadeOperar).Privacy(p => p.RequireConsent());
```

**👁 Result**

```
Server:  Error.ConsentRequired("marketing.loyalty", NotAsked)
POS:     ⓘ O cliente ainda não participa do programa de fidelidade.  [Convidar cliente]  → opens the prompt of C1
```

### C3. The legal-obligation path never asks (CPF na nota)

**✍ Dev writes**: the fiscal module's purpose is `[Purpose(LegalBasis.LegalObligation, …)] FiscalDocument`.

**👁 Result**: no prompt, no ledger lookup (`ConsentStatus.NotRequired`). The log says `purpose=fiscal.document consent=not-required`.
A withdrawal of any other consent never blocks the NF-e. This matters legally: offering a choice where there is none would mislead
the customer (10 §1.2).

### C4. Privacy notice changes materially

**✍ Dev writes** (tenant admin publishes; permission `privacy.notices.publish`)

```csharp
await notices.PublishAsync(current with { Version = 4, Text = novoTexto, MaterialChange = true }, ct);
```

**👁 Result**: grants for `marketing.loyalty` become `NoticeChanged`. Loyalty commands return `ConsentRequired(…, NoticeChanged)`
until the customer accepts version 4 at their next visit or in the portal. Non-material edits (typo fixes) set `MaterialChange = false`
and keep existing grants valid.

### C5. Customer withdraws consent in the portal

**👁 Result** (09 §7): ledger entry `Withdrawn` → snapshot invalidated → POS terminals refetch within seconds → loyalty commands fail
with `ConsentRequired(…, Withdrawn)` → the retention rule deletes loyalty contact data 30 days later. Offline terminals catch up on
reconnect. Meanwhile the `privacy.consent.staleness` metric shows how old their snapshot is.

### C6. Marketing consent expires after 24 months

**✍ Dev writes** (tenant policy, admin screen or configuration)

```csharp
policy.Purpose(FarmaPurposes.Values.LoyaltyProgram).ConsentValidity(TimeSpan.FromDays(730));
```

**👁 Result**: grants older than 24 months fold to `Expired`. The POS shows "Renovar consentimento do programa" on the next sale.

### C7. A minor joins the program

**👁 Result**: the customer's birth date shows 10 years and the purpose is consent-based, so `ConsentRules.RequiresGuardian` is true
(art. 14 §1, children under 12). The prompt asks for the parent's CPF and confirmation, and the entry stores
`Guardian = DataSubjectReference(Person, "customer", <guardian id>)`. For a 15-year-old the tenant policy decides (04 §12,
Enunciado CD/ANPD 1/2023).

### C8. Customer objects to a legitimate-interest purpose

**Situation:** `security.fraud` (legitimate interest) analyses purchase patterns. The customer objects (art. 18 §2).

**👁 Result**: a `Denied` entry for `security.fraud`. The fraud module checks `snapshot.Allows(FraudPrevention)` and skips the
customer's purchases. The DPO can still refuse the objection with a reasoned record when the interest prevails. The request desk records
that decision.

### C9. Desktop app first run: telemetry choices

**👁 Result** (YFex.Profiling, presets over purposes)

```
Ajude a melhorar o aplicativo
( ) Apenas diagnóstico de falhas        → telemetry.functional (legitimate interest, you may object)
(•) Estatísticas de uso                 → + telemetry.analytics, telemetry.behaviour, telemetry.installed-software
( ) Completo                            → + telemetry.active-window-title, telemetry.browser-data
[Avançado ▾]  ☑ Estatísticas ☑ Comportamento ☐ Programas instalados ☐ Títulos de janela ☐ Navegador
```

```csharp
await consentClient.ApplyPresetAsync(ConsentPresets.Analytics, notice, ct);   // one ledger entry per purpose
if (consent.Allows(TelemetryPurposes.Values.BrowserData)) CollectBrowserData(fields, errors);   // false here
```

### C10. Employer enables device monitoring

**Situation:** a corporate tenant uses YFex.Security on company PCs (process detail, file activity).

**✍ Dev writes**: nothing in code. The tenant admin sets the basis of `security.process-detail` and `security.file-activity` to
`LegitimateInterest`, which the catalog allows for these purposes (04 §8), and publishes an employee notice. Traffic interception
additionally needs the tenant flag.

**👁 Result**: watchers subscribe only for allowed purposes. Changing the policy back unsubscribes them at once. The ROPA shows the
basis change with who made it and when.

---

## D. Logs, audit, telemetry

### D1. A command with a CPF is logged

**✍ Dev writes**: nothing.

**👁 Result**

```
10:40:16.300 INF Venda.Finalizar completed 48.0ms venda=V-88123 cpfNaNota=<cpf #h:9f2c41d07a3be1c2> total=152.40
```

### D2. An exception message contains a CPF and a token

**👁 Result**

```
before: HttpRequestException: 401 calling https://api.parceiro.com/clientes?cpf=12345678909&token=eyJhbGciOi…
after:  HttpRequestException: 401 calling https://api.parceiro.com/clientes?<redacted>
before: DETAIL: Key (cpf_cnpj)=(12345678909) already exists.
after:  DETAIL: Key (cpf_cnpj)=(<cpf #h:9f2c41d07a3be1c2>) already exists.
```

### D3. Support searches all logs of one customer

**Situation:** the customer calls: "my sale failed yesterday". Support has the CPF but logs only hold pseudonyms.

**✍ Support types** (LoggY / `yfex-log` CLI)

```
yfex-log search --tenant farma-centro --identifier cpf=123.456.789-09 --since 2026-09-30
```

**📦 Framework**: the CLI asks the server to compute `Pseudonymize(cpf, Log)` with the tenant key (the key never leaves the server), then
searches `#h:9f2c41d07a3be1c2`. The search is audited (`Privacy.PseudonymLookup`).

**👁 Result**: every operation of that customer, including the exception from D2. The clear CPF is not in the logs.

### D4. An auditor needs the CPF on a cancelled NF-e

**👁 Result** (audit explorer, YFex.Audit)

```
2026-09-14 16:02  NFe.Cancelar  chave=3526…0013  cliente=🔒 [Revelar]  motivo="erro de digitação"  actor=caixa:joao
                                                    → 123.456.789-09   (auditor has audit.reveal; reveal audited)
```

The CPF in the audit entry is sealed with the subject key (audit 08 §2), so the hash chain is valid and the value stays erasable.

### D5. Telemetry property bag leaves the device

**👁 Result**: 06 §6 table. Before vs after: `userName` goes from clear to pseudonym, `multipleMonitors` is no longer wrongly masked,
and URL queries never ship.

### D6. Behaviour and operations joined only with consent

**👁 Result**: in LoggY, a failed `Caixa.Finalizar` trace shows the user's click history **only** if the session's `PurposeSet` contains
`telemetry.behaviour`. Otherwise the panel reads "comportamento do usuário não disponível (sem consentimento)".

---

## E. Storage and keys

### E1. Encrypt a column and still search by CPF

**✍ Dev writes** (Data configuration)

```csharp
d.Entity<ClienteEntity>().Privacy(p => p.Encrypt(c => c.CpfCnpj).Encrypt(c => c.Email));

var cliente = await db.Query<ClienteEntity>().Where(c => c.CpfCnpj == cpf).SingleOrDefaultAsync(ct);   // unchanged query code
```

**⚙ Generated / 👁 Result**

```sql
-- columns: cpf_cnpj varbinary (envelope v2), cpf_cnpj_index binary(16), email varbinary, email_index binary(16)
SELECT … FROM cliente WHERE cpf_cnpj_index = @p0      -- @p0 = blind index of the CPF; equality only
```

`Where(c => c.Email.Value.StartsWith("joao"))` fails at build time on an encrypted member, with a diagnostic pointing to 05 §4.

### E2. A sale event is sealed; later the customer is erased

**👁 Result**

```
before erasure: VendaFinalizada { Id: V-88123, CpfNaNota: 123.456.789-09, Total: 152.40 }
after erasure : VendaFinalizada { Id: V-88123, CpfNaNota: null,           Total: 152.40 }   Flags: Shredded
```

Projections keep totals and products and lose the identifier. With a fiscal hold, the key destruction waits for the end of the hold
(G5, H2).

### E3. A view model persists a CPF on the device

**✍ Dev writes**

```csharp
[Observable, Persist] public partial Cpf? UltimoClienteConsultado { get; set; }
```

**👁 Result**: `ℹ YFP0030 persisted PersonalIdentifier: the device store must be encrypted`. In Production, if no protector is registered:

```
PrivacyStartupException: snapshot provider "caixa" persists PersonalIdentifier data (UltimoClienteConsultado) but no IValueProtector is registered.
Call services.AddYFexStorageEncryption(...) or remove [Persist].
```

### E4. POS creates a customer offline

**👁 Result**: the POS seals the new customer's personal fields with the tenant **intake public key** (ECIES). It cannot read them back
from the outbox, but it shows them from its encrypted local store. On sync the server opens the intake envelope, creates the subject
key and re-seals. The device never holds a subject key.

### E5. Store API credentials of an integration

**✍ Dev writes**: nothing new. `ApiRow.Secret/Token/ApiKey` keep going through `ISecretProtector`, now on envelope v2 with a tenant
`credentials` field key.

**👁 Result**: credentials are encrypted per tenant with key ids (rotatable). Legacy rows stay readable and are re-sealed on the next write.
Credential records no longer print secrets (A3).

### E6. Share a phone number with a delivery partner

**✍ Dev writes**

```csharp
var token = await tokens.TokenizeAsync(scope, cliente.Subject, cliente.Telefone, ct);   // tok_7Q2M…
await entregas.SolicitarAsync(new(pedido.Id, endereco, contato: token.ToString()), ct);

// partner callback, later — needs privacy.detokenize, audited
var result = await tokens.DetokenizeAsync(scope, token, new PrivacyAccess(principal, FarmaPurposes.Values.Sale, "entrega"), ct);
```

**👁 Result**: the partner never stores the number. After erasure, `DetokenizeAsync` returns `TokenDeleted`.

### E7. Yearly key rotation

**✍ Ops runs** (scheduled job or CLI)

```bash
yfex-privacy keys rotate --tenant farma-centro --kind kek          # re-wraps subject + field keys; data untouched
yfex-privacy keys rotate --tenant farma-centro --kind field --category credentials
```

**👁 Result**: new writes use the new key ids. Old ciphertexts stay readable until they are re-sealed on touch.
`log` and `identifier:*` pseudonym keys are **never** rotated within retention (05 §5.4), because that would break joins and blind indexes.

### E8. Turn on encryption for an existing table

**👁 Result**

```
yfex-privacy data migrate --entity ClienteEntity --tenant all
  1/3 add cpf_cnpj_index, email_index ........................ done
  2/3 backfill blind indexes (1 284 112 rows, 8 batches/s) .... done   (resumable)
  3/3 re-encrypt cpf_cnpj, email (rolling) .................... 61% … (mixed rows readable)
```

---

## F. AI

### F1. Assistant answers about a customer (cloud model)

**👁 Result** (08 §8.2): the model sees `[[PESSOA_1]]`, `[[CPF_1]]`, `[[EMAIL_1]]`. It calls `Vendas.Commands.ReenviarCupom { email:
"[[EMAIL_1]]" }`, and the runtime restores the real e-mail before execution under the operator's permissions. The operator's screen
shows the answer with real names restored for display.

### F2. Local model may see names

**✍ Dev writes**

```csharp
.Ai(a => a.Provider("claude-desktop", cloud: false).MaximumInClear(DataSensitivity.Personal))
```

**👁 Result**: names and e-mails go in clear to the local model. CPF and health data still become placeholders. Cloud providers keep
the default (`Internal`).

### F3. Model forges a placeholder

**👁 Result**: a tool call with `[[CPF_7]]` (never issued in this session) is rejected with `PlaceholderUnknown`. The model cannot
reach other people's data by guessing references.

### F4. AI tool schema tells the host what is sensitive

**👁 Result** (`ActionDescriptor.ParametersSchema`)

```json
"cliente": { "type": "string", "x-type": "cpf-cnpj", "x-sensitivity": "personal-identifier", "x-mask": "***.###.###-**" }
```

### F5. Data migration asks an LLM to classify columns

**👁 Result** (DataMining prompt)

```
Column CAD_CLI.DOC  — shapes: ddd.ddd.ddd-dd ×812, dd.ddd.ddd/dddd-dd ×95, (empty) ×3  — what is the semantic type?
```

No customer value leaves the machine. The answer `BrCnpjOrCpf` maps to `PersonalIdentifier` in the inventory (03 §5.3).

---

## G. Data subject rights (LGPD art. 18)

### G1. "Do you have my data?" (confirmation)

**👁 Result** (portal, immediately)

```
Sim. A Farmácia Centro trata dados seus nas categorias: identificação, contato, comportamento de compra, saúde.
Finalidades: emissão fiscal (obrigação legal), vendas (contrato), programa de fidelidade (consentimento), histórico de medicamentos (consentimento).
[Solicitar cópia completa]  [Gerenciar consentimentos]  [Falar com o encarregado]
```

### G2. "Send me my data" (access)

**👁 Result**: a ZIP in the portal for 7 days with `README.md`, `subject.json`, `subject.schema.json`, `consents.json`, `sharing.json`
(07 §5, excerpt in 09 §8). Third parties inside the records, such as the prescriber, are masked.

### G3. "Move my data to another pharmacy" (portability)

**👁 Result**: the same `subject.json` plus its JSON Schema (structured, documented). The receiving controller imports it with their
own tools. Nothing is sent to them automatically.

### G4. "My e-mail is wrong" (correction)

**✍ Dev writes** (once)

```csharp
p.Corrections(c => c.For<Email>("contact", (subject, value) => Clientes.Commands.AtualizarContato(subject.As<CustomerId>(), email: value)));
```

**👁 Result**: in the portal (authenticated) the customer edits the e-mail. The app's own command runs with validation, rules and
audit, as if the cashier had done it. If the old e-mail was shared with the payment provider, a manual task asks to inform them.

### G5. "Delete my data" (with fiscal hold)

**👁 Result**: dry-run plan in 07 §7.3. Loyalty and medication history are deleted, the customer record is anonymized, and fiscal
documents are **retained until 2031-01-01** with the legal basis stated. The subject-key destruction is scheduled for the end of the
hold. Devices are signalled. The report to the customer explains each line. (Audit design 08 §3 proposes an alternative, keys per
retention class. See the note at the end.)

### G6. "Who did you share my data with?"

**👁 Result**

```
Secretaria da Fazenda (NF-e/NFC-e) — identificação — obrigação legal
Gateway de pagamento — contato, financeiro — execução de contrato
Provedor de IA (EUA) — nenhum dado pessoal em claro (pseudônimos) — interesse legítimo
```

### G7. "Why was I refused credit?" (automated decision review, art. 20)

**👁 Result**: the request attaches the YFex.Rules `Trace` of the decision ("limite de crediário: renda declarada < 3× parcela →
recusado") and routes it to a human reviewer with permission `privacy.requests.handle`. The reviewer's decision is recorded and sent
to the customer.

### G8. Someone asks for another person's data

**👁 Result**: an e-mail request for "the data of CPF 123…" without login gets `AwaitingVerification(ContactConfirmed)`. The OTP goes
to the e-mail **already on file**, not to the requester's address. With no confirmation the request ends `Rejected`. Repeated attempts
for the same subject from different addresses raise a security alert.

### G9. A request is about to miss its deadline

**👁 Result**

```
⚠ Privacy request PR-2026-0042 (Deletion) due in 2 days — waiting on: manual task "inform payment-provider" (owner: op:maria)
```

---

## H. Retention, holds, operations

### H1. Inactive loyalty members are purged

**✍ Dev writes** (retention rule)

```csharp
p.Retain(FarmaPurposes.Values.LoyaltyProgram, DataCategory.Contact).AlsoAfter(years: 2, RetentionTrigger.LastActivity).Then(ErasureAction.Delete);
```

**👁 Result**: the daily sweeper `[Job]` deletes the loyalty contact data of members inactive for 2 years and writes one audit entry
per batch (`Privacy.Retention.Executed rule=… rows=312`).

### H2. Fiscal hold ends five years later

**👁 Result** (2031-01-01, sweeper)

```
Retention  fiscal.document  hold ended for 1 204 subjects (issued in 2025)
  ANONYMIZE venda.cpf_na_nota (18 330 rows)
  SHRED     scheduled subject keys (47 subjects whose erasure was requested during the hold)
```

### H3. A lawsuit freezes a customer's data

**✍ User does** (permission `privacy.legal-hold.manage`)

```csharp
await holds.PlaceAsync(new LegalHold(scope, subject, reason: "Processo 0012345-67.2026", basis: LegalBasis.LegalProceedings, until: null), ct);
```

**👁 Result**: any erasure plan for this subject turns `DELETE` into `RETAIN (case hold "Processo 0012345-67.2026")`. Retention sweeps
skip the subject. Releasing the hold resumes pending plans.

### H4. Database restored from backup

**👁 Result**

```
YFex startup: database "farma" last erasure replayed = 2026-09-20; erasure log has 14 later entries → refusing to serve.
> yfex-privacy replay-erasures --database farma --since 2026-09-20
  replayed 14 erasures (PR-2026-0039 … PR-2026-0052) ✓   → startup proceeds
```

### H5. Legacy ERP import brings back an erased customer

**👁 Result**: the import (DataMining or CSV) computes identifier pseudonyms, finds a match in the erasure log, and skips the row:
`skipped 1 row: subject erased on 2026-09-18 (PR-2026-0037)`.

### H6. Security incident: whose data was exposed?

**Situation:** the `cliente` table of one tenant leaked through a misconfigured backup bucket between two dates.

**👁 Result** (suggestion, 10 §2.1): the inventory says `cliente` holds identification + contact, with `cpf_cnpj`/`email` encrypted (E1). The
report: "1 284 112 subjects; exposed in clear: names, phones, addresses; encrypted: CPF, e-mail (keys not exposed)". That is the
input for the ANPD notification and the subject notices.

### H7. Same CPF in two pharmacies (tenants)

**👁 Result**: two data subjects, two subject keys, two consent ledgers, unlinkable pseudonyms (per-tenant keys). A deletion at
Farmácia Centro does not touch Farmácia Norte. Each pharmacy is the controller of its own relationship with the customer.

---

## I. Tests

```csharp
// I1 — the reviewed snapshot (one per assembly)
[Fact] public void Privacy_schema_is_reviewed() => PrivacySchemaSnapshot.Verify(YFexFarma.PrivacySchema.Text, YFexFarma.PrivacySchema.InventoryJson);

// I2 — no clear personal data in logs/audit/AI prompts
logs.Records.ShouldNotContainPersonalData(cpf);
fakeCloudModel.Prompts.ShouldNotContainPersonalData(cpf, email);

// I3 — consent fold rules with a fake clock (09 §10)
(await ledger.GetSnapshotAsync(subject)).StatusOf(Loyalty).Should().Be(ConsentStatus.Withdrawn);

// I4 — shredding
await vault.DestroySubjectKeyAsync(scope, subject, "PR-1");
(await store.ReadAsync(stream).SingleAsync()).Flags.Should().HaveFlag(CommittedEventFlags.Shredded);

// I5 — scrubber corpus (06 §7): NF-e keys, EAN-13, dates and amounts never match; CPFs in SQL errors always match
ScrubberCorpus.Verify(scrubber, "Scrubbing/Corpus");
```

---

## Note: one alignment item with YFex.Audit

The audit design (`../audit/08-privacy-retention-and-access.md` §3) proposes **subject keys scoped by retention class** (Option A).
Erasure then shreds the keys of the classes not on hold right away, and the fiscal-class key later. This design currently uses **one subject key
with a scheduled destruction** (07 §7.3, the behaviour of G5/H2). Option A erases non-fiscal audit values immediately at the cost of one
key per subject per retention class. It should be settled as one decision for both libraries before Phase 4. The privacy files 04, 07 and 08 also still
say "Audit channel" / "YFex.Logging.Audit" in places: read them as **YFex.Audit** (audit was split from logging, logging ⚑L20).
