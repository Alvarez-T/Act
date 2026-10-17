# 13 — Implementation phases & acceptance criteria

> Order agreed (D19). "First release" = Phases 0–5 (runtime table editing included, D11).
> Every session: read `README.md` + the files listed for its phase + `11` §1 checklist.
> Use the RoselYn MCP tools for C# exploration (per the user's global instructions) and
> `dotnet build YFex/yfex.slnx` / `dotnet test YFex/yfex.slnx` to verify.
> Add new projects to `yfex.slnx` in a `/Validation/` folder; update `YFex/CLAUDE.md` (solution map,
> dependency layers, generator table) and `docs/internal/source-generators.md` when a phase adds a generator.
> Items marked ⚑P# in README are proposals — if a phase depends on one the user hasn't confirmed, ask.

---

## Phase 0 — Fix YFex.State validation (small, independent)

**Load:** 01 §2. **Touch:** `YFex.State.Generator/ObservableEmitter.cs`, `YFex.State.Tests`.

Tasks
1. Bug 1: pass the property name to `ValidationBag.Set`.
2. Bug 2: call the right helper(s); sync-then-async.
3. Bug 3: replace `async void` with versioned, cancellable Task + error routing + UI-thread marshalling.
4. Add generator emission tests (none exist) — 5 scenarios in 01 §2.

Acceptance
- A `StateObject` using `[ValidateWith]` (sync), `[ValidateAsync]` (async only) and both compiles with zero warnings.
- Race test: latest value's result wins; throwing validator doesn't crash.
- All existing State tests green.

## Phase 1 — YFex.Validation core + generator

**Load:** README (YFex core section), 02, 03 (§1–6, 8, 9), 10 (§2–3), 11, and `Rate`/`RoundingPolicy`/`Bounds<T>` from 04.
**Create:** `YFex/src/Validation/YFex.Validation`, `YFex.Validation.Generator`, `YFex.Validation.Tests`,
`YFex.Validation.Generator.Tests`, `YFex.Validation.Testing`, AOT smoke console.

Tasks
0. **YFex core additions** (README "What lives in YFex core"; `YFex/src/YFex`): `ValueKind`, `RawValue`
   (+`Payload16`, direct `Money`/`Percentual`/`Quantity`/`Mass`/`Length`/EnumCode support), `FieldPath`,
   `IndexPath`, `Bounds<T>`, `OptionSet`, `Rate`, `RoundingPolicy`, `StableHash64`,
   `YFex.Localization` (`IMessageSource`, `IMessageCatalog`, `MessageCatalog`) — with tests in the
   core test project (create `YFex.Tests` if none exists). Reuse existing core types; don't duplicate.
1. YFex.Validation types (02 §1–7): `Severity`, `RuleCode`, `FieldId`, `RuleArgs`, `Violation`, origin, `ViolationWriter`, `ViolationCollector`,
   `ViolationList`, `ValidationResult`, `Valid<T>`, `Validated<T>`, `Invalid`, `Validator`,
   `IValidatable<T>`, rule interfaces, `FieldConstraints`, `OptionSet`, `IValidationSource`, `FieldStatus`,
   `IPathMap`, `RuntimeRules<T>`, `ValidationSlot<T>`.
2. Attributes (02 §8) incl. DataAnnotations mapping; built-ins (02 §9).
3. Generator: attribute + DataAnnotations + invariants (03 §2–3); fluent parsing may land in Phase 3
   (it needs the Cqrs API), but the emission model must already support it.
4. Message contracts + built-in catalog `en`/`pt-BR` (10 §2–3) + YFV0040 analyzer (can be Phase 2).
5. `[ModuleInitializer]` emitting `ValidationSlot<T>.Set(&T.Validate)` for validatable types.

Acceptance
- Emission snapshot, diagnostics (every YFV0001–0015, 0030), caching tests green.
- Valid 5-member object: **0 bytes** allocated; invalid: 1 allocation (result array).
- AOT smoke publishes with zero trim/AOT warnings.
- YFex.Validation references **only YFex core**; new core types have no dependency on YFex.Validation.

## Phase 2 — YFex.State migration

**Load:** 07 (§1–8, 10), 03 §7, 01 §3, 10 §5–6.

Tasks
1. YFex.State → references YFex.Validation; obsolete old types/attributes (forwarding).
2. New `ValidationBag`, `ErrorItem`, `FieldState`; INDEI changes in `MvvmStateObject`.
3. State generator: emit `partial void __OnValidate_<Prop>` hooks + `__OnBatchValidated`; delete old emission.
4. Validation generator: implement hooks for VMs (own rules, invariants on instances, `[ValidatesAs]`,
   `[MapsTo]`, `Prefix`, `Profile`, `ToX()`, path maps, `IValidationSource`, YFV0020–24).
5. Timing: `[ValidationOptions]`, `[ValidateOn]`, `ShowErrors`, `[Debounce]` on properties (public `Debouncer`).
6. `ApplyServer` (generated members or YFex.Mvvm extensions — see 07 §7 layering note).

Acceptance
- Phase 0 tests still pass on the new path; all 07 §10 tests green.
- `CustomerFormVm` from 07 §4.1 works end-to-end in a test (borrowed rules, MapsTo both ways, invariant).
- Setting a valid value on a validated property allocates 0 bytes.

## Phase 3 — YFex.Cqrs & YFex.Messaging

**Load:** 09 (§1–5, 7, 9–11), 03 §4, 01 §3.

Tasks
1. Replace `YFex.Cqrs.ValidationResult`/`ValidationError`; fix `Error` (null-safe, `Violations`, `ApprovalRequired`).
2. Fluent `.Validation(...)`, `.Validate<TContextRule>()`, inline context rules; generator fluent parsing (03 §4).
3. Typed stages in `LocalDispatcher`/`FusionMessageBus`/Wolverine adapter; remove `Func<object>` validate.
4. MemoryPack formatters; ProblemDetails mapping (`errors` + `violations`).
5. Queries: coerce before cache key; invalid never cached.
6. Offline: shape validation before enqueue.
7. Move `UseFluentValidator` to `YFex.Cqrs.FluentValidation`; drop the package from YFex.Cqrs.
8. Handler interfaces `IValidatedCommandHandler`, registration generator support.

Acceptance
- 09 §11 tests (except rules/approval parts) green; YFex.Cqrs has no FluentValidation reference.
- A `CreateCustomer` with fluent rules round-trips a structured violation through RPC and lands on
  the right VM field via `ApplyServer`.

## Phase 4 — YFex.Rules (4a engine, 4b runtime editing — both in the first release)

**Load:** 04, 05, 06, 09 §6 + §8, 12.

4a Tasks
1. Situation generator (schema, Read, Hash, WriteTo/ReadFrom, DiffMask, `[AsOf]`, `[Scope]`,
   `[Requested]`, `[Derived]`, lines).
2. Rules (`IRule<TS>`, line rules, `RuleContext`, `[Rule]`, `[Effective]`, `[Sealed]`, `RuleParameter`).
3. Rule sets (stages, groups, strategies, rounding, derived fields), generated `Set`/`Evaluate`/`Catalog`.
4. Directives, `DirectiveWriter`, evaluation algorithm (04 §7), allocation to lines, guardrails.
5. `Decision`, `Verdict`, totals, constraints for UI, fingerprint.
6. Trace (levels, outcomes, condition splitting), `Explanation`, applied adjustments.
7. Typed decision tables (05 §1–3): cells, `[When]/[Then]`, hit policies, `Freeze`, `Check`, JSON rows.
8. Cqrs stage 3–4 (`UseSituation`, `Enforce`, mismatch policy, approvals/`OverrideToken`), `IRuledCommandHandler`.
9. VM side: `[ConstraintsFrom]`, decision → bag/constraints (07 §9), optional `[SituationSource]`.

4b Tasks (runtime editing)
1. `IRuleStore` contract + SQL implementation (YFex.Data) + client cache (Messaging.Rpc storage).
2. `RuleAdmin` commands/queries (publish table/parameters/overrides, versions, definitions).
3. Publish pipeline: validation, guardrails, table checks, permissions, optimistic concurrency, audit.
4. `RuleSetHost` hot-swap, as-of resolution, last-N cache; `RulesetPublished` distribution + invalidation.

Acceptance
- The sale scenario of **12** passes as tests: breakdown amounts, approval, Pix/Boleto limits,
  stacking cap, exclusive promo, allocation per line sums exactly.
- Determinism (fingerprint stable ×1000), allocation budget, as-of selection, sealed/guardrail tests.
- Publish a new installment table version at runtime → next evaluation uses it; old decisions keep
  their `RuleRef` versions; offline replay scenarios of 11 §2.3 behave per policy.

## Phase 5 — Avalonia (YFex.UI repo)

**Load:** 08, 07 §5–9, 06 §5, 10 §6.

Tasks: `YValidation` attached behavior, `IConstraintTarget` on inputs (constraint props, generic-math
bound conversion), `Platform.ApplyBinding` path extraction, `{y:Field}` extension, `ErrorItem` template
& pseudo-classes, `YValidationSummary`, `YRuleBreakdown`, `YDecisionTableEditor` + parameter editor,
approval dialog flow, gallery pages, headless tests.

Acceptance
- Gallery page with the `SaleVm` of 12 running against in-memory rules: constraints update live,
  breakdown renders, approval flow works, table editor publishes a version.
- 08 §11 tests green; no external package added to YFex.UI.

## Phase 6 — Fiscal, Composer, AI (after the first release)

**Load:** 01 §3.1–3.2, 04 §11, 05 §8, `YFex/docs/internal/composer-design.md`.

Tasks: `ResultadoValidacao` facade + `IRegraNFe` → `IRule<NFeSituation>` migration; `XsdValidation.ToViolation`;
`YFex.Rules.Dynamic` (spec, compiler to postfix, interpreter, publish validation) + Composer authoring;
`FieldConstraints`/`SituationSchema` → JSON schema for AI tools (YFex.Capabilities/Brain).

Acceptance: fiscal tests unchanged in behavior; a Composer-authored rule participates in a group with
trace/explanation identical in shape to code rules.

---

## Implementation log & deviations (keep updated)

**2026-09-29 — Phase 0 done.** `ObservableEmitter` fixed (bugs 1–3). Extras found while fixing:
`StateObject.OnValidationCreated` (Mvvm INDEI is now wired automatically — before, `ErrorsChanged`
only fired after a manual `EnsureValidationWired()`), CAS on the lazy `Validation` bag (a thread-pool
async continuation could create a second bag), `StateObject.OnAsyncValidationFaulted` (fault route;
`[StateCommand]` has no error route yet either). Async results resume via `ConfigureAwait(true)`
(UI thread when a SynchronizationContext exists). Tests: `YFex.State.Tests/Validation/GeneratedValidationTests.cs`.

**2026-09-29 — Phase 1 done.** Core types (`YFex/src/YFex` + `YFex.Tests`), `YFex.Validation` (runtime,
attributes, `BuiltIn` checks + `BuiltInMessages` en/pt), `YFex.Validation.Generator` (types, module
initializer, `messages*.json` catalogs, YFV0040), `YFex.Validation.Testing`, `YFex.Validation.Smoke`
(native AOT publish: zero IL warnings, runs green), `.Tests` (45) and `.Generator.Tests` (30).
Acceptance status: 0 bytes for valid objects ✔ (hand-written and generated); invalid = one result array ✔;
diagnostics tested for every Phase-1 id ✔ (YFV0003/0004/0011 are fluent-only → Phase 3); caching test ✔;
YFex.Validation references only YFex core ✔.

Generator deviations (03 not yet rewritten):
- `[Pattern]` → cached `internal static readonly Regex` (CultureInvariant, 1 s timeout), **not** `[GeneratedRegex]`:
  a generator cannot rely on another generator (the Regex generator never sees our partial method).
- `Profile` is name-based: `ProfileId(string? Name)`; `Validate(in v, ref w, ProfileId profile)`; `default` = all.
  (Byte ids per compilation would not match across assemblies.)
- `Rules.X.Check(value, ref w, in path)` holds only **unconditional** rules; `When`/`Profile` rules run in the
  owner-aware `__Validate_X(in v, ref w, profile)` used by `Validate`/`ValidateMember`. Constraints come from
  unconditional rules only.
- `[Each]`: Required/NotEmpty stay on the collection (path `"Tags"`); every other rule targets elements (path
  `Rules.Tags.ItemPath` = `"Tags[]"` + index, `CheckItem`, `ItemConstraints`). A collection-level Length together
  with `[Each]` is therefore not expressible (use an invariant).
- `ValidateAt` = `Validator.ValidateAt<T>` (validates into an inner writer, re-targets paths with
  `FieldPath.Prefixed`; the combined template allocates only on failure). Nested members and validatable
  elements use it.
- Length codes: `len` (min,max), `len.min` (min), `len.max` (max). `NotEmpty` projects as `minLength: 1`.
- Async rules (`IAsyncValueRule<T>` via `[ValidateWith]`) are metadata only (`Rules.X.HasAsyncRules`); they run in
  the Cqrs context stage / State debounced path (Phases 2–3).
- Module initializer calls `ValidationSlots.Register<T>()` (the `&T.Validate` pointer is taken inside
  YFex.Validation → consumers need no `AllowUnsafeBlocks`). Generic, private and protected nested types are skipped.
- Inheritance: a derived validatable runs `Base.Validate` first; field ids continue after the base's members;
  lookups fall back to the base.
- StateObject-derived types are skipped until Phase 2 (partial-void hooks, 03 §7).
- YFV0012 = cycle through **non-nullable** nested members only (nullable links are normal trees).
- New: YFV0041 (invalid messages JSON). YFV0040 implemented in the generator: active only when the project has
  `messages*.json`; default culture = `messages.json` + `messages.<YFexDefaultMessageCulture>.json` (default `pt-BR`).
- Messages class = `<RootNamespace>.<LastSegment>Messages` (override: MSBuild `YFexMessagesClass`, needs
  `CompilerVisibleProperty`). `services.AddYFexMessages(...)` DI registration not done (YFex core has no DI
  abstractions reference) — compose with `new MessageCatalog(layers…)`.

