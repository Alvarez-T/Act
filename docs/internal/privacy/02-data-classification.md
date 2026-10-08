# 02 — Data classification

Classification answers three questions about a value: **how dangerous is it** (`DataSensitivity`), **what kind of data is it**
(`DataCategory`), and **what role does it play** (identifier of a subject, free text…, `DataClassificationFlags`). It is declared
**once on the type** and resolved for every member by the generator (03).

## 1. What lives in YFex core and why (⚑P1, ⚑P2)

**Rule:** a type goes to YFex core when **contract assemblies or generated code inside contract assemblies** need it. Contract
assemblies are `YFex.Entity.Public`, `*.Public` message projects, and value-object libraries. They must compile with YFex core plus
the privacy generator as an **analyzer-only** reference. Everything that *acts* (keys, HMAC, AES, consent, rights) goes to the
YFex.Privacy repo.

| Core type (`YFex/src/YFex/Privacy/`, namespace `YFex.Privacy`) | Kind | Why it cannot live in the YFex.Privacy repo |
|---|---|---|
| `DataSensitivity` | enum (closed) | used in `[Sensitive(...)]` on value objects in contract assemblies |
| `DataCategory` | sealed class, interned | referenced by generated classification constants |
| `DataClassification`, `DataClassificationFlags` | 4-byte struct + flags | returned by generated `IClassifiedData<T>` members |
| `SensitiveAttribute`, `DataSubjectKeyAttribute`, `NotPersonalDataAttribute`, `SensitiveTypeAttribute<T>` | attributes | placed in contract assemblies |
| `IClassifiedData`, `IClassifiedData<TSelf>` | interfaces | implemented by generated code on `Cpf`, `Email`… |
| `IPersonalDataShape<TSelf>`, `IPersonalDataVisitor`, `PersonalDataMember`, `PersonalDataMemberSource` | interfaces + struct + enum | implemented by generated code on commands/entities/events in contract assemblies |
| `PersonalDataShapes` | static registry (`Type` → member table), filled by generated module initializers | lets object-typed paths (JSON contract modifiers, UI bindings) find a type's personal members without reflection (03 §2.3) |
| `Masks`, `CanonicalForm`, `Classified` | static helpers (span only, ~150 lines) | called by generated mask/canonical code in contract assemblies |

That is **16 small types**, comparable to the logging probe contract (logging ⚑L1). No dependency, no allocation, AOT-safe.

**Alternative (rejected, listed for ⚑P2):** only `SensitiveAttribute` + `DataSensitivity` in core, read by every *other* generator
at compile time. It works for logging (the logging generator reads the attribute), but then UI masking, AI redaction and rights
tooling have **no runtime contract**. They would need a registry populated by reflection, or each library would re-implement masks.

The existing `YFex/src/YFex/Consent/` folder **leaves core** (consent is behaviour, not declaration). See 04 §8.

## 2. `DataSensitivity` (closed set, ⚑P3)

```csharp
namespace YFex.Privacy;

/// Protection level of a value. Closed set: the privacy engine owns the handling of each level.
/// Ordered by protection strength, so "at least X" policies are a comparison.
public enum DataSensitivity : byte
{
    Public = 0,              // published data: product codes, NCM/CFOP, company CNPJ and legal name, list prices
    Internal = 1,            // non-personal operational data: ids, amounts, quantities, tenant ids, technical values (default)
    Personal = 2,            // LGPD art. 5 I: name, e-mail, phone, address, CEP, birth date, customer number linked to a person
    PersonalIdentifier = 3,  // art. 5 I, unique official identifiers with fraud and linkage risk: CPF, RG, CNH, passport, NIS/PIS
    SensitivePersonal = 4,   // art. 5 II / art. 11: health, biometric, genetic, ethnic origin, religion, political opinion,
                             //   union membership, sex life. Requires an art. 11 legal basis (04 §2)
    Financial = 5,           // card number, bank account, Pix key bound to an account: never logged, encrypted or tokenized at rest
    Secret = 6,              // passwords, tokens, API keys, private keys, certificates. Not personal data: credentials.
                             //   Never logged, never displayed, never sent to AI, never exported
}
```

**Why `PersonalIdentifier` (correction of logging 02 §5.2).** The logging design labelled CPF `SensitivePersonal` "(LGPD art. 5 II)".
Art. 5 II defines *dado pessoal sensível* as racial or ethnic origin, religious conviction, political opinion, union or religious,
philosophical or political organization membership, **health or sex life, genetic or biometric data**. CPF is ordinary personal
data (art. 5 I). The mislabel has real consequences: art. 11 does not allow `ContractPerformance` or `LegitimateInterest`, so every
sale with *CPF na nota* would appear to need specific, highlighted consent. It also hides the truly sensitive data a pharmacy
holds (medication history). CPF still deserves stronger handling than an e-mail: it is a unique national key, it enables fraud,
and it links datasets. Hence a separate level between `Personal` and `SensitivePersonal`.

