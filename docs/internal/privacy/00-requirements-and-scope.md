# 00 — What is really required, and when (scope tiers)

YFex.Privacy is generic. It must eventually cover everything a Brazilian app that handles personal data may need. Most apps, though, need
only a small part **today**. This file maps every feature of the design to:

1. **the legal source** that requires the *outcome* (LGPD article, ANPD regulation, or sector rule);
2. **when it applies** (the business feature that triggers it);
3. **the minimum way to comply** without the library (often a manual procedure);
4. **the library feature** that automates it, and its **tier**.

The law almost never prescribes a technique. It prescribes outcomes (minimal collection, security against unauthorized access,
answering a subject within 15 days, keeping fiscal records for 5 years). The library is the means. A small pharmacy can comply with
manual procedures for several items. The library makes them cheap and consistent when volume or risk grows.

> Legal statements below were checked against the sources listed in §6 (2026-10-02). They are engineering guidance, not legal advice:
> each tenant (the controller) confirms its own obligations with its lawyer/DPO.

## 1. Tiers

| Tier | Meaning | Build when |
|---|---|---|
| **Tier 1 — Foundation** | needed by any app that stores customers; cheap; everything else builds on it | now (first implementation) |
| **Tier 2 — Feature-triggered** | required by law **once a specific business feature exists** | when that feature is built (trigger column) |
| **Tier 3 — Optional / scale** | good practice or tooling that pays off with many developers, tenants or volume; not required | when the pain appears |

## 2. Requirements map

