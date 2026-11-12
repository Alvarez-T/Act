# 07 — Data subject rights, retention & legal holds

## 1. The rights and what the system does

| LGPD | Right | System behaviour | Deadline |
|---|---|---|---|
| art. 18 I | confirmation that data is processed | locate across participants → yes/no + categories | simplified: immediately; complete: 15 days (art. 19) |
| art. 18 II | access | export package (§5) | idem |
| art. 18 III | correction | routed to the app's own commands (§6) | — (reasonable) |
| art. 18 IV | anonymization, blocking or deletion of unnecessary/excessive/unlawful data | erasure plan with `Anonymize`/`Block`/`Delete` actions (§7) | — |
| art. 18 V | portability | export package in a structured, documented format (§5) | per ANPD regulation |
| art. 18 VI | deletion of data processed **with consent** | erasure plan restricted to consent-based purposes; art. 16 exceptions → `Retain` with basis | — |
| art. 18 VII | information on sharing | `SharedWith` declarations (03 §5.5) filtered by the subject's categories | with the access response |
| art. 18 VIII | information on not consenting and consequences | purpose descriptors + notices (04) | at collection |
| art. 18 IX | revocation of consent | ledger `Withdrawn` entry (04 §6) | immediate |
| art. 18 §2 | objection to processing based on legitimate interest | ledger `Denied` entry for a `LegitimateInterest` purpose → treated like a withdrawal for that purpose | — |
| art. 18 §6 | inform processors/recipients of correction/deletion | manual tasks generated per `SharedWith` recipient (§4.3) | — |
| art. 20 | review of automated decisions | `AutomatedDecisionReview` request: attaches the decision trace (YFex.Rules `Trace`, validation 06) and routes to a human reviewer | — |

## 2. Who is the subject? (⚑P10)

- **The tenant is the controller.** The same CPF at two pharmacies is two data subjects for two controllers. Every request, key,
  ledger entry and erasure is scoped by `KeyScope(tenant)`. A request never crosses tenants.
- A subject is identified by `DataSubjectReference(Person, "customer", "<customer id>")`. Id types carry `[DataSubjectKey("customer")]`
  once (02 §4).
- **People who act** (staff, account holders) appear in records as YFex.Actors `ActorIdentity` values (`user:8f3a2c@0=farmaviva`), whose
  subject is the opaque account id, never a name. Their data subject is the account (`DataSubjectReference(Person, "user", subject)`);
  erasure marks that subject erased in the actor directory (`ActorState.Erased`, actors AC2), so audit entries keep the opaque id and
  render "removido" — the chain is never rewritten.
- **Natural identifiers resolve to subjects** through the identifier column: by blind index when the column is encrypted
  (`Pseudonymize(cpf, Identifier("cpf"))`), by canonical value otherwise. The request record, the erasure log and the audit keep only
  the pseudonym, never the clear identifier. The **resolver is generated** from the inventory: any
  entity that has a `SubjectKey` member and an `Identifier` member in the same row (`ClienteEntity { Id: CustomerId, CpfCnpj, Email }`)
  yields a lookup query. An app can replace it with its own `IDataSubjectResolver`.
- **Identity verification before anything is disclosed or destroyed.** A request for someone else's data is itself a breach. Levels
  (tenant policy picks the minimum per request kind):

| Level | How | Typical use |
|---|---|---|
| `Authenticated` | the subject is the logged-in account holder (portal) | access, consent changes |
| `ContactConfirmed` | OTP sent to an e-mail/phone **already on file** (not one supplied in the request) | access, portability |
| `InPersonDocument` | operator checked an ID document at the counter; operator + document type recorded (no copy kept) | deletion at the store |
| `LegalRepresentative` | guardian or attorney with evidence | minors, deceased |

## 3. Request lifecycle

```csharp
namespace YFex.Privacy.Rights;

public enum PrivacyRequestKind : byte   // closed: the law defines the rights
{
    Confirmation = 1, Access = 2, Correction = 3, Anonymization = 4, Blocking = 5, Deletion = 6,
    Portability = 7, SharingInformation = 8, ConsentRevocation = 9, Objection = 10, AutomatedDecisionReview = 11,
}

// Cold path: the native union is fine here.
public sealed record Received(DateTimeOffset At, RequestChannel Channel);
public sealed record AwaitingVerification(VerificationLevel Required, DateTimeOffset ExpiresAt);
public sealed record InProgress(DateTimeOffset StartedAt, ImmutableArray<ParticipantProgress> Participants);
public sealed record AwaitingManualTasks(ImmutableArray<ManualTask> Tasks);                 // processors to notify, paper archives…
public sealed record Completed(DateTimeOffset At, PrivacyRequestReport Report);
public sealed record Rejected(DateTimeOffset At, string Reason);                             // e.g. identity not verified
public readonly union PrivacyRequestState(Received, AwaitingVerification, InProgress, AwaitingManualTasks, Completed, Rejected);

public sealed record PrivacyRequest(
    string RequestId, string Tenant, PrivacyRequestKind Kind, DataSubjectReference Subject,
    ImmutableArray<string> Purposes,          // optional restriction ("delete my loyalty data only")
    PrivacyRequestState State, DateTimeOffset DueAt);   // Received + 15 days by default; tenant policy may shorten
```

