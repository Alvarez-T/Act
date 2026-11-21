# 05 — Decision tables, runtime editing, parameters, dynamic rules

> **Names updated 2026-09-30 (D35–D38):** the engine is domain-agnostic — amounts are any `IAmount<T>`/number (not
> Money), adjustments are `Increase`/`Decrease`, publishing tiers are app-declared `[RuleTier]` fields (no built-in
> Tenant/Store), the effective-date field is `[RuleEffectiveDate]`. The sale examples below are just examples. See 13
> "Domain-agnostic refactor (D35–D38)" for the full rename map.

> Load for: **Phase 4** (sections 1–3 in 4a; 4–7 in 4b — runtime editing ships in the first release, D11)
> and **Phase 6** (section 8, Composer).
> Most discount/installment rules are **tables**, not arbitrary code. Tables are **typed** (D10):
> you declare a row type; rows are data that can come from code, the database, the UI grid editor,
> or files — all sources produce the same `TableVersion<TRow>`.

---

## 1. Declaring a typed table

```csharp
/// A decision table bound to a situation. The nested Row type IS the table's schema:
/// [When] columns are conditions over situation fields, [Then] columns are directive outputs.
/// The generator binds column names to SaleSituation.Schema at compile time (typos = error YFR0101).
[DecisionTable<SaleSituation>("sale.installments",
    HitPolicy.First,                                   // first matching row wins (row order matters, editor shows it)
    Group = nameof(SaleRules.Installments),            // competes in this group like a code rule
    EditableAt = new[] { "Tenant", "Store" },     // who may publish versions (D12)
    PublishPermission = "sales.rules.installments.publish",
    Guardrail = typeof(InstallmentGuardrails))]        // sealed limits every row must respect (§3.3)
public partial class InstallmentTable
{
    [Validatable]                                      // rows are validated with YFex.Validation (editor + publish)
    public readonly partial record struct Row(
        [property: When("Payment")] Match<PaymentMethod> Payment,       // Any | Is(x) | In(x,y) | Not(x)
        [property: When("Total")]   Band<Money> Total,                  // Any | From(a) | Below(b) | Between(a,b) — half-open [a,b)
        [property: When("Tier")]    Match<CustomerTier> Tier,
        [property: LimitMax("Installments"), Range(1, 48)] int MaxInstallments,
        [property: LimitMin("InstallmentValue")]      Money MinInstallment);

    /// Rows shipped with the code (Layer.Code). Tenants/stores publish their own versions on top.
    public static ReadOnlySpan<Row> DefaultRows =>
    [
        new(Is(PaymentMethod.CreditCard), From(Money.Of(1000)), Is(CustomerTier.Gold), 18, Money.Of(80)),
        new(Is(PaymentMethod.CreditCard), From(Money.Of(1000)), Any,                    12, Money.Of(80)),
        new(Is(PaymentMethod.CreditCard), Between(Money.Of(300), Money.Of(1000)), Any,   6, Money.Of(50)),
        new(Is(PaymentMethod.CreditCard), Below(Money.Of(300)), Any,                     3, Money.Of(50)),
        new(In(PaymentMethod.Pix, PaymentMethod.Boleto), Any, Any,                       1, Money.Zero),
    ];
}
```

> `ReadOnlySpan<Row>` of a non-primitive struct from a collection expression allocates once into a
> static array in current compilers; if it does not, use `static readonly Row[]`.

### 1.1 Cell types

```csharp
/// Equality-style condition cell. Any = wildcard. Stored non-boxing (enum → underlying long; OptionSet for In).
public readonly struct Match<T> { public static Match<T> Any { get; } public static Match<T> Is(T v);
    public static Match<T> In(params ReadOnlySpan<T> vs); public static Match<T> Not(T v); public bool Matches(T v); }

/// Range-style condition cell, half-open [From, To). Any/From/Below/Between.
public readonly struct Band<T> where T : IComparable<T> { public static Band<T> Any { get; }
    public static Band<T> From(T a); public static Band<T> Below(T b); public static Band<T> Between(T a, T b); public bool Contains(T v); }
```

Static-import helpers (`using static YFex.Rules.Cells;`) provide `Any`, `Is`, `In`, `Not`, `From`,
`Below`, `Between` for readable row literals.

### 1.2 `[When]` and output columns (renamed 2026-11-21, localization ⚑LZ12, ⚑LZ30)

- `[When]` binds the member's own name: a situation field, otherwise a field of the line type (the table becomes a
  **line table**, evaluated per line, Items stage). Name the field only when the member is called differently:
  `[When(nameof(SaleSituation.Tier))]`, or a line path `[When("Lines[].Category")]`. No match = YFR0101.
