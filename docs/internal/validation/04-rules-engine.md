# 04 — YFex.Rules (rule enforcement engine)

> **Names updated 2026-09-30 (D35–D38):** the engine is domain-agnostic — amounts are any `IAmount<T>`/number (not
> Money), adjustments are `Increase`/`Decrease`, publishing tiers are app-declared `[RuleTier]` fields (no built-in
> Tenant/Store), the effective-date field is `[RuleEffectiveDate]`. The sale examples below are just examples. See 13
> "Domain-agnostic refactor (D35–D38)" for the full rename map.

> Load for: **Phase 4**. Tables & runtime editing: file 05. Trace: file 06. Full example: file 12.
> Projects: `YFex/src/Validation/YFex.Rules` (runtime; refs YFex core for `Money`, `Percentual`,
> `EnumCode`, and YFex.Validation) + `YFex.Rules.Generator` (netstandard2.0, bundled as analyzer).
> Namespace: `YFex.Rules`. **Must not reference** YFex.State, YFex.Cqrs, YFex.Authorization
> (permissions are carried as strings, mapped by Cqrs/UI).

What it does: given an immutable **Situation** (Principle A), evaluates the applicable rules of a
**rule set** stage by stage, resolves conflicts per **group**, and returns a **Decision**: verdict,
limits/options per field (Principle B), adjustments (discounts) with provenance, totals,
fingerprint and optional trace. It generalizes the fiscal `IRegraNFe` pattern (D5, §11).

---

## 1. Situation (D8)

### 1.1 Why a separate type (and not the command / VM)

1. Rules need data the command doesn't carry (customer tier, actor role, store, business date).
2. The same rules run on the **UI** (situation built from VM state) and on the **server** (built from
   command + DB). A plain struct decouples rules from where data came from.
3. It is an **immutable snapshot** → deterministic, hashable (fingerprint/cache key), storable
   (audit: store situation + decision and reproduce years later).

### 1.2 Declaration

```csharp
/// Snapshot evaluated by the sale rules. Everything a rule may read MUST be here — rules never do I/O,
/// never read clocks, never read ambient context.
[Situation("sale")]                                            // "sale" = schema name used by tables/dynamic rules/catalog
public readonly partial record struct SaleSituation(
    [property: RuleTier] TenantId Tenant,       // tier 1 (app-declared; any name)
    [property: RuleTier] StoreId Store,         // tier 2
    Channel Channel,
    [property: RuleEffectiveDate] DateOnly BusinessDate,                    // selects the rule versions in force (§10); NEVER DateTime.Now in rules
    CustomerTier Tier, bool IsBirthdayMonth,                   // gathered from the customer (async, before evaluation)
    ActorLevel Actor,                                          // gathered from the authenticated user (server: from claims, never from the client)
    PaymentMethod Payment,
    [property: Requested] int Installments,                    // value the user ASKED for → auto-checked against Limit/Grant (§7 step 6)
    [property: Requested] Percentual ManualDiscount,
    ImmutableArray<SaleLineSituation> Lines)                   // item-stage rules run per line
{
    /// [Derived] = pure computed field (from other fields only). Part of the schema/Read (tables can
    /// bind "Total"), excluded from WriteTo (recomputed on read). Here: gross total before any adjustment.
    [Derived] public Money Total => SaleMath.Gross(Lines);
}

[Situation("sale.line")]
public readonly partial record struct SaleLineSituation(ProductId Product, CategoryId Category, decimal Quantity, Money UnitPrice);
```

Field attributes:
- `[RuleEffectiveDate]` — exactly one `DateOnly`/`DateTime` field; the engine picks rule versions effective at it.
- `[RuleTier]` — the next level of the app's publishing hierarchy (declaration order = level, max 4; §9).
- `[Requested]` — a value the actor chose; after all stages the engine checks it against the
  resolved limits/options for the same field and emits violations/approvals (paths = field name).
- `[Exposed(false)]` — hide from table editors / dynamic rules (e.g. internal flags). Default exposed.
- Field types: scalars supported by `RawValue` (numbers, `bool`, `string`, `DateOnly`, enums
  (`EnumCode`-aware), `Money`, `Percentual`, `Quantity`, ids that wrap a scalar) and
  `ImmutableArray<TLineSituation>` of another `[Situation]` (one level of lines).

### 1.3 Generated members

