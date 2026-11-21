# 14 — Explanations a UI can show ("why is this field like this?")

> **Status: proposal, 2026-11-19.** Decisions **⚑RX1–⚑RX33** are pending; nothing is built. Asked for by the YFex.UI "why"
> component plan ([`YFex.UI/docs/plans/why-component-plan.md`](../../../../YFex.UI/docs/plans/why-component-plan.md), ⚑WH1–WH12).
> This document covers the **YFex.Rules side**: ⚑WH2 (owner changes), ⚑WH7 (rule as applied), ⚑WH8 (what if) and ⚑WH10
> (trace cost), plus what Rules needs from ⚑WH1 (where the neutral reason model lives) and ⚑WH3 (the generated per-field source).
> **Revised 2026-11-20 by [localization](../localization/README.md):** ⚑RX7 display texts move onto declarations with
> generated keys (⚑LZ10); ⚑RX14 `AmountFields` is replaced by `[Amount]` + `ForAmount()` and `ForField` becomes typed
> (⚑LZ15); `RuleRef`/table codes in examples become generated handles (⚑LZ16). Settle those before RX-P1 / RX-P3.
> **Load for:** any work on `Explanation`, the trace, condition reporting, `[ConstraintsFrom]` explanations or the "why" UI.
> Read 04 §4–§8, 05 §1–§2 and 06 first. Every current-state claim below was checked against the code on 2026-11-19.

The UI never interprets rules (06 §5, ⚑WH2). So everything a "why" view shows — which rule set a limit, which conditions
passed, what is missing, what would change — must come out of YFex.Rules already resolved, with display text from the
message catalog and stable keys for links and actions.

---

## 1. Current state (code, 2026-11-19)

| Area | What exists | Where |
|---|---|---|
| Limits | `FieldLimit(Field, Min, Max, Options)` per field, sorted by field id. No source rule, no `Exceed`, no approval permission. | `Decisions/Decision.cs` |
| Limit merging | The engine **already tracks** the winning candidate per bound and options (`LimitState.MinCandidate`, `MaxCandidate`, `OptionsCandidate`, plus `MinExceed/MaxExceed/MinPermission/MaxPermission`) and uses them for `[Requested]` violations — then **drops them** when it builds `FieldLimit`. Options merged by intersection keep only the first grant's candidate. | `Engine/Evaluator.cs` `MergeLimit`, `MergeGrant`, `MergeState`, `Build` (lines 538–594, 712–718) |
| Explanation | `Applied` (from adjustments), `NotApplied` (Full trace), `Limits` (from `Decision.Limits`, `RuleRef` = default, `Stage` = default), `Approvals`, `Initial`, `Final`. `ExplanationLine` is a positional `record struct` (Title, Detail, Rule, Stage, LineIndex, Amount, Kind, Direction). | `Tracing/Explanation.cs` |
| Related rules | Only inside `Detail` text: "exclusive with {0}", "capped from {0} to {1}", "disabled by {tier}". | `Explanation.Build`, `RuleMessages` |
| Resolution notes | `ResolutionNote` declares `CappedBy`, `StackedWith`, `ClampedByGuardrail`, `ApprovedBy`; the engine produces **only `CappedBy`**. No code anywhere in `C:\Dev\YFex` creates the other three. A Stack-group cap is recorded as `CappedBy(new RuleRef(group.Name, 0, 0, 0))` — a fake rule ref whose "code" is the group name. | `Decisions/Adjustments.cs`; `Evaluator.ApplyAdjust` line 455 |
| Guardrails | Clamping happens at compile time (`CompileNote.ParameterClamped`, `RowRejected`, `SealedOverrideIgnored`); the Full trace lists them as `Overridden(0)` entries with an empty group and stage, unlinked to the adjustment they changed. | `Hosting/CompiledRuleSet.cs`; `Evaluator.NoteCompileNotes` |
| Trace | `TraceEntry(Rule, Stage, Group (name), LineIndex, Outcome, Directives)`; outcomes Applied, NotApplicable(ConditionId), Superseded(by), Capped(from, to), Overridden(tier), Blocked(violation), NotEffective(date). `Applied` level keeps applied/capped/blocked candidates; `Full` adds the rest. | `Tracing/Trace.cs`; `Evaluator.BuildTrace` |
| Conditions | `ConditionInfo(Index, Text, MessageKey, Field, Value)` per split `&&` operand (generator `ConditionSplitter`; Dynamic `RuleCompiler.Info`). `Field` is a member name of the situation **or** the line (no flag says which). The explain delegate returns the first false index; operands after it are never evaluated. `Decision.Rules` is `internal`. | `Definition/RuleDescriptor.cs`; `Generator/ConditionSplitter.cs` |
| Tables | `Select` reports the closest row (the one whose first failing `[When]` column is furthest right) and that column. No way to get the row's cell as text or value. | `TableInstance.Select`; `Tables/TableContracts.cs` |
| Dynamic rules | One program per top-level `and` operand. Explain runs with `$base` = null, so a `$base` condition can be reported as failed when it is not (13, Phase 6 "open"). | `Rules.Dynamic/Runtime/CompiledRule.cs` |
| Violations | `Violation.Origin = (ViolationSource.Rule, OwnerId)`; owner = rule index or `Rules.Length + table index`. The table **row** is lost. | `Directives/Directive.cs`; `Evaluator.Exceeded` |
| Per-field query | None. `Decision.TryGetConstraints(name)` exists for constraints only. | `Decision.cs` |
| Hierarchy | Stage and group exist on entries; rule set name on the decision; no path, no display names for stages or groups. | — |
| Rule as applied | **Built** as `RuleAdmin.Queries.GetRuleDefinition(RuleSet, Code, Version, Tier, DataVersion, RowId)` → `RuleDefinitionView` (catalog entry + the raw JSON of the row / parameters / dynamic spec, published by/at/note). Guarded by `RuleAdminOptions.ViewPermission` (null by default = any signed-in user; apps that set it, e.g. to `RuleAdminPermissions.View`, make it admin-only, together with the other history queries); server-side; tested (`PublishTests`, `DynamicRuleAdminTests`). Not a display model. | `YFex.Messaging/src/YFex.Rules.Admin/RuleAdmin.cs:87`, `RuleAdminHandlers.cs:299` |
| What if | `IRuleSetHost.Simulate` replays **past situations** against **draft data** (publish-time what-if). Nothing evaluates a **changed situation** against the current data. Situations have no generated "with field" member. | `Hosting/Simulation.cs`; `Situations/ISituation.cs` |
| In view models | `[ConstraintsFrom] Decision<TS>`: generated `__ApplyDecision_X` replaces the bag's Rule violations; `__vTryGetConstraints` merges `TryGetConstraints(path)`. Matched by attribute name, no reference to Rules. | `YFex.Validation.Generator/Emitter.Vm.cs:166, 343` |
| Consumers of `Explanation` | Only YFex.Rules tests (`GeneratedScenarioTests`, `EngineScenarioTests`, `DynamicRuleTests`). | — |

## 2. Goals and non-goals

- **Goals:** every limit, option set, violation, adjustment and approval names its source rule (and table row); every line
  carries group, field, related rules as data, tier label, description and a hierarchy path; a public condition checklist
  per rule; a per-field query; "rule as applied" as a display model; a counterfactual "what if"; a projection to the
  neutral reason model.
- **Hot path unchanged:** `TraceLevel.Off` evaluations allocate exactly what they allocate today (same number of objects; the
  `FieldLimit` array grows by a few bytes per field). Everything else is computed **on demand from the decision** or with the
  trace on.
- **Non-goals:** UI layouts and triggers (YFex.UI), the Forms "why read-only" reasons (⚑WH9), Authorization and Licensing
  reasons (⚑RX29).

## 3. Design per gap

### 3.1 Sources of limits and options (gap 1) — ⚑RX1, RX2, RX3

The decision keeps a compact source per bound. A source is resolved to a `RuleRef` on demand, through the compiled rule set the
decision now keeps (it already keeps its `RuleSetDefinition` and the rule array; the compiled set is cached by the host).

```csharp
/// Compact reference to the candidate that produced a value (no strings, no allocation). Resolve with Decision.RefOf.
public readonly record struct RuleSource(ushort Owner, int Row = -1, short Line = -1)
{
    public static RuleSource None => new(ushort.MaxValue);
    public bool IsNone => Owner == ushort.MaxValue;
    // Owner = rule index in the compiled set, or Rules.Length + table index — the same numbering as ViolationOrigin.OwnerId.
}

/// After (⚑RX1): sources and the exceed behaviour of each bound travel with the limit.
public readonly record struct FieldLimit(SituationFieldRef Field, RawValue Min, RawValue Max, OptionSet Options)
{
    public RuleSource MinSource { get; init; }
    public RuleSource MaxSource { get; init; }
    public RuleSource OptionsSource { get; init; }          // the first grant; see OptionsMerged
    public byte OptionsMerged { get; init; }                // how many grants were intersected (0 or 1 = only OptionsSource)
    public Exceed MinExceed { get; init; }
    public Exceed MaxExceed { get; init; }
    public Permission? ApprovalPermission { get; init; }    // when a bound's Exceed is RequireApproval
    public bool HasOptions => !Options.IsEmpty;
}

public sealed class Decision<TSituation>
{
    // was: RuleDescriptor<TSituation>[] _rules  →  now the compiled set (rules, tables, tiers, data versions)
    public RuleRef RefOf(RuleSource source);                  // code rule or table row, with tier and data version
    public RuleRef RefOf(in ViolationOrigin origin);          // rule-sourced violations (no row; see ⚑RX3)
    public RuleSource SourceOf(int violationIndex);           // ⚑RX3: with the row for table-issued Deny/Warn/[Requested]
}
```