Default handling per level (the policy in 05 §1 can tighten every cell; `Financial` and `Secret` cannot be relaxed):

| Sensitivity | Diagnostic logs | Audit | UI display | SQL at rest | AI (cloud) | AI (local model) | Subject export |
|---|---|---|---|---|---|---|---|
| `Public` / `Internal` | value | value | clear | clear | value | value | included if linked |
| `Personal` | pseudonym `#h:` | encrypted (subject key) | clear on screens that need it | clear (opt-in encryption) | placeholder | value | included |
| `PersonalIdentifier` | pseudonym | encrypted | **masked**, reveal by permission (audited) | clear; opt-in encryption + blind index (⚑P15) | placeholder | placeholder | included |
| `SensitivePersonal` | erased | encrypted | masked, reveal by permission | **encrypted by default** | never | placeholder | included |
| `Financial` | erased | erased (token / last 4 only) | `****1234` | **encrypted or tokenized** | never | never | masked |
| `Secret` | erased | erased | hidden | credential protector (05 §6.3) | never | never | never |

```csharp
public static class DataSensitivityExtensions
{
    /// Personal data in the LGPD sense (a natural person can be identified). Secret is excluded: credentials are protected
    /// for security reasons, not as personal data.
    public static bool IsPersonal(this DataSensitivity sensitivity)
        => sensitivity is >= DataSensitivity.Personal and <= DataSensitivity.Financial;

    /// Values that must never leave the process in any reversible or joinable form (no hash, no mask in logs).
    public static bool IsNeverLogged(this DataSensitivity sensitivity) => sensitivity >= DataSensitivity.Financial;
}
```

## 3. `DataCategory` (open set, interned, ⚑P4)

Sensitivity says *how careful*. Category says *what kind*. Categories drive the inventory, the ROPA, privacy notices, retention
rules ("delete contact data 2 years after the last purchase") and the art. 11 checks. Apps add categories, so this is not an enum
(memory: enums only for closed sets). It follows the `RawType.Define` pattern.

```csharp
namespace YFex.Privacy;

public sealed class DataCategory : IEquatable<DataCategory>
{
    public string Key { get; }                              // persisted form, e.g. "health"; lower-case, [a-z0-9-.]
    public ushort Id { get; }                               // process-local interned id; never persisted
    public DataSensitivity MinimumSensitivity { get; }      // health → SensitivePersonal; contact → Personal
    public bool IsSpecialCategory => MinimumSensitivity == DataSensitivity.SensitivePersonal;   // art. 5 II

    public static DataCategory Define(string key, DataSensitivity minimumSensitivity);         // idempotent for equal arguments
    public static bool TryGet(string key, [NotNullWhen(true)] out DataCategory? category);
    public static DataCategory FromId(ushort id);

    // Well-known categories (registered on first access to the class)
    public static DataCategory None { get; }               // not personal data
    public static DataCategory Identification { get; }     // CPF, RG, name, customer number            (Personal)
    public static DataCategory Contact { get; }            // e-mail, phone, address, CEP               (Personal)
    public static DataCategory Demographic { get; }        // birth date, gender, marital status         (Personal)
    public static DataCategory Location { get; }           // geolocation, IP address                   (Personal)
    public static DataCategory Behavioral { get; }         // usage, navigation, purchase history       (Personal)
    public static DataCategory Financial { get; }          // card, bank account, income                (Financial)
    public static DataCategory Health { get; }             // diagnoses, prescriptions, medications     (SensitivePersonal)
    public static DataCategory Biometric { get; }          // fingerprint, face template                (SensitivePersonal)
    public static DataCategory Genetic { get; }            //                                           (SensitivePersonal)
    public static DataCategory EthnicOrigin { get; }       //                                           (SensitivePersonal)
    public static DataCategory ReligiousBelief { get; }    //                                           (SensitivePersonal)
    public static DataCategory PoliticalOpinion { get; }   //                                           (SensitivePersonal)
    public static DataCategory Membership { get; }         // union / religious / political organization (SensitivePersonal)
    public static DataCategory SexLife { get; }            //                                           (SensitivePersonal)
    public static DataCategory Credentials { get; }        // passwords, tokens                         (Secret)
    public static DataCategory FreeText { get; }           // unstructured; may contain anything        (Personal, scrubbed)
}
```

