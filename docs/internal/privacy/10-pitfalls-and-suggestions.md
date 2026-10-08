# 10 — Pitfalls, what is missing, ecosystem suggestions

## 1. Pitfalls (and the design's answer)

### 1.1 Legal

| # | Pitfall | Answer |
|---|---|---|
| 1.1 | **Labelling CPF as "sensitive data".** Art. 5 II is a closed list (health, biometrics, religion…). The label would demand art. 11 bases for every sale and dilute the real sensitive data | `PersonalIdentifier` level (⚑P3); special categories are explicit (02 §3) |
| 1.2 | **Asking consent for processing that rests on another basis** (NF-e, SNGPC). The subject is told they can refuse or withdraw, and they cannot | purposes carry one legal basis; consent is only consulted for `Consent` purposes (04 §2, §5.2 rule 5) |
| 1.3 | **Pseudonymized data treated as anonymous.** Data that can be re-linked with a key is still personal data (art. 12, art. 13 §4) | logs with `#h:` pseudonyms keep retention limits and access control; only `Anonymize` (07 §7.4) leaves LGPD scope |
| 1.4 | **"Delete everything" vs legal retention** | plan with `Retain` + basis + end date; scheduled shredding (07 §7.3) |
| 1.5 | **Burden of proof of consent** is on the controller (art. 8 §2), and today's queue rebuilds consent as `(UtcNow, "restored")` | append-only ledger, notice hash, device signature, audit chain; ledger kept pseudonymized under a consent-proof hold (04 §7, 07 §4.2) |
| 1.6 | **Tenant vs vendor roles.** The tenant is the controller of its customers' data; the vendor is a processor there, but the controller of its own telemetry about app users | keys, notices, policies and requests per tenant; vendor telemetry has its own purposes and notice (04 §4, §8) |
| 1.7 | **Children's data** needs a guardian's specific consent (art. 14) | `Guardian` on ledger entries; rule helper (04 §12) |
| 1.8 | **International transfer** when prompts go to a cloud model hosted abroad (art. 33). Providers may also retain prompts (caching, abuse monitoring) | cloud default: nothing above `Internal` in clear (⚑P19); checklist item for provider data-retention settings |
| 1.9 | **Automated decisions** (Brain `RiskScore`, rules that deny credit) can be contested (art. 20) | keep inputs + `Trace` (validation 06); `AutomatedDecisionReview` request (07 §1) |
| 1.10 | **Rights requests as an attack** (someone asks for another person's export) | verification levels, OTP only to contacts on file, rate limits, audited (07 §2) |
| 1.11 | **Third parties inside the subject's records** (prescriber, co-holder) | masked in exports (07 §5) |
| 1.12 | **CNPJ of individual entrepreneurs (MEI/EI)** can identify a natural person; `Cnpj` is classified `Public` | documented simplification; tenants can tighten `Cnpj` to `Personal` through the redaction policy; the `CpfCnpj` union keeps CPFs strict |

### 1.2 Technical

| # | Pitfall | Answer |
|---|---|---|
| 1.13 | **Unkeyed or statically salted hashes are reversible** for low-entropy values (CPF ≈ 10⁹, IPv4, MAC, phones) | HMAC with vault keys (05 §3); YFP0007 flags new cases |
| 1.14 | **Re-identification by combination** (birth date + CEP + gender; a rare medication + neighbourhood) | generalization helpers, analytics pseudonyms rotated yearly; suggestion: a k-anonymity check before analytics exports (§3) |
| 1.15 | **Blind indexes leak frequency** (the same CPF in 300 rows), and indexes on low-cardinality members leak the distribution | indexes only for `Identifier` members; per-tenant keys; never index gender/status-like members |
| 1.16 | **Substring heuristics misfire** (`multipleMonitors` → masked; `userName` → clear) | token conventions (02 §5.2); every decision visible in the snapshot |
| 1.17 | **Bare `Name` false positives** (dozens of schema/file/job names) | only compound names or names inside person-like types |
| 1.18 | **Free text leaks** (`Observacao`, `RawPayload`, exception messages, search queries, AI prompts) | `FreeText` classification, scrubber on every exit (06), YFP0006 against interpolating classified values |
| 1.19 | **Compiler-generated `record.ToString()` prints secrets** (`BearerTokenCredential(Token)`) | YFP0005/YFP0010 + code fix; `AccessToken` pattern |
| 1.20 | **Unstable canonical forms** (`Email` casing, phone formats, name whitespace) break pseudonym joins and blind-index lookups | value objects normalize on construction; tests per type: `Parse(a) == Parse(b)` ⇒ equal canonical bytes |
| 1.21 | **Masking is not access control** when the full value reaches the device | server-side omission + masked display metadata (⚑P17) |
| 1.22 | **Masks leak structure** (length of names/e-mails) | presets with fixed-length stars, initials without lengths (02 §7.2) |
| 1.23 | **Backups resurrect erased people** | erasure log replay before going live; short vault backup retention (07 §8, 05 §8) |
| 1.24 | **Immutable stores** (event streams, audit hash chains) | crypto-shredding; readers handle `default` + `Shredded` (08 §4) |
| 1.25 | **Copies we do not control**: device caches, outbox, State.History stacks, search frecency, clipboard, print spool, DANFE PDFs, sent e-mails | encryption + TTL + `SubjectErased` where we can; listed explicitly in the erasure report where we cannot |
| 1.26 | **Re-imports** (legacy migration, CSV) recreate erased subjects | importers check the erasure log (07 §8) |
| 1.27 | **Key material in memory and crash dumps**; `string`s cannot be zeroed | `KeyLease` pinned + zeroed; keys never as strings; redaction before the logging flight recorder; servers configured without full-memory dumps |
| 1.28 | **AES-GCM random-nonce limit** (~2³² messages per key) | field keys rotate by count/time; subject keys see few messages (05 §5.4) |
| 1.29 | **Regex denial of service** on hostile input | bounded quantifiers, 25 ms timeout → fail closed, 64 KiB input cap, prefilter (06 §5) |
| 1.30 | **Placeholder injection**: the model invents or swaps placeholders to reach other people's data | only placeholders issued in the session restore; execution re-checks authorization (08 §8.1) |
| 1.31 | **The model re-identifies from context** (purchase history + neighbourhood) | data minimization in tool results (shapes + `MaximumInClear`), not only identifier replacement |
| 1.32 | **Consent snapshots go stale on offline devices** | local entries effective immediately; staleness metric + alert; withdrawal precedence on merge (04 §5.2, §6) |
| 1.33 | **Clock skew** makes "latest wins" wrong | corrected time + ±5 min window where "no" wins |
| 1.34 | **Encrypted columns break existing queries** (`LIKE`, ranges, sorts) | encryption opt-in for `PersonalIdentifier`; generator/run-time errors for unsupported predicates; truncated index for prefix search (05 §4) |
| 1.35 | **AOT**: reflection-based "find all personal properties" is unavailable | generated shapes, `PersonalDataShapes` registry, JSON contract modifiers on source-generated contexts |
| 1.36 | **Generated code bloat** | shapes only for types that hold personal data; masks as straight-line code only for value objects |
| 1.37 | **Analyzer fatigue** | few warnings, code fixes for the common ones, the snapshot as the main net |
| 1.38 | **Retention without a usable timestamp** cannot be enforced | listed as a ROPA gap (07 §7.5) |
| 1.39 | **Search caches keyed by CPF** | pseudonymized cache keys (08 §11) |
| 1.40 | **Logging joins behaviour with operations** without consent | join gated by `telemetry.behaviour` in the session `PurposeSet` (04 §9) |

## 2. What the brief does not mention but LGPD compliance needs

1. **Security incident handling (art. 48).** The controller must notify the ANPD and the affected subjects of incidents that may
   cause relevant risk or damage (the ANPD regulation sets a short deadline in business days). The inventory plus audit makes the
   hard question answerable: "whose data was in `cliente` between these dates, and which categories?" Suggested: an
   `IncidentRegister` (aggregate + report) in YFex.Privacy.Rights, phase 6.
2. **Data protection impact report (RIPD, art. 38).** A draft can be generated from the ROPA (purposes, categories, safeguards, sharing,
   retention) for the tenant's DPO to complete.
3. **DPO (encarregado) contact** must be public (art. 41 §1): part of `ControllerIdentity`/notices and the portal footer.
4. **Processors and sub-processors** (art. 39): `SharedWith` declarations are the start. Contracts live outside the code, but the list
   should be exportable for the tenant.
5. **Security measures (art. 46)**: the inventory already shows encryption, masking, pseudonymization and access audit per field.
   The ROPA exports them as the "safeguards" column.
6. **Purpose compatibility** for secondary use (art. 6 I, art. 7 §7): reusing sale data for marketing needs a new compatible purpose or
   consent. A command whose purposes do not include the purpose of the data's origin is a review item (ROPA report).
7. **Access logging for sensitive data.** Reveals are audited (08 §1). Suggested as well: audit of queries that **return**
   `SensitivePersonal` members (the `PrivacyStage` can record them).
8. **Employees and app users are data subjects too.** Operator names in audit trails are personal data kept under legitimate interest /
   legal obligation for accountability. Their erasure requests are answered with `Retain` for audit, plus deletion of HR-irrelevant data.
9. **Data minimization at collection (art. 6 III):** a report of personal members that are captured but never read by any handler or
   projection would show what to stop collecting. This is a future generator analysis (cross-assembly), listed as a suggestion.

## 3. Ecosystem suggestions (concrete)

| # | Where | Suggestion |
|---|---|---|
| 3.1 | YFex.Domain `YFex.Entity.Public` | add `[Sensitive]` to `Cpf`, `Cnpj`, `CpfCnpj`, `Email`, `Telefone`, `Rg`, `FullName`, `CompanyName`, `PartyName` (02 §8); `TryGetValue` overloads on `CpfCnpj`; `TryFindIn` on `Cpf`/`Telefone`/`Cnpj`; normalize `Email` domain casing; fix `CompanyName`'s namespace (`CompanyName.CompanyName`); new value objects `Cep`, `Endereco`, `DataNascimento`, `PersonDocument` (union for RG/NIS/PIS/passport) |
| 3.2 | YFex.Farma | replace raw strings in `CustomerModel`, `AddCustomerViewModel`, `SupplierCrudModel`, `ConvenioCrudModel`, `LabCrudModel` and `CreateCustomerCommand(string Name, string Email)` with value objects (YFP0001 fixes); declare `FarmaPurposes`, including health purposes, before medication history or SNGPC features are built |
| 3.3 | YFex.Payment | `Payer.Email/Phone` → `Email`/`Telefone`; keep provider error bodies out of logs (already the default) and scrub them when extracted |
| 3.4 | YFex.Web | `PrintMembers` overrides on credential records (YFP0005); `AesSecretProtector` → envelope v2 (05 §6.3) |
| 3.5 | YFex.Automation | encrypt `PortableSecretStore` files; `RedactingAiAgent` decorator (08 §8) |
| 3.6 | YFex.Brain | seal `RawPayload` with the subject key + retention; blind index on `CpfCnpj`; `PersonDocument` union for `EntityIdentifier`; keep explanations for `RiskScore` |
| 3.7 | YFex.DataMining | send value shapes to the AI worm; map semantic tags to classifications; add a `PersonName` semantic; check the erasure log on import |
| 3.8 | YFex.Security | gate watchers by purpose in code; tenant flag for traffic interception |
| 3.9 | YFex.Profiling / YFex.System | purposes, ledger, `PropertyRedactor`, pseudonyms; rewrite both consent docs; remove `TransportSaltKey` |
| 3.10 | YFex.Avalonia (Windows) | exclude sensitive windows from screen capture (`SetWindowDisplayAffinity`); clear the clipboard after copying a revealed value (configurable) |
| 3.11 | YFex.Devices | PIN-pad confirmation as consent evidence (04 §7) |
| 3.12 | YFex.Logging design | apply the amendments in 08 §7 (remove `ILogValue.Sensitivity`, adopt `Redactor`/`TextScrubber`/`IKeyVault`, 7-value enum) |
| 3.13 | YFex.Rules | decision traces as art. 20 explanations; `OverrideTokens` on `KeyRing` (optional) |
| 3.14 | YFex.Jobs / YFex.Orchestration | retention sweeper `[Job]`; request execution as a saga when participants span services |
| 3.15 | `YFex/CLAUDE.md` | lists `YFex.Messaging.Rpc.Encryption`, which does not exist; encryption is `YFex.Persistence.Encryption` |
| 3.16 | Analytics exports | k-anonymity check (minimum group size per quasi-identifier combination) before an export leaves the tenant |
