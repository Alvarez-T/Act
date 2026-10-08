# 13 — Usage examples from real pharmacy features

Each example starts from a **real business feature** of a Brazilian pharmacy, names the **legal requirement** behind it (rows R1–R20 of
[00-requirements-and-scope.md](00-requirements-and-scope.md)), states the **tier**, then shows:

- **✍ Dev writes**: the complete code a developer types;
- **⚙ Generated / 📦 Framework**: only when it helps to see it;
- **👁 Result**: what each role sees (cashier, pharmacist, manager, customer), plus logs, audit and integrations.

APIs follow 02–08 (privacy), the implemented logging API (`ILog<T>`, `log.Information($"…")`, `LogCapture`), YFex.Authorization
(`[PermissionCatalog]`, `[RequirePermission]`, `.RequirePermission(...)`) and YFex.Audit. Names marked **(new proposal)** are not yet in
02–08 and should be folded in when approved.

## Index

| # | Example | Legal driver | Tier |
|---|---|---|---|
| 1 | Minimum setup: classify the value objects once | R2, R3 | 1 |
| 2 | Customer registration at the counter | R1, R2, R9 | 1 (+2 notice) |
| 3 | Sale with *CPF na nota* | R2, R3 | 1 |
| 4 | Controlled-drug dispensing (Portaria 344/98) | R5, R7, R8 | 2 |
| 5 | Antibiotic dispensing (RDC 471/2021): collecting less | R1, R5, R8 | 2 |
| 6 | Farmácia Popular dispensing + Ministry inspection hold | R5, R8 | 2 |
| 7 | Loyalty program done right ("desconto no CPF") | R9, R10 | 2 |
| 8 | Discount program of a laboratory / PBM: art. 11 §4 | R6 | 2 |
| 9 | Convênio statement sent to the employer | R5, R6 | 2 |
| 10 | Pharmaceutical services (glucose, blood pressure, vaccines) | R5, R7 | 2 |
| 11 | An error message with a CPF reaches the logs | R3 | 1 |
| 12 | Customer asks "what do you have about me?" | R12 | 2 |
| 13 | Customer asks for deletion: four retention rules collide | R8, R12 | 2 |
| 14 | A manager's notebook with a customer spreadsheet is stolen | R15 | 2 |
| 15 | AI assistant at the counter | R5, R16 | 2 |
| 16 | Children: pediatric prescription vs kids' loyalty account | R11 | 2 |

Shared declarations used by the examples:

```csharp
[PermissionCatalog]
public static partial class FarmaPermissions
{
    [PermissionMeta(DisplayName = "Ver documentos de clientes (CPF/RG)")] public const string RevealIdentifier = "privacy.reveal.identifier";
    [PermissionMeta(DisplayName = "Dispensar medicamentos controlados")]   public const string ControladosDispensar = "farmacia.controlados.dispensar";
    [PermissionMeta(DisplayName = "Consultar escrituração de controlados")] public const string ControladosVer = "farmacia.controlados.ver";
    [PermissionMeta(DisplayName = "Registrar serviços farmacêuticos")]    public const string ServicosFarmaceuticos = "farmacia.servicos";
    [PermissionMeta(DisplayName = "Emitir extrato de convênio")]            public const string ConvenioExtrato = "farmacia.convenio.extrato";
}

[PurposeCatalog]
public static partial class FarmaPurposes
{
    [Purpose(LegalBasis.ContractPerformance, Categories = ["identification", "contact"])] public const string Sale = "sale";
    [Purpose(LegalBasis.LegalObligation, Categories = ["identification"])]                public const string FiscalDocument = "fiscal.document";
    [Purpose(LegalBasis.LegalObligation, Categories = ["identification", "health"])]      public const string ControlledDrugs = "health.controlled-drugs";
    [Purpose(LegalBasis.LegalObligation, Categories = ["health"])]                        public const string Antibiotics = "health.antibiotics";
    [Purpose(LegalBasis.LegalObligation, Categories = ["identification", "health"])]      public const string FarmaciaPopular = "health.farmacia-popular";
    [Purpose(LegalBasis.HealthProtection, Categories = ["health"])]                       public const string PharmaceuticalServices = "health.pharmaceutical-services";
    [Purpose(LegalBasis.Consent, Categories = ["contact", "behavioral"])]                 public const string LoyaltyProgram = "marketing.loyalty";
    [Purpose(LegalBasis.Consent, Categories = ["health"])]                                public const string MedicationHistory = "health.medication-history";
    [Purpose(LegalBasis.Consent, Categories = ["health"])]                                public const string ConvenioItemizedStatement = "convenio.itemized-statement";
}
```