The aggregate is a normal YFex.Cqrs aggregate (`PrivacyRequests.Commands.Submit/Verify/Execute/CompleteManualTask/Reject`,
`PrivacyRequests.Queries.Get/ListOpen/ListOverdue`). Execution fans out to participants in parallel with retries (YFex.Resilience
policies), and each participant reports its own outcome. A request is `Completed` when every participant succeeded, partially
retained data is explained, and manual tasks are closed. Every transition goes to the Audit channel. Overdue requests raise an alert
(logging 08 §5).

## 4. Participants: every store that holds personal data

### 4.1 Contract

```csharp
namespace YFex.Privacy.Rights;

public interface IPersonalDataStore
{
    string Name { get; }                                         // "sql:farma", "events:farma", "consent-ledger", "token-vault"…

    /// What do we hold about the subject? Counts and categories, no values (used for confirmation and to plan erasure).
    ValueTask<SubjectFootprint> LocateAsync(SubjectQuery query, CancellationToken cancellationToken);

    /// Writes the subject's data into the export package (values in clear: it is their data).
    ValueTask ExportAsync(SubjectQuery query, IPersonalDataExportWriter writer, CancellationToken cancellationToken);

    /// Executes the part of the erasure plan that concerns this store. Idempotent: running it twice is harmless.
    ValueTask<ParticipantOutcome> EraseAsync(SubjectQuery query, ErasurePlan plan, CancellationToken cancellationToken);
}

/// Identifiers: blind indexes for encrypted columns; canonical values for clear columns (in memory only, during execution;
/// the persisted request keeps the indexes). Needed for rows without a subject key (a sale with only "CPF na nota").
public sealed record SubjectQuery(KeyScope Scope, DataSubjectReference Subject, ImmutableArray<Pseudonym> IdentifierIndexes,
                                  ImmutableArray<IdentifierValue> IdentifierValues, ImmutableArray<string> Purposes);

public sealed record SubjectFootprint(string Store, ImmutableArray<FootprintEntry> Entries);   // (type, store location, member count, categories, record count)
```

### 4.2 Implementations

| Participant | Where | Locate | Export | Erase |
|---|---|---|---|---|
| **SQL entities** | YFex.Privacy.Data, driven by **YFex.Data generated metadata** (08 §3) | `SELECT count(*)` per entity by subject key column, or identifier column (blind index if encrypted) | typed rows via the entity's generated reader → JSON by personal-data shape | per member action of the plan: `DELETE` row, `UPDATE … SET col = NULL / anonymized`, or skip (`Retain`) |
| **Event streams** | YFex.EventSourcing integration (08 §4) | streams tagged with the subject (stream metadata) | decrypt sealed parts with the subject key | `DestroySubjectKey` (shred); projections: re-project or SQL participant |
| **Consent ledger** | YFex.Privacy | entries by subject | all entries + notice texts | entries are pseudonymized (subject → blind index) and **kept as proof** under a legal hold "consent proof" (art. 8 §2 puts the burden of proof on the controller) |
| **Token vault** | YFex.Privacy | tokens by subject | values (decrypted) | delete tokens |
| **Audit** | YFex.Logging.Audit | entries mentioning the subject (sealed fields + subject index) | who accessed/changed the subject's data (good practice for access requests) | shred (key destroyed, chain intact) |
| **Diagnostic logs** | — | — | — | not a participant: pseudonyms only, retention-limited; the report states "expire by {date}" |
| **Devices** (Persistence snapshots, Rpc outbox and client cache, State.History persistence, search frecency) | each library, via the `SubjectErased` signal (08 §2, §6) | — | — | devices purge entries tagged with the subject's index; the report lists acknowledged/pending devices |
| **YFex.Brain master data** | SQL participant over its EF tables (Brain uses EF Core: an EF-specific participant reading the same inventory) | by `CpfCnpj` index, entity id | entity + sources (`RawPayload` decrypted) | delete entity + children; `RawPayload` sealed with the subject key → shred |
| **Search index** | YFex.Search | documents tagged with subject | — | delete documents; frecency entries for the subject's entity keys |
| **Files** (DANFE PDFs, e-mails sent, report exports) | app-provided participant | app-specific | app-specific | app-specific; often `Retain` (fiscal) |