| # | Requirement (outcome) | Source | Applies when | Minimum way to comply without the library | Library feature | Tier |
|---|---|---|---|---|---|---|
| R1 | Collect only data needed for the purpose | LGPD art. 6 III (necessidade) | always | form design reviews | classification makes collected personal data **visible** (inventory); record-level classification (02 §5.5) | 1 |
| R2 | Protect data against unauthorized access (security measures) | art. 6 VII, art. 46; ANPD security guide: access control by **least privilege / need to know** | always | role permissions on screens and queries | `[Sensitive]` + mask helpers + `RevealPolicy`; integrates with YFex.Authorization permissions | 1 |
| R3 | Do not leak personal data through logs, error messages, crash reports | art. 46 (security), art. 48 (incidents start this way) | always (once logging exists) | discipline in log statements | logging reads classification; `TextScrubber` on free text and exceptions; secrets never printed | 1 |
| R4 | Credentials and secrets protected | art. 46 | integrations with API keys | encrypted config | `Secret` level; credential protector (05 §6.3) | 1 |
| R5 | Health/biometric/etc. data only under an art. 11 basis; stricter handling | art. 5 II, art. 11 | **health features**: controlled drugs, antibiotics, Farmácia Popular, pharmaceutical services, medication history | legal review per feature | `SensitivePersonal` + category; generator check of purpose bases (YFP0022); encryption at rest by default | 2 |
| R6 | **No sharing of health data with other controllers for economic advantage** (exceptions: health services, pharmaceutical assistance, portability with consent, resulting financial/administrative transactions) | art. 11 §4 (wording by Lei 13.853/2019); MPF inquiry and ANPD sanction process against pharmacy chains (2023–2025) | integrations with labs, PBMs, insurers, advertising partners | contract review | `ExternalSystem` destination erases `SensitivePersonal` by default (05 §1); `SharedWith` declarations (03 §5.5) | 2 |
| R7 | Pharmacist confidentiality about patients and prescriptions | CFF Resolução 724/2022 (Código de Ética: dever de sigilo; prohibition to give patient/prescription data to third parties against the LGPD) | health features | screen permissions for pharmacists only | permission-gated screens + **audit of reads** of health records (who saw whose data) | 2 |
| R8 | Keep regulated records for the legal period, then dispose | Portaria SVS/MS 344/98 art. 64 (controlled-drug records: **2 years**; anabolic steroids: **5 years**); RDC 471/2021 art. 22 (antibiotic documents: **2 years**); Portaria GM/MS 12.091/2026 (Farmácia Popular: **5 years** from dispensing; longer while an inspection is open); tax law (fiscal documents: 5 years) | the corresponding feature | manual archive + calendar | retention rules declared by each module + legal holds + sweeper (07 §7) | 2 |
| R9 | Transparency: inform purpose, use, sharing, retention, controller contact | art. 9; CDC art. 43 §2 (opening a consumer record must be communicated in writing when not requested by the consumer; basis of the R$ 7.9 M Procon-MG fine on Drogaria Araujo, 2018) | customer registration, loyalty, "CPF discounts" | privacy notice on site/store | `PrivacyNotice` versioned per tenant (04 §4) | 2 |
| R10 | Consent: free, informed, for **determined purposes**; provable; revocable | art. 8 (§2 burden of proof on the controller, §4 generic authorizations void, §5 revocation), art. 11 I (specific and highlighted for sensitive data) | loyalty/marketing, medication-history programs, behavioural telemetry | signed forms / logs | purposes + consent ledger (04) | 2 |
| R11 | Children and adolescents: best interest; children's data under consent needs a parent's specific, highlighted consent | art. 14 §1; **Enunciado CD/ANPD 1/2023** (any art. 7/11 basis may be used if the best interest prevails) | features that register minors (pediatric dispensing, family loyalty accounts) | operator procedure | `Guardian` on ledger entries; birth-date rule (04 §12, corrected) | 2 |
| R12 | Data subject rights: confirmation, access, correction, anonymization/blocking/deletion, portability, sharing info, revocation; **15 days** for the complete statement | art. 18, art. 19 II | **always** (any controller) | a documented manual procedure and a contact channel | rights tooling: locate/export/erase participants, plan with legal holds (07) — automation is Tier 2 when volume grows; the manual procedure is enough at first | 2 |
| R13 | Contact channel / DPO (encarregado) | art. 41; Res. CD/ANPD 2/2022 art. 11 (small agents may skip the DPO but must offer a channel; not available for high-risk processing) | always | e-mail address in the notice | `ControllerIdentity`/`DataProtectionOfficer` in notices; portal (phase 7) | 2 |
| R14 | Record of processing operations | art. 37; Res. CD/ANPD 2/2022 art. 9 (**simplified** for small agents, ANPD template) | always (simplified for small agents not doing high-risk processing) | ANPD template spreadsheet | inventory + ROPA export (03 §5) | 3 |
| R15 | Security incident: register every incident; notify ANPD and subjects of relevant-risk incidents within **3 business days** (doubled for small agents) | art. 48; **Res. CD/ANPD 15/2024** arts. 6, 9 | always | incident procedure + spreadsheet | incident register + "who was affected" query from the inventory (10 §2.1) | 2 |
| R16 | International transfer only through a valid mechanism (adequacy, standard contractual clauses…) | art. 33; Res. CD/ANPD 19/2024 (standard clauses mandatory since 2025-08-23); Res. 32/2026 (EU adequacy) | **AI features using foreign cloud models**, foreign SaaS | contract with the provider | `PseudonymizationSession` keeps personal data from leaving (08 §8) | 2 |
| R17 | Review of automated decisions | art. 20 | credit/limits/risk scoring (Brain `RiskScore`, Rules-based refusals) | human review procedure | decision `Trace` + review request (07 §1) | 2 |
| R18 | Erasure must work even in immutable stores and backups | art. 18 IV/VI, art. 16 | event sourcing / audit hash chains with personal data | do not put personal data in immutable stores | subject keys + crypto-shredding (05 §8); erasure log replay (07 §8) | 2 |
| R19 | Pseudonymized data is still personal data | art. 12/13 | analytics, logs | treat logs as personal data (retention, access) | HMAC pseudonyms (05 §3) | 3 |
| R20 | High-risk processing loses the small-agent simplifications | Res. CD/ANPD 2/2022 arts. 3–4 (a general criterion such as large scale or significant impact **plus** a specific one such as sensitive data) | pharmacy chains with health data at scale | legal assessment | inventory + ROPA (3) make the assessment possible | 3 |

## 3. Feature list by tier

**Tier 1 — Foundation (build now)**

- YFex core declaration contract: `DataSensitivity`, `DataCategory`, `[Sensitive]` on value objects **and record types** (02 §5.5),
  `IClassifiedData<T>` (generated), `Masks`.
- Generator: classification + mask + canonical form for classified types. **No analyzers, no name conventions** in Tier 1.
- Logging integration: capture by classification (mask/erase), `TextScrubber` for free text and exceptions.
- UI integration: mask helpers + `RevealPolicy` driven by **YFex.Authorization permissions** (screens decide; the type only says what
  the data is). A7 is a UI decision.