---

## 1. Minimum setup: classify the value objects once (Tier 1)

**Situation:** the first privacy step for every YFex app. Nothing about consent or rights yet.

**✍ Dev writes** (YFex.Domain, once for the whole ecosystem)

```csharp
[Sensitive(DataSensitivity.PersonalIdentifier, Mask = "***.###.###-**", Identifier = true)] public readonly partial struct Cpf { … }
[Sensitive(DataSensitivity.PersonalIdentifier, Identifier = true)]                          public readonly partial struct CpfCnpj { … }
[Sensitive(DataSensitivity.PersonalIdentifier, Identifier = true)]                          public readonly partial struct Rg { … }
[Sensitive(DataSensitivity.Personal, Category = "contact", Mask = "{email}", Identifier = true)] public readonly partial struct Email { … }
[Sensitive(DataSensitivity.Personal, Category = "contact", Mask = "{last:4}", Identifier = true)] public readonly partial struct Telefone { … }
[Sensitive(DataSensitivity.Personal, Mask = "{initials}")]                                   public readonly partial struct FullName { … }
[Sensitive(DataSensitivity.Personal)]                                                        public readonly partial struct PartyName { … }
// Cnpj, CompanyName: no attribute (company data is not personal data)
```

**✍ Dev writes** (app composition root)

```csharp
services.AddYFexPrivacy(p => p.Scrubber(s => s.AddBuiltIn(BuiltInDetectors.All).Add<Cpf>().Add<Telefone>()));
services.AddYFexLogging(log => log.Node(n => n.App("farma-desktop")));   // logging reads the classification by itself
```

**👁 Result:** every command, entity, event, query result and view model that holds these types is handled correctly in logs, AI and
screens **without any change in handlers or view models**. That is the whole Tier 1 cost: one attribute per value-object type.

---

## 2. Customer registration at the counter (Tier 1 + notice in Tier 2)

**Situation:** the cashier registers a customer (name, CPF, phone, e-mail, address) to sell on credit (*fiado*) or to issue invoices.
CDC art. 43 §2 says opening a consumer record must be communicated in writing when the consumer did not ask for it (R9, the basis of
the Drogaria Araujo fine). LGPD art. 46 + ANPD least-privilege guidance (R2): not every employee needs to browse every customer's CPF.

**✍ Dev writes**

```csharp
// contract (entity already uses value objects)
public partial record CadastrarClienteCommand(PartyName Nome, CpfCnpj? Documento, Telefone? Telefone, Email? Email, string? Endereco)
    : ICommand<CustomerId>;

// aggregate configuration
b.Command<CadastrarClienteCommand, CustomerId>()
 .RequirePermission(Permissions.ClientesCadastrar)
 .Privacy(p => p.Purpose(FarmaPurposes.Values.Sale));

// view model: unchanged; the notice text comes from the tenant's PrivacyNotice "cadastro" (Tier 2)
[StateCommand]
async Task SalvarAsync(CancellationToken ct)
{
    await dialogs.Show(new NoticeAcknowledgement(await notices.GetCurrentAsync("cadastro", "pt-BR", ct)), ct);   // prints/shows, no consent
    await Clientes.Commands.CadastrarAsync(new(Nome, Documento, Telefone, Email, Endereco), ct);
}
```

**👁 Result**

| Who | Sees |
|---|---|
| Cashier, while typing | everything they type (they entered it) |
| Cashier, customer list | `João da Silva · ***.456.789-** · ****4321 · j***@acme.com.br`; search **by CPF works** (exact match), browsing does not expose documents |
| Manager (`privacy.reveal.identifier`) | 👁 reveals the CPF; the reveal is audited |
| Customer | a printed/on-screen notice: who the controller is, why the record exists, how long it is kept, how to contact the pharmacy (art. 9, CDC art. 43 §2) |
| Log | `Clientes.Cadastrar completed nome=J. S. documento=***.456.789-** telefone=****4321 email=j***@acme.com.br endereco=<free text, scrubbed>` |