```csharp
partial record struct SaleSituation : global::YFex.Rules.ISituation<SaleSituation>
{
    /// Typed handles — code rules, tables and Limit/Grant directives refer to these, never to strings.
    public static class Fields
    {
        public static readonly SituationField<SaleSituation, TenantId> Tenant = new(0, "Tenant", ValueKind.Custom);
        public static readonly SituationField<SaleSituation, PaymentMethod> Payment = new(7, "Payment", ValueKind.EnumCode);
        public static readonly SituationField<SaleSituation, int> Installments = new(8, "Installments", ValueKind.Int32);
        // … one per field
    }

    /// The schema: a data description of the fields. Used by decision tables (column binding),
    /// the table editor (columns, kinds, enum dropdown options), dynamic rules (field lookup),
    /// publish-time checks (type of a cell vs field kind), dependency masks and the catalog.
    public static SituationSchema Schema { get; } = new("sale",
    [
        new(0, "Tenant", ValueKind.Custom, FieldRole.Scope),
        new(3, "BusinessDate", RawType.Date, FieldRole.EffectiveDate),
        new(4, "Tier", ValueKind.EnumCode, enumCodes: global::My.CustomerTierCodes.All),  // from YFex EnumCode machinery
        new(7, "Payment", ValueKind.EnumCode, enumCodes: global::My.PaymentMethodCodes.All),
        new(8, "Installments", ValueKind.Int32, FieldRole.Requested),
        new(10, "Lines", ValueKind.None, lineSchema: SaleLineSituation.Schema),
        // …
    ]);

    /// Read by id without reflection/boxing (tables & dynamic rules).
    public static RawValue Read(in SaleSituation s, ushort fieldId) => fieldId switch
    {
        7 => RawValue.FromEnum(s.Payment),
        8 => RawValue.From(s.Installments),
        // …
        _ => default,
    };
    public static bool TryGetFieldId(ReadOnlySpan<char> name, out ushort id);   // "Payment" → 7
    public static RawValue ReadLine(in SaleSituation s, int lineIndex, ushort lineFieldId);

    /// Deterministic 64-bit hash over all fields (YFex core StableHash64 over the canonical binary write).
    /// Part of query cache keys (GetSaleOptions) and stored for audit.
    public static ulong Hash(in SaleSituation s);

    /// Canonical binary form for audit/replay ("reproduce this decision years later").
    public static void WriteTo(in SaleSituation s, global::System.Buffers.IBufferWriter<byte> w);
    public static SaleSituation ReadFrom(global::System.ReadOnlySpan<byte> data);

    /// Bit i set = field i differs. Used by incremental UI evaluation (§12.3).
    public static ulong DiffMask(in SaleSituation a, in SaleSituation b);
}
```

```csharp
/// Typed handle to one field of a situation. Implicitly converts to SituationFieldRef (untyped id+name)
/// for directives. Example: d.Limit(SaleSituation.Fields.Installments, max: 6)
public readonly record struct SituationField<TSituation, T>(ushort Id, string Name, ValueKind Kind)
    where TSituation : ISituation<TSituation>;

public interface ISituation<TSelf> where TSelf : ISituation<TSelf>
{
    static abstract SituationSchema Schema { get; }
    static abstract RawValue Read(in TSelf s, ushort fieldId);
    static abstract bool TryGetFieldId(ReadOnlySpan<char> name, out ushort id);
    static abstract ulong Hash(in TSelf s);
    static abstract ulong DiffMask(in TSelf a, in TSelf b);
    static abstract void WriteTo(in TSelf s, IBufferWriter<byte> w);
}
```

## 2. Gathering data (Principle A) — where Situation values come from

**Server:** a loader per (command/query → situation). Registered with `.UseSituation<TS, TLoader>()` (09).

```csharp
/// Loads everything the rules need BEFORE evaluation. All I/O happens here.
/// Must be deterministic for the same input + same data (no DateTime.Now: take the business date
/// from the command; fall back to TimeProvider only when the command has none).
public interface ISituationLoader<in TFrom, TSituation> where TSituation : ISituation<TSituation>
{
    ValueTask<TSituation> LoadAsync(TFrom source, CancellationToken ct);
}

public sealed class SaleSituationLoader(ICustomerStore customers, IActorContext actor, ITenantContext tenant, TimeProvider clock)
    : ISituationLoader<Sale.Commands.CloseSale, SaleSituation>
{
    public async ValueTask<SaleSituation> LoadAsync(Sale.Commands.CloseSale c, CancellationToken ct)
    {
        var info = c.CustomerId is { } id ? await customers.GetLoyaltyAsync(id, ct) : LoyaltyInfo.None;
        var date = c.BusinessDate;                                  // captured by the client when the sale was opened
        return new(tenant.Id, c.Store, Channel.Pos, date,
                   info.Tier, info.BirthMonth == date.Month,
                   actor.Level,                                     // server-side identity, never trusted from the client
                   c.Payment, c.Installments, c.ManualDiscount, c.Lines.ToSituations());
    }
}
```

