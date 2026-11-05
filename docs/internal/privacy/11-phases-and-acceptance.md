# 11 — Phases & acceptance

Order principle: **declare → generate/inventory → primitives → consent → storage → rights → AI & remaining integrations → UI last**.
Each phase ends with green tests, a native-AOT smoke publish with zero warnings, unchanged pre-existing test counts in every touched
repo, and a log section appended to this file (date, what was done, deviations, gotchas, test counts). This is the same protocol as the
validation and logging designs.

**Coordination with YFex.Logging:** logging Phase 0 puts `YFex.Diagnostics` in core and *uses* `DataSensitivity`/`[Sensitive]`. Privacy
Phase 0 must land first, or in the same session, so the logging probe contract compiles against the 7-value enum and `IClassifiedData<T>`.

## 1. Phases

> **Tiers vs phases (00 §3):** the first implementation is **Tier 1** = Phase 0 + Phase 1 *without* analyzers, conventions, snapshot and
> CLI + from Phase 2 only `Masks`, `TextScrubber` and the logging/UI integration. Everything else stays designed and is built when its
> trigger feature arrives (00 §3 Tier 2 table); Phase 1's analyzers/CLI and Phase 2's pseudonyms/blind indexes are Tier 3.

| Phase | Scope | Loads | Acceptance |
|---|---|---|---|
| **0 — Core contract** (⚑P1–P5 confirmed) | `YFex/src/YFex/Privacy/`: `DataSensitivity`, `DataSensitivityExtensions`, `DataCategory` (+ well-known), `DataClassification` + flags, 4 attributes, `IClassifiedData(<T>)`, `Classified`, `IPersonalDataShape<T>`, `IPersonalDataVisitor`, `PersonalDataMember(+Source)`, `PersonalDataShapes`, `Masks` + `MaskPreset`, `CanonicalForm`. `[Obsolete]` on `YFex.Consent.*` | README, 02 | core still has no new dependency; `Masks.*` allocation-free (test with `GC.GetAllocatedBytesForCurrentThread`); fail-closed tests (wrong length, wrong literal → hidden); `DataCategory.Define` idempotent and thread-safe; YFex.Tests count +N, nothing else changes |
| **1 — Generator, analyzers, inventory** (⚑P6, ⚑P7, ⚑P22, ⚑P23) | new repo `YFex.Privacy` + `privacy.slnx`; `YFex.Privacy.Generator` (classified types, unions, masks, canonical, `TryFind` wiring, shapes, module initializer, `PrivacySchema` text/inventory JSON, `[PurposeCatalog]`), analyzers YFP0001–YFP0030 + code fixes for 0001/0005/0008/0010, `PrivacyEmit.cs` shared file, `YFex.Privacy.Testing` (snapshot), CLI (`where`, `inventory`, `verify`); apply to `YFex.Entity.Public` (attributes, `TryGetValue` on `CpfCnpj`, `CompanyName` namespace fix) | 02, 03 | driver golden tests per emission; incremental caching test (unrelated edit → cached); `YFex.Entity.Public` builds with the analyzer-only reference; committed `PrivacySchema.verified.txt` for Entity.Public and Farma; running analyzers over all repos: **zero** YFP0001 on bare `Name`, every hit reviewed and listed in the log; `yfex-privacy where Cpf` lists the Farma, Brain and Payment holders |
| **2 — Primitives** (⚑P11–P14, ⚑P18) | `YFex.Privacy`: `Pseudonymizer`/`Pseudonym`/domains, `KeyRing`, `IKeyVault` (in-memory, file), root providers (environment, provided, DataProtection adapter; DPAPI in a Windows package), `AesGcmEnvelope` v2, `Redactor` + `RedactionPolicy` + `RedactedValue`, `MaskPattern`, `TextScrubber` + built-ins + `ValueObjectDetector<T>`, `PropertyRedactor`, `ExceptionScrubbing`, `ITokenVault` (in-memory), benchmarks, AotSmoke. Migrations: Profiling/System hashing and scrubber (shims), `Persistence.Encryption` over the envelope (0x01 still readable), `TryFindIn` on `Cpf`/`Telefone` | 05, 06 | budgets of 05 §9 met; scrubber corpus green (no false positives on NF-e keys, EAN-13, dates, amounts); 10 000 no-match scrubs allocate 0 B; envelope tamper tests (flip a bit, move to another AAD, unknown key, destroyed key → `KeyDestroyed`); Profiling + Persistence + Messaging encryption tests still green |
| **3 — Consent** (⚑P8, ⚑P9) | purposes runtime (`Purpose`, `PurposeRegistry`, `PurposeSet`), `LegalBasis` checks (YFP0022), notices + store, ledger (local JSONL, in-memory, contract tests), fold rules, `ConsentSnapshot`, `ConsentClient`, device signatures, policy per scope; Profiling + Security migration (presets, purpose-gated watchers, `consent.json` import, `ConsentEntryId` in queued events); Cqrs `.Privacy(...)` parsing + `PrivacyStage` + `Error.ConsentRequired` (wire v2); Rpc `IConsentQueries` compute method + invalidation | 04, 08 §5–§6, §12 | fold-rule tests (order, ±5 min "no" wins, expiry, material notice change, `NotRequired` for non-consent purposes); offline grant + online withdrawal round trip over the `Messaging.Rpc.Tests` in-memory bridge; consent proof survives a restart of the telemetry queue; `IsAllowed(ConsentLevel)` shim keeps old Profiling tests green |
| **4 — Storage** (⚑P12, ⚑P13, ⚑P15, ⚑P16) | `YFex.Privacy.Data` (SQL key vault, ledger, notices, token vault, erasure log); YFex.Data generator `EntityPrivacy` + `.Privacy(p => p.Encrypt(...))` + blind index + QueryBuilder rewrite + backfill/re-encryption jobs; EventSourcing two-part payload + shredded reads + stream subject tags; Persistence mandatory-encryption startup check; `Web.Data` protector migration; `PortableSecretStore` encryption | 05 §5–§8, 08 §2–§4 | SQLite + Postgres contract suites; AAD binding (ciphertext moved to another column/tenant fails); KEK rotation re-wraps without touching data; field-key rotation leaves mixed versions readable; shredding test on SQL encrypted column, ES stream and audit-like store; offline intake sealing → server re-seal test |
| **5 — Rights** (⚑P10, ⚑P20, ⚑P21) | `YFex.Privacy.Rights`: `PrivacyRequest` aggregate + messages, verification levels, generated subject resolver, participants (Data, ES, ledger, tokens, audit, devices, Brain, Search), export package, correction mapping, `ErasurePlanner` + dry run, retention rules + clocks + legal holds, sweeper `[Job]`, scheduled key destruction, erasure-log replay CLI, report renderer, `PrivacyPermissions` catalog; Fiscal module ships its retention rule | 07, 08 §9–§10 | Farma scenario 09 §8–§9 end to end, including the fiscal hold; executing a plan twice is harmless; **restore drill**: restore a pre-erasure database → `replay-erasures` → subject absent, and the startup check refuses to serve before the replay; overdue request raises an alert; every transition audited |
| **6 — AI & remaining integrations** (⚑P17, ⚑P19) | `PseudonymizationSession`, `AiDataPolicy`; Automation `RedactingAiAgent`; Capabilities redacting JSON for tool results + placeholder restore for arguments; Validation `x-sensitivity`/`x-category`/`x-mask`; server-side omission + privacy metadata in results; DataMining shapes + erasure-log check on import; Search integration; Logging adoption (if the logging engine exists by then); optional `IncidentRegister` | 08 §5, §7, §8, §11 | prompt-capture test: a fake cloud provider receives **no** value classified `Personal` or higher over a corpus of 200 tool results and prompts; restore round trip; placeholder-injection test (unknown/forged placeholder rejected); schema snapshot shows `x-sensitivity` |
| **7 — UI (last)** | YFex.Avalonia `PrivacyPresenter`, reveal (+ audit, justification, re-mask timer), consent prompt, preference center, operator consent with PIN pad, Blazor privacy portal (requests, downloads, preferences), request desk, sensitive-window capture exclusion | 08 §1 | masked by default for users without permission; every reveal audited; portal flows tested; dogfooding in Farma desktop |