Before / after for a sale with the installments table (row 3: Payment = Card, Total 500–999.99 → max 6) and the
`installments.card.12x` rule not applying:

```csharp
// before: the limit is known, its origin is not
var l = d.Limits[0];                       // FieldLimit(Installments, None, 6, Unrestricted)
explanation.Limits[0].Rule;                // default(RuleRef)

// after
var l = d.Limits[0];
d.RefOf(l.MaxSource);                      // RuleRef("sale.installments", 1, Tier: 1, DataVersion: 42, RowId: 7f3c…)
l.MaxExceed;                               // Exceed.RequireApproval
l.ApprovalPermission;                      // Vendas.Parcelamento.Acima
```

- When several rules limit the same field, the bound keeps the **winner** (the most restrictive one, or the least restrictive
  inside a `LeastRestrictive` group, as today). The other contributors ("also limited by…") come from the trace's `Limit` and
  `Grant` directives, which already record them at `TraceLevel.Applied` and above (§3.8 makes that trace available on demand).
- The fingerprint does **not** include sources (04 §8: it hashes the outcome). Two decisions with the same limits from
  different rules compare equal — see ⚑RX23 for what that means for view-model refreshes.
- Cost: `FieldLimit` grows by roughly 40 bytes (three 8-byte sources, the exceed bytes and the permission); no new object.
  `Decision` swaps one reference for another. RX-P0 measures it.

### 3.2 Richer explanation lines (gap 2) — ⚑RX4, RX5, RX6, RX7

`ExplanationLine` becomes a `sealed record` (cold path: building it already allocates strings). The only consumers are
YFex.Rules tests, so the break is cheap (⚑RX4).

```csharp
public enum ExplanationKind : byte { Applied, NotApplied, Limit, Options, Approval, Warning, Denied }

public sealed record ExplanationLine
{
    public required string Title { get; init; }              // "Parcelamento" / "Cliente Gold 5%"
    public string? Detail { get; init; }                      // "Parcelas: até 6x" / "exclusiva com Black Friday"
    public string? Description { get; init; }                 // catalog "{code}.description" (⚑RX7)
    public required ExplanationKind Kind { get; init; }
    public RuleRef Rule { get; init; }                        // now also set on limit, option and violation lines
    public StageRef Stage { get; init; }
    public string? Group { get; init; }                       // group name (key), e.g. "Installments"
    public string? GroupTitle { get; init; }                  // display, e.g. "Parcelamento"
    public SituationFieldRef Field { get; init; }             // the field it acts on (limit, grant, deny, warn, approval)
    public int? LineIndex { get; init; }
    public RawValue Amount { get; init; }
    public AdjustDirection Direction { get; init; }
    public string? TierLabel { get; init; }                   // "Loja 12" / "Padrão" (⚑RX7, ⚑RX28)
    public ImmutableArray<RuleRelation> Related { get; init; } // as data, for links (⚑RX5)
    public ExplanationPath Path { get; init; }                // §3.5
    public RuleConditions? Conditions { get; init; }          // §3.3 (not-applied lines; applied lines on request)
    public WhatIfRequest? WhatIf { get; init; }               // §3.7, when a counterfactual exists
}

public enum RuleRelationKind : byte
{
    SupersededBy,        // lost an Exclusive/First resolution to Other
    Supersedes,          // the winner's side of the same pair
    CappedByGroup,       // Stack group cap (Group is set, Other is default) — replaces CappedBy(RuleRef(groupName,…))
    CappedByRule,        // the directive's own Max
    StackedWith,         // same Stack group, both applied
    ClampedByGuardrail,  // a sealed guard clamped the parameter / row value (⚑RX6)
    DisabledByTier,      // an override published at a tier disabled or replaced it
    LimitedAlsoBy,       // another rule limited the same field less strictly (from the trace)
}

/// One related rule as data. Text is pre-formatted; the UI turns Other into a link to that rule's line.
public readonly record struct RuleRelation(RuleRelationKind Kind, RuleRef Other, string Text,
    string? Group = null, RawValue From = default, RawValue To = default);
```

Fixes that come with it (all in YFex.Rules):

- A Stack-group cap records `CappedByGroup` (group name in `Group`), not a fake `RuleRef` (⚑RX5). The persisted
  `ResolutionNote.CappedBy.CapOwner` keeps today's bytes for stored adjustments; the explanation reads the group from
  `AppliedAdjustment.Stage` + the rule's group.
- `StackedWith` is **derived** by the explanation (other applied adjustments of the same Stack group and line); the engine
  does not write it into `ResolutionNote` (an adjustment can be stacked *and* capped, and the note holds one case). Remove the
  unused `StackedWith` and `ClampedByGuardrail` note cases, or keep them reserved (⚑RX5).
- Guardrail clamps (⚑RX6): `CompileNote` gains the rule/table index and the requested/clamped values; the explanation attaches
  `ClampedByGuardrail` to the lines of that rule ("limitado pela trava de 20%"), and the trace entry gets the rule's real group
  and stage instead of `""`/default.
- Catalog keys (⚑RX7), each with a fallback so nothing is ever blank:

| Text | Key | Fallback |
|---|---|---|
| rule / table title | `{code}` (today) | the code |
| rule description | `{code}.description` | none (null) |
| rule set | `{ruleSet}` | `MessageCatalog.Humanize(ruleSet)` |
| stage | `{ruleSet}.stage.{stageCode}` | humanized stage code |
| group | `{ruleSet}.group.{groupName}` | humanized group name |
| tier | `FieldName({schema}.{tierField})` + the situation's value at that depth: "Loja 12"; depth 0 → `rules.tier.code` ("Padrão" / "Default") | `Schema.TierName(depth)` |
| table row | `rules.explain.row` ("linha {0}") with the 1-based row position in its version | — |

### 3.3 Condition checklist (gap 3) — ⚑RX8, RX9, RX10, RX11, RX12

A public, on-demand checklist per rule (and per line for line rules). It is recomputed from the decision's situation —
conditions are pure — so it needs **no trace** and costs nothing until asked for (⚑RX9).

```csharp
public enum ConditionState : byte
{
    Passed,        // evaluated true (it came before the failing one)
    Failed,        // the first false operand
    NotChecked,    // after the failing one; && never evaluated it
    WouldPass,     // NotChecked, but a "field op literal" comparison we can evaluate safely: it holds   (⚑RX8)
    WouldFail,     // same, and it does not hold
    Unknown,       // a $base / custom condition that can't be recomputed outside the evaluation
}

public readonly record struct ConditionCheck(
    ConditionInfo Info,
    ConditionState State,
    RawValue Actual,       // the field's value in this situation (None when the operand isn't a plain field)
    RawValue Missing);     // for ≥ / > / ≤ / < on numbers and amounts: how far the actual value is (None otherwise)

public sealed class RuleConditions
{
    public RuleRef Rule { get; }
    public int LineIndex { get; }                          // −1 = situation level
    public ImmutableArray<ConditionCheck> Checks { get; }
    public bool AllPassed { get; }
    public string Format(int index, IMessageCatalog catalog, CultureInfo culture); // "Total ≥ R$ 1.000 (atual R$ 940 · faltam R$ 60)"
}

public sealed class Decision<TSituation>
{
    /// Conditions of one rule (code, dynamic) or the closest row of a table that selected nothing, recomputed from Situation.
    public RuleConditions? Conditions(RuleRef rule, int line = -1);
}
```

Example — `installments.card.12x` with `Applies => s.Payment == CreditCard && s.Total >= Money.Of(1000) && s.Customer.Score >= 600`
on a card sale of R$ 940:

| # | Text (pt) | State | Actual | Missing |
|---|---|---|---|---|
| 0 | requer Pagamento = Cartão de crédito | Passed | Cartão de crédito | — |
| 1 | requer Total ≥ R$ 1.000,00 | **Failed** | R$ 940,00 | R$ 60,00 |
| 2 | requer Score ≥ 600 | WouldPass | 712 | — |

Rules:

- **States:** `&&` order is the truth: operands before the failing one passed, the failing one failed, the rest were not
  evaluated. Evaluating later operands in general is unsafe — earlier operands often guard them (`s.Customer is not null &&
  s.Customer.Tier == Gold`). Only operands the splitter recognized as **field op literal** (`rules.cond.eq/neq/lt/lte/gt/gte/
  true/false`) are compared afterwards, by YFex.Rules itself with `RawValue.TryCompareTo` on the field value read through
  `TS.Read` / `TS.ReadLine`: no generated code runs, so nothing can throw (⚑RX8).
- **Actual value:** read generically from the schema by `ConditionInfo.Field`. `ConditionInfo` gains `bool IsLineField`
  (the splitter and the Dynamic compiler both know it; today a same-named line and situation field are ambiguous) (⚑RX10).
  A member that is not a situation field (a computed property) gives `Actual = None`.