When a message already **carries** the situation (e.g. `GetSaleOptions(SaleSituation Situation)` from a
thin client), use the built-in `IdentitySituationLoader<TS>` (returns `message.Situation`) **plus** an
app-provided `ISituationSanitizer<TS>` (required by the loader) that overwrites trusted fields
(`[Scope]` fields, actor/role, anything security-relevant) from the server context — a client must never
be able to claim another tenant, store or role.

**UI:** the VM builds it from its own state (`BuildSituation()`, 07 §7). Async facts (customer tier)
are loaded by a separate reaction; the decision is recomputed synchronously.

**Business date rule:** the client captures `BusinessDate` **once, when the sale/document is opened**
(from `TimeProvider`), sends it in the command; the server validates plausibility (not in the future,
within the configured offline window) as a shape/context rule. Client and server therefore evaluate
the *same* date even when the command syncs hours later. Tests inject a fake `TimeProvider`.

## 3. Rules written in code

```csharp
/// A business rule over a situation. All members static abstract → called directly, inlinable.
/// Two signatures are accepted for each method (with or without RuleContext); the generator adapts.
public interface IRule<TSituation> where TSituation : ISituation<TSituation>
{
    static abstract bool Applies(in TSituation s);                                  // or (in TSituation s, in RuleContext c)
    static abstract void Evaluate(in TSituation s, ref DirectiveWriter d);           // or (in TSituation s, in RuleContext c, ref DirectiveWriter d)
}

/// Line-level rule (Items stage): evaluated once per line for which Applies is true.
public interface IRule<TSituation, TLine>
    where TSituation : ISituation<TSituation> where TLine : ISituation<TLine>
{
    static abstract bool Applies(in TSituation s, in TLine line);
    static abstract void Evaluate(in TSituation s, in TLine line, ref DirectiveWriter d);
}

/// Read-only evaluation context (ref struct: cannot escape). Gives a rule what is NOT in the situation
/// but is part of the evaluation itself.
public readonly ref struct RuleContext
{
    public DateOnly EffectiveDate { get; }   // = the [RuleEffectiveDate] field
    public Money Base { get; }               // running amount of the current stage/target (e.g. subtotal after item discounts)
    public int LineIndex { get; }            // −1 outside line rules
    public RawValue Base { get; }            // running amount, typed as the rule set's amount (c.Base.As<Money>())
    public T Get<T>(RuleParameter<T> p);     // parameter value for the current scope/version (§3.2)
}
```

### 3.1 Declaring a code rule

```csharp
/// [Rule] = stable code (persisted/audited), the group it competes in, priority inside the group,
/// Version = bump manually when the LOGIC changes (parameters/tables have their own versions),
/// so "how was the rule when it was applied" is answerable (06).
[Rule("loyalty.gold", Group = nameof(SaleRules.Loyalty), Priority = 100, Version = 1)]
[Effective(From = "2026-01-01")]                 // optional window, compared to the [RuleEffectiveDate] field
public readonly struct GoldLoyalty : IRule<SaleSituation>
{
    /// Editable parameter: code decides the logic, the tenant decides the number (05 §5).
    public static readonly RuleParameter<Percentual> DiscountRate =
        new("rate", Percentual.Of(5), ["Tenant"], guard: new(Percentual.Of(0), Percentual.Of(15)));

    public static bool Applies(in SaleSituation s) => s.Tier == CustomerTier.Gold;
    public static void Evaluate(in SaleSituation s, in RuleContext c, ref DirectiveWriter d)
        => d.Decrease(AdjustTarget.Total, c.Get(DiscountRate));
}

[Sealed]                                          // no tenant/store layer may disable, replace or loosen it
[Rule("sale.installments.boleto", Group = nameof(SaleRules.Installments))]
public readonly struct BoletoSingleInstallment : IRule<SaleSituation>
{
    public static bool Applies(in SaleSituation s) => s.Payment == PaymentMethod.Boleto;
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d)
        => d.Limit(SaleSituation.Fields.Installments, max: 1);
}
```

Applicability conditions written as `&&`-chains of simple comparisons are **split by the generator
into condition ids** for the trace ("not applied: requires Payment = CreditCard") — see 06 §4.

### 3.2 `RuleParameter<T>`

