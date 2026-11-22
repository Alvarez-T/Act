# 01 — Use cases in a PDV / ERP

> Proposal (2026-11-18). Each scenario names the deterministic part that runs first and its owner, the question asked to a
> model, what the user sees, and the ⚑ row that records it. Code is a *proposed* API. Rules for every row: the rules-only path
> works without a model, a judgment is a suggestion or a fact for YFex.Rules, never the decision about a person (⚑IF31).

## 1. The three question kinds

| Kind | Example | Answer |
|---|---|---|
| **Judge**: yes/no, choice, score | "Is this supplier item one of these 5 products?" | probability per option; `Abstained` when nothing may answer |
| **Find spans** | "Where are the names, health terms and addresses in this note?" | `(start, length, label, probability)` |
| **Embed** | "Which settings or actions mean *mudar impressora*?" | vectors compared in memory |

Options always come from **data the app owns** (categories, chart of accounts, reason codes, queues), so a tenant adds an
option without a deploy and a model can never answer outside the list.

## 2. Front of store (caixa / balcão)

| Scenario | Deterministic first | Question | User sees | ⚑ |
|---|---|---|---|---|
| Typed sale "2 coca lata e um pão frances" | quantities, EAN, exact product lookup (`IRecordLookup`) | spans (quantity, product text) → choice among the top 5 lookup results per line | cart lines filled, each with a confidence chip; Enter confirms | IF5, IF35 |
| "remédio pra dor de cabeça" in the search box | fold, fuzzy, frecency | embeddings over categories / active ingredients (catalog) | "Analgésicos" group on top | SK49 |
| Unknown barcode | EAN lookup, supplier mapping table | choice: one of these similar products, or new | "Parece ser *Dipirona 500mg EMS c/10* · 87% — vincular?" | IF39 |
| Many cancellations or discounts in a shift | counts and limits in Rules | score: how unusual is this pattern | Rules turns `p ≥ 0.9` into `NeedsApproval` (supervisor); the model never blocks alone | IF11 |
| Discount reason typed freely | — | choice over the tenant's reason codes | reports group by reason with no forced dropdown at the counter | IF39 |
| "como faço sangria?" | capability keywords | Search intent `help` (⚑SK46) | the Sangria capability and its help page | IF43 |

## 3. Purchasing and goods-in (entrada de nota)

| Scenario | Deterministic first | Question | User sees | ⚑ |
|---|---|---|---|---|
| Supplier item → own SKU ("DIPIRONA SOD 500MG C/10 CPR EMS") | EAN, then the confirmed mapping table (de-para) | choice among lookup candidates | a matching grid pre-filled and sorted by confidence; a confirmed pair is stored and never asked again | IF39, IF37 |
| NCM / CFOP for a new product | registry lookups, rules tables | choice among candidate codes from the description | "Sugerido: 3004.90.69 · 82%"; the user stays legally responsible | IF39 |
| Inbound partner CSV (YFex.Files exchange) | encoding, schema, `IExtractable<T>` hit ratios | column semantics (choice over declared types) | column mapping proposed; personal-data columns badged | IF9, IF6 |

## 4. Finance

| Scenario | Deterministic first | Question | User sees | ⚑ |
|---|---|---|---|---|
| Bank reconciliation ("PIX RECEBIDO MARIA S 152,40") | amount + date window, exact document match | choice among open receivables that pass the filter | auto-match list ordered by confidence; one click accepts | IF39, IF37 |
| Payable / expense categorization | supplier's last category | choice over the tenant's chart of accounts | ghost-text suggestion in the account field | IF35, IF39 |
| Document inbox (boleto, NF-e, cobrança, spam) | XML/NF-e parse, barcode line check | choice of document kind | sorted inbox | IF36 |

## 5. Records and forms (cadastro)

| Scenario | Deterministic first | Question | User sees | ⚑ |
|---|---|---|---|---|
| Paste a WhatsApp message into the customer form | `Cpf` / `Telefone` / `Email` via `TryExtract`, CEP lookup | spans for name and address parts | **paste preview**: field-by-field diff, then Apply | IF8, FM51 |
| Duplicate customers ("Maria A. Silva" / "Maria Aparecida da Silva", same phone) | blocking: same phone, CPF or CEP | yes/no "same person?" (local only) | "Possível duplicado" banner with a merge suggestion; merging is the user's command | IF40, FM52 |
| Messy legacy addresses | CEP lookup | spans: street, number, district | data-cleaning screen with suggestions | IF8 |

