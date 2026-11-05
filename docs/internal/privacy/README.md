# YFex.Privacy — Data Classification, Consent, Redaction & Data Subject Rights — Design

> **Status:** Tier 1 BUILT (2026-10-02): core contract in `YFex/src/YFex/Privacy/`, generator in repo `YFex.Privacy`, logging
> integration (log in [11](11-phases-and-acceptance.md)). Tier 1 remainder (`TextScrubber`, UI mask presenter, existing leaks) and
> Tier 2/3 are designed, not built. Open decisions are marked **⚑P#** (the list is at the end of this file). No 3rd-party package is added without explicit approval. The design uses only the BCL
> (`HMACSHA256`, `HKDF`, `AesGcm`, `ECDsa`, `SHA256`, `[GeneratedRegex]`, `RandomNumberGenerator`).
> **Audience:** the user first, then the AI sessions that will implement it. Read this file, then only the files your phase needs
> (see [11-phases-and-acceptance.md](11-phases-and-acceptance.md)).
> **Normative vs sketch:** type names, member names and signatures in code blocks are **normative** unless the block is marked
> `// (sketch)`. Comments inside code explain *how it works*. Keep them (adapted) as XML docs when implementing.
> **Relation to YFex.Logging:** this design **takes over** everything privacy-related that the logging design (`../logging/`)
> placed in YFex core or in the logging engine: `[Sensitive]`, `DataSensitivity`, redaction policies, the free-text scrubber and
> crypto-shredding keys (logging 02 §5.2, 04 §3, 07 §3–§6, 12 §6). It also corrects one claim there (⚑P3). Logging becomes a
> **consumer** of YFex.Privacy.

> **Scope (2026-10-02):** the design is complete for future features, but it is built in **tiers**. Start with
> [00-requirements-and-scope.md](00-requirements-and-scope.md): it maps every feature to its legal source (LGPD article, ANPD
> resolution, sanitary/fiscal rule), the business feature that triggers it, and its tier. **Tier 1** (build now) = classification on
> value objects and record types + masks + log/UI integration + scrubber. **Tier 2** = built together with the feature that requires it
> (health records, loyalty/consent, rights automation, AI, event sourcing, incidents). **Tier 3** = optional tooling (analyzers, name
> conventions, snapshot, CLI, pseudonyms, blind indexes). Concrete usage: [13-pharmacy-usage-examples.md](13-pharmacy-usage-examples.md).

---

## Why this exists

Personal data flows through almost every YFex library, but privacy code today is **scattered, partial and in places wrong**
(full inventory in [01-current-state-and-inventory.md](01-current-state-and-inventory.md)):

- **Consent** exists twice. YFex core has `YFex.Consent` (`ConsentLevel`, `ConsentState`, `ConsentRecord`, `IConsentStore`), and only
  YFex.Profiling uses it. `ConsentRecord` holds six per-source opt-ins (browser data, traffic interception…) that **nothing reads
  or persists**. The offline queue rebuilds the consent of a queued event as `(UtcNow, "restored")`, so **proof of consent is lost**.
  A four-step ladder cannot express LGPD's "consent for **determined purposes**" (art. 8 §4), and most business processing
  (sales, NF-e) does not rest on consent at all.
- **Scrubbing** is key-name based (`url`, `title`, `mac`, `ip`). It has **no CPF/CNPJ/e-mail/phone patterns**, even though the
  logging design assumed it does. `"multipleMonitors"` gets masked because it contains `ip`. `"machineName"` gets hashed because it
  contains `mac`. `"userName"` ships in clear.
- **Hashing** of personal values uses `SHA256(value + salt)` with a salt from configuration (Profiling ×3), or **no salt at all**
  (YFex.System hashes MAC addresses with plain SHA-256). Low-entropy values (CPF: ~10⁹ valid numbers, IPv4, MAC) are brute-forced
  from such hashes in seconds.
- **Crypto** is implemented four times with four formats: `YFex.Persistence.Encryption` (`AesGcmValueProtector`, version 0x01),
  `YFex.Web.Data` (`AesSecretProtector`, no version byte), `YFex.Rules` (`OverrideTokens` HMAC key ring), `YFex.Messaging.Rpc`
  (`EventChannel` HMAC). Automation's `PortableSecretStore` writes secrets **in plain text**.