Generator checks: a category whose `MinimumSensitivity` is higher than the declared sensitivity is an error (YFP0013:
`[Sensitive(Personal, Category = "health")]`). `SensitivePersonal` without a special category is an error (YFP0012).

## 4. `DataClassification`, flags and attributes

```csharp
namespace YFex.Privacy;

/// 4 bytes, passed by value everywhere. Equality is structural.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct DataClassification : IEquatable<DataClassification>
{
    public DataClassification(DataSensitivity sensitivity, DataCategory category, DataClassificationFlags flags = DataClassificationFlags.None);

    public DataSensitivity Sensitivity { get; }
    public ushort CategoryId { get; }
    public DataCategory Category => DataCategory.FromId(CategoryId);
    public DataClassificationFlags Flags { get; }

    public bool IsPersonal => Sensitivity.IsPersonal();
    public bool Has(DataClassificationFlags flag) => (Flags & flag) != 0;

    public static DataClassification NotPersonal { get; }                               // Internal, None
    /// Stricter of the two (sensitivity wins; the category of the stricter one is kept; flags are OR-ed).
    public static DataClassification Max(DataClassification left, DataClassification right);
}

[Flags]
public enum DataClassificationFlags : byte
{
    None = 0,
    SubjectKey = 1 << 0,      // surrogate id of a data subject ([DataSubjectKey] types): locates the subject's records
    Identifier = 1 << 1,      // natural identifier (CPF, e-mail, phone): blind-indexed, resolves to a subject (07 §2)
    VariesByValue = 1 << 2,   // unions: call Classify(in value) for the actual case
    FreeText = 1 << 3,        // unstructured text: scrubbed when it leaves the process (06)
    ByConvention = 1 << 4,    // decided by a name/type convention, not declared ("convention" in the snapshot)
}
```

```csharp
namespace YFex.Privacy;

/// Normal use: ONCE on the value-object type. Exception: on a raw member that cannot be a value object yet.
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field
              | AttributeTargets.Parameter, Inherited = false)]
public sealed class SensitiveAttribute(DataSensitivity sensitivity) : Attribute
{
    public DataSensitivity Sensitivity { get; } = sensitivity;
    public string? Category { get; init; }      // DataCategory key; default by sensitivity: Personal → "identification",
                                                // PersonalIdentifier → "identification", Financial → "financial",
                                                // Secret → "credentials"; SensitivePersonal → REQUIRED (YFP0012)
    public string? Mask { get; init; }          // §7; default by sensitivity (§7.3); alternatives separated by '|'
    public string? MaskFormat { get; init; }    // format passed to TryFormat for the mask source ("F" = punctuated CPF)
    public bool Identifier { get; init; }       // natural identifier: blind index + subject resolution (Cpf, Email: true)
}

/// On the id type of a data subject, once: CustomerId, PatientId, EmployeeId. The string is the subject kind (open set).
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class DataSubjectKeyAttribute(string subjectKind) : Attribute
{
    public string SubjectKind { get; } = subjectKind;
}

/// Silences a convention false positive. The reason is printed in the snapshot, so reviewers see why.
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field
              | AttributeTargets.Parameter, Inherited = false)]
public sealed class NotPersonalDataAttribute : Attribute
{
    public string? Reason { get; init; }
}

/// Classifies a type you do not own (BCL, 3rd party). Assembly level, read by the generator at compile time.
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class SensitiveTypeAttribute<T>(DataSensitivity sensitivity) : Attribute
{
    public DataSensitivity Sensitivity { get; } = sensitivity;
    public string? Category { get; init; }
    public string? Mask { get; init; }
    public bool Identifier { get; init; }
}
```

Note the rename from the logging draft: the constructor argument is `Sensitivity` (not `Level`), because "level" already means
log level in the logging vocabulary.

## 5. Member resolution: type first, then convention, then snapshot (⚑P5)

> **Tier (00 §3):** rules 1–6 and record-level classification (§5.5) are **Tier 1**. Name conventions (rules 7–8, §5.2), the snapshot
> and per-case unions (§5.3) are **Tier 3** (optional). Without them, an unclassified raw `string` is simply "not personal" and is still
> scrubbed whenever it leaves the process (06).

Every generator that captures, displays, stores or sends data (Privacy, Logging, Validation schemas, State, Data, EventSourcing,
Cqrs) resolves a member's classification with **one shared routine** (`PrivacyEmit.cs`, linked source, 03 §6), in this order. The
first rule that matches wins:

| # | Rule | Result | Source shown in snapshot |
|---|---|---|---|
| 1 | `[NotPersonalData]` on the member | not personal | `not personal: <reason>` |
| 2 | `[Sensitive]` on the member (exception path) | as declared | `member attribute` (+ YFP0014 if the member type is already classified differently) |
| 3 | member type (after unwrapping `Nullable<T>`, arrays, `List<T>`, `IReadOnlyList<T>`, `ImmutableArray<T>`, dictionary values) is classified by `[Sensitive]` / implements `IClassifiedData<T>` | the type's classification | `type` |
| 4 | member type declared by `[assembly: SensitiveType<T>(...)]` | as declared | `assembly declaration` |
| 5 | member type has `[DataSubjectKey]` | `Internal` + `SubjectKey` | `subject key (customer)` |
| 6 | member type holds personal members itself (record/class/struct with a personal-data shape) | nested | `nested` |
| 7 | raw `string`, `ReadOnlyMemory<char>`, `char[]`, `DateOnly`, `DateTime`, `double`: **name convention** (§5.2) matches | the convention's classification + `ByConvention` | `convention(<token>)` + analyzer suggestion |
| 8 | raw string whose name is free-text-like (`observacao`, `obs`, `notes`, `comment`, `description`, `message`, `payload`, `raw`, `json`, `body`) | `Personal` + `FreeText` | `free text` |
| 9 | anything else | not personal | (not listed) |

Strings that reach rule 9 are still **scrubbed** whenever they leave the process (logs, AI, exceptions), because a "not personal"
string can contain anything at run time (06).

### 5.1 Collections and nesting

A `List<Cpf>` is classified like `Cpf`. A `List<ItemVenda>` where `ItemVenda` has no personal member is not personal. Nesting is
followed to depth 4 (configurable via `yfex_privacy.max_depth` in `.editorconfig`). Cycles are cut and reported (YFP0016).

### 5.2 Name conventions (token based)

Names are split into tokens (camelCase, PascalCase, `_`, digits), lower-cased and stripped of diacritics, then matched on **whole
tokens**. This fixes today's substring bug (`"multipleMonitors"` contains `ip`). Portuguese and English are built in. Projects can
add tokens in `.editorconfig` (`yfex_privacy.extra_personal_tokens = matricula, prontuario`).

| Tokens (any of) | Classification | Category | Analyzer suggestion (when the type exists in a referenced assembly) |
|---|---|---|---|
| `cpf`, `rg`, `cnh`, `passaporte`, `passport`, `nis`, `pis`, `pasep`, `nit` | `PersonalIdentifier` + `Identifier` | identification | `Cpf`, `Rg` |
| `cpfcnpj`, `cpf`+`cnpj`, `documento`, `document`, `taxid` | `PersonalIdentifier` + `Identifier` (conservative: may be a CPF) | identification | `CpfCnpj` |
| `email`, `e`+`mail` | `Personal` + `Identifier` | contact | `Email` |
| `telefone`, `phone`, `celular`, `mobile`, `whatsapp`, `fone` | `Personal` + `Identifier` | contact | `Telefone` |
| `endereco`, `address`, `logradouro`, `street`, `cep`, `zipcode`, `zip`, `postalcode` | `Personal` | contact | (none yet; 10 §3 suggests `Endereco`, `Cep`) |
| `nomecompleto`, `fullname`, `firstname`, `lastname`, `sobrenome`, `nomesocial`, compound `nome`/`name` **+** a person token (`cliente`, `customer`, `paciente`, `patient`, `pessoa`, `person`, `responsavel`, `mae`, `pai`, `contato`, `contact`, `titular`, `holder`, `payer`, `pagador`) | `Personal` | identification | `FullName` |
| bare `nome` / `name` inside a type whose own name has a person token (`Customer`, `Cliente`, `Paciente`, `Pessoa`, `Payer`, `Contato`…) | `Personal` | identification | `FullName` |
| `nascimento`, `birthdate`, `birthday`, `datanascimento`, `sexo`, `gender`, `genero`, `estadocivil`, `maritalstatus` | `Personal` | demographic | — |
| `latitude`, `longitude`, `geolocation`, `ip`, `ipaddress` | `Personal` | location | — |
| `senha`, `password`, `passwd`, `pwd`, `token`, `secret`, `segredo`, `apikey`, `privatekey`, `chaveprivada`, `pin`, `otp`, `cvv`, `cvc`, `clientsecret`, `refreshtoken`, `connectionstring` | `Secret` | credentials | YFP0002 |
| `numerocartao`, `cardnumber`, `pan`, `iban`, `contabancaria`, `bankaccount`, `chavepix`, `pixkey` | `Financial` | financial | YFP0003 |
| `diagnostico`, `diagnosis`, `cid`, `prescricao`, `prescription`, `alergia`, `allergy`, `medicamentocontrolado`, `impressaodigital`, `fingerprint`, `biometria`, `biometric`, `raca`, `etnia`, `ethnicity`, `religiao`, `religion` | `SensitivePersonal` | by token (health/biometric/…) | YFP0004 (warning: declare a value object or `[Sensitive]` explicitly) |