## 2. Open questions to settle before Phase 0

1. ⚑P1–⚑P23 (README).
2. **Which app goes first?** Suggestion: Farma desktop + server. It has CPF na nota (legal obligation), loyalty (consent), future
   health data (art. 11), offline POS, and fiscal retention, so it exercises every part of the design.
3. **Root key hosting** on servers: environment variable (simple, weakest), file + OS permissions, DataProtection (already used by
   Persistence.Encryption), or a cloud KMS later (needs a package approval).
4. **Who provides the privacy portal** for end customers: the vendor (multi-tenant portal) or each tenant's own site (embedding the
   Blazor components)?
5. **Legal review** of the default retention numbers and the module-declared holds (fiscal 5 years is settled practice; SNGPC and other
   health records need the tenant's regulatory confirmation).
6. **AI providers**: which ones are approved for which tenants, and whether the providers' zero-retention options are contracted.

## 3. Migration order for existing code (no big bang)

1. Classify value objects in YFex.Domain (Phase 1). **No behaviour changes**; snapshots appear.
2. Fix YFP0001 hits in Farma/Payment/Brain one module at a time. Each fix is a type change caught by the compiler.
3. Swap hashing and scrubbing in Profiling/System behind shims (Phase 2). Pseudonyms change once, which is documented in the release
   notes (server-side joins on old hashes end).
4. Profiling consent → purposes, with the `consent.json` import (Phase 3). The `ConsentLevel` shim stays one release.
5. Persistence.Encryption → envelope v2 (Phase 2/4). Old blobs remain readable, and new writes use v2.
6. Data encryption per entity, opt-in, with backfill jobs (Phase 4).
7. Delete shims and `YFex.Consent` from core after one release.

## 4. What each session loads

| Session goal | Read | Skip |
|---|---|---|
| Phase 0 | README, 02 | everything else |
| Phase 1 | README, 02, 03 | 04–09 |
| Phase 2 | README, 02 §6–§7, 05, 06 | 04, 07 |
| Phase 3 | README, 04, 08 §5–§6, §12 | 06, 07 |
| Phase 4 | README, 05 §5–§8, 08 §2–§4 | 04, 06 |
| Phase 5 | README, 07, 08 §9–§10, 09 §8–§9 | 06 |
| Phase 6 | README, 08 §5, §7, §8, §11, 06 §4–§5 | 07 |
| Phase 7 | README, 04 §7, 08 §1, 09 §3 | 05–07 |

## Implementation log

*(empty: append per phase: date, what was done, deviations from this design, gotchas, test counts)*

### Tier 1 — core contract + generator + logging integration (2026-10-02)

User decisions: ⚑P1 new sibling repo `C:\Dev\YFex\YFex.Privacy`; ⚑P2 declaration contract in YFex core; scope = core contract +
generator + logging integration (Masks/TextScrubber runtime and the existing leaks are next).

**Done**

- YFex core `YFex/src/YFex/Privacy/`: `DataSensitivity` (+ `DataSensitivityExtensions.IsPersonal/IsNeverRecorded/Max`),
  `DataCategory` (interned, 17 well-known incl. the art. 5 II categories, `Define/Get/TryGet/FromId`), `DataClassification`
  (4 bytes, `Max`, `AtLeast`) + `DataClassificationFlags` (new `RecordLevel`), `SensitiveAttribute` (new `MaskFormat`),
  `IClassifiedData` / `IClassifiedData<TSelf>` (Classification, Classify, MaxMaskedLength, TryWriteMasked; boxed view via default
  interface members), `Classified` (Static/Of/TryMask/Mask), `Masks` + `MaskPreset` (pattern with `|` alternatives, email, initials,
  last/first N, ip, hidden; all fail closed, zero allocation).
- New repo `YFex.Privacy` (`privacy.slnx`, `Directory.Build.props`): `src/YFex.Privacy.Generator` (netstandard2.0; implements
  `IClassifiedData<T>` on every `[Sensitive]` type; diagnostics YFP0008 not partial, YFP0009 invalid mask, YFP0012 sensitive without
  special category, YFP0013 below the category minimum, YFP0017 generic type, YFP0018 MaskFormat ignored), `tests/YFex.Privacy.Generator.Tests`,
  `samples/YFex.Privacy.AotSmoke` (native AOT, real YFex.Domain value objects + LogWriter).
- YFex.Domain `YFex.Entity.Public`: now references YFex core and the privacy generator (analyzer only). Classified: `Cpf`
  (`***.###.###-**`, `MaskFormat = "F"`), `CpfCnpj` (CPF masked / CNPJ shown via alternatives), `Rg` (`{last:2}`), `Email` (`{email}`),
  `Telefone` (`{last:4}`), `FullName` and `PartyName` (`{initials}`). `Cnpj`/`CompanyName` unclassified. New `tests/YFex.Entity.Public.Tests`.
- Logging integration (YFex core `LogWriter` + repo YFex.Logging): `LogWriter.WriteClassified<T>(field, in value, floor)`,
  `WriteMaskedText(field, text, sensitivity)`, `WriteSensitive(field)`, `LogTextFlags.Masked`, `LogOmission.Sensitive`;
  `CaptureResolver` resolves `[Sensitive]` value objects (masked / never recorded), `[Sensitive]` on raw members (masked by level) and
  records classified as a whole (shape with a floor; own id kept; nested objects omitted; collections as counts);
  `RecordFormatter` renders `<redacted:sensitive>`. `YFex.Logging.Tests` references the privacy generator as an analyzer.

**Deviations from the design**

1. Tier 1 core = 9 types, not 16: `DataSubjectKeyAttribute`, `NotPersonalDataAttribute`, `SensitiveTypeAttribute<T>`, the
   personal-data shapes/visitor/registry, `CanonicalForm`, `TryFind` and `SubjectKind` wait for their tiers (canonical form + TryFind
   come with pseudonyms and the scrubber).
2. Generated masks call `Masks.TryWritePattern/TryWritePreset` with the pattern literal instead of straight-line code (same zero
   allocation; far simpler generator). TryFormat is called through a constrained generic helper, so explicit implementations work
   without boxing; types that are not `ISpanFormattable` (`Email`, `Telefone`) mask their `ToString()` (`Telefone.ToString()` allocates).
3. New: `MaskFormat` (the real `Cpf` formats as digits by default) and `|` alternatives (one attribute for `CpfCnpj`).
4. Value object vs record classified as a whole is decided by kind: **struct = value object** (masks itself), **class/record class
   = record level** (`RecordLevel` flag, hidden mask, members floored in logs).
5. Logs use **masks** in Tier 1 (`***.456.789-**`), not pseudonyms (Tier 3).
6. Under a record floor, a nested plain object is written as `Sensitive` (omitted) because log shapes are registered once per type;
   the record's own id (`Id` or `<Record>Id`) stays readable.

**Counts:** YFex.Tests 140 (+28), YFex.Privacy.Generator.Tests 18 (new), YFex.Entity.Public.Tests 6 (new), YFex.Logging.Tests 58 (+3),
YFex.Logging.Generator.Tests 16 (+1). AOT smoke: 0 IL warnings, exit 0, 1.9 MB. `LogSchema.verified.txt` gained the three privacy
test methods (reviewed: `Nome redacted`, `Cpf redacted`, `Dispensacao shape`).

**Gotchas**

- Projects that declare `[Sensitive]` types must reference `YFex.Privacy.Generator` as an analyzer (the logging generator emits
  `WriteClassified<T>` and relies on the generated `IClassifiedData<T>`; generators do not see each other's output).
- `CompanyName.cs` has no namespace: use `global::CompanyName`.
- Pre-existing, not caused by this work: `YFex.Fiscal.Public` (and NFe) do not build because they still `using YFex.Entity;`
  (types moved to `YFex.Entity.Public`).
- Analyzer message rule RS1032: no "e.g." / "art. 5" inside diagnostic messages (periods count as sentences).

**Next (Tier 1 remainder):** `TextScrubber` + `Masks`-based UI presenter, fix of the existing leaks (Profiling scrubber/hashing,
Web credential records, PortableSecretStore), logging `ILogTextScrubber` backed by the privacy scrubber.

### Tier 1 remainder, part 1 — TextScrubber + logging integration (2026-11-05)

User decisions: the scrubber lives in Privacy; Logging references Privacy (option A, never the reverse); logging gets a
`.Redaction(...)` policy section, configurable from code and `appsettings.json`.

**Done**

- New project `YFex.Privacy/src/YFex.Privacy` (runtime, references YFex core only, `IsAotCompatible`): `TextScrubber`,
  `TextScrubberBuilder`, `ITextDetector`, `DetectorPrefilter`, `BuiltInDetectors`, `DetectedKinds`, `ScrubReplacement`,
  `ScrubSummary`. Built-ins of 06 §3: e-mail, card (Luhn + length + IIN 2–6), JWT, bearer, known API keys, secret assignment (key
  kept), URL query (scheme/host/path kept), IPv4 (octets ≤ 255, version context excluded) + IPv6 (BCL parser). Prefilter pass
  (`SearchValues<string>`), overlap resolution (longer wins, then higher sensitivity), fail closed on regex timeout, truncation marker.
- YFex.Logging: references `YFex.Privacy`. `LoggingBuilder.Redaction(r => r.PersonalData(Mask|Omit).BuiltInDetectors(...).Detect(...)
  .MaximumInputLength(n))`, `RedactionOptions` on `LoggingOptions`, config section `Redaction` (`PersonalData`, `Detectors`,
  `MaximumInputLength`), `PrivacyLogTextScrubber` (the adapter), `ILogTextScrubber.OmitMaskedText` (default member) honoured by
  `RecordFormatter` and `PortableFields`. The engine's default scrubber is now the privacy one; `InterimLogTextScrubber` stays for
  tools without the engine. Memory and live-tap sinks now get the engine's scrubber (they always used the interim one before).

**Deviations from the design**

1. No `ScrubContext` / `Redactor` / pseudonyms yet: the replacement mode is fixed per scrubber (`ScrubReplacement.Label` or `.Mask`).
   Logs use masks by default (consistent with typed values in Tier 1), `Omit` uses labels. `Hash` waits for `Pseudonymizer` (Phase 2).
2. New built-in `LongDigitRuns` (not in `All`): fail-closed fallback for national documents until value objects expose
   `TryFindIn` (⚑P18, `Add<T>()` not built). Logging enables it by default, so CPF/phones in exception messages stay masked like
   before; it also masks barcodes, NF-e keys and timestamps in free text. Remove it once `Cpf`/`Telefone` detectors exist.
3. Redaction is read once at `Build` (reloads do not change it). The secret detectors are mandatory: removing any is a startup problem.
4. `ExceptionScrubbing` and `PropertyRedactor` (06 §6) not built: the logging sinks already scrub exception messages.

**Counts:** YFex.Privacy.Tests 41 (new), YFex.Privacy.Generator.Tests 18, YFex.Logging.Tests 65 (+7; 4 existing expectations moved
from `***@***` to the core e-mail mask `a***@x.com`), YFex.Logging.Generator.Tests 16. AOT: Privacy smoke (+5 scrub checks) and
Logging smoke, 0 IL warnings, exit 0. 10 000 no-match scrubs allocate 0 B (after tiered-JIT warm-up; tier-0 code allocates).

**Next (Tier 1 remainder):** UI mask presenter; existing leaks (Profiling scrubber/hashing, Web credential records,
`PortableSecretStore`); `Cpf.TryFindIn`/`Telefone.TryFindIn` + `Add<T>()` so logging can drop `LongDigitRuns`.

### Tier 1 remainder, part 2 — credential records leak in YFex.Web (2026-11-05)

**Done:** every `ApiCredential` record (`ApiKeyCredential`, `BasicCredential`, `BearerTokenCredential`, `OAuthClientCredential`,
`ClientCertificateCredential`) is `[Sensitive(DataSensitivity.Secret)]` (record level: structured logs omit it) and overrides
`PrintMembers`, so `ToString()`, interpolation and exception messages print `<redacted>` (`ApiCredential.Redacted`) in place of the
key, secret, token or certificate password. Identifiers stay readable (`HeaderName`, `Username`, `ClientId`, `GrantType`, `Scope`,
`Resource`, PKCS#12 length). `ClientCertificateCredential` already hid its bytes; it now also states whether a password is set.
`YFex.Web` references the privacy generator as an analyzer only. New `YFex.Web.Tests/Credentials/CredentialToStringTests`: the
secret is absent from `ToString`/interpolation/exception text for each type, each is classified `Secret` + `RecordLevel`, and a new
credential type that is not added to the test list makes the coverage test fail.

**Gotcha:** the compiler marks even a hand-written record `PrintMembers` with `[CompilerGenerated]`, so reflection cannot detect a
missing override; the behavioural test plus the coverage check is what enforces it.

**Counts:** YFex.Web.Tests 44 (+12); YFex.Web.Data.Tests 52, YFex.Web.Refit.Tests 35, YFex.Web.Generator.Tests 1 unchanged.

**Next (Tier 1 remainder):** `PortableSecretStore` plain text (YFex.Automation), value-object detectors (`Cpf`/`Telefone`
`TryFindIn` + `Add<T>()`), Profiling `PrivacyScrubber` → `TextScrubber`, UI mask presenter.

### Tier 1 remainder, part 3 — PortableSecretStore / LinuxSecretStore plain text (2026-11-05)

User decisions: encrypt through YFex.Cryptography (no fifth AES implementation), and move the repo from `C:\Dev\YFex.Automation`
to `C:\Dev\YFex\YFex.Automation` so it sits inside the ecosystem (it is not under git; moved with `mv`, nothing outside referenced it).

**Done**

- New project `YFex.Automation.Secrets` (Abstractions + YFex.Cryptography): `SealedFileSecretStore`, the Automation `ISecretStore`
  over Cryptography's `FileSecretStore` (sealed `*.sealedsecret` files bound to the key name, owner-only on Unix). Environment
  variable first (`YFEX_<KEY>`, unchanged). Root key: `YFEX_ROOT_KEY` when set, else a 32-byte key file next to the directory
  (`<directory>.root.key`). Legacy plain-text `*.secret` files are sealed on first read and deleted; `SetAsync` deletes them too.
- `PortableSecretStore` (Core) and `LinuxSecretStore` (Platform.Linux) are thin wrappers over it (Core and the platforms still do not
  reference each other). DI registrations create the store on first use (the root key file is created then, not at registration).
  `LinuxPlatformCapabilities.SecretEncryption` is now true. The Windows DPAPI store is unchanged.

**Limits (stated in the XML docs):** the key file protects secret files that leak on their own (backups, synced or copied folders),
not against code running as the same user; DPAPI (Windows) or an injected `YFEX_ROOT_KEY` covers that. A libsecret root key is the
later Linux upgrade. Not run on Linux in this session (Cryptography's file permissions are covered by its own tests).

**Counts:** YFex.Automation.Core.Tests 39 (+9: round trip with no plain text on disk, legacy migration, set replaces legacy,
environment wins, edited file throws, copied directory without its key fails, Portable + Linux encrypted, lazy registration; one
capability assertion flipped), Ai.Api.Tests 9, Platform.Windows.Tests 11 unchanged.

**Next (Tier 1 remainder):** value-object detectors (`Cpf`/`Telefone` `TryFindIn` + `Add<T>()`), Profiling `PrivacyScrubber` →
`TextScrubber`, UI mask presenter.