- **Missing:** `Value − Actual` for `gte`/`gt` (and `Actual − Value` for `lte`/`lt`) when both are numbers or the same amount
  type; formatted with `rules.cond.missing` ("faltam {0}" / "{0} short"). For strict `>`/`<` the text says "mais de".
- **Applied rules:** all `Passed` ("por que se aplicou"): same API.
- **Decision tables (⚑RX11):** for a table that selected no row, the checklist is the closest row's `[When]` columns:
  columns before the failing one Passed, the failing one Failed, the rest NotChecked / WouldPass / WouldFail. The cell
  text needs one new generated member, a static **virtual** with a default so hand-written tables keep compiling:

  ```csharp
  public interface IDecisionTable<TSituation, TRow>
  {
      /// How [When] column whenIndex of row matches: Value(x) → "= x", Band(lo, hi) → "entre lo e hi", Any → none.
      static virtual bool TryDescribeWhen(in TRow row, int whenIndex, out CellCondition condition) { condition = default; return false; }
  }
  public readonly record struct CellCondition(string MessageKey, RawValue Value, RawValue High = default); // rules.cond.eq / rules.cond.between
  ```

  The explanation also shows which row came closest ("linha 3 da tabela Parcelamento v42").
- **Dynamic rules (⚑RX12):** `ConditionInfo` is already produced per top-level `and` operand. Two fixes:
  1. The explain delegate receives the running context, so `$base` is the real base:
     `RuleExplainFn<TS>(in TS s, int line, in RuleContext c)` (generated and Dynamic-only delegate; the evaluator has the
     context where it calls it). This also lets the code-rule splitter accept operands that read `RuleContext.Base`.
  2. For a comparison whose operand is an expression (`sum(Lines.Quantity) >= 10`), the compiler also emits a program for
     the non-literal side, so `Actual` shows "atual 7". Non-comparison operands stay `rules.cond.expr` with no actual value.
  On-demand recomputation outside the evaluation has no running base: such checks are `Unknown` unless they come from a Full
  trace (§3.8).

### 3.4 Per-field query (gap 4) — ⚑RX13, RX14

```csharp
public sealed class Decision<TSituation>
{
    /// Cheap (no allocation): does anything in this decision, or any rule that could act on it, concern this field?
    /// Drives the UI's ShowWhen = WhenAffected.
    public bool Affects(ReadOnlySpan<char> fieldName);
}

public sealed class Explanation
{
    /// Only what concerns one field: its limit / options lines (with sources and "also limited by"), violations on its path,
    /// approvals whose reason path is the field, rules that did not apply but would act on it (with their checklist), and
    /// adjustments when the field is one of the rule set's amount fields (⚑RX14). Line fields: "Lines[2].Discount".
    public Explanation ForField(ReadOnlySpan<char> fieldName);
}
```

What "would act on it" means needs **static** knowledge, because a rule that did not apply never ran `Evaluate`:

- **Code rules (⚑RX13):** the generator scans `Evaluate` for `DirectiveWriter` calls whose field argument is a
  `Fields.X` member (`d.Limit(SaleSituation.Fields.Installments, max: 12)`, `d.Grant(...)`, `d.Deny(path...)`) and emits
  `affects: [ids]` on the descriptor. When a call passes a field it cannot resolve (a variable, a helper), it reports a
  diagnostic and accepts `[Rule(Affects = [nameof(SaleSituation.Installments)])]` instead.
- **Tables:** the `[Then]` columns' fields (already in `TableSchema`).
- **Dynamic rules:** `DirectiveSpec.Field` / `Path` (already in the spec).
- **Amounts (⚑RX14):** adjustments change the rule set's amount, which is not a situation field. The rule set declares which
  view-model / situation names show that amount: `public static string[] AmountFields => [nameof(Sale.Total), nameof(Sale.Discount)];`
  (generator-checked, like `Allocation`). `ForField("Total")` then returns `Initial`, the applied adjustments and `Final`
  (Receipt layout); a derived field computed after a stage (`DerivedField`) explains as its value plus the stage it follows.

### 3.5 Hierarchy path (gap 5) — ⚑RX16

```csharp
public enum PathSegmentKind : byte { RuleSet, Stage, Group, Rule, Row, Line, Condition }

/// One level of a breadcrumb. Key is stable (codes, row id, index) so a UI can filter ("drill in") by it; Text is display.
public readonly record struct PathSegment(PathSegmentKind Kind, string Key, string Text);

public readonly struct ExplanationPath    // ≤ 7 segments; ImmutableArray inside, cold path
{
    public ReadOnlySpan<PathSegment> Segments { get; }
}
```

"Venda › Pagamento › Parcelamento › Tabela Parcelamento › linha 3" — keys `sale`, `payment`, `Installments`,
`sale.installments`, `7f3c…`. Line rules add "Item 2" after the group. A checklist line adds the condition as the last level.
Texts come from §3.2's keys.

### 3.6 Rule as applied (gap 6) — ⚑RX20, RX21, RX22

`GetRuleDefinition` exists in `YFex.Rules.Admin` but returns raw JSON, and it shares `RuleAdminOptions.ViewPermission` with
the history and data queries (an app that locks those to admins locks this too). A cashier asking "why" needs a display model, and an audit reviewer needs the same thing for an old sale. Proposal: a pure builder in YFex.Rules,
used by the Admin query handler, by a client with the version cached, and by Audit viewers:

```csharp
public sealed record AppliedRuleView(
    RuleRef Rule, string Title, string? Description, string Kind,         // "regra" / "linha de tabela" / "regra dinâmica"
    string? Group, string? Stage, string? TierLabel, string VersionLabel,  // "v3 · dados v42 · publicado em 29/09/2026 por ana"
    ImmutableArray<string> Conditions,                                     // readable conditions (code rules, dynamic specs)
    ImmutableArray<(string Name, string Value)> Values,                     // parameters, or the row's cells by column display name
    string? SpecText);                                                     // dynamic: ExprText of `when` + directives

public static class AppliedRules
{
    /// data = the stored record of RuleRef.DataVersion (null for code rules and code rows).
    public static AppliedRuleView Describe<TS>(RuleSetDefinition<TS> set, in RuleRef rule, RuleDataRecord? data,
        IMessageCatalog catalog, CultureInfo culture) where TS : ISituation<TS>;
}
```

- Code rule: catalog title + description + `Version`; the condition texts from `ConditionInfo`; parameter values of that data
  version. (The code itself is versioned by the app build; the `Version` bump discipline of 06 §6 stays.)
- Table row: the row's cells formatted by column (`TableColumn` names, `CellCondition` from §3.3), plus its row meta.
- Dynamic rule: the stored `RuleSpec` of that version, rendered with `ExprText`, and its own versioned messages.
- A new query `RuleAdmin.Queries.DescribeAppliedRule` returns only the view, with its own permission option
  (`AppliedRulePermission`, null = any signed-in user), so apps can keep `GetRuleDefinition` (raw data) admin-only (⚑RX20).
- **Audit (⚑RX21):** `AuditDecision` carries only rule set, version, fingerprint, verdict and a count. Auditing a sale needs the
  applied `RuleRef`s (and limit sources) to call `Describe` later. Request to YFex.Audit: add the refs to `AuditDecision`.
- **Retention (⚑RX22):** describing an old decision needs its data version. The server store keeps versions; the device cache
  prunes old ones, so offline "rule as applied" works for the versions in force and says "disponível online" otherwise.

### 3.7 What if (gap 7) — ⚑RX18, RX19

`IRuleSetHost.Simulate` answers "what would this **draft table** do to **past sales**" — not "what would happen **to this sale**
if Total were R$ 1.000". The second is a counterfactual evaluation: same compiled set, a changed situation.

```csharp
public interface ISituation<TSelf>
{
    /// Generated: a copy with one field (or line field) set. Default false so hand-written situations keep compiling.
    static virtual bool TryWith(in TSelf s, ushort fieldId, in RawValue value, int line, out TSelf result) { result = s; return false; }
}

/// Built from a failed checklist: every Failed / WouldFail "field op literal" condition of the rule, set to the nearest value
/// that satisfies it (⚑RX19).
public readonly record struct WhatIfRequest(RuleRef Rule, int Line, ImmutableArray<(ushort Field, RawValue Value)> Changes);

public sealed record WhatIfResult(WhatIfRequest Request, Decision<TSituation> After, string Summary); // "Com Total ≥ R$ 1.000: até 12x"

public sealed class Decision<TSituation>
{
    public bool TryWhatIf(in WhatIfRequest request, out Decision<TSituation> after);   // pure, same compiled set
    public WhatIfResult? WhatIf(in WhatIfRequest request, ReadOnlySpan<char> field, IMessageCatalog catalog, CultureInfo culture);
}
```

- Target values (⚑RX19): `eq`/`true`/`false` → the literal; `gte`/`lte` → the literal; `gt`/`lt` → the literal ± one step
  of the value's precision (integers ± 1; amounts ± one unit of the rule set's rounding decimals); `neq` and expressions → no
  request (the UI hides "Simular").
- The summary compares the field before and after (limit, options, verdict, adjustments on amount fields) with catalog key
  `rules.whatif.summary` ("Com {condition}: {result}").
- A condition on a `[Derived]` situation field (e.g. `Total` computed from lines) is changed directly in the copy; the
  summary says it is hypothetical for the rule's conditions — the lines are not changed.
- Cost: one evaluation (microseconds), only when the user asks.

