# 06 — Fingerprint, applied adjustments, trace, explanation

> **Names updated 2026-09-30 (D35–D38):** the engine is domain-agnostic — amounts are any `IAmount<T>`/number (not
> Money), adjustments are `Increase`/`Decrease`, publishing tiers are app-declared `[RuleTier]` fields (no built-in
> Tenant/Store), the effective-date field is `[RuleEffectiveDate]`. The sale examples below are just examples. See 13
> "Domain-agnostic refactor (D35–D38)" for the full rename map.

> Load for: **Phase 4** (engine side) and **Phase 5** (UI `YRuleBreakdown`).
> Goal (D17): for any rule set (discounts, shipping, commission, fiscal…), know **exactly what was
> applied, why, and how each rule looked when it was applied**, and show it to end users.

---

## 1. Fingerprint vs Trace

| | **Fingerprint** | **Trace** |
|---|---|---|
| Analogy | checksum of a receipt | the itemized receipt with notes |
| What | 64-bit hash of the Decision **outcome** (04 §8) | full story: considered / applied / skipped (why) / superseded / capped / overridden, with bases and amounts |
| Question | "Did client and server reach the **same** decision?" | "**Why** R$ 57,50? Why didn't Black Friday apply?" |
| Cost | always computed, no allocation | allocates; opt-in level |
| Used for | offline replay comparison, cache keys, change detection, `Decision` equality | UI breakdown, support, audit, tests |
| Explains? | no | yes |

Between them sits the **always-on** part that is business data, not debugging:
`Decision.Adjustments` (§2).

## 2. `AppliedAdjustment` — always recorded, persisted

```csharp
/// Stable reference to the exact rule definition that acted. Persist the CODE (stable across
/// builds), never the in-memory numeric id.
///   code rule   → ("loyalty.gold", Version: 1 (from [Rule(Version=…)]), Layer.Code, DataVersion: parameters version)
///   table row   → ("sale.installments", RowId, Layer.Tenant, DataVersion: table version 42)
///   dynamic     → ("promo.store12.bf", Version: spec version, Layer.Composer)
public readonly record struct RuleRef(string Code, int Version, rule tier Layer, long DataVersion, Guid RowId = default);

/// One money effect, with everything needed to print it, audit it and rebuild the fiscal vDesc.
public readonly struct AppliedAdjustment
{
    public RuleRef Rule { get; }
    public StageRef Stage { get; }              // "items" | "subtotal" | "payment"
    public AdjustTarget Target { get; }         // line index or subtotal/named
    public int Order { get; }                   // application order (1..n) — explains stacking
    public Money Base { get; }                  // amount it was computed from at that moment
    public Rate Rate { get; }                   // 5% or R$ 50 fixed
    public Money Amount { get; }                // resulting (rounded, capped) discount
    public ResolutionNote Note { get; }         // why the amount might differ from Rate × Base
    public ReadOnlySpan<LineAllocation> Allocations { get; } // subtotal/payment adjustments split per line (⚑P9)
}

/// [Union] note attached to an applied adjustment.
[Union] public readonly struct ResolutionNote : IUnion
// None | CappedBy(RuleRef capOwnerOrGroup, Rate requested, Rate applied) | StackedWith(RuleRef other)
// | ClampedByGuardrail(RuleRef guard) | ApprovedBy(string userId, string permission)
```

**Persistence (app responsibility, shape recommended):** a `SaleAdjustment` table/child collection with
`(SaleId, Order, RuleCode, RuleVersion, Layer, DataVersion, RowId, Stage, TargetLine, Base, RateKind,
RateValue, Amount, NoteKind, NoteData)` and per-line allocations. Store also the Decision
`Fingerprint`, `RulesetVersion` and the canonical `Situation` bytes (`WriteTo`) for full replay.

## 3. Trace levels

```csharp
public enum TraceLevel : byte
{
    Off = 0,       // default on the server hot path: Adjustments only
    Applied = 1,   // + one TraceEntry per applied rule with its directives
    Full = 2,      // + not-applicable (failed condition), superseded, capped, overridden, denied — "why not"
}

public readonly struct EvaluateOptions
{
    public TraceLevel Trace { get; init; }
}

public sealed class DecisionTrace
{
    public ReadOnlySpan<TraceEntry> Entries { get; }
}

public readonly struct TraceEntry
{
    public RuleRef Rule { get; }
    public StageRef Stage { get; }
    public int LineIndex { get; }                 // −1 when not a line rule
    public TraceOutcome Outcome { get; }
    public ReadOnlySpan<Directive> Directives { get; }  // what it issued (Applied/Superseded/Capped)
}

[Union] public readonly struct TraceOutcome : IUnion
// Applied
// | NotApplicable(ConditionId failed)             — which condition was false (see §4)
// | Superseded(RuleRef by)                        — lost an Exclusive/First resolution
// | Capped(Rate from, Rate to)                    — reduced by a Stack cap / MaxAmount
// | Overridden(int ByTier)                        — disabled/replaced by data of a deeper rule tier
// | Denied(Violation)                             — issued a Deny
// | NotEffective(DateOnly asOf)                   — outside its [Effective]/version window
```

