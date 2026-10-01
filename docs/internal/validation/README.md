# YFex.Validation & YFex.Rules — Design

> **Status:** approved design; Phases 0–4 implemented (Phase 3: 2026-09-29, D23–D28; Phase 4: 2026-09-30, D29–D38 — 4a engine,
> domain-agnostic refactor, 4b runtime editing incl. SQL store, audit, what-if, device cache) — progress and deviations in
> [13-phases-and-acceptance.md](13-phases-and-acceptance.md). **Phase 6 B + C done** (2026-09-30, "Phase 6 implementation log"):
> `YFex.Rules.Dynamic` (JSON rules with arithmetic + built-in/app/contract functions) and the AI schema export (validatable types,
> situations, rule sets, rule specs; YFex.Capabilities `ParametersSchema`). Open: Phase 6A Fiscal (deferred by the user), then
> Phase 5 (Avalonia) last (needs a reusable table/grid design).
> **Audience:** AI sessions that will implement it. Read this file first, then only the files your
> phase needs (see [13-phases-and-acceptance.md](13-phases-and-acceptance.md)).
> **Normative vs sketch:** type names, member names and signatures in code blocks are **normative**
> unless the block is marked `// (sketch)`. Comments inside code explain *how it works* — keep them
> (adapted) as XML docs when implementing.

---

## Why this exists

Today the ecosystem has **four unrelated validation result types** and a half-built, unused
validation seed inside YFex.State (details: [01-current-state-and-migration.md](01-current-state-and-migration.md)).
At the same time, business modules (sales, fiscal, finance) need **rule enforcement**, not just
field validation: *"which discount rules apply to this sale, which installment limits, what needs a
manager, and exactly what was applied"*.

This design delivers one reusable foundation for both, usable by UI (Avalonia via YFex.State) and
server (YFex.Cqrs / YFex.Messaging), AOT-ready, reflection-free and allocation-conscious.

## The mental model — three layers, two principles

| Layer | Question | Example | Owner |
|---|---|---|---|
| **1. Shape validation** | Is the input well-formed? | Name 2–80 chars, document is a valid CPF/CNPJ, installments 1–48 | **YFex.Validation** (generated, pure, sync; runs on client *and* server) |
| **2. Rule enforcement** | Which business rules apply here, and what do they allow/require/adjust? | Credit card ≥ R$1000 → max 12x; Gold → 5% stackable up to 20%; seller discount > 5% → manager approval | **YFex.Rules** (evaluates a typed **Situation**) |
| **3. Domain invariants** | Can this aggregate make this state change? | Cannot refund more than paid | Domain model / handler (`Result`) — **not** this library |

**Principle A — gather async, decide sync.** Everything that needs I/O (customer tier, stock, rule
tables, the logged-in actor) is loaded *before* evaluation into an immutable **Situation**. The rules
engine is pure, synchronous and deterministic → testable, cacheable, replayable (offline), cheap
enough for every UI change.

**Principle B — rules produce constraints; validation checks values against them.** An installment
rule doesn't answer "valid/invalid"; it outputs *"Installments ≤ 6, installment ≥ R$50"*. The UI
uses that to configure the picker; the server checks the command against the same output. Static
constraints (attributes/fluent) and dynamic constraints (rules) reach controls through **one
channel** (`IValidationSource`), so auto-binding handles both.

## Files