Deviations from 02/03 (normative text not yet updated):
- **`using var w = new ViolationWriter(); X.Validate(in v, ref w);` does not compile** (CS1657: a
  `using` variable cannot be passed by `ref`). **Fix (02 §4 updated):** `using var scope = ViolationWriter.Begin(out var w);`
  — `[UnscopedRef] out` lets `Begin` return a `ViolationScope` holding a `ref` to the writer's storage;
  `w` stays an ordinary stack local passed by `ref` (no indirection, no allocation, same IL cost as
  try/finally). Writer state moved to an internal non-ref `ViolationStorage` (CS9050 forbids ref fields of
  ref struct type); the spill uses `ArrayPool<Violation>` directly instead of `RefArrayPoolBufferWriter`
  (a ref struct). Generated code and `Validator` use this pattern.
- `IndexPath` has no `AsSpan()` (a readonly struct cannot return a span over its own inline array);
  use `Count`, the indexer or `CopyTo(Span<int>)`. `RuleArgs` exposes `Buffer` (copy) for the same reason.
- `RawValue` adds `TryCompareTo` (non-throwing), `From(bool/TimeOnly/DateTime/Guid)`, `AppendTo(ref StableHash64)`;
  an `EnumCode` compares with Int32/Int64 by underlying value; units compare with plain numbers by amount.
  `FromEnum` stores the value **and** the `[EnumCode]` text (formatting uses the code).
- `OptionSet` distinguishes `IsEmpty` (= unrestricted, the default) from `AllowsNothing` (empty intersection).
- `Valid<T>` carries `Warnings`; `Validator.AssumeValid` exists for infrastructure that already ran the checks (Cqrs).
- `ValidationResult.MapPaths`: paths the map rejects become object-level.
- `RuntimeRules<T>` uses delegate `RuntimeRule<T>(in T, ref ViolationWriter)` + `RegisterConstraints`/`Clear`.
- `Quantity` moved to namespace **`YFex`** (2026-09-30; was legacy `Act.Utils`). Generators now reference `global::YFex.Quantity`.
- `MessageCatalog` lookup is culture-major (exact culture across all layers, then parent culture).


**2026-09-29 — Phase 2 done (D22 = ⚑P6 confirmed with refinements).**
- YFex.State → YFex.Validation. Old `IValidator`/`IAsyncValidator`/`ValidationResult`/`ValidationSeverity` and
  `[ValidateWith(typeof)]`/`[ValidateAsync(typeof)]` are `[Obsolete]` and still honoured — by the **Validation** generator
  through the hooks (`YFex.State.Validation.LegacyValidation`: code `state.legacy`, the old message in `Violation.Detail`).
- State generator: every writable, non-Task-notifier `[Observable]` gets `__OnValidate_X(in value);` after `NotifyChanged`
  + `partial void __OnValidate_X(in T value);`; every class gets `protected override void OnUpdateCompleted()` →
  `partial void __OnBatchValidated();`. `Validate_X` emission and the validator fields of the models are deleted
  (`ObservablePropertyModel.HasValidationHook` = the rule the Validation generator mirrors in `Parser.HasStateHook`).
- `StateObject`: `OnUpdateCompleted()` (outermost batch end, runs BEFORE the batched notifications), `IsInUpdate`,
  `ConfigureValidation(bag)` (generated code only: `Show`, default `Profile`), `ValidationServices` (async rules; default empty).
- New `ValidationBag` (07 §2) + `ErrorItem`, `ValidationMessages.Catalog`, `FieldState` union, `AsyncValidationTicket`,
  `EmptyServiceProvider`; `[ValidationOptions]`, `[ValidateOn]`, `ValidateOn`, `ShowErrors`, `ServerOutcome`;
  `[Debounce]` now also targets properties/fields (delays async rules). `MvvmStateObject` INDEI = visible only, `ErrorItem`s.
- Validation generator: `Parser.Vm.cs` / `Emitter.Vm.cs`; `[ValidatesAs<T>(Prefix, Profile)]` and `[MapsTo]` in YFex.Validation;
  YFV0020–0025; every member's `Rules.X` also gets `Check(value, ref w, in path, ProfileId profile)` (runs Profile rules too).
- Tests: `YFex.State.Tests` 352 (36 new: `Validation/ViewModelValidationTests.cs`), `.Generator.Tests` 39
  (9 new: `ViewModelGeneratorTests.cs`; `GeneratorHarness.RunWithState` runs both generators so VM output is compiled).
  Dependents rebuilt: Mvvm, Collections, History (+Tests 60 ✔), Blazor, State.Smoke, YFex.Mvvm, Messaging(.Fusion/.Rpc),
  Validation.Smoke. YFex.State / State.Mvvm build with zero CS/IL warnings.

Acceptance: Phase 0 tests pass on the new path ✔ (only change: INDEI returns `ErrorItem`, so one assertion reads
`.Message`); 07 §10 ✔ except `ApplyServer` → `Queued` (needs the Cqrs results, Phase 3); `CustomerFormVm` end-to-end ✔
(borrowed rules, `[MapsTo]` both ways incl. `Address.City`, VM invariant, contract invariant via `ValidateAll`);
setting a valid value on a validated property (field rules + invariant re-run) = **0 bytes** ✔.

