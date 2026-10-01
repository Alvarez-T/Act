# YFex.Composer — End-User Screen & Logic Composition (Design Proposal)

> Status: **proposal / evaluation — revision 2** (2026-09-30). Working name `YFex.Composer` (placeholder).
> Sibling docs: `capabilities-design.md`, `custom-logic-design.md`, `authorization-design.md`,
> **`validation/README.md`** (YFex.Validation + YFex.Rules, Phases 0–4 + 6B/C implemented).
> Rev 1 (2026-09-29) was written before YFex.Validation/YFex.Rules existed. Rev 2 re-bases Composer on them;
> see §0.1 for the delta.

---

## 0. TL;DR — verdict

The idea is good, and now even cheaper to build. The parts that rev 1 planned to write from scratch
already exist and are tested:

| Rev 1 planned to build | Now reuse (implemented) |
|---|---|
| `IFieldValue<TSelf>`, `ValueKind`, `Value` union | **YFex core** `IRawValue<TSelf>` + `RawType` + `RawValue` (32 B, typed, non-boxing) — D35 |
| `FieldFacets` | **`FieldConstraints`** (required, lengths, min/max/step as `RawValue`, `OptionSet`, format) + `Bounds<T>`/`OptionSet` in core |
| Own Excel-like formula language | **`YFex.Rules.Dynamic` expression core**: `Expr` AST (JsonLogic-like JSON), type binder with JSON-path diagnostics, `FunctionCatalog` (+ `[RuleFunctions]` generator, contracts), postfix `DynProgram`, totality, size limits, `ExprText` |
| Validation of composed inputs | **`YFex.Validation`**: `Violation` (code + `FieldPath` + `RawValue` args), `IValidatable<T>` static constraints, `IValidationSource` (the UI constraint channel), `ValidationBag` |
| Message/label localization | **`YFex.Localization`** message catalog layers (dynamic → tenant → app → module → built-in) |
| Business logic in screens | **`YFex.Rules`** (situations, directives, decisions, tiers, guardrails, approvals, trace) + **`Rules.Admin`** (enact, versions, audit diff, what-if, device sync) |
| Parameter schemas for AI | `IValidatable<T>.Shape` → **`JsonSchemaWriter`**, `JsonSchemas` registry, Capabilities **`ParametersSchema`**, `SituationJsonSchema`, `SpecJsonSchema` |
| Server-side enforcement & identity | CQRS stages 1–4 (`IValidatedCommandHandler`, `IRuledCommandHandler`), **`CallContext`** over RPC (auth + `ScopeIdentity`) — D24/D29/D33 |

The three rev-1 recommendations stand, with sharper wording:

1. **The product is the IR, not the DLL.** Interpreter first, compiled DLL as an optional,
   benchmark-gated tier. Dynamic rules measure about 0.4–10 µs per rule evaluation and about 60 µs
   per compile, which weakens the case for Tier 2 even further.
2. **The end user composes catalog items; they don't code.** One **expression language** serves
   both screen formulas *and* business rules, so the user and the AI learn one dialect.
3. **Every designer action is an edit op on the IR.** That gives one validator, one diff, one audit
   log, and the same `generate → check → simulate → a person enacts` loop that Rules.Dynamic
   already uses.

And one new principle that Validation/Rules makes possible:

4. **A screen can make things stricter or friendlier, never more permissive.** Anything that *must*
   hold (limits, discounts, approvals) is a **Rule** (server-enforced, tiered, audited). Screens hold
   UI behavior and extra input checks only. The designer can *author* rules, but they are enacted
   into YFex.Rules, not into the screen.

### 0.1 Revision 2 delta