```csharp
/// A named, typed, versioned value a rule reads through RuleContext.Get. Default comes from code;
/// layers allowed by EditableAt may override it (stored/versioned like tables, 05 §5). `guard`
/// is a sealed bound: overrides outside it are rejected at publish and clamped+traced at evaluation.
public sealed class RuleParameter<T>(string name, T @default, EditableAt editable = EditableAt.None, Bounds<T>? guard = null);

/// [YFex core] (YFex/src/YFex/Values/Bounds.cs, namespace YFex) — inclusive [Min, Max] pair used for
/// guards here and reusable for input limits / Composer facets (own type; System.Range is int-index only).
public readonly record struct Bounds<T>(T Min, T Max);
```

## 4. Rule sets, stages, groups

```csharp
/// One rule set per situation type (you may have several, e.g. "sale" and "sale.return").
/// Code rules/tables/dynamic rules join a GROUP; groups belong to a STAGE; stages run in order.
[RuleSet<SaleSituation>("sale")]
public static partial class SaleRules
{
    // Stage order is explicit and total. Each stage's Base = running amount after previous stages.
    public static readonly Stage Items    = Stage.First("items");
    public static readonly Stage Subtotal = Stage.After(Items, "subtotal");
    public static readonly Stage Payment  = Stage.After(Subtotal, "payment");
    public static readonly Stage Limits   = Stage.After(Payment, "limits");       // no money effect; limits/options/approvals

    // Group = rules competing for the same thing + how the conflict is resolved.
    public static readonly RuleGroup Promotions   = new(Items,    Resolve.Exclusive(Prefer.Largest)); // only the best promo per line
    public static readonly RuleGroup Loyalty      = new(Subtotal, Resolve.Stack(cap: Percentual.Of(20)));// additive, total capped
    public static readonly RuleGroup Manual       = new(Subtotal, Resolve.MostRestrictive);             // seller discretion limit
    public static readonly RuleGroup PaymentDisc  = new(Payment,  Resolve.First);                       // highest priority wins
    public static readonly RuleGroup Installments = new(Limits,   Resolve.MostRestrictive);             // min of maxes, max of mins

    public static RoundingPolicy Rounding => RoundingPolicy.Currency(decimals: 2, MidpointRounding.AwayFromZero); // RoundingPolicy: YFex core

    /// Post-stage field: computed by the ENGINE from running totals after a stage, so it can depend on
    /// discounts (a [Derived] situation field cannot). Can be limited and [Requested]-checked like a
    /// situation field; it appears in the schema (tables may bind [Then(LimitMin, "InstallmentValue")]).
    [Requested]
    public static readonly DerivedField<SaleSituation, Money> InstallmentValue =
        DerivedField.After(Payment, "InstallmentValue",
            static (in SaleSituation s, in RunningTotals<Money> t) => s.Installments > 0 ? t.Final / s.Installments : t.Final);
}
```

Code references it as `SaleRules.InstallmentValue`; tables and dynamic rules bind it by name
(`"InstallmentValue"`) — the generator resolves column names against `Schema` ∪ the rule set's derived
fields (the situation may live in a contracts assembly that cannot see the rule set).

Resolution strategies (`Resolve`):

| Strategy | Meaning | Typical use |
|---|---|---|
| `First` | highest `Priority` applicable rule wins; others Superseded | payment-method discount |
| `All` | every applicable rule's directives apply | legal/fiscal checks, warnings |
| `MostRestrictive` | Limits: max of mins / min of maxes; Grants: intersection | installments, discretionary limits |
| `LeastRestrictive` | opposite (rare; e.g. "best entitlement among memberships") | entitlements |
| `Stack(cap)` | adjustments add up; total rate/amount capped; capped rules traced as `Capped` | loyalty stacking |
| `Exclusive(Prefer.Largest\|Business)` | evaluate all candidates, keep the single one with the highest/lowest benefit | promotions |

Generated for the rule set:

```csharp
partial class SaleRules
{
    /// Frozen definition (code rules + table bindings + groups). Built once.
    public static RuleSetDefinition<SaleSituation> Set { get; }
    /// Evaluates using the default host (code-only snapshot unless a store is configured, 05).
    public static Decision<SaleSituation> Evaluate(in SaleSituation s, in EvaluateOptions options = default);
    public static Decision<SaleSituation> Evaluate(RuleSetHost<SaleSituation> host, in SaleSituation s, in EvaluateOptions options = default);
    /// Rule catalog: id → code, group, stage, layer, editability, version, condition ids, message keys.
    public static RuleCatalog Catalog { get; }
}
```

## 5. Directives (D9)

A rule never mutates anything; it **issues directives** into a `DirectiveWriter`. The engine
resolves them per group into the Decision.

