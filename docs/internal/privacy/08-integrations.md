# 08 — Integration with every library

Rule (same as validation and logging): **thin integrations live inside the owning library**, reference YFex core for declarations and
`YFex.Privacy` for primitives, and never reference `YFex.Privacy.Rights`. Generators use the linked `PrivacyEmit.cs` (03 §6).

| # | Library | What it gets | Effort |
|---|---|---|---|
| 1 | YFex.UI (Avalonia, Blazor), YFex.State | masking presenter, reveal by permission (audited), consent prompt + preference center, `[Persist]` checks | M |
| 2 | YFex.Persistence (+ Encryption) | envelope v2 under `IValueProtector`, mandatory encryption when snapshots hold personal data, `SubjectErased` purge | S |
| 3 | YFex.Data (+ Generator) | per-entity privacy metadata, blind-index + encrypted columns, locate/export/erase SQL, retention SQL | L |
| 4 | YFex.EventSourcing (+ Generator) | two-part event payload sealed with subject keys, shredded reads, stream subject tags | M |
| 5 | YFex.Cqrs / YFex.Validation | `.Privacy(...)` config, `PrivacyStage`, `Error.ConsentRequired`, server-side omission (⚑P17), `x-sensitivity` in JSON Schema | M |
| 6 | YFex.Messaging.Rpc | consent snapshot compute method, `SubjectErased`/`KeyDestroyed` signals, outbox encryption check, privacy metadata on results | S |
| 7 | YFex.Logging | redaction via `Redactor`, scrubber, pseudonyms, audit sealing with subject keys, consent session join | S (design already assumes it) |
| 8 | AI: YFex.Automation, YFex.Capabilities, YFex.Brain, YFex.DataMining | `PseudonymizationSession`, redacting `IAiAgent` decorator, tool I/O redaction, shapes not samples | M |
| 9 | YFex.Tenancy | key scope / policy scope from `IScopeContext` | XS |
| 10 | YFex.Authorization | `PrivacyPermissions` catalog (reveal, detokenize, requests, holds), consent ≠ permission | XS |
| 11 | YFex.Search | query classification, no CPF in cache keys, erasure of documents/frecency | S |
| 12 | YFex.Profiling, YFex.System, YFex.Security | purposes instead of the ladder, `PropertyRedactor`, pseudonyms, watchers gated by purpose | M |

## 1. UI (YFex.UI Avalonia/Blazor, YFex.State)

**Display masking without per-property work.** The State generator knows each `[Observable]` property's type and, through
`PrivacyEmit`, its classification. It emits a static table `PrivacyHints` per view model (property → classification). Read-only
presenters (grids, labels, detail views) in YFex.Avalonia use a `PrivacyPresenter` that:

1. gets the bound value's classification: `IClassifiedData.Classify()` for value objects (no registry), `PersonalDataShapes`/`PrivacyHints`
   for convention-classified strings;
2. asks the `RevealPolicy` (04 §11) whether the current principal may see that sensitivity/category in clear on this screen;
3. renders the clear value or the mask, and shows a reveal button (eye icon) when a reveal permission exists.

Default `RevealPolicy`: `Public`/`Internal`/`Personal` in clear for anyone who can open the screen (`privacy.reveal.personal` only
matters when a tenant tightens `Personal`), `PersonalIdentifier` needs `privacy.reveal.identifier`, `SensitivePersonal` needs
`privacy.reveal.sensitive` (+ justification), `Financial` is always `****1234`, `Secret` is never shown.

**Reveal is audited**: `Privacy.Revealed(subject index, member, screen, principal)` goes to the Audit channel. The policy can require a
justification for `SensitivePersonal` ("atendimento farmacêutico"). A revealed value re-masks after a timeout or on navigation.

**Editing:** input controls show the value in clear while editing (the user is typing it). A masked value is not editable unless revealed.

**✍ Dev writes**: nothing new. Existing XAML: `<TextBlock Text="{Binding Cliente.CpfCnpj}"/>` becomes masked through the presenter's
default style. Opt-out for a screen that legitimately shows clear values: `privacy:Reveal.Mode="Always"` (still audited).