**Bare `Name`/`Nome` outside person-like types is never personal.** The repos contain dozens of `string Name` for schema, file,
job and capability names. Ambiguous Portuguese words are deliberately absent: `receita` (revenue *or* prescription), `digital`
(digital *or* fingerprint).

### 5.3 Unions (⚑P6, Tier 3)

`[Union]` value objects classify **per case**. The static `Classification` is the strictest case (used when the value is unknown,
e.g. in schemas). The generated `Classify(in value)` returns the case that is actually present, with `VariesByValue` set on the
static classification. The generator finds cases through the `IUnion` convention `bool TryGetValue(out TCase value)` (one overload
per case). `CpfCnpj` currently exposes `AsCpf`/`AsCnpj`, so it gains the two `TryGetValue` overloads (small change in YFex.Domain).

| Union | Cases | Static | Per value |
|---|---|---|---|
| `CpfCnpj` | `Cpf`, `Cnpj` | `PersonalIdentifier` | CPF → `PersonalIdentifier`; CNPJ → not personal (`Cnpj` has no attribute) |
| `PartyName` | `FullName`, `CompanyName` | `Personal` | Person → `Personal`; Company → not personal |

So a company's CNPJ in a sale is logged in clear and shown unmasked, while a consumer's CPF is pseudonymized and masked,
**through the same member** `Cliente: CpfCnpj`. (Caveat for individual entrepreneurs: 10 §1, row 1.12.)

### 5.4 Classification that depends on another member

YFex.Brain's `EntityIdentifier { IdentifierType Type; string Value }`: `Value` is a passport, an RG, a NIS, or an internal source
code, depending on `Type`. A per-value classification needs a **value object**, so the recommendation is a `[Union]` value object
`PersonDocument` (cases `Rg`, `Nis`, `Pis`, `Passport`, `ExternalCode`), classified per case like `CpfCnpj`. Until it exists,
`[Sensitive(DataSensitivity.PersonalIdentifier, Identifier = true)]` on `Value` is the conservative exception (shown as `member
attribute` in the snapshot). No "classified by another member" mechanism is proposed: it would need per-member configuration,
which is the boilerplate we avoid.

### 5.5 Record-level classification (⚑P24)

Some data is sensitive only **in combination**. A medication (`Sku`) is not personal. A CPF is personal but not health data. A record
saying *this person bought clonazepam with this prescription* is **health data** (art. 5 II). So the classification belongs to the
**record type**, not to any member:

```csharp
[Sensitive(DataSensitivity.SensitivePersonal, Category = "health")]
public sealed partial record DispensacaoControlado(
    DispensacaoId Id, CustomerId Paciente, Cpf CpfComprador, FullName NomeComprador,
    Sku Medicamento, Quantity Quantidade, string NumeroNotificacao, string CrmPrescritor, DateOnly Data);
```

Rules (normative):

1. Every member of the record is treated as **at least** the record's level, including non-personal members (`Medicamento`,
   `Quantidade`, `Data`): together with the subject they reveal the condition.
2. **Exception: the record's own identity** (its id type, here `DispensacaoId`) keeps its own classification, so logs and audits can
   still say *which* record was touched (`id=D-1029`) without saying *what* it contains.
3. Members whose own classification is stricter keep it (a `Secret` member stays `Secret`).
4. A record that **contains** a record-level classified type (e.g. a `RegistrarDispensacaoCommand` with a `DispensacaoControlado` member,
   or a list of them) inherits the level for that member (nesting rule 6).
5. Record-level classification does **not** mask screens by itself. Access to the screen/query is a YFex.Authorization permission
   (e.g. `farmacia.controlados.ver`). The classification makes everything **outside** the authorized screen safe by default: logs,
   AI, integrations, exports to third parties, and read auditing (Tier 2).

**👁 Result** (log line of the command that registers it)

```
10:52:03.114 INF Controlados.RegistrarDispensacao completed 12.4ms id=D-1029 <redacted:health 8 fields> actor=farmaceutico:lucia
```

## 6. `IClassifiedData<TSelf>` — the runtime contract (generated)

