# 03 — Generator, analyzers & data inventory

`YFex.Privacy.Generator` (netstandard2.0, incremental) does four jobs: implements `IClassifiedData<T>` on classified types, emits
**personal-data shapes** for types that hold personal members, emits **purpose catalogs** (04 §3), and produces the **schema
snapshot and inventory** that answer "where do we store CPF?". The same assembly carries the YFP analyzers and code fixes.

It is referenced **as an analyzer only** by contract assemblies (no runtime reference, 02 §8). Libraries that also need the runtime
reference `YFex.Privacy` normally, and the analyzer comes with it.

## 1. Inputs

| Input | Provider | Notes |
|---|---|---|
| `[Sensitive]` on struct/class | `ForAttributeWithMetadataName("YFex.Privacy.SensitiveAttribute")` | must be `partial` (YFP0008) |
| `[DataSubjectKey]` on struct/class | `ForAttributeWithMetadataName` | must be `partial` |
| `[assembly: SensitiveType<T>]` | compilation assembly attributes | for types you do not own |
| `[PurposeCatalog]` static partial classes | `ForAttributeWithMetadataName` | 04 §3 |
| every non-generated record/class/struct with instance data members | `CreateSyntaxProvider` (type declarations with a property, field or primary-constructor parameter) | the model is a small `EquatableArray<MemberModel>`; the classification of each member type is resolved from symbols **in the transform**, so unrelated edits hit the cache |
| `.editorconfig`: `yfex_privacy.extra_personal_tokens`, `yfex_privacy.person_types`, `yfex_privacy.max_depth`, `yfex_privacy.schema_namespace` | `AnalyzerConfigOptionsProvider` | conventions are tunable per repo |
| MSBuild `<YFexPrivacyShapes>false</YFexPrivacyShapes>` | build property | opt-out of shape emission (the snapshot is still produced) |

Classification of member types defined in **referenced** assemblies is read from their `[Sensitive]`/`[DataSubjectKey]` attributes
(metadata), so it works even when the referenced assembly was compiled before this design existed, as long as the attribute is there.

## 2. Emitted code

### 2.1 Classified types

`IClassifiedData<T>` implementation: classification constant, mask code (02 §7), canonical form (02 §6), `Classify` for unions,
`TryFind` delegation when the type declares `static bool TryFindIn(ReadOnlySpan<char>, out int, out int)` (06 §4), `SubjectKind`
for `[DataSubjectKey]` types. Shown in 02 §8.

### 2.2 Personal-data shapes

For each type with at least one personal member (02 §5 rules 2–8), the generator implements `IPersonalDataShape<T>`. Only types
that **hold** personal data get code. `ItemVenda { Produto, Quantidade, Preco }` gets nothing.

```csharp
namespace YFex.Privacy;

public interface IPersonalDataShape<TSelf> where TSelf : IPersonalDataShape<TSelf>
{
    /// Personal members in declaration order (static table, generated).
    static abstract ReadOnlySpan<PersonalDataMember> PersonalMembers { get; }

    /// Read-only visit of every personal member value (nested shapes included). No boxing: TVisitor is a struct.
    static abstract void Accept<TVisitor>(in TSelf value, ref TVisitor visitor)
        where TVisitor : struct, IPersonalDataVisitor, allows ref struct;
}

public interface IPersonalDataVisitor
{
    void Value<T>(in PersonalDataMember member, in T value) where T : IClassifiedData<T>;
    void Text(in PersonalDataMember member, ReadOnlySpan<char> value);            // raw classified strings (never called for null)
    void Nested<T>(in PersonalDataMember member, in T value) where T : IPersonalDataShape<T>;   // visitor decides whether to recurse
    void BeginCollection(in PersonalDataMember member, int count);
    void EndCollection(in PersonalDataMember member);
}

public readonly struct PersonalDataMember(string name, ushort index, DataClassification classification,
                                          PersonalDataMemberSource source, bool isCollection)
{
    public string Name { get; } = name;
    public ushort Index { get; } = index;
    public DataClassification Classification { get; } = classification;
    public PersonalDataMemberSource Source { get; } = source;
    public bool IsCollection { get; } = isCollection;
}

public enum PersonalDataMemberSource : byte { Type, MemberAttribute, AssemblyDeclaration, SubjectKey, Nested, Convention, FreeText }
```

The visitor is **read-only on purpose**. Records use `init` accessors, and generated code cannot assign them after construction. The
operations that need to *change* data do it where the data is written instead: SQL updates (07), the two-part event payload (08 §4)
and redacting JSON serialization (08 §8.2). This keeps one generated method per type instead of three.