| § | Change |
|---|---|
| 5–6 | Value system = core `RawType`/`RawValue`/`IRawValue<T>`; field type = `RawType` + `FieldConstraints` + UI-only `DisplayHints`. `IFieldValue`/`Value` removed. |
| 6.4 | Unions follow the ecosystem rule from validation/README "Hard constraints" #2: **`[Union]` tagged struct everywhere**, no native `union` keyword (also on cold paths, for consistency). |
| 7 | Generated shapes: the *descriptive* half exists (`TypeShape`, `TryGetConstraints`, JSON Schema). Composer's generator now only adds **field access + construction + invokers**. |
| 8 | Logic split into **three kinds** (behavior / input checks / business rules). Formula language = extracted **`YFex.Expressions`** (from Rules.Dynamic), not a new language. |
| 8.1 | Slot store simplified to `RawValue[]` (the engine's currency) instead of typed SoA columns. |
| 8.6 | New: live **Decisions** in composed screens (limits/options/approvals from rule sets). |
| 9 | Renderers bind constraints/violations through **`IValidationSource`** (same projection as validation/08 Avalonia). |
| 11 | Store/lifecycle mirrors **Rules.Admin** (`RulePublisher`, `IRuleStore`, audit diff, device cache), extracted into a shared **`YFex.Revisions`** (verb **Enact**) at Composer P3 (C4/C5). |
| 12 | AI loop aligned with Rules.Dynamic (`CatalogJson`, `SpecJsonSchema`, `Check`, `Simulate`). |
| 15–16 | Roadmap re-sequenced. Gap table: two gaps closed, several new ones (expression extraction, runtime situations, record field access, grid). |

### 0.2 Decisions log

| # | Decision | Date |
|---|---|---|
| C1 | **Phase X approved:** extract `YFex.Expressions` from `YFex.Rules.Dynamic`. Rules.Dynamic keeps the rule envelope (`RuleSpec`, directives, group policies, caps, tiers, codec) and depends on Expressions; Composer uses Expressions directly. Function catalogs are layered (deterministic shared core → rule layer → screen-only layer such as `today()`, rejected by the binder inside rules). | 2026-10-01 |
| C2 | **Promote-to-rule is explicit, never automatic.** The designer offers "enforce everywhere?" only when the preconditions hold (open `RuleGroup.Dynamic` group allowing `deny`, all referenced fields exposed in the situation, user holds the rule permission), shows a what-if first, then enacts through Rules.Admin. Users without rule permission never see the action; their screen checks stay UX-only ("never" fallback). | 2026-10-01 |
| C3 | **Record read/construct lives in YFex.Validation**, opted in with a **separate `[RecordAccess]` attribute** (not a property on `[Validatable]`, not automatic). The Validation generator emits `IRecordAccess<T>` with the same `FieldIds`/`TypeShape` as validation. A type can have `[RecordAccess]` without any validation rules (query rows). `[RecordAccess(Construct = false)]` = read-only. Dispatch-by-key invokers come from the CQRS generator, not Composer (§7.2). | 2026-10-01 |
| C4 | The generic runtime-configuration lifecycle library is **`YFex.Revisions`**, and the verb for "put a version in force" is **Enact**. "Publish" is avoided: it clashes with `dotnet publish` and `IDispatcher.PublishAsync`. Vocabulary in §11.1. Extracted from Rules.Admin when Composer reaches P3 (two consumers by then). | 2026-10-01 |
| C5 | **Rules.Admin adopts the Enact vocabulary** during the extraction (hard rename, all in-repo, same reasoning as D28): `PublishTable/PublishParameters/PublishRule` → `EnactTable/EnactParameters/EnactRule`, `RulePublisher` → built on `Enactor`, `RulesetPublished` → `RevisionEnacted` (or `RulesetEnacted` if a rules-specific event is kept), permission `rules.dynamic.publish` → `rules.dynamic.enact`, option `RulePublishPermission` → `RuleEnactPermission`. | 2026-10-01 |

---

## 1. Scope

### Goals
- End users build screens (forms, lists, master-detail, dashboards) and attach logic (behavior,
  input checks, action flows) **without code**, across Avalonia / Blazor / WPF / Uno.
- Use existing developer-authored commands/queries/jobs/events/rule sets as building blocks, with
  their contracts, constraints, permissions and descriptions.
- Add **new fields** (extension fields) to existing objects.
- Act as the **authoring UI for YFex.Rules** (decision tables, parameters, dynamic rules), which
  validation/13 lists as "Composer authoring UI: later".
- Reuse Authorization, Tenancy, Capabilities, CQRS, Messaging, Jobs, State, NavigatR, Validation,
  Rules, Localization.
- Zero domain knowledge. AI-ready from day one.

### Non-goals
- No general-purpose scripting for end users. Expressions are total (Rules.Dynamic semantics:
  ÷0/overflow → null, size limits).
- No pixel/absolute layout. Layout is semantic.
- No end-user-defined new commands/aggregates. No end-user schema migrations (extension store).
- **No business enforcement inside screens** (principle 4). It goes to YFex.Rules.

### Hard constraints (adopted from validation/README so the ecosystem stays uniform)
AOT + trimming clean; no reflection/`Expression.Compile`/`dynamic`; `[Union]` tagged structs; spans
and own types, **no LINQ in library code**; `unsafe` only where it measurably pays; no new
third-party dependencies beyond what YFex already brings (MemoryPack via YFex.Persistence is fine;
Roslyn only in the server-side Tier-2 project); generators `netstandard2.0` with the pinned Roslyn
version; runtime `net11.0`, `LangVersion preview`.

---

## 2. Core model: Catalog vs Composition

```
 DEV TIME (compile)                         RUNTIME (data)
 ──────────────────────────                 ───────────────────────────────────────────────────
 IValidatable<T> shapes + constraints ┐
 ISituation<T> schemas, rule sets      │
 [Composable] access/invokers          ├─► CATALOG ──(filtered by auth/tenant)──► DESIGNER / AI
 ICapabilityProfile (+ParametersSchema)│    (frozen, stable keys)                    │ ScreenEdit ops
 FunctionCatalog ([RuleFunctions])     │         ▲                                   ▼
 IRawValue<T> types, widgets, [Job]  ──┘         │                    SCREEN DEFINITION (IR, versioned)
                                                 │ resolve                           │ compile
                                                 └───────────────────────────────────┤
                                                                                     ▼
                                                                           SCREEN PLAN (frozen)
                                                                   Tier 1 interpreter │ Tier 2 DLL (opt.)
                                                                                     ▼
                                    RuntimeScreen : StateObject, IValidationSource ──► Renderer adapter
                                         │ dispatch (CQRS stages 1–4 on server)        (Avalonia/Blazor/…)
                                         └─ live Decisions (YFex.Rules) ─► constraints/approvals/explanations
```

- **Catalog**: the allowed vocabulary, generated and frozen. It now also lists **rule sets** (open
  groups, `CatalogJson`), **function catalogs** and **validatable contracts**.
- **Definition (IR)**: what the user authored. Plain data, scoped, versioned, immutable once
  enacted.
- **Plan**: the definition bound against the current catalog. Names are resolved to ids,
  expressions are bound and compiled (`YFex.Expressions`), slots are assigned, and the dependency
  graph is built.

---

## 3. Execution strategy — options and tradeoffs

| Option | Perf (steady) | Startup | Platforms | Complexity | Security |
|---|---|---|---|---|---|
| **A. Interpreter over compiled plan** (postfix programs over `RawValue`, slot store) | ★★★★ — measured in Rules.Dynamic: false condition ≈ 0.4 µs, complex rule ≈ 6–10 µs | ★★★★★ ≈ 60 µs/expression compile, cached per version | **All** incl. NativeAOT/iOS/WASM-AOT | Medium (engine exists) | ★★★★★ |
| B. Client-side Roslyn → DLL | ★★★★★ | ★ 1–4 s | JIT only | High | ★★ |
| **C. Server-side Roslyn → signed DLL** (real YFex generators via `CSharpGeneratorDriver`) | ★★★★★ | ★★★★ | JIT desktop / server / WASM-interp; not NativeAOT/iOS | High | ★★★ |
| D. Expression trees / DynamicMethod | ★★★★ | ★★★ | Violates hard constraints | — | — |
| **E. Developer "eject"** (emit C# into a dev repo) | ★★★★★ | n/a | All | Low once the emitter exists | ★★★★★ |

**Recommendation: A → E → (C only if benchmarks demand it).** Same reasoning as rev 1 (I/O-bound
screens, host-upgrade staleness, AOT, code-loading risk), now backed by measurements. A form with
100 formulas recomputing a few dependents per keystroke costs single-digit microseconds. If one hot
spot appears (large grids × computed columns), the first lever is a faster evaluator *inside the
shared expression core*, for example closure-compiled typed delegates from the bound tree. That
helps Rules too, before any DLL tier is needed.

---

## 4. Solution architecture

```
YFex/src/Expressions/                 (NEW, extracted — §8.2)
  YFex.Expressions                    Expr AST + JSON codec, DynType, binder over a schema object, FunctionCatalog,
                                      built-ins, postfix compiler/program over IFieldReader, ExprText, text parser (new)
  YFex.Expressions.Generator          [ExprFunctions]/[ExprFunction] (today's [RuleFunctions] generator, moved)

YFex.Composer/                        composer.slnx
├── src/
│   ├── YFex.Composer.Public          IR records ([Union] node sets), ScreenEdit, keys, DisplayHints, diagnostics codes.
│   │                                 → YFex core, YFex.Validation (Violation/RuleCode/FieldConstraints), YFex.Expressions (Expr)
│   │                                 (no Composer generator: record access = YFex.Validation [RecordAccess] (C3);
│   │                                  dispatch-by-key invokers = YFex.Cqrs.SourceGenerator)
│   ├── YFex.Composer.Catalog         Frozen catalog over Capabilities + JsonSchemas + IRecordAccess + rule sets +
│   │                                 FunctionCatalog + widgets + jobs. Palette queries (auth/tenant filtered).
│   ├── YFex.Composer.Runtime         Plan compiler, ScreenState (RawValue slots), dependency graph, behavior evaluation,
│   │                                 input checks → ViolationWriter, flows, data sources, live decisions,
│   │                                 RuntimeScreen : StateObject, IValidationSource; NavigatR ComposedScreenRoute.
│   ├── YFex.Composer.Designer        Headless designer VMs (State + History undo). Also hosts the Rules authoring panels
│   │                                 (decision tables, parameters, dynamic rules) over Rules.Admin commands.
│   ├── YFex.Composer.Store           The screen revision kind for YFex.Revisions (validate = plan compile, canonical IR
│   │                                 JSON, ScreenDiff, preview) + scoped resolution. Until P3: a file store with the
│   │                                 same contract shapes (§11).
│   ├── YFex.Composer.Extensions      Extension fields: ExtensionBag, IExtensionStore, CQRS behavior.
│   ├── YFex.Composer.Rendering       WidgetKind, IWidgetFactory, ISlotBinding contracts (UI-free).
│   ├── YFex.Composer.Avalonia        Renderer + designer views over YFex.Avalonia (+ its IValidationSource projection).
│   ├── YFex.Composer.Blazor / .Wpf / .Uno
│   ├── YFex.Composer.Ai              (P4) MCP tools / AIFunctions over catalog + schemas + check/preview.
│   └── YFex.Composer.Compiler        (P5) Plan → C# emitter; eject; server Roslyn build + signing.
```

Layering:

```
L0    YFex core (RawValue, RawType, IRawValue, FieldPath, OptionSet, Bounds, StableHash64, Localization)
L0.5  YFex.Validation (+ IRecordAccess, [RecordAccess])    YFex.Expressions (→ core, Validation for Violation diagnostics)
L1    YFex.Rules (→ Expressions)    YFex.State    YFex.Composer.Public    YFex.Revisions (→ core, Validation, Authorization.Public)
L1.5  YFex.Rules.Dynamic (→ Rules, Expressions, Persistence)    YFex.Revisions.Data (SQL over YFex.Data)
L2    YFex.Cqrs    YFex.Composer.Catalog (→ Capabilities, Cqrs, Rules, Authorization.Public, Tenancy)
L3    YFex.Revisions.Sync (device cache + incremental sync)    YFex.Composer.Runtime / Store / Extensions    YFex.Rules.Admin (→ Revisions)
L4    YFex.Composer.Designer (→ State.History, Rules.Admin contracts)
L5    Renderers, Composer.Ai, Composer.Compiler
```

---

## 5. The IR (screen definition)

The JSON Forms / Dynamics separation is unchanged: *data shape* ≠ *layout* ≠ *behavior*.

```csharp
public sealed record ScreenDefinition(
    ScreenKey Key, DefinitionVersion Version, IrSchemaVersion Schema,
    ScopeIdentity Scope,              // YFex.Tenancy (ambient audience), see §10 on tiers
    ScreenTemplate Template,          // Form | List | MasterDetail | Dashboard | Wizard
    DataShape Data,                   // fields: Bound(catalog member) | Extension | Transient
    LayoutNode Layout,                // Section, Columns, Tabs, Stack, Widget(fieldId, kind), Grid(collection)
    BehaviorSet Behavior,             // per field: VisibleWhen, ReadOnlyWhen, Default, Computed  (Expr)
    InputCheckSet Checks,             // extra input checks: RequiredWhen, Min/Max/Options exprs, Check(expr, code)
    ActionSet Actions,                // flows over catalog items (§8.5)
    DataSourceSet Sources,            // queries + arg bindings + refresh triggers
    RuleBindingSet Rules,             // rule sets this screen evaluates live (§8.6) — references, never copies
    Audience Audience,                // required permission(s), scope visibility
    ImmutableDictionary<string, ImmutableDictionary<string, string>> Texts);  // key → culture → text (dynamic message layer)
```

- **Expressions are `YFex.Expressions.Expr`** and are stored in the same JSON format Rules.Dynamic
  specs use (`RuleSpecJson` expression part, canonical key order). One dialect for screens and
  rules, one AI schema.
- **Texts** follow the Rules.Dynamic precedent (`RuleSpec.Messages`): stored per culture with the
  definition and exposed as an `IMessageSource` in the **dynamic** layer of the message catalog.
  Labels, help, and input-check messages all resolve through `IMessageCatalog`. Violations carry
  codes, never text (D6).
- Stable keys everywhere. `NodeId : int` per node. Enacted revisions are immutable.

### 5.1 Edit operations

```csharp
[Union] public readonly struct ScreenEdit : IUnion
// AddField | RemoveField | SetDisplayHint | InsertNode | MoveNode | RemoveNode | SetBehavior | SetCheck
// | BindAction | SetFlowStep | BindDataSource | BindRuleSet | SetAudience | SetText
// (each case a distinct record type; TryGetValue overloads per case — validation/README hard constraint #2)
```

`ScreenEditor.Apply(definition, ReadOnlySpan<ScreenEdit>) → (definition', ViolationList)`. The
diagnostics are ordinary **`Violation`s with `FieldPath`s into the IR** (e.g.
`Behavior[3].VisibleWhen`). That's the same shape as Rules.Dynamic's enact-time checks
(`then[0].decrease.percent`), so the designer, the AI and the CLI show errors uniformly.

---

## 6. Type system — reuse core

### 6.1 Field type = `RawType` + `FieldConstraints` + `DisplayHints`

```csharp
public readonly record struct FieldType(
    RawType Type,                 // core: stable key + storage kind + id (Money.RawType, RawType.Int32, app types…)
    FieldConstraints Constraints, // YFex.Validation: required, lengths, min/max/step (RawValue), OptionSet, format code
    DisplayHints Display);        // Composer-only, UI: widget hint, mask, placeholder, decimals shown, multiline…
```

- **Semantic value types** (Money, Percentual, Quantity, Mass, Length, Cpf, Cnpj, Email, Sku…) are
  `IRawValue<TSelf>` types (D35). Composer knows them only by `RawType`. The library stays
  domain-free, and any app type a dev adds shows up in the designer automatically (formatter via
  `RawType`, storage via `RawType.Storage`).
- **Amounts** (`IAmount<TSelf>`, D36) get amount arithmetic in expressions for free (same type
  rules as Rules.Dynamic: amount ± same amount, amount × number, amount ÷ amount = ratio).
- **Enums** use `EnumCode`, with `EnumOption(Value, Code, Name)` for pickers (as in
  `SituationFieldInfo`).
- **Bound fields inherit constraints** from the contract (`IValidatable<T>.TryGetConstraints`). The
  user can *tighten* them (screen check) but never loosen them (principle 4; enforced at bind: a
  screen `Max` above the contract `Max` produces diagnostic `composer.check.looser`).

### 6.2 Values at runtime

`RawValue` is the single currency: slots, expression stack, constraints, violation args, extension
bag, AI tool I/O. Typed access is non-boxing through `TryGet<T>`/`As<T>()` (any `IRawValue` or
primitive). Rev 1's `Value` union is **dropped**.

### 6.3 Unions

All Composer unions (IR nodes, `ScreenEdit`, flow steps, outcomes) use the **`[Union]` tagged
struct** pattern with distinct case types. Native `union` is not used (ecosystem rule; it boxes, and
consistency beats the cold-path exception rev 1 allowed). Existing `ActionGate` (native, in
Capabilities) is consumed as-is.

---

## 7. The Catalog

### 7.1 Sources

| Catalog item | Comes from | Status |
|---|---|---|
| Objects/models + their fields | `IValidatable<T>.Shape` (`TypeShape`/`MemberShape`: RawType, enum members, nested, element) + **Composer field access** (§7.2) | shape ✅, access ➕ |
| Commands / queries | CQRS + capability profile `.Composable()`; parameters + **`ParametersSchema`** from Capabilities (built from the shape, no reflection) | schema ✅, opt-in flag ➕ |
| Field constraints | `IValidatable<T>.TryGetConstraints(path)`, `JsonSchemas.TryGetShape` | ✅ |
| **Rule sets** | `RuleSetDefinition<TS>`, `SituationSchema`, `DynamicRules.CatalogJson` (open groups, tiers, directive families, caps) | ✅ |
| **Functions** | `FunctionCatalog` (built-ins + app `[RuleFunctions]` + contracts) → shared with screen expressions after §8.2 | ✅ (moves) |
| Events / jobs / routes / dialogs | Messaging, YFex.Jobs, NavigatR, UI.Abstractions | ✅ |
| Value types | `RawType` registry (`RawType.Find(key)`) | ✅ |
| Widgets | Composer widget registry with capability descriptions | ➕ |

**Exposure is opt-in** (security boundary), as in rev 1.

### 7.2 Record access and invokers (C3)

The descriptive half exists (`TypeShape`, constraints, JSON Schema). What's missing is **reading a
member by id and constructing a record from values** without reflection. Composer needs it (slots →
command, query rows → grid cells), and so do the AI agent (JSON tool args → command; Capabilities
has `CanInvoke` but no `Invoke` today) and Rules live decisions (screen → situation). So it lives in
**YFex.Validation**, emitted by the same generator pass with the same `FieldIds`. A constraint for
member 2 can then never be applied to member 3.

Contract (YFex.Validation, next to `IValidatable<T>`):

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class RecordAccessAttribute : Attribute
{
    /// False = read-only access (query rows, results). Default true; the generator reports a diagnostic
    /// when the type has no accessible constructor / settable members for every member.
    public bool Construct { get; init; } = true;
}

public interface IRecordAccess<TSelf> where TSelf : IRecordAccess<TSelf>
{
    static abstract TypeShape Shape { get; }                             // same shape JsonSchemaWriter uses
    static abstract RawValue Read(in TSelf value, FieldId member);       // non-boxing (IRawValue → ToRaw())
    static abstract bool TryCreate<TSource>(ref TSource source, out TSelf value, ref ViolationWriter w)
        where TSource : struct, IMemberSource, allows ref struct;        // conversion failures → Violations, not exceptions
}

public interface IMemberSource
{
    bool TryGet(FieldId member, out RawValue value);
    bool TryEnterNested(FieldId member);  void ExitNested();                       // Address.City
    int  CountItems(FieldId member);      bool TryEnterItem(FieldId member, int i); // Lines[]
}
```

Usage:

```csharp
[RecordAccess]                         // read + construct
public partial record CreateOrder(CustomerId Customer, Money Total, string? Notes) : ICommand<OrderId>;

[RecordAccess(Construct = false)]      // read-only grid rows; no validation rules needed
public partial record OrderRow(OrderId Id, string Customer, Money Total, DateOnly Date);

// Composer: slots → command → contract validation → dispatch
var src = new SlotSource(state, plan.ArgMap);
if (CreateOrder.TryCreate(ref src, out var cmd, ref w) && CreateOrder.Validate(in cmd, ref w)) …

// AI agent: JSON args → command (Utf8JsonReader-based source, no STJ reflection)
var json = new JsonMemberSource(args);  CreateOrder.TryCreate(ref json, out var cmd, ref w);

// Grid cell
RawValue cell = OrderRow.Read(in rows[i], OrderRow.FieldIds.Total);
```

Generated body (sketch): `Read` is a `switch` on `FieldId` returning `ToRaw()`/`RawValue.From(...)`;
`TryCreate` reads each member with `TryFromRaw`, writes `Violation(FieldPath.Of("Total"), "convert")`
on failure, and calls the positional constructor or object initializer.

**Dispatch by key** ("capability `sales.order.create` → `CommandAsync<CreateOrder, OrderId>`") is a
frozen invoker table emitted by **YFex.Cqrs.SourceGenerator**, which already knows every
command/result pair and emits the static call helpers:

```csharp
public interface IMessageInvoker   // generated per composable message, registered by key
{
    ValueTask<ActionOutcome> InvokeAsync<TSource>(IDispatcher d, ref TSource args, ExtensionBag ext, CancellationToken ct)
        where TSource : struct, IMemberSource;   // TryCreate → dispatch → server stages 1–4; Error carries Violations
}
```

Composer therefore has **no generator of its own** for access or dispatch.

### 7.3 Palette API

Same as rev 1 (`Search`, `TryGetShape`, `CanUse` → `ActionGate`, `FieldsOf`, `Scaffold`), plus:
- `ConstraintsOf(CatalogKey, FieldPath)` → `FieldConstraints` (static).
- `RuleSetsFor(CatalogKey command)` → rule sets enforced on that command (from the CQRS
  `.Enforce(...)` config), so the designer can show "this command is governed by *Sale discounts*".

**Scaffold-from-command** now produces a better form for free. Required markers, lengths, ranges and
option lists come straight from `FieldConstraints`, and pickers are pre-limited by the live decision.

---

## 8. Runtime

### 8.1 Slot store

```csharp
public sealed class ScreenState : IFieldReader            // IFieldReader = the expression engine's input (§8.2)
{
    private readonly RawValue[] _slots;                    // 32 B each; 200 fields ≈ 6.4 KB
    private readonly CollectionSlot[] _collections;        // grids/lines: row storage (RawValue rows or typed rows + IRecordAccess)
    private PropertyBitmap64[] _dirty;                     // reuse YFex.State bitmaps

    public RawValue Read(ushort id) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_slots), id); // ids validated at plan compile
    public void Write(ushort id, in RawValue v);           // equality check → no-op/no event when unchanged
}
```

Rev 1's typed SoA columns are dropped. `RawValue` is what the expression program, constraints and
violations consume, so storing it directly avoids conversions. Typed access is still non-boxing.

### 8.2 Expressions — extract `YFex.Expressions` from Rules.Dynamic

Rules.Dynamic already has everything a screen formula needs: arithmetic over amounts, comparisons,
`if`/`coalesce`, `in`/`between`/`isSet`, aggregates over lines (`sum/count/any/all/minOf/maxOf`),
function calls with generics and contracts, literal typing (`"Pix"` → enum, `500` → Money), totality
and size limits. It's coupled to `ISituation<TS>` in **only three places**:

| Coupling | Today | Extracted |
|---|---|---|
| Schema for binding | `Binder<TS>` reads `TS.Schema` (a runtime `SituationSchema` object) | binder takes an **`ExprSchema` instance** (fields: id, name, `RawType`, enum options; named collections). `SituationSchema` adapts/derives from it. |
| Field reads | `DynProgram<TS>` calls `TS.Read/ReadLine/LineCount` | `Run<TReader>(ref TReader r) where TReader : struct, IFieldReader, allows ref struct`. The JIT specializes per reader, so there's no virtual call and **no perf loss for Rules** (`SituationReader<TS>` wraps `in TS`). |
| Specials | `$base`, `$effectiveDate`, `$lineIndex` | a pluggable `SpecialVar` table. Rules registers its three; Composer adds `$today`, `$user`, `$row`, `$isNew`. |

What stays in Rules.Dynamic: `RuleSpec`, `DirectiveSpec`, group policies, caps, tiers, the codec,
messages, Admin integration.

What's new in `YFex.Expressions` (benefits both):
- **Text syntax parser** (Excel-like: `If(Total > 1000, ReduceBy(Total, 5%), Total)`) ↔ `Expr`.
  `ExprText` already renders the reverse; validation/13 lists "end-user text syntax: later".
- **Named collections** (multiple grids per screen; `Sum(Items, Qty * Price)`). Today the engine has
  one lines field per situation.
- Function attributes renamed `[ExprFunctions]`/`[ExprFunction]`, with `[RuleFunctions]` kept as an
  alias for compatibility.

Rejected alternatives: (a) a second Composer-only language, since users and AI would face two
dialects; (b) Composer generating a fake `ISituation` per screen, which can't work because screens
are runtime schemas and `ISituation` is static-abstract.

Regression guard for the extraction: the Rules.Dynamic suite (19 tests incl. traces/explanations)
and the generated-functions tests must stay byte-identical, plus a benchmark showing no regression
on the measured numbers (0.4 / 2.6 / 9.5 µs).

### 8.3 The three kinds of logic

| Kind | Example | Lives in | Enforced where | Authoring |
|---|---|---|---|---|
| **Behavior** (UX) | visible/readonly when, defaults, computed display totals | Screen IR (`BehaviorSet`, `Expr`) | client only | screen designer |
| **Input checks** | contract constraints; *extra* screen checks ("Notes required when Total > 10k") | contract: `YFex.Validation` (static); screen extras: IR `InputCheckSet` → `Violation`s | contract: client **and** server (CQRS stage 1); screen extras: client, plus the server only if promoted (below) | designer |
| **Business rules** | discount caps, installment limits, approvals | **YFex.Rules** rule sets (code, tables, parameters, **dynamic rules** in open groups) | server (stages 3–4) + client preview via Decisions | designer's **Rules panel** → `RuleAdmin.EnactRule/EnactTable/EnactParameters` (C5) |

**Promote to rule.** When a screen check must also hold server-side, the designer offers to convert
it into a dynamic rule (`Deny`/`Warn` directive) in an open group of the command's rule set. That
group must opt in via `RuleGroup.Dynamic`. Same expression, now enforced, tiered, audited, and
simulatable.

### 8.4 Dependency graph, validation flow

- Behavior/checks declare reads → **CSR** DAG + topological order at plan compile (cycles =
  diagnostics). Recompute inside `BatchScope`.
- Per-field checks write into a `ViolationWriter`, and `RuntimeScreen` keeps them in the State
  **`ValidationBag`** (`Replace(path, source, owner, ref w)`). Display policy (touched/submitted),
  status, and debounce (`[Debounce]` semantics in State, D16) come for free.
- Submit: `IValidatable<TCmd>.Validate` on the constructed command (contract), screen checks, then
  dispatch. A server `Error` carrying violations goes through `ValidationBag.ApplyServer`, with paths
  mapped *command path → screen node* via a plan-generated `IPathMap` (same mechanism as
  `SituationPathMap` / `[MapsTo]`).

### 8.5 Action flows

The closed `[Union]` step set gains steps that Rules makes possible:

```
Validate → Confirm (auto when SideEffect = Irreversible) → Approval (auto when the live Decision has RequireApproval:
           collects an OverrideToken via the approval flow, validation/08; online HMAC or capped offline, D34)
         → Command(key, args ← slots) ─ Ok → Toast/Navigate │ Queued → "saved offline" │ Error → ApplyServer → show
