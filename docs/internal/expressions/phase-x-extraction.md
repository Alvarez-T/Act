# Phase X — Extract `YFex.Expressions` from `YFex.Rules.Dynamic`

> **Status (2026-10-01):** **X.0–X.4 implemented** (not committed). Remaining: verification items blocked by other in-progress
> work, X.5, X.6 (optional), X.7. **Next session: start at §11 "Handoff"**, then the "Implementation log" (§12).
> **Audience:** the session that implements it. Read this file, then only the source files named in each step.
> **Normative vs sketch:** type and member names in code blocks are **normative** unless marked `// (sketch)`.
> Decisions still to confirm are marked **⚑ XD#** (§9); the recommended option is what the steps assume.
> Sibling docs: `../studio/02-new-screens.md` (§8.2, §15 Phase X), `../validation/05-decision-tables-and-dynamic-rules.md` §8,
> `../validation/13-phases-and-acceptance.md` ("Phase 6 implementation log").

---

## 1. Why

`YFex.Rules.Dynamic` contains two things:

| Part | Generic? | Today's files |
|---|---|---|
| **Expression language**: AST, JSON form, types, functions, binder, compiler, interpreter, readable text | yes | `Model/Expr.cs`, expression half of `Model/RuleSpecJson.cs`, `Model/ExprText.cs`, `Typing/DynType.cs`, `Functions/*`, `Compiler/Binder.cs`, `Compiler/Emitter.cs`, `Runtime/DynProgram.cs` |
| **Rule language**: `RuleSpec`, directives, group policies/caps/tiers, codec, messages, authoring catalog | no (rules only) | `RuleSpec`/`DirectiveSpec` in `Expr.cs`, rule half of `RuleSpecJson.cs`, `Compiler/RuleCompiler.cs`, `Runtime/CompiledRule.cs`, `DynamicRules.cs`, `Authoring/DynamicRulesAuthoring.cs` |

Composer needs the first part for screen formulas (visible/readonly/default/computed, input checks) over a **runtime**
schema (screen fields built at run time). That can't be an `ISituation<TS>`, whose schema and reads are `static abstract`.
Extracting the generic part gives:

- one dialect (JSON + text) for screen formulas and business rules, for users and for AI;
- one function catalog: a function written once is usable in both;
- "promote to rule" (composer-design C2) with the expression unchanged.

## 2. Where the engine is coupled to situations (exactly)

Found by reading the source on 2026-10-01:

| # | Place | Coupling | Extracted form |
|---|---|---|---|
| 1 | `Binder<TS>` | `_schema = TS.Schema` (a runtime `SituationSchema` object); `TryGetField`, `LineSchema`, `field.Exposed`, `field.Role == Lines` | binder over an **`ExprSchema` instance** |
| 2 | `Binder<TS>` | `_set.AmountType` for `$base`; specials `$base/$effectiveDate/$lineIndex` hard-coded | pluggable **`ExprSpecials`** table |
| 3 | `Binder<TS>` | error codes `DynamicRuleCodes.*` (`rules.dynamic.*`) | **`ExprDiagnosticCodes`** map (Rules passes its codes, so outputs stay identical, XD1) |
| 4 | `DynType.EnumOf` | `SituationFieldInfo?` (Rules type) | **`ExprEnumDomain`** (name, CLR type name, options) |
| 5 | `DynProgram<TS>` | `TS.Read`, `TS.ReadLine`, `TS.LineCount`; `@base`/`date` parameters | `Run<TReader>(ref TReader, in ExprEnv)` over **`IFieldReader`** |
| 6 | `Emitter<TS>` | only the `DynProgram<TS>` type parameter | non-generic emitter |
| 7 | `Arithmetic/Neg/Sum` | `Amounts.ToRaw` (lives in **YFex.Rules**) | core helper `RawValueConvert.FromDecimal(decimal, RawType)` (XD6) |
| 8 | `RuleFunction` ctor | reserved names via `RuleSpecJson.TryGetOperator` | `ExprJson.TryGetOperator` |
| 9 | `ExprText` | operator names via `RuleSpecJson.OperatorName` | `ExprJson.OperatorName` |
| 10 | `YFex.Rules.Generator/FunctionGen.cs` | emits `global::YFex.Rules.Dynamic.*` | moves to `YFex.Expressions.Generator` (XD2) |

**Not coupled, so YFex.Rules itself does not change** except for the tiny core moves in step X.1 (`EnumOption`, `Amounts.ToRaw`
delegation). The `SituationSchema` → `ExprSchema` adapter lives in **Rules.Dynamic**. That corrects composer-design §4, which
had "YFex.Rules (→ Expressions)"; the dependency is **YFex.Rules.Dynamic → YFex.Expressions**.