**✍ Dev writes** (Farma; nothing privacy-related)

```csharp
public static partial class Venda
{
    public partial record FinalizarCommand(
        VendaId Id,
        CustomerId? Cliente,          // [DataSubjectKey("customer")] on CustomerId
        CpfCnpj? CpfNaNota,           // classified type
        List<ItemVenda> Itens,        // not personal
        Money Total,
        string? Observacao) : ICommand<VendaFinalizada>;
}
```

**⚙ Generated**

```csharp
// <auto-generated/> YFex.Privacy.Generator
partial record FinalizarCommand : global::YFex.Privacy.IPersonalDataShape<FinalizarCommand>
{
    private static readonly global::YFex.Privacy.PersonalDataMember[] __personalMembers =
    [
        new("Cliente", 1, new(global::YFex.Privacy.DataSensitivity.Internal, global::YFex.Privacy.DataCategory.Identification,
                              global::YFex.Privacy.DataClassificationFlags.SubjectKey), global::YFex.Privacy.PersonalDataMemberSource.SubjectKey, false),
        new("CpfNaNota", 2, global::YFex.Privacy.Classified.Static<global::YFex.Entity.Public.CpfCnpj>(),
                              global::YFex.Privacy.PersonalDataMemberSource.Type, false),
        new("Observacao", 5, new(global::YFex.Privacy.DataSensitivity.Personal, global::YFex.Privacy.DataCategory.FreeText,
                              global::YFex.Privacy.DataClassificationFlags.FreeText | global::YFex.Privacy.DataClassificationFlags.ByConvention),
                              global::YFex.Privacy.PersonalDataMemberSource.FreeText, false),
    ];

    static global::System.ReadOnlySpan<global::YFex.Privacy.PersonalDataMember> global::YFex.Privacy.IPersonalDataShape<FinalizarCommand>.PersonalMembers
        => __personalMembers;

    static void global::YFex.Privacy.IPersonalDataShape<FinalizarCommand>.Accept<TVisitor>(in FinalizarCommand value, ref TVisitor visitor)
    {
        if (value.Cliente is { } cliente) visitor.Value(in __personalMembers[0], in cliente);
        if (value.CpfNaNota is { } cpfNaNota) visitor.Value(in __personalMembers[1], in cpfNaNota);
        if (value.Observacao is { } observacao) visitor.Text(in __personalMembers[2], observacao);
    }
}
```

(`Classified.Static<T>()` returns `T.Classification` through a constrained generic call; no boxing, inlined by the JIT/AOT compiler.)

### 2.3 Module initializer: the `Type` → members registry

```csharp
// <auto-generated/> one per assembly
file static class __YFexPrivacyRegistration
{
    [global::System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
    {
        global::YFex.Privacy.PersonalDataShapes.Register(typeof(global::YFexFarma.Venda.FinalizarCommand),
            global::YFex.Privacy.PersonalDataShapes.MembersOf<global::YFexFarma.Venda.FinalizarCommand>());
        // … one line per shaped type
    }
}
```

```csharp
namespace YFex.Privacy;

public static class PersonalDataShapes   // core
{
    public static void Register(Type type, ReadOnlySpan<PersonalDataMember> members);    // module initializers only
    public static bool TryGetMembers(Type type, out ReadOnlySpan<PersonalDataMember> members);
    public static ReadOnlySpan<PersonalDataMember> MembersOf<T>() where T : IPersonalDataShape<T> => T.PersonalMembers;
}
```

`typeof(X)` as a dictionary key is AOT-safe (no reflection over members). Object-typed consumers (STJ contract modifiers, Avalonia
bindings) use `TryGetMembers(jsonTypeInfo.Type, …)`.

### 2.4 Schema text and inventory data

Each assembly gets one public class (namespace from `yfex_privacy.schema_namespace`, default = assembly name):

```csharp
// <auto-generated/>
namespace YFex.Entity.Public;
public static class PrivacySchema
{
    public const string Text = "…";                                   // the PrivacySchema.verified.txt content
    public static global::System.ReadOnlySpan<byte> InventoryJson => "…"u8;    // privacy-inventory.json content (§5.2)
}
```

## 3. The snapshot (safety net instead of attributes)

```csharp
// YFex.Privacy.Testing; one test per assembly
[Fact] public void Privacy_schema_is_reviewed()
    => PrivacySchemaSnapshot.Verify(YFexFarma.PrivacySchema.Text, PrivacySchema.InventoryJson);
// writes/compares PrivacySchema.verified.txt and privacy-inventory.json next to the test project; a diff fails the test
```