Other UI pieces (phase 7): **consent prompt** (notice text, per-purpose toggles, highlighted block for sensitive purposes, guardian
flow), **preference center** (the subject's purposes and history), **operator consent flow** at the POS (PIN-pad/OTP confirmation,
04 §7), **privacy portal** in Blazor (requests, downloads, preferences), **request desk** for operators (verification, plan dry run,
manual tasks). Window-level protection for sensitive screens (`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` on Windows) is a
suggestion (10 §3).

**State.History:** undo stacks of `UserInput` properties hold personal values in memory. `YFex.State.History.Persistence` snapshots are
device data: same rule as §2. Logging already records only pseudonyms in the edit timeline (logging 12 §6).

## 2. YFex.Persistence (+ Encryption)

- `IValueProtector` stays the store-level seam. Its implementation becomes envelope v2 with a `DeviceStorage` key (05 §6.3).
- **Mandatory encryption check:** at startup, `PersistenceService` asks each registered snapshot provider for the classification of its
  persisted members (generated by the Persistence generator from `PrivacyEmit`). If any is `PersonalIdentifier` or higher and no
  protector is registered, startup fails in Production with a clear message, and logs a warning in Development. (YFP0030 is the
  compile-time hint.)
- **`SubjectErased` signal** (from the server via Rpc, §6): providers that hold subject-tagged entries purge them. Snapshots are
  per-screen state, so the default is to clear snapshots that contain the subject's identifier index.
- Offline durable queues (`IDurableQueue<T>`) holding commands with personal data are covered by the encrypted store, and the queue
  serializer records the consent entry id instead of a rebuilt state (04 §7).

## 3. YFex.Data (+ Generator): the biggest integration

The YFex.Data generator already processes entities (`[RowLevelSecurity]`). It adds, for every entity type with personal members
(rules of 02 §5, via `PrivacyEmit`), a generated **`EntityPrivacy` descriptor** registered in a catalog (same pattern as
`RowSecurityCatalog`):

```csharp
// (sketch) generated per entity
internal static class VendaEntityPrivacy
{
    public static readonly global::YFex.Data.Privacy.EntityPrivacyDescriptor Descriptor = new(
        table: "venda",
        subjectKeyColumns: ["cliente_id"],
        members:
        [
            new("CpfNaNota", column: "cpf_na_nota", indexColumn: "cpf_na_nota_index",
                classification: /* PersonalIdentifier, identification, identifier */, storage: ColumnStorage.Clear),
        ],
        retentionColumns: ["data_emissao"]);
}
```

**Encryption at rest (⚑P15)** is opt-in per entity in the Data configuration (configuration over attributes, as in Cqrs):

```csharp
d.Entity<ClienteEntity>().Privacy(p => p.Encrypt(c => c.CpfCnpj).Encrypt(c => c.Email));   // blind indexes come automatically
```

For `SensitivePersonal` and `Financial` members, encryption is the **default** and opting out needs `p.StoreInClear(c => c.X,
reason: "…")` (shown in the inventory). The generated reader/writer seals and opens values with the envelope (05 §6). AAD = tenant +
table + column. The key is the subject key when the row has a subject key column, otherwise a field key for the category.
`WHERE cpf_cnpj = @value` written through the QueryBuilder against an encrypted column is rewritten to the blind-index column
(equality only). `LIKE` against an encrypted column fails at build time when the generator sees it, and at run time otherwise.

**Schema migration** for an existing table: add `<column>_index` (binary 16), backfill with a batch job (read, pseudonymize, write),
then optionally switch storage to encrypted (`<column>` becomes varbinary; a rolling re-encryption job). The job is idempotent and
resumable. Rows carry the envelope version, so mixed states are readable.

**Row-level security** and privacy are complementary. RLS decides which rows a tenant/user sees. Privacy decides how personal
columns inside those rows are stored and shown.