---

## 3. Sale with *CPF na nota* (Tier 1)

**Situation:** the customer asks for their CPF on the NFC-e. This is a legal obligation (purpose `fiscal.document`), not consent.

**✍ Dev writes:** nothing privacy-related (the command has `CpfCnpj? CpfNaNota`).

**👁 Result**

```
POS screen      CPF na nota: 123.456.789-09          (the cashier typed it; shown in full during the sale)
NFC-e / DANFE   printed as required by the fiscal layout (outside the library's control: legal document)
Log             Venda.Finalizar completed venda=V-88123 cpfNaNota=***.456.789-** total=152.40 purpose=sale,fiscal.document
Consent check   not required (LegalObligation) — no prompt, never blocked by a withdrawal elsewhere
```

---

## 4. Controlled-drug dispensing (Portaria 344/98) (Tier 2)

**Situation:** a customer buys clonazepam (list B1) with a *Notificação de Receita*. The pharmacist must record the **buyer** (name,
**CPF**, full address, phone; the CPF rule is in force since 2026-02-13 through RDC 1.000/2025), plus patient, prescriber and medication. The
records go to SNGPC (Anvisa) and are archived for **2 years** (art. 64), **5 years for anabolic steroids**. This record reveals a mental-health
treatment: health data (R5), covered by pharmacist confidentiality (R7, CFF 724/2022).

**✍ Dev writes**

```csharp
// The record is health data as a whole (record-level classification, 02 §5.5)
[Sensitive(DataSensitivity.SensitivePersonal, Category = "health")]
public sealed partial record DispensacaoControlado(
    DispensacaoId Id,
    FullName NomeComprador, Cpf CpfComprador, Endereco EnderecoComprador, Telefone TelefoneComprador,
    FullName NomePaciente, Sku Medicamento, ListaControle Lista, Quantity Quantidade,
    string NumeroNotificacao, string CrmPrescritor, DateOnly Data);

public static partial class Controlados
{
    public partial record RegistrarDispensacaoCommand(DispensacaoControlado Dispensacao) : ICommand;
    public partial record LivroQuery(DateOnly De, DateOnly Ate) : IQuery<IReadOnlyList<DispensacaoControlado>>;
}

public sealed class ControladosConfiguration : IAggregateConfiguration<Controlados>
{
    public void Configure(AggregateBuilder<Controlados> b)
    {
        b.Privacy(p => p.DefaultPurpose(FarmaPurposes.Values.ControlledDrugs));
        b.Command<Controlados.RegistrarDispensacaoCommand>().RequirePermission(FarmaPermissions.ControladosDispensar).Audit();
        b.Query<Controlados.LivroQuery, IReadOnlyList<DispensacaoControlado>>()
         .RequirePermission(FarmaPermissions.ControladosVer)
         .Privacy(p => p.AuditReads());                                   // (new proposal) who read whose health records
    }
}

// Retention declared by the module (07 §7.1)
p.Retain(FarmaPurposes.Values.ControlledDrugs).For(years: 2).From(RetentionClocks.SameDay, member: "Data")
 .AsLegalHold("Portaria SVS/MS 344/98 art. 64").Then(ErasureAction.Delete);
p.Retain(FarmaPurposes.Values.ControlledDrugs).When(d => d.Lista == ListaControle.C5)              // (new proposal) filtered rule
 .For(years: 5).From(RetentionClocks.SameDay, member: "Data").AsLegalHold("Lei 9.965/2000 — anabolizantes").Then(ErasureAction.Delete);

// Sharing declared once (ROPA, art. 18 VII answers)
p.SharedWith("anvisa-sngpc", "Anvisa — SNGPC", LegalBasis.LegalObligation, DataCategory.Health, DataCategory.Identification);

// Screen: the whole view requires the permission (YFex.Authorization view-model policy)
[Route(typeof(LivroControladosRoute)), RequirePermission(FarmaPermissions.ControladosVer)]
public partial class LivroControladosViewModel : PageViewModel { … }
```

**👁 Result**