- `Secret` handling: never logged, records print `****` (fix the three YFex.Web credential records by hand).

**Tier 2 — Feature-triggered (design kept complete; build with the feature)**

| Trigger feature | Build |
|---|---|
| Controlled drugs (SNGPC), antibiotics, Farmácia Popular, pharmaceutical services, medication history | health categories, encryption at rest for `SensitivePersonal`, **read audit** of health records (YFex.Audit), retention rules + legal holds, `ExternalSystem` guard (art. 11 §4) |
| Loyalty / marketing / "desconto no CPF" / behavioural telemetry | purposes, notices, consent ledger, withdrawal |
| First data-subject request volume beyond a manual procedure | locate/export/erase participants, request desk |
| AI over customer data | `PseudonymizationSession`, `AiDataPolicy` |
| Event-sourced aggregates with personal data | subject keys, sealed event parts, crypto-shredding |
| Integrations sending data to partners | `SharedWith`, tokenization |
| Incident handling | incident register |
| Minors registered | guardian flow |

**Tier 3 — Optional / scale**

Analyzers YFP* and name conventions, `PrivacySchema` snapshot, inventory CLI and ROPA generator, pseudonyms for log search, blind
indexes, per-case union classification (`CpfCnpj` → CNPJ not personal), `[NotPersonalData]`, `SensitiveTypeAttribute<T>`.

## 4. What the law does **not** require (common over-engineering)

| Myth | Reality |
|---|---|
| "CPF must be masked everywhere" | The law requires protection against unauthorized access (art. 46) and minimal collection. Masking is one technique, applied **per screen and role**. A cashier finishing a sale with *CPF na nota* may see the CPF they just typed |
| "CPF is sensitive data" | No. Art. 5 II is a closed list (health, biometric, genetic, ethnicity, religion, political opinion, union/religious/political membership, sex life). CPF is ordinary personal data (`PersonalIdentifier` here) |
| "Everything needs consent" | Most processing rests on contract, legal obligation or legitimate interest. Asking consent where the subject cannot refuse (NF-e, controlled-drug records) is misleading |
| "Every column must be encrypted" | Encryption at rest is a security measure chosen by risk: default for health/financial data, optional for the rest |
| "We must delete everything on request" | Art. 16 allows keeping data for legal obligations (fiscal 5 years, controlled-drug records 2 years). The response explains what was kept and until when |
| "Every company needs a DPO" | Small agents not doing high-risk processing may skip it, but they must provide a contact channel (Res. 2/2022 art. 11) |
| "Pseudonymized logs are no longer personal data" | They still are (art. 12/13): they keep retention and access rules |

## 5. Decisions changed by this scoping

| # | Change |
|---|---|
| ⚑P3 (clarified) | The 7-value enum stays (it is the complete model, shared with the logging and audit designs). Developers only **write** `Personal`, `PersonalIdentifier`, `SensitivePersonal`, `Financial`, `Secret`. No attribute = not personal (`Internal`). `Public` exists only as a computed result (e.g. a CNPJ case, Tier 3). `Cnpj` and `CompanyName` get **no** attribute |
| ⚑P5, ⚑P6, ⚑P22, ⚑P23 | Moved to **Tier 3** (optional): name conventions, per-case unions, analyzers, inventory CLI/ROPA |
| ⚑P24 (new) | **Record-level classification**: `[Sensitive(SensitivePersonal, Category = "health")]` on a record/entity type (02 §5.5). For health data the sensitivity comes from the combination (identified person + medication), so it belongs to the record |
| ⚑P25 (new, align with YFex.Audit) | Erasure under a legal hold: **one subject key destroyed when the hold ends** (this design, 07 §7.3) **vs** subject keys **per retention class** (audit 08 §3, Option A). Decide once for both libraries before the event-sourcing/audit encryption work |
| Correction | Art. 14: the parent's consent rule (§1) is for **children** when consent is the basis; Enunciado CD/ANPD 1/2023 allows any art. 7/11 basis for children and adolescents if their best interest prevails. 04 §12 and catalog C7 corrected |

## 6. Sources