## 4. YFex.EventSourcing (+ Generator) (⚑P16)

Events are immutable and replicated to devices, so erasure is crypto-shredding.

- The ES generator, using shapes, splits each event's personal members into a **sealed part**. `EventRecord` gains
  `Sealed` (envelope v2 of the MemoryPack-serialized personal members) and `SubjectIndex` (blind index of the subject). The public
  `Payload` carries the event with those members at `default`.
- On read, the generator-emitted merge opens `Sealed` with the subject key and rebuilds the event (`with { Cliente = …, CpfNaNota = … }`,
  events are records). If the key is destroyed, members stay `default` and `CommittedEvent.Flags` gets `Shredded`. Projections
  written against personal members must handle `default` (YFE0xx analyzer hint in the ES generator).
- Stream metadata records the subject(s) of the stream, so the rights participant locates streams without scanning payloads.
- Offline devices seal with the tenant **intake key** (05 §5.5). The server re-seals on ingest.
- `StreamRebaser`/`ReplaceAsync` (client rebase) preserve sealed parts byte for byte.

**Alternative (not recommended):** a separate personal-data store keyed by `(stream, event id, member)`. It works, but every read
joins two stores and every device needs both, which hurts offline sync.

## 5. YFex.Cqrs and YFex.Validation

- `.Privacy(p => …)` in aggregate configurations (04 §10): `DefaultPurpose`, `Purpose`, `RequireConsent`, `Subject(c => c.X)`.
  Parsed syntactically by the Cqrs generator (like `.Validation`, D27). Emits a `PrivacyDescriptor` per message.
- `PrivacyStage` in the server pipeline (after authorization, before rules), plus purpose tagging of the operation for logging
  (`privacy.purpose` attribute) and the ROPA.
- **Server-side omission (⚑P17)** for query results: when the caller lacks the reveal permission for a member's sensitivity/category,
  the server replaces the value with `default` and adds an entry to the result's **privacy metadata**: `(path, masked display text,
  reason)`. The client UI shows the masked text from the metadata. Typed value objects cannot hold `***.456.789-**`, and sending the
  full value to be masked on the client is not a control. The metadata travels in the wire v2 result envelope (validation D26 typed
  results) as an optional section, so old clients ignore it.
- `Error.ConsentRequired(purpose, status)` is a typed error (MemoryPack, wire v2).
- **Validation JSON Schemas:** `MemberShape` gains `DataClassification Classification` (filled by the Validation generator through
  `PrivacyEmit`). `JsonSchemaWriter` writes:

```json
"CpfNaNota": { "type": ["string", "null"], "x-type": "cpf-cnpj", "x-sensitivity": "personal-identifier",
               "x-category": "identification", "x-mask": "***.###.###-**" }