- Output columns use attributes named like the `DirectiveWriter` methods (they replace `[Then(DirectiveKind, "Field")]`;
  `DirectiveKind` stays the engine and Rules.Dynamic vocabulary):

| Attribute | Column type | Produces |
|---|---|---|
| `[LimitMax(field)]` / `[LimitMin(field)]` | field's type | `LimitDirective` (optionally `OnExceed`, `ApprovalPermission`); no argument = the member's name |
| `[Grant(field)]` | `OptionSet` | `GrantDirective` |
| `[Decrease]` / `[Increase]` | `Percentual` (percent of the base) or an amount (`Money`, `Mass`, `decimal`…) | `AdjustDirective` (total, or the line in line tables) |
| `[Deny(path)]` / `[Warn(path)]` | `string` (the row's message key) | Deny/Warn with the row's code at `path` (none = object level) |
| `[RequireApproval(path)]` | `string` permission | `ApprovalDirective` |

Every row implicitly has hidden metadata columns managed by the store/editor:
`RowId` (stable `Guid` — used in trace/audit: `RuleRef = table code + RowId + table version`),
`Priority` (only for `HitPolicy.Priority`), optional `EffectiveFrom/To`, `Note`, `Sealed`
(code rows can be marked sealed so editors cannot change/delete them).

### 1.3 Hit policies (DMN vocabulary)

| Policy | Meaning | Publish-time check |
|---|---|---|
| `First` | first matching row in table order | unreachable rows (fully shadowed) = warning |
| `Unique` | at most one row may match any input | **overlap = error** |
| `Priority` | highest `Priority` among matches | equal-priority overlap = error |
| `Collect` | all matches become directives; the group's `Resolve` combines them | — |
| `CollectMax` / `CollectMin` | numeric output max/min across matches | — |
| (any) `Complete = true` | every possible input must match a row | **gap = error** (e.g. no row for Total < 300 with Pix) |

## 2. What the generator emits for a table

```csharp
partial class InstallmentTable : IDecisionTable<SaleSituation, InstallmentTable.Row>
{
    /// Column metadata for editors and publish checks: name, header message key, kind, when/then,
    /// bound situation field id, enum options (from EnumCode), cell constraints (from Row attributes).
    public static TableSchema Schema { get; }

    /// Builds the evaluation index from rows (any source). Validates rows, applies guardrails.
    /// Index: per Match<enum> column a bitset per value; per Band column sorted boundaries → binary
    /// search gives a row mask; candidate rows = AND of column masks; First = lowest set bit.
    public static FrozenTable<Row> Freeze(ReadOnlySpan<Row> rows, in TableVersionInfo info);

    /// Publish-time analysis: overlaps, gaps (when Complete), unreachable rows, guardrail violations.
    /// Reports through ViolationWriter with paths "Rows[i].Column" so the editor highlights cells.
    public static bool Check(ReadOnlySpan<Row> rows, ref ViolationWriter w);

    /// Called by the engine inside the table's group: evaluates the frozen table against a situation
    /// and writes directives stamped with RuleRef(table code, RowId, version).
    public static void Evaluate(in FrozenTable<Row> table, in SaleSituation s, ref DirectiveWriter d);

    /// STJ source-generated serialization context for rows (store/JSON files/wire). No reflection.
    public static JsonTypeInfo<Row[]> RowsJson { get; }
}
```

## 3. Table sources

### 3.1 Contract

```csharp
/// One published (immutable) version of a table for one scope.
public sealed class TableVersion<TRow>
{
    public string TableCode { get; }             // "sale.installments"
    public long Version { get; }                  // monotonic per (table, scope)
    public RuleTierPath Scope { get; }                // Code | Tenant(id) | Store(tenant, store)
    public DateOnly EffectiveFrom { get; }
    public ImmutableArray<TRow> Rows { get; }
    public ImmutableArray<RowMeta> Meta { get; }  // RowId, Priority, Sealed, EffectiveFrom/To, Note (parallel to Rows)
    public string PublishedBy { get; }            // user id
    public DateTimeOffset PublishedAt { get; }
    public string? Note { get; }
}

/// Anything that can provide versions: the code defaults, the database store, a JSON file.
public interface ITableSource<TRow>
{
    ValueTask<ImmutableArray<TableVersion<TRow>>> LoadAsync(string tableCode, RuleTierPath scope, CancellationToken ct);
}
```

### 3.2 Sources shipped

| Source | Where | Notes |
|---|---|---|
| Code defaults | `DefaultRows` on the table | Layer.Code, version = code version |
| Store (DB) | `IRuleStore`-backed source (§4.3) | tenant/store versions; first release |
| UI editor | produces a draft `TRow[]` → publish command → store | first release |
| JSON file | `RowsJson` serializer | seeds, tests, import/export |
| CSV | optional import adapter later | **not** a primary source (D10) |

### 3.3 Guardrails

```csharp
/// Sealed checks that every row must satisfy — run in the editor (live), at publish (reject) and at
/// evaluation (clamp + trace), so a bad store can never exceed legal/business limits.
public interface IRowGuard<TRow>
{
    static abstract void Check(in TRow row, ref ViolationWriter w);
}

[Sealed, Rule("sale.installments.legal", Group = nameof(SaleRules.Installments))]
public readonly struct InstallmentGuardrails : IRule<SaleSituation>, IRowGuard<InstallmentTable.Row>
{
    public static bool Applies(in SaleSituation s) => true;
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d)
        => d.Limit(SaleSituation.Fields.InstallmentValue, min: Money.Of(5));         // also a normal sealed rule
    public static void Check(in InstallmentTable.Row r, ref ViolationWriter w)
    {
        if (r.MaxInstallments > 24)
            w.Add(InstallmentTable.Row.Fields.MaxInstallments, "sale.installments.above-legal", RawValue.From(24));
    }
}
```

## 4. Runtime editing lifecycle (first release)

```
Admin opens "Parcelamento"
  → grid built from InstallmentTable.Schema (columns, kinds, dropdowns)       [08 §7]
  → edits rows; each row validated live (Row is [Validatable]) + guardrails
  → "Publish": choose EffectiveFrom (today or future), scope (tenant / store), note
  → PublishTable command (needs PublishPermission; base version for optimistic concurrency)
     server: re-validate rows + guardrails + Check (overlaps/gaps/unreachable) → reject with cell paths
             → persist immutable version N+1 → audit record (who, when, diff vs N)
             → publish RulesetPublished event (YFex.Messaging)
  → server RuleSetHost.Publish(snapshot); clients receive the event (online now / on reconnect),
    fetch the version, cache it locally (last N versions per table/scope), RuleSetHost.Publish
  → next Evaluate with AsOf ≥ EffectiveFrom uses version N+1; decisions record RuleRef versions
Rollback = publish the old rows again as version N+2 (history is never rewritten).
```

### 4.0 Simulation before publishing ("what-if")
The editor can run a **draft** against recent historical decisions (stored situations, 06 §2) before
publishing: `RuleAdmin.Queries.SimulateTable(tableCode, scope, draftRows, sampleWindow)` re-evaluates
the stored situations with the draft and reports how many decisions change, total discount delta,
and examples (situation, old vs new explanation). Pure evaluation — no side effects. Same for
parameters and dynamic rules. Recommended before every tenant publish; mandatory for `Editable`
tables flagged `RequireSimulation = true`.

### 4.1 Commands & queries (module `YFex.Rules` CQRS contracts; handlers in the app's server)

```csharp
public static partial class RuleAdmin
{
    public static partial class Commands
    {
        /// Publishes a new immutable version. BaseVersion = the version the editor started from;
        /// if the current version differs → Conflict (someone published meanwhile).
        public sealed partial record PublishTable(string TableCode, RuleTierPath Scope, long BaseVersion,
            DateOnly EffectiveFrom, ReadOnlyMemory<byte> RowsJson, string? Note) : ICommand<long>;
        public sealed partial record PublishParameters(string RuleCode, RuleTierPath Scope, long BaseVersion, DateOnly EffectiveFrom, ReadOnlyMemory<byte> ValuesJson) : ICommand<long>;
        public sealed partial record SetRuleOverride(string RuleCode, RuleTierPath Scope, RuleOverrideAction Action, DateOnly EffectiveFrom) : ICommand<long>; // Disable | ReplaceWith(table/dynamic code) — forbidden for [Sealed]
    }
    public static partial class Queries
    {
        public sealed partial record GetTableVersions(string TableCode, RuleTierPath Scope) : IQuery<ImmutableArray<TableVersionInfo>>, ICacheable;
        public sealed partial record GetTableVersion(string TableCode, RuleTierPath Scope, long Version) : IQuery<TableVersionPayload>, ICacheable;
        /// "How was this rule when it was applied?" → definition of the exact RuleRef (06 §6).
        public sealed partial record GetRuleDefinition(RuleRef Rule) : IQuery<RuleDefinitionView>, ICacheable;
    }
}
```

Authorization: `PublishPermission` (and a matching view permission) from the table/parameter
declaration are enforced through the normal Cqrs `.RequirePermission(...)` with a permission
resolved **per table code** (generated permission map). Store-scope publishing additionally
requires the actor to belong to that store (Tenancy).

### 4.2 Concurrency & scheduling
- Optimistic: `BaseVersion` must equal the latest version for (table, scope).
- Future `EffectiveFrom` is allowed (schedule a Black Friday table); `RuleSetHost` switches by asOf.
- Two versions with the same `EffectiveFrom` → the higher version wins.

### 4.3 Storage abstraction

```csharp
/// Persistence for published rule data. Implementations: SQL (YFex.Data), local cache on clients
/// (YFex.Persistence / Messaging.Rpc storage) — choose per app; the engine only needs this contract.
public interface IRuleStore
{
    ValueTask<long> AppendTableVersionAsync(string tableCode, RuleTierPath scope, long baseVersion, TableVersionPayload payload, CancellationToken ct);
    ValueTask<ImmutableArray<TableVersionInfo>> GetTableVersionsAsync(string tableCode, RuleTierPath scope, CancellationToken ct);
    ValueTask<TableVersionPayload?> GetTableVersionAsync(string tableCode, RuleTierPath scope, long version, CancellationToken ct);
    // parameters, overrides and dynamic rules: same shape
}
```

### 4.4 Distribution to clients (offline-first)
- Event `RulesetPublished(RuleTierPath Scope, string Code, long Version, DateOnly EffectiveFrom)` over
  YFex.Messaging.
- Queries that expose decisions (`GetSaleOptions`, 09 §7) are invalidated by it (Live/Fusion).
- Clients keep the last N versions (configurable; ≥ the maximum offline window) so an offline POS
  evaluates the version that was in force at the sale's business date. If the POS missed a version
  (was offline when it was published), its decision may differ from the server's → fingerprint
  mismatch → conflict policy (09 §8, 11 §2.3).

## 5. Parameters (editable numbers of code rules)

`RuleParameter<T>` values follow the same lifecycle (publish → version → effective date → cache).
The editor is a generated form per rule (name, current value, guard as min/max, history). Guard is
enforced at publish and evaluation. Parameters are the preferred way to make code rules tenant-tunable
without turning them into tables.

## 6. Rule overrides (disable / replace non-sealed rules)

`SetRuleOverride(code, scope, Disable | ReplaceWith(tableOrDynamicCode))`, versioned and effective-dated.
`[Sealed]` rules reject overrides (publish error `rules.sealed`). Overrides are visible in the trace
(`Overridden(by layer)`).

## 7. Who can do what — summary

| Action | Permission (example) | Scope |
|---|---|---|
| Publish tenant table/parameters | `sales.rules.<table>.publish` | tenant |
| Publish store table | `sales.rules.<table>.store.publish` | store (actor must belong) |
| Override (disable/replace) rule | `sales.rules.override` | tenant |
| Approve a `RequireApproval` decision at the POS | the directive's permission, e.g. `sales.discount.override` | per decision (OverrideToken, 09 §6) |
| View rule history / definitions | `sales.rules.view` | tenant |

## 8. Runtime-authored rules — `YFex.Rules.Dynamic` (Composer, advanced tenants)

For rules that cannot be expressed as a table of an existing row type.

```csharp
/// Serializable rule definition (STJ source-gen). Versioned and published like tables.
public sealed record RuleSpec(
    string Code, string Group, int Priority, ConditionSpec When, ImmutableArray<DirectiveSpec> Then,
    ImmutableDictionary<string, string> Messages);               // culture → text, versioned with the rule (10 §4)

/// Condition AST — closed [Union] set.
[Union] public readonly struct ConditionSpec : IUnion
// Compare(field, Op, RawValue) | In(field, RawValue[]) | Between(field, RawValue, RawValue) | IsTrue(field)
// | And(ConditionSpec[]) | Or(ConditionSpec[]) | Not(ConditionSpec)

[Union] public readonly struct DirectiveSpec : IUnion
// mirrors Directive cases with literal values: Limit(field, min, max, exceed, permission) | Grant | Adjust(target, rate) | Deny(code) | Warn(code) | Approval(permission, code)
```

Execution: at publish/load, a spec is **compiled to a flat postfix instruction array**
(`LoadField id`, `Const k`, `CmpGe`, `And`, …) evaluated by a small interpreter over
`TSituation.Read(in s, id)`. No `Expression`, no reflection, AOT-safe. Roughly an order of magnitude
slower than code rules — fine for tens/hundreds of rules.

Publish-time validation: fields exist and are `Exposed`, kinds compatible (`RawValue` kind vs field
kind), group exists and accepts the directive kinds, guardrails respected, size limits (max nodes,
max rules per scope) to protect the engine.

Trace: dynamic rules get condition ids per AST node, so explanations work the same way (06).