```
# PrivacySchema.verified.txt — YFexFarma.Entity.Public
## Types holding personal data
Venda.FinalizarCommand                        cqrs.command
  Cliente      CustomerId?   Internal           subject key (customer)
  CpfNaNota    CpfCnpj?      PersonalIdentifier type, varies-by-value
  Observacao   string?       Personal           free text (scrubbed)
Customer.CreateCustomerCommand                cqrs.command
  Name         string        Personal           convention(name in Customer)   YFP0001 → FullName
  Email        string        Personal           convention(email)              YFP0001 → Email
## Not personal (by declaration)
ReportFilter.Name            not personal: "report name, not a person"
```

Adding `string Senha` to a command changes this file, so the reviewer sees `Senha string Secret convention(senha) YFP0002` in the
pull request. Nobody has to remember an attribute.

## 4. Analyzers (⚑P22)

### 4.1 Rules

| Id | Severity | Trigger | Code fix |
|---|---|---|---|
| YFP0001 | Warning | raw string member classified by convention, and a classified value object for that convention exists in a referenced assembly (`Cpf`, `CpfCnpj`, `Email`, `Telefone`, `FullName`, `Rg`) | change the member type (+ `using`), add `.Parse` at obvious construction sites |
| YFP0002 | Info (Warning in records) | raw secret-like string member (`senha`, `token`, `apikey`…) | suggest a `Secret` value object (`Password`, `ApiKey`) or `[Sensitive(DataSensitivity.Secret)]` |
| YFP0003 | Warning | raw financial string (`cardNumber`, `contaBancaria`, `chavePix`) | suggest tokenization (05 §4) |
| YFP0004 | Warning | raw sensitive-personal string by name (`diagnostico`, `prescricao`, `alergia`…) | declare a value object with a special category |
| YFP0005 | Warning | `Secret`/`Financial` member in a **record** or class with compiler-generated `ToString` (`BearerTokenCredential(string Token)` prints the token) | generate a `PrintMembers` override that writes `Token = ****` |
| YFP0006 | Warning | classified value (≥ `Personal`) or convention-personal string **interpolated or concatenated** into: exception constructors, `Console.Write*`, `Debug.*`, `ILogger.Log*`, `string.Format` stored into a free-text member | use the structured value (logging captures it typed and redacted) |
| YFP0007 | Warning | `SHA1/SHA256/SHA512/MD5.HashData` (or `ComputeHash`) over bytes derived from a classified value or a convention-personal member/variable (`ip`, `mac`, `cpf`, `email`, `machineGuid`) | replace with `Pseudonymizer.Pseudonymize(...)` (05 §3) |
| YFP0008 | Error | `[Sensitive]`/`[DataSubjectKey]` type is not `partial` | add `partial` |
| YFP0009 | Error | invalid mask (unknown preset, empty pattern, `{last:0}`) | — |
| YFP0010 | Warning | `Secret` value object whose `ToString()` is not overridden to a redacted form (pattern: `AccessToken` → `"Bearer ****"`) | generate the override |
| YFP0011 | Info | type holds personal members but is not `partial` → no runtime shape (still in the snapshot) | add `partial` |
| YFP0012 | Error | `SensitivePersonal` without a special category | — |
| YFP0013 | Error | declared sensitivity below the category minimum (`Personal` + `health`) | raise the sensitivity |
| YFP0014 | Warning | `[Sensitive]` on a member whose type is already classified differently | remove the member attribute |
| YFP0015 | Error | canonical form not derivable (type is neither `IRawValue`, `IUtf8SpanFormattable` nor `ISpanFormattable`, and has no `TryWriteCanonical`) | — |
| YFP0016 | Info | cycle in nested personal shapes (cut at the second visit) | — |
| YFP0020–0024 | Error | purposes: 0020 malformed id, 0021 duplicate id, 0022 **invalid legal basis for sensitive categories** (art. 11 has no contract or legitimate-interest basis), 0023 unknown purpose referenced in `.Privacy(...)`, 0024 ambiguous data-subject member in a message (add `p.Subject(c => c.X)`) | — |
| YFP0030 | Info | `[Persist]` view-model property (device snapshot) of `PersonalIdentifier` or higher | reminder: the device store must be encrypted (runtime check in 08 §2) |

### 4.2 Heuristics that must not cry wolf