```

Steps: `Validate | Confirm | Approval | Command | Query | SetField | Navigate | Dialog | Toast |
EnqueueJob | PublishEvent | Explain | Branch`. `Explain` opens the decision breakdown (`YRuleBreakdown`,
validation/06+08). Multi-command compensation is still delegated to Orchestration/Compensation.

### 8.6 Live decisions (new)

When the bound command is governed by a rule set (`.UseSituation<TS>().Enforce(set)`), the screen
evaluates the **same rule set client-side** as the user types:
- **Limits/options** → merged into the field's constraints (most restrictive, as
  `JsonSchemaWriter.Merge` does). The picker shows "max 6x" before the user hits submit
  (Principle B).
- **Adjustments** (increase/decrease, allocation) → read-only computed slots ("discount applied",
  "final total").
- **RequireApproval** → the flow inserts the `Approval` step. **Explanation/trace** → `Explain`.
- **Fingerprint** is sent with the command, so the server detects decision drift
  (`OnDecisionMismatch`).

Gap: building a `TS` situation from screen slots at runtime. VMs use the generated
`[SituationSource<TS>] BuildSituation()` (D31), but a runtime screen has no generated VM. Options:
(a) a new generated `static abstract TS Build<TReader>(ref TReader r)` on `ISituation<TS>` (clean,
recommended); (b) interim: write the canonical bytes with `SituationWriter` and call
`TS.Deserialize`. The format is stable and versioned, but it's a detour. A non-generic entry point is
also needed: `IRuleSetHost.Evaluate(ReadOnlySpan<byte> situation) → DecisionSummary` (the host
already simulates from situation bytes).

### 8.7 Data sources

Unchanged from rev 1. Rows are read through `IRecordAccess<TRow>.Read`, and
`CacheableQueryResult` provenance becomes a badge.

---

## 9. Rendering — cross-framework

Unchanged: semantic layout tree, YFex.UI tokens, widget kinds + `IWidgetFactory`, renderer-driven
slot binding. New:

- **`RuntimeScreen` implements `IValidationSource`** (constraints = contract ⊕ screen checks ⊕ live
  decision; visible violations; status; `Touch`). The renderer adapters reuse the **constraint
  projection** that validation/08 defines for YFex.Avalonia (attached properties, error presenter).
  So a composed screen and a hand-written VM get identical validation UX, and the adapter writes no
  validation code.
- Built-in widgets gain `DecisionPanel`/`RuleBreakdown` and `ApprovalPrompt`.
- **Shared prerequisite:** the reusable **table/grid design** in YFex.UI. Rules Phase 5 (Avalonia
  table editor) is blocked on it, and Composer's `Grid` widget and the designer's Rules panel need
  it too. Build it once, before Composer P2.

---

## 10. Security — auth, tenancy, policies

Rev 1's table stands (palette filtered by auth; enact-time audience checks; load-time
`Audience`; server CQRS auth + `YFex.Data` RLS authoritative; total expressions; signed Tier-2
artifacts). Additions:

- **Server pipeline is now complete.** Commands dispatched from composed screens pass stage 1
  (shape validation), 2 (auth), 3–4 (rules) like any other caller, so a malicious or broken screen
  can't skip contract validation or business rules.
- **Identity over RPC:** the `CallContext` (D29) carries `IAuthContext` + `ScopeIdentity`
  implicitly, so Composer server handlers (enact, extension store) need no identity parameters.
- **Rule authoring permissions** reuse Rules.Admin's (`rules.dynamic.enact`, per-table enact
  permissions after C5, `IRuleTierGuard`), and **guardrails/caps** (`DynamicRulePolicy` max percent/amount,
  sealed rules) still bound what an end user can author through Composer.
- **Two kinds of scope, kept distinct on purpose:**
  - *Screen audience/override* is **ambient**: Tenancy `ScopeIdentity` resolved most-specific-first
    (user → company → tenant → global) via `ScopeChain`.
  - *Rule data* is **situation-driven**: `RuleTierPath` from app-declared `[RuleTier]` fields (D37).
    A screen enacted for tenant T only *references* rule sets. The rule data in force is chosen by
    the situation's tier path, not by the screen.
  The designer shows both explicitly ("screen visible to: Store 12" vs "rule enacted at:
  Store 12").

---

## 11. Persistence, revisions & distribution

Whenever end users change configuration at runtime, a safe path from "edited" to "in force
everywhere" is needed: draft → validate → immutable numbered revision → in force (from a date, at a
scope) → on every device (offline cache + catch-up) → audited → revertible. Rules.Admin has already
built and tested this for rule data, and Composer screens need exactly the same lifecycle:

| Concern | Rules.Admin (implemented today) | Composer |
|---|---|---|
| Versioned immutable store, optimistic concurrency per stream | `IRuleStore` (`RuleDataKey(Set, Kind, Code, TierPath)`), `InMemoryRuleStore`, `SqlRuleStore` (YFex.Data, 4 dialects) | stream `(ScreenKey, Scope)` |
| Pipeline | `RulePublisher`: authorize → validate → canonicalize → append → apply → notify | same; validate = plan compile against the catalog |
| Canonical form | `Canonicalize` (stable ids, key order) | canonical JSON of the IR (stable `NodeId`s) |
| Audit | `RuleDataDiff` (rows by id, LIS-minimal moves), `IRuleAuditSink` | `ScreenDiff` (nodes by `NodeId`) |
| What-if | `Simulate` over `ISituationSamples` → `SimulationReport` | **Preview**: run the plan over sample records |
| Device cache + incremental sync | `KeyValueRuleDataCache` (`IKeyValueStore`, MemoryPack), `RuleSetSync` (cache first → fetch after last version → apply; on event/reconnect) | same |
| Effective dating | `EffectiveFrom` | `EffectiveFrom` (schedule a screen change) |

### 11.1 `YFex.Revisions` (C4, C5)

**When:** extracted from Rules.Admin when Composer reaches **P3**. Rules.Admin becomes its first
consumer and Composer its second. Until then, Composer P0–P2 uses a file store whose contracts copy
these shapes, so the merge is mechanical.

**Vocabulary** ("publish" avoided: it clashes with `dotnet publish` and `IDispatcher.PublishAsync`):

| Concept | Name |
|---|---|
| Immutable numbered version of an artifact | `Revision` (`Version`, `Key`, `EffectiveFrom`, `Payload`, `Actor`, `Note`, `EnactedAt`) |
| Stream key (scope is opaque text: Rules stores its tier path, Composer a `ScopeIdentity`) | `RevisionKey(Area, Kind, Code, Scope)` |
| Store (append with base version, load after N, get by version) | `IRevisionStore`, `InMemoryRevisionStore`; SQL in `YFex.Revisions.Data` (today's `SqlRuleStore`) |
| Per-kind plug-in implemented by each library | `IRevisionKind`: `Validate`, `Canonicalize`, `Diff`, `Apply` (hot swap), optional `Simulate` |
| Pipeline: authorize → validate → canonicalize → append → apply → notify | `Enactor.EnactAsync(...)` |
| Event | `RevisionEnacted` |
| Rollback (old content enacted again as a new revision) | `Revert` |
| Audit | `RevisionDiff`, `IRevisionAuditSink` (enacted + rejected attempts) |
| Device side | `IRevisionCache` (over `IKeyValueStore`), `RevisionSync`, in `YFex.Revisions.Sync` |
| Permission suffix | `*.enact` (`rules.dynamic.enact`, `composer.screen.enact`) |

**What stays in each library:** Rules.Admin keeps the rule kinds (tables, parameters, overrides,
dynamic rules), tier guards, approvals (`ApproveDecision` — unrelated to enacting) and
rule-definition queries. Composer.Store keeps the screen kind, plan-compile validation and preview.

**Rules.Admin rename (C5)**, done in the same change (hard replace, all in-repo):

| Today | After |
|---|---|
| `RuleAdmin.Commands.PublishTable / PublishParameters / PublishRule` | `EnactTable / EnactParameters / EnactRule` |
| `RulePublisher` | rule kinds plugged into `Enactor` |
| `RulesetPublished` | `RevisionEnacted` (filtered by area `rules`), or `RulesetEnacted` if a rules-specific event is kept |
| permission `rules.dynamic.publish`, per-table `….publish` | `rules.dynamic.enact`, `….enact` |
| `RuleAdminOptions.RulePublishPermission` | `RuleEnactPermission` |
| `IRuleStore` / `InMemoryRuleStore` / `SqlRuleStore` (`yfex_rule_data`) | `IRevisionStore` / `InMemoryRevisionStore` / `SqlRevisionStore` (`yfex_revisions`). Rules data is days old; provide a one-shot copy script rather than a compatibility layer |
| `KeyValueRuleDataCache`, `RuleSetSync` | `IRevisionCache` impl, `RevisionSync` (device keys `yfex.rev/{area}/{…}`; old `yfex.rules/…` keys dropped and refetched) |
| validation docs 05/09/13 | wording updated to Enact |

Acceptance for the extraction: all Rules.Admin (22), Rules.Data (15, contract suite now against
`IRevisionStore`) and Rules.Dynamic tests green after the rename; the offline-restart and
missed-versions sync tests unchanged in behavior.

**Formats.** The IR is stored as canonical JSON (diff/review/AI, same style as `RuleSpecJson`), and
the device cache holds MemoryPack payloads. Plans aren't persisted; they're recompiled per version
(Rules.Dynamic made the same call: ≈ 30 ms for 500 rules isn't worth a second binary format), with
an in-memory compile cache keyed by *(definition hash, catalog fingerprint, function catalog
`Version`)*, like `DynamicRules`' cache.

### 11.2 Extension fields

Unchanged recommendation: one extension-bag column per aggregate, with optional "promote to
indexed" later. Updates:
- `ExtensionBag` entries are `(FieldId, RawValue)` with `RawType` ids, using the same wire encoding
  rules as `SituationWriter` v2 (type id per value).
- Extension field definitions carry `FieldConstraints`, so the CQRS behavior validates the bag with
  the **same violation model** (code + `FieldPath` `Extensions.<key>`) and the server error maps
  back to the screen like any field.
- Extension fields can be **exposed to rule sets** as situation fields (`Exposed`) only through a
  generated situation member that reads the bag. This is a dev decision per rule set, never
  automatic (situations are static by design).

---

## 12. AI integration (P4)

The Rules work has already established the loop and most schemas. Composer copies the pattern:

| Step | Rules (implemented) | Composer |
|---|---|---|
| Discover | `CatalogJson(set)` (fields, functions, open groups, caps) | `composer.catalog` (objects, commands + `ParametersSchema`, rule sets, functions, widgets) |
| Output schema for structured generation | `SpecJsonSchema(set)` specialized to the rule set | `ScreenSpecJsonSchema(catalog subset)` specialized to the chosen object/command (field-name enums, allowed widgets/steps) |
| Check | `dynamic.Check` → violations with JSON paths | `ScreenEditor.Apply` / plan compile → violations with IR paths |
| Simulate | `Simulate` over samples | `Preview` over sample records |
| Enact | a person enacts (`EnactRule`, C5) | a person enacts the screen revision; embedded rules go through `EnactRule` |

Because screen expressions and rule expressions share `YFex.Expressions`, a single prompt like
"VIP customers get 10% off above R$1000, and show the discount on the order screen" produces **one
dynamic rule + one screen edit**, both validated by their own checkers, both previewed, and both
approved by the user. Exposed via MCP + `Microsoft.Extensions.AI` as in rev 1. The AI always acts
with the user's `IAuthContext`.

---

## 13. Tier 2 — compiled screens (P5, benchmark-gated)

Unchanged from rev 1 (C# emitter → eject → server build job with the real YFex generators → signed
artifact → collectible ALC with fallback → differential tests). Two refinements:
- The emitter works from the **bound expression tree** of `YFex.Expressions`, so the same emitter
  can later compile hot *dynamic rules* to C# too, if ever needed.
- The **differential test** compares `ViolationList`s, slot traces and decision fingerprints
  (`StableHash64`) between tiers, reusing the fingerprint machinery instead of writing a new
  comparer.

---

## 14. Library shortlist (updated)

| Need | Choice |
|---|---|
| Values, paths, hashing, option sets, bounds | **YFex core** (`RawValue`/`RawType`/`IRawValue`, `FieldPath`, `StableHash64`, `OptionSet`, `Bounds<T>`) |
| Constraints, violations, contract shapes, JSON Schema | **YFex.Validation** |
| Expressions & functions | **YFex.Expressions** (extracted from YFex.Rules.Dynamic) |
| Business rules, decisions, approvals, trace | **YFex.Rules**, **Rules.Dynamic**, **Rules.Admin**, **Rules.Data** |
| Messages | **YFex.Localization** catalogs (dynamic layer for screen texts) |
| Binary cache format | MemoryPack (via YFex.Persistence, as Rules' device cache) |
| JSON | `Utf8JsonWriter`/reader, hand-written canonical codecs (as `RuleSpecJson`) |
| Tier 2 | Microsoft.CodeAnalysis.CSharp (server project only) |
| AI | Microsoft.Extensions.AI, ModelContextProtocol C# SDK |
| Benchmarks | BenchmarkDotNet |
| Rejected | NCalc, DynamicExpresso, RulesEngine, Jint, Power Fx as a dependency (semantic reference only), FluentValidation (only via the `YFex.Cqrs.FluentValidation` adapter, as decided in D18) |

Prior art is as in rev 1 (JSON Forms, Dynamics/Power Apps model-driven, Power Fx, Odoo view
inheritance, Appsmith/ToolJet, Lightning App Builder), plus DMN (decision tables), which Rules
already follows.

---

## 15. Phased roadmap (re-sequenced)

| Phase | Deliverable | Depends on | Exit criterion |
|---|---|---|---|
| **X — Expression extraction** | `YFex.Expressions` (+ generator) out of Rules.Dynamic: `ExprSchema`, `IFieldReader` generic program, pluggable specials, named collections; Rules.Dynamic re-pointed | — | All Rules/Rules.Dynamic/generator tests green, unchanged traces; benchmark parity |
| **P0 — Catalog & access** | `[RecordAccess]` + `IRecordAccess<T>` in YFex.Validation (+ generator tests, AOT smoke), invoker table in YFex.Cqrs.SourceGenerator (C3); `Composer.Public` (IR as `[Union]`s, `ScreenEdit`, `DisplayHints`), `Composer.Catalog` over Capabilities + JsonSchemas + rule sets, `.Composable()` opt-in | X (for function catalog) | Catalog lists commands with constraints + `ParametersSchema`; a command is built from a `RawValue` source and dispatched with no reflection; AOT smoke clean |
| **P1 — Forms MVP** | Plan compiler, `ScreenState`, `RuntimeScreen : StateObject, IValidationSource`, flows (Validate/Confirm/Command/Toast/Navigate), scaffold-from-command, Avalonia renderer reusing the validation/08 projection, file store | P0; validation/08 projection (part of Rules Phase 5) | Scaffold → run a form whose required/length/range UX comes from the contract; server violations map back to fields |
| **P2 — Behavior, data, grids** | Behavior + input checks (`YFex.Expressions` text syntax), data sources, **grids** (shared YFex.UI table design), Blazor renderer, headless designer + Avalonia views | **YFex.UI table/grid design** | Master-detail with computed totals + multiple collections, identical on Avalonia + Blazor |
| **P3 — Rules & governance** | Live decisions (§8.6, needs `ISituation.Build<TReader>` + non-generic `Evaluate`), Approval/Explain steps, **Rules panel** (dynamic rules, tables, parameters via Rules.Admin; promote-to-rule), scoped definitions, **`YFex.Revisions` extraction + Rules.Admin Enact rename** (C4/C5, §11.1), screen revision kind, extension fields, IR upgraders, device sync, composed screens as capabilities, WPF renderer | P2; Rules.Admin | A tenant authors a screen *and* a dynamic discount rule. The rule is enforced server-side, previewed client-side, and audited; tenant B is unaffected |
| **P4 — AI** | `ScreenSpecJsonSchema`, MCP tools, generate → check → preview → approve; combined rule + screen proposals | P3 | Natural-language request → validated, previewed, user-approved screen + rule |
| **P5 — Compiled tier** | Emitter, eject, server build, signing, ALC loader, differential tests (fingerprints) | Benchmarks | Justified by measurements; differential tests green |
| **P6 — Automations** | Event/schedule-triggered flows (`ComposedFlowJob`), compensation via Orchestration | P3 | Scheduled flow runs under tenant scope with audit |

Rules' remaining Phase 5 (Avalonia) and Composer P1/P2 share their UI prerequisites. Schedule them
together so the projection and grid are built once.

---

## 16. Required changes in existing libraries (gap table, updated)

| Library | Gap | Status / suggested change |
|---|---|---|
| ~~YFex.State~~ | ~~Validation coupled to MVVM~~ | ✅ **Closed** by YFex.Validation (D2/D4) |
| ~~YFex.Capabilities~~ | ~~`ParameterDoc.Type` not serializable~~ | ✅ **Closed**: `ParametersSchema` + shape-based `ParameterDoc` (Phase 6 C4) |
| **YFex.Rules.Dynamic** | Expression engine bound to `ISituation<TS>`; one lines collection; no text syntax | Extract **`YFex.Expressions`** (Phase X, §8.2) |
| **YFex.Rules** | No runtime construction of a situation; no non-generic evaluation | `static abstract TS Build<TReader>(ref TReader)` on `ISituation<TS>` (generated); `IRuleSetHost.Evaluate(bytes)` → `DecisionSummary` |
| | Dynamic messages: latest version wins (open deviation) | Versioned texts per decision. Composer texts have the same need, so solve it once |
| **YFex.Validation** | No generated member *access/construction* by id | `[RecordAccess]` → `IRecordAccess<T>` (Read/TryCreate, shared `FieldIds`) (C3) |
| **YFex.Cqrs** | No dispatch by capability key | Invoker table emitted by YFex.Cqrs.SourceGenerator (C3) |
| **YFex.UI** | No reusable table/grid design (blocks Rules Phase 5) | Design it once; Composer grid + Rules table editor + designer use it |
| **YFex.Capabilities** | Registry built once at startup | Overlay registry (`IDynamicCapabilitySource` + atomic swap) so enacted screens appear in search/AI |
| | No composition opt-in | `CapabilityFlags.Composable`, `.Composable()`; kinds `ComposedScreen`, `ComposedFlow` |
| **YFex.Authorization** | Compile-time permission catalog | Runtime-registered permissions in a reserved namespace (`composer.screen.<key>.open`). Rules.Admin's configurable `RuleEnactPermission` (C5) shows the minimal version |
| **YFex.Tenancy** | Scoped most-specific lookup still lives in `CustomLogicRegistry` (Rules doc 04 §9 also asked for `ScopedTable<TKey,TEntry>`; not found in source; the `ScopedTableBuilder` in YFex.Data.Tenancy is RLS, a different concern) | Extract the generic scoped lookup into Tenancy; CustomLogic + Composer use it |
| **Rules.Admin / Rules.Data** (+ Composer) | Revision lifecycle is rules-specific; "publish" naming clashes | Extract **`YFex.Revisions`** (+ `.Data`, `.Sync`) at Composer P3 and rename Publish → Enact (C4/C5, §11.1) |
| **YFex.Cqrs** | Extension-field carrier | `IExtensible` + pipeline behavior hook (validated with the Validation violation model) |
| **YFex.Jobs** | Static, generator-registered jobs | Generic `ComposedFlowJob` (payload = flow key + args) |

---

## 17. Risks & open questions

**Risks**
1. **Scope creep into a full low-code platform.** Mitigated by the non-goals and principle 4.
2. **Extraction churn.** `YFex.Expressions` touches freshly built Rules.Dynamic code. Mitigate with
   the regression guard (§8.2) and by doing it before more consumers appear.
3. **Catalog drift.** Stable keys, IR upgraders, and CI that compiles every enacted definition
   against the new catalog (Rules.Dynamic's compile cache keyed by schema hash + catalog version
   already detects staleness).
4. **Two scope models** (ambient tenancy vs situation tiers) confusing admins. Mitigated by explicit
   designer UX (§10).
5. **.NET 11 preview union lowering** is still moving. The `[Union]` struct pattern isolates us.

**Open questions**
1. Which platforms must run composed screens? (NativeAOT/iOS → Tier 2 never applies there.)
2. Is there always a server for enacting/extension fields, or must offline desktop apps author too?
3. Who are the end users: admins/power users (Rules panel, text formulas) or operators (templates only)?
4. Should tenant screens override developer screens (Odoo-style seams) in P3, or only add new screens?
5. ~~Approve Phase X?~~ → **C1: approved.**
6. ~~Where is record access generated?~~ → **C3: YFex.Validation generator, `[RecordAccess]` opt-in.**
7. ~~Shared lifecycle library and its name?~~ → **C4: `YFex.Revisions` + Enact, extracted at P3;
   C5: Rules.Admin renamed to match.**
8. ~~Promote-to-rule automatic or explicit?~~ → **C2: explicit, with "never" for users without rule
   permission.**