### 4.3 Manual tasks

Recipients declared with `SharedWith` (03 §5.5) become manual tasks: "Inform payment provider X of the deletion of subject Y" (art.
18 §6). The task closes with a note or an attachment hash. SEFAZ (NF-e) is listed as **not erasable** (legal obligation), and the
report says so.

## 5. Export (access and portability)

The package is a ZIP produced by `PersonalDataExportPackage` (BCL `ZipArchive`):

```
export-<request id>.zip
  README.md                 human-readable summary: controller, DPO, categories, purposes, sharing, retention, how to contest
  subject.json              machine-readable data, one section per participant/type (portability)
  subject.schema.json       JSON Schema of subject.json (generated from the inventory; x-sensitivity, x-category)
  consents.json             ledger entries + the exact notice texts consented to
  sharing.json              recipients (SharedWith) for the subject's categories
```

- **Delivery:** authenticated download in the privacy portal, link valid 7 days, the file is deleted after download or expiry. The
  package is personal data too: it is never e-mailed as an attachment.
- **Third parties inside the subject's records** (the prescribing doctor in a prescription, another customer in a shared account) are
  masked in the export. The rule: values of members whose subject is a *different* `DataSubjectReference` are masked. Shapes know
  which members are subject keys.
- `subject.json` keeps member names and paths from the inventory, so a portability recipient can map them. It is not tailored per
  recipient.

## 6. Correction

Generic field writes would bypass validation, rules, invariants and audit, so they are not offered. A correction request carries the
member path and the proposed value. The app maps it to its own command once:

```csharp
p.Corrections(c => c
    .For<Email>("contact", (subject, value) => Clientes.Commands.AtualizarContato(subject.As<CustomerId>(), email: value))
    .For<FullName>("identification", (subject, value) => Clientes.Commands.AtualizarNome(subject.As<CustomerId>(), value)));
```

Self-service (portal, `Authenticated`) runs the command directly. Otherwise an operator reviews it. Corrections of data shared with
recipients produce manual tasks (§4.3).

## 7. Erasure, anonymization, retention and legal holds (⚑P20, ⚑P21)

### 7.1 Retention rules are declared by the module that knows them

The library is domain-agnostic. It provides the mechanism. Modules declare the rules, including legal minimums:

**✍ Dev writes** (Fiscal module, once; Farma app)

```csharp
// YFex.Fiscal: legal obligation, counted from the first day of the year after issue (prescription period)
p.Retain(FiscalPurposes.Values.FiscalDocument)
 .For(years: 5).From(RetentionClock.StartOfNextYear, member: "DataEmissao")
 .AsLegalHold("Obrigação fiscal: guarda de documentos fiscais por 5 anos")
 .Then(ErasureAction.Anonymize);

// Farma app
p.Retain(FarmaPurposes.Values.LoyaltyProgram, DataCategory.Contact)
 .For(days: 30).From(RetentionTrigger.ConsentWithdrawn).Then(ErasureAction.Delete)
 .AlsoAfter(years: 2, RetentionTrigger.LastActivity);                       // inactive members are purged too

p.Retain(FarmaPurposes.Values.MedicationHistory)
 .For(years: 2).From(RetentionTrigger.LastActivity).Then(ErasureAction.Delete);
```

```csharp
namespace YFex.Privacy.Rights;

public enum ErasureAction : byte { Delete = 1, Anonymize = 2, Shred = 3, Block = 4 }      // Block = keep, but no processing (art. 18 IV)
public enum RetentionTrigger : byte { Created = 1, LastActivity = 2, ConsentWithdrawn = 3, RelationshipEnded = 4, MemberDate = 5 }

/// Domain-agnostic date arithmetic for retention starts. Modules add their own (fiscal year end, academic year…).
public delegate DateOnly RetentionClock(DateOnly memberDate);
public static class RetentionClocks
{
    public static DateOnly SameDay(DateOnly date) => date;
    public static DateOnly StartOfNextYear(DateOnly date) => new(date.Year + 1, 1, 1);
}
```

Tenant policy may **extend** retention up to a configured maximum. It can **never shorten** a rule marked `AsLegalHold`.

### 7.2 Legal holds

Two kinds, both block erasure and both show in the response with basis and end date:

| Kind | Source | Example |
|---|---|---|
| **Statutory** | a retention rule marked `AsLegalHold` | fiscal documents 5 years; consent proof; controlled-drug records (rule declared by the module from the ANVISA regulation) |
| **Case** | `LegalHold` record created by an authorized user (permission `privacy.legal-hold.manage`), audited | litigation, ANPD inquiry, police request; scoped to tenant / subject / purpose / record set; open-ended until released |