```csharp
namespace YFex.Privacy;

/// Non-generic view for boxed values (UI bindings, object-typed paths). Same pattern as core IRawValue.
public interface IClassifiedData
{
    DataClassification Classify();
    bool TryMask(Span<char> destination, out int written);
}

/// Implemented by GENERATED code on every [Sensitive] type (03 §2). Static abstract: no boxing, no virtual call.
public interface IClassifiedData<TSelf> : IClassifiedData where TSelf : IClassifiedData<TSelf>
{
    /// Strictest classification of the type (unions: of any case).
    static abstract DataClassification Classification { get; }

    /// Classification of this value (unions: of the present case). Default: the static one.
    static virtual DataClassification Classify(in TSelf value) => TSelf.Classification;

    /// Upper bound of TryWriteMasked output, so callers can stackalloc.
    static abstract int MaxMaskedLength { get; }

    /// Display mask (§7). Fails closed: if the value does not fit the mask, writes the hidden form.
    static abstract bool TryWriteMasked(in TSelf value, Span<char> destination, out int written);

    /// Deterministic, culture-invariant UTF-8 form. Input of pseudonyms, blind indexes and tokens (05 §3).
    /// Equal values MUST produce equal bytes.
    static abstract int MaxCanonicalLength { get; }
    static abstract bool TryWriteCanonical(in TSelf value, Span<byte> destination, out int written);

    /// Free-text detection (06 §4): finds the first occurrence of a value of this type in text. Default: not detectable.
    static virtual bool TryFind(ReadOnlySpan<char> text, out int start, out int length)
    {
        start = 0; length = 0; return false;
    }

    /// For [DataSubjectKey] types: the subject kind ("customer", "patient"). Null for everything else.
    static virtual string? SubjectKind => null;

    DataClassification IClassifiedData.Classify() => TSelf.Classify((TSelf)this);
    bool IClassifiedData.TryMask(Span<char> destination, out int written) => TSelf.TryWriteMasked((TSelf)this, destination, out written);
}

/// Generic helpers so generated and library code can call static members through a type parameter.
public static class Classified
{
    public static DataClassification Static<T>() where T : IClassifiedData<T> => T.Classification;
    public static DataClassification Of<T>(in T value) where T : IClassifiedData<T> => T.Classify(in value);
    public static bool TryMask<T>(in T value, Span<char> destination, out int written) where T : IClassifiedData<T>
        => T.TryWriteMasked(in value, destination, out written);
    public static bool TryCanonical<T>(in T value, Span<byte> destination, out int written) where T : IClassifiedData<T>
        => T.TryWriteCanonical(in value, destination, out written);
}
```

**Canonical form rules** (generated, 03 §2): `IRawValue<T>` types use their raw payload. `IUtf8SpanFormattable` types use
`TryFormat(utf8, out n, default, InvariantCulture)`. `ISpanFormattable` types format to a stack buffer and transcode. Any other
type must provide `static bool TryWriteCanonical(...)` by hand (YFP0015). A canonical form is only correct if the type normalizes
on construction (`Cpf` keeps digits; `Email` must lower-case the domain, see 10 §1, row 1.20).

**Logging alignment:** logging's `ILogValue<T>.Sensitivity` (logging 02 §5.1) is **removed**. The logging generator reads
`IClassifiedData<T>` (rule 3) through the shared routine, so there is one source of truth.

## 7. Mask language (⚑P7)

### 7.1 Positional patterns

A pattern is applied **position by position to the type's display form** (`TryFormat(default, InvariantCulture)`):

| Pattern char | Meaning |
|---|---|
| `#` | copy the source character |
| `*` | write `*` |
| anything else | literal; the source character at that position **must be equal**, otherwise the whole value is hidden (fail closed) |

The display length must equal the pattern length, otherwise the value is hidden. Several patterns can be given, separated by `|`: the first whose length matches is used (`CpfCnpj`: `***.###.###-**|##.###.###/####-##` masks a CPF and shows a CNPJ). `MaskFormat` chooses the format the mask applies to (`Cpf` formats as digits by default; `"F"` gives `123.456.789-09`). Example: `Cpf` displays as `123.456.789-09`,
pattern `***.###.###-**` gives `***.456.789-**`.

### 7.2 Presets (for values without a fixed layout)