- **Personal data in raw strings** everywhere: Farma view models and models (`string CpfCnpj`, `Email`, `Phone`, `Address`, `Cep`),
  `CreateCustomerCommand(string Name, string Email)`, Payment `Payer.Email/Phone`. Meanwhile YFex.Domain already has `Cpf`,
  `Cnpj`, `CpfCnpj`, `Email`, `Telefone`, `Rg`, `FullName`, `PartyName`.
- **Nobody can answer "where do we store CPF?"** or execute a data subject's request (LGPD art. 18) across YFex.Data, event streams,
  device caches, search, logs and audit.
- **AI calls** (YFex.Automation `IAiAgent`, YFex.Capabilities tool I/O, DataMining's `AiSemanticWorm`) have no redaction step
  before the prompt leaves the machine.

Goal: **one library that knows what data is, why it is processed, who may see it, how long it may be kept, and how to give it
back or make it disappear.** Developers do almost nothing: they classify a value-object type **once**, and every library
(logging, UI, persistence, AI, schemas, rights tooling) applies the right treatment automatically.

## The mental model — classify once, decide by purpose, act through primitives

```
             DECLARE (compile time, once)                       DECIDE (run time, per tenant)               ACT (primitives)
 ┌──────────────────────────────────────────────┐   ┌────────────────────────────────────────┐   ┌───────────────────────────────────┐
 │ [Sensitive(PersonalIdentifier, Mask=…)] Cpf  │   │ Purposes + legal basis (art. 7 / 11)   │   │ Mask      ***.456.789-**          │
 │ [DataSubjectKey("customer")] CustomerId      │──▶│ Consent ledger (per subject, purpose)  │──▶│ Pseudonymize  #h:9f2c41d07a3be1c2 │
 │ conventions for raw members + snapshot       │   │ Policy: redaction, reveal, retention,  │   │ Tokenize  tok_7Q2M…  (vault)      │
 │ [PurposeCatalog] app purposes                │   │ AI allowance, legal holds              │   │ Encrypt   AES-GCM, subject key    │
 └──────────────────────────────────────────────┘   └────────────────────────────────────────┘   │ Shred     delete the subject key  │
        │ generator: IClassifiedData<T>, personal-data shapes,                                     │ Scrub     free text, exceptions   │
        │ PrivacySchema snapshot, data inventory, analyzers                                        └───────────────────────────────────┘
        ▼
 consumers: Logging · UI (Avalonia/Blazor) · Persistence/Data · EventSourcing · Cqrs/Validation schemas · Rpc · AI/Capabilities · Rights tooling
```

**Principle A — sensitivity is a property of the data type, not of the place it appears.** `Cpf` is classified once on the struct.
Every command, entity, event, DTO and view model that holds a `Cpf` inherits the classification. Raw members are classified by
convention (name and type), the result goes into a committed snapshot, and attributes on members are rare exceptions.

**Principle B — processing is justified by a purpose, not by a consent level.** Each purpose has one legal basis from the closed
set the law defines (art. 7 / art. 11). Consent is only checked for purposes whose basis is consent. Legal obligations (NF-e, SNGPC,
accounting) never ask, and a withdrawal never breaks them.

**Principle C — declare in core, act in YFex.Privacy.** Contract assemblies (`*.Public`, value objects) need only YFex core and the
generator as an analyzer. Everything that *does* something (keys, hashing, encryption, consent, rights) lives in the YFex.Privacy
repo. This mirrors the logging design's probe contract (logging ⚑L1).

**Principle D — make data disappear by destroying keys, not by rewriting history.** Immutable stores (audit hash chains, event
streams, backups) hold personal fields encrypted with a **per-data-subject key**. Erasure deletes the key (crypto-shredding).
Mutable stores delete or anonymize rows, guided by the generated inventory, with legal holds (fiscal: 5 years) taking precedence.

**Principle E — the hot path pays a branch.** Classification is static data. Masks and canonical forms are generated span code.
Consent checks are a bit test on a snapshot. HMAC and encryption run off the caller's thread wherever a pipeline exists (the logging
consumer, persistence writers).

## Files