- Token matching, not substrings (02 §5.2). `multipleMonitors`, `shipping`, `description` never match `ip`.
- Bare `Name`/`Nome` only inside person-like types (configurable list `yfex_privacy.person_types`).
- Generated code, test projects (`IsTestProject=true`) and `[NotPersonalData]` members are skipped.
- Every convention decision is visible in the snapshot. A false positive costs one `[NotPersonalData(Reason = "…")]`. A false
  negative costs a review comment on the snapshot diff.

**✍ Before** (Farma today) → **after the code fix**

```csharp
// before                                                    // after (YFP0001 fix)
public partial class CustomerModel                           public partial class CustomerModel
{                                                            {
    public string CpfCnpj { get; set; }    // ⚠ YFP0001          public CpfCnpj? CpfCnpj { get; set; }
    public string Phone { get; set; }      // ⚠ YFP0001          public Telefone? Phone { get; set; }
    public string Email { get; set; }      // ⚠ YFP0001          public Email? Email { get; set; }
    public string Address { get; set; }    // ℹ convention        public string Address { get; set; }   // Personal by convention, no VO yet
}                                                            }
```

## 5. Data inventory (⚑P23)

### 5.1 What is recorded per type

| Field | Source |
|---|---|
| roles | known interfaces/attributes, resolved by FQN in the shared emit code: `cqrs.command/query/event/result` (`ICommand`, `IQuery<T>`, `IEvent`, result types of queries), `entity` (YFex.Data entity metadata, EF `IEntityTypeConfiguration<T>` target), `event-sourced.event` (event union cases / `[Emits]`), `view-model` (`StateObject` subclass), `device-snapshot` (`[Persist]` members), `outbox` (`IQueueable`), `rpc` (generated contracts), `dto` (anything else reachable from the above) |
| store | table/column names when the YFex.Data generator knows them (08 §3); event stream type for ES; snapshot discriminator for Persistence |
| members | name, path (`Itens[].Paciente.Cpf`), CLR type, sensitivity, category, flags, source (type / convention / attribute) |
| purposes | from `.Privacy(p => p.Purpose(...))` in aggregate configurations (04 §10), when present |

### 5.2 `privacy-inventory.json` (committed per assembly, aggregated by the CLI)

```json
{
  "assembly": "YFexFarma.Entity.Public",
  "generatedBy": "YFex.Privacy.Generator 1.0",
  "types": [
    {
      "type": "YFexFarma.Venda+FinalizarCommand",
      "roles": ["cqrs.command", "outbox"],
      "purposes": ["sale", "fiscal.document"],
      "members": [
        { "path": "Cliente",   "clrType": "CustomerId?", "sensitivity": "Internal", "category": "identification", "flags": ["subject-key"], "subjectKind": "customer", "source": "subject-key" },
        { "path": "CpfNaNota", "clrType": "CpfCnpj?", "sensitivity": "PersonalIdentifier", "category": "identification", "flags": ["identifier", "varies-by-value"], "source": "type" },
        { "path": "Observacao","clrType": "string?",  "sensitivity": "Personal", "category": "free-text", "flags": ["free-text", "by-convention"], "source": "free-text" }
      ]
    },
    {
      "type": "YFexFarma.Data.VendaEntity",
      "roles": ["entity"],
      "store": { "kind": "sql", "table": "venda" },
      "members": [
        { "path": "CpfNaNota", "column": "cpf_na_nota", "indexColumn": null, "encrypted": false, "sensitivity": "PersonalIdentifier", "category": "identification", "source": "type" }
      ]
    }
  ]
}
```

### 5.3 Databases we do not model in C#

Legacy ERPs being migrated (YFex.DataMining) and external databases have no C# types. DataMining already tags columns with
semantic types. YFex.Privacy maps them to classifications so the same inventory covers them:

| DataMining semantic | Classification |
|---|---|
| `BrCpf`, `BrRg` | `PersonalIdentifier` / identification |
| `BrCnpjOrCpf` | `PersonalIdentifier` (conservative) |
| `BrCnpj` | `Public` |
| `Email`, `Phone`, `BrCep` | `Personal` / contact |
| `PersonName` (to add) | `Personal` / identification |
| `Money`, `PrimaryKey`, `BrNcmCode`, `BrCfopCode`, `ProductSku` | not personal |

The mapping is a table in `YFex.Privacy` (`SemanticClassificationMap`), extensible by YAML in the DataMining project config.
`yfex-privacy inventory --blueprint <file>` adds the blueprint's tagged columns to the report.

### 5.4 The CLI: "where do we store CPF?"

`tools/YFex.Privacy.Cli` (`yfex-privacy`) reads the committed `privacy-inventory.json` files under a root folder. It loads no
assemblies, so it runs in CI and on any machine.