```csharp
/// Closed set of rule outputs — keep it small. [Union] non-boxing (README constraint 2).
[Union]
public readonly struct Directive : IUnion
{
    // cases: DenyDirective | WarnDirective | LimitDirective | GrantDirective | AdjustDirective | ApprovalDirective
}

/// Blocks the decision. Path may be empty (object-level) → Verdict.Denied.
/// Example: d.Deny(SaleSituation.Fields.Payment, "sale.payment.not-allowed-for-channel")
public readonly record struct DenyDirective(Violation Violation);

/// Non-blocking message (Verdict.Warned if nothing blocks).
public readonly record struct WarnDirective(Violation Violation);

/// Bounds a field. Resolved per group strategy; then [Requested] values are checked against it.
/// OnExceed decides what happens when the requested value is outside: Deny (default), Warn,
/// or RequireApproval with ApprovalPermission (e.g. "sales.discount.override").
/// Example: d.Limit(SaleSituation.Fields.ManualDiscount, max: Percentual.Of(5), onExceed: Exceed.RequireApproval, permission: "sales.discount.override")
public readonly record struct LimitDirective(SituationFieldRef Field, RawValue Min, RawValue Max,
    Exceed OnExceed = Exceed.Deny, string? ApprovalPermission = null);

/// Allowed discrete values for a field (UI picker items). Example: d.Grant(Fields.Installments, OptionSet.Range(1, 6))
public readonly record struct GrantDirective(SituationFieldRef Field, OptionSet Options);

/// Money effect. Rate is a percent or a fixed amount; the ENGINE computes the amount from the
/// current Base of the target (rules never do money math → rounding in one place).
/// Example: d.Decrease(AdjustTarget.Total, Percentual.Of(5))
///          d.Decrease(AdjustTarget.Line, line.UnitPrice)        // inside a line rule
public readonly record struct AdjustDirective(AdjustTarget Target, Rate Rate, Money? MaxAmount = null);

/// The decision is allowed only if someone with Permission approves (manager override).
public readonly record struct ApprovalDirective(string Permission, Violation Reason);

public enum Exceed : byte { Deny, Warn, RequireApproval }

/// [YFex core] (YFex/src/YFex/Rate.cs, namespace YFex) — "percent or fixed amount", reused by Finance/
/// Payment/Fiscal for fees, interest, commissions. Apply computes the amount with explicit rounding.
[Union] public readonly struct Rate : IUnion { /* cases: PercentRate(Percentual) | FixedRate(Money) */
    public static Rate Percent(Percentual p); public static Rate Fixed(Money m);
    public Money Apply(Money @base, in RoundingPolicy rounding); }

/// Where an adjustment applies. Line = current line (only valid inside line rules).
public readonly struct AdjustTarget { public static AdjustTarget Line { get; } public static AdjustTarget Subtotal { get; }
    public static AdjustTarget Named(string key); /* e.g. "freight" */ }

/// Stack-only writer the engine passes to Evaluate. Stamps each directive with the rule's RuleRef.
public ref struct DirectiveWriter
{
    public void Deny(in FieldPath path, RuleCode code, RawValue a0 = default);
    public void Warn(in FieldPath path, RuleCode code, RawValue a0 = default);
    public void Limit<TS, T>(SituationField<TS, T> field, T? min = default, T? max = default,
                             Exceed onExceed = Exceed.Deny, string? permission = null) where TS : ISituation<TS>;
    public void Grant<TS, T>(SituationField<TS, T> field, in OptionSet options) where TS : ISituation<TS>;
    public void Adjust(AdjustTarget target, Rate rate, Money? maxAmount = null);
    public void RequireApproval(string permission, in FieldPath path, RuleCode reason);
}
```

## 6. Decision