| # | File | Contents |
|---|---|---|
| 01 | [current-state-and-migration](01-current-state-and-migration.md) | Inventory, known bugs (Phase 0), old → new mapping, fiscal/xml/cqrs migration |
| 02 | [validation-core](02-validation-core.md) | `YFex.Validation` runtime types: Violation, FieldPath, writer, results, rule contracts, constraints, attributes, built-ins |
| 03 | [validation-generator](03-validation-generator.md) | `YFex.Validation.Generator`: inputs, emitted code, invariants, fluent parsing, diagnostics, coordination with State generator |
| 04 | [rules-engine](04-rules-engine.md) | `YFex.Rules`: Situation, rules, rule sets, stages, groups, Directives, Decision, layers, versions, fingerprint |
| 05 | [decision-tables-and-dynamic-rules](05-decision-tables-and-dynamic-rules.md) | Typed decision tables, table sources, runtime editing lifecycle, parameters, `YFex.Rules.Dynamic` |
| 06 | [trace-and-explanation](06-trace-and-explanation.md) | Fingerprint vs Trace, applied adjustments (persisted), trace levels, condition splitting, UI breakdown |
| 07 | [state-integration](07-state-integration.md) | YFex.State changes: bag, FieldState, rules on VMs, `[ValidatesAs]`, `[MapsTo]`, timing, `ApplyServer`, Decisions in VMs |
| 08 | [ui-avalonia](08-ui-avalonia.md) | Constraint projection, attached properties, error presenter, `YRuleBreakdown`, table editor, approval flow |
| 09 | [cqrs-messaging](09-cqrs-messaging.md) | Pipeline stages, fluent config API, typed slots, wire format, handlers, queries, offline/fingerprint |
| 10 | [messages-localization](10-messages-localization.md) | Message catalog layers (dynamic → tenant → app → module → built-in) |
| 11 | [performance-aot-and-pitfalls](11-performance-aot-and-pitfalls.md) | AOT/perf checklist, pitfalls & tradeoffs (drift, time, debounce, generators…), reference libraries |
| 12 | [workflow-example-sale](12-workflow-example-sale.md) | End-to-end POS sale: contracts → rules → tables → VM → view → server → failures → tenant edit → offline → tests |
| 13 | [phases-and-acceptance](13-phases-and-acceptance.md) | Implementation phases, tasks, acceptance criteria, what each session loads |

## Projects

```
YFex/src/Validation/
  YFex.Validation              runtime core (refs YFex core only)
  YFex.Validation.Generator    netstandard2.0 incremental generator (bundled as analyzer of YFex.Validation)
  YFex.Rules                   rules engine (refs YFex core for Money/Percentual/EnumCode + YFex.Validation)
  YFex.Rules.Generator         netstandard2.0 generator (bundled as analyzer of YFex.Rules)
  YFex.Rules.Dynamic           runtime-authored rules: JSON spec → type-checked → postfix program; functions (built-in, [RuleFunctions], contracts);
                               authoring catalog + spec JSON Schema for editors/AI (→ Rules, Persistence)
  YFex.Rules.Admin             RuleAdmin module: publish/history/what-if/approvals, publish pipeline, client sync + device cache (→ Rules, Cqrs, Messaging.Rpc)
  YFex.Rules.Data              SQL IRuleStore over YFex.Data (C:\Dev\YFex\YFex.Data) — SQLite/Postgres/SQL Server/Oracle
  YFex.Validation.Testing      assertion helpers + rule scenario helpers
  YFex.Validation.Tests / YFex.Rules.Tests / *.Generator.Tests
YFex/src/Cqrs/
  YFex.Cqrs.FluentValidation   optional adapter (the ONLY place FluentValidation is referenced)
YFex/src/YFex/                 (existing YFex core) — receives the reusable base types listed below
```

Thin integrations live **inside the owning libraries** (no extra packages):
YFex.State (+Generator, +Mvvm), YFex.Cqrs, YFex.Messaging.Rpc, YFex.Avalonia (YFex.UI repo).

**Why not one project per scenario (UI / server / …):** each integration is a few hundred lines that
needs internals of the owning library (State's setter pipeline, Cqrs's dispatch pipeline); separate
adapter packages would only create a diamond of tiny packages. The real split is **validation vs
rules**: validation is small, hot-path and used by everyone; rules are bigger, versioned, data-driven
and only needed by business modules. Separate projects exist only where a dependency must stay
optional (FluentValidation adapter, Dynamic rules) or a generator is required.