| # | File | Contents |
|---|---|---|
| 00 | [requirements-and-scope](00-requirements-and-scope.md) | **Read first.** What the law really requires (R1–R20 with sources), when each feature becomes necessary, tiers, myths, decision changes |
| 01 | [current-state-and-inventory](01-current-state-and-inventory.md) | Every consent/scrub/hash/mask/crypto/personal-data site across all repos, defects found, old → new mapping |
| 02 | [data-classification](02-data-classification.md) | `DataSensitivity` (7 values), `DataCategory`, `[Sensitive]`, `DataClassification`, `IClassifiedData<T>`, unions, mask language, resolution rules, the core/library split |
| 03 | [generator-analyzers-and-inventory](03-generator-analyzers-and-inventory.md) | `YFex.Privacy.Generator`: emitted code, personal-data shapes and visitors, `PrivacySchema` snapshot, analyzers YFP*, data inventory, "where do we store CPF?", ROPA report, CLI |
| 04 | [consent-and-purposes](04-consent-and-purposes.md) | Purposes, `LegalBasis`, `[PurposeCatalog]`, privacy notices, consent ledger, withdrawal, proof, offline devices, per-tenant policy, evaluation API, Profiling/Logging/Authorization/Cqrs integration |
| 05 | [redaction-and-cryptography](05-redaction-and-cryptography.md) | `RedactionAction`, masks, HMAC pseudonyms, blind indexes, tokenization, AES-GCM envelope, key hierarchy, `IKeyVault`, rotation, crypto-shredding |
| 06 | [free-text-scrubbing](06-free-text-scrubbing.md) | `TextScrubber`: prefilter, `[GeneratedRegex]`, checksum validation, detectors from value objects, span API, exceptions/URLs/AI prompts, migration of `PrivacyScrubber` |
| 07 | [data-subject-rights](07-data-subject-rights.md) | LGPD art. 18 requests: lifecycle, identity verification, participants per store, export, correction, anonymization, erasure, retention, legal holds, backups |
| 08 | [integrations](08-integrations.md) | Per library: UI, Persistence, Data, EventSourcing, Cqrs/Validation, Messaging.Rpc, Logging, AI (Automation, Capabilities, Brain, DataMining), Tenancy, Authorization, Search, Profiling/System/Security |
| 09 | [usage-examples](09-usage-examples.md) | End to end in a pharmacy: dev writes / generated / result for classification, consent at the POS, a sale, logs, AI, export, erasure with fiscal hold |
| 10 | [pitfalls-and-suggestions](10-pitfalls-and-suggestions.md) | Legal and technical pitfalls, what the brief does not mention, ecosystem suggestions |
| 11 | [phases-and-acceptance](11-phases-and-acceptance.md) | Phases, tasks, acceptance criteria, migration order, what each session loads |
| 12 | [use-case-catalog](12-use-case-catalog.md) | 50+ scenarios by audience (development, screens, consent, logs/audit, storage/keys, AI, subject rights, retention/operations, tests): situation, dev writes, result |
| 13 | [pharmacy-usage-examples](13-pharmacy-usage-examples.md) | 16 detailed examples from real pharmacy features (controlled drugs, antibiotics, Farmácia Popular, loyalty, PBM/art. 11 §4, convênio, pharmaceutical services, rights, incidents, AI, children) |

## Projects (⚑P1: new sibling repo `C:\Dev\YFex\YFex.Privacy`, `privacy.slnx`, like Tenancy/Jobs/Logging)