| Who | Sees |
|---|---|
| Cashier (no permission) | the POS line says "Medicamento controlado — chamar o farmacêutico". The dispensing screen does not open, and the book is not in the menu |
| Pharmacist | the dispensing form and the book, in full (they need them for the legal bookkeeping) |
| Manager with `farmacia.controlados.ver` | the book; every query is recorded |
| Log | `Controlados.RegistrarDispensacao completed id=D-1029 <redacted:health 12 fields> actor=farmaceutico:lucia` |
| Audit (YFex.Audit) | `RegistrarDispensacao id=D-1029 actor=farmaceutico:lucia` with the fields **encrypted** (subject key) · `Privacy.HealthRecordsRead query=Livro 2026-09 rows=212 actor=gerente:ana` |
| AI | never receives these records (`SensitivePersonal` → `CloudLanguageModel`: erase) |
| Database | `SensitivePersonal` → encrypted at rest by default (⚑P15) |
| 2028-10-02 | the sweeper deletes the B1 record (2 years). C5 records stay until 5 years |

---

## 5. Antibiotic dispensing (RDC 471/2021): collecting less (Tier 2)

**Situation:** amoxicillin with a prescription. The pharmacy keeps the 2nd copy, with the **patient's** name, age and sex. The rule does
**not** require buyer data. The privacy-relevant decision is therefore what **not** to collect (R1, art. 6 III).

**✍ Dev writes**

```csharp
[Sensitive(DataSensitivity.SensitivePersonal, Category = "health")]
public sealed partial record DispensacaoAntimicrobiano(
    DispensacaoId Id, FullName NomePaciente, int Idade, Sexo Sexo,           // what the prescription carries
    Sku Medicamento, Quantity Quantidade, string Lote, string CrmPrescritor, DateOnly Data);
    // no CPF, no address, no phone: the rule does not ask for them

p.Retain(FarmaPurposes.Values.Antibiotics).For(years: 2).From(RetentionClocks.SameDay, member: "Data")
 .AsLegalHold("RDC 471/2021 art. 22").Then(ErasureAction.Delete);
```

**👁 Result:** the dispensing screen has no CPF field. If a product owner later asks to "add CPF just in case", the ROPA shows a
`health.antibiotics` purpose whose categories now include `identification` without a legal requirement. That is the exact pattern the
ANPD criticized in pharmacies ("CPF unnecessary for the declared purposes"), and the review catches it.

---

## 6. Farmácia Popular + a Ministry inspection (Tier 2)

**Situation:** losartan through Farmácia Popular. Required: **CPF + official photo ID + prescription**. Documents are kept for **5 years**
from dispensing (Portaria GM/MS 12.091/2026; 10 years for dispensations up to 2026-08-11). When the Ministry opens a monitoring
procedure, the pharmacy must keep the documents of the 5 years before the notification **until the procedure ends**: a legal hold.

**✍ Dev writes**

```csharp
[Sensitive(DataSensitivity.SensitivePersonal, Category = "health")]
public sealed partial record DispensacaoFarmaciaPopular(
    DispensacaoId Id, Cpf CpfBeneficiario, FullName NomeBeneficiario, DocumentoComFoto Documento,
    Sku Medicamento, Quantity Quantidade, DocumentoDigitalizado Receita, DateOnly Data, string Autorizacao);

p.Retain(FarmaPurposes.Values.FarmaciaPopular).For(years: 5).From(RetentionClocks.SameDay, member: "Data")
 .AsLegalHold("Portaria GM/MS 12.091/2026").Then(ErasureAction.Delete);

// Scanned prescription and ID images live in a file store; the app registers it as a rights participant (07 §4.2 "Files")
services.AddPersonalDataStore<FarmaciaPopularDocumentStore>();
```

**When the Ministry notifies an inspection** (manager with `privacy.legal-hold.manage`):

```csharp
await holds.PlaceAsync(LegalHold.ForPurpose(scope, FarmaPurposes.Values.FarmaciaPopular,   // (new proposal) purpose-scoped hold
    recordsFrom: notificationDate.AddYears(-5), reason: "Procedimento de monitoramento MS nº 2026/123",
    basis: LegalBasis.LegalProceedings, until: null), ct);
```