**Dependency layers** (extends the table in `YFex/CLAUDE.md`):

```
Layer 0   : YFex (core)                           ecosystem base types (Money, Percentual, RawValue, FieldPath, …)
Layer 0.5 : YFex.Validation (→ YFex core)         must NOT reference YFex.State/Cqrs/Rules
Layer 1   : YFex.Rules (YFex + YFex.Validation), YFex.State (→ YFex.Validation), YFex.UI.Abstractions
Layer 1.5 : YFex.Rules.Dynamic
Layer 2   : YFex.Cqrs (→ YFex.Validation, YFex.Rules), YFex.State.Mvvm …
UI repo   : YFex.Avalonia (→ YFex.Validation, YFex.Rules)
```

## What lives in YFex core (D20)

YFex core (`YFex/src/YFex`, `net11.0`, AOT-compatible) is the home of the ecosystem's **base types**.
Every library in this design may reference it. **Rule:** a type goes to core when it is useful
beyond validation/rules (Composer, Capabilities/AI, Persistence, Xml, UI, Finance/Fiscal…); types
whose meaning is validation- or rules-specific stay in YFex.Validation / YFex.Rules.

**New types added to YFex core by this work** (Phase 1 creates them there):

| Type | Namespace | Why core | Defined in |
|---|---|---|---|
| `ValueKind`, `RawValue` (+ `Payload16`) | `YFex` | non-boxing scalar for any boundary: rule args, situation schema, decision tables, Composer slots/field types (`RawType` + `FieldConstraints`), AI tool I/O | 02 §1.1 |
| `FieldPath`, `IndexPath` | `YFex` | member path with indices into an object graph: violations, Composer bindings, Xml/fiscal paths, UI | 02 §2 |
| `Bounds<T>` | `YFex` | inclusive [Min, Max] pair (guards, input limits, facets) | 04 §3.2 |
| `OptionSet` | `YFex` | compact set of discrete values (bitset ≤ 64, else array): allowed options, enum facets, filters | 02 §7 |
| `Rate` ([Union] `PercentRate(Percentual)` \| `FixedRate(Money)`) | `YFex` | "percent or amount" used by discounts, fees, interest, commissions (Finance/Payment/Fiscal) | 04 §5 |
| `RoundingPolicy` | `YFex` | money rounding rules (decimals + `MidpointRounding`) shared by Rules, Finance, Fiscal | 04 §4 |
| `StableHash64` | `YFex.Helpers` | deterministic 64-bit hash (own XxHash64-style implementation — **no** `System.IO.Hashing` package) for fingerprints, cache keys, situation hashes | 04 §8 |
| `IMessageSource`, `IMessageCatalog`, `MessageCatalog` (code + `RawValue` args → text) | `YFex.Localization` | message catalogs are needed by any library that shows coded messages (Cqrs errors, notifications, Composer, receipts), not only validation | 10 §2 |

**Existing YFex core types this design reuses** (do not re-create):

| Existing | Used for |
|---|---|
| `Money`, `Percentual` (`namespace YFex`), `Quantity`, `Mass`, `Length` | `RawValue` kinds, bounds, adjustments, cells — `RawValue.From(Money)` etc. live directly on `RawValue` |
| `YFex.Extensions.EnumExtensions` / `EnumCodeAttribute` (`GetCode`/`FromCode`/`GetCodeMap`) | `ValueKind.EnumCode`, schema enum options, stable wire codes |
| `YFex.Collections.RefArrayPoolBufferWriter<T>` / `ArrayPoolBufferWriter<T>` / `IBuffer<T>` | `Situation.WriteTo` buffers, serialization payloads |
| `YFex.Guard` | argument checks in public APIs |
| `YFex.Helpers.HashHelpers` | sizing of any custom hash tables |

Stays **out** of core (validation/rules meaning only): `RuleCode`, `Severity`, `Violation`,
`ViolationWriter`/`Collector`/`List`, `ValidationResult`, `FieldConstraints`, `IValidationSource`,
all rule-engine types. (`FieldConstraints` depends on `OptionSet`/`RawValue` from core.)