```csharp
/// Immutable result of one evaluation. A class (one allocation per evaluation is fine: evaluations
/// happen per user action / per command). Equality = Fingerprint equality → safe as an [Observable]
/// VM property without UI churn.
public sealed class Decision<TSituation> : IEquatable<Decision<TSituation>> where TSituation : ISituation<TSituation>
{
    public TSituation Situation { get; }
    public Verdict Verdict { get; }
    public ViolationList Violations { get; }                 // Deny/Warn + [Requested] checks, paths = situation field names
    public ApprovalRequestList Approvals { get; }            // which permissions are needed and why
    public DecisionTotals Totals { get; }                    // per line + subtotal + final, after all stages
    public AdjustmentList Adjustments { get; }               // AppliedAdjustment[] — ALWAYS present (06 §2)
    public RulesetVersion RulesetVersion { get; }            // code version + data version used
    public ulong Fingerprint { get; }                        // hash of the OUTCOME (§8) — not of the trace
    public DecisionTrace? Trace { get; }                     // null unless TraceLevel > Off (06)

    // Constraints (Principle B)
    public bool TryGetLimit(SituationFieldRef field, out RawValue min, out RawValue max);
    public T? Max<T>(SituationField<TSituation, T> field);   // convenience for bindings: Policy.Max(Fields.Installments)
    public T? Min<T>(SituationField<TSituation, T> field);
    public OptionSet Options(SituationFieldRef field);
    /// FieldConstraints for a situation field — what the UI merges into IValidationSource (07 §7.3).
    public FieldConstraints ConstraintsFor(SituationFieldRef field);

    /// Human explanation built from Adjustments (+Trace when present) and a message catalog (06 §5).
    public Explanation Explain(IMessageCatalog catalog, CultureInfo culture);
}

/// [Union] verdict. Distinct case types (README constraint 2).
[Union] public readonly struct Verdict : IUnion
{
    // Allowed | Warned(ViolationList) | NeedsApproval(ApprovalRequestList) | Denied(ViolationList)
    public bool IsAllowed { get; }        // Allowed or Warned
    public bool IsNeedsApproval { get; }
    public bool IsDenied { get; }
}

public readonly struct DecisionTotals
{
    public Money Gross { get; }                       // Σ lines before any adjustment
    public Money Final { get; }
    public ReadOnlySpan<LineTotal> Lines { get; }     // per line: gross, item adjustments, ALLOCATED subtotal/payment adjustments (⚑P9), net
    public Money AdjustmentsTotal { get; }
}
```

## 7. Evaluation algorithm (normative)

`Evaluate(in s, options)`:

1. **Resolve the compiled rule set**: `host.Resolve(scope from [Scope] fields, asOf from [RuleEffectiveDate] field)`
   → `CompiledRuleSet<TS>` = code rules + table versions + parameter versions + dynamic rules of every
   layer in scope, effective at `asOf` (§10). Cached per (scope, version set).
2. **Init running amounts**: for each line `Base = Quantity × UnitPrice` (rounded by `Rounding`);
   `Subtotal = Σ lines`.
3. **For each stage in order**, for each group of the stage:
   1. **Candidates**: pre-filter with the applicability index (§12.1), then call `Applies`
      (line rules: per line). Not applicable → trace `NotApplicable(conditionId)`.
   2. **Evaluate** each candidate into a scratch `DirectiveWriter` (stamped with its `RuleRef`).
   3. **Resolve** per strategy (§4): pick/merge directives; losers traced `Superseded`/`Capped`.
   4. **Apply adjustments**: amount = rate × current base of the target (or fixed), clamp by
      `MaxAmount` and group cap, round, never below zero; update running amounts; record
      `AppliedAdjustment` (rule ref, stage, target, base, rate, amount, note).
   5. **Accumulate** limits, grants, denies, warns, approvals.
   6. After the stage's last group: compute the rule set's `DerivedField`s declared `After(thisStage)`.
4. **Allocate** subtotal/payment-stage adjustments to lines proportionally to each line's current
   base; remainder cents go to the line with the largest base (deterministic tie-break: lowest index).
   Needed for NF-e `vDesc` per item (⚑P9).
5. **Guardrails**: re-check sealed bounds (a table row/parameter somehow outside) → clamp + trace
   `Overridden(by Sealed)`.
6. **Check `[Requested]` fields** (situation fields and `[Requested]` derived fields) against final limits/options: outside → per `OnExceed`: Deny →
   violation `rules.limit.max|min|options` with args (limit), path = field name; Warn → warning;
   RequireApproval → approval request.
7. **Verdict**: any Deny → Denied; else any approval → NeedsApproval; else any warning → Warned; else Allowed.
8. **Fingerprint** (§8), **Trace** (if requested), build Decision.

Determinism rules: stable iteration orders (stage order, group declaration order, priority desc,
then rule code ordinal), no hash-set iteration, no floating point for money.

## 8. Fingerprint

64-bit `StableHash64` (YFex core, `YFex.Helpers`) over a canonical binary write of the **outcome**: verdict kind; limits (field id, min,
max) sorted by field id; options; adjustments (rule code, target, amount) in application order;
approval permissions; violation (path, code) pairs. **Excludes** the trace and the ruleset version
(same outcome under a newer version still matches — that is what offline replay wants, 09 §8).

Differences with Trace → 06 §1.

## 9. Layers, editability, guardrails (D12)

```csharp
public enum rule tier : byte { Code = 0, Tenant = 1, Store = 2, Composer = 3 }
[Flags] public enum EditableAt : byte { None = 0, Tenant = 1, Store = 2 }
```