```
YFex/src/YFex/Privacy/              (YFex core) DECLARATION CONTRACT ONLY (⚑P2, 02 §1):
                                     DataSensitivity, DataCategory, DataClassification(+Flags), SensitiveAttribute,
                                     DataSubjectKeyAttribute, NotPersonalDataAttribute, SensitiveTypeAttribute<T> (assembly),
                                     IClassifiedData / IClassifiedData<TSelf>, IPersonalDataShape<TSelf>, IPersonalDataVisitor,
                                     PersonalDataMember(+Source), PersonalDataShapes (registry), Masks, CanonicalForm, Classified
                                     (16 small types, span-only helpers)
YFex.Privacy/src/
  YFex.Privacy                  runtime primitives: masks, Pseudonymizer + Pseudonym, BlindIndex, TextScrubber + detectors,
                                AesGcmEnvelope, KeyRing, IKeyVault/IRootKeyProvider (+ in-memory, file, environment), ITokenVault,
                                RedactionPolicy/RedactedValue, purposes (Purpose, LegalBasis, PurposeSet), consent model + ConsentSnapshot
                                + IConsentLedger + device-side ConsentClient, PrivacyNotice, PseudonymizationSession (AI),
                                PersonalDataInventory registry, redacting JSON helpers          (→ YFex core, YFex.Tenancy)
  YFex.Privacy.Generator        netstandard2.0: IClassifiedData<T> impls (masks, canonical form, per-case classification),
                                personal-data shapes + visitors, [PurposeCatalog], inventory tables, analyzers YFP0001–YFP0099;
                                shared emit file linked into other generators (03 §6)
  YFex.Privacy.Rights           data subject requests (PrivacyRequest aggregate, messages), IPersonalDataStore participants,
                                orchestration, export package writer, retention policies, legal holds, erasure log, sweeper jobs
                                (→ Privacy, Cqrs, YFex.Jobs, YFex.Authorization)
  YFex.Privacy.Data             SQL stores over YFex.Data: consent ledger, key vault, token vault, requests, legal holds, erasure log;
                                YFex.Data participant (locate/export/erase from generated entity metadata) (→ Privacy, Privacy.Rights, YFex.Data)
  YFex.Privacy.Testing          PrivacySchemaSnapshot.Verify, InventorySnapshot, fake vault/clock, ShouldNotContainPersonalData assertions
  tools/YFex.Privacy.Cli        `yfex-privacy` inventory / where / ropa / verify (reads committed snapshots, no assembly loading)
  samples/YFex.Privacy.AotSmoke · benchmarks/YFex.Privacy.Benchmarks · tests/*
```

Thin integrations live **inside the owning libraries** (same rule as validation and logging): the YFex.Data generator emits blind
indexes and erasure metadata, the EventSourcing generator emits the two-part event payload, YFex.UI ships the masking presenter,
Automation ships the redacting `IAiAgent` decorator, and so on (08). They reference YFex core (declarations) and, when they need a
primitive, `YFex.Privacy`, never `YFex.Privacy.Rights`.

**Dependency layers**

```
Layer 0   : YFex core — YFex.Privacy declaration contract (no dependencies, net11, AOT)
Contracts : YFex.Entity.Public, *.Public       → YFex core + YFex.Privacy.Generator as ANALYZER ONLY (no runtime reference)
Runtime   : YFex.Privacy (→ YFex core, YFex.Tenancy) — must NOT reference Cqrs/Data/Logging/UI (they consume it)
Consumers : YFex.Logging engine, YFex.Persistence.Encryption, YFex.Data, YFex.EventSourcing, YFex.UI/Avalonia, YFex.Capabilities,
            YFex.Automation.Core, YFex.Profiling, YFex.Security, YFex.Search … → YFex.Privacy
Server    : YFex.Privacy.Rights (→ Privacy, Cqrs, Jobs, Authorization) · YFex.Privacy.Data (→ Rights, YFex.Data)
```

## Hard constraints (apply to every file)

1. **AOT & trimming:** `IsAotCompatible=true`, trim analyzers on, `TreatWarningsAsErrors`. No reflection, no `Expression`, no `dynamic`.
   Classification, shapes, masks, purposes and the inventory are **generated static data**. JSON only through source-generated contexts.
2. **BCL cryptography only.** `HMACSHA256.HashData`, `HKDF`, `AesGcm`, `ECDsa`, `SHA256`, `RandomNumberGenerator`,
   `CryptographicOperations.ZeroMemory/FixedTimeEquals`. No BouncyCastle, no format-preserving encryption library (⚑P14).
3. **Zero allocation on hot paths:** masking, canonical forms, pseudonyms and scrubbing write into caller-provided `Span<char>`/`Span<byte>`
   or `IBufferWriter<T>`. Consent checks are bit tests. No LINQ, no closures, no params arrays on those paths.
   `[Union]` + `IUnion` tagged structs (pattern of `YFex.Cqrs/Result.cs`) on hot paths. The native `union` keyword is fine on cold paths
   (request lifecycle, ledger reads).
4. **Domain-agnostic base types.** The library knows *sensitivity levels* and *legal bases* (closed sets: we and the law own them). It does
   not know CPF, NF-e, tenants-as-stores or Money. Brazilian document detection lives with the Brazilian value objects (06 §4). Fiscal
   retention is declared by the fiscal module (07 §7). Data categories, purposes and subject kinds are **app-declared** open sets.