**👁 Result:** while the hold exists, the sweeper skips those dispensations, and a customer's deletion request shows
`RETAIN — Farmácia Popular documents — inspection MS nº 2026/123 (open)`. Releasing the hold resumes normal retention. The scanned
images are encrypted at rest and exported in an access request (Example 12).

---

## 7. Loyalty program done right ("desconto no CPF") (Tier 2)

**Situation:** the chain wants discounts for members. Lessons from enforcement: Procon-MG fined Drogaria Araujo R$ 7.9 M (2018) for tying
discounts to giving a CPF without informing that a record was opened. In 2025 the ANPD sanction process against a large chain found
CPF collection unnecessary for the stated purposes, and the purchase-history use unclear. Done right means a **separate, informed,
revocable** consent, and the price without the program shown honestly (R9, R10).

**✍ Dev writes**

```csharp
b.Command<AderirFidelidadeCommand, MembroId>().Privacy(p => p.Purpose(FarmaPurposes.Values.LoyaltyProgram));
b.Command<AcumularPontosCommand, Saldo>().Privacy(p => p.Purpose(FarmaPurposes.Values.LoyaltyProgram).RequireConsent());

// medication history for pharmaceutical care is a DIFFERENT purpose, asked separately and highlighted (art. 11 I)
b.Query<HistoricoMedicamentosQuery, IReadOnlyList<ItemHistorico>>()
 .RequirePermission(FarmaPermissions.ServicosFarmaceuticos)
 .Privacy(p => p.Purpose(FarmaPurposes.Values.MedicationHistory).RequireConsent());

p.Retain(FarmaPurposes.Values.LoyaltyProgram, DataCategory.Contact)
 .For(days: 30).From(RetentionTrigger.ConsentWithdrawn).Then(ErasureAction.Delete)
 .AlsoAfter(years: 2, RetentionTrigger.LastActivity);
```

**👁 Result** (consent prompt at the POS, confirmed by the customer on the PIN pad)

```
Programa de fidelidade — Farmácia Centro
Para participar, usamos seu nome, CPF, telefone e as compras feitas com o programa para calcular pontos e enviar ofertas.
Não compartilhamos com laboratórios, seguradoras ou planos de saúde. Você pode sair quando quiser (app, site ou balcão).
[ ] Aceito participar do programa                       ← separate checkbox
[ ] ⚠ DADO DE SAÚDE — Guardar meu histórico de medicamentos para orientação farmacêutica (opcional, em destaque)
Preço sem programa: R$ 42,90 · com programa: R$ 39,90
```

Points accumulate only for members (`ConsentRequired` otherwise). A withdrawal stops accumulation immediately, and the contact data
is deleted 30 days later. The medication-history purpose can be refused without leaving the program.

---

## 8. Discount program of a laboratory / PBM: art. 11 §4 (Tier 2)

**Situation:** a laboratory runs a discount program (PBM). At checkout the pharmacy sends the customer identification and the
medication to the PBM to authorize the discount. The same partner also asks for a **monthly file with purchases per CPF** "for
marketing". Art. 11 §4 forbids sharing health data between controllers for economic advantage, **except** for pharmaceutical
assistance/health services and the financial/administrative transactions resulting from them, or portability with consent (R6).

**✍ Dev writes**

```csharp
p.SharedWith("pbm-labx", "PBM Laboratório X — autorização de desconto", LegalBasis.ContractPerformance,
             DataCategory.Identification, DataCategory.Health)
 .Allow(SharingException.PharmaceuticalAssistanceTransaction);              // (new proposal) art. 11 §4 exception, reviewed

// Authorization call: allowed (transaction of the pharmaceutical service)
var payload = PrivacyJson.Serialize(new AutorizacaoPbm(cpf, sku, quantidade), destination: DataDestination.ExternalSystem,
                                    recipient: "pbm-labx");                // (new proposal) redacting serializer, 08 §8.2 mechanism

// Monthly "marketing" file: same recipient, different purpose → the policy erases health data
var arquivo = PrivacyJson.Serialize(comprasDoMes, destination: DataDestination.ExternalSystem, recipient: "pbm-labx",
                                    purpose: "marketing.partner-analytics");
```

**👁 Result**