### 3.8 Trace cost (gap 8) — ⚑RX15

| Need | Source | Cost with trace Off |
|---|---|---|
| limit/option/violation sources | `FieldLimit`, `SourceOf` (§3.1) | bigger structs, no new object (violation sources: ⚑RX3) |
| `Affects(field)` | limits, violations, approvals, static `affects` | none |
| checklist, what if, rule as applied | recomputed from `Situation` on demand | none until asked |
| "not applied" list, "also limited by", superseded/capped relations | trace `Full` | — |

Instead of evaluating with `TraceLevel.Full` on every keystroke (⚑WH10), `Decision.Explain` **re-evaluates once with Full** when
the decision's trace is lower, and keeps that trace on the decision (the engine is pure and deterministic, so the result is the
same decision with a trace; `Extras` gets a lazily-set slot) (⚑RX15). Screens keep `Off` (or `Applied`) and pay for "why" only
when a user opens it. Gates: the existing allocation tests stay green for `Off`, and a new benchmark measures Off / Applied /
Full / Full-on-demand.

### 3.9 Producing the neutral reason model (gap 9) — ⚑RX17

Rules sits below Mvvm and UI, so it can only produce a type from **core** (or from a layer below Rules). With ⚑WH1 = core
`src/YFex/Reasons`:

```csharp
public sealed class Explanation
{
    public WhyReport ToWhy();                       // the whole decision
}

public sealed class Decision<TSituation>
{
    /// What the generated IWhySource calls (one line per [ConstraintsFrom] property, like TryGetConstraints).
    public bool TryGetWhy(ReadOnlySpan<char> field, IMessageCatalog catalog, CultureInfo culture, out WhyReport report);
}
```