| Preset | Example input | Output | Use |
|---|---|---|---|
| `{email}` | `joao.silva@acme.com.br` | `j***@acme.com.br` | e-mail (first character of the local part, fixed `***`, full domain) |
| `{initials}` | `João da Silva Santos` | `J. S. S.` | person names (words of ≥ 3 letters; does not leak length) |
| `{last:N}` | `(11) 98765-4321`, N=4 | `****4321` | phones, cards, accounts (fixed 4-star prefix) |
| `{first:N}` | `Rua das Flores 123`, N=3 | `Rua****` | addresses, free text previews |
| `{ip}` | `192.168.10.42` / `2001:db8::1` | `192.168.10.*` / `2001:db8::*` | IP addresses (replaces both `MaskIpAddress` copies) |
| `{hidden}` | anything | `******` | default for `SensitivePersonal` free text, `Secret` |

### 7.3 Defaults when `Mask` is omitted

`Personal` → `{first:1}`, `PersonalIdentifier` → `{last:2}`, `SensitivePersonal` → `{hidden}`, `Financial` → `{last:4}`,
`Secret` → `{hidden}`, `Public`/`Internal` → no mask (display as is).

### 7.4 Runtime patterns

Value objects get **generated** straight-line code (no parsing at run time). Raw strings classified by convention or by
`[Sensitive]` on a member use the preset for their convention row (e-mail → `{email}`, phone → `{last:4}`…), executed by
`Masks.TryWritePreset(...)` in core (span in, span out, no allocation). Arbitrary runtime patterns (for example a tenant-defined
mask) compile once into a `MaskPattern` (YFex.Privacy runtime) and are cached by the caller.

```csharp
namespace YFex.Privacy;

public static class Masks   // core; used by generated code and by YFex.Privacy
{
    public static bool TryWriteHidden(Span<char> destination, out int written);
    public static bool TryWritePattern(ReadOnlySpan<char> source, ReadOnlySpan<char> pattern, Span<char> destination, out int written);
    public static bool TryWriteEmail(ReadOnlySpan<char> source, Span<char> destination, out int written);
    public static bool TryWriteInitials(ReadOnlySpan<char> source, Span<char> destination, out int written);
    public static bool TryWriteLast(ReadOnlySpan<char> source, int count, Span<char> destination, out int written);
    public static bool TryWriteFirst(ReadOnlySpan<char> source, int count, Span<char> destination, out int written);
    public static bool TryWriteIpAddress(ReadOnlySpan<char> source, Span<char> destination, out int written);
    public static bool TryWritePreset(ReadOnlySpan<char> source, MaskPreset preset, int count, Span<char> destination, out int written);
}

public enum MaskPreset : byte { None, Hidden, Email, Initials, Last, First, IpAddress, Pattern }
```

## 8. Dev writes / generated / result

**✍ Dev writes** (YFex.Domain `YFex.Entity.Public`: one line per type; the csproj adds the generator as an analyzer)

```csharp
[Sensitive(DataSensitivity.PersonalIdentifier, Mask = "***.###.###-**", Identifier = true)]
public readonly partial struct Cpf { /* existing code unchanged */ }

// Cnpj: NO attribute — a company's tax id is not personal data (no attribute = not personal)
public readonly partial struct Cnpj { … }

[Sensitive(DataSensitivity.PersonalIdentifier)]          // union: per-case refinement is generated
public readonly partial struct CpfCnpj { … }

[Sensitive(DataSensitivity.Personal, Category = "contact", Mask = "{email}", Identifier = true)]
public readonly partial struct Email { … }

[Sensitive(DataSensitivity.Personal, Category = "contact", Mask = "{last:4}", Identifier = true)]
public readonly partial struct Telefone { … }

[Sensitive(DataSensitivity.PersonalIdentifier, Identifier = true)]                // mask default {last:2}
public readonly partial struct Rg { … }

[Sensitive(DataSensitivity.Personal, Mask = "{initials}")]
public readonly partial struct FullName { … }

[Sensitive(DataSensitivity.Personal)]                    // union: Person → Personal, Company → not personal
public readonly partial struct PartyName { … }
```

```xml
<!-- YFex.Entity.Public.csproj: analyzer only, no runtime reference to YFex.Privacy -->
<ProjectReference Include="..\..\..\YFex.Privacy\src\YFex.Privacy.Generator\YFex.Privacy.Generator.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

**⚙ Generated** (excerpt for `Cpf` and `CpfCnpj`)

```csharp
// <auto-generated/> YFex.Privacy.Generator
partial struct Cpf : global::YFex.Privacy.IClassifiedData<Cpf>
{
    private static readonly global::YFex.Privacy.DataClassification __privacyClassification = new(
        global::YFex.Privacy.DataSensitivity.PersonalIdentifier,
        global::YFex.Privacy.DataCategory.Identification,
        global::YFex.Privacy.DataClassificationFlags.Identifier);