| Layer | Who changes it | How | Example |
|---|---|---|---|
| **Sealed code** (`[Sealed]`) | developers | code + deploy | "Boleto = 1x", "installment ≥ R$5", legal caps |
| **Code** (not sealed) | developers; tenants may **disable/replace by code** if allowed | code + deploy | default loyalty logic |
| **Tenant** | tenant admin with the table's/parameter's publish permission | table editor / parameter editor → publish | installment table, loyalty %, promos |
| **Store** | store manager with store publish permission | same editor scoped to a store | local promos |
| **User discretion** | nobody edits rules at runtime; the **limit depends on the actor** (`Actor` field) | a table keyed by `ActorLevel` | seller ≤5%, manager ≤15%, above → approval |

Resolution across tiers (for the same rule code or table code): **deepest tier > … > tier 1 > code** along the
situation's `RuleTierPath`, except:
- `[Sealed]` rules/rows cannot be disabled, replaced or loosened;
- **guardrails** (`IRowGuard<TRow>`, parameter `guard`) are enforced at **publish** (reject) and at
  **evaluation** (clamp + trace), so a corrupted store can never exceed them;
- a layer can **add** rules to a group, **disable** a non-sealed rule by code, or **replace** a rule by
  code with a table/dynamic rule of the same code.

Permissions are strings (`"sales.rules.installments.publish"`) on table/parameter declarations;
YFex.Cqrs checks them with YFex.Authorization on the publish command (05 §4).

Scope resolution (most specific wins) is the same problem `YFex.CustomLogic` solves in
`CustomLogicRegistry`; composer-design §16 already plans extracting a reusable `ScopedTable<TKey, TEntry>`
into YFex.Tenancy — **reuse it** here instead of writing a second scoped lookup (if not extracted yet,
extract it as part of Phase 4b).

## 10. Versions, effective dating, snapshots

```csharp
/// Code version (assembly build id + each code rule's Version) + data version (monotonic id of the
/// set of published table/parameter/dynamic versions used). Stored with every decision.
public readonly record struct RulesetVersion(ulong CodeVersion, long DataVersion);

/// Immutable published content of ONE table/parameter set/dynamic rule for ONE scope, effective
/// from a date. Never edited — changes create a new version (05 §4).
public sealed class RulesetSnapshot { public long Version; public DateOnly EffectiveFrom; public RuleTierPath Scope; … }

/// Holds compiled rule sets per (scope, effective window); resolves by asOf; hot-swaps atomically
/// when a new version is published (Interlocked exchange of a frozen array). Clients keep the last
/// N versions (enough for the offline window) so as-of evaluation works offline.
public sealed class RuleSetHost<TSituation> where TSituation : ISituation<TSituation>
{
    public CompiledRuleSet<TSituation> Resolve(in RuleTierPath scope, DateOnly asOf);
    public void Publish(RulesetSnapshot snapshot);             // called by the store/distribution layer (05)
}
```

## 11. Generalizing the fiscal pattern (D5)

| `IRegraNFe` today | YFex.Rules |
|---|---|
| `Codigo` | `[Rule("nfe.539")]` |
| `Faixa` (`FaixaNt`: NT version range) | `Applies` on an `NtVersao` situation field **or** `[Effective]` by date — prefer a situation field because NT versions are not dates |
| `Modelo` (55/65 filter) | `Applies(in s) => s.Modelo == ModeloDocumento.NFCe` (condition split gives "requires model 65" in trace) |
| `RequerEstadoExterno` | not a rule concern: the loader gathers the external state into the situation |
| `Check(ctx) → IEnumerable<ViolacaoRegra>` | `Evaluate(in s, ref DirectiveWriter d)` → `d.Deny(path, "nfe.539", cStat)` / `d.Warn(...)` |
| `NFeRuleContext(Comando, Versao)` | `[Situation("nfe")] NFeSituation` (built from the command by a loader) |
| `IConjuntoRegras` / resolver | `[RuleSet<NFeSituation>("nfe")]` with group `Resolve.All` |

Other use cases the same engine serves: shipping fees, commissions, credit approval, price lists,
fiscal CFOP/CST/tax-rule selection (`Resolve.First` with specificity), return policies.

## 12. Performance

### 12.1 Applicability index
At compile/freeze time, for rules whose split conditions include equality on enum/`EnumCode`/small-int
fields, build `ulong` bitsets per (field, value) → candidate mask = AND of masks; scan set bits with
`BitOperations.TrailingZeroCount` (optional `unsafe` pointer walk for > 64 rules). Rules without
indexable conditions are always candidates.

### 12.2 Allocation
Evaluation state lives in a pooled `EvaluationScratch` (arrays rented, returned after building the
Decision). The Decision allocates exact-size arrays once. `RuleContext`/`DirectiveWriter` are ref
structs. For bulk scenarios (re-pricing 10k items) use `RuleEvaluator<TS>` (reusable scratch).