## 3. Target layout

```
YFex.Expressions/
  YFex.Expressions               net11.0, AOT, → YFex core + YFex.Validation (Violation, FieldPath, ValidationResult)
    Model/      Expr, ExprKind, ExprJson (read/write, operator table), ExprText
    Typing/     ExprType, ExprEnumDomain, TypeConstraint, TypeRef
    Schema/     ExprSchema, ExprField, ExprCollection, ExprSpecials, ExprSpecial
    Functions/  ExprFunction, ExprFunctionParameter, ExprFunctionFn, FunctionCatalog, BuiltInFunctions,
                ExprFunctionsAttribute, ExprFunctionAttribute, ExprFunctionArgs
    Compiler/   ExprBinder, BoundExpr (internal), ExprEmitter (internal), ExprCompiler (public entry), ExprLimits,
                ExprCodes (default "expr.*"), ExprDiagnosticCodes
    Runtime/    ExprProgram, IFieldReader, ExprEnv, Instr/Op (internal)
    Authoring/  ExprAuthoring (operators/functions/fields JSON + JSON-Schema $defs for expressions)
    Messages/   ExprMessages : IMessageSource (en/pt texts for expr.* codes)
  YFex.Expressions.Generator     netstandard2.0, bundled as analyzer of YFex.Expressions (FunctionGen moved here)
  YFex.Expressions.Tests
  YFex.Expressions.Generator.Tests
  YFex.Expressions.Benchmarks    BenchmarkDotNet (baseline + parity, §7)
```

Layering (extends validation/README "Dependency layers"):

```
Layer 0   : YFex core                  (+ EnumOption, RawValueConvert.FromDecimal — X.1)
Layer 0.5 : YFex.Validation            YFex.Expressions (→ core, Validation)
Layer 1   : YFex.Rules                 (unchanged; does NOT reference Expressions)
Layer 1.5 : YFex.Rules.Dynamic         (→ Rules, Expressions, Persistence)
```

## 4. Public API (normative)

### 4.1 Schema: what an expression can read

```csharp
namespace YFex.Expressions;

/// A field an expression can read: {"var": Name} at the top level, {"item": [Collection, Name]} / {"line": Name} in an item.
/// Ids are dense inside their schema (Fields[i].Id == i) — the program reads by id through IFieldReader.
public sealed class ExprField(ushort id, string name, RawType type, bool exposed = true, ExprEnumDomain? enumDomain = null)
{
    public ushort Id { get; } = id;
    public string Name { get; } = name;
    public RawType Type { get; } = type;
    /// False → binder reports codes.FieldNotExposed (Rules: situation fields with Exposed = false).
    public bool Exposed { get; } = exposed;
    public ExprEnumDomain? EnumDomain { get; } = enumDomain;
}

/// A named list of items (situation lines, a screen grid). Items have their own schema.
public sealed class ExprCollection(ushort id, string name, ExprSchema items)
{
    public ushort Id { get; } = id;
    public string Name { get; } = name;
    public ExprSchema Items { get; } = items;
}

/// Runtime description of what expressions may read. Built once and immutable (Rules: from SituationSchema;
/// Composer: from the screen's DataShape at plan compile).
public sealed class ExprSchema
{
    public ExprSchema(string name, ExprField[] fields, ExprCollection[]? collections = null, int defaultCollection = -1);
    public string Name { get; }
    public ReadOnlySpan<ExprField> Fields { get; }
    public ReadOnlySpan<ExprCollection> Collections { get; }
    /// The collection {"line": …} and collection-less aggregates refer to (Rules: the situation's lines); null when none.
    public ExprCollection? Default { get; }
    public bool TryGetField(ReadOnlySpan<char> name, [NotNullWhen(true)] out ExprField? field);
    public bool TryGetCollection(ReadOnlySpan<char> name, [NotNullWhen(true)] out ExprCollection? collection);
    /// StableHash64 over names/ids/type keys/exposed/collections — part of every compile-cache key.
    public ulong Hash { get; }
}

/// Enum options for literal typing ("Pix" → the enum value) and editors.
public sealed record ExprEnumDomain(string Name, string? ClrType, ImmutableArray<EnumOption> Options);   // EnumOption: core (X.1)
```

### 4.2 Specials: context values (`$name`)

```csharp
/// A context value an expression reads with {"var": "$name"}. Slot = index into ExprEnv at run time.
public sealed record ExprSpecial(string Name, ExprType Type, byte Slot, string Description);

/// The specials a host offers. Built-in: "$index" (current item index inside a per-item expression, no slot).
public sealed class ExprSpecials
{
    public static ExprSpecials None { get; }
    public ExprSpecials With(ExprSpecial special);          // max 4 slots (ExprEnv)
    /// Alias name → built-in item index (Rules keeps "$lineIndex").
    public ExprSpecials WithItemIndexAlias(string name);
    public bool TryGet(string name, out ExprSpecial special);
}

/// Values of the specials for one run. [InlineArray(4)] of RawValue; passed by `in`, never allocated.
[InlineArray(4)] public struct ExprEnv { private RawValue _s0; }
```