UI recommendation: POS screens use `Full` (a few microseconds more, only per user action); the server
command pipeline uses `Off` unless auditing is configured (`EnforceOptions.Trace`).

## 4. Condition splitting — how the generator makes "why not" explainable

For expression-bodied (or single-`return`) `Applies` methods made of `&&`-chains of simple
comparisons (`==`, `!=`, `<`, `<=`, `>`, `>=`, `is` patterns, bool members, `Contains` on static
sets), the generator assigns **condition ids** and emits a diagnostic companion:

```csharp
// user
[Rule("installments.card.12x", Group = nameof(SaleRules.Installments))]
public readonly struct Card12x : IRule<SaleSituation>
{
    public static bool Applies(in SaleSituation s) => s.Payment == PaymentMethod.CreditCard && s.Total >= Money.Of(1000);
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d) => d.Limit(SaleSituation.Fields.Installments, max: 12);
}

// generated (catalog + explainer)
partial class SaleRules
{
    // C0: "Payment == CreditCard"   → message key "rules.cond.eq"   args: [field "Payment", value CreditCard]
    // C1: "Total >= 1000"           → message key "rules.cond.gte"  args: [field "Total", value R$ 1000]
    private static ConditionId __Explain_Card12x(in SaleSituation s)
    {
        if (!(s.Payment == PaymentMethod.CreditCard)) return new(RuleIds.Card12x, 0);
        if (!(s.Total >= Money.Of(1000))) return new(RuleIds.Card12x, 1);
        return ConditionId.None;
    }
}
```

`__Explain_*` is called **only** when `Applies` returned false and `TraceLevel.Full` is on.
Non-splittable bodies get a single condition `C0: custom` whose message key is
`rules.<code>.cond` (the developer provides text in the module catalog).
Decision tables need no splitting: the failing **column** of the closest row is the reason.
Dynamic rules: each AST node is a condition.

Messages for conditions are generic templates: `"requer {field} = {value}"`, `"requer {field} ≥ {value}"`
with field display names and value formatting from the catalog (10).

## 5. Explanation (display model)

```csharp
/// Framework-neutral display model built from Adjustments (+Trace) and a message catalog.
/// UI controls (YRuleBreakdown) only render this — they never interpret rules.
public sealed class Explanation
{
    public ImmutableArray<ExplanationLine> Applied { get; }      // in application order
    public ImmutableArray<ExplanationLine> NotApplied { get; }   // Full trace only
    public ImmutableArray<ExplanationLine> Limits { get; }       // "Parcelas: até 6x (tabela Parcelamento v42)"
    public ImmutableArray<ExplanationLine> Approvals { get; }
    public Money Gross { get; } public Money Final { get; }
}

public readonly record struct ExplanationLine(
    string Title,            // "Cliente Gold 5%"   (catalog: rule display name + args)
    string? Detail,          // "acumulável, teto 20% ✓" / "exclusiva com Leve 3 Pague 2 (benefício menor)"
    RuleRef Rule,            // for "view rule as it was" (§6)
    StageRef Stage,
    int? LineIndex,
    Money? Amount,
    ExplanationKind Kind);   // Applied | NotApplied | Limit | Approval | Warning
```

Example rendered by `YRuleBreakdown` (08 §6):

```
Subtotal                                                 R$ 1.200,00
  − Leve 3 Pague 2 (Coca 2L)      promo.l3p2 · v7 · Loja      − R$ 50,00   [itens]
  − Cliente Gold 5%               loyalty.gold · v3 · Tenant  − R$ 57,50   [subtotal, acumulável]
  − Aniversariante 3%             loyalty.bday · v1 · Tenant  − R$ 34,50   [subtotal, acumulável, teto 20% ✓]
  − Pix 2%                        payment.pix · v2 · Tenant   − R$ 21,16   [pagamento]
Total                                                    R$ 1.036,84
Não aplicados
  ✗ Black Friday 10%   — exclusiva com "Leve 3 Pague 2" (benefício menor: R$ 105 < R$ 110)
  ✗ 12x sem juros      — requer pagamento com cartão de crédito
```

## 6. "How was the rule when it was applied?"

Every `RuleRef` carries code + version + layer + data version (+ row id). The query
`RuleAdmin.Queries.GetRuleDefinition(RuleRef)` (05 §4.1) returns the exact definition:
- table row → the row as stored in table version N;
- parameters → the parameter values of version N;
- dynamic rule → the `RuleSpec` of version N (with its messages);
- code rule → the catalog entry for that code + `Version` (description text from the catalog;
  code logic itself is versioned by the app build — keep the `Version` bump discipline).

UI: clicking a line in the breakdown opens "Regra como aplicada em 29/09/2026: …".

## 7. Test helpers (YFex.Validation.Testing)

```csharp
var d = SaleRules.Evaluate(s, new() { Trace = TraceLevel.Full });
d.ShouldApply("loyalty.gold").WithAmount(Money.Of(57.50m));
d.ShouldNotApply("promo.blackfriday", because: TraceOutcome.Superseded);
d.ShouldNotApply("installments.card.12x", failedCondition: 0);
d.Fingerprint.ShouldBe(expectedFromServer);
```