### 12.3 Incremental UI evaluation (optimization, after v1)
The generator records which situation fields each rule reads (`ulong` mask). With
`DiffMask(prev, next)`, rules whose masks don't intersect keep their previous directives; resolution
re-runs. v1 may skip this: a full evaluation is microseconds for realistic rule counts.

## 13. Tests

- Scenario tests per rule set with `YFex.Validation.Testing` helpers:
  `SaleRules.Evaluate(s with { … }).ShouldApply("loyalty.gold").ShouldNotApply("promo.bf", because: TraceOutcome.Superseded)`.
- Determinism test: same situation ×1000 → same fingerprint; shuffled table rows with `First` policy
  → documented order only.
- Allocation budget test for evaluation (one Decision + exact arrays).
- Rounding/allocation tests: remainders, zero bases, 100% discount, negative protection.
- Layer tests: sealed not overridable; guardrail clamp; store beats tenant.

## 14. Supporting types (reserved names — shapes are guidance, keep them minimal)

| Type | Kind | Purpose |
|---|---|---|
| `tier` | enum | `Tenant, Store, Channel, User` — used by `[Scope]` |
| `RuleTierPath` | readonly struct | up to 4 tier values built from the situation's `[RuleTier]` fields; `RuleTierPath.Code` = code tier |
| `FieldRole` | enum | `Normal, Tier, EffectiveDate, Requested, Derived, Lines` — flag on `SituationFieldInfo` |
| `SituationSchema` / `SituationFieldInfo` | sealed class / readonly struct | name, fields (id, name, `ValueKind`, role, enum codes, `Exposed`, line schema) |
| `SituationFieldRef` | readonly record struct | untyped `(ushort Id, string Name)`; implicit from `SituationField<TS,T>` and `DerivedField<TS,T>` |
| `DerivedField<TS,T>` + `DerivedField.After(stage, name, fn)` | sealed class | post-stage computed field; `fn` is `static` delegate `(in TS, in RunningTotals) → T` |
| `RunningTotals` | readonly ref struct | running gross/subtotal/final and per-line bases during evaluation |
| `RoundingPolicy` | readonly struct — **YFex core** | decimals + `MidpointRounding`; `Currency(…)` factory; also used by `Rate.Apply` |
| `StageRef` | readonly record struct | `(byte Order, string Code)` — persisted by code |
| `RuleSetDefinition<TS>` | sealed class | frozen code part of a rule set (stages, groups, code rules, table bindings, derived fields) |
| `CompiledRuleSet<TS>` | sealed class | definition + resolved data versions for one (scope, asOf); has `Evaluate` |
| `RuleCatalog` / `RuleIds` | sealed class / generated consts | id ↔ code, group, stage, layer, editability, version, condition count, message keys |
| `ConditionId` | readonly record struct | `(ushort RuleId, byte Index)`; `None` |
| `AdjustmentList` | readonly struct | exact-size `AppliedAdjustment[]` with struct enumerator |
| `LineTotal` / `LineAllocation` | readonly structs | per-line gross/adjustments/net; per-line share of a subtotal/payment adjustment |
| `ApprovalRequest` / `ApprovalRequestList` | readonly struct / list | `(string Permission, Violation Reason, RuleRef Rule)` |
| `OverrideToken` | readonly record struct | signed `(approverId, permission, fingerprint, expiresAt, signature)` — issued by `RuleAdmin.Commands.ApproveDecision` (09 §6) |
| `EvaluationScratch` / `RuleEvaluator<TS>` | pooled class / reusable evaluator | buffers for evaluation; bulk mode |
| `IdentitySituationLoader<TS>` / `ISituationSanitizer<TS>` | class / interface | loader for messages that carry a situation + mandatory sanitizer (`TS Sanitize(in TS s)`) |
| `SituationPathMap` | builder | `map.Path("Lines[]", "Items[]")` — situation → message path renames for `.UseSituation` |
| `IDecisionTable<TS,TRow>` | interface (generated impl) | static `Schema`, `Freeze`, `Check`, `Evaluate`, `RowsJson` (05 §2) |
| `FrozenTable<TRow>` / `TableSchema` / `RowMeta` | classes/structs | frozen index; column metadata; per-row metadata (05) |
| `TableVersionInfo` / `TableVersionPayload` | records | version header (no rows) / header + serialized rows |
| `RuleOverrideAction` | [Union] | `Disable` \| `ReplaceWith(string code)` |
| `RuleDefinitionView` | record | display model of a rule definition at a version (06 §6) |