Rules.Dynamic registers `$base` (slot 0, the rule set's amount type, only when `AmountType` is set),
`$effectiveDate` (slot 1, Date) and the alias `$lineIndex`. Composer registers `$today`, `$user`, `$isNew`, etc.

### 4.3 Binding & compiling

```csharp
public sealed record ExprCompileOptions(
    ExprSchema Schema,
    FunctionCatalog Functions,
    ExprSpecials Specials,
    ExprDiagnosticCodes Codes,                // default ExprCodes.Default ("expr.*"); Rules passes its "rules.dynamic.*" map
    ExprLimits Limits,                        // default MaxNodes 500, MaxDepth 32 (today's values)
    bool RequireDeterministic = false);       // Rules: true → non-deterministic functions (today(), now()) are rejected

/// Bind + type-check + compile one expression. Errors are Violations with JSON paths under `path`
/// (e.g. "when.and[1]", "then[0].decrease.percent" — the caller chooses the prefix).
public static class ExprCompiler
{
    public static ExprProgram? Compile(Expr expr, string path, ExprCompileOptions options, List<Violation> errors,
        ExprType? expected = null);           // expected: convert literals / require the type (today's RuleCompiler.Typed)

    /// Same, returning the bound tree for callers that inspect it (Rules: condition templates for traces).
    public static BoundInfo? Bind(Expr expr, string path, ExprCompileOptions options, List<Violation> errors);
}

/// What a caller may learn from a bound expression (kept small; BoundExpr itself stays internal).
public readonly record struct BoundInfo(ExprType Type, bool IsLiteral, RawValue Literal, ExprKind Kind,
    string? FieldName, int ItemCollection /* -1 = not per-item */, ImmutableArray<BoundInfo> Args);

/// Diagnostic codes the binder reports (all strings; a host maps them to its own namespace).
public sealed record ExprDiagnosticCodes(string Malformed, string UnknownField, string FieldNotExposed, string NoItems,
    string NestedAggregate, string UnknownFunction, string Unimplemented, string NonDeterministic, string Arity, string Type,
    string BadLiteral, string UnknownSpecial, string TooLarge, string AmbiguousCollection);
```

### 4.4 Running

```csharp
/// What the program reads. Implemented by a struct or ref struct → the JIT specializes Run per reader
/// (direct calls, no boxing, no virtual dispatch).
public interface IFieldReader
{
    RawValue Read(ushort fieldId);
    int Count(ushort collectionId);
    RawValue ReadItem(ushort collectionId, int index, ushort fieldId);
}

/// A compiled expression: flat postfix code over a pooled RawValue stack (today's DynProgram, unchanged semantics).
public sealed class ExprProgram
{
    public ExprType ResultType { get; }
    /// -1 = runs once; otherwise the collection it runs per item (Rules: a per-line rule; Composer: a computed column).
    public int ItemCollection { get; }

    public RawValue Run<TReader>(ref TReader reader, in ExprEnv env, int item = -1)
        where TReader : IFieldReader, allows ref struct;
    public bool RunBool<TReader>(ref TReader reader, in ExprEnv env, int item = -1)
        where TReader : IFieldReader, allows ref struct;
}
```

Semantics are unchanged from today and pinned by tests: total (÷0/overflow → null), null-propagating calls,
short-circuit `and/or/coalesce`, `ValueEquals` across numeric storages, aggregates skip nulls for `minOf/maxOf`.

### 4.5 JSON, text, functions

- `ExprJson.Read(ref Utf8JsonReader, string path)` / `ExprJson.Read(ReadOnlySpan<byte>)` / `ExprJson.Write(Utf8JsonWriter, Expr)`.
  `RuleSpecJson` calls these for `when` and every directive value. The format is **byte-identical** to today's, plus the new
  `item` node (XD4).
- `ExprText.Render(Expr)`: unchanged output (traces depend on it); `item` renders as `Items.Qty`, `line` stays `line.Qty`.
- Functions: today's `RuleFunction` API under new names (XD2), plus `bool IsDeterministic` (default `true`; set by
  `[ExprFunction(Deterministic = false)]` or `ExprFunction.Create(..., deterministic: false)`). `FunctionCatalog.Version`
  also hashes the flag.

## 5. Steps

Each step ends with **build + all listed suites green** before the next one starts.