Deviations from 07 / 03 (normative text updated where noted):
- Hooks take `in T` and the batch hook goes through `OnUpdateCompleted` (D22; 03 §7 updated).
- Bag (07 §2 updated): `ReplaceAll(source, owner, ReadOnlySpan<Violation> | ref ViolationWriter | in ValidationResult)` —
  no `IPathMap?` parameter (it would box struct maps); map first with `MapPaths`. Added `ClearStale(path)` (Server +
  submit results, called by every hook), `HasFieldErrorsFor(path)` (invariant gate — `HasErrorsFor` would count the
  invariant's own output and make it skip itself), `ReplaceSubmitResult<TMap>`, `ApplyServer(in ValidationResult)`,
  `Profile`, `Show`, `ResetInteraction()`, `ToResult()`, `GetVisibleItems`/`GetAllVisibleItems`/`HasVisibleErrors`.
  Owner ids under `Field`: `FieldOwner = 0` (sync, own + borrowed), `AsyncOwner = 1`, `SubmitOwnerBase = 0x8000` (+ contract index).
- `ViolationSource.Manual` is always visible (like Server/Rule): app code that adds one wants it shown. Obsolete
  `Set(name, message, severity)` / `GetError` / `GetErrors` kept (Manual violation with `Detail`).
- Compatibility: a VM using the obsolete typeof attributes and no `[ValidationOptions]` gets `Show = Immediately` (old behaviour).
- `ValidateAll(string? profile = null)` exists on the ROOT generated VM; derived VMs chain through protected virtual
  `__vValidateAll` / `__vTryGetConstraints` / `__vTouch`. `IValidationSource` is implemented explicitly. `ValidateAll`
  does not re-run async rules (keeps their last result).
- Submit pass per contract: `T.Validate(in ToT(), ref w, profile)` remapped to VM paths; field violations the per-field
  rules already reported (same path + code) are dropped; stored under `SubmitOwnerBase + i`, cleared on edit of the path.
- Profiles: a `[ValidatesAs(Profile = p)]` contract is active when `bag.Profile.Includes(p)`; the bag starts with the
  first declared contract's profile. YFV0024 fires only for contracts that can be active together (no profile / same profile).
- `When` rules on VMs: changing the When member re-validates the guarded member. Borrowed per-keystroke checks skip the
  contract's `When` rules (they need the contract instance) — `ValidateAll` covers them.
- `ApplyServer` (Phase 2 half): generated `ApplyServer<TContract>(in ValidationResult)` → `Ok`/`Invalid` (`typeof`
  switch, folded by JIT/AOT) + `bag.ApplyServer` for identity paths. The `QueueableResult`/`CacheableQueryResult`
  overloads (`Queued`/`Failed`) are Phase 3 — they need `Error.Violations`.
- `ToT()`: positional parameters by name + initializer for settable/init members, nested members built recursively;
  unmapped required input → `public partial T ToT();` (you implement it, YFV0020 lists them); a user-written `ToT()` wins.
- New **YFV0025** (Warning): rules that cannot run on change (not a writable `[Observable]` of this class), and contract
  members matching a property inherited from a base VM (declare `[ValidatesAs]` on the declaring class — D22).
- YFex.Validation: `ViolationList.FromOwnedArray` (non-copying wrap for immutable exact-size arrays).
- Field display names: `ErrorItem` uses `"<VmType>.<Path>"` then the catalog's humanized fallback; the contract-key
  fallback of 10 §5 is not implemented yet.
- Name clash: a file with both `using YFex.State.Validation;` and `using YFex.Validation;` gets CS0104 on
  `ValidationResult` until the obsolete State type is removed → `using ValidationResult = YFex.Validation.ValidationResult;`.

Found while verifying (pre-existing, NOT caused by Phase 2):
- Field-form `[Observable] private T _x;` does not compile in the State emitter (it emits a `public partial` property
  without a definition part + a duplicate field: CS9249/CS0102). The Validation generator mirrors the field rule anyway.
- `YFex.Messaging.Tests` `LiveGeneratorTests`: tests `await` then call `Deactivate()` on another thread → DEBUG
  owner-thread assert (4–5 of 7 fail in isolation). `YFex.NavigatR.Tests`: 5 failures (route parsing / navigation
  direction; NavigatR does not reference YFex.State; `Navigator.cs` has uncommitted edits from another session).

---

## Phase 3 handoff (written at the end of the Phase 2 session, 2026-09-29)

Everything below was verified against the code on 2026-09-29 (file names are under `YFex/src/` unless noted).
Line numbers drift — search for the quoted identifiers.

> **Decisions taken at the start of the Phase 3 session (2026-09-29): D23–D28 in README** — P2 `.Validation`, P7 opt-in
> handler, Q-A invoker decorator, Q-B serializable typed results, Q-C per-`(member, rule)` compiled list (refines the
> per-type flag proposed below), Q-D hard replace. Baseline that session: State 358, Validation 45, Validation.Generator 39,
> Messaging 120 (+2 skipped; LiveGeneratorTests fixed by side session b), Cqrs.SourceGenerator 6 (+1 skipped).

### 0. Session start protocol

1. Read: README (decisions D1–D22), this file (Phase 2 log above + this section), 09 (§1–5, 7, 8 "enqueue time"
   only, 9, 9.1, 11), 03 §4 + §6, 01 §3, 10 §6, 11 §1–2, and `docs/internal/cqrs-internals.md` +
   `docs/internal/messaging-internals.md` (09 asks for them).
2. `git status` in `C:/Dev/YFex/YFex`: two side sessions started at the end of Phase 2 may have landed —
   (a) field-form `[Observable] private T _x` fix in `State/YFex.State.Generator/ObservableEmitter.cs`,
   (b) cross-thread `LiveGeneratorTests` fix in `Messaging/YFex.Messaging.Tests/Generator/LiveGeneratorTests.cs`.
   Don't redo them; rebase your understanding on what's there.
3. Baseline before coding: `dotnet test` on `State/YFex.State.Tests` (352), `Validation/YFex.Validation.Tests` (45),
   `Validation/YFex.Validation.Generator.Tests` (39), `Messaging/YFex.Messaging.Tests` (known failures: see Phase 2 log
   unless side session (b) fixed them), `Cqrs/YFex.Cqrs.SourceGenerator.Test`. Record the numbers.
4. **Ask the user before coding** (use AskUserQuestion; record answers as D23+ in README):
   - **⚑P2** — fluent method name `.Validation(v => …)` (proposed) vs `.Rules(r => …)`.
   - **⚑P7** — opt-in `IValidatedCommandHandler<TCmd,TResult>` receiving `Valid<TCmd>` (existing handler interfaces unchanged).
   - **Q-A (new) — where the SERVER pipeline runs.** Today nothing validates on the server (§2 below). Options:
     (1) a validating `IHandlerInvoker` decorator registered by `AddYFexMessagingRpcServer…` (one place, covers the
     generated `RpcContractImpl`, Wolverine and replay) — *recommended*; (2) emit stages into the generated
     `RpcContractImpl` (Cqrs SourceGenerator); (3) Wolverine middleware only.
   - **Q-B (new) — how `Error` crosses the RPC wire.** The client route throws `"Command failed."` and drops the error
     (§2). Options: make `Error`/`QueueableResult`/`Result` serializable by the Fusion RPC serializer in use (verify
     which — ActualLab default, likely MemoryPack; `Error` has no `[MemoryPackable]` today) and return the typed
     result through `FusionMessageBus` — *recommended*; or map to an exception type carrying violations.
   - **Q-C (new) — compile-time vs runtime fluent rules.** `.Validation(v => …)` executes at registry build too. The
     generator compiles the constant rules into `T.Validate`; the runtime builder must then register ONLY what the
     generator could not compile (non-constant args, `Invariant` lambdas → `RuntimeRules<T>`), or rules run twice.
     Proposed: generator emits `internal static bool __HasCompiledFluent => true` (or a `[CompiledFluentRules]`
     assembly attribute listing types); the builder checks it via a generic static (`CompiledFluent<T>.IsSet`, set by the
     module initializer) — no reflection.
   - **Q-D (new) — breaking vs shims.** 01 §3 says replacing `YFex.Cqrs.ValidationResult`/`ValidationError` is
     breaking. Usages are all inside this repo (Cqrs builders/registry, `LocalDispatcher`, `Messaging.Tests`
     `ComplianceTests.cs` + `Rpc/OutboxTtlTests.cs`); `YFex.Devices`/`YFex.Fiscal` hits are unrelated types. Confirm the
     hard replace (recommended) vs `[Obsolete]` shims.

### 1. Current state (what exists today)

**Cqrs (`Cqrs/YFex.Cqrs`)**
- `ValidationResult.cs`: `record ValidationError(PropertyName, Message, ErrorCode?)` + struct `ValidationResult(IsValid,
  IReadOnlyList<ValidationError>)`, `Success()`, `Failure(...)`. Strings only (D6 violation).
- `Error.cs`: `readonly struct Error { List<ErrorDetail> Details = new(); Type/Message via LINQ FirstOrDefault }` →
  `default(Error).Details` is **null** (NRE on `Type`/`Message` of a default error) and allocates a list per error.
  `ErrorType`: `None, NotFound, Fail, Unauthorized, ValidationProblem, Conflict` (add `ApprovalRequired` only in Phase 4).
- Results: `QueueableResult`, `QueueableResult<T>`, `QueueableResult<T0,T1>` (`[Union]` tag structs, `Fail(string)` →
  `ErrorType.Fail`); `CacheableQueryResult<T>` has `ValidationProblem(string)`, `Unauthorized`, `NotFound`, `Fail`.
- `Configuration/CommandConfigurationBuilder.cs` (both `<TCommand>` and `<TCommand,TResult>`) and
  `QueryConfigurationBuilder.cs`: `Validate(Func<T, CancellationToken, ValueTask<ValidationResult>>)`,
  `Validate<TValidator>() where TValidator : ICommandValidator<T>` / `IQueryValidator<T>` (`Configuration/Interfaces.cs`),
  `UseFluentValidator<TValidator>()` (uses FluentValidation directly; LINQ `Select`). The csproj references
  `FluentValidation 11.11.0` — to be moved to a new `Cqrs/YFex.Cqrs.FluentValidation` project (task 7).
- `Registration/Descriptors.cs`: `ValidatorDescriptor { Kind: Inline | TypeRef | FluentTypeRef }`.
- `CompiledMessagingRegistry.cs` → `BuildCompositeValidator`: compiles **only Inline** descriptors into
  `CommandPolicy.Validate` / `QueryPolicy.Validate` (`Func<object, CancellationToken, ValueTask<ValidationResult>>`,
  sync fast path). **`Validate<TValidator>()` type-ref validators are silently ignored** (comment: "handled by Plan 3
  dispatcher" — no dispatcher does it). Fixing this = task 3 (context rules via `ContextRuleFactory<T>`).

**Dispatchers (`Messaging/*`)**
- `YFex.Messaging.Rpc/LocalDispatcher.cs` (client / in-process): online path = `policy.Validate` → authorize →
  CustomLogic → handler. Validation failure → commands: `QueueableResult.Fail(FormatErrors(r))` (**`ErrorType.Fail`**,
  a "; "-joined message string); queries: `CacheableQueryResult.ValidationProblem(string)`. Unauthorized commands return
  `Fail("Unauthorized.")` (not `ErrorType.Unauthorized`). **Offline path (`OfflineCommandAsync`) enqueues without any
  validation** (task 6).
- `YFex.Messaging.Rpc/FusionMessageBus.cs` (client over Fusion RPC): **no validation at all**; `QueryDispatchEntry` /
  `CommandDispatchEntry` return `object?`, filled by the generated `RegisterRoutes` which does
  `if (r.TryGetValue(out var v)) return v!; throw new InvalidOperationException("Command failed.")` → **the server's
  `Error` never reaches the caller** (Q-B).
- Server: generated `{Aggregate}RpcContractImpl` (`Cqrs/YFex.Cqrs.SourceGenerator/RpcContractCodeBuilder.cs`) forwards
  to `IHandlerInvoker.InvokeAsync` and returns `QueueableResult<T>.Ok` / `Result<T>.Ok`; only
  `ResultConversionException` (Wolverine middleware, `Messaging/YFex.Messaging.Rpc.Wolverine/WolverineExceptionMiddleware.cs`)
  becomes an `Error`. `WolverineLocalDispatcher.cs` invokes the bus directly — **no validation**. So today a command
  sent over RPC is **never validated server-side** (Q-A).
- Handler interfaces: `YFex.Messaging.Rpc/HandlerInterfaces.cs` (`IQueryHandler`, `ICommandHandler<T>`,
  `ICommandHandler<T,TResult>`); invoker: `IHandlerInvoker`, `LocalHandlerInvoker(Registry).cs`; handler/outbox
  registration generator: `Messaging/YFex.Messaging.Rpc.Generator/OutboxCommandGenerator.cs` (`YFexLocalHandlerRegistrations`).
- Replay: `OutboxReplayer.cs` → a rejected replay (`QueueableResult` error) goes through `ConflictPolicy`; escalation writes
  `SyncFailure(..., string Reason, ...)` (`ISyncFailureLog.cs`; JSON via `RpcInternalJsonContext`) with reason `"conflict"`
  or `Error.Message` — no structured violations.
- Serialization: outbox payloads = System.Text.Json source-gen contexts (`OutboxCommandPayload.cs`); `[MemoryPackable]`
  only on `OutboxEntry` (`IOutbox.cs`) and `RpcEventEnvelope`; Fusion RPC wire format not configured explicitly.

**Tests touching this**: `Messaging.Tests/Compliance/ComplianceTests.cs` (lines ~75, ~118: inline `.Validate` +
`ValidationResult.Failure("Name", …)`), `Rpc/OutboxTtlTests.cs` (~254: rejecting validator drives replay rejection),
`Cqrs/RegistryTests.cs` (`registry.Validate(...)` = configuration-completeness check, unrelated to message validation).
There is **no in-process Fusion RPC client↔server test fixture** — Phase 3 acceptance needs one (ActualLab.Rpc test
client or a real in-process host).

**From Phase 2 (ready to use)**
- `YFex.Validation`: `ValidationResult`/`ViolationList`/`Violation`/`FieldPath`/`RuleCode`, `ViolationWriter.Begin(out var w)`,
  `ViolationCollector` (async), `ValidationSlot<T>` + `ValidationSlots.Register<T>()` (module initializer per assembly),
  `IContextRule<T>`, `IAsyncValueRule<T>`, `Validator.Check/AssumeValid`, `Valid<T>`, `RuntimeRules<T>`, `ProfileId`.
- View models: generated `ApplyServer<TContract>(in ValidationResult)` (`Validation/YFex.Validation.Generator/Emitter.Vm.cs`
  → `EmitContracts`) and `ValidationBag.ApplyServer(in ValidationResult)`; `ServerOutcome { Ok, Queued, Invalid, Failed }`
  in YFex.State. VMs borrow `T.Rules.X.Check(value, ref w, in path, ProfileId)` only when
  `Parser.IsValidatableType(T)` is true.

### 2. Gaps Phase 3 must close (beyond 13's task list)

| # | Gap | Where | Task |
|---|---|---|---|
| G1 | Server never validates RPC commands/queries | `RpcContractImpl` → `IHandlerInvoker`; `WolverineLocalDispatcher` | 3 (Q-A) |
| G2 | Client drops the server `Error` (throws "Command failed.") | generated `RegisterRoutes`, `FusionMessageBus` | 4 (Q-B) |
| G3 | Type-ref validators (`Validate<TValidator>()`) never run | `CompiledMessagingRegistry.BuildCompositeValidator` | 3 |
| G4 | Validation failure = `ErrorType.Fail` + joined string; Unauthorized = `Fail` | `LocalDispatcher` | 1, 3 |
| G5 | `default(Error)` NRE; LINQ + list per error | `Error.cs` | 1 |
| G6 | Offline enqueue skips shape validation | `LocalDispatcher.OfflineCommand*Async`, `FusionMessageBus.OfflineCommand*Async` | 6 |
| G7 | Replay rejection loses structure (string `Reason`) | `OutboxReplayer`, `SyncFailure` (JSON) | 4 |
| G8 | Fluent-only contracts are invisible to `[ValidatesAs]` | `Parser.IsValidatableType` | 2 |

### 3. Suggested order (each step builds + tests green before the next)

1. **Error & result types (G4, G5)** — `Cqrs/YFex.Cqrs/Error.cs`: replace `List<ErrorDetail>` with fields
   (`ErrorType Type`, `string Message`, `ViolationList Violations`); null-safe on `default`; `Error.Validation(in
   ValidationResult)` (Message = first violation code/detail for old callers); keep `ErrorDetail`/`Details` only if
   something outside needs them (grep first — likely drop). YFex.Cqrs → reference `Validation/YFex.Validation`
   (Layer 2 → 0.5, allowed). Delete `YFex.Cqrs.ValidationResult/ValidationError` (Q-D); `ICommandValidator`/`IQueryValidator`
   return the new `ValidationResult` and become `[Obsolete]` (adapted as context rules in step 3). Add
   `QueueableResult(<T>).Invalid(in ValidationResult)` / `CacheableQueryResult<T>.ValidationProblem(in ValidationResult)`
   overloads. Name clash alert: inside `namespace YFex.Cqrs…` nothing clashes once the old type is deleted.
2. **Fluent `.Validation(...)` API + generator parsing (03 §4, G8, Q-C)** — runtime `ValidationBuilder<T>` /
   `FieldRuleBuilder<T,TMember>` / `EachRuleBuilder` / `InvariantFn<T>` in **YFex.Validation** (Cqrs builders just expose
   `.Validation(Action<ValidationBuilder<TMsg>>)` and forward). Generator: a second pipeline over
   `IAggregateConfiguration<T>` classes finding `.Validation(lambda)` whose receiver chain has
   `.Command<TCmd…>()`/`.Query<TQ,…>()`; produce rule models per message type and MERGE them with the attribute
   `TypeModel` (Collect + group by FQN; keep models equatable; add a tracking name + caching test). Only messages in the
   SAME compilation can receive emitted members; others → runtime-only + YFV0003-like info. Diagnostics YFV0003/0004/0011
   (descriptors already exist in `Diagnostics.cs`). Then `IsValidatableType` must also accept fluent-configured types —
   feed it from the merged model set (the VM parser currently evaluates it per symbol: expose a "fluent-validatable
   FQN set" to the VM pipeline via `Combine`).
3. **Typed stages in the dispatchers (09 §1, §4; G1, G3, Q-A)** — stage 1 = `ValidationSlot<TCmd>.Validate(in cmd, ref w)`
   (generic dispatch methods already have `TCommand`); stage 2 authorize (return `ErrorType.Unauthorized`); stage 5 context
   rules = `ContextRuleFactory<T>[]` built at registration (inline `Validate(Func…)`, `Validate<TRule>() where TRule :
   IContextRule<T>`, obsolete `ICommandValidator` adapters) stored as `object` on the policy, reinterpreted with
   `Unsafe.As` in the generic dispatcher; remove `CommandPolicy.Validate`/`QueryPolicy.Validate`. Apply in
   `LocalDispatcher`, the server entry chosen in Q-A, and `WolverineLocalDispatcher`. Order spy tests (09 §11).
4. **Wire (G2, G7, Q-B)** — structured `Error` through Fusion RPC: serializer formatters for `Violation`, `ViolationList`,
   `FieldPath`, `IndexPath`, `RuleArgs`, `RawValue` (+`Payload16`), `RuleCode` (AOT-safe; if MemoryPack: custom
   `MemoryPackFormatter<T>` registered in a module initializer, since these types live in YFex/YFex.Validation which
   must not reference MemoryPack); change `RegisterRoutes`/`FusionMessageBus` to return the typed result instead of
   throwing; `SyncFailure` gains `ViolationList Violations` (+ JSON converter in `RpcInternalJsonContext`). ProbDetails
   (`errors` + `violations`, 09 §5) only if an ASP.NET host exists in the repo — otherwise document and defer.
5. **Queries (09 §7)** — `Range(..., coerce: true)` clamps BEFORE `QueryCacheKey.For(...)`; invalid queries never touch
   `_cache`; tests.
6. **Offline (G6, 09 §8 first bullet)** — run stage 1 before `_outbox.EnqueueAsync` in both dispatchers; invalid →
   `Error.Validation`, never enqueued.
7. **`YFex.Cqrs.FluentValidation`** — new project `Cqrs/YFex.Cqrs.FluentValidation` (+ tests), extension
   `UseFluentValidator<TValidator>()` on the builders (same call site), FluentValidation failure → `Violation` (`Code` =
   error code or `"fluent"`, `Detail` = message, path = `FieldPath.Of(PropertyName)`); remove the package from YFex.Cqrs;
   add to `yfex.slnx` under `/Cqrs/`.
8. **Handlers (⚑P7)** — `IValidatedCommandHandler<TCmd,TResult>` in `HandlerInterfaces.cs`; `OutboxCommandGenerator`
   registration learns it; `Valid<T>` created by the pipeline (`Validator.AssumeValid` exists for exactly this).
9. **View-model `ApplyServer` Cqrs overloads (07 §7)** — in `Emitter.Vm.cs`, when the compilation has
   `YFex.Cqrs.QueueableResult\`1` (`compilation.GetTypeByMetadataName`, pass a bool into `VmModel` — keep it equatable):
   `ApplyServer<TContract, TResult>(in QueueableResult<TResult>)`, `ApplyServer<TContract>(in QueueableResult)`,
   `ApplyServer<TQuery, TResult>(in CacheableQueryResult<TResult>)` → `Queued`, `Invalid` (via the existing
   `ApplyServer<TContract>(in ValidationResult)`), other errors → `Validation.Replace("", Server, 0, …)` form-level +
   `Failed`, success → clear Server violations + `Ok`. YFex.State itself must not reference YFex.Cqrs.
10. **Docs** — update 09 (as-built deviations), 13 (Phase 3 log + Phase 4 handoff), `YFex/CLAUDE.md` (YFex.Cqrs → YFex.Validation,
    new project), `docs/internal/cqrs-internals.md` + `messaging-internals.md` (pipeline), memory.

### 4. Acceptance (13 Phase 3) — how to prove it

- 09 §11 tests except rules/approval: stage order with spies; structured violations over `LocalDispatcher` AND over
  Fusion RPC (needs the new in-process client↔server fixture); invalid query not cached; coerce before cache key;
  invalid command never enqueued offline; replay rejection keeps violations in `ISyncFailureLog`.
- `YFex.Cqrs.csproj` has no FluentValidation reference; adapter tests green.
- End-to-end: `CreateCustomer` configured ONLY with fluent `.Validation(...)` → a VM with `[ValidatesAs<CreateCustomer>]`
  borrows its rules (proves G8) → server rejects over RPC → `ApplyServer<CreateCustomer, CustomerId>(result)` returns
  `Invalid` and the violation is on the mapped VM field (e.g. `Document` → `CpfCnpj`).
- Zero-allocation check on stage 1 for a valid command (`GC.GetAllocatedBytesForCurrentThread`), like Phase 1/2.

### 5. Gotchas carried over

- VMs with rules need BOTH generators referenced (State + Validation); `GeneratorHarness.RunWithState` for generator
  tests of VMs. Cqrs generator tests live in `Cqrs/YFex.Cqrs.SourceGenerator.Test`.
- `using YFex.State.Validation;` + `using YFex.Validation;` → CS0104 on `ValidationResult` (alias it). After step 1,
  `using YFex.Cqrs;` no longer brings a `ValidationResult`.
- xunit 2 sync context + blocking waits deadlock (`SyncContextScope(null)` + `SpinWait`); DEBUG owner-thread assert on
  StateObjects (no `await` between VM calls in tests).
- `yfex.slnx` restore fails (missing `../YFex.System/YFex.System.Tests`) → build/test projects individually;
  `YFex.Persistence.Tests` has broken references (pre-existing). YFex.Cqrs references sibling repos
  (`YFex.Authorization`, `YFex.Resilience`, `YFex.Tenancy`) — build them if restore complains.
- Generators: netstandard2.0, Roslyn 4.11 pinned, new diagnostic ids go to `AnalyzerReleases.Unshipped.md` (RS2008).
- AOT: YFex.Validation/YFex.State/YFex.Cqrs paths must stay reflection-free; re-run `Validation/YFex.Validation.Smoke`
  (native AOT publish) and add a dispatch case to it if feasible.

---

## Phase 3 implementation log (started 2026-09-29)

Decisions D23–D28 (README). Baseline: State 358, Validation 45, Validation.Generator 39, Messaging 120 (+2 skipped),
Cqrs.SourceGenerator 6 (+1 skipped), Cqrs.Compensation 17, Cqrs.Tenancy 11.

**Step 1 — Error & result types (G4, G5) — DONE.**
- `YFex.Cqrs` → `Validation/YFex.Validation` project reference. `YFex.Cqrs.ValidationResult`/`ValidationError` deleted (D28);
  all builder/registry/dispatcher/test usages moved to `YFex.Validation.ValidationResult`.
- `Error` is now plain fields (`Type`, `Message`, `Violations`, `HasViolations`), safe on `default`; `Error.Validation(in
  ValidationResult)` → `ValidationProblem` whose `Message` is the first **error** as `"Path: detail-or-code"`.
  `ErrorDetail`/`Details` dropped (no consumer anywhere under `C:\Dev\YFex`).
- New helpers: `QueueableResult(<T>).Invalid(in ValidationResult)` + `.Unauthorized(string)`,
  `CacheableQueryResult<T>.ValidationProblem(in ValidationResult)`, `Result*.ValidationProblem(in ValidationResult)`.
- `LocalDispatcher`: validation failure → `Error.Validation` (was `ErrorType.Fail` + joined string, `FormatErrors` removed);
  unauthorized commands → `ErrorType.Unauthorized` (was `Fail`).
- `ICommandValidator`/`IQueryValidator` → `[Obsolete]`, return the new `ValidationResult`. **Deviation found:**
  `Validate<TRule>() where TRule : IContextRule<T>` cannot coexist with the old `Validate<TValidator>() where TValidator :
  ICommandValidator<T>` (constraints don't distinguish overloads → CS0111). Nothing implements the old interfaces, so step 3
  renames the obsolete registration (see step 3 entry).
- `UseFluentValidator` maps failures through a temporary internal `FluentValidationMapping` (path = PropertyName, code =
  ErrorCode or `"fluent"`, detail = message) until task 7 moves it out.
- Downstream builds checked green: Messaging.Rpc.Wolverine, Cqrs.Compensation(.Tests), Cqrs.Tenancy(.Tests),
  CustomLogic.Cqrs, Orchestration.Rpc, Capabilities.Cqrs, EventSourcing.
- Tests: `Messaging.Tests/Cqrs/StructuredErrorTests.cs` (+5) → Messaging 125 (+2 skipped).

**Step 2 — fluent `.Validation(...)` + generator parsing (G8, D27) — DONE.**
- Runtime (`YFex.Validation/Fluent.cs`): `ValidationBuilder<T>` (`Field`, `Each<TItem> where TItem : IValidatable<TItem>`,
  `Invariant`, static `Apply(rules)`), `FieldRuleBuilder<T,TMember>` (Required, NotEmpty, Length, Range(coerce), Pattern,
  Email, With<TRule>, With<TRule,TArg>, When, Severity, Code, Profile), `FluentRuleKey` (canonical key format shared with
  the generator + `PathOf(lambda text)` + non-boxing `Raw<T>`). `RuntimeRule<T>` renamed **`InvariantFn<T>`** (09 name);
  `RuntimeRules<T>.Register(key, fn)` is keyed/idempotent (registry rebuilds never duplicate). New
  `CompiledFluentRules<T>` (D27 list). `ValidationSlot<T>.Validate` falls back to `RuntimeRules<T>` when no generated
  validator exists (non-partial / other-assembly messages would otherwise never be validated).
- Cqrs: `.Validation(Action<ValidationBuilder<TMsg>>)` on the three builders → `ValidationBuilder<TMsg>.Apply` immediately
  at `Configure` time (keyed registration makes repeated registry builds safe).
- Generator: `Parser.Fluent.cs` — second syntax pipeline (`IsFluentCandidate`/`TransformFluent`, tracking name
  `YFexValidation.Fluent`) recognises ANY `Validation(Action<ValidationBuilder<T>>)` call (not tied to YFex.Cqrs). Constant
  rules become the same `RuleModel`s as attributes (rule builders refactored onto `RuleInput`/`ArgValue` so attributes and
  fluent calls share `BuildRule`/`BuildRange`/`BuildValidateWith`). `FluentMerge` (after `Collect`, combined with the
  compilation) rebuilds every emittable target with the union of attribute + all configs' rules (YFV0011 on conflicts),
  lists only keys whose rule actually landed, and rebuilds VMs whose `[ValidatesAs]` contract is fluent-only (G8, via
  `[ThreadStatic] t_fluentTargets` consulted by `IsValidatableType`). Module initializer emits
  `CompiledFluentRules<T>.Set(...)`. YFV0003 (not partial) / YFV0004 (non-constant, runtime-only) reported.
- **Semantics chosen (document in 09 §2):** `Severity`/`Code` modify the rule just before; `When`/`Profile` apply to every
  rule declared before them in the chain (FluentValidation convention). `When` is compiled only for `c => c.BoolMember`
  (attribute semantics); other lambdas → runtime. Keys include the `When` lambda text (`[CallerArgumentExpression]` ==
  syntax `ToString()`).
- **Deviations:** `Each` returns `ValidationBuilder<T>` (no `EachRuleBuilder`; element rules come from the element type).
  `With<TRule>` cannot run at runtime (static abstract) → registry build throws `NotSupportedException` when it was not
  compiled (non-partial message, or a generic rule type — `typeof(TRule).FullName` can't match the generator key).
  Runtime `Range(coerce: true)` registers nothing yet (clamping = step 5). Fluent `Invariant` reads are not inferred for
  the UI. A get-only property with only fluent rules is still discovered (DiscoverMembers takes the fluent names).
- **Bug found & fixed while testing:** the generator's semantic model is pre-generation, so
  `Each<TItem> where TItem : IValidatable<TItem>` over a generator-made validatable element doesn't bind and
  `GetSymbolInfo().Symbol` is null — the whole `.Validation` call was silently dropped. The parser now falls back to a
  single `CandidateSymbols` entry.
- **Pre-existing bug noticed (not fixed, spun off as a separate task):** YFV diagnostics use external-file locations
  (`Location.Create(path, …)`), so `#pragma warning disable YFVxxxx` does not suppress them.
- Tests: `Validation.Generator.Tests/FluentGeneratorTests.cs` (+9) → 48; `Validation.Tests/FluentRuntimeTests.cs` (+13:
  proves generator keys == runtime keys on real generated code, no rule runs twice, slot fallback, runtime Each, With
  fail-fast) → 58. State 358, Messaging 125 (+2 skipped) unchanged.

**Step 3 — typed stages (G1, G3, G4, D25) — DONE** (+ step 6's shape-before-enqueue, same code).
- `YFex.Cqrs/Runtime/MessagePipeline.cs`: `ContextRuleFactory<T>` (`Shared`, `FromServices<TRule>` = DI or
  `ActivatorUtilities` with `[DynamicallyAccessedMembers]`), `MessageValidator<T>` (stage 5, sequential, stops after the first
  rule reporting an error), `MessagePipeline.CheckShape<T>(in T, out ValidationResult)` (zero-alloc when valid, proven by a test)
  / `CheckShape(object, …)` / `CheckContextAsync`. Policies: `Validate: Func<object,…>` **removed** → `MessageValidator? Validation`
  (stage 5 only; stage 1 needs no policy). `ValidatorDescriptor`/`ValidatorKind` deleted; builders store `ContextRuleFactory`.
- Builder API: `Validate(Func<T,CT,ValueTask<ValidationResult>>)`, `Validate(Func<T,ViolationCollector,CT,ValueTask>)`,
  `Validate<TRule>() where TRule : class, IContextRule<T>`; obsolete validators via **`[Obsolete] UseValidator<TValidator>()`**
  (renamed — constraints can't disambiguate the old `Validate<TValidator>()`, logged in step 1). Adapters in `ContextRuleAdapters.cs`.
- `YFex.Validation`: `ObjectValidation` (type-keyed stage 1 for `object` boundaries), filled by `ValidationSlots.Register<T>` and
  by `ValidationBuilder<T>.Register` (runtime-only messages).
- `LocalDispatcher`: 1 shape → 2 authorize → 5 context → CustomLogic → handler for queries and both command shapes; offline command
  paths run stage 1 before the offline handler/enqueue (G6). Unwraps a `ValidatingHandlerInvoker` (no double validation).
- Server (D25): `YFex.Messaging.Rpc/ValidatingHandlerInvoker` (stages 1 + 5) + `Decorate(IServiceCollection)` called by
  `UseYFexMessagingRpcServer` and again by `UseYFexMessagingRpcServerWithWolverine` (its `RemoveAll` dropped it). Because
  `IHandlerInvoker.InvokeAsync<T>` returns the raw value, a rejection is thrown as the new **`YFex.Cqrs.ErrorResultException`**
  (`TypedError`); Wolverine's `ResultConversionException` now derives from it. The generated `{Aggregate}RpcContractImpl` catches
  the BASE type for commands **and queries** (queries had no catch; commands caught the Wolverine type → generated servers no
  longer need a Wolverine reference). `WolverineLocalDispatcher` runs stages 1 + 5 itself (optional registry/services ctor args).
- **Not done — needs a decision:** stage 2 (authorization) is NOT run server-side; nothing in the server stack authorized RPC
  calls before this phase either, and there is no RPC principal source. Context rules therefore run for unauthorized RPC callers.
- **Pre-existing Cqrs generator bug fixed:** a record without the `Command`/`Query` suffix (`CreateCustomer`) produced a static
  helper with the type's own name (CS0102). Such helpers are now named `{Name}Async`.
- Tests: `Messaging.Tests/Fixtures/ShopAggregate.cs` (fluent-only messages, spy log, context rule, `ShopFixture`) +
  `Cqrs/PipelineStageTests.cs` (+10) → Messaging 135 (+2 skipped). Messaging.Tests now references the Validation generator.

**Step 4 — errors over the RPC wire (G2, G7, D26) — DONE, with major pre-existing findings.**
- **Wire format verified empirically:** `RpcSerializationFormatResolver.Default.DefaultFormatKey` = **`mempack6` (MemoryPack)**.
- **Pre-existing, severe:** `Result`/`Result<T>`/`QueueableResult(<T>)`/`Error` had NO MemoryPack formatter
  (`… is not registered in this provider`) → every Fusion RPC call returning a result failed to send its response.
  Fixed: `YFex.Messaging.Rpc/Serialization/RpcResultFormatters.cs` — versioned formatters for `Error` (+ full `ViolationList`:
  path template + indices, code, severity, args, origin, detail), `ValidationResult`, `Result`, `Result<T>`, `QueueableResult`,
  `QueueableResult<T>`; module initializer registers the non-generic ones (+ open generics when dynamic code is supported);
  `RpcResultFormatters.Register<T>()` for AOT, emitted by the CQRS generator per result type
  (`{Aggregate}RpcFormatters` module initializer). YFex core gained `RawValue.GetParts/FromParts` (lossless parts for any
  serializer — D20).
- **Pre-existing: the generated RPC code (Pipeline C) had never compiled** (no project defined `YFEX_RPC`): record names were
  not qualified (`CreateCustomer` instead of `global::Ns.Agg.Commands.CreateCustomer`) and `[CommandHandler]` had no using.
  Fixed in `RpcContractCodeBuilder` (qualified names, `global::ActualLab.CommandR.Configuration.CommandHandler`).
- **Pre-existing, NOT fixed (architecture): Fusion 13 builds RPC proxies at compile time (ActualLab.Generators), and a generator
  cannot see another generator's output → the CQRS-generated `I{Aggregate}RpcContract` never gets a proxy** ("doesn't have a
  proxy type generated"). Apps must declare the contract interface in source. The test project does that (`IShopRpc`, forwarding
  to the generated `ShopAggregateRpcContractImpl`, so the generated server path is what runs). Needs a design decision (e.g. the
  CQRS generator stops emitting the interface and the app declares it, or emits a `.cs` contract via a tool/analyzer fix).
- `FusionMessageBusBuilder`: new overloads taking proxies that return `Result<T>` / `QueueableResult<T>` / `QueueableResult`
  (entries store the typed delegate; dispatch recovers it with a type test). Generated `RegisterRoutes` now uses them — the server
  `Error` reaches the caller instead of `InvalidOperationException("Command failed.")` (G2). `FusionMessageBus` also runs stage 1
  before calling the server / enqueueing offline (G6 for this dispatcher).
- `SyncFailure.Violations` (additive, `[JsonConverter(ViolationListJsonConverter)]`, lossless compact JSON) filled on escalated
  replay rejections (G7).
- ProblemDetails (09 §5): no ASP.NET host in this repo (YFex.Web is a separate repo) → deferred to the host integration.
- New test project **`src/Messaging/YFex.Messaging.Rpc.Tests`** (imports `YFex.Messaging.Rpc.targets` like a NuGet consumer →
  `YFEX_RPC`; test-only `ActualLab.Generators 13.0.12`, approved by the user): two DI containers/hubs bridged in memory
  (`RpcFixture.InMemoryBridgeClient`), so every call is really serialized. `WireFormatTests` (4) + `RpcRoundTripTests` (7).
  ActualLab peer teardown takes ~4 s → the fixture is an `IClassFixture`. Messaging.Tests +1 (failure-log violations) → 136.

**Step 5 — queries (09 §7) — DONE.** `IValidatable<T>` gained a default `static virtual T Coerce(in T value) => value`; the
generator overrides it for records/structs with `Range(coerce: true)` members (`value with { Take = Rules.Take.Coerce(value.Take) }`).
`CoercionSlot<T>` (filled by `ValidationSlots.Register<T>`), `ObjectValidation.Coerce(object)`, `MessagePipeline.Coerce<T>`.
LocalDispatcher / FusionMessageBus coerce + run stage 1 BEFORE the offline cache path and the cache key (an invalid query never
touches the cache, online or offline); the server decorator coerces (old clients) and WolverineLocalDispatcher coerces.
A non-compiled fluent `Range(coerce: true)` fails fast at registry build (clamping must rebuild `T`; no reflection).
Tests: `Messaging.Tests/Cqrs/QueryCoercionTests.cs` (+4) → 140.

**Step 6 — offline (G6) — DONE** as part of steps 3/4 (LocalDispatcher and FusionMessageBus offline command paths run stage 1
before the offline handler and the enqueue; tests `PipelineStageTests.Offline_invalid_command_is_never_enqueued`,
`RpcRoundTripTests.FusionMessageBus_offline_never_enqueues_an_invalid_command`).

**Step 7 — `YFex.Cqrs.FluentValidation` — DONE.** New `src/Cqrs/YFex.Cqrs.FluentValidation` (+ `.Tests`, both in `yfex.slnx`
`/Cqrs/`); `YFex.Cqrs` no longer references FluentValidation (test asserts it). Failures → violations: path = PropertyName,
code = ErrorCode (or `"fluent"`), detail = message, severity mapped. **Call-site deviation:** `.UseFluentValidator(new V())`
instead of `.UseFluentValidator<V>()` — the `<V>()` shape needs explicit type arguments on a C# 14 generic extension-block member,
which the .NET 11 preview compiler (11.0.100-preview.4) does not resolve (the static form compiles; the instance form reports
CS1061). Revisit when the compiler supports it.

**Step 8 — handlers (⚑P7 → D24) — DONE.** `IValidatedCommandHandler<TCmd,TResult>` / `IValidatedCommandHandler<TCmd>` in
`HandlerInterfaces.cs`. The typed invokes of `LocalHandlerInvokerRegistryBuilder.AddCommand` prefer a registered validated handler
(`Validator.AssumeValid` — the pipeline already ran) and fall back to `ICommandHandler`; the generated registrations (per message
type) needed no change. Wolverine discovers handlers by its own conventions → validated handlers are not wired there (documented
gap). `IRuledCommandHandler` is Phase 4. Test: `PipelineStageTests.Validated_handler_receives_Valid_T_and_wins_over_the_classic_one`.

**Step 9 — VM `ApplyServer` Cqrs overloads (07 §7) — DONE.** Emitted only when the compilation has `YFex.Cqrs.QueueableResult\`1`
(`VmModel.HasCqrs`): `ApplyServer<TContract,TResult>(in QueueableResult<TResult>)`, `ApplyServer<TContract>(in QueueableResult)`,
`ApplyServer<TQuery,TResult>(in CacheableQueryResult<TResult>)` → Queued / Invalid (violations mapped to VM fields) / Failed
(one form-level violation `server.<type>` with the message as detail) / Ok (server violations cleared).

**Bug fixed — YFV diagnostics ignored `#pragma warning disable` (pre-existing, all diagnostics).** `LocationInfo.ToLocation()`
built external-file locations (`Location.Create(path, …)`). Reporting now happens in nodes combined with the compilation
(`SourceTrees` maps paths back to the real `SyntaxTree`, in-source locations); emission nodes stay cached. Proven by a real build:
the `#pragma warning disable YFV0003` region in `Validation.Tests/FluentRuntimeTests.cs` no longer warns.
Tests: `Validation.Generator.Tests/PragmaSuppressionTests.cs` (+2) → 50.

**Phase 3 acceptance — MET.**
- 09 §11 (non-rules parts): stage order with spies, structured violations over LocalDispatcher AND over real Fusion RPC, invalid
  query not cached, coerce before cache key, invalid command never enqueued (both dispatchers), replay rejection keeps violations
  in `ISyncFailureLog`, YFex.Cqrs has no FluentValidation reference, adapter tests green.
- End-to-end (`Messaging.Rpc.Tests/AcceptanceTests.cs`): `CreateCustomer` configured ONLY with fluent `.Validation(...)` → a VM with
  `[ValidatesAs<CreateCustomer>]` borrows its rules (G8) → the server rejects over RPC (shape AND context rule) →
  `ApplyServer<CreateCustomer, int>(result)` returns `Invalid` and the violation is on the mapped field (`Document` → `CpfCnpj`).
- Stage 1 of a valid command allocates 0 bytes (`PipelineStageTests.Stage1_of_a_valid_command_allocates_nothing`).
- AOT: `YFex.Validation.Smoke` native AOT publish clean and green; `YFex.Messaging.Rpc.AotSample` (IsAotCompatible, warnings as
  errors) builds clean (`-p:NuGetAudit=false` needed only because the private feed `nuget.tratorimetro.com` is unreachable here).
- Final counts: YFex.Tests 55, Validation 58, Validation.Generator 50, State 358, Messaging 141 (+2 skipped), Messaging.Rpc 15,
  Cqrs.SourceGenerator 6 (+1 skipped), Cqrs.Compensation 17, Cqrs.Tenancy 11, Cqrs.FluentValidation 3.

---

## D29/D30 implementation log (Phase 3 follow-ups, 2026-09-30)

Decisions D29 (implicit RPC call context) and D30 (CQRS generator emits the Fusion proxies) — README. Baseline = Phase 3
final counts. Nothing committed (same working tree as Phase 3).

**D30 — generated Fusion proxies — DONE.**
- `YFex.Cqrs.SourceGenerator/RpcProxyCodeBuilder.cs` (Pipeline C, YFEX_RPC) emits `{Ns}.ActualLabProxies.I{Aggregate}RpcContractProxy`,
  `I{Aggregate}RpcClientProxy` (interface proxies, `InterfaceProxy` base) and `{Aggregate}RpcContractImplProxy` (class proxy, mirrors
  the `IHandlerInvoker` constructor) with ActualLab.Generators 13.0.12's exact template (field layout, shared `__cachedIntercept`
  per return type, `ArgumentListG{n}`/`S{n}` with casts beyond 4 items, `KeepCode` module initializer, copied usings). The models
  carry the result type in ActualLab's two display formats (`ProxyReturnType` = `ToGlobalName`, `KeepReturnType` = `ToFullName`).
- New client view `I{Aggregate}RpcClient : I{Aggregate}RpcContract` + `rpc.Add{Aggregate}RpcRemoteClient()` =
  `rpc.AddClient<I…RpcClient>(nameof(I…RpcContract))` (Fusion's same-hub pattern, `FusionRpcCallTypeDowngradeTest`).
  `fusion.Add{Aggregate}RpcClient()` (compute client of the contract) is unchanged for client-only processes.
- **Pre-existing bug found:** the generated `[CommandHandler]` on command methods made `AddServer` fail at runtime ("Wrong command
  handler method arguments": CommandR requires ActualLab `ICommand<T>` arguments; the Phase 3 hand-written test contract had
  silently dropped the attribute). Command methods are now regular RPC methods; invalidation stays `Invalidation.Begin()` after
  success. Consequence: no CommandR pipeline/operation log for generated commands (multi-host invalidation is an open item).
- **Golden test** `Messaging.Rpc.Tests/ProxyGoldenTests`: runs `CQRSGenerator`, then the REAL `ActualLab.Generators.ProxyGenerator`
  (loaded from the package folder: `ExcludeAssets="all" GeneratePathProperty="true"` + assembly metadata) on a source copy of the
  generated contract + impl, and compares the three proxies (namespaced + global-namespace aggregates; nullable/array/shared
  return types, void commands). Also compiles the generated code. The test project gained `Microsoft.CodeAnalysis.CSharp 4.11.0`
  (same pin as the generator tests) and references the generator assembly (`ReferenceOutputAssembly="true"`).
- Workaround removed: `ShopRpc.cs` deleted; `RpcFixture` uses the generated `AddShopAggregateRpcServer`,
  `AddShopAggregateRpcRemoteClient`, `IShopAggregateRpcClient` and `ShopAggregateRpcClientRegistration.RegisterRoutes`.

**D29 — implicit call context — DONE.** Code in `YFex.Messaging.Rpc/CallContext/` (YFex.Cqrs untouched; the ASP.NET-specific
capture lives in YFex.Messaging.Rpc, which already carries Fusion.Server + the ASP.NET framework reference — no new package).
- `CallContext` (AsyncLocal: `Auth` = `IAuthContext`, `Scope` + `HasScope`, `Culture`, `CorrelationId`, `Subject`; `Enter()` also
  enters a `ScopeOverride` of the tenant when `WithTenant()`); `CallIdentity` (principal, token, scope, expiry from token/`exp`,
  cached resolved auth + permissions version).
- Sources (`ICallIdentitySource`, first AUTHENTICATED wins; a presented-but-rejected token → anonymous, no fallback; expired → skipped):
  `FromConnection()` (default; `CallContextConnections.Wrap` decorates the `RpcPeerOptions` DI singleton's `ServerConnectionFactory`,
  captures `HttpContext.User`, bearer from `Authorization` or `access_token` query, and with `WithTenant()` the host's
  `IScopeContext` for the upgrade request, stored in the connection's `PropertyBag`), `OrFromHeader(name)` (+ `yfex-scope` header;
  app `ICallTokenValidator` validates token AND requested tenant; cached per token+scope), `OrFromSession(resolve)`
  (`SessionBoundRpcConnection`), `OrFrom(custom)`.
- `CallContextMiddleware : IRpcMiddleware` (priority 990 — after argument validation/session replacer, before Fusion's compute and
  command handlers): resolves the context, enters it, coerces arg0 (replacing it), and when the shape is valid runs stage 2 with
  the registry's compiled authorizer — rejection = the method's typed result via `RpcErrorResults<T>` (filled by
  `RpcResultFormatters.EnsureRegistered/Register<T>`, i.e. the generated per-aggregate registration; unknown T → `ErrorResultException`).
  Marks the authorized instance (`CallContext.AuthorizedMessage`) so `ValidatingHandlerInvoker` skips its own stage 2.
  **Why a middleware:** queries are Fusion compute methods cached by arguments — authorizing inside them would serve one caller's
  cached value to another (proved by `Stage2_runs_outside_the_compute_cache`, which fails when the middleware's stage 2 is removed).
- **Compute-cache variant (added while implementing; not in the approved text — review):** once tenant/user reach handlers, a
  compute query cached by arguments only would leak across tenants. Generated query methods now take
  `string? variant = null` (routes pass null); the middleware overwrites it from the query's `CacheScope`: Global/Tenant → tenant,
  User/Custom → tenant + user, Session → tenant + connection. Never shared across tenants even for `Global`.
  (`Compute_cache_is_keyed_per_tenant_and_user` fails when the overwrite is removed.)
- Stage 2 everywhere via `CallAuthorization` / `CallPrincipalSource` (`CallContext.Current ?? IPrincipalAccessor.Current ?? anonymous`,
  accessor resolved lazily): `LocalDispatcher` (replaces `ClaimsPrincipal.Current ?? new()`), `ValidatingHandlerInvoker`,
  `WolverineLocalDispatcher` (now 1 → 2 → 5, returns `Error`), `FusionMessageBus` (client, UX only, online paths).
  `AddYFexCqrsAuthorization`'s principal factory = `CallAuthorization.ContextFor` → reuses the already-resolved auth context.
  `CallContextPrincipalAccessor.Decorate` wraps the registered `IPrincipalAccessor` (keeps its lifetime) so other consumers
  (Capabilities, custom handlers) see the RPC caller.
- Permissions: `IPermissionsVersions` (default in-memory `PermissionsVersions`, `Bump`/`BumpAll`), checked per call (one dictionary
  read) → `IAuthContextFactory.Create` only on change (inside the caller's tenant for scoped providers). `CallContextSignals`:
  `PermissionsChanged(subject)` / `AllPermissionsChanged()` (bump + invalidate the client computed), `RevokeAsync(subject)` (bump +
  `CallContextConnections.DisconnectAsync` → the client reconnects and the new upgrade is authenticated again).
- `ICallPermissions` compute service (`GetMyPermissionsAsync(string? caller = null)` → `CallPermissions` MemoryPackable snapshot);
  the middleware forces `caller` = tenant|subject (anti-spoofing, per-caller cache key). Server registered by
  `UseYFexMessagingRpcServer`, client (`fusion.AddClient<ICallPermissions>()`) by `AddYFexMessagingRpcClient`. Its proxies are
  ActualLab output checked in verbatim (`CallContext/ActualLabProxies/*.cs`); `ProxyGoldenTests.Library_proxies_…` regenerates and compares.
- Client header option: `YFexMessagingRpcClientOptions.CallContext(ctx => ctx.SendToken((sp, ct) => …).SendTenant())`;
  `FusionMessageBus` wraps each proxy call in `RpcOutboundCallSetup { Headers }`. **Limitation:** Fusion compute-client calls
  (`fusion.AddClient<T>()`) create their own setup — no custom headers; they rely on the connection identity.
- Wolverine: `UseYFexMessagingRpcServerWithWolverine(configure)` now forwards its options (call context) to the base registration.
- Tests (`Messaging.Rpc.Tests/CallContextTests.cs`, `CallContextSupport.cs`; the fixture connects through the real
  `ServerConnectionFactory` with a fake upgrade `HttpContext`): connection identity + tenant reach a handler that reads only
  `IPrincipalAccessor` + YFex.Tenancy; header ignored when the connection is authenticated; permissions resolved once per version;
  anonymous → `ErrorType.Unauthorized` over the wire and the context rule does not run; shape before authorize; per-call tokens;
  invalid token → anonymous; tenant header granted only by the validator; compute cache per tenant/user; `FusionMessageBus` sends
  token + tenant headers; permission change → server reload + client computed invalidated; revocation closes the connection and the
  reconnect is anonymous; client-side stage 2 never sends a denied command. Mutation-checked (middleware stage 2, variant overwrite).
- Final counts: YFex.Tests 55, Validation 58, Validation.Generator 50, State 358, Messaging 141 (+2 skipped), **Messaging.Rpc 40**
  (15 + 9 golden + 16 call context), Cqrs.SourceGenerator 6 (+1 skipped), Cqrs.Compensation 17, Cqrs.Tenancy 11,
  Cqrs.FluentValidation 3. Builds clean: Messaging.Rpc.Wolverine, `Messaging.Rpc.AotSample` (warnings as errors, `-p:NuGetAudit=false`),
  EventSourcing, Jobs.Hub.Abstractions, Orchestration.Rpc.

Open items from D29/D30 (not blocking Phase 4):
- Denied compute calls have no computed → Fusion sends an invalidation after one tick; a client that LIVE-binds a denied query
  re-asks every tick. Fine for one-shot `FusionMessageBus` calls; don't live-bind denied queries (or gate them client-side).
- Generated commands bypass CommandR → no operation log / multi-host invalidation; revisit if the server is a farm.
- `PermissionsVersions`/`CallContextConnections` are per process: a farm must fan `PermissionsChanged`/`RevokeAsync` out to every node.
- Connection-bound identities expire with their token (`exp`) → calls become anonymous; the client must reconnect with a fresh token
  (or use the header option). No automatic re-auth push yet.
- `EventChannelHost<TEvent>` (library compute service, generic) has no ActualLab proxy either (pre-existing; not addressed).
- `AddYFexAuthorizationAspNetCore` called AFTER `UseYFexMessagingRpcServer` replaces the decorated accessor (dispatchers still read
  `CallContext` first; only other `IPrincipalAccessor` consumers lose the RPC caller) — register authorization first.

---

## Phase 4 handoff (written at the end of the Phase 3 session, 2026-09-29)

Start protocol: read README (D1–D28), this file's Phase 3 log above, 04, 05, 06, 09 §6 + §8 (rules stage, fingerprints,
`DecisionMismatchPolicy`), 11 §2.3/§2.6/§2.10/§2.11. Baseline = the final counts above. Ask ⚑P8 (`[SituationSource]`) and ⚑P9
(subtotal discount allocation) before coding.

Open items carried from Phase 3 (decide before/while doing Phase 4):
1. ~~Server authorization (stage 2) not performed server-side~~ — **RESOLVED by D29** (2026-09-30, see "D29/D30 implementation
   log"): stage 2 runs in `CallContextMiddleware` (RPC, outside the compute cache) and in every dispatcher; the caller + tenant are
   an implicit `CallContext`.
2. ~~Pipeline C vs Fusion 13 compile-time proxies~~ — **RESOLVED by D30**: the CQRS generator emits the proxies (golden-tested
   against ActualLab.Generators 13.0.12); `ShopRpc.cs` is gone. Re-run `ProxyGoldenTests` on any Fusion upgrade.
3. ProblemDetails mapping (09 §5) — belongs with the ASP.NET host (YFex.Web repo).
4. `.UseFluentValidator<V>()` call site — restore when the compiler resolves generic extension-block members with explicit type args.
5. Wolverine handler discovery does not know `IValidatedCommandHandler` (and will not know `IRuledCommandHandler`).
6. Where Phase 4 plugs in: stages 3–4 go between authorize and `CheckContextAsync` in `LocalDispatcher`,
   `ValidatingHandlerInvoker` (after its stage-2 block — for RPC calls stage 2 already ran in `CallContextMiddleware`),
   `WolverineLocalDispatcher.ValidateAsync` (returns `Error?`) and client-side in `FusionMessageBus` if decisions are evaluated
   locally. Situation loaders get the caller/tenant implicitly (`CallContext.Current`, `IPrincipalAccessor`, `IScopeContext`) — but
   09 §10 still applies: `[Scope]` fields are copied into the situation so decisions stay replayable. `ErrorType.ApprovalRequired` +
   `Error.Approvals` extend `RpcResultFormatters` (bump `Version`); a middleware rejection of a new result type needs
   `RpcErrorResults<T>` (automatic for `Result<T>`/`QueueableResult<T>`).
7. D29/D30 open items: see the end of "D29/D30 implementation log" above.

---

## Phase 4 implementation log (started 2026-09-30)

Decisions D31–D34 (README). Baseline = Phase 3 final counts + Messaging.Rpc.Tests 40 (after D29/D30).

**Step 1 — `YFex.Rules` runtime (4a tasks 2–7, engine side) — DONE.** New `src/Validation/YFex.Rules` (+ `YFex.Rules.Generator`
stub, `YFex.Rules.Tests`; all in `yfex.slnx` `/Validation/`). Folders: `Situations/` (attributes, `ISituation<T>`, schema, field
handles, canonical `SituationWriter/Reader`, `ScopeKey`, loaders/sanitizer), `Rules/` (`IRule` markers, `[Rule]`/`[Effective]`/
`[Sealed]`/`[RuleSet<T>]`, `RuleContext`, `RuleParameter<T>`), `Definition/` (stages, groups, `Resolve`, `Allocation` (D32),
`DerivedField`, `RuleDescriptor`, `TableBinding`, `RuleSetDefinition` + builder, `RuleCatalog`), `Directives/`, `Decisions/`
(`Decision`, `Verdict`, adjustments/allocations, approvals, `RuleRef`), `Tracing/` (trace, `Explanation`, `RuleMessages` en/pt),
`Tables/` (cells, attributes, `IDecisionTable`, versions, `FrozenTable`, publish checks), `Hosting/` (`RuleData` snapshots,
overrides, `CompiledRuleSet`, `RuleSetHost`), `Approvals/` (D34 tokens, `IDecisionBound`, `DecisionMismatchPolicy`, `EnforceOptions`),
`Engine/Evaluator.cs` (04 §7). YFex core gained `RawValueConvert` (generic non-boxing T ↔ RawValue, D20).
Proven first with hand-written generator-shaped code (`Rules.Tests/HandWritten/*`, the doc-12 sale) → 22 tests.

Deviations / decisions taken while implementing (all small; revisit if the user disagrees):
- `IRule<TS>` / `IRule<TS,TLine>` are **marker interfaces**: static abstract members can't accept the two documented method
  shapes (with/without `RuleContext`); the generator validates the shapes (YFR0010) and emits static-lambda adapters.
- `DirectiveWriter.Limit(field, min:, max:)` takes `LimitBound<T>` (implicit from `T`) — an unconstrained `T?` can't tell "no min"
  from `0`. Untyped variant for tables/dynamic rules: `LimitRaw`.
- `AdjustTarget.Named(key)` dropped (no base semantics defined); only `Line` and `Subtotal`. Positive amount = reduction,
  negative = increase (fees); never below zero.
- Line base / situation base are **declarative per rule set**: optional `static Money LineBase(in TS, in TLine)` /
  `static Money Base(in TS)` (engine stays business-agnostic — no built-in `Quantity × UnitPrice`).
- Trace case "Denied" is named **`Blocked`** (`Denied` is the Verdict case type; union cases must be distinct types).
- Hit policies `CollectMax/CollectMin` not implemented (Collect + the group strategy covers them); `Complete = true` gap analysis
  **deferred** (overlap + unreachable checks are implemented).
- `RuleOverrideAction` gained **`Enable`** (a store can cancel a tenant's Disable).
- `IDecisionBound.Approvals` is a list (one token per required permission) instead of a single `OverrideToken? Approval`.
- MostRestrictive/LeastRestrictive groups with several adjusting candidates keep only the smallest/largest adjustment; the
  others' non-adjust directives still apply.
- Fingerprint also hashes **derived field values** (two decisions differing only in `InstallmentValue` were equal — found by the
  determinism test).
- `DefaultRows` of a table: `ReadOnlySpan<Row> => [...]` of a non-primitive struct does not compile (CS9203, as 05 §1 warned) →
  convention `public static readonly Row[] DefaultRows = [...]`; the generator emits the explicit `IDecisionTable.DefaultRows` forwarder.
- D34 offline approvals: `DeviceOverrideTokenIssuer` (ECDSA P-256 device key) + `DeviceOverrideTokenVerifier` (device public keys
  via `IDeviceKeyStore`, per-permission `OfflineApprovalCap` on the requested value (arg {1} of the limit violation) + max age;
  always `Flagged`). Explicit `RequireApproval` directives (no requested value) are not approvable offline. **Security review still
  required.**

**Step 2 — `YFex.Rules.Generator` (4a tasks 1, 3, 6-conditions, 7) — DONE.** `[Situation]` → `ISituation<T>` (+ `Fields`,
`FieldIds` consts (cross-assembly binding), `Schema` with enum options, `Read`, `TryGetFieldId`, `ReadLine`, `AsOf`, `Scope`,
`AppendTo`/`Serialize`/`Deserialize`, `DiffMask`, `Hash`/`WriteTo`/`ReadFrom`); supported field types = scalars, enums, core value
types, single-scalar ids (`TenantId(string)`), `Nullable<>` of those, one `ImmutableArray<TLine>`. `[RuleSet<TS>]` + `[Rule]` +
`[DecisionTable<TS>]` → `Set` (Lazy), `Evaluate` (code-only / host), `CreateHost`, `Catalog`, `RuleIds`, rule adapters (both method
shapes), condition splitting with **fully-qualified rewrite** of each `&&` operand (`ConditionSplitter`; operands touching the
`RuleContext`/locals/private members → rule not splittable, YFR0016 info), tables (`Match`/`Band`/value cells, situation / line
(`"Lines[].X"` → line table) / rule-set derived field binding, row `[Validatable]` + guardrail in `CheckRow`, cell constraints in
the schema for the editor). Diagnostics YFR0001–0006, 0010–0017, 0020–0021, 0101–0106 (in-source locations → `#pragma` works).
Tests: `Rules.Tests/Generated/*` (the doc-12 sale written with attributes only; outcomes asserted identical to the hand-written
set) + `YFex.Rules.Generator.Tests` (15: clean emission, every error id, caching).
- Bugs found & fixed: enum-typed attribute arguments arrive as their underlying `byte` (DirectiveKind/HitPolicy/Exceed/ScopeKind
  were silently read as 0); pooled evaluation scratch nested in `Evaluator<TS>` broke when one thread evaluated two situation types.
- Rule-set conventions (additions): optional `static Money LineBase(in TS, in TLine)` / `static Money Base(in TS)`, optional
  `Allocation` / `Rounding` static members. Derived-field names are read from the `DerivedField.After(stage, "Name", …)` literal.
- Not done yet in the generator: `RowsJson` (row JSON for store/files/wire — lands with 4b's store), applicability index (04 §12.1,
  optimization), per-rule read masks for incremental UI evaluation (04 §12.3, after v1 per design).
- Counts: Rules.Tests 31, Rules.Generator.Tests 15.

**Step 3 — Cqrs stages 3–4 (4a task 8, D33, D34) — DONE.** `YFex.Cqrs` → `YFex.Rules`. Verbs on the command/query builders:
`.UseSituation<TS, TLoader>()` (loader from DI, else constructed) → `.Enforce(ruleSet, EnforceOptions?)` → `.OnDecisionMismatch(p)`
(typed: `Enforce` without a matching `UseSituation` throws at registry build). `Runtime/RulesStage.cs`: `RulesStage<TMsg,TS>` —
gather → evaluate through a DI-registered `RuleSetHost<TS>` for the same definition (else code-only) → Denied = `Error.Validation`,
NeedsApproval = every required permission needs a verified `OverrideToken` bound to the fingerprint (`IOverrideTokenVerifier` from DI;
**device tokens also need `IApproverDirectory`** to re-check the approver, else rejected) else `Error.ApprovalRequired`, fingerprint
mismatch per `DecisionMismatchPolicy` (Reject → `rules.decision.mismatch` violation; Escalate → `ErrorType.Conflict`; AcceptAndFlag →
`IDecisionAuditSink.OnFlaggedAsync`; UseServer). Queries run the stage but never reject (thin clients read decisions).
Wired in `LocalDispatcher` (queries + both command shapes), `ValidatingHandlerInvoker` (server/RPC/replay) and
`WolverineLocalDispatcher`, between authorize and context rules. Hand-off to handlers without touching the registration generator:
`DecisionContext` (AsyncLocal, pinned to the message instance, cleared for nested dispatches); the typed invokes of
`LocalHandlerInvokerRegistryBuilder` prefer `IRuledCommandHandler<TCmd,TResult,TS>` / `<TCmd,TS>` (declared in YFex.Cqrs);
other handlers read `IDecisionAccessor<TS>` (`DecisionAccessor<TS>` singleton). `Error` gained `Approvals` + `DecisionFingerprint` +
`Error.ApprovalRequired(...)`, `ErrorType.ApprovalRequired`; `RpcResultFormatters.Version` = **2** (v1 payloads still read).
Tests: `Messaging.Tests/Cqrs/RulesStageTests.cs` (+8: order gather→context→handler, deny, approval round trip incl. token reuse for
another outcome, mismatch policies, query + sanitizer, server invoker path, classic fallback, config error) → 149 (+2 skipped);
`Messaging.Rpc.Tests/WireFormatTests` (+1 approvals over MemoryPack) → 41. AotSample builds clean.
- Deviations: situation→message path mapping (`SituationPathMap`, 04 §14) not implemented — violations keep situation paths (name
  them like the message fields); Wolverine's handler discovery still doesn't know `IRuledCommandHandler` (use `IDecisionAccessor`).

**Step 4 — VM side (4a task 9, D31) + allocation budget — DONE.**
- `[ConstraintsFrom]` (YFex.Rules attribute, recognized by name in YFex.Validation.Generator — no reference): on an
  `[Observable] Decision<TS>` property the VM emitter (a) merges `decision.TryGetConstraints(path)` (situation or rule-set derived
  field NAMED like the VM property; new `Decision.TryGetConstraints(ReadOnlySpan<char>)`) into `IValidationSource.TryGetConstraints`,
  most restrictive with the static constraints; (b) implements the property's State hook: `Validation.ReplaceAll(Rule, 0,
  decision.Violations)` + `ConstraintsChanged("")` (`""` = re-read everything). `ConstraintsFrom` added to the generator's syntax
  pre-filter. Mapping by name only ([MapsTo] for situations not implemented).
- `[SituationSource<TS>]` (D31) in YFex.Rules.Generator (`SituationSourceGen`): per stored situation field — user method
  `__Situation_<F>()` (non-partial, or `private partial` → declaration emitted) › same-named member (implicit conversion, else
  YFR0031) › generated `private partial T __Situation_<F>();` that the VM must implement (CS8795 names it). Lines: same-named
  `ImmutableArray<TLine>`, or a collection of element VMs carrying `[SituationSource<TLine>]` (their `BuildSituation()`).
  Deviation from the chat sketch: partial methods with a return type need an explicit accessibility → users write
  `private partial TenantId __Situation_Tenant() => …;`.
- Tests: `Rules.Tests/Generated/SaleVmTests.cs` (+3, StateObject VM: build by name + lines + partials; decision → constraints,
  violations (source Rule) replaced on the next decision; approval warning), `AllocationTests` (+1: < 2.5 KB per evaluation of the
  sale — fixed two hidden allocations: a capturing sort lambda per partition and `Guid.ToString` in the fingerprint),
  `Rules.Generator.Tests` (+2: YFR0031, unimplemented field is a compile error). Counts: Rules 35, Rules.Generator 17.

**4a acceptance status:** doc-12 sale scenario passes as tests (breakdown amounts, approval, Pix/Boleto limits, stacking cap,
exclusive promo, exact per-line allocation), determinism ×1000, allocation budget, as-of selection, sealed/guardrail/layer tests,
Cqrs stages, VM projection. Still open in 4a: `RowsJson` (moves to 4b with the store), `Complete` gap analysis, applicability index
(optimization), `SituationPathMap`.


## Domain-agnostic refactor (D35–D38, 2026-09-30)

Why: YFex.Rules/Validation are reused by apps with unrelated domains; the 4a engine hard-coded `Money` (adjustments, caps,
totals, allocation, `RuleContext.Base`), a Tenant/Store hierarchy (`ScopeKey`, `RuleLayer`, `EditScope`, `ScopeKind`) and a
closed list of unit types (`ValueKind.Money…`, FQN tables in both generators). Done **before** 4b's SQL store so the stored
tier-path format is generic from day one.

Rename map (old → new):

| Old | New |
|---|---|
| `ValueKind.Money/Percentual/Quantity/Mass/Length/Custom` | storage `ValueKind.Decimal` + `RawType` (`Money.RawType`…); app types implement `IRawValue<T>` |
| `RawValue.From(Money)` / `TryGet(out Money)` | generic `RawValue.From<T>` / `TryGet<T>` / `As<T>()` (any IRawValue or primitive) |
| `GetParts(…, out aux)` / `FromParts(…, aux)` | `+ out uint type` / `+ uint type` |
| `SituationField<TS,T>(Id, Name, ValueKind)`, `SituationFieldInfo(…, ValueKind, …, ScopeKind scope)`, `TableColumn(…, ValueKind)` | `RawType` instead of `ValueKind`; `int tier` |
| `[AsOf]`, `ISituation.AsOf`, `RuleContext.AsOf`, `NotEffective(AsOf)` | `[RuleEffectiveDate]`, `EffectiveDate` |
| `[Scope(ScopeKind.Tenant/Store)]`, `ISituation.Scope`, `ScopeKey`, `ScopeKeys` | `[RuleTier]`, `TierPath`, `RuleTierPath`, `RuleTierPaths` |
| `RuleLayer` (Code/Tenant/Store/Composer) | tier depth `int` (0 = code) |
| `EditScope` flags (tables, parameters, catalog) | `EditableAt` tier names (`string[]`) |
| `d.Adjust(target, Rate.Percent/Fixed(x), maxAmount)` | `d.Decrease/Increase(target, percent | amount [, max])`, `d.Adjust(AdjustDirective)` |
| `AdjustTarget.Subtotal` | `AdjustTarget.Total` |
| `Resolve.Stack(Percentual? cap, Money? capAmount)` | `Stack(cap)` / `Stack<TAmount>(capAmount, cap)` |
| `BestFor.Customer/Business` | `Prefer.Largest/Smallest` |
| `DirectiveKind.AdjustPercent/AdjustFixed` | `DecreasePercent/DecreaseAmount` (+ `IncreasePercent=9/IncreaseAmount=10`) |
| `RunningTotals` (Money) | `RunningTotals<TAmount>` (`Initial/Current/Final/LineBase(i)`) |
| `DecisionTotals.Gross/Final/AdjustmentsTotal`, `LineTotal(Gross, LineAdjustments, Allocated, Net)` | `Initial/Final/Change`, `LineTotal(Initial, LineChange, AllocatedChange, Final)` (typed RawValues, signed changes) |
| `AppliedAdjustment(…, Money base, Rate rate, Money amount)` | `(…, AdjustDirection, Percentual? percent, RawValue base, RawValue amount)` |
| `LineBaseFn/SituationBaseFn` → `Money` | → `decimal` (generator wraps with `Amounts.ToDecimal`); builder `.Amount<T>()` |
| `CompileNoteKind.NotEditableAtLayer` | `NotEditableAtTier` |
| `IRuleScopeGuard`, admin message field `Scope` | `IRuleTierGuard`, `TierPath` |

Formats: `SituationWriter.FormatVersion` 2 (type id per value; v1 still read), RPC result format **v3** (RawValue type id;
approvals' tier byte = v2 layer byte; v1/v2 still read via `RawValueLegacy`), violation JSON arrays get a 6th element (type id;
5-element arrays = legacy), stored tier path `"code" | "tier:<v1>|<v2>…"` (value = `kind.low.high.aux.type.b64text`).

New generator diagnostics: YFR0007 (> 4 `[RuleTier]`), YFR0022 (Base/LineBase not an amount), YFR0023 (Base vs LineBase
amount types differ), YFR0107 (`EditableAt` names no `[RuleTier]` field). YFR0002 message lists "IRawValue types".

Implementation notes:
- `RawValue` gained `TypeId/IsTyped/Type/Aux/WithType/Untyped/Fits/TryRead(RawType, out …)`; two different declared types
  never compare; a typed amount compares with a plain number by amount (as the old unit kinds did).
- `RawValueConvert.From/TryGet<T>` fall back to the type's converter; unregistered value types are registered by probing
  `default(T)` through the non-generic `IRawValue.RawTypeOf()` default interface method (one box per type, trim/AOT-clean —
  `RuntimeHelpers.RunClassConstructor` was tried first and raised IL2059 in the native AOT smoke).
- Integer amount types round to whole units whatever the rule set's `Rounding` says (`Amounts.EffectiveRounding`).
- `Increase/Decrease` sizes must be ≥ 0 (direction is explicit); a decrease never takes a target below zero; a percent or amount
  in a rule set without amount throws `InvalidOperationException` naming the rule; an amount of another type (Mass in a Money
  rule set) throws too.
- `Allocate` uses a stackalloc scratch (≤ 128 lines) — the allocation budget test (< 2.5 KB) stays green although decisions now
  hold 32-byte RawValues.
- `YFex.Rules.Admin.Tests` never compiled before (4b WIP: `ManualNetworkStatus` from another test project, target-typed `new`
  inside `params`) — minimal local fixes so the publish pipeline is covered (10 tests).
- Not changed: `OverrideTokenKind { Server, Device }` (offline approvals, D34) — optional rename to Online/Offline left to the user.

Tests (all green): YFex.Tests 65 (+8 `RawTypeTests`: app types Temperature/Sku, formatting, parts, hashing, collisions),
Validation 58, Validation.Generator 50, Rules 45 (+5 `DomainNeutralTests`: freight in `Mass` with Carrier/Hub tiers,
HR leave in `int` days, listing eligibility with strings/Sku and no amount), Rules.Generator 21 (+4), Rules.Admin 10,
Messaging 149 (+2 skipped), Messaging.Rpc 41, State 358, Cqrs.SourceGenerator 6 (+1 skipped); Validation.Smoke native AOT
publish without IL warnings; Messaging.Rpc.AotSample builds.


## Phase 4b implementation log (runtime rule editing — completed 2026-09-30)

Baseline = the counts at the end of "Domain-agnostic refactor". User decisions for this step (2026-09-30): the SQL store lives in
a new **`YFex.Rules.Data`** project (→ YFex.Rules + `C:\Dev\YFex\YFex.Data`); do the 4a leftovers, the what-if simulation and
the audit now. Event sourcing for rule data was discussed: kept as an optional future `IRuleStore` adapter (YFex.EventSourcing
references Cqrs/Messaging/Rpc — layer-1 YFex.Rules can't; its offline sync targets client-authored events, while rule data flows
server → device and devices prune; its concurrency is per stream = per rule set, publishing is per table/tier).

**Already built before this step, never logged (4b work of an earlier session):**
- `YFex.Rules/Hosting/RuleStore.cs` — `IRuleStore` (immutable versions, numbered per RULE SET, optimistic concurrency per stream
  `RuleDataKey(RuleSet, Kind, Code, TierPath)`), `InMemoryRuleStore`, `RuleTierPaths` (stored tier-path text), `RuleDataCodec`
  (parameter / override JSON documents).
- `RuleSetHost` hot swap (`Publish`, `Apply(record)`, `LoadAsync(store)`, last-N `RuleData.With(snapshot, keepPerCode)`) and the
  non-generic admin view `IRuleSetHost` (`ValidateTable/Parameters/Override`, `DefaultRowsJson`).
- `YFex.Rules.Admin` (D33): `RuleAdmin` commands `PublishTable/PublishParameters/SetRuleOverride/ApproveDecision`, queries
  `GetRuleData/GetDataVersions/GetDataVersion/GetRuleDefinition`, event `RulesetPublished`; `RulePublisher` (authorize →
  validate → append → apply → notify), `IRuleTierGuard`, `RuleSetRegistry`, `RuleSetSync` (client), `RuleAdminBaseline`,
  `AddYFexRuleSet/AddYFexRulesAdmin/AddYFexRuleSetSync`, `LocalHandlerInvokerRegistryBuilder.AddRuleAdmin()`. 10 tests.

**Store contract.** `IRuleStore.LoadAsync(ruleSet, afterVersion, ct)` (ascending; replaces `LoadAsync(ruleSet)`) and
`GetByVersionAsync(ruleSet, version)` (versions are unique per rule set; `GetRuleDefinition` no longer loads everything).

**Bug fixed — unstable row ids.** Payloads were stored as the editor sent them; rows without an `"id"` (every newly added row)
got a NEW random id on every decode, so server and clients disagreed on `RuleRef.RowId` and "rule as applied" could not find
the row. `IRuleSetHost.Canonicalize(kind, code, payload)` parses + re-writes a validated payload (ids assigned once, codec key
order); `RulePublisher` stores the canonical form.

**Audit (05 §4 "diff vs N").** `RuleDataDiff` (YFex.Rules, JSON-level, no typed rows needed): table rows matched by id,
`RowChangeKind` flags Added/Removed/Changed/Moved (moves = minimal set via longest increasing subsequence, so an insert doesn't
flag the rows it shifts), cells + row metadata compared by canonical JSON text (`ValueChange`); parameters/overrides compared
as flat objects. `IRuleAuditSink` (Admin, optional): `OnPublishedAsync(RulePublishAudit(info, diff))` for every accepted
version (diff vs the base version, or vs the code rows for a table's first version) and `OnRejectedAsync(key, actor, error)` for
every rejected attempt (unauthorized, invalid, conflict). Query `GetDataChanges(RuleSet, Kind, Code, TierPath, Version)` →
`RuleDataChanges` (diff vs the previous version of the stream; cacheable — versions are immutable).

**What-if (05 §4.0).** `ISituationSamples` (app: canonical situation bytes it stored, 06 §2) + `IRuleSetHost.Simulate(kind, code,
tierPath, draft, situations, maxExamples)` → `SimulationReport` (Evaluated; Changed = fingerprint differs — includes changed
limits/options, not only verdicts; VerdictChanged, NewlyDenied, NewlyNeedingApproval, FinalDelta in the amount type,
Unreadable, examples with before/after `DecisionSummary`). The draft replaces ONLY its own stream (`RuleData.Without`) and is in
force on every date; more specific tiers keep their versions. Query `Simulate(...)` (Admin): needs the same permission as
publishing that item, validates the draft first, reads samples from DI.
- Open: `RequireSimulation` table flag (05 §4.0 "mandatory for flagged tables") not implemented; examples carry summaries, not
  explanations (the client can re-evaluate the situation bytes).

**Device cache + incremental sync (05 §4.4, 11 §2.3).** `GetRuleData(RuleSet, AfterVersion = 0)`. `IRuleDataCache` +
`KeyValueRuleDataCache` over the device `IKeyValueStore` (key `yfex.rules/{set}/{version:D19}` → MemoryPack `RuleDataPayload`;
prunes to the last N per stream but always keeps the version in force today). `RuleSetSync` rewritten: start = apply the
cache FIRST (offline restarts keep their versions) → fetch only versions after the last known one → cache → apply; every
`RulesetPublished` (ignored when already applied) and every reconnect (`INetworkStatus`, optional) repeats the incremental fetch,
so missed versions are caught up in order; one sync at a time (semaphore); `LastVersion(set)`, `SyncErrors`.
`AddYFexRuleSetSync(deviceCache = true, keepVersions = 16)` registers the cache over the app's `IKeyValueStore`.
- Note: the host's in-memory `RuleData.With(keepPerCode)` still prunes by count only (a long run of future-dated versions can
  evict the one in force); the device cache handles it — align if it matters.

**SQL store — new `YFex.Rules.Data`** (`src/Validation`, refs YFex.Rules + `../YFex.Data/src/YFex.Data`): `SqlRuleStore :
IRuleStore` over `YFexConnectionFactory` with plain ADO.NET commands (no Dapper/reflection in this code; the dialect gives the
parameter prefix + identifier casing). Table `yfex_rule_data`: PK `(rule_set, version)` (concurrent appends race for the number
→ the loser's insert fails and is retried, re-checking its base version → conflict if it was the same stream), `stream_hash`
(`StableHash64` of kind + code + tier path) + index `(rule_set, stream_hash, version)` (SQL Server index key limit), exact
columns still compared; dates as ISO text. `RuleStoreDatabase { Sqlite, Postgres, SqlServer, Oracle }` selects DDL/types;
`CreateTableScript(db)`, `EnsureSchemaAsync()`. Apps register it: `services.AddSingleton<IRuleStore>(new SqlRuleStore(factory,
new() { Database = RuleStoreDatabase.Postgres }))`.
- Tests: `YFex.Rules.Data.Tests` — one `RuleStoreContract` suite (numbering, conflicts, incremental loads, lookups, lossless
  round trip incl. typed tier values, concurrent appends on different streams / on one stream) run against `InMemoryRuleStore`
  AND SQLite, + DDL test = 15. Postgres/SQL Server/Oracle: DDL written, not executed here. YFex.Data row-level security is not
  applied to the rule table.

**Pre-existing bug fixed (Messaging.Rpc).** `LocalDispatcher.QueryAsync` let an `ErrorResultException` thrown by a QUERY handler
escape to the caller (commands already mapped it). It now returns the typed error (never cached) — the RuleAdmin read handlers
reject this way (`DemandView`, not found).

**4a leftovers.**
- `RowsJson`: was already done in 4a step 2 (generated `WriteRow/ReadRow` + `TableJson`) — the "open" note was stale.
- `Complete` gap check (05 §1.3): `ConditionCell` (untyped condition cell) + `IDecisionTable.GetCondition(row, column)` /
  `ConditionDomain(column)` (static VIRTUAL, defaults Unknown/null → hand-written tables still compile and get a warning),
  emitted by the generator (enum members / bool as closed domains). `TableGaps.Check` (called by `DecisionTable.Check` when
  `Schema.Complete`): per column the points that matter (closed domain, or listed values + "any other value"; band columns =
  intervals between all edges), points grouped by the rows covering them (a column no row distinguishes disappears from the
  message), odometer over groups with row bitsets, ≤ 1 000 000 combinations (else warning), ≤ 5 gaps reported.
  Codes `rules.table.gap` (error, path `Rows`, args: the input as text — e.g. `Channel = Store, Amount ≥ $100.00` — and the
  total) and `rules.table.complete-unverifiable` (warning); messages en/pt. Row effective windows are not considered.
- `SituationPathMap` (04 §14, YFex.Rules): immutable renames by template prefix on segment boundaries (`"Total"` doesn't touch
  `"TotalTax"`), longest match wins, indices kept (same number of `[]` required), `Map(FieldPath)` / `Map(ViolationList)`.
  `EnforceOptions.Paths`; `.MapSituationPaths(m => m.Path("Total", "Amount").Path("Lines[]", "Items[]"))` on both command
  builders (not queries — they never reject); `RulesStage` maps denied violations, approval-required violations and approval
  reasons.
- Applicability index (04 §12.1): `ApplicabilityIndex<TS>` built once per definition (`RuleSetDefinition.Index`). Conservative:
  situation-level rules only; split conditions `rules.cond.eq` / `.true` / `.false` on situation fields stored as
  EnumCode/Bool/Int32/Int64 with the SAME value kind as the constant; per field a `constrained` mask + value → allowed mask (two
  different constants on one field = never applies). The evaluator skips non-candidates and notes them exactly like a false
  `Applies`. Test switch `ApplicabilityIndex<TS>.Enabled`; equivalence test (400 random sales, full traces identical).
- Not done (by design, after v1): per-rule read masks for incremental UI evaluation (04 §12.3).

**Acceptance status (13 Phase 4, 4b part):** publish a new table version at runtime → next evaluation uses it (Admin tests);
old decisions keep their `RuleRef` versions (row ids now stable); offline: device cache + incremental catch-up (Admin tests);
server replay policies were covered in 4a.

Tests (all green): Rules 64 (+19: gaps 8, path map 9 incl. theory rows, applicability 2), Rules.Generator 21, Rules.Admin 19 (+9:
canonical ids, audit diff + rejection, `GetDataChanges`, minimal moves, simulate ×2, offline restart from cache, missed versions
caught up, cache pruning), Rules.Data 15 (new), Messaging 150 (+1 path map; +2 skipped), Messaging.Rpc 41, Validation 58,
Validation.Generator 50, State 358, YFex.Tests 65; Messaging.Rpc.AotSample builds.

Open after 4b: `RequireSimulation`; optional `EventSourcedRuleStore` adapter; SQL store only exercised on SQLite; Wolverine
handler discovery still doesn't know `IValidatedCommandHandler`/`IRuledCommandHandler` (from Phase 3/4a). Next: Phase 6
(Fiscal, `YFex.Rules.Dynamic`, AI schemas) — the user moved Phase 5 (Avalonia) to last because the table editor needs a
reusable table/grid design first.


## Phase 6 implementation log — runtime-authored rules + AI schemas (2026-09-30)

User decisions (2026-09-30): do `YFex.Rules.Dynamic` and the AI schema export now; **Fiscal migration (6A) deferred**. Rules are
authored as **JSON only** for now (a simpler end-user syntax comes later). Runtime rules may join a group only when the group
**opts in** (`RuleGroup.Dynamic` policy). The language **includes arithmetic and functions** — built-ins, app/domain functions
written in C#, and **abstract (contract) functions** a library declares and a domain implements. **YFex.Capabilities
integration included.** Specs persist like tables (IRuleStore / device cache over YFex.Persistence) and compile once per version.

**B1 — engine hook (YFex.Rules).** `RuleDataKind.Rule = 4`; `IRuntimeRuleCodec<TS>` (Validate / Canonicalize / Decode) plugged
with `host.UseRuntimeRules(codec)`; `RuntimeRuleSnapshot<TS>` carries an ordinary `RuleDescriptor<TS>` template.
`CompiledRuleSet` now owns a merged rule list: code rules (same indices), then the runtime rules in force for the tier path /
date (per code: most specific tier allowed by the group's policy; `MaxRules` per group; code collisions ignored), with per-rule
tier + data version; table owner ids = `Rules.Length + t`. Overrides (Disable/Enable) work on runtime rules. `Decision` keeps the
compiled rule list (explanations of runtime rules). `RuleGroup.Dynamic = new DynamicRulePolicy(editableAt, DynamicDirectives
allow, maxPercent, maxAmount, maxRules)`. `IRuleSetHost.ValidateRule/AcceptsRuntimeRules`; `Canonicalize(Rule)`; simulation and
sync decode runtime rules through the host's codec. New `CompileNoteKind.RuntimeRuleIgnored`. The applicability index covers
code rules only.

**B2 — `YFex.Rules.Dynamic`** (new, layer 1.5: → YFex.Rules + YFex.Persistence):
- Model: `Expr` (literal, var, line, + − × ÷ neg, comparisons, and/or/not, in, between, isSet, if, coalesce, aggregates over
  lines sum/count/any/all/minOf/maxOf, function calls), `DirectiveSpec` (deny, warn, limit, grant, decrease, increase, approval —
  values may be expressions), `RuleSpec` (code, group, priority, when, then, messages per culture, description).
  `RuleSpecJson` (JsonLogic-like, hand-written reader/writer, canonical key order), `ExprText` (readable text for traces/AI).
- Types: `DynType` = RawType (+ enum field). Literals take the type they are compared/combined with (`"Pix"` → enum value,
  `500` → Money 500). Arithmetic: numbers; amounts + − same type; amount × ÷ number; amount ÷ amount = ratio. Specials
  `$base` (rule set amount), `$effectiveDate`, `$lineIndex`. A line field outside an aggregate makes it a per-line rule.
- Functions: `RuleFunction` (name, parameters, return, generics T/U with `Comparable/Numeric/Amount` constraints, description,
  category, implementation) + `FunctionCatalog` (layered, `With`, `Implement`, `Unimplemented`, `Version` hash). Built-ins: abs, min,
  max, clamp, round, floor, ceiling, percentOf, lower, upper, trim, length, contains, startsWith, endsWith, year, month, day,
  dayOfWeek (ISO), addDays, daysBetween, date. Null in → null out; never read clocks.
- `[RuleFunctions]`/`[RuleFunction]` (YFex.Rules.Generator `FunctionGen`): a static partial class → `All`; a partial interface of
  `static abstract` methods → `Contracts` + `Implement<TImpl>()` (static dispatch, no reflection). Diagnostics YFR0040–0044.
- Binder (publish checks with JSON paths, e.g. `then[0].decrease.percent`, `when.==[1]`): unknown/unexposed fields, enum
  literals, types, arity, generic constraints, nested aggregates, closed group, tier not allowed, directive family not allowed,
  literal over cap, unimplemented contract, size limits (500 nodes, depth 32, 20 directives). Codes `rules.dynamic.*`.
- Compiler → postfix `DynProgram<TS>` (constants, short-circuit jumps, per-line sub-programs for aggregates) interpreted over a
  pooled `RawValue` stack and `TS.Read/ReadLine`; total (÷0/overflow → null). One program per top-level `and` operand → trace
  "why not" names the failing condition (`rules.cond.<op>` templates for "field op literal", else `rules.cond.expr`).
  Adjustments are clamped to the policy caps at evaluation; negative/null sizes emit nothing.
- `DynamicRules<TS>` (the codec): validate, canonicalize, decode; in-memory compile cache keyed by (rule set code version,
  situation schema hash, function catalog version, payload) — a code/function change never reuses a stale compilation;
  `DynamicRuleMessages` (`IMessageSource` of the specs' messages; key "name" = the rule code).
- Measured (Release): compile ≈ 60 µs per rule (parse ≈ 15 µs) once per version; a dynamic rule whose condition is false adds
  ≈ 0.4 µs to an evaluation; applied simple rule ≈ +2.6 µs (engine adjustment/allocation); complex (sum over lines + 2 functions)
  ≈ +9.5 µs vs 3.5 µs baseline. **Not built: a persisted compiled-program cache** — the specs are already persisted (store +
  device cache); recompiling 500 rules costs ≈ 30 ms at startup, not worth a second binary format.

**B3 — Admin.** `RuleAdmin.Commands.PublishRule(RuleSet, RuleCode, TierPath, BaseVersion, EffectiveFrom, Spec, Note)`,
permission `RuleAdminOptions.RulePublishPermission` (default `rules.dynamic.publish`); Simulate/diff/audit/sync work for the new
kind unchanged; `GetRuleDefinition` returns the stored spec for runtime-rule refs.

**B4 — authoring catalog.** `dynamic.CatalogJson(set)`: fields (+ enum options), line fields, specials, operators, functions
(signature, description, category, implemented), open groups (stage, tiers, directive families, caps, max rules).

**C1 — JSON Schema of validatable types (YFex.Validation).** Generated `IValidatable<T>.Shape` (`TypeShape`/`MemberShape`: JSON
kind, nullable, app value type, enum codes+values, nested shape, array element, CLR type via `typeof`). `JsonSchemaWriter`
(draft 2020-12): Required → `required` (Warning-only and conditional requirements are not), lengths, bounds (dates:
`formatMinimum`), step → `multipleOf`, options → `enum`, format → `format`/`x-format`, `x-type`; `Merge(static, live)` (most
restrictive — e.g. a command's rules + the current decision); `JsonSchemas` registry by CLR type (`TryGet`, `TryGetShape`) filled by
the module initializer. Not exported: `[Pattern]` regexes (not in FieldConstraints).

**C2 — situations and rule sets (YFex.Rules).** `SituationJsonSchema.ToJson<TS>(decision?)` (exposed, non-derived fields; lines as
an array; decision limits/options as minimum/maximum/enum). `RuleSetDescription.ToJson(set, compiled?, catalog?, culture?)` —
stages → groups (resolve, runtime-rule policy) → rules (code/runtime, display name, sealed, tier, readable conditions) and tables.

**C3 — rule-spec JSON Schema.** `dynamic.SpecJsonSchema(set)`: `$defs` expr/var/line/op/call/directive specialized to the rule
set (field-name enums, implemented functions with arity, open groups, allowed directive families) — LLM structured output; the loop
is generate → `dynamic.Check` (violations with JSON paths) → Simulate → a person publishes.

**C4 — YFex.Capabilities** (other repo; additive): `CapabilityDescriptor.ParametersSchema` / `ActionDescriptor.ParametersSchema`
(optional, appended); `CqrsCapabilitySource` fills them from `JsonSchemas` and builds `ParameterDoc`s from the generated shape
(declared types, required from the rules) instead of reflection; messages without validation keep the reflected parameters.
The Capabilities.Cqrs test project references the Validation generator. (YFex.Capabilities had unrelated uncommitted work from
2026-09-25 — not touched beyond these appends.)

Deviations / open:
- Dynamic messages: the latest decoded version of a rule wins (10 §4 asks for versioned texts per decision) — keep old specs
  addressable via `GetRuleDefinition` meanwhile.
- Rule-set derived fields (`DerivedField.After`) are not readable from dynamic rules (situation `[Derived]` fields are); limit/grant
  target situation fields only.
- "Why not" of dynamic rules evaluates `$base` as null (the explain delegate has no running base).
- End-user text syntax, Composer authoring UI, versioned function catalogs per tenant: later. Fiscal (6A): deferred by the user.

Tests (all green): Rules 64, Rules.Generator 27 (+6 functions), Rules.Admin 22 (+3 PublishRule), Rules.Data 15, Rules.Dynamic 19
(new: spec → evaluation, trace/explanation, line rules, arithmetic + functions + aggregates, cap clamping, publish checks with
paths, tiers + overrides, effective dates, contracts, compile-once + canonical JSON, text, null-safe division, generated
functions ×3, AI schemas ×4), Validation 62 (+4 JSON Schema), Validation.Generator 50, Capabilities 28 + Cqrs 7 (+2) + Generator 5.

## Signed rule data hooks (offline Phase 2, F6 — 2026-10-15, additive)

Done by the YFex.Offline Phase 2 session (no Rules session was running; additive only, offline 11 §1). Purpose: rule data a device
loads offline must be what central published (offline 05 §2.1, scenario 09 S4). Rules.Admin gains hooks only; the signing and the
verification with version floors live in `YFex.Offline.Rules` (signed bundles of kind `rules.data`), so Rules references no
Installations/Offline code.

| Change | Where |
|---|---|
| `IRuleDataSigner` (central, signs a version once), `IRuleDataVerifier` (device, batch, `received` vs cached), `RuleDataCheck`, `SignedRuleData` — in **YFex.Rules** so an implementation needs no RPC/MemoryPack (AOT-clean) | new `YFex.Rules/Hosting/RuleDataSigning.cs` |
| `ISignedRuleDataCache`, wire `SignedRuleDataPayload` | new `YFex.Rules.Admin/RuleDataSigning.cs` |
| Query `RuleAdmin.Queries.GetSignedRuleData(RuleSet, AfterVersion)` + `GetSignedRuleDataHandler` (NotFound without a signer), baseline (view requirement, cacheable, invalidated by every publish), local invoker | `RuleAdmin.cs`, `RuleAdminHandlers.cs`, `RuleAdminSetup.cs`, `RuleAdminAuthorization.cs` |
| `RulePublisher(..., IRuleDataSigner? signer = null)` signs each new version after the store append | `RuleAdminHandlers.cs` |
| `RuleSetSync(..., IRuleDataVerifier? verifier = null)`: with a verifier it fetches `GetSignedRuleData`, applies/caches only `Valid` versions, lists the rest in `Refused` | `RuleAdminSetup.cs` |
| `KeyValueRuleDataCache(..., IRuleDataVerifier? verifier = null)` implements `ISignedRuleDataCache`: signed forms under `yfex.rules.signed/{set}/{version:D19}`, re-verified at every `LoadAsync` (refused → `Refused`), pruned together; `AddYFexRuleSetSync` passes a registered verifier | `RuleAdminSetup.cs` |

Without a signer/verifier nothing changes (same queries, same cache keys). Tests: Rules.Admin.Tests 27 → **30** (`SignedRuleDataTests`:
sign once + serve, sync refuses a bad version, cache re-verifies after an on-disk edit); `AdminFixture` takes an optional signer.