Mapping: `ExplanationLine` → `WhyItem` (Title, Detail, Kind, Amount formatted, Path, Stage title, Reference =
"code · v3 · Loja 12", Conditions → `WhyCondition` with Actual and Missing text, Related → `WhyLink`, WhatIf → an action key,
Key = the line's stable key). Formatting (amounts, dates) uses the catalog's culture; the UI receives text only.

What Rules needs **from ⚑WH1** (changes to the UI plan's §3 sketch):

1. `WhyItem.Path` as segments with a **key and a text** (§3.5), not `IReadOnlyList<string>`: drill-in filters by key.
2. `WhyItem.Key` and `WhyLink.Target` as **strings** (stable keys such as `rule:sale.installments#7f3c…`), not `object?`:
   no boxing of `RuleRef`, and a report can cross the wire (server-produced reasons, Blazor; ⚑RX29 Cqrs).
3. `WhyCondition` gains `Missing` text and the states `WouldPass`, `WouldFail`, `Unknown`.
4. `WhyItem` gains `Field` (the field it acts on), `Description`, and an optional `WhatIf` action key.
5. `IWhySource.TryGetWhy(path, catalog, culture, out report)`: the view supplies the catalog and culture, so a generated view
   model needs no injected catalog (core has no ambient catalog, and a VM-held one would go stale on a culture change).

What Rules needs **from ⚑WH3** (YFex.Validation generator): for each `[ConstraintsFrom] Decision<TS>` property, emit
`IWhySource.TryGetWhy` calling `Decision.TryGetWhy(path, catalog, culture, out report)` (matched by name, no reference, like
`TryGetConstraints` today) and raise `WhyChanged("")` from `__ApplyDecision_X` (⚑RX23).

## 4. Decisions (⚑, all pending)

| ⚑ | Topic | Proposal (recommended first) | Rejected alternatives | § |
|---|---|---|---|---|
| RX1 | Limit and option sources | compact `RuleSource` (owner, row, line) on `FieldLimit` + `MinExceed/MaxExceed/ApprovalPermission`; `Decision` keeps the `CompiledRuleSet` and resolves `RefOf` on demand | full `RuleRef` per bound inline (≈ 3 × 40 bytes, strings copied per limit); sources only from the trace (needs trace on every evaluation) | 3.1 |
| RX2 | Options merged from several grants | keep the first grant + a count; the full list from the trace | an array of sources per field always (allocation per evaluation) | 3.1 |
| RX3 | Row of table-issued violations | a `RuleSource[]` parallel to `Violations`, allocated only when there are violations | trace-only (no row with trace Off); extend `ViolationOrigin` in YFex.Validation (a small source + owner id shared by every validator; a table row does not belong there) | 3.1 |
| RX4 | `ExplanationLine` shape | `sealed record` with init members (breaking; only Rules tests use it) | keep the positional struct and add side arrays (unreadable); a new `ExplanationLine2` | 3.2 |
| RX5 | Related rules and notes | `RuleRelation` list on lines; Stack cap as `CappedByGroup`; `StackedWith` derived by the explanation; delete the never-produced `StackedWith`/`ClampedByGuardrail` note cases (or keep reserved) | keep relations in `Detail` text only (no links); write `StackedWith` into the persisted note (one case per note) | 3.2 |
| RX6 | Guardrail clamps | `CompileNote` gains the rule/table index and requested/clamped values; lines get `ClampedByGuardrail`; trace entries get real group/stage | leave them as anonymous `Overridden(0)` entries | 3.2 |
| RX7 | Display-name keys | `{code}.description`, `{ruleSet}`, `{ruleSet}.stage.{code}`, `{ruleSet}.group.{name}`, tier = field name + situation value, `rules.tier.code`; humanized fallbacks | attributes with display text on rules (not translatable; catalogs are the localization owner) | 3.2 |
| RX8 | Condition states | Passed / Failed / NotChecked, plus WouldPass / WouldFail for field-op-literal operands compared by Rules itself | evaluate every operand (breaks `&&` guards, may throw); Passed / Failed only (hides "what else is missing") | 3.3 |
| RX9 | When checklists are built | on demand from the decision's situation (`Decision.Conditions(rule, line)`) | stored in the Full trace (allocates per not-applied rule on every evaluation) | 3.3 |
| RX10 | `ConditionInfo` additions | `bool IsLineField` (generator + Dynamic compiler) | resolve by name, situation first (wrong for same-named line fields) | 3.3 |
| RX11 | Table cell text | generated `static virtual TryDescribeWhen(row, whenIndex, out CellCondition)` with a default | parse the row JSON at explain time (slow, duplicates the column logic); only "requer outro {field}" (today) | 3.3 |
| RX12 | Dynamic rule checks | explain delegate takes `RuleContext` (fixes `$base`); operand programs for actual values of expressions | keep `$base` = null (wrong "failed" reports) | 3.3 |
| RX13 | Static "affects" of code rules | generator scans `Evaluate` for `DirectiveWriter` field arguments + `[Rule(Affects = …)]` override + diagnostic when unresolvable | attribute only (boilerplate on every rule); none (per-field view misses not-applied rules) | 3.4 |
| RX14 | Amount fields | the rule set declares `AmountFields` names (generator-checked) | a VM attribute per screen; treat every adjustment as affecting every field | 3.4 |
| RX15 | Trace cost for "why" screens | evaluate with Off/Applied; `Explain` re-evaluates once with Full on demand and caches the trace on the decision | Full on every UI evaluation (⚑WH10 as written); always Full | 3.8 |
| RX16 | Hierarchy path | `PathSegment(Kind, Key, Text)` list per line | strings only (UI cannot filter by level) | 3.5 |
| RX17 | Reason model production | `Explanation.ToWhy()` + `Decision.TryGetWhy(field, catalog, culture, out report)` producing core `Reasons` types (needs ⚑WH1 = core and the five model changes in §3.9) | Rules references YFex.UI.Public (wrong layer direction); the UI renders `Explanation` directly (ties the control to Rules) | 3.9 |
| RX18 | What if | counterfactual `Decision.TryWhatIf` / `WhatIf` on the same compiled set + generated `ISituation.TryWith` (static virtual, default false) | `IRuleSetHost.Simulate` (answers a different question: draft data on past situations); the app builds the changed situation by hand | 3.7 |
| RX19 | What-if target values | literal for eq/gte/lte; ± one precision step for gt/lt; none for neq and expressions | always the literal (wrong for strict comparisons); let the user type a value (UI work, later) | 3.7 |
| RX20 | Rule as applied | pure `AppliedRules.Describe` in Rules; new query `RuleAdmin.Queries.DescribeAppliedRule` returning only the view, with its own `AppliedRulePermission` | add the view to `GetRuleDefinition` (it shares the admin view permission with raw data); raw JSON only; build the display in the UI (would interpret rules) | 3.6, 9.8 |
| RX21 | Audit of decisions | request: `AuditDecision` carries applied `RuleRef`s and limit sources | app persists adjustments only (06 §2; limits and denials lost) | 3.6 |
| RX22 | Old data versions offline | describe what the device has; otherwise "disponível online" | keep every version on devices (unbounded cache) | 3.6 |
| RX23 | Refresh of a VM's "why" | fingerprint excludes sources (unchanged); generated code raises `WhyChanged` on every decision **reference** change, not only fingerprint changes | add sources to the fingerprint (client and server could disagree on sources while agreeing on the outcome → false mismatches) | 3.1, 3.9 |
| RX24 | Messages | new keys in `RuleMessages` (en, pt): `rules.cond.missing`, `rules.cond.actual`, `rules.cond.between`, `rules.cond.would-pass`, `rules.explain.row`, `rules.explain.also-limited`, `rules.explain.stacked`, `rules.explain.guardrail`, `rules.tier.code`, `rules.whatif.summary`, relation texts | texts built in code | 3.2–3.7 |
| RX25 | Validation generator (⚑WH3) | emit `IWhySource` per `[ConstraintsFrom]` property calling `Decision.TryGetWhy`; rule-sourced violations in the bag link to their why through `ViolationOrigin` | hand-written per screen | 3.9 |
| RX26 | Personal data in explanations | situation fields marked `[Sensitive]` (YFex.Privacy) show `Actual` masked in `ToWhy` and in `AppliedRuleView`; the app can opt a screen in | show actual values always (a "why" view or a support screenshot leaks e.g. income, birth date) | 3.3 |
| RX27 | AI and Capabilities | `WhyReport` JSON (source-generated) so YFex.Capabilities / YFex.Automation agents can answer "why" with the same data as the UI; `RuleSetDescription` stays for the rule set itself | separate AI-only explanation format | 3.9 |
| RX28 | Tier value labels | tier label = catalog field name + the raw situation value; apps that want "Loja Centro" add the names to their message source (gathered before evaluation, Principle A) | Rules calls YFex.Tenancy to resolve scope names (I/O inside a pure library) | 3.2 |
| RX29 | Reasons beyond Rules (⚑WH9) and across the wire | Cqrs `RulesStage` can attach a `WhyReport` to a Denied result for remote clients (needs string keys, §3.9); Authorization (`AuthorizationContext.ExplainUnavailable`, `DenialKind`) and Licensing produce `WhyReport` in their own phases | Rules explains permissions too (wrong owner) | 2, 3.9 |

## 5. Ecosystem fit

| Library | Outcome | Role / reason | ⚑ |
|---|---|---|---|
| YFex core | **better placement / lacks feature** | home of the reason model (`Reasons`), named by Rules, Validation, Authorization, Licensing, UI; `Localization` catalog used for every text | WH1, RX17 |
| YFex.Validation | **fits, lacks feature** | generator emits `IWhySource` for `[ConstraintsFrom]`; `ViolationOrigin` links bag violations to rules | RX25, RX3 |
| YFex.Expressions | used | Dynamic rule operand programs for actual values; `ExprText` for applied dynamic rules | RX12, RX20 |
| YFex.State | used | `[Observable] Decision` property; refresh rule for `WhyChanged` | RX23 |
| YFex.Persistence | not a fit | explanations are not stored; the device rule-data cache already lives in Rules.Admin | — |
| YFex.Data | used (unchanged) | `SqlRuleStore` keeps data versions that `Describe` reads | RX22 |
| YFex.Cqrs | **fits** | Denied results could carry a `WhyReport` for remote clients | RX29 |
| YFex.Messaging (YFex.Rules.Admin) | **used, lacks feature** | `GetRuleDefinition` exists; add `DescribeAppliedRule` with its own permission | RX20 |
| YFex.NavigatR, YFex.Mvvm | not a fit | no navigation or VM base change; the generated source is a Validation concern | — |
| YFex.UI | consumer | renders `WhyReport`; needs the five model changes in §3.9 | WH1, RX17 |
| YFex.Audit | **fits, lacks feature** | `AuditDecision` needs applied rule refs so a sale can show "rule as applied" later | RX21 |
| YFex.Privacy | **fits** | mask `[Sensitive]` actual values in explanations | RX26 |
| YFex.Authorization | **fits (producer)** | approval permissions on limits come from Authorization `Permission`; it can produce reasons for unavailable commands | RX1, RX29 |
| YFex.Licensing | fits (producer) | unavailable features through the Authorization availability hook | RX29 |
| YFex.Tenancy | not a fit (by design) | tier labels come from situation values and the app's catalog, never from a Tenancy call | RX28 |
| YFex.Capabilities, YFex.Automation | **fits** | AI "why" answers from the same `WhyReport` JSON | RX27 |
| YFex.Time | not a fit | effective dates already come from the situation | — |
| YFex.Logging | not a fit | explanations are user data, not logs; diagnostics probes unchanged | — |
| YFex.Composer (design) | fits | dynamic rules it authors are explained by the same checklist and `Describe` | RX12, RX20 |
| YFex.Offline | used (unchanged) | signed rule data on devices bounds what can be described offline | RX22 |
| YFex.Inference (design) | fits | a probability fact in a condition explains like any field ("p(atípico) ≥ 0,9 (atual 0,93)"), D39 | — |
| YFex.Actors, Authentication, Cryptography, Installations, Networking, Resilience, Orchestration, Jobs, EventSourcing, Notifications, Files, Web, Search, System, Profiling, Security, Devices, Backup, Hosting, Cluster, Provisioning, Installer, CustomLogic | not a fit | no explanation data flows through them; CustomLogic implementations are invisible to rules | — |

### Requests to other libraries

| Library | Change | Breaking | ⚑ | Needed by |
|---|---|---|---|---|
| YFex core | `src/YFex/Reasons` with the §3.9 changes (segment keys, string keys, condition states, Field/Description/WhatIf, catalog on `IWhySource`) | new | WH1, RX17 | RX-P4 |
| YFex.Validation | generator: `IWhySource` for `[ConstraintsFrom]`; `WhyChanged` on reference change | additive | WH3, RX25, RX23 | RX-P4 |
| YFex.Rules.Admin (YFex.Messaging repo) | query `DescribeAppliedRule` → `AppliedRuleView`; option `AppliedRulePermission` | additive | RX20 | RX-P6 |
| YFex.Audit | `AuditDecision` + applied `RuleRef`s and limit sources (canonical encoding v1 → new version tag + golden test) | yes (stored format) | RX21 | RX-P6 |
| YFex.Privacy | none new: `[Sensitive]` already classifies; Rules reads the classification of situation fields | — | RX26 | RX-P2 |
| YFex.UI | consume the model; `SimulateRequested` carries the what-if key and calls `Decision.WhatIf` through the source, not `Simulation` | plan change | WH8, RX18 | W3, W5 |

## 6. Phases

Order follows the dependencies: sources before lines, lines before per-field, everything before `ToWhy`. Each phase builds
on its own; tests close each phase (00-start-here §3).

| Phase | Content | Done when |
|---|---|---|
| **RX-P0** Sources | benchmark baseline first (Off/Applied/Full); `RuleSource`; `FieldLimit` sources + exceed/permission; `Decision` keeps the compiled set, `RefOf`, `SourceOf` (⚑RX1–RX3) | `d.RefOf(d.Limits[i].MaxSource)` names the table row; Off allocations unchanged in count |
| **RX-P1** Lines | `ExplanationLine` record; group, field, tier label, description, relations, path; Stack cap as `CappedByGroup`; guardrail notes with rule index; new messages (⚑RX4–RX7, RX16, RX24) | the sale sample explains every line with group, path and relations; no `RuleRef(groupName,…)` left |
| **RX-P2** Conditions | `IsLineField`; `Decision.Conditions`; actual / missing / would-pass; table `TryDescribeWhen`; Dynamic explain with context and operand programs; `[Sensitive]` masking (⚑RX8–RX12, RX26) | the 12x checklist shows Passed / Failed (atual R$ 940, faltam R$ 60) / WouldPass; a table and a dynamic rule give the same shape |
| **RX-P3** Per field | static `affects` (generator scan + attribute + diagnostic), tables, dynamic; `AmountFields`; `Decision.Affects`; `Explanation.ForField`; lazy Full re-evaluation (⚑RX13–RX15) | `ForField("Installments")` returns the limit with its row, the not-applied 12x rule with its checklist, and nothing about unrelated fields |
| **RX-P4** Reason model | after ⚑WH1 lands in core: `ToWhy`, `Decision.TryGetWhy`; Validation generator `IWhySource` (⚑RX17, RX23, RX25, RX27) | a validated view model with `[ConstraintsFrom] Decision<SaleSituation>` returns a `WhyReport` for "Installments" with no hand-written code |
| **RX-P5** What if | generated `ISituation.TryWith`; `WhatIfRequest` from checklists; `Decision.TryWhatIf` / `WhatIf` (⚑RX18, RX19) | "Com Total ≥ R$ 1.000: até 12x" from the 12x checklist |
| **RX-P6** Rule as applied | `AppliedRules.Describe`; `DescribeAppliedRule` query + `AppliedRulePermission`; Audit request; stored-decision replay (⚑RX20–RX22, RX31) | the same view for a code rule, a table row of version 42 and a dynamic rule; a stored sale replays to its fingerprint and explains |
| **RX-P7** Server and thin clients | `EnforceOptions.Reasons`, `Reasons` on `ValidationProblem` / `ApprovalRequired`, culture from the call, `ApplyServer` keeps them; generic explain / what-if handlers (⚑RX30, RX32) | a thin client shows the same "why" as the rich client, from a rejection or an explain query (§9) |

UI mapping: the UI plan's W3 needs RX-P0–RX-P4; W5 needs RX-P5 and RX-P6; thin clients (Blazor, Uno web) need RX-P7. Code examples for each scenario: §9.

## 7. Tests per phase

| Phase | Tests |
|---|---|
| RX-P0 | limit from a code rule, a table row, a dynamic rule; most-restrictive winner across groups; LeastRestrictive group winner; options intersection count; `[Requested]` violation from a table row has the row (`SourceOf`); **allocation test**: Off evaluation allocates the same number of objects as before; fingerprint unchanged for the existing golden decisions; benchmark Off/Applied/Full before and after |
| RX-P1 | Exclusive: superseded line links to the winner and the winner lists `Supersedes`; Stack cap → `CappedByGroup` with the group; stacked lines list each other; parameter clamped by a guard → `ClampedByGuardrail` on that rule's line; tier label for depth 0 and depth 2 ("Loja 12"); description present / absent; path segments and keys for code rule, table row, line rule; pt and en texts |
| RX-P2 | the 12x example (Passed, Failed with actual and missing, WouldPass); guarded operand stays NotChecked (`s.Customer is not null && …`); same-named line and situation field resolved by `IsLineField`; strict `>` missing text; table closest row with Band and Value cells; hand-written table without `TryDescribeWhen` still explains the column; dynamic `$base` condition no longer reported failed; dynamic aggregate operand shows the actual sum; `[Sensitive]` actual masked; generator tests for `IsLineField` and `TryDescribeWhen` emission |
| RX-P3 | generator: `affects` from `d.Limit(Fields.X…)`, `Grant`, `Deny(path)`; helper call → diagnostic; `[Rule(Affects)]` honoured; `Affects` false for an unrelated field and allocation-free; `ForField` on a line path; amount field returns Receipt data; lazy Full gives the same decision (fingerprint) plus a trace, computed once |
| RX-P4 | `ToWhy` mapping of every line kind; keys stable across two evaluations; `TryGetWhy` false for a field nothing affects; Validation generator test: VM with two `[ConstraintsFrom]` properties; `WhyChanged` raised when the decision reference changes with the same fingerprint; AOT smoke publishes with zero IL warnings (JSON source-generated) |
| RX-P5 | `TryWith` generated for situation and line fields; hand-written situation returns false; eq / gte / gt (integer and amount precision) targets; neq → no request; derived-field caveat text; what-if leaves the original decision untouched |
| RX-P6 | `Describe` for code rule (title, description, conditions, parameter values), table row of a past version, dynamic spec with its own messages; Admin handler returns the view; permission test for the narrower permission; Audit golden test for the new `AuditDecision` encoding (in YFex.Audit) |

Gates (infrastructure): Release build of `rules.slnx`, all tests, AOT smoke with zero IL warnings, allocation tests, and the
generator tests; Validation and Messaging repos for RX-P4 / RX-P6.

## 8. Findings that contradict or refine the UI plan

1. **§1 item 6 and ⚑WH7 ("designed, not built"):** `GetRuleDefinition` is built (YFex.Rules.Admin, in the YFex.Messaging
   repo) and tested. It returns raw JSON and shares `RuleAdminOptions.ViewPermission` (null by default = any signed-in user)
   with the history queries. What is missing is a display model and a permission of its own (⚑RX20).
2. **⚑WH8 ("the app runs `Simulation`"):** `Simulation` replays past situations against draft data. It cannot answer "with
   Total ≥ R$ 1.000". The what-if needs a counterfactual evaluation in Rules (⚑RX18).
3. **⚑WH10 ("screens evaluate with Full"):** not needed; `Explain` can re-evaluate once with Full on demand (⚑RX15).
4. **§1 row "AppliedAdjustment … `ResolutionNote` (capped by, stacked with, clamped by a guardrail, approved by)":** only
   `CappedBy` is ever produced, and a Stack-group cap uses a fake `RuleRef` named after the group (⚑RX5, RX6).
5. **§1 row "Rule catalog … tables: the failing column":** tables give the closest row and the failing column index only;
   there is no cell text yet (⚑RX11). Dynamic "why not" misreports `$base` conditions today (⚑RX12).
6. **§1 item 1:** the engine already tracks the winning candidates of each limit and drops them in `Build`; the fix is cheaper
   than the plan suggests and works with the trace off (⚑RX1).
7. **§3 model (⚑WH1):** `Path` as strings cannot drive drill-in; `Key`/`Target` as `object?` box and cannot cross the wire;
   `WhyCondition` lacks Missing and would-pass states; `WhyItem` lacks Field; `IWhySource` needs the catalog and culture
   (§3.9 list).
8. **§1 "`TraceEntry` … group":** it is the group's name (a key), not a display name; titles need the catalog keys of ⚑RX7.

## 9. Usage: rule sets, commands, queries, configuration and handlers

All blocks are `// (sketch)`: they use the names proposed above (pending ⚑) and the sale example of
[12](12-workflow-example-sale.md) (`SaleSituation`, `SaleRules`, `Sale.Commands.CloseSale`, `SaleVm`). Existing APIs are used as
they are today: `IAggregateConfiguration<T>`, `.UseSituation<TS, TLoader>()`, `.Enforce(set, options)`,
`.OnDecisionMismatch(...)`, `IRuledCommandHandler<,,>`, `IDecisionAccessor<TS>`, `ICarriesSituation<TS>`,
`IdentitySituationLoader<,>`, `RuleAdmin.Queries.*`, `AddYFexRulesAdmin(...)`.

| # | Scenario | Who writes code | New API it touches |
|---|---|---|---|
| 9.1 | authoring rules and tables | rule author | `AmountFields`, `[Rule(Affects)]` (only when the generator can't see the field), catalog keys |
| 9.2 | rich client view model (Avalonia, offline POS) | screen author | nothing for the "?" (generated); `Affects`, `ForField`, `Conditions`, `WhatIf`, `DescribeAppliedRule` for custom actions |
| 9.3 | the view | screen author | `YWhy` (YFex.UI) |
| 9.4 | server command configuration | module author | `EnforceOptions.Reasons` (⚑RX30) |
| 9.5 | command handler | module author | `RefOf`, limit sources, `ToAuditDecision` (⚑RX21, RX33) |
| 9.6 | client receives a rejection | nobody (generated `ApplyServer`) | `ValidationProblem.Reasons` (⚑RX30) |
| 9.7 | thin clients (Blazor, mobile web): explain and what-if queries | module author | `ExplainFieldHandler`, `WhatIfHandler` (⚑RX32) |
| 9.8 | "rule as applied" | admin setup only | `RuleAdmin.Queries.DescribeAppliedRule` (⚑RX20) |
| 9.9 | explaining a stored sale (history, support, audit) | module author | `DecisionReplay` (⚑RX31) |
| 9.10 | approval with reasons | nobody | `ApprovalRequired.Reasons` (⚑RX30) |
| 9.11 | tests | rule author | `ShouldLimit(...).From(...)`, `ShouldFailOn(...)` |

### 9.1 Rule set authoring — almost nothing changes

```csharp
// (sketch) — the only additions are AmountFields and, rarely, Affects.
[RuleSet<SaleSituation>("sale")]
public static partial class SaleRules
{
    public static readonly Stage Items    = Stage.First("items");
    public static readonly Stage Subtotal = Stage.After(Items, "subtotal");
    public static readonly Stage Payment  = Stage.After(Subtotal, "payment");
    public static readonly Stage Limits   = Stage.After(Payment, "limits");

    public static readonly RuleGroup Loyalty      = new(Subtotal, Resolve.Stack(cap: Percentual.Of(20)));
    public static readonly RuleGroup Installments = new(Limits,   Resolve.MostRestrictive);

    // ⚑RX14: names (VM / situation / derived) that show the rule set's amount → ForField("Total") returns the receipt.
    public static string[] AmountFields => ["Total", "Discount"];
}

// The generator sees `Fields.Installments` in Evaluate → affects = [Installments]; no attribute needed (⚑RX13).
[Rule("installments.card.12x", Group = nameof(SaleRules.Installments))]
public readonly struct Card12x : IRule<SaleSituation>
{
    public static bool Applies(in SaleSituation s) => s.Payment == PaymentMethod.CreditCard && s.Total >= Money.Of(1000);
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d) => d.Limit(SaleSituation.Fields.Installments, max: 12);
}

// The field comes from a helper the generator can't follow → a YFR diagnostic until Affects is declared.
[Rule("installments.campaign", Group = nameof(SaleRules.Installments), Affects = [nameof(SaleSituation.Installments)])]
public readonly struct CampaignInstallments : IRule<SaleSituation>
{
    public static bool Applies(in SaleSituation s) => s.Channel == Channel.Web;
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d) => Campaigns.LimitFor(in s, ref d);
}
```

Display texts are message keys in the module's catalog (⚑RX7), in the same `IMessageSource` that already holds rule titles:

```csharp
// (sketch) — Sales.Rules/SalesRuleMessages.cs, TryGetTemplate
"pt" => code switch
{
    "sale"                              => "Venda",
    "sale.stage.payment"                => "Pagamento",
    "sale.group.Installments"           => "Parcelamento",
    "sale.installments"                 => "Tabela de parcelamento",
    "installments.card.12x"             => "12x sem juros no cartão",
    "installments.card.12x.description" => "Compras a partir de R$ 1.000 no cartão podem ser parceladas em até 12x.",
    "loyalty.gold"                      => "Cliente Gold {0}",
    _ => "",
},
```

Tables and dynamic rules need no change: `TryDescribeWhen` is generated (⚑RX11), and dynamic specs already carry their fields
and messages.

### 9.2 Rich client view model

The "?" on every rule-affected field needs **no code**: the Validation generator implements `IWhySource` for
`[ConstraintsFrom]` properties (⚑RX25). What the screen author changes is the trace level (⚑RX15) and, optionally, adds
custom actions.

```csharp
// (sketch) — before (12, step 2): Full trace on every change
void RecomputePolicy() => Policy = SaleRules.Evaluate(BuildSituation(), new() { Trace = TraceLevel.Full });

// after: Off; "why" re-evaluates with Full once, only when the user opens it
void RecomputePolicy() => Policy = SaleRules.Evaluate(BuildSituation());
```

What the generator adds to `SaleVm` (shown for reading, not written by hand):

```csharp
// (sketch) — generated by YFex.Validation.Generator for [Observable, ConstraintsFrom] Decision<SaleSituation> Policy
partial class SaleVm : global::YFex.Reasons.IWhySource
{
    public bool TryGetWhy(ReadOnlySpan<char> path, IMessageCatalog catalog, CultureInfo culture, out WhyReport report)
    {
        if (__serverReasons?.TryGet(path, out report) == true) return true;       // 9.6: last server rejection first
        report = default!;
        return Policy is { } d && d.TryGetWhy(path, catalog, culture, out report);
    }

    public event Action<string>? WhyChanged;

    private void __ApplyDecision_Policy(Decision<SaleSituation> value)
    {
        Validation.ReplaceAll(ViolationSource.Rule, 0, value is null ? default : value.Violations.AsSpan());
        __serverReasons = null;
        __vConstraintsChanged?.Invoke("");
        WhyChanged?.Invoke("");                // ⚑RX23: on every new decision reference
    }
}
```

Custom actions a screen may add (all pure and synchronous, except the server round trip for "rule as applied"):

```csharp
// (sketch) — inside SaleVm (catalog injected like the other services)
[Computed] public bool InstallmentsHasWhy => Policy?.Affects(nameof(Installments)) ?? false;   // no allocation

// "Simular" from the checklist: the UI raises SimulateRequested with the item's what-if request
[StateCommand]
void Simulate(WhatIfRequest request)
{
    if (Policy?.WhatIf(in request, nameof(Installments), catalog, CultureInfo.CurrentUICulture) is { } r)
        toast.Info(r.Summary);                                           // "Com Total ≥ R$ 1.000,00: até 12x"
}

// "Ver regra como aplicada" (needs the server: old data versions live in the store)
[StateCommand]
async Task OpenAppliedRuleAsync(RuleRef rule, CancellationToken ct)
{
    var r = await dispatcher.QueryAsync<RuleAdmin.Queries.DescribeAppliedRule, AppliedRuleView>(
        new("sale", rule.Code, rule.Version, rule.Tier, rule.DataVersion, rule.RowId), ct);
    if (r.TryGetValue(out var view)) await navigator.NavigateTo(new AppliedRuleRoute(view));
}

// A one-off text without the UI component (receipt footer, a support note)
string WhyInstallments() => Policy!.Explain(catalog, CultureInfo.CurrentUICulture).ForField(nameof(Installments)).ToString();

// The checklist of one rule (e.g. a "como conseguir 12x?" banner)
RuleConditions? TwelveX() => Policy!.Conditions(Policy.RefOf("installments.card.12x"));
```

### 9.3 View

```csharp
// (sketch) — Avalonia fluent markup; YWhy comes from the UI plan (⚑WH4, WH5)
YIntegerInput().Bind(vm.Installments).Why()                            // "?" inside, flyout, List layout
YMoneyText().Bind(vm.Total).Why(layout: WhyLayout.Receipt)             // amount field → receipt (⚑RX14)
YPercentInput().Bind(vm.ManualDiscount).Why(trigger: WhyTrigger.Link)  // "até 5% · por quê?"
```

### 9.4 Server command configuration

Today a server rejection returns violations only (`RulesStage` → `Error.Validation(...)`). With ⚑RX30 the rules stage can attach
the reasons of the rejected fields, so thin clients — and rich clients whose offline decision the server overruled — explain
the same way.

```csharp
// (sketch) — SaleBaseline (12, step 0) with the new option
b.Command<Sale.Commands.CloseSale, Receipt>()
 .Validation(v => { /* unchanged */ })
 .RequirePermission(Perms.Sales.Close)
 .UseSituation<SaleSituation, SaleSituationLoader>()
 .Enforce(SaleRules.Set, new EnforceOptions
 {
     Trace   = TraceLevel.Off,                  // unchanged default; only a rejection re-evaluates with Full
     Reasons = RejectionReasons.Fields,         // ⚑RX30: None (default) | Fields (a report per rejected field) | Full
 })
 .OnDecisionMismatch(DecisionMismatchPolicy.AcceptAndFlag);

// Queries that expose decisions keep their shape; a rich client explains locally from the decision it receives.
b.Query<Sale.Queries.GetSaleOptions, SaleOptionsView>()
 .UseSituation<SaleSituation, IdentitySituationLoader<Sale.Queries.GetSaleOptions, SaleSituation>>()
 .Enforce(SaleRules.Set)
 .Cacheable();
```

Server text needs a catalog and a culture: the stage resolves `IMessageCatalog` from DI and uses `CultureInfo.CurrentUICulture`,
which the RPC call-context middleware sets from `CallContext.Culture` for the call (YFex.Messaging.Rpc already carries the
culture; Cqrs never references Messaging). Reason paths go through the same `EnforceOptions.Paths` map as violations.

### 9.5 Command handler

```csharp
// (sketch) — CloseSaleHandler (12, step 4) with sources and audit refs
public sealed class CloseSaleHandler(ISaleStore store) : IRuledCommandHandler<Sale.Commands.CloseSale, Receipt, SaleSituation>
{
    public async Task<Receipt> HandleAsync(Valid<Sale.Commands.CloseSale> cmd, Decision<SaleSituation> d, CancellationToken ct)
    {
        var sale = SaleAggregate.Create(cmd.Value, d.Totals);
        sale.RecordAdjustments(d.Adjustments);                      // unchanged (06 §2): code, version, tier, data version, row

        // optional (⚑RX33): which rule or row set each limit the customer saw
        foreach (ref readonly var l in d.Limits)
            if (!l.MaxSource.IsNone) sale.RecordLimit(l.Field.Name, l.Max, d.RefOf(l.MaxSource));

        using var bytes = new ArrayPoolBufferWriter<byte>();
        Situation.WriteTo(d.Situation, bytes);
        sale.RecordDecision(d.Fingerprint, d.RulesetVersion, bytes.WrittenSpan);   // enough to replay and explain later (9.9)

        AuditScope.Current?.Decision(d.ToAuditDecision());       // ⚑RX21: applied refs + limit sources, not just a count
        await store.SaveAsync(sale, ct);
        return new Receipt(sale.Id, d.Totals.Final);
    }
}

// (sketch) — a classic handler reads the same decision through the existing accessor
public sealed class CloseSaleClassicHandler(ISaleStore store, IDecisionAccessor<SaleSituation> decisions)
    : ICommandHandler<Sale.Commands.CloseSale, Receipt>
{
    public async Task<Receipt> HandleAsync(Sale.Commands.CloseSale cmd, CancellationToken ct)
    {
        var d = decisions.Current!;
        foreach (ref readonly var a in d.Adjustments) { /* a.Rule is the RuleRef, as today */ }
        /* … */
    }
}
```

### 9.6 Client receives a rejection

```csharp
// (sketch) — nothing changes in SaleVm.CloseAsync; ApplyServer (State) already maps violations to fields.
var result = await dispatcher.CommandAsync<Sale.Commands.CloseSale, Receipt>(cmd, ct);
switch (ApplyServer(in result))     // generated: violations → bag; new: Reasons → __serverReasons (9.2)
{
    case ServerOutcome.Ok: /* … */ break;
}

// Error case in YFex.Cqrs gains an optional member (⚑RX30); the RPC formatter (D26) carries it.
public readonly record struct ValidationProblem(ViolationList Violations)
{
    public WhyReportSet? Reasons { get; init; }       // field path → WhyReport; null unless EnforceOptions.Reasons ≠ None
}
```

A rich client normally explains from its own decision. Server reasons matter when the server decided differently (data
changed while offline) or the client is thin; the generated `TryGetWhy` answers from them until the next local decision.

### 9.7 Thin clients: explain and what-if queries

A thin client has no rule engine. It asks the server, sending the situation, which the server sanitizes like `GetSaleOptions`.

```csharp
// (sketch) — Sales.Contracts
public partial class Sale
{
    public static partial class Queries
    {
        public sealed partial record ExplainSaleField(SaleSituation Situation, string Field)
            : IQuery<WhyReport>, ICarriesSituation<SaleSituation>, IExplainQuery, ICacheable;

        public sealed partial record WhatIfSale(SaleSituation Situation, string Field, string WhatIfKey)
            : IQuery<WhatIfView>, ICarriesSituation<SaleSituation>, IWhatIfQuery;
    }
}

// (sketch) — configuration: same loader and rule set as GetSaleOptions; Full trace because explaining is the purpose
b.Query<Sale.Queries.ExplainSaleField, WhyReport>()
 .RequireAuthenticated()
 .UseSituation<SaleSituation, IdentitySituationLoader<Sale.Queries.ExplainSaleField, SaleSituation>>()
 .Enforce(SaleRules.Set, new EnforceOptions { Trace = TraceLevel.Full })
 .Cacheable();                                         // key = situation + field + culture; invalidated by RulesetPublished

b.Query<Sale.Queries.WhatIfSale, WhatIfView>()
 .RequireAuthenticated()
 .UseSituation<SaleSituation, IdentitySituationLoader<Sale.Queries.WhatIfSale, SaleSituation>>()
 .Enforce(SaleRules.Set);

// (sketch) — ready-made generic handlers in YFex.Cqrs (⚑RX32)…
services.AddTransient<IQueryHandler<Sale.Queries.ExplainSaleField, WhyReport>,
    ExplainFieldHandler<Sale.Queries.ExplainSaleField, SaleSituation>>();
services.AddTransient<IQueryHandler<Sale.Queries.WhatIfSale, WhatIfView>,
    WhatIfHandler<Sale.Queries.WhatIfSale, SaleSituation>>();

// …which are a few lines each, so a hand-written one stays easy:
public sealed class ExplainFieldHandler<TQuery, TS>(IDecisionAccessor<TS> decisions, IMessageCatalog catalog)
    : IQueryHandler<TQuery, WhyReport>
    where TQuery : IQuery<WhyReport>, IExplainQuery
    where TS : ISituation<TS>
{
    public Task<WhyReport> HandleAsync(TQuery q, CancellationToken ct)
        => Task.FromResult(decisions.Current!.TryGetWhy(q.Field, catalog, CultureInfo.CurrentUICulture, out var r)
            ? r : WhyReport.Empty(q.Field));
}

public interface IExplainQuery { string Field { get; } }
public interface IWhatIfQuery : IExplainQuery { string WhatIfKey { get; } }
```

`WhatIfKey` is the string form of a `WhatIfRequest` taken from the report (⚑RX17 string keys). The handler accepts it only if it
matches a condition of that decision's rules, so a client cannot make the server evaluate arbitrary field changes.

### 9.8 Rule as applied

```csharp
// (sketch) — YFex.Rules.Admin (YFex.Messaging repo): a new query next to GetRuleDefinition (⚑RX20)
public static partial class Queries
{
    [MemoryPackable]
    public sealed partial record DescribeAppliedRule(string RuleSet, string Code, int Version, int Tier, long DataVersion, Guid RowId)
        : IQuery<AppliedRuleView>, ICacheable;      // culture: CurrentUICulture of the call
}

// handler: the store lookup GetRuleDefinitionHandler does today + the pure builder in YFex.Rules
public sealed class DescribeAppliedRuleHandler(RulePublisher publisher, IMessageCatalog catalog)
    : IQueryHandler<Queries.DescribeAppliedRule, AppliedRuleView>
{
    public async Task<AppliedRuleView> HandleAsync(Queries.DescribeAppliedRule q, CancellationToken ct)
    {
        var host = publisher.Registry.Get(q.RuleSet);
        var data = q.DataVersion > 0 ? await publisher.Store.GetByVersionAsync(q.RuleSet, q.DataVersion, ct) : null;
        return host.DescribeApplied(new RuleRef(q.Code, q.Version, q.Tier, q.DataVersion, q.RowId), data, catalog,
            CultureInfo.CurrentUICulture);
    }
}

// setup
services.AddYFexRulesAdmin(o =>
{
    o.ViewPermission        = RuleAdminPermissions.View;   // existing: raw history and data admin-only
    o.AppliedRulePermission = null;                        // new: any signed-in user may see "rule as applied"
});
```

### 9.9 Explaining a stored sale (history, support, audit)

The handler in 9.5 stored the situation bytes, the `RulesetVersion` and the fingerprint. Months later a support screen asks
"why did sale 123 get 6x?" (⚑RX31):

```csharp
// (sketch)
public sealed partial record ExplainStoredSale(SaleId Sale, string? Field) : IQuery<WhyReport>;

public sealed class ExplainStoredSaleHandler(ISaleStore sales, IRuleStore store, RuleSetHost<SaleSituation> host,
    IMessageCatalog catalog) : IQueryHandler<ExplainStoredSale, WhyReport>
{
    public async Task<WhyReport> HandleAsync(ExplainStoredSale q, CancellationToken ct)
    {
        var stored = await sales.GetDecisionAsync(q.Sale, ct);          // bytes, RulesetVersion, fingerprint, adjustments
        var culture = CultureInfo.CurrentUICulture;

        // 1. Replay: gather the data as it was (I/O first), then evaluate (pure) with a Full trace.
        var data = await DecisionReplay.LoadDataAsync(store, host.Definition, stored.RulesetVersion, ct);
        var replay = DecisionReplay.Evaluate(host.Definition, data, stored.Situation.Span);
        if (replay.Fingerprint == stored.Fingerprint && replay.TryGetWhy(q.Field ?? "", catalog, culture, out var why))
            return why;

        // 2. Fallback (the code rules changed since: fingerprint differs): explain from what was persisted.
        return StoredDecisions.ToWhy(stored.Adjustments, stored.Limits, host.Definition, catalog, culture);
    }
}
```

`DecisionReplay.LoadDataAsync` reads the data versions ≤ `RulesetVersion.DataVersion` for the situation's tier path
(`IRuleStore.LoadAsync` + `RuleData.With`); `Evaluate` compiles with the public `CompiledRuleSet.Compile` at the stored
effective date. The fallback report says it comes from the record ("explicação a partir do registro; regras alteradas desde
então").

### 9.10 Approval with reasons

```csharp
// (sketch) — nothing new to configure: with EnforceOptions.Reasons ≠ None the ApprovalRequired error carries reasons too.
public readonly record struct ApprovalRequired(ApprovalRequestList Approvals, ulong DecisionFingerprint, ViolationList Violations = default /* … */)
{
    public WhyReportSet? Reasons { get; init; }      // ⚑RX30 — "Desconto manual: até 5% (tabela Desconto manual, linha Vendedor)"
}
// The manager's approval dialog (08 §8) shows Reasons["ManualDiscount"] above "Aprovar 8%".
// On a rich client the same text comes from the local decision: Policy.TryGetWhy("ManualDiscount", …).
```

### 9.11 Tests

```csharp
// (sketch) — YFex.Validation.Testing helpers (06 §7) extended
var d = SaleRules.Evaluate(s);                                          // trace Off is enough now
d.ShouldLimit(SaleSituation.Fields.Installments).Max(6).From("sale.installments", row: 3);
d.ShouldNotApply("installments.card.12x")                               // re-evaluates with Full on demand
 .ShouldFailOn(1, actual: Money.Of(940), missing: Money.Of(60))
 .AndWouldPass(2);
d.WhatIf(d.Conditions(d.RefOf("installments.card.12x"))!.WhatIf!.Value, "Installments", catalog, pt)!.Summary
 .ShouldBe("Com Total ≥ R$ 1.000,00: até 12x");
```

### 9.12 Decisions raised by these examples

| ⚑ | Topic | Proposal | Rejected alternatives | § |
|---|---|---|---|---|
| RX30 | Reasons on server rejections | `EnforceOptions.Reasons` (None default / Fields / Full); `ValidationProblem` and `ApprovalRequired` gain an optional `Reasons` (`WhyReportSet`: field → report); catalog from DI, culture from `CurrentUICulture` set by the RPC middleware from `CallContext.Culture`; generated `ApplyServer` keeps them for `IWhySource` until the next local decision | the client re-evaluates to explain (impossible for thin clients, wrong when the server's data differs); always attach (cost on every rejection) | 9.4, 9.6, 9.10 |
| RX31 | Explaining stored decisions | `DecisionReplay` (load data ≤ the stored data version, then pure evaluation, fingerprint check) with a fallback to persisted adjustments + limit refs | store the explanation text with the document (culture-bound, bulky, stale after catalog fixes); replay only (fails after code changes) | 9.9 |
| RX32 | Thin-client explain / what-if | app-declared typed queries (`ICarriesSituation`, the module's own authorization, cacheable) + generic handlers `ExplainFieldHandler` / `WhatIfHandler` in YFex.Cqrs; what-if only as a key from the report | one generic `RuleAdmin.Queries.Explain(RuleSet, byte[] situation)` (untyped, bypasses the module's authorization); what-if with arbitrary values from the client | 9.7 |
| RX33 | Limit sources on the document | optional (`RecordLimit`), the app decides; replay (RX31) recovers them anyway | always persist them with the adjustments (most apps don't need it) | 9.5 |

New helpers named above and not yet in §3: `Decision.RefOf(string code)` (the rule's ref in this decision's compiled set),
`WhyReportSet`, `ToAuditDecision`, `StoredDecisions.ToWhy`, `ShouldLimit/ShouldFailOn/AndWouldPass` test helpers.

Phase mapping: RX30 and RX32 form a new **RX-P7 Server and thin clients** (after RX-P4; YFex.Cqrs, the State generator's
`ApplyServer`, YFex.Messaging.Rpc for the culture and the error formatter); RX31 joins RX-P6. RX-P7 tests: a rejection carries
reasons for the rejected fields only (Fields) and none with None; culture taken from the call; RPC round trip of
`ValidationProblem.Reasons` (golden); `ApplyServer` precedence over the local decision; generic handlers with a sanitizer; a
what-if key from another rule is refused. RX-P6 adds: replay of a stored sale equals the original fingerprint; fallback after a
`CodeVersion` change.