| Step | Work | Touches | Green suites |
|---|---|---|---|
| **X.0 Baseline** | Add `YFex.Expressions.Benchmarks` **against the current Rules.Dynamic** with today's three measured scenarios (false condition; applied simple rule; sum over lines + 2 functions) and a compile benchmark (60 µs/rule). Record numbers in §7. Record test counts. | new project only | all Rules/Validation suites (baseline counts in §8) |
| **X.1 Core prep** | Move `EnumOption` from `YFex.Rules` (`Situations/ISituation.cs`) to YFex core (`YFex/Values/EnumOption.cs`, namespace `YFex`) (XD3). Usages found on 2026-10-01: `ISituation.cs`, `Tables/TableContracts.cs`, `Tracing/RuleSetDescription.cs`, `YFex.Rules.Tests/Generated/GeneratedScenarioTests.cs`, Rules.Dynamic (binder/authoring). The Rules generator doesn't name the type, but verify its emitted code still compiles. Add `RawValueConvert.FromDecimal(decimal, RawType)` = today's `Amounts.ToRaw` body; `Amounts.ToRaw` delegates to it (XD6). | YFex core, YFex.Rules | YFex.Tests, Rules, Rules.Generator, Rules.Dynamic, Rules.Admin |
| **X.2 New project, copy-then-generalize** | Create `YFex.Expressions` and **copy** (not move yet) Expr/ExprKind/ExprText/DynType→ExprType/functions/binder/emitter/program. Generalize per §2: `ExprSchema` instead of `TS.Schema`, `IFieldReader` instead of `TS.Read*`, `ExprSpecials` instead of hard-coded specials, `ExprDiagnosticCodes` instead of `DynamicRuleCodes`, `ExprEnumDomain` instead of `SituationFieldInfo`. Port the expression-level tests to `YFex.Expressions.Tests` with an array-backed test reader. | new projects | Expressions.Tests (new) + everything from X.1 |
| **X.3 Generator** | Move `FunctionGen` (+ diagnostics YFR0040–0044 → **YFE0001–0005**) from Rules.Generator into `YFex.Expressions.Generator`, emitting `global::YFex.Expressions.*`. Move the 6 function-generator tests. | Rules.Generator, new generator | Expressions.Generator.Tests, Rules.Generator.Tests (27 − 6 = 21) |
| **X.4 Re-point Rules.Dynamic** | Delete `Binder/Emitter/DynProgram/DynType/Expr/ExprText/Functions/*` from Rules.Dynamic. Add `SituationExprSchema.For<TS>()` (cached static: fields 1:1 with situation ids, lines field → the default collection named after it, `Exposed` copied, enum fields → `ExprEnumDomain`) and `SituationReader<TS>` (`readonly ref struct` over `ref readonly TS`). `RuleCompiler` calls `ExprCompiler` with Rules' specials, **`rules.dynamic.*` codes** (XD1) and `RequireDeterministic = true`. `CompiledRule` stores `ExprProgram`s and runs them with `SituationReader<TS>` + `ExprEnv { [0] = base, [1] = date }`. `RuleSpecJson` keeps the envelope and calls `ExprJson` for expressions. `DynamicRulesAuthoring` keeps the rule part and calls `ExprAuthoring` for operators/functions/expression `$defs`. Cache key adds `ExprSchema.Hash` (replaces `ComputeSchemaHash`). | Rules.Dynamic, its tests (usings/type names only) | **Rules.Dynamic.Tests 19 with unchanged assertions**, Rules 64, Rules.Admin 22, Rules.Data 15, Capabilities (+Cqrs), Validation.Smoke AOT |
| **X.5 New capabilities** | Named collections: `{"item": ["Items", "Qty"]}`, aggregate collection inferred from the item fields it reads (XD4); `AmbiguousCollection` error when an aggregate mixes collections or reads none while the schema has several. `IsDeterministic` + `RequireDeterministic`. Specials for screens. Tests for each. | Expressions | Expressions.Tests |
| **X.6 Text syntax** *(may slip to Composer P2)* | `ExprParser.TryParse(ReadOnlySpan<char>, out Expr, List<Violation>)`: Pratt parser for the `ExprText` grammar (`Total ≥ 500 and Payment = "Pix"`, ASCII aliases `>=`, `<>`, `*`, `/`), round-trip property test: `Parse(Render(e)) ≡ e` for generated trees. | Expressions | Expressions.Tests |
| **X.7 Docs** | validation 05 §8 + 13 (log entry "Phase X"), validation/README layers, composer-design §4/§8.2 (dependency correction), this file → status "done" + measured numbers. | docs | — |

## 6. Rules.Dynamic after X.4 (what stays)