- LGPD art. 11 (incl. §4 text by Lei 13.853/2019): [Lei na Mão](https://www.leinamao.com.br/leis/lei-geral-de-protecao-de-dados/art-11), [myLex](https://brasil.mylex.net/legislacao/lei-protecao-dados-pessoais-lgpd-art11_95854.html)
- LGPD art. 52 (fines): [Assis e Mendes](https://assisemendes.com.br/lei_na_integra/artigo-52-sancoes-administrativas-da-autoridade-nacional-anpd/)
- Res. CD/ANPD 2/2022 (small agents): [ANPD](https://www.gov.br/anpd/pt-br/acesso-a-informacao/institucional/atos-normativos/regulamentacoes_anpd/resolucao-cd-anpd-no-2-de-27-de-janeiro-de-2022)
- ANPD security guide for small agents (least privilege): [ANPD PDF](https://www.gov.br/anpd/pt-br/centrais-de-conteudo/materiais-educativos-e-publicacoes/guia-vf.pdf/@@display-file/file)
- Res. CD/ANPD 15/2024 (incident communication, 3 business days): [ANPD — CIS](https://www.gov.br/anpd/pt-br/canais_atendimento/agente-de-tratamento/comunicado-de-incidente-de-seguranca-cis), [Machado Meyer](https://www.machadomeyer.com.br/pt/inteligencia-juridica/publicacoes-ij/direito-digital/anpd-publica-regulamento-de-comunicacao-de-incidente-de-seguranca)
- Res. CD/ANPD 19/2024 and Res. 32/2026 (international transfer, EU adequacy): [ANPD](https://www.gov.br/anpd/pt-br/acesso-a-informacao/institucional/atos-normativos/regulamentacoes_anpd/resolucao-cd-anpd-no-19-de-23-de-agosto-de-2024), [Trench Rossi Watanabe](https://www.trenchrossi.com/en/legal-alerts/brazil-and-the-european-union-mutually-recognize-adequacy-in-personal-data-protection-resolution-no-32-dated-january-26-2026/)
- Enunciado CD/ANPD 1/2023 (children and adolescents): [ANPD](https://www.gov.br/anpd/pt-br/assuntos/noticias/anpd-divulga-enunciado-sobre-o-tratamento-de-dados-pessoais-de-criancas-e-adolescentes)
- Pharmacy enforcement: [MPMG — Drogaria Araujo fine](https://www.mpmg.mp.br/portal/menu/comunicacao/noticias/drogaria-araujo-devera-pagar-multa-de-r-7-milhoes-por-capturar-cpf-dos-consumidores.shtml), [Convergência Digital — MPF inquiry](https://convergenciadigital.com.br/governo/apos-investigacao-da-anpd-mpf-abre-inquerito-sobre-coleta-de-dados-por-redes-de-farmacias/), [Data Privacy Brasil](https://www.dataprivacybr.org/o-que-esta-por-tras-do-cpf-nas-farmacias/)
- Código de Ética Farmacêutica (CFF 724/2022): [CRF-RJ](https://crf-rj.org.br/noticias/crf-rj-em-acao/4701-resolucao-724.html)
- Portaria 344/98 (controlled drugs; buyer identification; art. 64 retention): [AnvisaLegis](https://anvisalegis.datalegis.net/action/ActionDatalegis.php?acao=abrirTextoAto&link=S&tipo=POR&numeroAto=00000344&seqAto=000&valorAno=1998&orgao=SVS%2FMS&codTipo=&desItem=&desItemFim=&cod_modulo=134&cod_menu=1696), [CRF-PR dispensing manual](https://crf-pr.org.br/uploads/revista/20526/EXEVqiSjXfTYOyk3cbuL1f7Q7kSfYZxO.pdf)
- RDC 471/2021 (antibiotics; replaced RDC 20/2011): [CRF-PR](https://www.crf-pr.org.br/noticia/view/8721/resolucao-rdc-4712021-anvisa), [Anvisa technical note](https://www.gov.br/anvisa/pt-br/assuntos/fiscalizacao-e-monitoramento/sngpc/legislacao/arquivos/9170json-file-1)
- Farmácia Popular (Portaria GM/MS 12.091/2026): [P2 Saúde](https://p2saude.com.br/portaria-12091-2026-farmacia-popular-novas-regras/), [CFF](https://site.cff.org.br/noticia/noticias-do-cff/12/08/2026/farmacia-popular-tem-novas-regras-e-abre-caminho-para-discutir-servicos-farmaceuticos)