5. **Own types first:** `Pseudonym`, `DataToken`, `KeyId`, `EncryptedValueReader`, `ConsentSnapshot`, `PurposeSet`. BCL types where
   they are the standard (`AesGcm`, `Utf8JsonWriter`).
6. **No per-member boilerplate.** Classification goes on the value-object type. Members are classified by type and convention, and the
   result is reviewed in a committed snapshot (`PrivacySchema.verified.txt`). Member attributes exist only as exceptions.
7. **Fail closed.** Unknown classification of a captured string → scrubbed. Missing key → value unreadable, never "returned in clear".
   `Financial`/`Secret` can never be relaxed by configuration.
8. **net11 preview, `LangVersion=preview`. `YFex.System` shadows `System`:** generated code always uses `global::` qualified names.
9. **Explicit names, no abbreviations** (`Pseudonymizer`, not `Pseud`; `DataSubjectReference`, not `SubjectRef`).

## Decisions to confirm (⚑)

| # | Proposal | Recommendation |
|---|---|---|
| ⚑P1 | Where the library lives | **New sibling repo `C:\Dev\YFex\YFex.Privacy`** (`privacy.slnx`): it has server parts (rights orchestration, vault, SQL stores), depends on Tenancy/Jobs/Authorization, and is consumed by sibling repos. YFex core keeps only the declaration contract |
| ⚑P2 | What stays in YFex core (`YFex.Privacy` namespace) | **The declaration contract, 16 small types** (02 §1): enum, category, classification struct, 4 attributes, `IClassifiedData(<T>)`, shape + visitor contracts, span-only mask/canonical helpers called by generated code. Justification: `*.Public` contracts and value objects must declare and expose classification without a runtime dependency on the privacy engine, exactly like logging's probe contract. Alternative: attribute + enum only (then UI/logging/AI each re-derive masks and classification, with no runtime contract) |
| ⚑P3 | `DataSensitivity` values (clarified 2026-10-02: devs only *write* Personal…Secret; no attribute = not personal; `Cnpj`/`CompanyName` get no attribute) | **7 values, adding `PersonalIdentifier`** (CPF, RG, CNH, passport, NIS). CPF is **not** "dado pessoal sensível" (art. 5 II lists health, biometrics, religion, etc.). Logging 02 §5.2 labelled CPF `SensitivePersonal`, which would wrongly demand art. 11 legal bases for every sale with "CPF na nota". Values renumbered (nothing is implemented yet) |
| ⚑P4 | Data categories | **Open set `DataCategory`** (interned keys like `RawType`), well-known list built in (`identification`, `contact`, `health`, …). `SensitivePersonal` types **must** name their art. 5 II category (YFP0012) |
| ⚑P5 | How members are classified (**Tier 3** for conventions + snapshot) | **By type, then convention, then snapshot** (02 §5): value-object classification → `[Sensitive]` on the raw member (exception) → name conventions for raw strings (flagged `ByConvention`) → free text. Reviewed in `PrivacySchema.verified.txt`. `[NotPersonalData]` to silence a false positive |
| ⚑P6 | Unions (`CpfCnpj`, `PartyName`) (**Tier 3**) | **Static worst case + per-value `Classify`**: `CpfCnpj` holding a CNPJ is `Public`, holding a CPF is `PersonalIdentifier`. `PartyName.Company` is `Public` |
| ⚑P7 | Mask language | **Positional pattern** (`#` keep, `*` hide, other characters literal) **plus presets** (`{email}`, `{initials}`, `{last:4}`, `{first:1}`, `{hidden}`). The generator turns each mask into straight-line span code; no runtime parsing for value objects |
| ⚑P8 | Consent model | **App-declared purposes** in `[PurposeCatalog]` classes, each with one `LegalBasis` (closed enum mapping art. 7 I–X and art. 11 II a–g; the generator rejects invalid bases for sensitive data). `ConsentLevel` survives only as a Profiling **preset** (a named bundle of purposes). Core `YFex.Consent` is deprecated and moved |
| ⚑P9 | Consent records | **Append-only ledger**: subject, purpose, decision, notice id + version + SHA-256 of the exact text shown, channel, collector, device, timestamps (device + server), device signature for offline entries. Withdrawal wins over any older grant. Server authoritative; devices enforce their last snapshot + local entries |
| ⚑P10 | Data subject identity | **`DataSubjectReference` per tenant** (the tenant is the controller). Id types carry `[DataSubjectKey("customer")]` once. Natural identifiers (CPF, e-mail) resolve to subjects through blind indexes, never through clear values |
| ⚑P11 | Pseudonyms | **HMAC-SHA256 with keys derived by HKDF per tenant, domain and version**; 128-bit stored, `#h:` + 16 hex displayed. Replaces every `SHA256(value + salt)` and the unsalted MAC hash |
| ⚑P12 | Keys | **Root → tenant key-encryption key → per-subject data keys and per-category field keys** behind `IRootKeyProvider` + `IKeyVault`. In-memory/file/environment providers in YFex.Privacy, SQL vault in YFex.Privacy.Data, DPAPI in the Windows platform package. No cloud KMS package now (seam only) |
| ⚑P13 | Encryption format | **Envelope v2** (AES-256-GCM, key kind + 16-byte key id, random nonce, AAD binding to owner type and member). `Persistence.Encryption`, `Web.Data` and the Rules key ring converge on these primitives, keeping readers for their current formats |
| ⚑P14 | Tokenization | **Random tokens + token vault** (detokenization authorised and audited, deleted on erasure). No format-preserving encryption (would need a non-BCL FF1 implementation) |
| ⚑P15 | Encryption at rest in SQL | **Opt-in per entity**, generated by YFex.Data: encrypted column + blind-index column for equality search. **Default on** for `SensitivePersonal` and `Financial`, opt-in for `PersonalIdentifier` |
| ⚑P16 | Event sourcing erasure | **Crypto-shredding with a two-part event payload** (personal members sealed with the subject key). After shredding they read back as `default(T)` plus a `Shredded` flag. Alternative: external personal-data store keyed by event (more moving parts, worse offline sync) |
| ⚑P17 | Server-side masking for callers without reveal permission | **Omit the value and send the masked display text in the result's privacy metadata** (typed value objects cannot hold `***.456.789-**`). Client-only masking is not a control when the full value reaches the device |
| ⚑P18 | Free-text scrubbing | **Prefilter + `[GeneratedRegex]` + checksum validation**. Brazilian documents are detected by the value objects themselves (`IClassifiedData<T>.TryFind`), so a CPF found in an exception message gets the same mask/pseudonym as a typed `Cpf`. `PrivacyScrubber` moves into `YFex.Privacy` (shim kept one release) |
| ⚑P19 | AI | **`PseudonymizationSession`** replaces personal values in prompts and tool results with placeholders (`[[CPF_1]]`) and restores them in tool arguments and display. Cloud providers default to "no clear personal data" (international transfer, art. 33). Decorators live in Automation/Capabilities. DataMining sends value **shapes**, not samples |
| ⚑P20 | Rights orchestration | **`PrivacyRequest` aggregate + `IPersonalDataStore` participants** (Data, EventSourcing, Persistence/devices, Search, Logging audit, token vault, consent ledger). Correction goes through the app's own commands, never generic field writes. Erasure log replayed after any backup restore |
| ⚑P21 | Retention & legal holds | **Declared by modules** (`p.Retain(...)` per purpose/category; the fiscal module declares the 5-year hold), enforced by a sweeper job. Legal holds block erasure and show in the response with their legal basis and end date |
| ⚑P22 | Analyzers (**Tier 3**, optional) | **YFP0001–YFP0099** in the generator assembly: raw personal/secret strings → suggest the existing value object (code fix), unkeyed hashing of personal data, classified values in interpolated strings/exceptions, invalid purpose bases. Name heuristics never flag a bare `Name` |
| ⚑P24 | Record-level classification (00 §5, 02 §5.5) | **`[Sensitive(SensitivePersonal, Category = "health")]` on record/entity types**: health data is sensitive by combination (person + medication), so every member of the record is treated at the record level except the record's own id |
| ⚑P25 | Erasure under a legal hold (align with YFex.Audit 08 §3) | **Decide once for both libraries:** one subject key destroyed when the hold ends (this design) vs subject keys per retention class (audit Option A) |
| ⚑P23 | Inventory (**Tier 3**) | **Committed snapshot files** (`PrivacySchema.verified.txt` + `privacy-inventory.json`) per assembly, aggregated by the `yfex-privacy` CLI ("where do we store CPF?"), plus a generated ROPA (art. 37) report. Runtime registry for the rights tooling |