```
> yfex-privacy where Cpf --root C:\Dev\YFex

Cpf (PersonalIdentifier · identification) — held by 14 types in 6 assemblies

STORED (at rest)
  sql      venda.cpf_na_nota                 YFexFarma.Data.VendaEntity            clear (encryption opt-in, not enabled)
  sql      cliente.cpf_cnpj                  YFexFarma.Data.ClienteEntity          encrypted ✓  blind index cpf_cnpj_index ✓
  sql      brain.entity.cpf_cnpj             YFex.Brain.Entity.Entity              clear   ⚠ master data: consider encryption
  events   VendaFinalizada.CpfNaNota         stream "venda"                        sealed (subject key) ✓
  device   CaixaViewModel.Cliente            [Persist] snapshot "caixa"            store encrypted ✓
  outbox   Venda.FinalizarCommand.CpfNaNota  IQueueable (offline queue)            store encrypted ✓
IN TRANSIT
  rpc      Venda.FinalizarCommand, Cliente.Queries.GetByDocumento
  ai       Cliente.Queries.Search (capability)                             placeholder in prompts ✓
DISPLAYED
  ui       ClienteDetailView (masked, reveal: privacy.reveal.identification), CaixaView
LOGGED
  logs     pseudonym #h: (domain "log"); audit: sealed with subject key
CONVENTION ONLY (review)
  YFexFarma.Desktop.Presentation.CustomerModel.CpfCnpj (string)    YFP0001
```

Other commands: `yfex-privacy inventory --format md|json|csv`, `yfex-privacy ropa --config privacy-ropa.yaml` (§5.5),
`yfex-privacy verify` (fails when a snapshot is stale or an assembly lacks one).

### 5.5 ROPA — record of processing operations (LGPD art. 37)

The ROPA joins the inventory (what data, where) with the purpose catalogs (why, legal basis, 04), retention rules (how long, 07 §7)
and the declared sharing (who receives it). Sharing is declared once per app in the privacy configuration:

```csharp
p.SharedWith("sefaz", "Secretaria da Fazenda (NF-e/NFC-e)", LegalBasis.LegalObligation, DataCategory.Identification)
 .SharedWith("payment-provider", "Gateway de pagamento", LegalBasis.ContractPerformance, DataCategory.Financial, DataCategory.Contact)
 .SharedWith("anthropic", "LLM provider (assistant)", LegalBasis.LegitimateInterest, internationalTransfer: "US")   // only placeholders leave (08 §8)
```

**👁 Result** (`yfex-privacy ropa`, excerpt, Markdown)

| Purpose | Basis | Subjects | Categories | Stores | Retention | Shared with | Transfer | Safeguards |
|---|---|---|---|---|---|---|---|---|
| fiscal.document | Legal obligation (art. 7 II) | customer | identification | sql venda, events venda | 5 years from the 1st of January after issue (legal hold) | sefaz | — | blind index, sealed events, audit |
| marketing.loyalty | Consent (art. 7 I) | customer | contact, behavioral | sql fidelidade | until withdrawal + 30 days | — | — | consent ledger, pseudonymized analytics |
| health.medication-history | Consent, specific and highlighted (art. 11 I) | customer | health | sql historico_medicamento | 2 years after last purchase | — | — | encrypted at rest (subject key), reveal audited |

## 6. Shared emit code for other generators

`YFex.Privacy/src/Shared/PrivacyEmit.cs` is **linked** into each generator that touches data (Logging, Validation, State, Cqrs,
Data, EventSourcing), the same strategy as logging's `DiagnosticsEmit.cs` and the per-generator `EquatableArray<T>`. It exposes:

```csharp
// (sketch) netstandard2.0, compile-time only
internal static class PrivacyEmit
{
    public static MemberPrivacyModel Resolve(ISymbol member, ITypeSymbol memberType, PrivacyConventions conventions);   // 02 §5 rules
    public static string ClassificationExpression(in MemberPrivacyModel model);   // "global::YFex.Privacy.DataClassification" constructor text
}

internal readonly record struct MemberPrivacyModel(          // equatable → safe in incremental pipelines
    byte Sensitivity, string CategoryKey, byte Flags, byte Source, string? ClassifiedTypeName, string? ConventionToken);
```

Consumers: logging capture (redaction per member), Validation `MemberShape` (`x-sensitivity`, 08 §5), State setter hooks and UI
masking hints (08 §1), Data blind indexes (08 §3), EventSourcing sealing (08 §4), Cqrs `.Privacy(...)` parsing (04 §10).