### 7.3 The erasure plan

`ErasurePlanner` combines the inventory (what is where), retention rules and holds (what must stay) and the request restriction
(which purposes) into one plan per participant. Each entry is `(store, type, member path, action, reason, until)`. The plan is shown to
the operator before execution (dry run) and hashed into the audit record.

**👁 Result** (dry run for a pharmacy customer asking for deletion)

```
Erasure plan — request PR-2026-0042 — subject customer:7f3a… — tenant farma-centro
DELETE     sql  fidelidade (2 rows)                         marketing.loyalty         consent withdrawn
DELETE     sql  historico_medicamento (14 rows)             health.medication-history consent basis (art. 18 VI)
ANONYMIZE  sql  cliente (1 row: nome, email, telefone, endereco → null; cpf_cnpj kept for fiscal)  sale
RETAIN     sql  venda.cpf_na_nota (37 rows)                 fiscal.document  legal hold until 2031-01-01  → then ANONYMIZE
RETAIN     events venda (37 sealed parts)                   fiscal.document  legal hold until 2031-01-01  → then SHRED
RETAIN     consent-ledger (6 entries, pseudonymized)        consent proof    legal hold until 2031-10-01
DELETE     token-vault (3 tokens)
SHRED      audit (subject key) — scheduled after the last legal hold ends (2031-10-01)
EXPIRE     diagnostic logs: pseudonyms only, expire by 2026-10-15
SIGNAL     devices: SubjectErased → 4 devices (3 acknowledged, 1 offline: CAIXA-03)
MANUAL     inform "payment-provider" (shared contact data)
```

Note what the plan shows: deletion is rarely all-or-nothing. The subject key cannot be destroyed while sealed fiscal data must remain
readable, so the shred is **scheduled**, and the vault keeps a `ScheduledDestruction` record that the sweeper executes.

### 7.4 Anonymization proper (art. 5 III, art. 12)

`Anonymize` sets personal members to `default`/null and keeps non-personal facts (amounts, products, dates) for statistics. For
analytics exports that need some personal dimensions, generalization helpers produce coarse values: birth date → year band, CEP → first
5 digits, city kept, address dropped. Re-identification by combination is the main risk (10 §1.14). Exports through `Analytics`
pseudonyms rotate yearly.

### 7.5 The sweeper

A YFex.Jobs `[Job]` per tenant, daily: evaluates retention rules through the generated SQL of each entity (`WHERE <trigger column> <
@cutoff AND NOT EXISTS hold`), executes due actions in batches, executes scheduled key destructions, and reports to the Audit channel.
Entities without a usable trigger column are listed as "retention not enforceable" in the ROPA (a review item).

## 8. Backups, restores and re-imports

- **Erasure log** (`YFex.Privacy.Data`): `(tenant, subject identifier indexes, request id, plan hash, executed at)`. It holds only
  pseudonyms and is kept for the longest backup retention.
- **Restore procedure:** after restoring a database backup, `yfex-privacy replay-erasures --since <backup timestamp>` re-executes
  every erasure executed after the backup was taken, before the system serves users. The restore runbook makes it mandatory, and a
  startup check refuses to serve if the database's "last erasure replayed" marker is older than the erasure log.
- **Re-imports:** a migration from a legacy ERP (YFex.DataMining) or a CSV import must not resurrect erased people. Importers check
  identifier indexes against the erasure log and skip matches (reported).
- **Built 2026-11-11 (request PR1, databases-and-backups ⚑DB21):** `YFex.Privacy.Erasure` — `ErasureRecord` (scope, subject kind,
  pseudonym/blind index, request id, executed at, optional destroyed subject key id), `IErasureLog` with `InMemoryErasureLog` and
  `FileErasureLog` (sites, on-prem, first backup phase), `IErasureReplayTarget` + `ErasureReplayer.ReplaySinceAsync`. Not built yet:
  the SQL log (`YFex.Privacy.Data`), the "last erasure replayed" marker and startup check, the `replay-erasures` CLI, and the erasure
  engine that appends to the log (nothing calls it until Privacy.Rights exists).
- **Vault backups** have a short retention (05 §8) so crypto-shredding completes in bounded time.

## 9. The response to the subject

`PrivacyRequestReport` renders (Markdown → portal page / PDF by the app) what was done per right: data found (categories, not values,
for confirmation), the package link (access/portability), what was deleted/anonymized, **what was retained, why (basis), and until
when**, who the data was shared with, and how to contact the DPO or the ANPD. The report is stored with the request (audit) for the
legal limitation period.