    static global::YFex.Privacy.DataClassification global::YFex.Privacy.IClassifiedData<Cpf>.Classification => __privacyClassification;
    static int global::YFex.Privacy.IClassifiedData<Cpf>.MaxMaskedLength => 14;
    static int global::YFex.Privacy.IClassifiedData<Cpf>.MaxCanonicalLength => 64;

    // Pattern "***.###.###-**" compiled to straight-line code over the display form.
    static bool global::YFex.Privacy.IClassifiedData<Cpf>.TryWriteMasked(in Cpf value, global::System.Span<char> destination, out int written)
    {
        global::System.Span<char> source = stackalloc char[14];
        if (destination.Length < 14
            || !value.TryFormat(source, out int length, default, global::System.Globalization.CultureInfo.InvariantCulture)
            || length != 14 || source[3] != '.' || source[7] != '.' || source[11] != '-')
            return global::YFex.Privacy.Masks.TryWriteHidden(destination, out written);   // fail closed
        destination[0] = '*'; destination[1] = '*'; destination[2] = '*'; destination[3] = '.';
        source.Slice(4, 3).CopyTo(destination.Slice(4));  destination[7] = '.';
        source.Slice(8, 3).CopyTo(destination.Slice(8));  destination[11] = '-';
        destination[12] = '*'; destination[13] = '*';
        written = 14;
        return true;
    }

    static bool global::YFex.Privacy.IClassifiedData<Cpf>.TryWriteCanonical(in Cpf value, global::System.Span<byte> destination, out int written)
        => global::YFex.Privacy.CanonicalForm.TryWriteSpanFormattable(in value, destination, out written);
}

partial struct CpfCnpj : global::YFex.Privacy.IClassifiedData<CpfCnpj>
{
    private static readonly global::YFex.Privacy.DataClassification __privacyClassification = new(
        global::YFex.Privacy.DataSensitivity.PersonalIdentifier,
        global::YFex.Privacy.DataCategory.Identification,
        global::YFex.Privacy.DataClassificationFlags.Identifier | global::YFex.Privacy.DataClassificationFlags.VariesByValue);

    static global::YFex.Privacy.DataClassification global::YFex.Privacy.IClassifiedData<CpfCnpj>.Classification => __privacyClassification;

    static global::YFex.Privacy.DataClassification global::YFex.Privacy.IClassifiedData<CpfCnpj>.Classify(in CpfCnpj value)
    {
        if (value.TryGetValue(out Cpf cpf)) return global::YFex.Privacy.Classified.Of(in cpf);
        if (value.TryGetValue(out Cnpj cnpj)) return global::YFex.Privacy.Classified.Of(in cnpj);
        return __privacyClassification;                     // default/empty union: strictest
    }

    static bool global::YFex.Privacy.IClassifiedData<CpfCnpj>.TryWriteMasked(in CpfCnpj value, global::System.Span<char> destination, out int written)
    {
        if (value.TryGetValue(out Cpf cpf)) return global::YFex.Privacy.Classified.TryMask(in cpf, destination, out written);
        if (value.TryGetValue(out Cnpj cnpj)) return global::YFex.Privacy.Classified.TryMask(in cnpj, destination, out written);
        return global::YFex.Privacy.Masks.TryWriteHidden(destination, out written);
    }
    // MaxMaskedLength = max of cases; TryWriteCanonical delegates the same way; TryFind tries Cpf then Cnpj.
}
```

**👁 Result**

```csharp
var cliente = CpfCnpj.Parse("123.456.789-09");
Span<char> buffer = stackalloc char[32];
Classified.TryMask(in cliente, buffer, out int n);       // "***.456.789-**"
Classified.Of(in cliente).Sensitivity;                    // PersonalIdentifier

var fornecedor = CpfCnpj.Parse("12.345.678/0001-95");
Classified.Of(in fornecedor).Sensitivity;                 // Internal (not personal) → shown and logged in clear
```

```
# PrivacySchema.verified.txt — YFex.Entity.Public (committed; a diff shows up in code review)
## Classified types
Cpf          PersonalIdentifier  identification  identifier                  mask "***.###.###-**"  detect: Cpf.TryFind
CpfCnpj      PersonalIdentifier  identification  identifier varies-by-value  Cpf → PersonalIdentifier · Cnpj → not personal
Email        Personal            contact         identifier                  mask {email}           detect: built-in e-mail
Telefone     Personal            contact         identifier                  mask {last:4}          detect: Telefone.TryFind
Rg           PersonalIdentifier  identification  identifier                  mask {last:2}          detect: —
FullName     Personal            identification                              mask {initials}        detect: —
PartyName    Personal            identification  varies-by-value             FullName → Personal · CompanyName → not personal
```