```
YFex.Rules.Dynamic
  Model/        RuleSpec, DirectiveSpec, DirectiveSpecKind, RuleSpecJson (envelope; expressions via ExprJson)
  Compiler/     RuleCompiler (placement, directive checks, caps, condition templates), DynamicRuleCodes (unchanged)
  Runtime/      CompiledRule, CompiledDirective (hold ExprProgram), SituationReader<TS>, SituationExprSchema
  DynamicRules  codec + compile cache (key: rule set code version, ExprSchema.Hash, FunctionCatalog.Version, payload)
  DynamicRuleMessages, DynamicRulesAuthoring (rule part)
```

Public types that leave Rules.Dynamic (hard move; all usages are in-repo, the D28/C5 precedent): `Expr`, `ExprKind`,
`ExprText`, `DynType` (→ `ExprType`), `TypeRef`, `TypeConstraint`, `RuleFunction*` (→ `ExprFunction*`), `FunctionCatalog`,
`BuiltInFunctions`, `[RuleFunctions]/[RuleFunction]` (→ `[ExprFunctions]/[ExprFunction]`), `RuleFunctionArgs`
(→ `ExprFunctionArgs`).

## 7. Performance guard

Today's numbers (Release, validation/13 "Phase 6 implementation log"): compile ≈ 60 µs/rule (parse ≈ 15 µs); false
dynamic condition ≈ +0.4 µs per evaluation; applied simple rule ≈ +2.6 µs; complex (sum over lines + 2 functions)
≈ +9.5 µs vs 3.5 µs baseline.

| Scenario | Baseline (X.0) | After X.4 | Limit |
|---|---|---|---|
| false condition | _record_ | _record_ | ≤ +5 % |
| applied simple rule | _record_ | _record_ | ≤ +5 % |
| sum over lines + 2 functions | _record_ | _record_ | ≤ +5 % |
| compile one rule | _record_ | _record_ | ≤ +10 % |
| allocations per evaluation (warm) | _record_ | _record_ | **no increase** |

Why no slowdown is expected: `Run<SituationReader<TS>>` is specialized by the JIT for the struct reader (direct
`TS.Read` calls, as today). `ExprEnv` is an inline array passed by `in` (no allocation). The stack pool is unchanged.
If a scenario exceeds its limit, check first that the reader is a struct/ref struct and that `Run` is not called through
an interface-typed variable (boxing).

## 8. Acceptance

1. **Rules behavior unchanged:** Rules.Dynamic.Tests (19) pass with **identical assertions**, including the
   `path:code` pairs (`when.==[1]:rules.dynamic.literal`, `then[0].decrease.percent:rules.dynamic.over-cap` …), trace
   details and explanations. Rules 64, Rules.Admin 22, Rules.Data 15, Rules.Generator 21 (+6 moved), Capabilities 28 +
   Cqrs 7 + Generator 5, Validation 62, Validation.Generator 50: green.
2. **Stored data unchanged:** every rule spec JSON stored before X reads back and re-canonicalizes to the **same bytes**
   (golden test over the specs in the test fixtures).
3. **Generic use works:** an `ExprSchema` built at run time with two collections + an array-backed reader compiles and
   runs formulas, per-item expressions and aggregates per collection (Expressions.Tests).
4. **AOT:** Validation.Smoke native-AOT publish still has no IL warnings, and it now also evaluates one expression over a
   runtime schema.
5. **Performance:** §7 limits met. Numbers recorded in this file.

## 9. Decisions