## 6. Pharmacy (domain)

| Scenario | Deterministic first | Question | User sees | ⚑ |
|---|---|---|---|---|
| Prescription (receita) text | drug registry lookup, CRM format | spans: drug, dose, prescriber → choice among registry drugs | SNGPC / antibiotic form pre-filled; **local only** (health data) | IF39, IF4 |
| Free-text notes ("está com depressão") | `TextScrubber` | spans for health terms | "pode conter dado de saúde" before export or AI | IF3, P26 |

## 7. Customer service

| Scenario | Deterministic first | Question | User sees | ⚑ |
|---|---|---|---|---|
| Inbound WhatsApp / e-mail reply | Notifications inbound router (opt-out keywords, receipts, ⚑N13) | choice: order, complaint, question, delivery; score: urgency | messages in the right queue, urgent first | IF36, N30 |
| Complaint severity | — | score | escalation through Rules + Notifications | IF11, IF36 |

## 8. Operations, support, migration, privacy

| Scenario | Deterministic first | Question | User sees | ⚑ |
|---|---|---|---|---|
| Exception and incident triage | fingerprint by type + stack | choice over known issues (+ "new") | grouped alerts with the known fix linked | IF42, L23 |
| Leaks in logs | `TextScrubber` on write | spans on a sample, in a Jobs pass | a report of leaks the scrubber missed | IF19, L22 |
| Legacy desktop ERP automation | window and control queries | yes/no on a screenshot: "error dialog shown?" (remote, later) | robust Automation steps | IF38, IF7 |
| AI trigger ("stock alert worth sending?") | Jobs cadence | yes/no with threshold | fires only above the threshold; abstain = no fire | IF7 |
| Legacy migration (DataMining) | naming worm, value shapes, hit ratios | column / table semantics | mapping proposals as `Suggestion` evidence | IF6 |
| LGPD data-subject request ("where is Maria mentioned?") | typed columns via the privacy inventory | spans for the subject's name in free-text columns | candidates a person reviews before export or erasure | IF41, P28 |
| Text sent to AI agents | — | yes/no: prompt injection? (later) | the agent refuses or asks | IF10 |
| User-composed screens (Composer) | — | the user defines a question: "Classificar ocorrência em: A, B, C" | a composed step with a suggestion | IF22, C6 |

## 9. UI and UX patterns (⚑IF35, ⚑UI116)

| Pattern | Where | Behaviour |
|---|---|---|
| **Suggestion chip** | any field, grid cell, matching row | "Sugerido · 82%", Tab or click accepts, Esc rejects; the source is shown on hover ("modelo local", "regra") |
| **Ghost default** | form fields (category, NCM, account) | faint text, never saved until accepted |
| **Paste preview** | forms | Ctrl+V on the form → diff of fields → Apply / Cancel |
| **Review queue by confidence** | matching, reconciliation, cataloguing | bulk-accept above a threshold, attention below it |
| **Badges** | grid columns, export dialogs | "dados pessoais", "pode conter dado de saúde" |
| **No explanation text** | everywhere | decision models give no rationale; the UI shows the deterministic signals and the probability instead |

Every accept or reject is a **feedback event** (⚑IF37): purpose, model id and version, top option, accepted or not; no input
text. Per tenant it gives a calibration report, and Rules may raise an auto-accept threshold **only** for purposes declared
low-risk (item mapping, document kind), never for decisions about people.

## 10. What it simplifies

- One contract replaces "answer yes or no" prompts, "one short word" parsing, keyword lists and categorization regex.
- Options are tenant data: a new category, account or queue needs no deploy.
- Mapping screens become "confirm the suggestion" screens.
- Tests use a fixed-probability fake provider; the rules-only path is always tested.

## 11. Where not to use it

- Anything a rule decides exactly: taxes, check digits, stock math, prices, permissions. A price typo (R$ 1,99 → R$ 199,00)
  is a ratio rule.
- Any decision about a person on its own: credit, blocking a customer, judging an employee (⚑IF31; LGPD art. 20; Microsoft's
  terms for its decision model).
- Profiling people (YFex.Profiling stays out on purpose).
- Hot paths (keystrokes in Tier 0, log writes) and handhelds (rules only, ⚑IF18).