```

  AI tool hosts and form builders use `x-sensitivity` to decide what may be sent to a model and how to render the field.

## 6. YFex.Messaging.Rpc

- `IConsentQueries.GetSnapshot(subject)` is a Fusion compute method on the server, invalidated on ledger change (04 §6). Clients cache
  snapshots like other `ICacheable` queries, scoped by tenant.
- **Signals:** `PrivacySignals.SubjectErased(tenant, identifier indexes)` and `KeyDestroyed(tenant, key id)` ride the same channel as
  `CallContextSignals.PermissionsChanged`. Devices acknowledge, and the request report counts acknowledgements (07 §4.2).
- `EncryptionMode.AesGcm` for the client outbox/cache becomes the **default when any queued message type holds `PersonalIdentifier`
  or higher** (the generator knows: `IQueueable` + shape). Startup warns when it is explicitly `None`.
- The wire itself is TLS. No field-level encryption in transit (it would add cost without a threat it solves). Sealed ES parts travel
  sealed anyway.

## 7. YFex.Logging (consumer; amends the logging design)

| Logging design | Becomes |
|---|---|
| `DataSensitivity` / `[Sensitive]` in "YFex core `YFex.Privacy`" (02 §5.2) | as proposed, with 7 values (⚑P3) and the full contract of 02 here |
| `ILogValue<T>.Sensitivity` (02 §5.1) | removed; the logging generator reads `IClassifiedData<T>` |
| redaction policies `Keep/Mask/Hash/Encrypt/Erase` (07 §3.2) | `RedactionPolicy` row `DiagnosticLog`/`AuditLog` (05 §1), `Hash` → `Pseudonymize` (domain `log`) |
| scrubber "PrivacyScrubber patterns, moved/shared" (07 §3.3) | `TextScrubber` (06). Those patterns did not exist; they are specified now |
| crypto-shredding key vault (07 §4) | `IKeyVault` subject keys (05 §5) |
| consent join by `session.id` (07 §6) | purpose `telemetry.behaviour` in the session's `PurposeSet` (04 §9) |
| name heuristics YFL0031/YFL0032 | YFP0001/YFP0002 (one implementation; logging keeps its IDs as aliases or drops them) |

Ordering detail: the consumer redacts **before** a record enters the flight-recorder ring, so memory dumps of the ring hold
pseudonyms. Producer slots hold clear canonical bytes only for microseconds.

## 8. AI: Automation, Capabilities, Brain, DataMining (⚑P19)

### 8.1 `PseudonymizationSession`

```csharp
namespace YFex.Privacy;

/// Per conversation. Never logged, never persisted (or persisted sealed with a field key when a conversation must resume).
public sealed class PseudonymizationSession : IDisposable
{
    public PseudonymizationSession(AiDataPolicy policy, Pseudonymizer pseudonymizer);

    /// Replaces a classified value by a stable placeholder within the session: same CPF → same [[CPF_1]].
    public bool TryWritePlaceholder<T>(in T value, Span<char> destination, out int written) where T : IClassifiedData<T>;

    /// Restores placeholders in model output (for display to an authorised user) or in tool-call arguments (before execution).
    public bool TryRestore(ReadOnlySpan<char> text, IBufferWriter<char> output, RestoreTarget target);   // Display | ToolArguments

    public void Dispose();   // clears the map (values zeroed where they are byte buffers)
}

public sealed record AiDataPolicy(string Provider, bool IsLocal, string? Region,
                                  DataSensitivity MaximumInClear,        // cloud default: Internal; local default: Personal
                                  DataSensitivity MaximumAsPlaceholder); // above it: [[REDACTED]], value never representable