```json
// authorization (allowed by the declared exception)
{ "cpf": "12345678909", "ean": "7891234567890", "quantidade": 1 }

// marketing file (no exception for this purpose → health data erased, the export is logged as a policy hit)
[ { "cpf": "<redacted:health-context>", "ean": "<redacted:health>", "quantidade": "<redacted:health>" } ]
```

```
WRN Privacy.SharingBlocked recipient=pbm-labx purpose=marketing.partner-analytics categories=health rows=18330
```

The library cannot decide legal questions, but it makes the default safe and the exception **explicit and reviewable** (ROPA lists it).

---

## 9. Convênio statement sent to the employer (Tier 2)

**Situation:** Farma already has **convênios**: a company pays its employees' purchases and receives a monthly statement. An
itemized statement tells the employer that employee X buys antidepressants: health data reaching the employer (R5, R6).

**✍ Dev writes**

```csharp
public sealed record LinhaExtratoConvenio(string Matricula, FullName Funcionario, Money Total, int Compras);    // totals: default

[Sensitive(DataSensitivity.SensitivePersonal, Category = "health")]
public sealed record ItemExtratoConvenio(string Matricula, Sku Medicamento, Quantity Quantidade, Money Valor);  // itemized

b.Query<ExtratoConvenioQuery, ExtratoConvenio>().RequirePermission(FarmaPermissions.ConvenioExtrato);
b.Query<ExtratoItemizadoQuery, IReadOnlyList<ItemExtratoConvenio>>()
 .RequirePermission(FarmaPermissions.ConvenioExtrato)
 .Privacy(p => p.Purpose(FarmaPurposes.Values.ConvenioItemizedStatement).RequireConsent());   // each employee's consent
```

**👁 Result:** the employer's statement shows `Matrícula 0042 · J. S. · R$ 312,40 · 6 compras`. The itemized version includes only
employees who consented. For the others, the line reads `itens não disponíveis (sem consentimento do funcionário)`.

---

## 10. Pharmaceutical services: glucose, blood pressure, vaccination (Tier 2)

**Situation:** the pharmacist measures capillary glucose and records it in the customer's care record. Basis: health protection by a
health professional (art. 11 II f), so **no consent** is needed. Access is restricted to pharmacists (R7). The customer can see their
own results in the portal.

**✍ Dev writes**

```csharp
[Sensitive(DataSensitivity.SensitivePersonal, Category = "health")]
public sealed partial record AtendimentoFarmaceutico(
    AtendimentoId Id, CustomerId Cliente, TipoServico Servico, decimal? Glicemia, PressaoArterial? Pressao,
    string? Observacao, string RegistroFarmaceutico, DateTimeOffset Em);

b.Command<RegistrarAtendimentoCommand>().RequirePermission(FarmaPermissions.ServicosFarmaceuticos).Audit()
 .Privacy(p => p.Purpose(FarmaPurposes.Values.PharmaceuticalServices));
```

**👁 Result:** the cashier's customer screen shows `Atendimentos farmacêuticos: 3 (restrito ao farmacêutico)`. The pharmacist sees the
values. `Observacao` free text is health data too (record level), so it never reaches logs or AI. The customer's access export
(Example 12) includes the readings.

---

## 11. An error message with a CPF reaches the logs (Tier 1)

**Situation:** a duplicate-key error from the database on customer registration.

**✍ Dev writes:** nothing.

**👁 Result**

```
before: 23505: duplicate key value violates unique constraint "cliente_cpf_cnpj_key" DETAIL: Key (cpf_cnpj)=(12345678909) already exists.
after:  23505: duplicate key value violates unique constraint "cliente_cpf_cnpj_key" DETAIL: Key (cpf_cnpj)=(***.456.789-**) already exists.
```

In Tier 1 logs use the **mask**. Tier 3 can switch logs to pseudonyms (`<cpf #h:9f2c…>`) when support needs to search all lines of one customer.

---

## 12. Customer asks "what do you have about me?" (Tier 2)

**Situation:** art. 18 I–II. Simplified answer immediately, complete answer within 15 days (art. 19). A small pharmacy can start with a
**manual procedure**. The library makes that procedure a button.

**✍ Dev writes:** nothing per request. The participants are registered once:

```csharp
services.AddYFexPrivacyRights(r => r.Participants(x => x.Data().ConsentLedger().Audit()).Add<FarmaciaPopularDocumentStore>());
```

**👁 Result** (request desk, after the customer's identity was verified at the counter: `InPersonDocument`)

```
Titular: J. S. (CPF ***.456.789-**) — Farmácia Centro
cadastro           1 registro    identificação, contato
vendas            37 registros   identificação (CPF na nota)
controlados        2 registros   saúde            (Portaria 344/98)
farmácia popular   4 registros   saúde + documentos digitalizados
fidelidade         1 registro    contato, comportamento (consentimento desde 2026-10-01)
atendimentos       3 registros   saúde
compartilhado com: Sefaz (NF-e), Anvisa/SNGPC (controlados), Ministério da Saúde (Farmácia Popular)
[Gerar cópia completa (ZIP)]
```

---

## 13. Customer asks for deletion: four retention rules collide (Tier 2)

**👁 Result** (dry-run plan, 07 §7.3 applied to the examples above)

```
DELETE     fidelidade (contato, pontos)                        consent basis (art. 18 VI)
DELETE     histórico de medicamentos                           consent basis (art. 18 VI)
ANONYMIZE  cadastro (nome, telefone, e-mail, endereço)         CPF kept only where a hold requires it
RETAIN     vendas.cpf_na_nota (37)          fiscal.document    legal hold until 2031-01-01  (tax law)
RETAIN     controlados (2)                  controlled-drugs   legal hold until 2028-03-14  (Portaria 344/98 art. 64)
RETAIN     farmácia popular (4 + images)    farmacia-popular   legal hold until 2031-08-20  (Portaria GM/MS 12.091/2026)
RETAIN     atendimentos farmacêuticos (3)   health protection  per the tenant's professional-records rule (confirm with the CRF/lawyer)
EXPIRE     logs: masked values only, expire by 2026-10-16
```

The customer receives this in plain Portuguese with each legal basis. Nothing is silently kept.

---

## 14. A manager's notebook with a customer spreadsheet is stolen (Tier 2)

**Situation:** an exported customer list (name, CPF, phone) on a stolen notebook. Res. CD/ANPD 15/2024: register every incident; if it
may cause relevant risk, notify the ANPD and the subjects within **3 business days** (doubled for small agents) (R15).

**✍ Ops writes**

```csharp
await incidents.RegisterAsync(new SecurityIncident(scope, discoveredAt: now, description: "Notebook furtado com planilha exportada",
    affectedExport: exportId), ct);                                                 // (new proposal, 10 §2.1)
```

**👁 Result:** the export was produced by the app, so the export log knows what it contained:
`1 284 subjects · categories: identification (CPF), contact · no health data · deadline for ANPD/subjects: 2026-10-07 (3 business days)`.
The DPO decides whether the risk is relevant. Either way the register keeps the record.

---

## 15. AI assistant at the counter (Tier 2)

**Situation:** the pharmacist uses an assistant backed by a cloud model abroad (international transfer, R16).

**👁 Result**

| Question | What the cloud model receives |
|---|---|
| "Qual a posologia usual de amoxicilina 500 mg em adultos?" | the question (no personal data) |
| "O que o João da Silva comprou mês passado?" | `O que [[PESSOA_1]] comprou mês passado?` plus the tool result with medications **erased** (health data is never sent to cloud models). The answer is "não posso consultar dados de saúde por este assistente" |
| Same question with a **local** model allowed for `Personal` | the name in clear, medications still as placeholders unless the tenant allows `SensitivePersonal` for that local model |

---

## 16. Children: pediatric prescription vs kids' loyalty account (Tier 2)

| Case | Basis | Guardian consent? |
|---|---|---|
| Amoxicillin for a 6-year-old (Example 5) | legal obligation (RDC 471/2021) | **no**: Enunciado CD/ANPD 1/2023 allows any art. 7/11 basis if the child's best interest prevails |
| "Clubinho" loyalty account for a 10-year-old | consent | **yes**: art. 14 §1, at least one parent, specific and highlighted (`Guardian` on the ledger entry) |
| Loyalty account for a 15-year-old | consent | tenant policy (04 §12). Conservative default: yes for health purposes |