**Status:** the user said "you can start it" (2026-10-01) without commenting on individual items, so XD1–XD7 were implemented
**as recommended** (XD4/XD5's screen-facing parts are X.5). The user can still revise any of them; XD2 supersedes the "alias"
wording of composer-design C1.

| # | Decision | Recommendation | Why |
|---|---|---|---|
| ⚑ XD1 | Diagnostic codes | Binder takes an `ExprDiagnosticCodes` map. Rules passes `rules.dynamic.*`; Expressions' default is `expr.*` | Rules output stays byte-identical (tests and editors key on codes); Composer gets neutral codes |
| ⚑ XD2 | Function API naming | **Hard rename** to `ExprFunction`, `[ExprFunctions]/[ExprFunction]`, `ExprFunctionArgs`, `DynType` → `ExprType`; diagnostics YFE0001–0005. **No alias** for `[RuleFunctions]` | Same reasoning as D28/C5: all usages in-repo, days old. An alias doubles the generator's attribute matching forever. ⚠ composer-design C1 said "kept as an alias"; confirming XD2 changes C1 |
| ⚑ XD3 | `EnumOption` | Move to YFex core (`YFex.EnumOption`) | D20 rule ("useful beyond rules → core"): Expressions, Composer pickers and Rules all use it. Unifying it with Validation's `EnumMember(Code, Value)` is a separate follow-up |
| ⚑ XD4 | Named-collection syntax | `{"item": ["Items", "Qty"]}`; aggregate collection **inferred** from the item fields inside it; `{"line": "Qty"}` stays as the default-collection shorthand | Keeps the one-key-per-object JSON rule and all stored specs valid; no new aggregate operators |
| ⚑ XD5 | Specials | `ExprSpecials` table, max 4 slots (`ExprEnv` inline array) + built-in item index | 4 covers Rules (2) and Composer's first needs; growing it later is a non-breaking struct change |
| ⚑ XD6 | `Amounts.ToRaw` | Body moves to core `RawValueConvert.FromDecimal(decimal, RawType)`; `Amounts.ToRaw` delegates | Expressions must not reference YFex.Rules |
| ⚑ XD7 | Determinism | `ExprFunction.IsDeterministic` (default true) + `ExprCompileOptions.RequireDeterministic` (Rules: true) | Enforces composer-design C1's layering: screen-only functions like `today()` can't enter rules (replay/audit safety) |

## 10. Out of scope

- Closure-compiled typed delegates or any faster evaluator (only if a benchmark asks for it, composer-design §3).
- Unifying `EnumOption` with Validation's `EnumMember`.
- The Composer side (screen schema builder, `ScreenState : IFieldReader`): Composer P1/P2.
- Versioned dynamic messages (open deviation in validation/13): unrelated to the extraction.

---

## 11. Handoff — for the next AI session (written 2026-10-01, end of X.4)

### 11.1 Session start protocol

1. Read this file §11 → §12, then `../studio/02-new-screens.md` §0.2 (decisions C1–C5) only if you need context on *why*.
2. Check the tree: `git -C C:\Dev\YFex\YFex status --short`. **Nothing of Phase X is committed.** The working tree also contains
   **another session's unrelated, in-progress work** (see 11.3) — never stage, revert or "fix" those files.
3. Build/test in **Release** (`-c Release`). The RoselYn MCP server locks the Debug generator DLLs
   (`MSB3027 … locked by RoselYn.Mcp`); Release output is not locked.
4. BenchmarkDotNet 0.15.8 can't run out-of-process on net11 → the benchmark project uses `InProcessEmitToolchain` (already wired).

### 11.2 Phase X files (only these belong to this work)

```
 M yfex.slnx                                                   (+ /Expressions/ folder: 5 projects)
 M src/YFex/Values/RawValueConvert.cs                          (+ FromDecimal — XD6)
?? src/YFex/Values/EnumOption.cs                               (moved from YFex.Rules — XD3)
 M YFex.Rules/src/YFex.Rules/Definition/Amounts.cs             (ToRaw delegates to RawValueConvert.FromDecimal)
 M YFex.Rules/src/YFex.Rules/Situations/ISituation.cs          (EnumOption removed)
 M YFex.Rules/src/YFex.Rules.Generator/{Diagnostics.cs,RulesGenerator.cs,AnalyzerReleases.Unshipped.md}   (functions removed)
 D YFex.Rules/src/YFex.Rules.Generator/FunctionGen.cs
 M YFex.Rules/tests/YFex.Rules.Generator.Tests/{GeneratorTests.cs,*.csproj}                                  (6 function tests moved)
 M/D/?? YFex.Rules/src/YFex.Rules.Dynamic/**                   (re-pointed, §6; new RuleSpec.cs, DynamicRuleCodes.cs, SituationExpressions.cs)
 M YFex.Rules/tests/YFex.Rules.Dynamic.Tests/{DynamicRuleTests.cs,GeneratedFunctionTests.cs,*.csproj}        (type renames only)
?? YFex.Rules/tests/YFex.Rules.Dynamic.Tests/GoldenProbe.cs      (equivalence probe; no-op unless YFEX_GOLDEN_OUT is set)
?? YFex.Expressions/**                                           (YFex.Expressions, .Generator, .Tests, .Generator.Tests, .Benchmarks)
?? docs/internal/expressions/phase-x-extraction.md             (this file)
  M docs/internal/composer-design.md                           (decisions C1–C5, Phase X links)
```

Suggested commit (only when the user asks): stage exactly the paths above, message
`Phase X (X.0–X.4): extract YFex.Expressions from YFex.Rules.Dynamic`.

### 11.3 Other work in the same tree (NOT Phase X — do not touch)

Seen on 2026-10-01 11:20–11:35, uncommitted and mid-change (did not compile then):
- `C:\Dev\YFex\YFex\src\YFex\Diagnostics\*` (logging: LogLevel, OperationKind, TraceContext, TraceId, LogWriter, ActiveOperation…)
  — `CS0103 'Operations' does not exist` → **every build that includes YFex core failed** while it was in progress.
- `C:\Dev\YFex\YFex\docs\internal\logging\*`.
- `C:\Dev\YFex\YFex.Data\src\YFex.Data\**` (10 files; `QueryParameterBuilder.cs` → `CS8120`) → blocks **Rules.Data.Tests**.

Because of this, X.4 was verified in a **temporary git worktree** (HEAD + only the Phase X changes), since removed.

### 11.4 What to do next (in order)

| # | Task | Done when |
|---|---|---|
| 1 | **Re-verify in the real tree** once the other work compiles: `dotnet build yfex.slnx -c Release`; run YFex.Tests, Validation(+Generator), Rules, Rules.Generator, Rules.Dynamic, Rules.Admin, **Rules.Data**, Expressions(+Generator), and the Capabilities suites (`..\YFex.Capabilities\capabilities.slnx`: 28 + Cqrs 7 + Generator 5). | All green with the counts in §12.2 |
| 2 | **AOT (acceptance 4):** add a `YFex.Expressions` reference to `YFex.Validation/app/YFex.Validation.Smoke`, evaluate one expression over a runtime `ExprSchema` with a struct reader there, then `dotnet publish YFex.Validation/app/YFex.Validation.Smoke -c Release -r win-x64` → **zero IL2xxx/IL3xxx**. | Publish clean |
| 3 | **Benchmarks on a quiet machine** (nothing else building): old vs new as in §12.2 (a HEAD worktree with only the benchmark project copied in, vs. the current tree); fill §7's table. | Within §7 limits |
| 4 | **X.5 — named collections (XD4):** append `ExprKind.Item` (keep the existing order); `ExprJson` read/write `{"item": ["Items", "Qty"]}`; `ExprText` renders `Items.Qty`; binder resolves `ExprSchema.TryGetCollection` + item field; aggregates **infer** their collection from the item reads inside (default collection when only `line`/none); new `ExprDiagnosticCodes.AmbiguousCollection` (an aggregate mixing collections, or none while the schema has several and no default) — Rules maps it to a new `rules.dynamic.ambiguous-collection`. `BoundExpression.CollectionId` already flows to `Op.Agg` (D) and `Op.ItemField` (B). `ExprAuthoring.WriteExpressionDefs` gets an `item` def when a schema has collections. Tests over a two-collection schema. GoldenProbe must stay identical for Rules. | Expressions.Tests green; Rules golden unchanged |
| 5 | **X.6 (optional, may slip to Composer P2):** text syntax parser (§5). | Round-trip property test |
| 6 | **X.7 docs:** `../validation/05-decision-tables-and-dynamic-rules.md` §8 (engine now in YFex.Expressions; `[ExprFunctions]`), `../validation/13-phases-and-acceptance.md` (log entry "Phase X" pointing here), `../validation/README.md` layers (Layer 0.5 YFex.Expressions; Rules.Dynamic → Expressions), `../studio/02-new-screens.md` §4/§8.2 status. | Docs consistent |

---

## 12. Implementation log (2026-10-01)

### 12.1 What was done

- **X.0** `YFex.Rules/benchmark/YFex.Expressions.Benchmarks` (5 scenarios, `[MemoryDiagnoser]`, in-process toolchain). Baseline tests:
  YFex 65, Validation 62, Validation.Generator 50, Rules 64, Rules.Generator 27, Rules.Dynamic 19, Rules.Admin 22, Rules.Data 15.
- **X.1** `EnumOption` → YFex core (namespace `YFex`; Rules files resolve it through the parent namespace, no `using` changes; the
  Rules generator emits target-typed literals, so it needed no change). `RawValueConvert.FromDecimal`; `Amounts.ToRaw` delegates.
- **X.2** `YFex.Expressions` — Model: `Expr`, `ExprJson`, `ExprText`; Typing: `ExprType`, `ExprEnumDomain`; Functions:
  `ExprFunction` (+`IsDeterministic`), `FunctionCatalog`, `BuiltInFunctions`, attributes, `ExprFunctionArgs`; Schema:
  `ExprSchema`/`ExprField`/`ExprCollection`, `ExprSpecials`/`ExprSpecial`, `ExprEnv` (InlineArray 4); Compiler:
  `ExprCompileOptions`, `ExprDiagnosticCodes`, `ExprLimits`, `ExprBinder`, `BoundExpression`, `ExprCompiler`; Runtime:
  `IFieldReader`, `ExprProgram.Run<TReader>`; Authoring: `ExprAuthoring`. Tests: **21** (incl. zero-allocation warm run,
  ref struct reader, generated functions).
- **X.3** `YFex.Expressions.Generator` (`[ExprFunctions]`, YFE0001–0005, emits `deterministic: false`), bundled as analyzer of
  YFex.Expressions; `YFex.Expressions.Generator.Tests` **6** (ported).
- **X.4** Rules.Dynamic re-pointed (§6): `SituationExprSchema<TS>`, `SituationReader<TS>` (ref struct over `ref readonly TS`),
  `RuleExpressions.Options` (`$base` slot 0 or unavailable → `rules.dynamic.no-amount`; `$effectiveDate` slot 1; `$lineIndex` =
  item index; codes map `DynamicRuleCodes.Expression`; `RequireDeterministic = true`), options cached per rule set in a
  `ConditionalWeakTable`, compile-cache key uses `ExprSchema.Hash`. Engine files deleted from Rules.Dynamic; `FunctionGen` and
  YFR0040–0044 removed from Rules.Generator (they were unshipped).

### 12.2 Verification

- **Golden equivalence** (`GoldenProbe`, old HEAD vs new, same machine): canonical spec bytes, `ExprText`, `CatalogJson`,
  `SpecJsonSchema`, validity of 5 broad specs and the `path:code` lists of 16 invalid specs — **identical except one line**, the
  deliberate `Total * 2` change (deviation 1).
- **Tests after X.4** (isolated tree): YFex 65, Validation 62, Validation.Generator 50, Rules 64, Rules.Generator **21** (27 − 6
  moved), Rules.Dynamic **20** (19 + GoldenProbe; the 19 with **unchanged assertions**), Rules.Admin 22, Expressions 21,
  Expressions.Generator 6. **Not run yet:** Rules.Data (blocked, 11.3), Capabilities (references the main tree's core), AOT publish.
- **Benchmarks** (noisy machine — another session was building; the untouched "code rules only" path itself varied 536–763 ns).
  Old vs new interleaved, two rounds, ns:

  | Scenario | X.0 | old r1 | new r1 | old r2 | new r2 |
  |---|---|---|---|---|---|
  | code rules only | 536 | 763 | 592 | 763 | 758 |
  | dynamic, condition false | 591 | 872 | 679 | 884 | 913 |
  | dynamic, applied (simple) | 1,315 | 1,770 | 1,486 | 1,690 | 1,795 |
  | dynamic, sum + 2 functions | 1,493 | 1,950 | 1,786 | 1,940 | 1,988 |
  | parse + bind + compile | 2,201 | 2,378 | 2,364 | 3,772 | 3,794 |

  Evaluation allocations unchanged (1.16 / 1.59 / 1.56 KB). Compile 8.74 → 8.91 KB (larger bound nodes). No regression
  distinguishable from noise; re-measure on a quiet machine (11.4 #3). The Phase 6 log's "≈ 60 µs per compile" was not
  reproduced: a small rule compiles in ~2–4 µs.

### 12.3 Deviations from the plan (and why)

1. **Bug fix (behavior change, accept-only):** in `*` and `/`, a number literal next to an amount stays a number (`Total * 2` =
   Money 1920). Before, the literal was coerced to the amount (Money 2 → Money × Money → `rules.dynamic.type`). Only specs that
   were rejected before are affected. Test: `Number_literals_scale_amounts_without_becoming_amounts`; golden `invalid[15]`.
2. **API:** `ExprBinder` is **public** with today's binder shape (`Bind`, `Bind(…, expected)`, `ConvertLiteral`, `Error`,
   `ItemCollection`), and `BoundExpression` is public (instead of §4.3's `BoundInfo` record): Rules binds many expressions per
   spec and needs "does any of them read lines". `ExprCompiler.Compile` (one-shot) and `ExprCompiler.Emit` (bound tree) as planned.
3. **Order:** X.3 only added the new generator; removing `FunctionGen` from Rules.Generator happened in X.4 (removing it in X.3
   would have broken Rules.Dynamic's `[RuleFunctions]` tests before the switch).
4. **Detail texts** (not codes, not paths) changed slightly: "not exposed to runtime rules" → "not exposed to expressions";
   unknown `$name` lists the offered specials. Codes and paths are identical (golden).
5. **`$lineIndex` in a situation without lines** used to turn the rule into a (never-running) line rule; it is now a plain rule
   where `$lineIndex` is null. Degenerate case; no test or stored spec affected.
6. **Collection-as-value check** is generic: a top-level name that is also a collection name → `type` error with the old message
   (`{"var":"Lines"}`), preserving Rules' behavior.
7. **Not built:** `ExprMessages` (en/pt texts for `expr.*`) — no catalog texts exist for `rules.dynamic.*` either (details are
   used). Add with Composer when screens show binder errors to end users.
8. `FunctionCatalog.Version` is unchanged for deterministic catalogs (the non-deterministic flag is hashed only when present).
9. Some edited files were rewritten with LF line endings by the tools; git's autocrlf normalizes them (warnings only).