## Hard constraints (apply to every file)

1. **AOT & trimming:** `IsAotCompatible=true`, `EnableTrimAnalyzer=true`. No reflection
   (`GetCustomAttributes`, `Activator`, `MakeGenericType`), no `Expression<>`/`Expression.Compile`,
   no `dynamic`. All metadata comes from generators.
2. **Unions:** use the **`[Union]` attribute + `IUnion` tagged struct** pattern (as in
   `YFex/src/Cqrs/YFex.Cqrs/Result.cs`, `QueueableResult.cs`), **not** the native `union` keyword
   (it stores cases as `object` → boxes value-type cases). Pattern:

   ```csharp
   [Union]
   public readonly struct Verdict : IUnion
   {
       private readonly ViolationList _violations;   // payload fields, one per distinct payload
       private readonly ApprovalRequest _approval;
       private readonly byte _tag;                    // 0 = none, 1..n = case

       public object? Value => _tag switch { … };     // required by IUnion; BOXES — never call it in library code
       public bool HasValue => _tag != 0;
       public VerdictKind Kind => (VerdictKind)_tag;   // cheap switch without pattern matching
       public bool TryGetValue(out Allowed v)  { … }   // one overload per case → the compiler uses these for
       public bool TryGetValue(out Denied v)   { … }   // non-boxing pattern matching: `v switch { Allowed a => … }`
   }
   ```
   Every case must be a **distinct type** (wrap payloads: `Warned(ViolationList)` and
   `Denied(ViolationList)` are two different structs), otherwise `TryGetValue` overloads collide.
3. **Spans & own types:** text rules take `ReadOnlySpan<char>`; prefer `SearchValues<char>`,
   `Ascii.*`, `[InlineArray]`, `ArrayPool<T>`, own struct enumerators. **No LINQ in library code**
   (tests may use it).
4. **`unsafe`** only where it measurably pays: `delegate*` validation slots (09), bitset scanning (04).
5. **No new third-party dependencies.** Allowed references: BCL, YFex core (whatever it already
   brings), and the YFex libraries named in each file. Existing libraries are *inspiration only* (11).
   FluentValidation only inside `YFex.Cqrs.FluentValidation`. When a base type is reusable, add it to
   YFex core instead of duplicating it (D20).
6. **Generators:** `netstandard2.0`, `LangVersion 12`, `Microsoft.CodeAnalysis.CSharp 4.11.0` pinned
   (copy `YFex.State.Generator.csproj`), incremental (`ForAttributeWithMetadataName`, equatable
   models, `EquatableArray`), bundled into the runtime package via `OutputItemType="Analyzer"`.
7. **Runtime projects:** `net11.0`, `LangVersion preview`, `Nullable enable`, `AllowUnsafeBlocks`
   (copy `YFex.State.csproj`).
8. **YFex conventions (from `YFex/CLAUDE.md`):** `[Observable]` on **partial properties**;
   CQRS messages carry only marker interfaces and **configuration lives in
   `IAggregateConfiguration<T>`** — so for commands/queries, validation is declared with the fluent
   API by default (attributes on messages are supported but not the convention, see D14).

## Decisions log

Confirmed by the user in the design conversation:

| # | Decision |
|---|---|
| D1 | Three layers + Principles A and B as above. |
| D2 | YFex.State validation **moves into YFex.Validation**; State keeps only the per-object bag + generator hooks + INDEI. |
| D3 | Fix the existing YFex.State validation bugs first (Phase 0). |
| D4 | One generic `ValidationResult` lives in YFex.Validation and replaces the State and Cqrs ones; Fiscal/Xml adapt to it. |
| D5 | YFex.Rules generalizes the fiscal `IRegraNFe` pattern (applicability + evaluate + violations). |
| D6 | Violations are **code + path + args**, never pre-built strings; messages are resolved at the edge. |
| D7 | Generator emits per-member metadata (`Rules.Name.MaxLength`, constraints, per-field check, path lookup). |
| D8 | Rule input type is named **Situation** (not "Facts": clashes with xUnit). |
| D9 | Rule outputs are named **Directives** (Deny, Warn, Limit, Grant, Adjust, RequireApproval). |
| D10 | Decision tables use **typed row types** (editable in a UI grid), with multiple sources (code, DB, UI, files) — not CSV-only. Runtime/dynamic rules also exist (Composer). |
| D11 | **Runtime table editing ships in the first release.** |
| D12 | Rules have edit layers: **Sealed (code) / Tenant / Store / User discretion**, controlled by YFex.Authorization permissions, with **guardrails** that editable rules cannot exceed. |
| D13 | Message catalog **per module + app overrides + custom/dynamic** layers. |
| D14 | Both **attributes and CQRS fluent configuration** can declare validation; both feed the same metadata and both work with `[ValidatesAs]`. |
| D15 | `[ValidatesAs<T>]` is optional; VMs can add rules; multiple `[ValidatesAs]` per VM allowed; queries supported. |
| D16 | Debounce/timing belongs to **YFex.State** (reusable elsewhere), never to YFex.Validation. |
| D17 | Trace: applied adjustments are business data (always recorded, persisted with rule **version**); full trace is opt-in; generator makes it explainable to end users. |
| D18 | FluentValidation integration only in a separate adapter project; no direct deps on other validation libs. |
| D20 | **YFex core hosts ecosystem base types**; YFex.Validation/Rules and every other library may reference it. Reusable types created by this work (`RawValue`, `FieldPath`, `Bounds<T>`, `OptionSet`, `Rate`, `RoundingPolicy`, `StableHash64`, message catalog contracts) go to YFex core. |
| D19 | Phase order: 0 fix State → 1 core+generator → 2 State → 3 Cqrs/Messaging → 4 Rules (+runtime tables) → 5 Avalonia → 6 Fiscal/Composer/AI. |
| D21 | P1, P4, P5 **confirmed** by the user (2026-09-29, before Phase 1): `IValueRule<T>`/`ITextRule`/`IAsyncValueRule<T>` + `[ValidateWith<T>]`; 3 severities; Required inference off by default. |
| D22 | P6 **confirmed with refinements** (2026-09-29, start of Phase 2): hooks are `partial void __OnValidate_<Prop>(in T value)` (exact fully-qualified, nullable-annotated type in both generators); batch end = new `protected virtual void StateObject.OnUpdateCompleted()` (outermost `BeginUpdate` scope end), State generator emits `override { base.OnUpdateCompleted(); __OnBatchValidated(); }`; rules for an inherited property must live on the declaring class (diagnostic otherwise); obsolete `[ValidateWith(typeof)]`/`[ValidateAsync(typeof)]` are honoured by the Validation generator through the hooks (async versioning moves to `ValidationBag` tickets). |
| D23 | P2 **confirmed** (2026-09-29, start of Phase 3): the fluent shape-validation method is `.Validation(v => …)`. |
| D24 | P7 **confirmed** (2026-09-29): opt-in `IValidatedCommandHandler<TCmd,TResult>` receiving `Valid<TCmd>` (Phase 4 adds `IRuledCommandHandler`); existing handler interfaces unchanged. |
| D25 | Q-A (2026-09-29): the **server pipeline runs in a validating `IHandlerInvoker` decorator** registered by `AddYFexMessagingRpcServer…` — one place covering the generated `RpcContractImpl`, Wolverine and outbox replay. |
| D26 | Q-B (2026-09-29): structured `Error` crosses the RPC wire as **serializable typed results** — `Error`/`QueueableResult`/`Result` get formatters for the Fusion RPC serializer (registered from YFex.Messaging.Rpc; YFex core/Validation never reference the serializer); `RegisterRoutes`/`FusionMessageBus` return the typed result instead of throwing `"Command failed."`. |
| D27 | Q-C (2026-09-29): fluent rules are split by a **generated per-message list of compiled `(member, rule)` pairs** (registered by module initializer, no reflection). The runtime `ValidationBuilder<T>` skips listed rules and registers only the leftovers (non-constant args, `Invariant` lambdas) into `RuntimeRules<T>`. Per member+rule, not per type: at runtime `Length(2, 80)` and `Length(1, opts.Max)` are indistinguishable — only the generator knows which had constants. |
| D28 | Q-D (2026-09-29): **hard replace** of `YFex.Cqrs.ValidationResult`/`ValidationError` (all usages are in-repo); `ICommandValidator`/`IQueryValidator` stay `[Obsolete]`, return the new `ValidationResult` and are adapted as context rules. |
| D29 | Handoff item 1 (approved 2026-09-29, built 2026-09-30): **implicit `CallContext` over RPC** — caller identity (`IAuthContext`) + tenant (`ScopeIdentity`, + room for culture/correlation id), never a field on messages or a handler parameter. Pluggable sources: `FromConnection()` (DEFAULT, captured from the WebSocket upgrade `HttpContext` in Fusion's `ServerConnectionFactory`), `OrFromHeader(name)` (per-call token validated by an app `ICallTokenValidator`), `OrFromSession(...)` (Fusion `Session`), `.WithTenant()`. Syntax: `UseYFexMessagingRpcServer(rpc => rpc.CallContext(ctx => ctx.FromConnection().OrFromHeader("Authorization").WithTenant()))`; client `AddYFexMessagingRpcClient(rpc => rpc.CallContext(ctx => ctx.SendToken(...).SendTenant()))` only for the header option. Every dispatcher runs stage 2 with `CallContext.Current ?? IPrincipalAccessor.Current` (order 1 → 2 → 5 kept); on the server stage 2 runs in an RPC middleware **outside Fusion's compute cache**, and generated compute queries take a server-filled `variant` argument so cached results are never shared across tenants (nor users, for `ScopedByUser`/`Session`/custom). Permissions: versioned per user (`IPermissionsVersions`, bumped by `CallContextSignals.PermissionsChanged`) → reload only on change; `RevokeAsync` closes the user's connections; client UI refresh via the `ICallPermissions.GetMyPermissionsAsync` compute method. Lives in YFex.Messaging.Rpc (already the ASP.NET/Fusion.Server package); YFex.Cqrs unchanged. Details: 13 "D29/D30 implementation log", `messaging-internals.md` "Call context". |
| D30 | Handoff item 2 (approved 2026-09-29, built 2026-09-30): **W1 — the CQRS generator emits the Fusion proxies itself** (`RpcProxyCodeBuilder`, Pipeline C): interface proxies of `I{Aggregate}RpcContract` and of the new client view `I{Aggregate}RpcClient` (registered under the contract's wire name for same-hub scenarios: `rpc.Add{Aggregate}RpcRemoteClient()`), and the class proxy of `{Aggregate}RpcContractImpl`, byte-identical to ActualLab.Generators 13.0.12 (pinned). A GOLDEN test (`Messaging.Rpc.Tests/ProxyGoldenTests`) runs the real ActualLab generator (test-only package) on a source copy of the generated contract and compares. Generated command methods are regular RPC methods (no CommandR `[CommandHandler]` — CommandR rejects non-ActualLab `ICommand` arguments). Apps no longer declare contracts in source. |
| D31 | ⚑P8 **confirmed** (2026-09-30, start of Phase 4): `[SituationSource<TS>]` generates `BuildSituation()` on VMs (name match; unmatched fields via `partial T __Situation_<Field>()`; lines via the element VM's own `[SituationSource<TLine>]`). Built as the **last** 4a task. |
| D32 | ⚑P9 **confirmed as a declarative option** (2026-09-30): each rule set declares `public static Allocation Allocation => Allocation.Proportional(Remainder.LargestLine)` (the default when omitted) or `Allocation.None`. The engine stays business-agnostic; the sum of allocations always equals the adjustment exactly. |
| D33 | Placement (2026-09-30): **YFex.Cqrs → YFex.Rules** (`.UseSituation/.Enforce/.OnDecisionMismatch`, `IRuledCommandHandler`, stages 3–4 in the existing dispatchers); `RuleAdmin` contracts + handlers live in a new opt-in module project **`YFex.Rules.Admin`** (→ Rules + Cqrs). |
| D35 | Domain-agnostic values (user, 2026-09-30): **`IRawValue<TSelf>` in YFex core** is the single extension point for semantic value types (static `RawType` = stable key + storage kind; `ToRaw()`; static `TryFromRaw`). `ValueKind` keeps **storage primitives only** (Money/Percentual/Quantity/Mass/Length became `IRawValue` types; `Custom` removed); a typed `RawValue` carries the 32-bit `RawType.Id` in its padding (still 32 bytes). Generators detect the interface instead of FQN lists. `Quantity` moved `Act.Utils` → `YFex`. |
| D36 | Domain-agnostic amounts (user, 2026-09-30): a rule set's amount is whatever `Base`/`LineBase` return — any **`IAmount<TSelf>`** (= `IRawValue` + the BCL `IAddition/ISubtraction/IMultiply<T,decimal,T>/IComparison/IAdditiveIdentity` operator interfaces; `INumber<T>` rejected — it demands `T×T`) or plain `decimal/int/long/double`; none = a rule set that only limits/grants/denies/warns/approves. The engine computes in `decimal`; decisions expose typed `RawValue`s (`d.Totals.Final.As<Mass>()`). Directives are **`Increase`/`Decrease`** (percent, amount, percent-with-max) — no signed "positive = discount". `AdjustTarget.Subtotal` → `Total`; `BestFor.Customer/Business` → `Prefer.Largest/Smallest`; totals `Gross` → `Initial` (+ signed `Change`); table columns `Decrease/IncreasePercent`, `Decrease/IncreaseAmount`. |
| D37 | Rule tiers (user, 2026-09-30): publishing levels are **app-declared** — `[RuleTier]` on situation fields (declaration order = level, max 4) → `RuleTierPath` (replaces `ScopeKey`); data published at a path applies to every evaluation below it, deepest wins. `EditScope` → `EditableAt = [nameof(…)]` (tier field names, generator-checked YFR0107); `RuleLayer` → tier depth (`int`, 0 = code) on `RuleRef`/`CompileNote`/`Overridden`/`ParameterValue`; `ScopeKind` (Channel/User) removed — those are ordinary fields. `RuleOrigin` (proposed) deferred to Phase 6 (nothing distinguishes it today). `IRuleScopeGuard` → `IRuleTierGuard`. |
| D38 | Explicit names (user, 2026-09-30): `[AsOf]` → **`[RuleEffectiveDate]`** (`ISituation.EffectiveDate`, `RuleContext.EffectiveDate`, `NotEffective(EffectiveDate)`), `Scope(in s)` → `TierPath(in s)`. |
| D34 | Approvals (2026-09-30): `OverrideToken` verified through `IOverrideTokenVerifier`; ships **online HMAC** (server key + KeyId rotation) **and offline device-signed** approvals, the latter **capped** (per-permission max) and **always flagged** for review on replay. Security review of the offline path still required before production. |

Proposed while writing this design (**confirm or change before/while implementing** — each is
marked `⚑ P#` where it appears):

| # | Proposal | Why |
|---|---|---|
| P1 ✅ D21 | Validation value-rule contracts are `IValueRule<T>` / `ITextRule` and the attribute is `[ValidateWith<T>]` (the conversation used `[Rule<T>]`/`IRule<T>`). | `IRule<TSituation>` + `[Rule("code")]` belong to YFex.Rules; same names would collide. `[ValidateWith]` also continues the existing State name. |
| P2 ✅ D23 | Fluent method is `.Validation(v => …)` (conversation used `.Rules(r => …)`). | Avoids confusion with YFex.Rules / `.Enforce(ruleSet)`. |
| ~~P3~~ | *Resolved by D20:* `RawValue`/`ValueKind` live in **YFex core**; Composer reuses them. | — |
| P4 ✅ D21 | Severity has 3 levels (Info, Warning, Error); "needs approval" is a Directive, not a severity. | Keeps severity orthogonal; fiscal `Blocking→Error`, `Alarm→Warning`. |
| P5 ✅ D21 | Required-from-nullability inference is **off** by default (MSBuild `YFexValidationInferRequired=true` to enable). | Empty string ≠ null surprises. |
| P6 ✅ D22 | Generators coordinate via `partial void` hooks (State emits declaration + call; Validation implements). | Generators cannot see each other's output. |
| P7 ✅ D24 | New opt-in handler interfaces receive `Valid<T>` and `Decision<TS>`; existing handler interfaces unchanged. | Non-breaking. |
| P8 ✅ D31 | `[SituationSource<TS>]` can generate `BuildSituation()` on a VM by name convention (optional convenience). | Same convention as `[ValidatesAs]`. |
| P9 ✅ D32 | Subtotal-stage discounts are allocated to lines (proportional, remainder to the largest line) inside the engine. | NF-e needs `vDesc` per item. |

## Glossary

| Term | Meaning |
|---|---|
| **Violation** | One failed check: `FieldPath` + `RuleCode` + `Severity` + up to 2 args + origin. |
| **RuleCode** | Stable, namespaced string id of a check or rule (`"len"`, `"br.document"`, `"sale.discount.cap"`, `"nfe.539"`). Persisted, sent over the wire, used as message key. |
| **FieldPath** | Where a violation belongs: a constant template (`"Lines[].Quantity"`) + indices (`[3]`) — no string concatenation. |
| **Shape rule** | Pure, sync, per-field or per-object check declared by attribute/fluent (layer 1). |
| **Invariant** | Cross-field shape rule (`[Invariant]` method). Still layer 1 — not a domain invariant. |
| **Context rule** | Async server-side check that needs I/O (unique email, stock). `IContextRule<T>`. |
| **Situation** | Immutable struct snapshot that business rules evaluate (layer 2). |
| **Directive** | What a business rule outputs (Deny, Warn, Limit, Grant, Adjust, RequireApproval). |
| **Decision** | The resolved result of evaluating a rule set on a Situation: verdict, limits, options, adjustments, totals, fingerprint, trace. |
| **Stage** | Ordered phase of evaluation (Items → Subtotal → Payment → Limits). Each stage's base is the running total left by the previous one. |
| **Group** | Set of rules competing for the same thing, with a **resolution strategy** (Exclusive, Stack, MostRestrictive, First…). |
| **Hit policy** | How a decision table picks rows (First, Unique, Priority, Collect…) — DMN vocabulary. |
| **Layer / EditScope** | Who defines/overrides a rule: Code (sealed), Tenant, Store, User discretion. |
| **Guardrail** | Sealed limit that editable rules/rows cannot exceed (checked at publish and at evaluation). |
| **Fingerprint** | 64-bit hash of a Decision's outcome. Equality check only ("same decision?"). |
| **Trace** | Opt-in record of why: applied/skipped/superseded/capped rules with reasons. |
| **AppliedAdjustment** | Always-recorded line of "this rule (code+version+layer) adjusted this target by this amount from this base". Persisted with the sale. |
| **Constraint / FieldConstraints** | Machine-readable limits for a field (required, lengths, bounds, options) used by UI controls. |
| **IValidationSource** | UI-neutral interface a VM exposes: constraints + violations per path + touch notifications. |
| **Profile** | Named strictness level for `[ValidatesAs]` (e.g. `"draft"` vs `"final"`). |