```

Placeholders are typed by label and index (`[[CPF_1]]`, `[[EMAIL_2]]`, `[[PESSOA_1]]`), so the model can reason ("send the receipt to
[[EMAIL_2]]") without ever seeing the value. The tool runtime restores `[[EMAIL_2]]` before invoking the capability, under the
caller's authorization. A placeholder that is not in the session is rejected (the model cannot invent references).

### 8.2 Where it plugs in

| Library | Integration |
|---|---|
| **YFex.Automation** (`IAiAgent`) | `RedactingAiAgent` decorator registered by `AddAiApiProviders(o => o.Privacy(...))`: prompt and message contents go through `TextScrubber` with the session context; responses are restored for display only when the caller asks. Policy per provider: `anthropic`/`glm`/`google` are **cloud** (international transfer, art. 33: default nothing above `Internal` in clear); `claude-desktop`/reversed agents follow the provider's location. `CallAiStep` gets the session from the run context |
| **YFex.Capabilities** | tool **parameters**: `ParametersSchema` already carries `x-sensitivity` (§5); arguments are restored from placeholders before `CanInvoke`/invoke. Tool **results**: serialized with **redacting JSON** — options-level converters for classified value types (`JsonSerializerOptions.Converters` take precedence over a type's `[JsonConverter]`) + a `JsonTypeInfo` modifier for convention-classified strings using `PersonalDataShapes` (AOT-safe on source-generated contexts). Free-text members go through the scrubber |
| **YFex.Brain** | master data is the richest personal store: `RawPayload` sealed with the subject key and retention-limited; `CpfCnpj` blind index; `RiskScore` decisions keep their inputs and explanation (art. 20 review, 07 §1). Any AI enrichment of entities uses the session |
| **YFex.DataMining** | `AiSemanticWorm` sends **value shapes**, not samples: `ddd.ddd.ddd-dd ×37, 11 digits ×5, null ×2` (shape alphabet `d` digit, `a` letter, punctuation kept, runs collapsed). Classification by shapes is as good as by values for format-driven semantics (CPF, CEP, e-mail domain shape), and no customer value leaves. Samples remain an explicit opt-in per project for local models only |

**👁 Result** (prompt as the cloud model sees it)

```
User: O cliente [[PESSOA_1]] (CPF [[CPF_1]]) pediu a 2ª via do cupom de ontem. E-mail dele: [[EMAIL_1]].
Tool result (Vendas.Queries.Buscar): [{ "id": "V-88123", "cliente": "[[CPF_1]]", "total": 152.40, "itens": 3 }]
Assistant → tool call Vendas.Commands.ReenviarCupom { "venda": "V-88123", "email": "[[EMAIL_1]]" }   ← restored before execution
```

## 9. YFex.Tenancy

`KeyScope` and `PrivacyPolicy` resolve from `IScopeContext` (top rung = controller). Single-tenant desktop apps use an app-level scope.
Impersonation (`ITenantImpersonationAuditor`) of a tenant by support staff does **not** grant reveal permissions automatically. Reveals
during impersonation are audited with the real actor.

## 10. YFex.Authorization

A generated permission catalog in YFex.Privacy:

```csharp
[PermissionCatalog]
public static partial class PrivacyPermissions
{
    [PermissionMeta(DisplayName = "Ver dados pessoais em claro")]                 public const string RevealPersonal = "privacy.reveal.personal";
    [PermissionMeta(DisplayName = "Ver documentos (CPF/RG) em claro")]            public const string RevealIdentifier = "privacy.reveal.identifier";
    [PermissionMeta(DisplayName = "Ver dados sensíveis (saúde…) em claro")]       public const string RevealSensitive = "privacy.reveal.sensitive";
    [PermissionMeta(DisplayName = "Resolver tokens")]                              public const string Detokenize = "privacy.detokenize";
    [PermissionMeta(DisplayName = "Atender solicitações de titulares")]           public const string HandleRequests = "privacy.requests.handle";
    [PermissionMeta(DisplayName = "Gerenciar retenção legal")]                     public const string ManageLegalHolds = "privacy.legal-hold.manage";
    [PermissionMeta(DisplayName = "Publicar avisos de privacidade")]              public const string PublishNotices = "privacy.notices.publish";
}
```

Consent is **not** a permission (04 §10): `RequirePermission` checks the caller, `RequireConsent` checks the subject.

## 11. YFex.Search

- `QueryAnalyzer` detects CPF/e-mail/phone in queries (existing `FormatKind`). Such queries are executed against **blind indexes**
  (exact match) and are **not** used as cache keys in clear. The cache key uses the pseudonym.
- Frecency (`IFrecencyStore`) entries keyed by entity keys of a subject are purged on `SubjectErased`.
- The shared `TryFindIn` methods of the value objects replace duplicated detection logic (06 §4).

## 12. YFex.Profiling, YFex.System, YFex.Security

Covered in 04 §8 (purposes, presets, ledger), 06 §6 and §8 (property redaction, scrubber migration) and 05 §3 (pseudonyms). Specific
points:

- `WindowsSystemInfoProvider` hashes MACs with plain SHA-256 → `Pseudonymizer` (domain `telemetry`). `MaskIpAddress` copy deleted.
- `AddYFexSecurityProfiling` subscribes watchers **only** when their purpose is allowed, and unsubscribes on withdrawal. Traffic
  interception additionally requires a tenant policy flag (an employer decision, 04 §8).
- Docs `YFex/docs/public/system.md` and `YFex.System/docs/01-consent-and-privacy.md` are rewritten for purposes, with stale namespaces fixed.
